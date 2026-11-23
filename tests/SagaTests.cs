using MathTournament;
using Xunit;
namespace Tests;
public sealed class FixedClock(DateTimeOffset now):TimeProvider
{
    public DateTimeOffset Current {get;set;}=now;
    public override DateTimeOffset GetUtcNow()=>Current;
}
public class SagaTests
{
    internal sealed class Flow
    {
        public readonly FixedClock Clock=new(DateTimeOffset.Parse("2026-11-22T12:00:00Z"));
        public readonly State T=new(),S=new(),F=new();
        public Engine Coordinator {get;}
        public readonly Engine Scores=new("scores"),Forms=new("simulator");
        int number;
        public Flow()
        {
            Coordinator=new("tournament",Clock,()=>"attempt-"+(++number));
            Coordinator.Create(T,new(){Id="t",Name="Test",Starts=Clock.Current,Ends=Clock.Current.AddDays(1),Jury=["j","s"],Senior="s",Regulation="test"},"o");
            Cmd("configure",new("g","math",[new("q","s","r",10,0)],new(){["normal"]=1},5,8));Cmd("publish");
            foreach(var id in new[]{"a","b"})Cmd("register",team:new(id,id,id,[id,id+"1",id+"2",id+"3"]));Cmd("start");
            var started=Coordinator.Events.Last();Scores.Receive(S,started);Forms.Receive(F,started);
        }
        public Game Game=>T.Tournaments["t"].Games[0];
        public void Cmd(string action,GameConfig? config=null,Team? team=null)=>Coordinator.Command(T,"t",action,team?.Captain??"o",T.Tournaments["t"].Revision,config,team,"g");
        public Event Begin(){Cmd("start-game");return Coordinator.Events.Last();}
        public Event ReserveFirst(){var request=Begin();Scores.Receive(S,request);Coordinator.Receive(T,Scores.Events.Last());return Coordinator.Events.Last();}
        public void ReleaseBoth(bool acknowledge=true)
        {
            Scores.Receive(S,Coordinator.Events.Last(e=>e.Type=="ReleaseScoreboard"));Forms.Receive(F,Coordinator.Events.Last(e=>e.Type=="ReleaseForms"));
            if(acknowledge){Coordinator.Receive(T,Scores.Events.Last());Coordinator.Receive(T,Forms.Events.Last());}
        }
    }
    [Fact]public void ReservesRemainInvisibleUntilSuccessfulStart()
    {
        var f=new Flow();var forms=f.ReserveFirst();Assert.Empty(f.S.Scores);Assert.Equal("Reserved",f.S.Reservations["attempt-1"].Status);Assert.Null(f.Game.SolutionsDue);
        f.Forms.Receive(f.F,forms);f.Coordinator.Receive(f.T,f.Forms.Events.Last());var started=f.Coordinator.Events.Last();
        Assert.Equal("GameStarted",started.Type);Assert.Equal(f.Clock.Current.AddSeconds(5),f.Game.SolutionsDue);
        f.Scores.Receive(f.S,started);f.Forms.Receive(f.F,started);
        Assert.Equal("Activated",f.S.Reservations["attempt-1"].Status);Assert.Equal("Activated",f.F.Reservations["attempt-1"].Status);Assert.Single(f.S.Scores);
    }
    [Fact]public void RejectionCompensatesLocalReservationAndRetryIgnoresLateOldAnswers()
    {
        var f=new Flow();var request=f.ReserveFirst();f.F.RejectNextForms=true;f.Forms.Receive(f.F,request);f.Coordinator.Receive(f.T,f.Forms.Events.Last());
        Assert.Equal("Compensating",f.Game.Status);f.ReleaseBoth(false);Assert.Equal("Released",f.S.Reservations["attempt-1"].Status);Assert.Empty(f.S.Scores);
        Assert.Throws<RuleException>(()=>f.Cmd("start-game"));
        f.Coordinator.Receive(f.T,f.Scores.Events.Last());Assert.Equal("Compensating",f.Game.Status);
        f.Coordinator.Receive(f.T,f.Forms.Events.Last());Assert.Equal("Configured",f.Game.Status);
        var late=Event.Create("FormsReserved","simulator","t","g",new PreparationReply("attempt-1"));
        var next=f.ReserveFirst();f.Coordinator.Receive(f.T,late);Assert.Equal("Preparing",f.Game.Status);Assert.Equal("attempt-2",f.Game.PreparationAttemptId);
        f.Forms.Receive(f.F,request with{Id="late-old-reserve"});Assert.Equal("Released",f.F.Reservations["attempt-1"].Status);
        f.Forms.Receive(f.F,next);f.Coordinator.Receive(f.T,f.Forms.Events.Last());Assert.Equal("Running",f.Game.Status);
        f.Coordinator.Receive(f.T,late with{Id="another-late"});Assert.Single(f.Coordinator.Events,e=>e.Type=="GameStarted");
    }
    [Fact]public void ReleaseBeforeReserveIsDurableTombstoneAndRepeatsAreSafe()
    {
        var f=new Flow();var reserve=f.Begin();var release=Event.Create("ReleaseScoreboard","tournament","t","g",new PreparationReply("attempt-1"));
        f.Scores.Receive(f.S,release);var restored=Json.Read<State>(Json.Write(f.S));
        f.Scores.Receive(restored,reserve);f.Scores.Receive(restored,release with{Id="release-again"});
        Assert.Equal("Released",restored.Reservations["attempt-1"].Status);Assert.Empty(restored.Scores);Assert.Empty(restored.Quarantine);
    }
    [Fact]public void RestartRecoversTimeoutAndDoesNotPublishDuplicateCompensations()
    {
        var f=new Flow();var path=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");var store=new Store(path,"tournament");
        store.Update((s,e)=>{s.Tournaments=f.T.Tournaments;e.Command(s,"t","start-game","o",s.Tournaments["t"].Revision,gameId:"g");});
        var restarted=new Store(path,"tournament");restarted.Update((s,e)=>e.Tick(s,DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal("Compensating",restarted.Read().Tournaments["t"].Games[0].Status);var count=restarted.PendingCount();
        restarted.Update((s,e)=>e.Tick(s,DateTimeOffset.UtcNow.AddMinutes(2)));Assert.Equal(count,restarted.PendingCount());
        Assert.Equal(2,restarted.Pending().Count(d=>d.Event.Type.StartsWith("Release",StringComparison.Ordinal)));
    }
}
