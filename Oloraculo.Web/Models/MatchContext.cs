namespace Oloraculo.Web.Models
{
    public class MatchContext
    {
        public required Fixture Fixture { get; init; }
        public required Team HomeTeam { get; init; }
        public required Team AwayTeam { get; init; }
        public Rating? HomeElo { get; set; }
        public Rating? AwayElo { get; set; }
        public Rating? HomeFifaRank { get; set; }
        public Rating? AwayFifaRank { get; set; }
        public IReadOnlyList<MatchResult> HomeRecentMatchHistory { get; set; }
        public IReadOnlyList<MatchResult> AwayRecentMatchHistory { get; set; }
        public FixtureContext? FixtureContext { get; set; }
        public FixtureWeatherContext? WeatherContext { get; set; }
        public TeamMoraleContext? HomeMoraleContext { get; set; }
        public TeamMoraleContext? AwayMoraleContext { get; set; }
        public string HomeTeamId => HomeTeam.Id;
        public string AwayTeamId => AwayTeam.Id;
    }
}
