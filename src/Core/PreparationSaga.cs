namespace MathTournament;

public record PreparationRequest(string AttemptId, GameConfig Config, List<string> TeamIds);
public record PreparationReply(string AttemptId, string? Reason = null);
public sealed class GamePreparation
{
    public string AttemptId { get; set; } = "";
    public string TournamentId { get; set; } = "";
    public string GameId { get; set; } = "";
    public string Status { get; set; } = "Preparing";
    public string Step { get; set; } = "Scoreboard";
    public string? Failure { get; set; }
    public DateTimeOffset Deadline { get; set; }
    public HashSet<string> Released { get; set; } = [];
}
public sealed class PreparationReservation
{
    public string AttemptId { get; set; } = "";
    public string TournamentId { get; set; } = "";
    public string GameId { get; set; } = "";
    public string Status { get; set; } = "Reserved";
    public string? Payload { get; set; }
    public List<string> TeamIds { get; set; } = [];
}
public sealed partial class Engine
{
    private DateTimeOffset Now => (time ?? TimeProvider.System).GetUtcNow();
    private void BeginPreparation(State state, Tournament t, Game game)
    {
        var attempt = (newId ?? (() => Guid.NewGuid().ToString("N")))();
        Require.That(!state.Preparations.ContainsKey(attempt), "attempt_id_exists");
        var saga = new GamePreparation { AttemptId=attempt,TournamentId=t.Id,GameId=game.Config.Id,Deadline=Now.AddSeconds(15) };
        state.Preparations.Add(attempt,saga);
        game.Status="Preparing";game.PreparationAttemptId=attempt;game.PreparationStatus="Preparing";game.PreparationFailure=null;
        Emit("ReserveScoreboard",t,game.Config.Id,new PreparationRequest(attempt,game.Config,t.Teams.Select(x=>x.Id).ToList()));
    }
    private void CancelPreparation(Tournament t, Game game, GamePreparation saga, string reason)
    {
        if(saga.Status!="Preparing")return;
        saga.Status="Compensating";saga.Step="ReleaseBoth";saga.Failure=reason;
        game.Status="Compensating";game.PreparationStatus="Compensating";game.PreparationFailure=reason;t.Revision++;
        // Always compensate both: an acknowledgement can be lost after local reservation committed.
        Emit("ReleaseScoreboard",t,game.Config.Id,new PreparationReply(saga.AttemptId));
        Emit("ReleaseForms",t,game.Config.Id,new PreparationReply(saga.AttemptId));
    }
    private void TickPreparations(State state, DateTimeOffset now)
    {
        if(service!="tournament")return;
        foreach(var saga in state.Preparations.Values.Where(x=>x.Status=="Preparing"&&now>=x.Deadline).ToList())
        {
            var t=state.Tournaments[saga.TournamentId];
            CancelPreparation(t,t.Games.Single(g=>g.Config.Id==saga.GameId),saga,"preparation_timeout");
        }
    }
    private bool IsPreparationEvent(Event e) => e.Type is "ReserveScoreboard" or "ReserveForms" or "ReleaseScoreboard" or "ReleaseForms" or "ScoreboardReserved" or "FormsReserved" or "ScoreboardPreparationRejected" or "FormsPreparationRejected" or "ScoreboardReleased" or "FormsReleased";
    private bool ApplyPreparation(State state, Event e)
    {
        if(e.Type is "ReserveScoreboard" or "ReserveForms" or "ReleaseScoreboard" or "ReleaseForms")
        {
            var forms=service=="simulator";
            var reserved=forms?"FormsReserved":"ScoreboardReserved";
            var rejected=forms?"FormsPreparationRejected":"ScoreboardPreparationRejected";
            var released=forms?"FormsReleased":"ScoreboardReleased";
            var release=e.Type.StartsWith("Release",StringComparison.Ordinal);
            var attempt=release?e.Body<PreparationReply>().AttemptId:e.Body<PreparationRequest>().AttemptId;
            Require.That(Validation.Text(attempt),"invalid_attempt",400);
            state.Reservations.TryGetValue(attempt,out var prior);
            var t=state.Tournaments.GetValueOrDefault(e.TournamentId);
            if(!release&&t is null)return false;
            var shell=t??new Tournament{Id=e.TournamentId};
            if(release)
            {
                if(prior?.Status=="Activated")return true;
                state.Reservations[attempt]=new(){AttemptId=attempt,TournamentId=e.TournamentId,GameId=e.GameId!,Status="Released"};
                Emit(released,shell,e.GameId,new PreparationReply(attempt),e.Id);return true;
            }
            var request=e.Body<PreparationRequest>();Validation.Game(request.Config);
            Require.That(request.Config.Id==e.GameId&&request.TeamIds is not null&&request.TeamIds.ToHashSet().SetEquals(t!.Teams.Select(x=>x.Id)),"invalid_preparation",400);
            if(prior is not null)
            {
                Require.That(prior.TournamentId==e.TournamentId&&prior.GameId==e.GameId,"attempt_payload_conflict");
                if(prior.Status=="Released"||prior.Status=="Rejected")Emit(rejected,shell,e.GameId,new PreparationReply(attempt,"attempt_cancelled"),e.Id);
                else {Require.That(prior.Payload==Json.Write(request),"attempt_payload_conflict");Emit(reserved,shell,e.GameId,new PreparationReply(attempt),e.Id);}
                return true;
            }
            if(forms&&state.RejectNextForms)
            {
                state.RejectNextForms=false;
                state.Reservations[attempt]=new(){AttemptId=attempt,TournamentId=e.TournamentId,GameId=e.GameId!,Status="Rejected"};
                Emit(rejected,shell,e.GameId,new PreparationReply(attempt,"demo_forms_rejected"),e.Id);return true;
            }
            state.Reservations[attempt]=new(){AttemptId=attempt,TournamentId=e.TournamentId,GameId=e.GameId!,Payload=Json.Write(request),TeamIds=request.TeamIds};
            Emit(reserved,shell,e.GameId,new PreparationReply(attempt),e.Id);return true;
        }
        var reply=e.Body<PreparationReply>();Require.That(Validation.Text(reply.AttemptId),"invalid_attempt",400);
        if(!state.Preparations.TryGetValue(reply.AttemptId,out var saga))return true;
        Require.That(saga.TournamentId==e.TournamentId&&saga.GameId==e.GameId,"attempt_payload_conflict");
        var tournament=state.Tournaments[saga.TournamentId];var game=tournament.Games.Single(g=>g.Config.Id==saga.GameId);
        if(saga.Status is "Succeeded" or "Compensated")return true;
        if(saga.Status=="Preparing"&&Now>=saga.Deadline)CancelPreparation(tournament,game,saga,"preparation_timeout");
        if(e.Type.EndsWith("Released",StringComparison.Ordinal))
        {
            if(saga.Status!="Compensating")return true;
            saga.Released.Add(e.Producer);
            if(saga.Released.SetEquals(new[]{"scores","simulator"}))
            {
                saga.Status="Compensated";saga.Step="Finished";game.Status="Configured";game.PreparationStatus="Compensated";tournament.Revision++;
            }
            return true;
        }
        if(saga.Status!="Preparing")return true;
        if(e.Type.EndsWith("PreparationRejected",StringComparison.Ordinal)){CancelPreparation(tournament,game,saga,reply.Reason??"participant_rejected");return true;}
        if(e.Type=="ScoreboardReserved"&&saga.Step=="Scoreboard")
        {
            saga.Step="Forms";
            Emit("ReserveForms",tournament,game.Config.Id,new PreparationRequest(saga.AttemptId,game.Config,tournament.Teams.Select(x=>x.Id).ToList()),e.Id);
        }
        if(e.Type=="FormsReserved"&&saga.Step=="Forms")
        {
            saga.Status="Succeeded";saga.Step="Finished";game.Status="Running";game.PreparationStatus="Succeeded";
            game.SolutionsDue=Now.AddSeconds(game.Config.SolutionSeconds);game.AppealsDue=Now.AddSeconds(game.Config.AppealSeconds);tournament.Revision++;
            Emit("GameStarted",tournament,game.Config.Id,game,e.Id);
        }
        return true;
    }
    private void ActivateReservation(State state, Game game)
    {
        if(game.PreparationAttemptId is not {} attempt)return; // Legacy events remain readable.
        Require.That(state.Reservations.TryGetValue(attempt,out var reservation)&&reservation.Status is "Reserved" or "Activated","reservation_not_ready");
        reservation.Status="Activated";
    }
}
