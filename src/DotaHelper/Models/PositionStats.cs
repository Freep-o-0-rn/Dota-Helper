namespace DotaHelper.Models;

/// <summary>
/// Observed match counts on exact positions 1..5 from STRATZ heroStats.stats.
/// This is not a Valve role tag, a lane assignment or a hardcoded hero list.
/// </summary>
public sealed record PositionSample(int HeroId, int Position, int Games, int Wins);

/// <summary>
/// Frequency of a hero playing a given position, NOT probability of winning.
/// The category is contextual to that hero, not a popularity ranking
/// among all midlaners or other heroes.
/// </summary>
public sealed record RoleUsageEvidence(
    int Games, long AllGames, double Share, bool Eligible,
    string Category, double ScoreAdjustment)
{
    public string ShortLabel(int position) => AllGames == 0
        ? $"Позиция {position}: данных нет"
        : $"Роль {position}: {Category.ToLowerInvariant()} · {Share:P0} · {Games:N0} игр";
}

public static class PositionEvidence
{
    // Conservative initial thresholds. Tune with historical out-of-sample tests.
    public const int MinGames = 30;
    public const double MinShare = 0.05;

    public static RoleUsageEvidence Evaluate(
        IReadOnlyDictionary<int, IReadOnlyList<PositionSample>> data,
        int heroId, int position)
    {
        if (!data.TryGetValue(heroId, out var rows))
            return new(0, 0, 0, false, "Нет данных", -8);
        var total = rows.Where(x => x.Games > 0).Sum(x => (long)x.Games);
        var item = rows.FirstOrDefault(x => x.Position == position);
        var games = item?.Games ?? 0;
        var share = total > 0 ? games / (double)total : 0;
        var eligible = item is not null && item.Games >= MinGames &&
            item.Wins >= 0 && item.Wins <= item.Games &&
            share >= MinShare;
        if (!eligible)
            return new(games, total, share, false, "Не подтверждена", -8);

        // Role familiarity, deliberately small relative to max +17 counters.
        // A minimum viable sample must not get the same bonus as thousands
        // of games on the role. Share only measures *usage*, not strength.
        var shareFactor = Math.Clamp((share - MinShare) / .40, 0, 1);
        var volumeFactor = Math.Clamp(
            Math.Log(games / (double)MinGames) / Math.Log(1000.0 / MinGames),
            0, 1);
        var contribution = -1.0 + 7.0 * shareFactor + 2.0 * volumeFactor;
        var category = games >= 200 && share >= .25 ? "Частая"
            : games >= 100 && share >= .12 ? "Ситуативная"
            : "Редкая";
        return new(games, total, share, true, category,
            Math.Round(contribution, 2));
    }

    public static bool IsEligible(
        IReadOnlyDictionary<int, IReadOnlyList<PositionSample>> data,
        int heroId, int position) => Evaluate(data, heroId, position).Eligible;

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
