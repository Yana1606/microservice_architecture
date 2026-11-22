using MathTournament;
using MathTournament.TwoPhaseLab;
using Xunit;
namespace Tests;
public class TwoPhaseTests
{
    private static (string Root,Participant A,Participant B,Coordinator C) Setup()
    {var root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString());return(root,new("a",Path.Combine(root,"a.db")),new("b",Path.Combine(root,"b.db")),new(Path.Combine(root,"c.db")));}
    [Fact]public void CommitIsDurableAndIdempotentAfterCoordinatorRestart()
    {
        var s=Setup();Assert.Equal("Commit",s.C.Run("tx",s.A,s.B,10,10,true));Assert.Equal("Prepared",s.A.State("tx"));Assert.Equal(100,s.A.Balance);
        var restarted=new Coordinator(Path.Combine(s.Root,"c.db"));restarted.Run("tx",s.A,s.B,10,10);restarted.Run("tx",s.A,s.B,10,10);
        Assert.Equal(90,s.A.Balance);Assert.Equal(90,s.B.Balance);Assert.Equal("Committed",s.B.State("tx"));
    }
    [Fact]public void NoVoteAbortsAndReleasesFirstParticipantsReservation()
    {
        var s=Setup();Assert.Equal("Abort",s.C.Run("tx",s.A,s.B,10,110));Assert.Equal("Aborted",s.A.State("tx"));Assert.Equal(100,s.A.Balance);
        Assert.True(s.A.Prepare("other",100));
    }
    [Fact]public void PreparedParticipantBlocksAndWaitsWhenDecisionEndpointIsUnavailable()
    {
        var s=Setup();s.A.Prepare("tx",80);s.B.Prepare("tx",10);var restarted=new Participant("a",Path.Combine(s.Root,"a.db"));var channel=new DecisionChannel(s.C){Available=false};
        Assert.Throws<HttpRequestException>(()=>restarted.Recover("tx",channel));Assert.Equal("Prepared",restarted.State("tx"));Assert.False(restarted.Prepare("other",30));
        channel.Available=true;Assert.False(restarted.Recover("tx",channel));s.C.Record("tx","Abort");Assert.True(restarted.Recover("tx",channel));Assert.Equal(100,restarted.Balance);
        Assert.Throws<RuleException>(()=>s.C.Record("tx","Commit"));
    }
}
