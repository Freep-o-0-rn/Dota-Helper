using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Transparent prototype ranking: role archetypes + Bayesian-smoothed matchups.
/// Scores are NOT calibrated win probabilities or current-patch lane statistics.
/// </summary>
public sealed class RecommendationEngine
{
    public IReadOnlyList<Recommendation> Rank(
        IReadOnlyList<Hero> heroes,
        IReadOnlyList<DraftSlot> allies,
        IReadOnlyList<DraftSlot> enemies,
        int position,
        IReadOnlySet<int> favorites,
        IReadOnlyDictionary<int, IReadOnlyList<Matchup>> enemyMatchups)
    {
        var alreadyPicked = allies.Concat(enemies)
            .Select(slot => slot.SelectedHero?.Id)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .ToHashSet();

        var pickedEnemies = enemies.Where(slot => slot.SelectedHero is not null).ToList();
        var ranked = new List<Recommendation>();

        foreach (var hero in heroes)
        {
            if (alreadyPicked.Contains(hero.Id)) continue;

            double weightedEdge = 0;
            double totalWeight = 0;
            int measured = 0;
            int games = 0;

            foreach (var enemy in pickedEnemies)
            {
                var enemyId = enemy.SelectedHero!.Id;
                if (!enemyMatchups.TryGetValue(enemyId, out var matchups)) continue;
                var matchup = matchups.FirstOrDefault(m => m.HeroId == hero.Id);

                // Very small matchup samples are not useful as evidence.
                if (matchup is null || matchup.GamesPlayed < 10 ||
                    matchup.Wins < 0 || matchup.Wins > matchup.GamesPlayed) continue;

                // We requested the ENEMY hero; reverse that hero's wins.
                // Beta(25,25) prior shrinks small samples toward 50%.
                double candidateRate = (matchup.GamesPlayed - matchup.Wins + 25.0)
                                       / (matchup.GamesPlayed + 50.0);
                double weight = enemy.IsLaneOpponent ? 2.0 : 1.0;
                weightedEdge += (candidateRate - 0.5) * weight;
                totalWeight += weight;
                measured++;
                games += matchup.GamesPlayed;
            }

            var roleFit = RoleFit(hero, position);
            var matchupAdjustment = totalWeight > 0 ? (weightedEdge / totalWeight) * 60.0 : 0.0;
            var favorite = favorites.Contains(hero.Id);

            // A ranking score, NOT percentage chance to win.
            var score = Math.Clamp(
                50 + (roleFit - 0.5) * 32 + matchupAdjustment + (favorite ? 5 : 0),
                0, 100);

            var roleLabel = roleFit >= 0.7 ? "Архетип героя подходит выбранной роли"
                : roleFit >= 0.5 ? "Архетип частично подходит роли"
                : "Соответствие роли требует проверки";
            var matchupLabel = pickedEnemies.Count == 0
                ? "Враги ещё не выбраны."
                : measured == 0
                    ? "Проверенных матчапов пока нет."
                    : $"Матчапы против {measured} из {pickedEnemies.Count} врагов; суммарная выборка {games} наблюдений.";

            var explanation = $"{roleLabel}. {matchupLabel} " +
                              "Синергия союзников в версии 0.1 пока не оценивается.";

            ranked.Add(new Recommendation(hero, score, measured, pickedEnemies.Count,
                games, favorite, explanation));
        }

        return ranked.OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.OpponentsMeasured)
            .ThenBy(r => r.Hero.Name)
            .Take(40)
            .ToList();
    }

    // OpenDota 'roles' are broad archetypes, not actual lane positions.
    private static double RoleFit(Hero hero, int position)
    {
        var tags = (hero.Roles ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(string tag) => tags.Contains(tag);

        return position switch
        {
            1 => Has("Carry") ? 0.83 : 0.25,
            2 => Has("Nuker") ? 0.78 : Has("Carry") ? 0.66 : 0.30,
            3 => Has("Durable") || Has("Initiator") ? 0.80 :
                 Has("Disabler") ? 0.60 : 0.28,
            4 => Has("Support") || Has("Disabler") ? 0.75 :
                 Has("Initiator") ? 0.64 : 0.28,
            5 => Has("Support") ? 0.84 : Has("Disabler") ? 0.55 : 0.22,
            _ => 0.5
        };
    }
}
