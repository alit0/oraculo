using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Oloraculo.Web.Services
{
    public class WeatherService(HttpClient http, OloraculoDbContext db, IOptions<OloraculoConfig> options,
        ILogger<WeatherService> logger)
    {
        private static readonly Dictionary<string, (double Min, double Max)> TeamComfort = new(StringComparer.OrdinalIgnoreCase)
        {
            { "brazil",          (24, 35) },
            { "colombia",        (22, 34) },
            { "ecuador",         (18, 30) },
            { "ivory-coast",     (26, 36) },
            { "senegal",         (26, 36) },
            { "ghana",           (26, 36) },
            { "dr-congo",        (22, 34) },
            { "cape-verde",      (24, 34) },
            { "haiti",           (26, 36) },
            { "panama",          (26, 34) },
            { "costa-rica",      (24, 32) },
            { "cameroon",        (24, 34) },
            { "nigeria",         (26, 36) },
            { "egypt",           (24, 38) },
            { "saudi-arabia",    (26, 40) },
            { "iran",            (22, 36) },
            { "algeria",         (22, 36) },
            { "tunisia",         (20, 34) },
            { "jordan",          (22, 36) },
            { "iraq",            (24, 40) },
            { "qatar",           (26, 42) },
            { "uzbekistan",      (20, 36) },
            { "south-africa",    (16, 30) },
            { "germany",         (12, 26) },
            { "france",          (14, 26) },
            { "spain",           (16, 28) },
            { "england",         (10, 24) },
            { "netherlands",     (10, 24) },
            { "portugal",        (14, 28) },
            { "belgium",         (10, 24) },
            { "switzerland",     (10, 24) },
            { "norway",          (8,  22) },
            { "sweden",          (8,  22) },
            { "austria",         (10, 24) },
            { "croatia",         (14, 26) },
            { "scotland",        (8,  22) },
            { "czechia",         (10, 24) },
            { "japan",           (16, 28) },
            { "south-korea",     (14, 26) },
            { "australia",       (14, 26) },
            { "new-zealand",     (10, 22) },
            { "united-states",   (14, 28) },
            { "canada",          (10, 24) },
            { "mexico",          (16, 28) },
            { "argentina",       (14, 26) },
            { "uruguay",         (14, 26) },
            { "paraguay",        (20, 32) },
            { "chile",           (12, 24) },
            { "morocco",         (18, 30) },
            { "turkey",          (16, 28) },
            { "bosnia-and-herzegovina", (12, 26) },
        };

        private static readonly (double Min, double Max) DefaultComfort = (14, 28);

        private readonly OloraculoConfig _config = options.Value;

        public async Task<FixtureWeatherContext?> FetchAndSaveAsync(Fixture fixture, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(_config.OpenRouterApiKey))
                return null;

            var home = fixture.HomeTeamId.Replace("-", " ");
            var away = fixture.AwayTeamId.Replace("-", " ");
            var dateHint = fixture.KickoffUtc.HasValue
                ? fixture.KickoffUtc.Value.UtcDateTime.ToString("yyyy-MM-dd")
                : DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");

            using var request = new HttpRequestMessage(HttpMethod.Post,
                _config.OpenRouterBaseUrl.TrimEnd('/') + "/chat/completions");
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
                            You are a weather analyst for the 2026 FIFA World Cup.
                            Search for the match schedule and current weather forecasts.
                            Return ONLY a JSON object with these fields:
                            {
                              "city": "<host city name>",
                              "venue": "<stadium name>",
                              "tempC": <expected temperature at match time in Celsius as number>,
                              "humidityPct": <expected humidity 0-100 as number>,
                              "precipPct": <rain probability 0-100 as number>,
                              "condition": "<weather condition: Clear, Partly cloudy, Overcast, Rain, Thunderstorm, etc>"
                            }
                            If you cannot find the match or weather data, return {"city": "unknown"}.
                            Do NOT wrap in markdown code blocks. Return raw JSON only.
                            """
                    },
                    new
                    {
                        role = "user",
                        content = $"Search for: 2026 FIFA World Cup match {home} vs {away} on {dateHint}. Find the host city, stadium, and weather forecast at match time. Return the JSON."
                    }
                }
            });

            try
            {
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var err = await response.Content.ReadAsStringAsync(ct);
                    logger.LogWarning("Weather fetch HTTP {Status}: {Body}", (int)response.StatusCode, err);
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(ct);
                using var outer = JsonDocument.Parse(body);
                var content = outer.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString() ?? "{}";

                content = ExtractJson(content);
                using var inner = JsonDocument.Parse(content);
                var root = inner.RootElement;

                var city = root.TryGetProperty("city", out var cityEl) ? cityEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(city) || city.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogWarning("Weather: could not resolve city for {Home} vs {Away}", home, away);
                    return null;
                }

                var tempC     = root.TryGetProperty("tempC",      out var tEl) ? tEl.GetDouble() : 22.0;
                var humidity  = root.TryGetProperty("humidityPct", out var hEl) ? hEl.GetDouble() : 50.0;
                var precip    = root.TryGetProperty("precipPct",   out var pEl) ? pEl.GetDouble() : 0.0;
                var condition = root.TryGetProperty("condition",   out var cEl) ? cEl.GetString() ?? "Unknown" : "Unknown";
                var venue     = root.TryGetProperty("venue",       out var vEl) ? vEl.GetString() ?? "" : "";

                logger.LogInformation("Weather for {Home} vs {Away}: {City} ({Venue}) {TempC}°C {Condition}",
                    home, away, city, venue, tempC, condition);

                var weather = new FixtureWeatherContext
                {
                    FixtureId            = fixture.Id,
                    TempC                = Math.Round(tempC, 1),
                    HumidityPct          = Math.Round(humidity, 1),
                    PrecipPct            = Math.Round(precip, 1),
                    Condition            = condition,
                    HomeClimateAdvantage = ClimateAdvantage(tempC, fixture.HomeTeamId),
                    AwayClimateAdvantage = ClimateAdvantage(tempC, fixture.AwayTeamId),
                    UpdatedAt            = DateTimeOffset.UtcNow
                };

                var existing = await db.WeatherContexts.FindAsync([fixture.Id], ct);
                if (existing is null)
                    db.WeatherContexts.Add(weather);
                else
                {
                    existing.TempC                = weather.TempC;
                    existing.HumidityPct          = weather.HumidityPct;
                    existing.PrecipPct            = weather.PrecipPct;
                    existing.Condition            = weather.Condition;
                    existing.HomeClimateAdvantage = weather.HomeClimateAdvantage;
                    existing.AwayClimateAdvantage = weather.AwayClimateAdvantage;
                    existing.UpdatedAt            = weather.UpdatedAt;
                }

                await db.SaveChangesAsync(ct);
                return weather;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Weather fetch failed for {Home} vs {Away}: {Error}", home, away, ex.Message);
                return null;
            }
        }

        public async Task<int> RefreshUpcomingAsync(CancellationToken ct = default)
        {
            var fixtures = (await db.Fixtures
                .AsNoTracking()
                .ToListAsync(ct))
                .Where(f => !f.IsPlayed)
                .ToList();

            var count = 0;
            foreach (var fixture in fixtures)
            {
                if (await FetchAndSaveAsync(fixture, ct) is not null)
                    count++;
                await Task.Delay(500, ct);
            }
            return count;
        }

        private static string ExtractJson(string text)
        {
            text = text.Trim();
            if (text.StartsWith("```"))
            {
                var start = text.IndexOf('\n');
                if (start >= 0) text = text[(start + 1)..];
                var end = text.LastIndexOf("```");
                if (end >= 0) text = text[..end];
            }
            var first = text.IndexOf('{');
            var last  = text.LastIndexOf('}');
            if (first >= 0 && last > first)
                text = text[first..(last + 1)];
            return text.Trim();
        }

        private static double ClimateAdvantage(double tempC, string teamId)
        {
            var comfort = TeamComfort.TryGetValue(teamId, out var c) ? c : DefaultComfort;
            if (tempC >= comfort.Min && tempC <= comfort.Max)
                return 0.025;
            var dist = tempC < comfort.Min ? comfort.Min - tempC : tempC - comfort.Max;
            return Math.Max(-0.06, -dist * 0.006);
        }
    }
}
