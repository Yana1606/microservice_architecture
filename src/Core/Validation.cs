namespace MathTournament;

public static class Validation
{
    public static bool Text(string? value) => !string.IsNullOrWhiteSpace(value);
    public static void Tournament(Tournament t)
    {
        Require.That(t is not null && Text(t.Id) && Text(t.Name) && t.TeamSize == 4 && t.Starts < t.Ends &&
            t.Jury is { Count: >= 2 } && t.Jury.All(Text) && t.Jury.Distinct().Count() == t.Jury.Count &&
            Text(t.Senior) && t.Jury.Contains(t.Senior) && t.TiePolicy == "SharedCompetition", "invalid_tournament", 400);
    }
    public static void Game(GameConfig g)
    {
        Require.That(g is not null && Text(g.Id) && Text(g.Type) && g.Tasks is { Count: > 0 } &&
            g.Tasks.All(t => t is not null && Text(t.Id) && Text(t.Statement) && Text(t.Reference)) &&
            g.Tasks.Select(t => t.Id).Distinct().Count() == g.Tasks.Count &&
            g.Modifiers is { Count: > 0 } && g.Modifiers.All(x => Text(x.Key) && x.Value > 0) &&
            g.SolutionSeconds > 0 && g.AppealSeconds >= g.SolutionSeconds, "invalid_game", 400);
    }
    public static void Team(Team t)
    {
        Require.That(t is not null && Text(t.Id) && Text(t.Name) && Text(t.Captain) && t.Members is { Count: 4 } &&
            t.Members.All(Text) && t.Members.Distinct().Count() == 4 && t.Members.Contains(t.Captain), "exactly_four_distinct_members", 400);
    }
    public static void Envelope(Event e)
    {
        Require.That(e is not null && Text(e.Id) && Text(e.Producer) && Text(e.Type) && Text(e.TournamentId) &&
            Text(e.CorrelationId) && e.Version == 1 && e.Data.ValueKind != System.Text.Json.JsonValueKind.Undefined,
            "invalid_event_envelope", 400);
    }
    public static void Event(Event e)
    {
        Envelope(e);
        try
        {
            switch (e.Type)
            {
                case "ReserveScoreboard": case "ReserveForms":
                    var prepare=e.Body<PreparationRequest>();Game(prepare.Config);
                    Require.That(Text(prepare.AttemptId)&&prepare.TeamIds is {Count:>0}&&prepare.TeamIds.All(Text),"invalid_event_payload",400);break;
                case "ReleaseScoreboard": case "ReleaseForms": case "ScoreboardReserved": case "FormsReserved": case "ScoreboardPreparationRejected": case "FormsPreparationRejected": case "ScoreboardReleased": case "FormsReleased":
                    Require.That(Text(e.Body<PreparationReply>().AttemptId),"invalid_event_payload",400);break;
                case "TournamentStarted":
                    var t = e.Body<Tournament>(); Tournament(t);
                    Require.That(t.Id == e.TournamentId && t.Teams is { Count: >= 2 } && t.Games is { Count: > 0 }, "invalid_event_payload", 400);
                    foreach (var team in t.Teams) Team(team);
                    foreach (var game in t.Games) { Require.That(game is not null, "invalid_event_payload", 400); Game(game.Config); }
                    Require.That(t.Teams.Select(x => x.Id).Distinct().Count() == t.Teams.Count &&
                        t.Games.Select(x => x.Config.Id).Distinct().Count() == t.Games.Count, "invalid_event_payload", 400);
                    break;
                case "GameStarted":
                    var g = e.Body<Game>(); Game(g.Config);
                    Require.That(g.Config.Id == e.GameId && g.SolutionsDue.HasValue && g.AppealsDue >= g.SolutionsDue,
                        "invalid_event_payload", 400); break;
                case "SolutionBound":
                    var b = e.Body<Binding>(); Require.That(Text(b.AnonymousId) && Text(b.TeamId) && Text(b.TaskId) && Text(b.FormType), "invalid_event_payload", 400); break;
                case "VerdictRecorded":
                    var v = e.Body<Verdict>(); Require.That(Text(v.AnonymousId) && Text(v.JuryId) && Text(v.Authority) && Text(v.Outcome), "invalid_event_payload", 400); break;
                case "TeamSealed":
                    var teams = e.Body<TeamSeal>(); Require.That(teams.AnonymousIds is not null && teams.AnonymousIds.All(Text), "invalid_event_payload", 400); break;
                case "ChecksSealed":
                    var checks = e.Body<CheckSeal>(); Require.That(checks.VerdictEventIds is not null && checks.VerdictEventIds.All(Text), "invalid_event_payload", 400); break;
                case "ScoresReady":
                    var scores = e.Body<ScoreSnapshot>(); Require.That(scores.Rows is not null && scores.Rows.All(x => x is not null && Text(x.TeamId)) &&
                        scores.AppliedVerdicts is not null && scores.AppliedVerdicts.All(Text), "invalid_event_payload", 400); break;
                case "GameFinalized": case "WinnerDetermined":
                    var rows = e.Body<List<Standing>>(); Require.That(rows.Count > 0 && rows.All(x => x is not null && Text(x.TeamId)) &&
                        rows.Select(x => x.TeamId).Distinct().Count() == rows.Count, "invalid_event_payload", 400); break;
            }
            if (e.Type is not ("TournamentStarted" or "WinnerDetermined")) Require.That(Text(e.GameId), "game_id_required", 400);
        }
        catch (System.Text.Json.JsonException) { throw new RuleException("invalid_event_payload", 400); }
    }
}
