namespace Oloraculo.Web.Models
{
    public class TeamMoraleContext
    {
        public required string TeamId { get; set; }
        public double MoraleAdjustment { get; set; }
        public string Summary { get; set; } = "";
        public string Signal { get; set; } = "neutral";
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
