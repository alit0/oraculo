using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Oloraculo.Web.Services
{
    public class TeamMoraleService(HttpClient http, OloraculoDbContext db, IOptions<OloraculoConfig> options)
    {
        private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
        {
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly OloraculoConfig _config = options.Value;

        public bool IsConfigured => !string.IsNullOrWhiteSpace(_config.OpenRouterApiKey);

        public async Task<TeamMoraleContext?> RefreshTeamAsync(string teamId, string teamName, CancellationToken ct = default)
        {
            if (!IsConfigured) return null;

            try
            {
                var adjustment = await QueryMoraleAsync(teamName, ct);
                var morale = new TeamMoraleContext
                {
                    TeamId            = teamId,
                    MoraleAdjustment  = adjustment.Adjustment,
                    Summary           = adjustment.Summary,
                    Signal            = adjustment.Signal,
                    UpdatedAt         = DateTimeOffset.UtcNow
                };

                var existing = await db.MoraleContexts.FindAsync([teamId], ct);
                if (existing is null)
                    db.MoraleContexts.Add(morale);
                else
                {
                    existing.MoraleAdjustment = morale.MoraleAdjustment;
                    existing.Summary          = morale.Summary;
                    existing.Signal           = morale.Signal;
                    existing.UpdatedAt        = morale.UpdatedAt;
                }

                await db.SaveChangesAsync(ct);
                return morale;
            }
            catch
            {
                return null;
            }
        }

        public async Task<int> RefreshUpcomingTeamsAsync(CancellationToken ct = default)
        {
            if (!IsConfigured) return 0;

            var now = DateTimeOffset.UtcNow;
            var fixtures = await db.Fixtures
                .AsNoTracking()
                .Where(f => f.KickoffUtc >= now)
                .ToListAsync(ct);

            var teamIds = fixtures
                .SelectMany(f => new[] { f.HomeTeamId, f.AwayTeamId })
                .Distinct()
                .ToList();

            var teams = await db.Teams
                .AsNoTracking()
                .Where(t => teamIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, ct);

            var count = 0;
            foreach (var (id, name) in teams)
            {
                if (await RefreshTeamAsync(id, name, ct) is not null)
                    count++;

                // Small delay to avoid rate limiting
                await Task.Delay(500, ct);
            }

            return count;
        }

        private async Task<MoraleResponse> QueryMoraleAsync(string teamName, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenRouterApiKey);

            request.Content = JsonContent.Create(new
            {
                model = _config.MoraleModel,
                messages = new[]
                {
                    new
                    {
                        role = "system",
                        content = """
                            You are a football analytics assistant for the 2026 FIFA World Cup.
                            Search for the latest news about the given team and return a JSON morale assessment.
                            Base your assessment ONLY on concrete recent signals: actual match results, confirmed injuries to key players, coach statements, internal conflicts, or tactical context.
                            Do NOT use vague narratives or general reputation.
                            Return JSON only: {"adjustment": number, "summary": "string", "signal": "positive|negative|neutral"}
                            adjustment range: -0.08 (very negative morale/form) to +0.08 (excellent morale/form). 0 means no clear signal.
                            summary: one concise sentence citing the specific evidence (e.g. "Won last 3 WC matches convincingly, key striker fully fit").
                            signal: "positive", "negative", or "neutral".
                            """
                    },
                    new
                    {
                        role = "user",
                        content = $"Team: {teamName}. Tournament: FIFA World Cup 2026 (currently in group stage). What is their current form, morale, and context for upcoming matches?"
                    }
                },
                response_format = new { type = "json_object" }
            }, options: JsonOpts);

            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "{}";

            using var inner = JsonDocument.Parse(content);
            var root = inner.RootElement;

            var adj = root.TryGetProperty("adjustment", out var adjEl) ? adjEl.GetDouble() : 0;
            var summary = root.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";
            var signal = root.TryGetProperty("signal", out var sigEl) ? sigEl.GetString() ?? "neutral" : "neutral";

            // Clamp to safe range
            adj = Math.Clamp(adj, -0.08, 0.08);

            return new MoraleResponse(adj, summary, signal);
        }

        private sealed record MoraleResponse(double Adjustment, string Summary, string Signal);
    }
}
