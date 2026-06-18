using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Oloraculo.Web.DAL;
using Oloraculo.Web.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Oloraculo.Web.Services
{
    public class WeatherService(HttpClient http, OloraculoDbContext db, IOptions<OloraculoConfig> options)
    {
        // WC 2026 venue coordinates keyed by city name from CSV
        private static readonly Dictionary<string, (double Lat, double Lon)> VenueCoords = new(StringComparer.OrdinalIgnoreCase)
        {
            { "Miami Gardens",   (25.958,  -80.239) },
            { "Atlanta",         (33.755,  -84.401) },
            { "Arlington",       (32.748,  -97.093) },
            { "Houston",         (29.685,  -95.411) },
            { "Kansas City",     (39.049,  -94.484) },
            { "Foxborough",      (42.091,  -71.264) },
            { "East Rutherford", (40.813,  -74.074) },
            { "Philadelphia",    (39.901,  -75.167) },
            { "Seattle",         (47.595, -122.331) },
            { "Inglewood",       (33.953, -118.339) },
            { "Santa Clara",     (37.403, -121.970) },
            { "Vancouver",       (49.278, -123.112) },
            { "Toronto",         (43.632,  -79.419) },
            { "Mexico City",     (19.303,  -99.151) },
            { "Guadalupe",       (25.671, -100.311) },
            { "Zapopan",         (20.673, -103.367) },
        };

        // Climate comfort range [min°C, max°C] for each team
        // Teams outside this range get a disadvantage, inside get a small advantage
        private static readonly Dictionary<string, (double Min, double Max)> TeamComfort = new(StringComparer.OrdinalIgnoreCase)
        {
            // Tropical / hot & humid comfortable
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

            // Hot & dry comfortable
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

            // Temperate (comfortable 14-26°C) — European & others
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

        private static readonly string KnownCities = string.Join(", ", VenueCoords.Keys);

        private readonly OloraculoConfig _config = options.Value;

        private async Task<string?> ResolveCityAsync(Fixture fixture, CancellationToken ct)
        {
            // Already has city data
            var city = fixture.City ?? ExtractCity(fixture.Venue);
            if (!string.IsNullOrWhiteSpace(city)) return city;

            // Ask the LLM — it knows the WC 2026 schedule
            if (string.IsNullOrWhiteSpace(_config.OpenRouterApiKey)) return null;

            var home = fixture.HomeTeamId.Replace("-", " ");
            var away = fixture.AwayTeamId.Replace("-", " ");

            using var request = new HttpRequestMessage(HttpMethod.Post, _config.OpenRouterBaseUrl.TrimEnd('/') + "/chat/completions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.OpenRouterApiKey);
            request.Content = JsonContent.Create(new
            {
                model = _config.MoraleModel,
                messages = new[]
                {
                    new { role = "system", content = $"You know the 2026 FIFA World Cup schedule. Reply ONLY with one city name from this list: {KnownCities}. If unknown, reply: unknown" },
                    new { role = "user",   content = $"What city hosts the WC 2026 match {home} vs {away}?" }
                },
                max_tokens = 10
            });

            try
            {
                using var response = await http.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) return null;
                var body = await response.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(body);
                var answer = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString()?.Trim();

                if (string.IsNullOrWhiteSpace(answer) || answer.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    return null;

                // Validate it's actually a known city
                return VenueCoords.ContainsKey(answer) ? answer : null;
            }
            catch { return null; }
        }

        public async Task<FixtureWeatherContext?> FetchAndSaveAsync(Fixture fixture, CancellationToken ct = default)
        {
            var city = await ResolveCityAsync(fixture, ct);
            if (string.IsNullOrWhiteSpace(city))
                return null;

            if (!VenueCoords.TryGetValue(city, out var coords))
                return null;

            // If no kickoff time, forecast for tomorrow as best approximation
            var kickoff = fixture.KickoffUtc ?? DateTimeOffset.UtcNow.AddDays(1);
            var date = kickoff.UtcDateTime.ToString("yyyy-MM-dd");
            var kickoffHour = kickoff.UtcDateTime.Hour;

            var url = $"https://api.open-meteo.com/v1/forecast" +
                      $"?latitude={coords.Lat}&longitude={coords.Lon}" +
                      $"&hourly=temperature_2m,relativehumidity_2m,precipitation_probability,weathercode" +
                      $"&start_date={date}&end_date={date}&timezone=UTC";

            try
            {
                var json = await http.GetStringAsync(url, ct);
                var doc = JsonDocument.Parse(json);
                var hourly = doc.RootElement.GetProperty("hourly");

                var temps = hourly.GetProperty("temperature_2m").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var humidity = hourly.GetProperty("relativehumidity_2m").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var precip = hourly.GetProperty("precipitation_probability").EnumerateArray().Select(e => e.GetDouble()).ToArray();
                var codes = hourly.GetProperty("weathercode").EnumerateArray().Select(e => e.GetInt32()).ToArray();

                // Average 3-hour window around kickoff
                var indices = new[] { kickoffHour - 1, kickoffHour, kickoffHour + 1 }
                    .Where(i => i >= 0 && i < temps.Length)
                    .ToArray();

                if (indices.Length == 0) return null;

                var tempC    = indices.Average(i => temps[i]);
                var humPct   = indices.Average(i => humidity[i]);
                var precipPct = indices.Average(i => precip[i]);
                var code     = codes[indices[indices.Length / 2]];

                var weather = new FixtureWeatherContext
                {
                    FixtureId            = fixture.Id,
                    TempC                = Math.Round(tempC, 1),
                    HumidityPct          = Math.Round(humPct, 1),
                    PrecipPct            = Math.Round(precipPct, 1),
                    Condition            = WmoDescription(code),
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
            catch
            {
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
            }

            return count;
        }

        private static string? ExtractCity(string? venue)
        {
            if (string.IsNullOrWhiteSpace(venue)) return null;
            // Match known WC 2026 venue names to cities
            return venue switch
            {
                var v when v.Contains("Hard Rock",       StringComparison.OrdinalIgnoreCase) => "Miami Gardens",
                var v when v.Contains("Mercedes-Benz",   StringComparison.OrdinalIgnoreCase) => "Atlanta",
                var v when v.Contains("AT&T",            StringComparison.OrdinalIgnoreCase) => "Arlington",
                var v when v.Contains("NRG",             StringComparison.OrdinalIgnoreCase) => "Houston",
                var v when v.Contains("Arrowhead",       StringComparison.OrdinalIgnoreCase) => "Kansas City",
                var v when v.Contains("Gillette",        StringComparison.OrdinalIgnoreCase) => "Foxborough",
                var v when v.Contains("MetLife",         StringComparison.OrdinalIgnoreCase) => "East Rutherford",
                var v when v.Contains("Lincoln",         StringComparison.OrdinalIgnoreCase) => "Philadelphia",
                var v when v.Contains("Lumen",           StringComparison.OrdinalIgnoreCase) => "Seattle",
                var v when v.Contains("SoFi",            StringComparison.OrdinalIgnoreCase) => "Inglewood",
                var v when v.Contains("Levi",            StringComparison.OrdinalIgnoreCase) => "Santa Clara",
                var v when v.Contains("BC Place",        StringComparison.OrdinalIgnoreCase) => "Vancouver",
                var v when v.Contains("BMO",             StringComparison.OrdinalIgnoreCase) => "Toronto",
                var v when v.Contains("Azteca",          StringComparison.OrdinalIgnoreCase) => "Mexico City",
                var v when v.Contains("BBVA",            StringComparison.OrdinalIgnoreCase) => "Guadalupe",
                var v when v.Contains("Akron",           StringComparison.OrdinalIgnoreCase) => "Zapopan",
                _ => null
            };
        }

        private static double ClimateAdvantage(double tempC, string teamId)
        {
            var comfort = TeamComfort.TryGetValue(teamId, out var c) ? c : DefaultComfort;

            if (tempC >= comfort.Min && tempC <= comfort.Max)
                return 0.025; // in comfort zone

            var dist = tempC < comfort.Min ? comfort.Min - tempC : tempC - comfort.Max;
            return Math.Max(-0.06, -dist * 0.006); // ~0.6% per degree outside range
        }

        private static string WmoDescription(int code) => code switch
        {
            0          => "Clear sky",
            1          => "Mainly clear",
            2          => "Partly cloudy",
            3          => "Overcast",
            45 or 48   => "Foggy",
            >= 51 and <= 57 => "Drizzle",
            >= 61 and <= 67 => "Rain",
            >= 71 and <= 77 => "Snow",
            >= 80 and <= 82 => "Rain showers",
            >= 95          => "Thunderstorm",
            _              => "Unknown"
        };
    }
}
