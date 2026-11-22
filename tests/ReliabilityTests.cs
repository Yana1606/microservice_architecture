using MathTournament;
using Microsoft.Data.Sqlite;
using Xunit;
namespace Tests;

public class ReliabilityTests
{
    private static string Database() => Path.Combine(Path.GetTempPath(), "math-reliability-" + Guid.NewGuid() + ".db");
    private static Tournament Tournament() => new() { Id="t",Name="T",Starts=DateTimeOffset.UtcNow,Ends=DateTimeOffset.UtcNow.AddDays(1),Jury=["j","s"],Senior="s" };
    [Fact] public void QueueSelectsEveryRecipientEvenBehindHundredsOfFailedMessages()
    {
        var path=Database();var store=new Store(path,"tournament");
        using(var c=new SqliteConnection("Data Source="+path))
        {
            c.Open();
            for(var i=0;i<150;i++) Insert(c,"failed-"+i,"offline");
            Insert(c,"healthy","online");
        }
        var pending=store.Pending(DateTimeOffset.UtcNow);
        Assert.Contains(pending,d=>d.Destination=="online");
        Assert.Equal(25,pending.Count(d=>d.Destination=="offline"));
        Assert.Equal(151,store.PendingCount());
    }
    [Fact] public async Task SlowRecipientDoesNotDelayHealthyRecipientOrDeadlineTransaction()
    {
        var path=Database();var store=new Store(path,"tournament");
        store.Update((s,e)=>e.Create(s,Tournament(),"o"));
        using(var c=new SqliteConnection("Data Source="+path)) { c.Open();Insert(c,"healthy","online"); }
        var blocked=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var healthy=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http=new HttpClient(new Handler(async(request,token)=>
        {
            if(request.RequestUri!.Host=="simulator")await blocked.Task.WaitAsync(token);
            else healthy.TrySetResult();
            return new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.Accepted);
        }));
        var dispatcher=new DeliveryDispatcher(store,http,d=>"http://"+d,"test");
        var sending=dispatcher.DispatchAsync();
        await healthy.Task.WaitAsync(TimeSpan.FromSeconds(5));Assert.False(sending.IsCompleted);
        store.Update((s,e)=> { s.Tournaments["t"].Games.Add(new Game {Config=new("g","math",[new("q","s","r",10,0)],new(){["normal"]=1},1,2),Status="Running",SolutionsDue=DateTimeOffset.UtcNow.AddSeconds(-2),AppealsDue=DateTimeOffset.UtcNow.AddSeconds(-1)});e.Tick(s,DateTimeOffset.UtcNow); });
        Assert.True(store.Read().Tournaments["t"].Games[0].AppealsClosed);
        blocked.TrySetResult();await sending;
    }
    [Fact] public void RetryTimeSurvivesRestartAndDoesNotLoseMessage()
    {
        var path=Database();var store=new Store(path,"tournament");store.Update((s,e)=>e.Create(s,Tournament(),"o"));
        var now=DateTimeOffset.UtcNow;var delivery=store.Pending()[0];store.Delivered(delivery,"offline",now);
        var restarted=new Store(path,"tournament");
        Assert.Empty(restarted.Pending(now));Assert.Single(restarted.Pending(now.AddSeconds(2)));
        restarted.Delivered(delivery);Assert.Equal(0,restarted.PendingCount());
    }
    [Fact] public void ExistingVersionOneDatabaseMigratesWithoutLosingOutbox()
    {
        var path=Database();
        using(var c=new SqliteConnection("Data Source="+path)){c.Open();using var command=c.CreateCommand();command.CommandText=Store.Migration;command.ExecuteNonQuery();Insert(c,"old","online");}
        var upgraded=new Store(path,"tournament");Assert.Single(upgraded.Pending(DateTimeOffset.UtcNow));
        Assert.Single(new Store(path,"tournament").Pending());
    }
    [Theory][InlineData("jury")][InlineData("id")][InlineData("senior")]
    public void NullTournamentFieldsProduceDomainError(string field)
    {
        var t=Tournament();if(field=="jury")t.Jury=null!;if(field=="id")t.Id=null!;if(field=="senior")t.Senior=null!;
        var error=Assert.Throws<RuleException>(()=>new Engine("tournament").Create(new State(),t,"o"));Assert.Equal(400,error.Status);
    }
    [Theory][InlineData("tasks")][InlineData("task")][InlineData("modifiers")]
    public void NullGameFieldsProduceDomainError(string field)
    {
        var g=new GameConfig("g","math",[new("q","s","r",10,0)],new(){["normal"]=1},1,2);
        if(field=="tasks")g=g with {Tasks=null!};if(field=="task")g=g with {Tasks=[null!]};if(field=="modifiers")g=g with {Modifiers=null!};
        Assert.Equal(400,Assert.Throws<RuleException>(()=>Engine.Validate(g)).Status);
    }
    [Fact] public void NullTeamMembersAndEventPayloadsAreRejected()
    {
        Assert.Equal(400,Assert.Throws<RuleException>(()=>Validation.Team(new("t","t","c",null!))).Status);
        var e=Event.Create("VerdictRecorded","checking","t","g",(Verdict?)null);
        Assert.Equal(400,Assert.Throws<RuleException>(()=>new Engine("scores").Receive(new State(),e)).Status);
        var seal=Event.Create("ChecksSealed","checking","t","g",new CheckSeal(null!));
        Assert.Equal(400,Assert.Throws<RuleException>(()=>new Engine("scores").Receive(new State(),seal)).Status);
    }
    private static void Insert(SqliteConnection c,string id,string destination)
    {
        using var command=c.CreateCommand();command.CommandText="INSERT INTO outbox(id,destination,json) VALUES($id,$dest,$json)";
        command.Parameters.AddWithValue("$id",id);command.Parameters.AddWithValue("$dest",destination);
        command.Parameters.AddWithValue("$json",Json.Write(Event.Create("Test","tournament","t",null,new{})));command.ExecuteNonQuery();
    }
    [Fact] public void SemanticRepeatWithNewEventIdDoesNotBlockSealedFinalization()
    {
        var t=Tournament();t.Teams=[new("a","A","a",["a","a1","a2","a3"]),new("b","B","b",["b","b1","b2","b3"])];
        var game=new Game{Config=new("g","math",[new("q","s","r",10,0)],new(){["normal"]=1},1,2)};t.Games=[game];
        var s=new State();var engine=new Engine("scores");engine.Receive(s,Event.Create("TournamentStarted","tournament","t",null,t));
        var running=new Game{Config=game.Config,Status="Running",SolutionsDue=DateTimeOffset.UtcNow,AppealsDue=DateTimeOffset.UtcNow.AddSeconds(1)};
        engine.Receive(s,Event.Create("GameStarted","tournament","t","g",running));
        var binding=Event.Create("SolutionBound","team","t","g",new Binding("anon","a","q","normal"));engine.Receive(s,binding);
        var v=Event.Create("VerdictRecorded","checking","t","g",new Verdict("anon",1,"Correct","j","Jury"));engine.Receive(s,v);
        engine.Receive(s,v with {Id="duplicate"});
        engine.Receive(s,Event.Create("SolutionsClosed","tournament","t","g",new{}));engine.Receive(s,Event.Create("AppealsClosed","tournament","t","g",new{}));
        engine.Receive(s,Event.Create("TeamSealed","team","t","g",new TeamSeal(["anon"])));engine.Receive(s,Event.Create("ChecksSealed","checking","t","g",new CheckSeal([v.Id])));
        Assert.True(s.Scores["t/g"].Ready);engine.Receive(s,v with {Id="another-duplicate"});engine.Receive(s,binding with{Id="binding-repeat"});
        Assert.Empty(s.Quarantine);Assert.Single(engine.Events,e=>e.Type=="ScoresReady");
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)=>send(request,token); }
}
