using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Helpers;
using Oloraculo.Web.Models;

namespace Oloraculo.Web.Services
{
    /// <summary>
    /// Fills fixture schedule (kickoff, venue) and results from the free, public-domain
    /// openfootball/worldcup.json dataset — no API key required.
    /// </summary>
    public class ScheduleService(HttpClient http, OloraculoDbContext db, ILogger<ScheduleService> logger)
    {
        private const string ScheduleUrl =
            "https://raw.githubusercontent.com/openfootball/worldcup.json/master/2026/worldcup.json";

        public async Task<ScheduleRefreshReport> RefreshAsync(CancellationToken ct = default)
        {
            string json;
            try
            {
                json = await http.GetStringAsync(ScheduleUrl, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Schedule fetch failed: {Error}", ex.Message);
                return new ScheduleRefreshReport(0, 0, 0, 0, [$"No se pudo descargar el calendario: {ex.Message}"]);
            }

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
                return new ScheduleRefreshReport(0, 0, 0, 0, ["El calendario no tiene la estructura esperada."]);

            var fixtures = await db.Fixtures.ToListAsync(ct);
            var byPair = new Dictionary<string, Fixture>(StringComparer.Ordinal);
            foreach (var f in fixtures)
                byPair[PairKey(f.Group, f.HomeTeamId, f.AwayTeamId)] = f;

            var matched = 0;
            var withResults = 0;
            var unmatched = 0;
            var notes = new List<string>();

            foreach (var match in matches.EnumerateArray())
            {
                // Only group-stage matches map to our generated fixtures.
                if (!match.TryGetProperty("group", out var groupEl) || groupEl.ValueKind != JsonValueKind.String)
                    continue;
                var groupName = groupEl.GetString()!.Replace("Group", "", StringComparison.OrdinalIgnoreCase).Trim();

                var team1 = match.TryGetProperty("team1", out var t1) ? t1.GetString() : null;
                var team2 = match.TryGetProperty("team2", out var t2) ? t2.GetString() : null;
                if (string.IsNullOrWhiteSpace(team1) || string.IsNullOrWhiteSpace(team2))
                    continue;

                var id1 = TeamNameNormalizer.ToId(team1);
                var id2 = TeamNameNormalizer.ToId(team2);

                if (!byPair.TryGetValue(PairKey(groupName, id1, id2), out var fixture))
                {
                    unmatched++;
                    if (notes.Count < 6)
                        notes.Add($"Sin match: {team1} vs {team2} (grupo {groupName}).");
                    continue;
                }

                matched++;

                var date = match.TryGetProperty("date", out var dateEl) ? dateEl.GetString() : null;
                var time = match.TryGetProperty("time", out var timeEl) ? timeEl.GetString() : null;
                if (ParseKickoff(date, time) is { } kickoff)
                    fixture.KickoffUtc = kickoff;

                if (match.TryGetProperty("ground", out var groundEl) && groundEl.ValueKind == JsonValueKind.String)
                    fixture.Venue = groundEl.GetString();

                if (TryReadFullTime(match, out var s1, out var s2))
                {
                    if (string.Equals(fixture.HomeTeamId, id1, StringComparison.OrdinalIgnoreCase))
                    {
                        fixture.HomeGoals = s1;
                        fixture.AwayGoals = s2;
                    }
                    else
                    {
                        fixture.HomeGoals = s2;
                        fixture.AwayGoals = s1;
                    }
                    fixture.IsPlayed = true;
                    fixture.Status = "FT";
                    withResults++;
                }

                fixture.Source = "openfootball/worldcup.json";
            }

            await db.SaveChangesAsync(ct);
            notes.Insert(0, $"Calendario openfootball: {matched} partidos actualizados, {withResults} con resultado.");
            return new ScheduleRefreshReport(matched, matched, withResults, unmatched, notes);
        }

        private static string PairKey(string group, string a, string b)
        {
            var (lo, hi) = string.CompareOrdinal(a, b) <= 0 ? (a, b) : (b, a);
            return $"{group}|{lo}|{hi}";
        }

        private static bool TryReadFullTime(JsonElement match, out int s1, out int s2)
        {
            s1 = s2 = 0;
            if (!match.TryGetProperty("score", out var score) ||
                !score.TryGetProperty("ft", out var ft) ||
                ft.ValueKind != JsonValueKind.Array ||
                ft.GetArrayLength() != 2)
                return false;

            s1 = ft[0].GetInt32();
            s2 = ft[1].GetInt32();
            return true;
        }

        /// <summary>
        /// Parses openfootball date ("2026-06-11") + time ("13:00 UTC-6") into a UTC instant.
        /// Falls back to 12:00 UTC if the time is missing or unparseable.
        /// </summary>
        private static DateTimeOffset? ParseKickoff(string? date, string? time)
        {
            if (string.IsNullOrWhiteSpace(date) ||
                !DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                return null;

            var hour = 12;
            var minute = 0;
            var offsetHours = 0;

            if (!string.IsNullOrWhiteSpace(time))
            {
                var parts = time.Split("UTC", StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                var clock = parts.Length > 0 ? parts[0] : "";
                if (TimeOnly.TryParse(clock, CultureInfo.InvariantCulture, out var t))
                {
                    hour = t.Hour;
                    minute = t.Minute;
                }
                if (parts.Length > 1 && int.TryParse(parts[1].Replace(" ", ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var off))
                    offsetHours = off;
            }

            var local = new DateTimeOffset(d.Year, d.Month, d.Day, hour, minute, 0, TimeSpan.FromHours(offsetHours));
            return local.ToUniversalTime();
        }
    }

    public sealed record ScheduleRefreshReport(
        int Matched,
        int Updated,
        int WithResults,
        int Unmatched,
        IReadOnlyList<string> Notes);
}
