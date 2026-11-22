using System.Text.Json;
using MathTournament;
using Xunit;
namespace Tests;
public class DeterminismTests
{
    static readonly DateTimeOffset At=DateTimeOffset.Parse("2026-11-22T12:00:00Z");
    static Event Fact<T>(string id,string type,string producer,string? game,T body)=>new(id,1,type,producer,"t",game,At,"t",null,JsonSerializer.SerializeToElement(body,Json.Options));
    [Fact]public void ThirtyTwoDeliveryOrdersHaveIdenticalFinalBusinessProjection()
    {
        string? baseline=null;
        for(int seed=0;seed<32;seed++)
        {
            var t=new Tournament{Id="t",Name="T",Starts=At,Ends=At.AddDays(1),Jury=["j","s"],Senior="s",Teams=[new("a","A","a",["a","a1","a2","a3"]),new("b","B","b",["b","b1","b2","b3"])]};
            t.Games=new[]{"g1","g2"}.Select(id=>new Game{Config=new(id,"math",[new(id+"q","s","r",10,0)],new(){["normal"]=1},5,8)}).ToList();
            var started=Fact("t-start","TournamentStarted","tournament",null,t);var facts=new List<Event>{started};
            foreach(var game in t.Games)
            {
                var id=game.Config.Id;facts.Add(Fact(id+"start","GameStarted","tournament",id,new Game{Config=game.Config,Status="Running",SolutionsDue=At.AddSeconds(5),AppealsDue=At.AddSeconds(8)}));
                foreach(var team in new[]{"a","b"})
                {
                    facts.Add(Fact(id+team+"bind","SolutionBound","team",id,new Binding(id+team,team,id+"q","normal")));
                    facts.Add(Fact(id+team+"verdict","VerdictRecorded","checking",id,new Verdict(id+team,1,team=="a"?"Correct":"Incorrect","j","Jury")));
                }
                facts.Add(Fact(id+"teamseal","TeamSealed","team",id,new TeamSeal([id+"a",id+"b"])));
                facts.Add(Fact(id+"checkseal","ChecksSealed","checking",id,new CheckSeal([id+"averdict",id+"bverdict"])));
                facts.Add(Fact(id+"close","SolutionsClosed","tournament",id,new{}));facts.Add(Fact(id+"appealclose","AppealsClosed","tournament",id,new{}));
            }
            var random=new Random(seed);var input=facts.Concat(facts.Where(x=>x.Type=="VerdictRecorded")).ToArray();random.Shuffle(input);
            var scores=new State();var engine=new Engine("scores",new FixedClock(At));foreach(var e in input)engine.Receive(scores,e);
            Assert.Empty(scores.Pending);Assert.Empty(scores.Quarantine);
            var resultInput=facts.Where(e=>e.Producer=="tournament").Concat(engine.Events.Where(e=>e.Type=="ScoresReady").Select(e=>Fact(e.GameId+"ready",e.Type,e.Producer,e.GameId,e.Body<ScoreSnapshot>()))).ToArray();random.Shuffle(resultInput);
            var results=new State();var resultEngine=new Engine("results",new FixedClock(At));foreach(var e in resultInput)resultEngine.Receive(results,e);
            Assert.Equal(2,results.Finals.Count);
            var winner=resultEngine.Events.Single(e=>e.Type=="WinnerDetermined");engine.Receive(scores,Fact("winner",winner.Type,"results",null,winner.Body<List<Standing>>()));Assert.Empty(scores.Winners);
            var finals=resultEngine.Events.Where(e=>e.Type=="GameFinalized").ToArray();random.Shuffle(finals);
            foreach(var e in finals){var canonical=Fact(e.GameId+"final",e.Type,"results",e.GameId,e.Body<List<Standing>>());engine.Receive(scores,canonical);engine.Receive(scores,canonical);}
            Assert.Contains("t",scores.Winners);Assert.Empty(scores.Pending);
            var projection=Json.Write(PublicTable.Build(scores,"t"));baseline??=projection;Assert.Equal(baseline,projection);
            Assert.Equal(new[]{200m,196m},winner.Body<List<Standing>>().Select(x=>x.Score));
        }
    }
}
