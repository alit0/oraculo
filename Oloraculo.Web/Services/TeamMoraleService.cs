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
    public class TeamMoraleService(HttpClient http, OloraculoDbContext db, IOptions<OloraculoConfig> options,
        ILogger<TeamMoraleService> logger)
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
            catch (Exception ex)
            {
                logger.LogWarning("Morale refresh failed for team {TeamId}: {Error}", teamId, ex.Message);
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

            logger.LogInformation("Morale refresh: {Count} unplayed fixtures found", fixtures.Count);

            var teamIds = fixtures
                .SelectMany(f => new[] { f.HomeTeamId, f.AwayTeamId })
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            logger.LogInformation("Morale refresh: {Count} unique team IDs", teamIds.Count);

            // Build name map: DB name if found, else humanize the ID
            var dbTeams = await db.Teams.AsNoTracking()
                .ToDictionaryAsync(t => t.Id, t => t.Name, StringComparer.OrdinalIgnoreCase, ct);

            var teams = teamIds.ToDictionary(
                id => id,
                id => dbTeams.TryGetValue(id, out var n) ? n : id.Replace("-", " "),
                StringComparer.OrdinalIgnoreCase);

            logger.LogInformation("Morale refresh: {Count} teams to analyze", teams.Count);

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
                            Sos un asistente de análisis de fútbol para el Mundial FIFA 2026.
                            Buscá las ÚLTIMAS noticias sobre el equipo y evaluá su estado para los próximos partidos.
                            Analizá SOLO estas cuatro señales — ignorá todo lo demás:

                            1. LESIONES Y FÍSICO: ¿Quién está lesionado, en duda, o entrenó separado? ¿Hay jugadores clave en riesgo?
                            2. PROBLEMAS PERSONALES/FAMILIARES: ¿Algún jugador enfrenta problemas familiares, personales o distracciones fuera del campo reportadas en los medios?
                            3. CONTEXTO DEL PARTIDO ANTERIOR: ¿Ganaron/perdieron/empataron su último partido del Mundial? ¿Fue convincente o sufrieron? ¿Hay tarjetas rojas o suspensiones pendientes?
                            4. MORAL Y DINÁMICA DEL EQUIPO: Declaraciones del técnico sobre presión, conflictos internos, críticas públicas de la federación o la prensa, o al contrario — espíritu de equipo excepcional reportado.

                            REGLAS:
                            - Si no encontrás evidencia concreta de una señal, poné adjustment = 0 y decilo.
                            - NO inventes, especules ni uses reputación general. Solo citá lo que fue reportado.
                            - Cada señal negativa (lesión clave, derrota, problema personal) aporta aproximadamente -0.02 a -0.03.
                            - Cada señal positiva (victoria convincente, plantel completo, moral alta) aporta aproximadamente +0.02 a +0.03.
                            - Rango máximo total: -0.08 (múltiples negativos serios) a +0.08 (múltiples positivos fuertes).

                            Devolvé SOLO JSON:
                            {
                              "adjustment": <número -0.08 a 0.08>,
                              "summary": "<una oración en español con la evidencia concreta encontrada>",
                              "signal": "<positive|negative|neutral>",
                              "signals_found": {
                                "injuries": "<qué se encontró o 'ninguna'>",
                                "personal_issues": "<qué se encontró o 'ninguno'>",
                                "previous_match": "<resultado y contexto o 'desconocido'>",
                                "morale": "<qué se encontró o 'ninguno'>"
                              }
                            }
                            """
                    },
                    new
                    {
                        role = "user",
                        content = $"Buscá las últimas noticias sobre {teamName} en el Mundial FIFA 2026. Evaluá su estado para el próximo partido según: lesiones, problemas personales/familiares que afecten a jugadores, el resultado y rendimiento en su partido más reciente del Mundial, y señales de moral del equipo. Respondé en español."
                    }
                },
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

            // Perplexity may wrap JSON in markdown code blocks
            content = ExtractJson(content);

            using var inner = JsonDocument.Parse(content);
            var root = inner.RootElement;

            var adj     = AsDouble(root, "adjustment");
            var summary = AsText(root, "summary") ?? "";
            var signal  = AsText(root, "signal") ?? "neutral";

            string injuries = "none", personal = "none", prevMatch = "unknown", morale = "none";
            if (root.TryGetProperty("signals_found", out var sf))
            {
                injuries  = AsText(sf, "injuries") ?? "none";
                personal  = AsText(sf, "personal_issues") ?? "none";
                prevMatch = AsText(sf, "previous_match") ?? "unknown";
                morale    = AsText(sf, "morale") ?? "none";
            }

            adj = Math.Clamp(adj, -0.08, 0.08);
            return new MoraleResponse(adj, summary, signal, injuries, personal, prevMatch, morale);
        }

        /// <summary>
        /// Reads a property as text regardless of whether the model returned a string, boolean,
        /// or number. LLM JSON is inconsistent (e.g. "injuries": true vs "injuries": "Messi out"),
        /// and a raw GetString() on a non-string throws and silently drops the whole result.
        /// </summary>
        private static string? AsText(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var el))
                return null;
            return el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Number => el.GetRawText(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                _ => el.GetRawText()
            };
        }

        private static double AsDouble(JsonElement parent, string name)
        {
            if (!parent.TryGetProperty(name, out var el))
                return 0;
            return el.ValueKind switch
            {
                JsonValueKind.Number => el.GetDouble(),
                JsonValueKind.String => double.TryParse(el.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0,
                _ => 0
            };
        }

        private static string ExtractJson(string text)
        {
            text = text.Trim();
            // Strip markdown code fences
            if (text.StartsWith("```"))
            {
                var start = text.IndexOf('\n');
                if (start >= 0) text = text[(start + 1)..];
                var end = text.LastIndexOf("```");
                if (end >= 0) text = text[..end];
            }
            // Find first { to last } as fallback
            var first = text.IndexOf('{');
            var last  = text.LastIndexOf('}');
            if (first >= 0 && last > first)
                text = text[first..(last + 1)];
            return text.Trim();
        }

        private sealed record MoraleResponse(double Adjustment, string Summary, string Signal,
            string Injuries, string PersonalIssues, string PreviousMatch, string Morale);
    }
}
