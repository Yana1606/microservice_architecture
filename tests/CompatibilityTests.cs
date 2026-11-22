using System.Text.Json;
using MathTournament;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Tests;

public class CompatibilityTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    private static Tournament Tournament(string id, string game) => new()
    {
        Id=id, Name=id, Starts=At, Ends=At.AddDays(1), Jury=["jury","senior"], Senior="senior",
        Teams=[new("a","A","a",["a","a1","a2","a3"]),new("b","B","b",["b","b1","b2","b3"])],
        Games=[new(){Config=new(game,"Math",[new("q","2+2","4",10,0)],new(){["normal"]=1},5,10)}]
    };

    [Theory]
    [InlineData("cup/x","round","cup","x/round")]
    [InlineData("cup/x","round","cup%2Fx","round")]
    public void DistinctTournamentGamePairsKeepIndependentScoresAndFinals(string firstId,string firstGame,string secondId,string secondGame)
    {
        var scores=new State();var results=new State();var se=new Engine("scores");var re=new Engine("results");
        var inputs=new[]{(firstId,firstGame,10m),(secondId,secondGame,0m)};
        foreach(var (id,game,points) in inputs)
        {
            var tournament=Tournament(id,game);
            void Both(Event e){se.Receive(scores,e);re.Receive(results,e);}
            Both(Event.Create("TournamentStarted","tournament",id,null,tournament));
            Both(Event.Create("GameStarted","tournament",id,game,new Game{Config=tournament.Games[0].Config,Status="Running",SolutionsDue=At.AddSeconds(5),AppealsDue=At.AddSeconds(10)}));
            var verdict=Event.Create("VerdictRecorded","checking",id,game,new Verdict("anon",1,points==10?"Correct":"Incorrect","jury","Jury"));
            se.Receive(scores,verdict);
            se.Receive(scores,Event.Create("SolutionBound","team",id,game,new Binding("anon","a","q","normal")));
            Both(Event.Create("SolutionsClosed","tournament",id,game,At.AddSeconds(5)));
            Both(Event.Create("AppealsClosed","tournament",id,game,At.AddSeconds(10)));
            se.Receive(scores,Event.Create("TeamSealed","team",id,game,new TeamSeal(["anon"])));
            se.Receive(scores,Event.Create("ChecksSealed","checking",id,game,new CheckSeal([verdict.Id])));
            var ready=se.Events.Last(e=>e.Type=="ScoresReady");re.Receive(results,ready);re.Receive(results,ready);
        }
        Assert.Equal(2,scores.Scores.Count);Assert.Equal(2,results.Finals.Count);Assert.Equal(2,results.Winners.Count);
        foreach(var (id,game,points) in inputs)
        {
            var key=State.Key(id,game);Assert.Equal(points,scores.Scores[key].Cells["a"]["q"].Points);
            Assert.Equal(points,results.Finals[key].Single(x=>x.TeamId=="a").Score);
        }
        Assert.Empty(scores.Pending);Assert.Empty(scores.Quarantine);
    }

    [Theory]
    [InlineData("cup/x","round")]
    [InlineData("cup%2Fx","round")]
    public void LegacyGameKeysUpgradeWithoutChangingDataAndSurviveAnotherRestart(string id,string game)
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
        var state=new State();state.Tournaments[id]=Tournament(id,game);
        var oldKey=id+"/"+game;
        state.Scores[oldKey]=new(){Cells=new(){["a"]=new(){["q"]=new(){Points=17,Status="Correct"}}}};
        state.Finals[oldKey]=[new("a",17,1,100),new("b",0,2,98)];
        state.Snapshots[oldKey]=new(state.Finals[oldKey],["verdict"]);
        state.Receipts["receipt"]="unchanged";state.Winners.Add(id);
        SeedLegacy(path,state);
        var store=new Store(path,"scores");var upgraded=store.Read();var key=State.Key(id,game);
        Assert.NotEqual(oldKey,key);Assert.Equal(17,upgraded.Scores[key].Cells["a"]["q"].Points);
        Assert.Equal(state.Finals[oldKey],upgraded.Finals[key]);Assert.Equal("verdict",Assert.Single(upgraded.Snapshots[key].AppliedVerdicts));
        Assert.Equal("unchanged",upgraded.Receipts["receipt"]);Assert.Contains(id,upgraded.Winners);
        Assert.False(upgraded.Scores.ContainsKey(oldKey));Assert.False(upgraded.Finals.ContainsKey(oldKey));Assert.False(upgraded.Snapshots.ContainsKey(oldKey));
        Assert.Equal(Json.Write(upgraded),Json.Write(new Store(path,"scores").Read()));
        using var c=new SqliteConnection("Data Source="+path);c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT response FROM commands WHERE id='command'";Assert.Equal("saved-response",q.ExecuteScalar());
        q.CommandText="SELECT json FROM outbox WHERE id='event'";Assert.Equal("saved-event",q.ExecuteScalar());
    }

    [Fact]
    public void AmbiguousLegacyKeysRollBackWithoutGuessingOwnerOrLosingData()
    {
        var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");var state=new State();
        state.Tournaments["cup/x"]=Tournament("cup/x","round");state.Tournaments["cup"]=Tournament("cup","x/round");
        state.Scores["cup/x/round"]=new();SeedLegacy(path,state);
        Assert.Equal("ambiguous_legacy_game_key",Assert.Throws<RuleException>(()=>new Store(path,"scores")).Message);
        using var c=new SqliteConnection("Data Source="+path);c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT json FROM state WHERE id=1";Assert.Equal(Json.Write(state),q.ExecuteScalar());
        q.CommandText="SELECT MAX(version) FROM schema_version";Assert.Equal(3L,q.ExecuteScalar());
    }

    private static void SeedLegacy(string path,State state)
    {
        new Store(path,"scores");using var c=new SqliteConnection("Data Source="+path);c.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE state SET json=$json WHERE id=1;DELETE FROM schema_version WHERE version=4;INSERT INTO commands VALUES('command','fingerprint','saved-response');INSERT INTO outbox(id,destination,json) VALUES('event','results','saved-event')";
        q.Parameters.AddWithValue("$json",Json.Write(state));q.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("FormsReserved")]
    [InlineData("FormsReleased")]
    [InlineData("FormsPreparationRejected")]
    public void OpenApiAcceptsSimulatorSagaReplyEnvelope(string type)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory is not null&&!File.Exists(Path.Combine(directory.FullName,"MathTournament.sln")))directory=directory.Parent;
        Assert.NotNull(directory);
        using var document=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName,"docs","openapi.json")));
        var schemas=document.RootElement.GetProperty("components").GetProperty("schemas");
        var schema=schemas.GetProperty(type+"Event");var properties=schema.GetProperty("properties");
        var message=Event.Create(type,"simulator","t","g",new PreparationReply("attempt",type=="FormsPreparationRejected"?"rejected":null));
        var envelope=JsonSerializer.SerializeToElement(message,Json.Options);
        foreach(var required in schema.GetProperty("required").EnumerateArray())Assert.True(envelope.TryGetProperty(required.GetString()!,out _));
        foreach(var field in new[]{"producer","type","version"})
            Assert.Contains(properties.GetProperty(field).GetProperty("enum").EnumerateArray(),allowed=>allowed.GetRawText()==envelope.GetProperty(field).GetRawText());
        var payloadSchema=schemas.GetProperty(properties.GetProperty("data").GetProperty("$ref").GetString()!.Split('/').Last());
        foreach(var required in payloadSchema.GetProperty("required").EnumerateArray())Assert.True(message.Data.TryGetProperty(required.GetString()!,out _));
    }
}
