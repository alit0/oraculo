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
                    TeamId               = teamId,
                    MoraleAdjustment     = adjustment.Adjustment,
                    Summary              = adjustment.Summary,
                    Signal               = adjustment.Signal,
                    InjuriesSignal       = adjustment.Injuries,
                    PersonalIssuesSignal = adjustment.PersonalIssues,
                    PreviousMatchSignal  = adjustment.PreviousMatch,
                    MoraleSignal         = adjustment.Morale,
                    UpdatedAt            = DateTimeOffset.UtcNow
                };

                var existing = await db.MoraleContexts.FindAsync([teamId], ct);
                if (existing is null)
                    db.MoraleContexts.Add(morale);
                else
                {
                    existing.MoraleAdjustment     = morale.MoraleAdjustment;
                    existing.Summary              = morale.Summary;
                    existing.Signal               = morale.Signal;
                    existing.InjuriesSignal       = morale.InjuriesSignal;
                    existing.PersonalIssuesSignal = morale.PersonalIssuesSignal;
                    existing.PreviousMatchSignal  = morale.PreviousMatchSignal;
                    existing.MoraleSignal         = morale.MoraleSignal;
                    existing.UpdatedAt            = morale.UpdatedAt;
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

            var fixtures = (await db.Fixtures
                .AsNoTracking()
                .ToListAsync(ct))
                .Where(f => !f.IsPlayed)
                .ToList();

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
                            Search for the LATEST news about the given team and assess their condition for upcoming matches.
                            Analyze ONLY these four signals — ignore everything else:

                            1. INJURIES & PHYSICAL: Who is injured, doubtful, or "trained separately"? Key players missing or at risk?
                            2. PERSONAL/FAMILY ISSUES: Any player dealing with family illness, personal problems, or off-pitch distractions reported in the news?
                            3. PREVIOUS MATCH CONTEXT: Did they win/lose/draw their last WC match? Was it convincing or did they suffer? Any red cards or suspensions carried over?
                            4. TEAM MORALE & DYNAMICS: Coach statements about pressure, internal conflicts, public criticism from federation or press, or conversely — exceptional team spirit reported?

                            RULES:
                            - If you find NO concrete evidence for a signal, set adjustment = 0 and say so.
                            - Do NOT invent, speculate, or use general reputation. Only cite what was actually reported.
                            - Each negative signal (key injury, loss, personal issue) contributes roughly -0.02 to -0.03.
                            - Each positive signal (convincing win, full squad fit, strong morale) contributes roughly +0.02 to +0.03.
                            - Maximum total range: -0.08 (multiple serious negatives) to +0.08 (multiple strong positives).

                            Return JSON only:
                            {
                              "adjustment": <number -0.08 to 0.08>,
                              "summary": "<one sentence with specific evidence found>",
                              "signal": "<positive|negative|neutral>",
                              "signals_found": {
                                "injuries": "<what was found or 'none'>",
                                "personal_issues": "<what was found or 'none'>",
                                "previous_match": "<result and context or 'unknown'>",
                                "morale": "<what was found or 'none'>"
                              }
                            }
                            """
                    },
                    new
                    {
                        role = "user",
                        content = $"Search for the latest news about {teamName} at the 2026 FIFA World Cup. Assess their condition for their next match based on: injuries, personal/family issues affecting players, their most recent WC match result and performance, and team morale signals."
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

            var adj     = root.TryGetProperty("adjustment", out var adjEl) ? adjEl.GetDouble() : 0;
            var summary = root.TryGetProperty("summary", out var sumEl) ? sumEl.GetString() ?? "" : "";
            var signal  = root.TryGetProperty("signal", out var sigEl) ? sigEl.GetString() ?? "neutral" : "neutral";

            string injuries = "none", personal = "none", prevMatch = "unknown", morale = "none";
            if (root.TryGetProperty("signals_found", out var sf))
            {
                injuries  = sf.TryGetProperty("injuries", out var i) ? i.GetString() ?? "none" : "none";
                personal  = sf.TryGetProperty("personal_issues", out var p) ? p.GetString() ?? "none" : "none";
                prevMatch = sf.TryGetProperty("previous_match", out var m) ? m.GetString() ?? "unknown" : "unknown";
                morale    = sf.TryGetProperty("morale", out var mo) ? mo.GetString() ?? "none" : "none";
            }

            adj = Math.Clamp(adj, -0.08, 0.08);
            return new MoraleResponse(adj, summary, signal, injuries, personal, prevMatch, morale);
        }

        private sealed record MoraleResponse(double Adjustment, string Summary, string Signal,
            string Injuries, string PersonalIssues, string PreviousMatch, string Morale);
    }
}
