namespace DotaHelper.Models;

/// <summary>
/// Observed match counts on exact positions 1..5 from STRATZ heroStats.stats.
/// This is not a Valve role tag, a lane assignment or a hardcoded hero list.
/// </summary>
public sealed record PositionSample(int HeroId, int Position, int Games, int Wins);

public static class PositionEvidence
{
    // Conservative initial thresholds. Tune with historical out-of-sample tests.
    public const int MinGames = 30;
    public const double MinShare = 0.05;

    public static bool IsEligible(
        IReadOnlyDictionary<int, IReadOnlyList<PositionSample>> data,
        int heroId, int position)
    {
        if (!data.TryGetValue(heroId, out var rows)) return false;
        var total = rows.Where(x => x.Games > 0).Sum(x => (long)x.Games);
        var current = rows.FirstOrDefault(x => x.Position == position);
        return current is { Games: >= MinGames } &&
               current.Wins >= 0 && current.Wins <= current.Games &&
               total > 0 && current.Games / (double)total >= MinShare;
    }

    public static PositionSample? Find(
        IReadOnlyDictionary<int, IReadOnlyList<PositionSample>> data,
        int heroId, int position) =>
        data.TryGetValue(heroId, out var rows)
            ? rows.FirstOrDefault(x => x.Position == position)
            : null;

    public static IReadOnlyDictionary<int, IReadOnlyList<PositionSample>> Index(
        IEnumerable<PositionSample> rows) =>
        rows.Where(x => x.HeroId > 0 && x.Position is >= 1 and <= 5 &&
                        x.Games > 0 && x.Wins >= 0 && x.Wins <= x.Games)
            .GroupBy(x => x.HeroId)
            .ToDictionary(g => g.Key,
                g => (IReadOnlyList<PositionSample>)g.GroupBy(x => x.Position)
                    .Select(p => new PositionSample(g.Key, p.Key,
                        p.Sum(x => x.Games), p.Sum(x => x.Wins)))
                    .ToArray());
}
