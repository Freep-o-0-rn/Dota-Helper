using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Explainable, conservative ranking. None of its scores are calibrated
/// win probabilities, lane success metrics, or patch-specific forecasts.
/// </summary>
public sealed class RecommendationEngine
{
    public IReadOnlyList<Recommendation> Rank(
        IReadOnlyList<Hero> heroes,
        IReadOnlyList<DraftSlot> allies,
        IReadOnlyList<DraftSlot> enemies,
        int position,
        IReadOnlySet<int> favorites,
        IReadOnlyDictionary<int, IReadOnlyList<Matchup>> enemyMatchups,
        IReadOnlyDictionary<int, HeroStats>? heroStatistics = null,
        int rankBracket = 0,
        HeroPoolMode pool = HeroPoolMode.Common)
    {
        var alreadyPicked = allies.Concat(enemies)
            .Select(slot => slot.SelectedHero?.Id)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        // Duplicate selections must not be counted as independent opponents.
        var pickedEnemies = enemies
            .Where(slot => slot.SelectedHero is not null)
            .GroupBy(slot => slot.SelectedHero!.Id)
            .Select(group => group.FirstOrDefault(x => x.IsLaneOpponent) ?? group.First())
            .ToList();

        var stats = heroStatistics ?? new Dictionary<int, HeroStats>();
        var heroPicks = heroes.ToDictionary(h => h.Id, h =>
            stats.TryGetValue(h.Id, out var data) ? Math.Max(0L, data.InBracket(rankBracket).Picks) : 0L);
        var knownPicks = heroPicks.Values.Where(p => p > 0).OrderBy(p => p).ToArray();
        var totalPicks = knownPicks.Sum(p => (double)p);
        var medianPicks = knownPicks.Length == 0 ? 0.0 : knownPicks[knownPicks.Length / 2];
        var rareCutoff = knownPicks.Length < 10 ? 0L
            : knownPicks[(int)((knownPicks.Length - 1) * 0.25)];
        // Treat tied least-played heroes equally; do not filter at all when
        // distribution is flat (quantile equals median).
        bool IsRare(int heroId) =>
            rareCutoff > 0 && rareCutoff < medianPicks &&
            heroPicks.GetValueOrDefault(heroId) <= rareCutoff;

        // Conditional broad-pool mode: exclude only the lowest quarter by pick
        // count, and only when bracket data is available. Show rare picks in All.
        var candidates = heroes.Where(hero =>
        {
            if (alreadyPicked.Contains(hero.Id)) return false;
            if (pool == HeroPoolMode.Favorites) return favorites.Contains(hero.Id);
            if (pool == HeroPoolMode.Common && !favorites.Contains(hero.Id) && IsRare(hero.Id)) return false;
            return true;
        });

        var ranked = new List<Recommendation>();
        foreach (var hero in candidates)
        {
            double weightedEdge = 0;
            double possibleWeight = 0;
            int measured = 0;
            int games = 0;

            foreach (var enemy in pickedEnemies)
            {
                var enemyId = enemy.SelectedHero!.Id;
                var weight = enemy.IsLaneOpponent ? 2.0 : 1.0;
                possibleWeight += weight;

                if (!enemyMatchups.TryGetValue(enemyId, out var matchups)) continue;
                var matchup = matchups.FirstOrDefault(m => m.HeroId == hero.Id);
                if (matchup is null || matchup.GamesPlayed < 20 ||
                    matchup.Wins < 0 || matchup.Wins > matchup.GamesPlayed) continue;

                // OpenDota /heroes/{enemyId}/matchups reports ENEMY wins.
                // Convert into candidate's rate, then shrink toward 50%.
                double candidateRate = (matchup.GamesPlayed - matchup.Wins + 50.0)
                                     / (matchup.GamesPlayed + 100.0);
                weightedEdge += (candidateRate - 0.5) * weight;
                measured++;
                games += matchup.GamesPlayed;
            }

            // Missing matchups contribute exactly neutral, never a fabricated edge.
            // Matchups are not filtered to the currently running Dota patch.
            var matchupAdjustment = possibleWeight > 0
                ? Math.Clamp(weightedEdge / possibleWeight * 45.0, -10.0, 10.0)
                : 0;

            var roleFit = RoleFit(hero, position);
            var favorite = favorites.Contains(hero.Id);
            double metaAdjustment = 0;
            double popularityAdjustment = 0;
            string metaLabel = "Общий WR: недостаточно данных";
            string popularityLabel = "Пики: недостаточно данных";
            var picks = heroPicks.GetValueOrDefault(hero.Id);
            bool rare = IsRare(hero.Id);

            if (stats.TryGetValue(hero.Id, out var record))
            {
                var (p, w) = record.InBracket(rankBracket);
                if (p >= 100 && w >= 0 && w <= p)
                {
                    // Beta(100,100) smoothing; scale is intentionally modest.
                    var winRate = (w + 100.0) / (p + 200.0);
                    metaAdjustment = Math.Clamp((winRate - .5) * 40.0, -5.0, 5.0);
                    metaLabel = $"Общий WR: {100.0 * w / p:F1}% ({p:N0} пиков)";
                }

                if (p > 0 && totalPicks > 0)
                {
                    var pickRate = 1000.0 * p / totalPicks;
                    popularityLabel = $"Частота выбора: ≈{pickRate:F1}% матчей";
                }

                if (p > 0 && medianPicks > 0)
                    popularityAdjustment = Math.Clamp(Math.Log2(p / medianPicks) * 2.0, -4.0, 4.0);
            }

            // Rule-of-thumb role archetypes are NOT verified position assignments.
            var roleAdjustment = (roleFit - .5) * 35.0;
            var score = Math.Clamp(50 + roleAdjustment + matchupAdjustment
                + metaAdjustment + popularityAdjustment + (favorite ? 4 : 0), 0, 100);

            string roleLabel = roleFit >= .7
                ? "Архетип подходит роли"
                : roleFit >= .5
                    ? "Архетип частично подходит роли"
                    : "Позиция не подтверждена";
            string evidence = pickedEnemies.Count == 0
                ? "Матчапы: враги не выбраны"
                : $"Матчапы: {measured}/{pickedEnemies.Count} противников";
            string details = $"{roleLabel}. {evidence}. " +
                $"{metaLabel}. {popularityLabel}. " +
                "Синергии и патч-специфическая статистика пока не учтены.";
            ranked.Add(new Recommendation(hero, score, measured, pickedEnemies.Count,
                games, favorite, details)
            {
                MetaLabel = metaLabel,
                PopularityLabel = popularityLabel,
                EvidenceLabel = evidence,
                IsRare = rare
            });
        }

        return ranked.OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.OpponentsMeasured)
            .ThenByDescending(r => r.SampleGames)
            .ThenBy(r => r.Hero.Name)
            .Take(40)
            .ToList();
    }

    private static double RoleFit(Hero hero, int position)
    {
        var tags = (hero.Roles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(string tag) => tags.Contains(tag);

        return position switch
        {
            1 => Has("Carry") ? .83 : .25,
            2 => Has("Nuker") ? .78 : Has("Carry") ? .66 : .30,
            3 => Has("Durable") || Has("Initiator") ? .80 :
                 Has("Disabler") ? .60 : .28,
            4 => Has("Support") || Has("Disabler") ? .75 :
                 Has("Initiator") ? .64 : .28,
            5 => Has("Support") ? .84 : Has("Disabler") ? .55 : .22,
            _ => .5
        };
    }
}
