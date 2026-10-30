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
        HeroPoolMode pool = HeroPoolMode.Common,
        bool professionalMeta = false)
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
        (long Picks, long Wins) ReadStats(HeroStats h) =>
            professionalMeta ? h.InProMatches() : h.InBracket(rankBracket);

        var heroPicks = heroes.ToDictionary(h => h.Id, h =>
            stats.TryGetValue(h.Id, out var data) ? Math.Max(0L, ReadStats(data).Picks) : 0L);
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
            // Broad Carry/Support/Durable/Disabler tags are not observed positions.
            // Enforce the same eligibility gate for positions 1, 2, 3, 4 and 5.
            if (pool != HeroPoolMode.Experimental &&
                !PositionProfile.IsStandard(hero, position)) return false;
            if (pool == HeroPoolMode.Favorites) return favorites.Contains(hero.Id);
            if (pool == HeroPoolMode.Common && !favorites.Contains(hero.Id) && IsRare(hero.Id)) return false;
            return true;
        });

        var ranked = new List<Recommendation>();
        foreach (var hero in candidates)
        {
            var counterDetails = new List<(string Name, double Rate, double Weight)>();
            double weightedEdge = 0;
            double possibleWeight = 0;
            int measured = 0;
            int games = 0;

            foreach (var enemy in pickedEnemies)
            {
                var enemyId = enemy.SelectedHero!.Id;
                // All five enemies matter. Known cores 1/2/3 have the highest
                // strategic impact. Lane opponent is a further modest bonus.
                // Unknown enemy positions are weighted between core and support.
                var weight = enemy.EnemyPosition switch
                {
                    1 or 2 or 3 => 1.7,
                    4 or 5 => 1.0,
                    _ => 1.2
                };
                if (enemy.IsLaneOpponent) weight *= 1.25;
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
                counterDetails.Add((enemy.SelectedHero!.Name, candidateRate, weight));
                measured++;
                games += matchup.GamesPlayed;
            }

            // Missing matchups contribute exactly neutral, never a fabricated edge.
            // Matchups are not filtered to the currently running Dota patch.
            var matchupAdjustment = possibleWeight > 0
                ? Math.Clamp(weightedEdge / possibleWeight * 70.0, -17.0, 17.0)
                : 0;

            var isStandard = PositionProfile.IsStandard(hero, position);
            // Same position-confidence prior across all five roles.
            // This is a manual eligibility prior, NOT data on role-specific wins.
            var roleFit = isStandard ? .82 : .12;
            var favorite = favorites.Contains(hero.Id);
            double metaAdjustment = 0;
            double popularityAdjustment = 0;
            var sourceName = professionalMeta ? "Профи" : "По рангу";
            string metaLabel = $"{sourceName}: WR (все роли) — нет данных";
            string popularityLabel = $"{sourceName}: популярность — нет данных";
            var picks = heroPicks.GetValueOrDefault(hero.Id);
            bool rare = IsRare(hero.Id);

            if (stats.TryGetValue(hero.Id, out var record))
            {
                var (p, w) = ReadStats(record);
                if (p >= 100 && w >= 0 && w <= p)
                {
                    // Beta(100,100) smoothing; scale is intentionally modest.
                    var winRate = (w + 100.0) / (p + 200.0);
                    metaAdjustment = Math.Clamp((winRate - .5) * 40.0, -5.0, 5.0);
                    metaLabel = $"{sourceName}: WR (все роли) {100.0 * w / p:F1}% ({p:N0} пиков)";
                }

                if (p > 0 && totalPicks > 0)
                {
                    var pickRate = 1000.0 * p / totalPicks;
                    popularityLabel = $"{sourceName}: частота выбора (все роли) ≈{pickRate:F1}% матчей";
                }

                if (p > 0 && medianPicks > 0)
                    popularityAdjustment = Math.Clamp(Math.Log2(p / medianPicks) * 2.0, -4.0, 4.0);
            }

            // Rule-of-thumb role archetypes are NOT verified position assignments.
            var roleAdjustment = (roleFit - .5) * 35.0;
            var score = Math.Clamp(50 + roleAdjustment + matchupAdjustment
                + metaAdjustment + popularityAdjustment + (favorite ? 4 : 0), 0, 100);

            string roleLabel = isStandard
                ? $"Позиция {position}: предварительно подходит (ручной пул, без статистики по патчу)"
                : $"НЕСТАНДАРТНЫЙ пик на позицию {position}: данных о пригодности нет";
            string evidence = pickedEnemies.Count == 0
                ? "Матчапы: враги не выбраны"
                : $"Матчапы: {measured}/{pickedEnemies.Count} противников";
            // Show exactly how a candidate got its heuristic score. No win-rate claims.
            string contributions = $"Вклад в балл: роль {roleAdjustment:+0.0;-0.0;0.0}; " +
                $"противники {matchupAdjustment:+0.0;-0.0;0.0}; " +
                $"общий WR {metaAdjustment:+0.0;-0.0;0.0}; " +
                $"популярность {popularityAdjustment:+0.0;-0.0;0.0}; " +
                $"избранное {(favorite ? "+4.0" : "0.0")}.";
            string details = $"{roleLabel}. Контрпики: данные OpenDota о лигах, НЕ выбранный рейтинг. " +
                "В расчёте участвуют все враги: керри/мид/оффлейн важнее, соперники по линии получают бонус. " +
                "Это общие матчапы, а не показатели линии. Синергии и патч пока не учтены.";
            var counterSummary = counterDetails.Count == 0
                ? "Контрпики: достоверных результатов по выбранным противникам нет."
                : "Контрпики по всей команде (вклад в балл): " +
                  string.Join("; ", counterDetails.Select(x =>
                      $"{x.Name} {((x.Rate - .5) * x.Weight / possibleWeight * 70):+0.0;-0.0;0.0}"));
            ranked.Add(new Recommendation(hero, score, measured, pickedEnemies.Count,
                games, favorite, details)
            {
                MetaLabel = metaLabel,
                PopularityLabel = popularityLabel,
                EvidenceLabel = evidence,
                ScoreBreakdown = contributions,
                CounterpickSummary = counterSummary,
                IsRare = rare,
                IsNonStandard = !isStandard,
                PositionLabel = isStandard
                    ? $"✓ Базовый пул позиции {position}"
                    : $"⚠ Нестандартный герой для позиции {position}"
            });
        }

        // Do not silently discard eligible heroes from the recommendations.
        return ranked.OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.OpponentsMeasured)
            .ThenByDescending(r => r.SampleGames)
            .ThenBy(r => r.Hero.Name)
            .Select((r, index) => r with { PositionInRanking = index + 1 })
            .ToList();
    }

}
