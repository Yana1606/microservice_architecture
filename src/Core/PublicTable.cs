namespace MathTournament;
public static class PublicTable
{
public static object Build(State s, string id)
{
    if (!s.Tournaments.TryGetValue(id, out var t)) return new { status = "WaitingForTournament", rows = Array.Empty<object>() };
    var ranked = Engine.Rank(t.Teams.Select(team => (team.Id, (decimal)t.Games.Sum(g => s.Finals.GetValueOrDefault(State.Key(id, g.Config.Id))?.SingleOrDefault(x => x.TeamId == team.Id)?.Points ?? 0))));
    return new { t.Id, complete = s.Winners.Contains(id), games = t.Games.Select(g => new { g.Config.Id, tasks = g.Config.Tasks.Select(x => x.Id) }),
        rows = ranked.Select(rank => new { id = rank.TeamId, name = t.Teams.Single(x => x.Id == rank.TeamId).Name, rank.Place, total = rank.Score,
            games = t.Games.Select(g => new { id = g.Config.Id,
                score = s.Scores.GetValueOrDefault(State.Key(id, g.Config.Id))?.Cells[rank.TeamId].Values.Sum(x => x.Points),
                place = s.Scores.TryGetValue(State.Key(id, g.Config.Id), out var live)
                    ? 1 + live.Cells.Values.Count(cells => cells.Values.Sum(x => x.Points) > live.Cells[rank.TeamId].Values.Sum(x => x.Points)) : (int?)null,
                cells = s.Scores.GetValueOrDefault(State.Key(id, g.Config.Id))?.Cells[rank.TeamId]
                    .ToDictionary(x => x.Key, x => new { x.Value.Status, x.Value.Points }),
                final = s.Finals.GetValueOrDefault(State.Key(id, g.Config.Id))?.SingleOrDefault(x => x.TeamId == rank.TeamId) }) }) };
}
}
