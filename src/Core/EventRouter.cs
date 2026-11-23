namespace MathTournament;

public sealed class EventRouter : IEventRouter
{
    public IReadOnlyList<string> Resolve(Event message)
    {
        return message.Type switch
        {
            "ReserveScoreboard" or
            "ReleaseScoreboard" =>
                ["scores"],

            "ReserveForms" or
            "ReleaseForms" =>
                ["simulator"],

            "ScoreboardReserved" or
            "ScoreboardPreparationRejected" or
            "ScoreboardReleased" or
            "FormsReserved" or
            "FormsPreparationRejected" or
            "FormsReleased" =>
                ["tournament"],

            "TournamentStarted" or
            "GameStarted" or
            "SolutionsClosed" or
            "AppealsClosed" =>
                ["scores", "results", "simulator"],

            "TournamentCreated" or
            "GamesConfigured" or
            "TournamentPublished" or
            "TeamRegistered" or
            "RegistrationRejected" or
            "TournamentCompleted" =>
                ["simulator"],

            "ScoresReady" =>
                ["results"],

            "GameFinalized" or
            "WinnerDetermined" =>
                ["tournament", "scores", "simulator"],

            "ScoreChanged" or
            "AppealScoreChanged" or
            "InitialResultsCreated" or
            "TournamentPointsUpdated" =>
                ["simulator"],

            _ => Array.Empty<string>()
        };
    }
}