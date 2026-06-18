namespace Oloraculo.Web.Models
{
    public class TeamMoraleContext
    {
        public required string TeamId { get; set; }
        public double MoraleAdjustment { get; set; }
        public string Summary { get; set; } = "";
        public string Signal { get; set; } = "neutral";
        public string InjuriesSignal { get; set; } = "none";
        public string PersonalIssuesSignal { get; set; } = "none";
        public string PreviousMatchSignal { get; set; } = "unknown";
        public string MoraleSignal { get; set; } = "none";
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
