namespace MathTournament;

public sealed class Engine(string service)
{
    public string Service => service;
    public List<Event> Events { get; } = [];
    private void Emit<T>(string type, Tournament t, string? game, T body, string? cause = null) =>
        Events.Add(Event.Create(type, service, t.Id, game, body, cause));
    public void Create(State s, Tournament t, string actor)
    {
        Require.That(service == "tournament", "wrong_service");
        Require.That(!s.Tournaments.ContainsKey(t.Id), "tournament_exists");
        Require.That(!string.IsNullOrWhiteSpace(t.Id) && !string.IsNullOrWhiteSpace(t.Name) &&
            t.TeamSize == 4 && t.Starts < t.Ends && t.Jury.Count >= 2 && t.Jury.Distinct().Count() == t.Jury.Count &&
            t.Jury.All(x => !string.IsNullOrWhiteSpace(x)) && t.Jury.Contains(t.Senior) &&
            t.TiePolicy == "SharedCompetition", "invalid_tournament", 400);
        t.Organizer = actor; t.Status = "Draft"; t.Games = []; t.Teams = []; t.Revision = 1;
        s.Tournaments.Add(t.Id, t); Emit("TournamentCreated", t, null, t);
    }
    public void Command(State s, string id, string action, string actor, long revision,
        GameConfig? config = null, Team? team = null, string? gameId = null)
    {
        Require.That(service == "tournament", "wrong_service");
        Require.That(s.Tournaments.ContainsKey(id), "tournament_not_found", 404);
        var t = s.Tournaments[id];
        Require.That(t.Revision == revision, "revision_conflict");
        if (action != "register") Require.That(t.Organizer == actor, "organizer_required", 403);
        switch (action)
        {
            case "configure":
                Require.That(t.Status is "Draft" or "Registration", "tournament_already_started");
                Validate(config!);
                var existing = t.Games.FindIndex(g => g.Config.Id == config!.Id);
                if (existing >= 0) t.Games[existing] = new Game { Config = config! };
                else t.Games.Add(new Game { Config = config! });
                Emit("GamesConfigured", t, config!.Id, t.Games); break;
            case "delete":
                Require.That(t.Status is "Draft" or "Registration", "tournament_already_started");
                Require.That(t.Games.RemoveAll(g => g.Config.Id == gameId) == 1, "game_not_found", 404);
                Emit("GamesConfigured", t, gameId, t.Games); break;
            case "publish":
                Require.That(t.Status == "Draft" && t.Games.Count > 0 && !string.IsNullOrWhiteSpace(t.Regulation), "not_ready_to_publish");
                t.Status = "Registration"; Emit("TournamentPublished", t, null, t.Regulation); break;
            case "register":
                try
                {
                Require.That(t.Status == "Registration", "registration_closed");
                Require.That(team != null && team.Captain == actor, "captain_required", 403);
                Require.That(team!.Members.Count == 4 && team.Members.Distinct().Count() == 4 &&
                    team.Members.Contains(actor) && team.Members.All(x => !string.IsNullOrWhiteSpace(x)) &&
                    !string.IsNullOrWhiteSpace(team.Id) && !string.IsNullOrWhiteSpace(team.Name), "exactly_four_distinct_members", 400);
                Require.That(!t.Teams.Any(x => x.Id == team.Id || x.Members.Intersect(team.Members).Any()), "team_or_member_already_registered");
                }
                catch (RuleException ex)
                {
                    Emit("RegistrationRejected", t, null, new { TeamId = team?.Id, Reason = ex.Message });
                    throw;
                }
                t.Teams.Add(team!); Emit("TeamRegistered", t, null, team); break;
            case "start":
                Require.That(t.Status == "Registration", "registration_must_be_open");
                Require.That(t.Teams.Count >= 2 && t.Games.Count > 0, "two_teams_and_games_required");
                t.Status = "Running"; Emit("TournamentStarted", t, null, t); break;
            case "start-game":
                Require.That(t.Status == "Running", "tournament_not_running");
                var next = t.Games.FirstOrDefault(g => g.Status != "Completed");
                Require.That(next != null && next.Config.Id == gameId && next.Status == "Configured", "previous_game_not_completed_or_wrong_order");
                next!.Status = "Running";
                next.SolutionsDue = DateTimeOffset.UtcNow.AddSeconds(next.Config.SolutionSeconds);
                next.AppealsDue = DateTimeOffset.UtcNow.AddSeconds(next.Config.AppealSeconds);
                Emit("GameStarted", t, gameId, next); break;
            default: throw new RuleException("unknown_command", 400);
        }
        t.Revision++;
    }
    public static void Validate(GameConfig g)
    {
        Require.That(g != null && !string.IsNullOrWhiteSpace(g.Id) && !string.IsNullOrWhiteSpace(g.Type) &&
            g.Tasks.Count > 0 && g.Tasks.Select(x => x.Id).Distinct().Count() == g.Tasks.Count &&
            g.Tasks.All(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.Statement) && !string.IsNullOrWhiteSpace(x.Reference)) &&
            g.Modifiers.Count > 0 && g.Modifiers.All(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value > 0) &&
            g.SolutionSeconds > 0 && g.AppealSeconds >= g.SolutionSeconds, "invalid_game", 400);
    }
    public void Tick(State s, DateTimeOffset now)
    {
        if (service == "tournament") foreach (var t in s.Tournaments.Values)
        foreach (var g in t.Games.Where(g => g.Status == "Running"))
        {
            if (!g.SolutionsClosed && now >= g.SolutionsDue) { g.SolutionsClosed = true; t.Revision++; Emit("SolutionsClosed", t, g.Config.Id, g.SolutionsDue); }
            if (!g.AppealsClosed && now >= g.AppealsDue) { g.AppealsClosed = true; t.Revision++; Emit("AppealsClosed", t, g.Config.Id, g.AppealsDue); }
        }
        Drain(s);
    }
    public void Receive(State s, Event e)
    {
        Require.That(e.Version == 1 && !string.IsNullOrWhiteSpace(e.Id), "unsupported_event", 400);
        Require.That(Allowed(e), "event_producer_or_type_forbidden", 403);
        var fingerprint = Json.Write(e);
        if (s.Receipts.TryGetValue(e.Id, out var previous)) { Require.That(previous == fingerprint, "event_id_payload_conflict"); return; }
        s.Receipts.Add(e.Id, fingerprint); s.Pending.Add(e.Id, e); Drain(s);
    }
    private bool Allowed(Event e) => (e.Producer, e.Type, service) switch
    {
        ("tournament", "TournamentStarted" or "GameStarted" or "SolutionsClosed" or "AppealsClosed", "scores" or "results") => true,
        ("team", "SolutionBound" or "TeamSealed", "scores") => true,
        ("checking", "VerdictRecorded" or "ChecksSealed", "scores") => true,
        ("scores", "ScoresReady", "results") => true,
        ("results", "GameFinalized" or "WinnerDetermined", "tournament" or "scores") => true,
        _ => false
    };
    private void Drain(State s)
    {
        bool progress;
        do
        {
            progress = false;
            foreach (var e in s.Pending.Values.ToList())
            {
                try { if (!Apply(s, e)) continue; }
                catch (RuleException ex) { s.Quarantine[e.Id] = ex.Message; }
                s.Pending.Remove(e.Id); s.AppliedEvents.Add(e.Id); progress = true;
            }
        } while (progress);
        if (service == "scores") Ready(s);
        if (service == "results") FinalizeGames(s);
    }
    private bool Apply(State s, Event e)
    {
        if (e.Type == "TournamentStarted")
        {
            if (!s.Tournaments.ContainsKey(e.TournamentId)) s.Tournaments.Add(e.TournamentId, e.Body<Tournament>());
            return true;
        }
        if (!s.Tournaments.TryGetValue(e.TournamentId, out var t)) return false;
        var key = State.Key(t.Id, e.GameId ?? "");
        var g = t.Games.SingleOrDefault(x => x.Config.Id == e.GameId);
        if (e.Type == "WinnerDetermined")
        {
            if (t.Games.Any(x => x.Status != "Completed")) return false;
            if (service == "tournament")
            {
                if (t.Status != "Completed") { t.Status = "Completed"; t.Revision++; Emit("TournamentCompleted", t, null, e.Body<List<Standing>>(), e.Id); }
            }
            else s.Winners.Add(t.Id);
            return true;
        }
        Require.That(g != null, "unknown_game");
        if (e.Type == "GameStarted")
        {
            if (g!.Status == "Configured")
            {
                var started = e.Body<Game>(); g.Status = "Running"; g.SolutionsDue = started.SolutionsDue; g.AppealsDue = started.AppealsDue;
                if (service == "scores")
                {
                    s.Scores.Add(key, new ScoreGame { Cells = t.Teams.ToDictionary(x => x.Id,
                        _ => g.Config.Tasks.ToDictionary(x => x.Id, _ => new Cell())) });
                    Emit("InitialResultsCreated", t, g.Config.Id, new { Teams = t.Teams.Select(x => x.Id), Tasks = g.Config.Tasks.Select(x => x.Id) }, e.Id);
                }
            }
            return true;
        }
        if (g!.Status == "Configured") return false;
        if (e.Type == "SolutionsClosed") { g.SolutionsClosed = true; return true; }
        if (e.Type == "AppealsClosed") { g.AppealsClosed = true; return true; }
        if (e.Type == "GameFinalized")
        {
            var rows = e.Body<List<Standing>>();
            Require.That(rows.Select(x => x.TeamId).ToHashSet().SetEquals(t.Teams.Select(x => x.Id)), "invalid_final_teams");
            if (!s.Finals.ContainsKey(key)) { s.Finals[key] = rows; g.Status = "Completed"; t.Revision++; }
            return true;
        }
        if (e.Type == "ScoresReady")
        {
            if (!g.SolutionsClosed || !g.AppealsClosed) return false;
            var snapshot = e.Body<ScoreSnapshot>();
            Require.That(snapshot.Rows.Count == t.Teams.Count && snapshot.Rows.Select(x => x.TeamId).ToHashSet().SetEquals(t.Teams.Select(x => x.Id)), "incomplete_scores");
            if (s.Snapshots.TryGetValue(key, out var prior)) Require.That(Json.Write(prior) == Json.Write(snapshot), "sealed_scores_conflict");
            else s.Snapshots[key] = snapshot;
            return true;
        }
        var score = s.Scores[key];
        if (e.Type == "TeamSealed")
        {
            if (!g.SolutionsClosed) return false;
            var ids = e.Body<TeamSeal>().AnonymousIds;
            Require.That(ids.Count == ids.Distinct().Count(), "duplicate_submission_in_seal");
            if (score.TeamSeal != null) Require.That(score.TeamSeal.ToHashSet().SetEquals(ids), "team_seal_conflict");
            else score.TeamSeal = ids;
        }
        if (e.Type == "ChecksSealed")
        {
            if (!g.SolutionsClosed || !g.AppealsClosed) return false;
            var ids = e.Body<CheckSeal>().VerdictEventIds;
            Require.That(ids.Count == ids.Distinct().Count(), "duplicate_verdict_in_seal");
            if (score.CheckSeal != null) Require.That(score.CheckSeal.ToHashSet().SetEquals(ids), "check_seal_conflict");
            else score.CheckSeal = ids;
        }
        if (e.Type == "SolutionBound")
        {
            var b = e.Body<Binding>();
            Require.That(!score.Ready && (score.TeamSeal == null || score.TeamSeal.Contains(b.AnonymousId)), "submission_after_seal");
            Require.That(score.Cells.ContainsKey(b.TeamId) && score.Cells[b.TeamId].ContainsKey(b.TaskId) && g.Config.Modifiers.ContainsKey(b.FormType), "invalid_binding");
            if (score.Bindings.TryGetValue(b.AnonymousId, out var prior)) Require.That(prior == b, "binding_conflict");
            else
            {
                Require.That(!score.Bindings.Values.Any(x => x.TeamId == b.TeamId && x.TaskId == b.TaskId), "task_already_bound");
                score.Bindings.Add(b.AnonymousId, b);
            }
        }
        if (e.Type == "VerdictRecorded")
        {
            var v = e.Body<Verdict>();
            if (!score.Bindings.TryGetValue(v.AnonymousId, out var b)) return false;
            var cell = score.Cells[b.TeamId][b.TaskId];
            Require.That(!score.Ready && (score.CheckSeal == null || score.CheckSeal.Contains(e.Id)), "verdict_after_seal");
            Require.That(t.Jury.Contains(v.JuryId) && (v.Authority == "Jury" || v.Authority == "Senior" && v.JuryId == t.Senior), "invalid_jury");
            Require.That(v.Revision is 1 or 2, "invalid_verdict_revision");
            Require.That(v.Revision == 1 ? v.Outcome is "Correct" or "Incorrect" : v.Outcome is "AppealUpheld" or "AppealRejected", "invalid_outcome");
            var semantic = v.AnonymousId + "/" + v.Revision;
            if (score.Verdicts.TryGetValue(semantic, out var old))
            { Require.That(old == v, "conflicting_verdict"); score.Applied.Add(e.Id); return true; }
            if (v.Revision == 2 && cell.Revision == 0) return false;
            if (v.Revision == 2)
            {
                Require.That(cell.Status == "Incorrect", "appeal_requires_incorrect");
                var alreadyAppealed = t.Games.Any(game =>
                    s.Scores.TryGetValue(State.Key(t.Id, game.Config.Id), out var gameScore) &&
                    gameScore.Cells[b.TeamId].TryGetValue(b.TaskId, out var priorCell) &&
                    priorCell.Revision == 2);
                Require.That(!alreadyAppealed, "appeal_already_used");
            }
            score.Verdicts.Add(semantic, v);
            var rule = g.Config.Tasks.Single(x => x.Id == b.TaskId);
            cell.Points = v.Outcome switch
            {
                "Correct" or "AppealUpheld" => rule.Correct * g.Config.Modifiers[b.FormType],
                "Incorrect" => rule.Incorrect * g.Config.Modifiers[b.FormType],
                _ => cell.Points
            };
            cell.Status = v.Outcome; cell.Revision = v.Revision; cell.AnonymousId = v.AnonymousId;
            score.Applied.Add(e.Id);
            Emit(v.Revision == 1 ? "ScoreChanged" : "AppealScoreChanged", t, e.GameId,
                new { b.TeamId, b.TaskId, cell.Status, cell.Points, GameTotal = score.Cells[b.TeamId].Values.Sum(x => x.Points) }, e.Id);
        }
        return true;
    }
    private void Ready(State s)
    {
        foreach (var t in s.Tournaments.Values) foreach (var g in t.Games)
        {
            var key = State.Key(t.Id, g.Config.Id);
            if (!s.Scores.TryGetValue(key, out var sc) || sc.Ready || !g.SolutionsClosed || !g.AppealsClosed ||
                sc.TeamSeal == null || sc.CheckSeal == null || !sc.TeamSeal.ToHashSet().SetEquals(sc.Bindings.Keys) ||
                !sc.CheckSeal.ToHashSet().SetEquals(sc.Applied) || sc.Bindings.Values.Any(b => sc.Cells[b.TeamId][b.TaskId].Revision == 0) ||
                s.Pending.Values.Any(e => e.TournamentId == t.Id && e.GameId == g.Config.Id) ||
                s.Quarantine.Keys.Any(id => { var e = Json.Read<Event>(s.Receipts[id]); return e.TournamentId == t.Id && e.GameId == g.Config.Id; })) continue;
            sc.Ready = true;
            Emit("ScoresReady", t, g.Config.Id, new ScoreSnapshot(sc.Cells.Select(x => new Standing(x.Key, x.Value.Values.Sum(c => c.Points), 0, 0)).ToList(), sc.Applied.Order().ToList()));
        }
    }
    public static List<Standing> Rank(IEnumerable<(string Team, decimal Score)> source) => source
        .OrderByDescending(x => x.Score).ThenBy(x => x.Team, StringComparer.Ordinal)
        .Select(x => new Standing(x.Team, x.Score, 1 + source.Count(y => y.Score > x.Score), 100 - 2 * source.Count(y => y.Score > x.Score))).ToList();
    private void FinalizeGames(State s)
    {
        foreach (var t in s.Tournaments.Values)
        {
            foreach (var g in t.Games)
            {
                var key = State.Key(t.Id, g.Config.Id);
                if (s.Finals.ContainsKey(key) || !g.SolutionsClosed || !g.AppealsClosed || !s.Snapshots.TryGetValue(key, out var snapshot)) continue;
                var rows = Rank(snapshot.Rows.Select(x => (x.TeamId, x.Score)));
                s.Finals.Add(key, rows); g.Status = "Completed";
                Emit("GameFinalized", t, g.Config.Id, rows); Emit("TournamentPointsUpdated", t, g.Config.Id, rows);
            }
            if (t.Games.Count == 0 || t.Games.Any(g => !s.Finals.ContainsKey(State.Key(t.Id, g.Config.Id))) || s.Winners.Contains(t.Id)) continue;
            var standings = Rank(t.Teams.Select(team => (team.Id, (decimal)t.Games.Sum(g => s.Finals[State.Key(t.Id, g.Config.Id)].Single(x => x.TeamId == team.Id).Points))));
            s.Winners.Add(t.Id); Emit("WinnerDetermined", t, null, standings);
        }
    }
}
