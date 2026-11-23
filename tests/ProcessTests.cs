using MathTournament;
using Xunit;
namespace Tests;

public class ProcessTests
{
    static Tournament NewTournament() => new() { Id = "t", Name = "Tournament", Starts = DateTimeOffset.UtcNow, Ends = DateTimeOffset.UtcNow.AddDays(1), Jury = ["j", "senior"], Senior = "senior", Regulation = "shared places; correct*modifier" };
    static GameConfig Config(string id = "g1") => new(id, "math", [new("task", "statement", "reference", 10, -2)], new() { ["normal"] = 1, ["risk"] = 2 }, 10, 20);
    static Team Team(string id) => new(id, id, id, [id, id + "1", id + "2", id + "3"]);
    static (State State, Engine Engine) Running()
    {
        var s = new State(); var e = new Engine("tournament"); e.Create(s, NewTournament(), "o");
        void Cmd(string action, GameConfig? g = null, Team? team = null, string? game = null) => e.Command(s, "t", action, team?.Captain ?? "o", s.Tournaments["t"].Revision, g, team, game);
        Cmd("configure", Config()); Cmd("configure", Config("g2")); Cmd("publish"); Cmd("register", team: Team("a")); Cmd("register", team: Team("b")); Cmd("start"); Cmd("start-game", game: "g1"); FinishPreparation(s,e,"g1");
        return (s, e);
    }
    static void FinishPreparation(State s,Engine e,string game)
    {
        var attempt=s.Tournaments["t"].Games.Single(x=>x.Config.Id==game).PreparationAttemptId!;
        e.Receive(s,External("ScoreboardReserved","scores",new PreparationReply(attempt),game));
        e.Receive(s,External("FormsReserved","simulator",new PreparationReply(attempt),game));
    }
    static Event External<T>(string type, string producer, T body, string game = "g1") => Event.Create(type, producer, "t", game, body);
    static State Scores(Engine tournament)
    {
        var state = new State(); var scores = new Engine("scores");
        foreach (var e in tournament.Events.Where(e => e.Type is "TournamentStarted" or "ReserveScoreboard" or "GameStarted")) scores.Receive(state, e);
        return state;
    }
    static void Close(State state, Engine e)
    {
        e.Receive(state, External("SolutionsClosed", "tournament", new { }));
        e.Receive(state, External("AppealsClosed", "tournament", new { }));
    }
    [Fact] public void RegistrationStartAndRoles()
    {
        var s = new State(); var e = new Engine("tournament"); e.Create(s, NewTournament(), "o");
        Assert.Throws<RuleException>(() => e.Command(s, "t", "start", "o", 1));
        Assert.Throws<RuleException>(() => e.Command(s, "t", "configure", "intruder", 1, Config()));
        e.Command(s, "t", "configure", "o", 1, Config()); e.Command(s, "t", "publish", "o", 2);
        Assert.Throws<RuleException>(() => e.Command(s, "t", "start", "o", 3));
        var wrong = Team("a") with { Members = ["a", "a1", "a2"] };
        Assert.Throws<RuleException>(() => e.Command(s, "t", "register", "a", 3, team: wrong));
        Assert.Throws<RuleException>(() => e.Command(s, "t", "register", "b", 3, team: Team("a")));
        Assert.Throws<RuleException>(() => e.Command(s, "t", "publish", "o", 1));
    }
    [Fact] public void RegistrationRejectionIsDurableAndIdempotent()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");var store=new Store(path,"tournament");
        store.Update((s,e)=>e.Create(s,NewTournament(),"o"));
        Assert.Throws<RuleException>(()=>store.Update((s,e)=>e.Command(s,"t","register","a",1,team:Team("a")),"rejected","fp"));
        var count=store.Pending().Count;Assert.Single(store.Pending(),d=>d.Event.Type=="RegistrationRejected");
        Assert.Throws<RuleException>(()=>store.Update((_,_)=>throw new Exception("must not run"),"rejected","fp"));
        Assert.Equal(count,store.Pending().Count);Assert.Empty(store.Read().Tournaments["t"].Teams);
    }
    [Fact] public void ConfigurationAndSequenceFreezeAtStart()
    {
        var (s, e) = Running(); var t = s.Tournaments["t"];
        Assert.Throws<RuleException>(() => e.Command(s, "t", "configure", "o", t.Revision, Config()));
        Assert.Throws<RuleException>(() => e.Command(s, "t", "delete", "o", t.Revision, gameId: "g1"));
        Assert.Throws<RuleException>(() => e.Command(s, "t", "start-game", "o", t.Revision, gameId: "g2"));
        Assert.Throws<RuleException>(() => e.Command(s, "t", "register", "c", t.Revision, team: Team("c")));
    }
    [Fact] public void InitializeEveryCellAndApplyModifier()
    {
        var (_, te) = Running(); var s = Scores(te); var e = new Engine("scores"); var g = s.Scores["t/g1"];
        Assert.Equal(2, g.Cells.Count); Assert.All(g.Cells.Values.SelectMany(x => x.Values), c => { Assert.Equal(0, c.Points); Assert.Equal("NotSubmitted", c.Status); });
        e.Receive(s, External("SolutionBound", "team", new Binding("anon", "a", "task", "risk")));
        e.Receive(s, External("VerdictRecorded", "checking", new Verdict("anon", 1, "Correct", "senior", "Senior")));
        Assert.Equal(20, g.Cells["a"]["task"].Points);
    }
    [Theory][InlineData("AppealUpheld",20)][InlineData("AppealRejected",-4)]
    public void AppealReplacesAndReorderingWorks(string outcome, int points)
    {
        var (_, te) = Running(); var s = Scores(te); var e = new Engine("scores");
        var first = External("VerdictRecorded", "checking", new Verdict("anon", 1, "Incorrect", "j", "Jury"));
        var appeal = External("VerdictRecorded", "checking", new Verdict("anon", 2, outcome, "senior", "Senior"));
        e.Receive(s, appeal); e.Receive(s, first); e.Receive(s, first);
        e.Receive(s, External("SolutionBound", "team", new Binding("anon", "a", "task", "risk")));
        e.Receive(s, appeal); Assert.Empty(s.Pending); Assert.Equal(points, s.Scores["t/g1"].Cells["a"]["task"].Points);
        e.Receive(s, first with { Id = "semantic-duplicate" }); Assert.Equal(points, s.Scores["t/g1"].Cells["a"]["task"].Points);
    }
    [Fact] public void SealsCannotOvertakeMissingVerdict()
    {
        var (_, te) = Running(); var s = Scores(te); var e = new Engine("scores"); Close(s,e);
        var v = External("VerdictRecorded", "checking", new Verdict("anon", 1, "Correct", "j", "Jury"));
        e.Receive(s, External("TeamSealed", "team", new TeamSeal(["anon"])));
        e.Receive(s, External("ChecksSealed", "checking", new CheckSeal([v.Id])));
        e.Receive(s, External("SolutionBound", "team", new Binding("anon", "a", "task", "normal")));
        Assert.DoesNotContain(e.Events, x => x.Type == "ScoresReady"); e.Receive(s,v);
        Assert.Single(e.Events, x => x.Type == "ScoresReady");
        e.Receive(s,v); Assert.Single(e.Events, x => x.Type == "ScoresReady");
    }
    [Fact] public void InvalidLateVerdictIsQuarantined()
    {
        var (_, te) = Running(); var s = Scores(te); var e = new Engine("scores"); Close(s,e);
        e.Receive(s, External("TeamSealed", "team", new TeamSeal([]))); e.Receive(s, External("ChecksSealed", "checking", new CheckSeal([])));
        Assert.True(s.Scores["t/g1"].Ready);
        e.Receive(s, External("SolutionBound", "team", new Binding("late", "a", "task", "normal")));
        Assert.Single(s.Quarantine); Assert.Equal(0, s.Scores["t/g1"].Cells["a"]["task"].Points);
    }
    [Fact] public void ConflictingDuplicateAndForgedProducerRejected()
    {
        var (_, te) = Running(); var s = Scores(te); var e = new Engine("scores");
        var bound = External("SolutionBound", "team", new Binding("anon", "a", "task", "normal")); e.Receive(s, bound);
        Assert.Throws<RuleException>(() => e.Receive(s, bound with { CorrelationId = "other" }));
        Assert.Throws<RuleException>(() => e.Receive(s, bound with { Id = "forged", Producer = "checking" }));
    }
    [Fact] public void SharedCompetitionPlacesAndTournamentPoints()
    {
        var rank = Engine.Rank(new[] { ("a", 10m), ("b", 10m), ("c", 0m) });
        Assert.Equal(new[] { 1,1,3 }, rank.Select(x=>x.Place)); Assert.Equal(new[] {100,100,96},rank.Select(x=>x.Points));
    }
    [Fact] public void DurableDeadlinesOutboxAndCommandIdempotency()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var store = new Store(path, "tournament");
        store.Update((s,e) => e.Create(s,NewTournament(),"o"),"create","same");
        var first = store.Pending().Count; store.Update((_,_) => throw new Exception("must not execute"),"create","same"); Assert.Equal(first,store.Pending().Count);
        Assert.Throws<RuleException>(()=>store.Update((_,_)=>{},"create","other"));
        store.Update((s,e)=> { var (running,_) = Running(); s.Tournaments = running.Tournaments; });
        var restarted = new Store(path,"tournament"); restarted.Update((s,e)=>e.Tick(s,DateTimeOffset.UtcNow.AddHours(1)));
        Assert.True(restarted.Read().Tournaments["t"].Games[0].AppealsClosed);
        var count = restarted.Pending().Count; restarted.Update((s,e)=>e.Tick(s,DateTimeOffset.UtcNow.AddHours(2))); Assert.Equal(count,restarted.Pending().Count);
        var before = Json.Write(restarted.Read()); Assert.Throws<Exception>(()=>restarted.Update((s,_)=>{s.Tournaments.Clear();throw new Exception();})); Assert.Equal(before,Json.Write(restarted.Read()));
    }
    [Fact] public async Task ConcurrentCommandsUseRevisionAndOutboxIsRetryable()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db");
        var store = new Store(path, "tournament"); store.Update((s,e)=>e.Create(s,NewTournament(),"o"));
        async Task<bool> Attempt(string game) => await Task.Run(() =>
        {
            try { new Store(path,"tournament").Update((s,e)=>e.Command(s,"t","configure","o",1,Config(game))); return true; }
            catch (RuleException ex) when(ex.Message=="revision_conflict") { return false; }
        });
        var outcomes = await Task.WhenAll(Attempt("g1"),Attempt("g2")); Assert.Single(outcomes,x=>x);
        Assert.Single(store.Read().Tournaments["t"].Games);
        var delivery = store.Pending()[0]; store.Delivered(delivery,"connection lost");
        Assert.Contains(new Store(path,"tournament").Pending(),d=>d.Event.Id==delivery.Event.Id);
        store.Delivered(delivery); Assert.DoesNotContain(store.Pending(),d=>d.Event.Id==delivery.Event.Id && d.Destination==delivery.Destination);
    }
    [Fact] public void PendingEventsSurviveRestartAndValidAppealIsRequired()
    {
        var (_,te)=Running(); var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db"); var store=new Store(path,"scores");
        var v=External("VerdictRecorded","checking",new Verdict("anon",1,"Correct","j","Jury"));
        store.Update((s,e)=>e.Receive(s,v)); Assert.Single(store.Read().Pending);
        var restarted=new Store(path,"scores");
        restarted.Update((s,e)=>{foreach(var message in te.Events.Where(x=>x.Type is "TournamentStarted" or "ReserveScoreboard" or "GameStarted"))e.Receive(s,message);e.Receive(s,External("SolutionBound","team",new Binding("anon","a","task","normal")));});
        Assert.Empty(restarted.Read().Pending); Assert.Equal(10,restarted.Read().Scores["t/g1"].Cells["a"]["task"].Points);
        restarted.Update((s,e)=>e.Receive(s,External("VerdictRecorded","checking",new Verdict("anon",2,"AppealUpheld","senior","Senior"))));
        Assert.Single(restarted.Read().Quarantine);
    }
    [Fact] public void FinalScoresStillWaitForBothClosuresAndCompleteTeams()
    {
        var (_,te)=Running();var s=new State();var e=new Engine("results");
        foreach(var message in te.Events.Where(x=>x.Type is "TournamentStarted" or "GameStarted"))e.Receive(s,message);
        var ready=External("ScoresReady","scores",new ScoreSnapshot([new("a",10,0,0),new("b",0,0,0)],[]));e.Receive(s,ready);
        Assert.Empty(s.Finals);e.Receive(s,External("SolutionsClosed","tournament",new{}));Assert.Empty(s.Finals);
        e.Receive(s,External("AppealsClosed","tournament",new{}));Assert.Single(s.Finals);Assert.Empty(s.Winners);
    }
    [Theory]
    [InlineData("AppealUpheld")]
    [InlineData("AppealRejected")]
    public void AppealLimitSurvivesRestartAndAppliesAcrossGames(string firstOutcome)
    {
        var (tournament, commands) = Running();
        var scores = Scores(commands);
        var engine = new Engine("scores");
        var standings = new List<Standing> { new("a", 10, 1, 100), new("b", 0, 2, 98) };

        engine.Receive(scores, External("SolutionBound", "team", new Binding("first", "a", "task", "normal")));
        engine.Receive(scores, External("VerdictRecorded", "checking", new Verdict("first", 1, "Incorrect", "j", "Jury")));
        engine.Receive(scores, External("VerdictRecorded", "checking", new Verdict("first", 2, firstOutcome, "senior", "Senior")));
        var final = External("GameFinalized", "results", standings);
        engine.Receive(scores, final);
        commands.Receive(tournament, final);
        scores = Json.Read<State>(Json.Write(scores));

        commands.Command(tournament, "t", "start-game", "o", tournament.Tournaments["t"].Revision, gameId: "g2");
        FinishPreparation(tournament,commands,"g2");
        engine.Receive(scores,commands.Events.Last(x=>x.Type=="ReserveScoreboard"));
        engine.Receive(scores, commands.Events.Last());
        engine.Receive(scores, External("SolutionBound", "team", new Binding("second", "a", "task", "normal"), "g2"));
        engine.Receive(scores, External("VerdictRecorded", "checking", new Verdict("second", 1, "Incorrect", "j", "Jury"), "g2"));
        var repeated = External("VerdictRecorded", "checking", new Verdict("second", 2, "AppealUpheld", "senior", "Senior"), "g2");
        engine.Receive(scores, repeated);

        Assert.Equal("appeal_already_used", scores.Quarantine[repeated.Id]);
        var cell = scores.Scores["t/g2"].Cells["a"]["task"];
        Assert.Equal("Incorrect", cell.Status);
        Assert.Equal(-2, cell.Points);
    }

    [Fact]
    public void ScoreboardWaitsForEveryGameFinalBeforeShowingWinner()
    {
        var (tournament, commands) = Running();
        var scores = Scores(commands);
        var engine = new Engine("scores");
        var standings = new List<Standing> { new("a", 10, 1, 100), new("b", 0, 2, 98) };
        var winner = Event.Create("WinnerDetermined", "results", "t", null, standings);
        engine.Receive(scores, winner);
        Assert.Contains(winner.Id, scores.Pending.Keys);
        Assert.Empty(scores.Winners);

        var first = External("GameFinalized", "results", standings);
        engine.Receive(scores, first);
        Assert.Empty(scores.Winners);
        commands.Receive(tournament, first);
        commands.Command(tournament, "t", "start-game", "o", tournament.Tournaments["t"].Revision, gameId: "g2");
        FinishPreparation(tournament,commands,"g2");
        engine.Receive(scores,commands.Events.Last(x=>x.Type=="ReserveScoreboard"));
        engine.Receive(scores, commands.Events.Last());
        engine.Receive(scores, External("GameFinalized", "results", standings, "g2"));

        Assert.Contains("t", scores.Winners);
        Assert.Equal(2, scores.Finals.Count);
        Assert.Empty(scores.Pending);
    }

    [Fact] public void FullTournamentTwoTeamsTwoGamesWithDuplicateAndReorderedDelivery()
    {
        var (ts,te) = Running(); var ss = new State(); var rs = new State(); var se = new Engine("scores"); var re = new Engine("results");
        void Broadcast()
        {
            foreach(var e in te.Events.Where(e=>e.Type is "TournamentStarted" or "ReserveScoreboard" or "GameStarted" or "SolutionsClosed" or "AppealsClosed").Reverse().ToList()) {se.Receive(ss,e);if(e.Type!="ReserveScoreboard")re.Receive(rs,e);}
            foreach(var e in se.Events.Where(e=>e.Type=="ScoresReady").ToList()) re.Receive(rs,e);
            foreach(var e in re.Events.Where(e=>e.Type is "GameFinalized" or "WinnerDetermined").Reverse().ToList()){te.Receive(ts,e);se.Receive(ss,e);}
        }
        Broadcast();
        foreach(var game in new[]{"g1","g2"})
        {
            if(game=="g2"){te.Command(ts,"t","start-game","o",ts.Tournaments["t"].Revision,gameId:game);FinishPreparation(ts,te,game);}
            Broadcast();
            var v = External("VerdictRecorded","checking",new Verdict(game+"anon",1,"Correct","j","Jury"),game);
            se.Receive(ss,External("TeamSealed","team",new TeamSeal([game+"anon"]),game));
            se.Receive(ss,External("ChecksSealed","checking",new CheckSeal([v.Id]),game));
            se.Receive(ss,v);se.Receive(ss,External("SolutionBound","team",new Binding(game+"anon","a","task","normal"),game));
            Broadcast();Assert.False(rs.Finals.ContainsKey("t/"+game));
            te.Tick(ts,DateTimeOffset.UtcNow.AddHours(1));Broadcast();Broadcast();
            Assert.Equal("Completed",ts.Tournaments["t"].Games.Single(g=>g.Config.Id==game).Status);
        }
        Assert.Equal("Completed",ts.Tournaments["t"].Status);Assert.Equal(2,rs.Finals.Count);
        Assert.Equal(200,rs.Finals.Values.Sum(rows=>rows.Single(x=>x.TeamId=="a").Points));
        Assert.Equal(196,rs.Finals.Values.Sum(rows=>rows.Single(x=>x.TeamId=="b").Points));
        Assert.Single(re.Events, e=>e.Type=="WinnerDetermined");Assert.Single(te.Events, e=>e.Type=="TournamentCompleted");
    }
}
