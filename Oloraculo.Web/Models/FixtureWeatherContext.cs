namespace Oloraculo.Web.Models
{
    public class FixtureWeatherContext
    {
        public required string FixtureId { get; set; }
        public double? TempC { get; set; }
        public double? HumidityPct { get; set; }
        public double? PrecipPct { get; set; }
        public string? Condition { get; set; }
        public double HomeClimateAdvantage { get; set; }
        public double AwayClimateAdvantage { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
