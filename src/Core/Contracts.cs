using System.Text.Json;
namespace MathTournament;

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string value) => JsonSerializer.Deserialize<T>(value, Options)!;
}
public sealed class RuleException(string code, int status = 409) : Exception(code) { public int Status { get; } = status; }
public static class Require
{
    public static void That(bool condition, string code, int status = 409) { if (!condition) throw new RuleException(code, status); }
}
public record TaskRule(string Id, string Statement, string Reference, decimal Correct, decimal Incorrect);
public record GameConfig(string Id, string Type, List<TaskRule> Tasks, Dictionary<string, decimal> Modifiers,
    int SolutionSeconds, int AppealSeconds);
public record Team(string Id, string Name, string Captain, List<string> Members);
public sealed class Game
{
    public GameConfig Config { get; set; } = null!;
    public string Status { get; set; } = "Configured";
    public DateTimeOffset? SolutionsDue { get; set; }
    public DateTimeOffset? AppealsDue { get; set; }
    public bool SolutionsClosed { get; set; }
    public bool AppealsClosed { get; set; }
}
public sealed class Tournament
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Organizer { get; set; } = "";
    public DateTimeOffset Starts { get; set; }
    public DateTimeOffset Ends { get; set; }
    public int TeamSize { get; set; } = 4;
    public List<string> Jury { get; set; } = [];
    public string Senior { get; set; } = "";
    public string Regulation { get; set; } = "";
    public string TiePolicy { get; set; } = "SharedCompetition";
    public string Status { get; set; } = "Draft";
    public long Revision { get; set; }
    public List<Game> Games { get; set; } = [];
    public List<Team> Teams { get; set; } = [];
}
// Jury-facing messages deliberately have no team, rank, or form ownership.
public record AnonymousReview(string AnonymousId, string TaskId, string PhotoUrl, string Statement,
    string Reference, string AssignedJury, bool Appeal, string? Reason);
public record Binding(string AnonymousId, string TeamId, string TaskId, string FormType);
public record Verdict(string AnonymousId, int Revision, string Outcome, string JuryId, string Authority);
public record TeamSeal(List<string> AnonymousIds);
public record CheckSeal(List<string> VerdictEventIds);
public record Standing(string TeamId, decimal Score, int Place, int Points);
public record ScoreSnapshot(List<Standing> Rows, List<string> AppliedVerdicts);
public record Event(string Id, int Version, string Type, string Producer, string TournamentId,
    string? GameId, DateTimeOffset OccurredAt, string CorrelationId, string? CausationId, JsonElement Data)
{
    public T Body<T>() => Data.Deserialize<T>(Json.Options)!;
    public static Event Create<T>(string type, string producer, string tournament, string? game, T body,
        string? cause = null) => new(Guid.NewGuid().ToString("N"), 1, type, producer, tournament, game,
        DateTimeOffset.UtcNow, tournament, cause, JsonSerializer.SerializeToElement(body, Json.Options));
}
public sealed class Cell
{
    public string Status { get; set; } = "NotSubmitted";
    public decimal Points { get; set; }
    public int Revision { get; set; }
    public string? AnonymousId { get; set; }
}
public sealed class ScoreGame
{
    public Dictionary<string, Dictionary<string, Cell>> Cells { get; set; } = [];
    public Dictionary<string, Binding> Bindings { get; set; } = [];
    public Dictionary<string, Verdict> Verdicts { get; set; } = [];
    public HashSet<string> Applied { get; set; } = [];
    public List<string>? TeamSeal { get; set; }
    public List<string>? CheckSeal { get; set; }
    public bool Ready { get; set; }
}
public sealed class State
{
    public Dictionary<string, Tournament> Tournaments { get; set; } = [];
    public Dictionary<string, ScoreGame> Scores { get; set; } = [];
    public Dictionary<string, List<Standing>> Finals { get; set; } = [];
    public Dictionary<string, ScoreSnapshot> Snapshots { get; set; } = [];
    public HashSet<string> Winners { get; set; } = [];
    public Dictionary<string, Event> Pending { get; set; } = [];
    public HashSet<string> AppliedEvents { get; set; } = [];
    public Dictionary<string, string> Receipts { get; set; } = [];
    public Dictionary<string, string> Quarantine { get; set; } = [];
    public static string Key(string tournament, string game) => tournament + "/" + game;
}
public record Delivery(Event Event, string Destination);
