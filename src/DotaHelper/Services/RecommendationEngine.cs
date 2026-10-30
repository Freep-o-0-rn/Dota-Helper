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
        bool professionalMeta = false,
        IReadOnlyDictionary<int, IReadOnlyList<PositionSample>>? observedPositions = null,
        IReadOnlyDictionary<int, string>? matchupSources = null,
        IReadOnlyDictionary<int, IReadOnlyList<Matchup>>? allySynergies = null,
        IReadOnlyDictionary<int, string>? synergySources = null)
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

        var pickedAllies = allies.Where(slot => slot.SelectedHero is not null)
            .GroupBy(slot => slot.SelectedHero!.Id)
            .Select(group => group.First())
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

        // No manual hero whitelist, Valve role archetypes or arbitrary popularity cutoff.
        // The standard pool uses ONLY actual games at the selected position.
        // Without a verified match sample we cannot claim the hero suits this role.
        var observed = observedPositions ??
            new Dictionary<int, IReadOnlyList<PositionSample>>();
        var candidates = heroes.Where(hero =>
            !alreadyPicked.Contains(hero.Id) &&
            (pool == HeroPoolMode.Experimental ||
             PositionEvidence.IsEligible(observed, hero.Id, position)));

        var ranked = new List<Recommendation>();
        foreach (var hero in candidates)
        {
            var counterDetails = new List<(string Name, double Edge, double Weight,
                int Games, string Source)>();
            // STRATZ rank-filtered matchups span all positions. Use the hero's
            // overall winrate in that same bracket as a coarse baseline to
            // avoid mistaking general hero strength for a specific counter.
            double overallStratzBaseline = .5;
            if (observed.TryGetValue(hero.Id, out var observedByPosition))
            {
                var total = observedByPosition.Sum(r => (long)r.Games);
                var wins = observedByPosition.Sum(r => (long)r.Wins);
                if (total >= 100 && wins >= 0 && wins <= total)
                    overallStratzBaseline = (wins + 100.0) / (total + 200.0);
            }
            double weightedEdge = 0;
            double possibleWeight = 0;
            double reliableWeight = 0;
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

                // Both sources report wins of the QUERIED enemy. The observed
                // candidate wins are games - enemy wins. Shrink the matchup
                // toward its own source's baseline, NOT toward a fixed 50%
                // followed by subtracting a different baseline.
                var source = matchupSources?.GetValueOrDefault(enemyId) ?? "OpenDota";
                var baseline = source.StartsWith("STRATZ", StringComparison.Ordinal)
                    ? overallStratzBaseline : .5;
                const double priorGames = 200.0;
                double candidateWins = matchup.GamesPlayed - matchup.Wins;
                double candidateRate = (candidateWins + priorGames * baseline)
                                     / (matchup.GamesPlayed + priorGames);
                var edge = candidateRate - baseline;
                weightedEdge += edge * weight;
                reliableWeight += weight * matchup.GamesPlayed /
                    (matchup.GamesPlayed + priorGames);
                counterDetails.Add((enemy.SelectedHero!.Name, edge, weight,
                    matchup.GamesPlayed, source));
                measured++;
                games += matchup.GamesPlayed;
            }

            // Missing matchups contribute exactly neutral, never a fabricated edge.
            // Matchups are not filtered to the currently running Dota patch.
            var matchupAdjustment = possibleWeight > 0
                ? Math.Clamp(weightedEdge / possibleWeight * 70.0, -17.0, 17.0)
                : 0;
            // Confidence is a coverage indicator, NOT win probability.
            var evidenceCoverage = possibleWeight > 0
                ? 100 * reliableWeight / possibleWeight : 0;
            var evidenceGrade = pickedEnemies.Count == 0 ? "враги не выбраны"
                : evidenceCoverage >= 55 ? "высокая"
                : evidenceCoverage >= 25 ? "средняя" : "низкая";
            var evidenceSummary = $"Достоверность контрпиков: {evidenceGrade}, " +
                $"полнота данных {evidenceCoverage:F0}% ({measured}/{pickedEnemies.Count} врагов).";

            // Ally synergy is a SECONDARY factor: its maximum absolute effect
            // is 7 points, vs 17 for enemy counterpicks. We compare the
            // observed combined-team win rate to this hero's rank baseline.
            // Missing teammates count as neutral, and duplicates only once.
            double allyEdgeSum = 0;
            var synergyDetails = new List<string>();
            foreach (var ally in pickedAllies)
            {
                var allyId = ally.SelectedHero!.Id;
                if (allySynergies is null ||
                    !allySynergies.TryGetValue(allyId, out var rows)) continue;
                var with = rows.FirstOrDefault(r => r.HeroId == hero.Id);
                if (with is null || with.GamesPlayed < 30 ||
                    with.Wins < 0 || with.Wins > with.GamesPlayed) continue;
                var combinedRate = (with.Wins + 50.0) / (with.GamesPlayed + 100.0);
                var edge = combinedRate - overallStratzBaseline;
                allyEdgeSum += edge;
                var source = synergySources?.GetValueOrDefault(allyId) ?? "STRATZ";
                synergyDetails.Add($"{ally.SelectedHero.Name}: {edge * 35.0 / pickedAllies.Count:+0.0;-0.0;0.0}" +
                    $" ({with.GamesPlayed:N0} матчей, {source})");
            }
            var synergyAdjustment = pickedAllies.Count > 0
                ? Math.Clamp(allyEdgeSum / pickedAllies.Count * 35.0, -7.0, 7.0)
                : 0;

            var isStandard = PositionEvidence.IsEligible(observed, hero.Id, position);
            var sample = PositionEvidence.Find(observed, hero.Id, position);
            var totalHeroGames = observed.TryGetValue(hero.Id, out var positionRows)
                ? positionRows.Sum(r => (long)r.Games) : 0L;
            var roleShare = sample is not null && totalHeroGames > 0
                ? sample.Games / (double)totalHeroGames : 0;
            // Empirical position eligibility is a gate, not an uncalibrated
            // one-size-fits-all role score. Off-role experiments are penalized.
            var roleAdjustment = isStandard ? 6.0 : -8.0;
            var favorite = favorites.Contains(hero.Id);
            double metaAdjustment = 0;
            double popularityAdjustment = 0;
            string compactWinRate = "WR —";
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
                    metaLabel = $"{sourceName}: WR (все роли) {100.0 * w / p:F1}% ({p:N0} пиков)";
                    // Pro mode currently has no *professional* position split.
                    // Use pro overall WR conservatively instead of falsely
                    // labelling ranked position data as professional.
                    if (professionalMeta)
                    {
                        var smoothed = (w + 100.0) / (p + 200.0);
                        metaAdjustment = Math.Clamp((smoothed - .5) * 40.0, -5.0, 5.0);
                        compactWinRate = $"Профи WR {100.0 * w / p:F1}%";
                    }
                }
                if (p > 0 && totalPicks > 0)
                {
                    var pickRate = 1000.0 * p / totalPicks;
                    popularityLabel = $"{sourceName}: частота выбора (все роли) ≈{pickRate:F1}% матчей";
                }
            }

            if (!professionalMeta && sample is { Games: >= PositionEvidence.MinGames } &&
                sample.Wins >= 0 && sample.Wins <= sample.Games)
            {
                var smoothed = (sample.Wins + 100.0) / (sample.Games + 200.0);
                metaAdjustment = Math.Clamp((smoothed - .5) * 55.0, -6.0, 6.0);
                metaLabel = $"STRATZ: WR на позиции {position}: " +
                    $"{100.0 * sample.Wins / sample.Games:F1}% ({sample.Games:N0} матчей)";
                compactWinRate = $"Роль WR {100.0 * sample.Wins / sample.Games:F1}%";
            }
            // Raw global pick frequency is deliberately not used as evidence
            // of strength on a specific position.
            popularityAdjustment = 0;
            var score = Math.Clamp(50 + roleAdjustment + matchupAdjustment
                + synergyAdjustment + metaAdjustment + popularityAdjustment +
                (favorite ? 4 : 0), 0, 100);

            string roleLabel = isStandard
                ? $"STRATZ: позиция {position}, {sample!.Games:N0} игр " +
                  $"({roleShare:P1} всех известных игр героя)"
                : sample is null
                    ? $"Нет подтверждённых матчей на позиции {position}"
                    : $"Недостаточно игр на позиции {position}: {sample.Games:N0} " +
                      $"({roleShare:P1} игр героя)";
            string evidence = pickedEnemies.Count == 0
                ? "Матчапы: враги не выбраны"
                : $"Матчапы: {measured}/{pickedEnemies.Count} противников";
            // Show exactly how a candidate got its heuristic score. No win-rate claims.
            string contributions = $"Вклад в балл: роль {roleAdjustment:+0.0;-0.0;0.0}; " +
                $"противники {matchupAdjustment:+0.0;-0.0;0.0}; " +
                $"союзники {synergyAdjustment:+0.0;-0.0;0.0}; " +
                $"WR выбранной меты {metaAdjustment:+0.0;-0.0;0.0}; " +
                $"популярность {popularityAdjustment:+0.0;-0.0;0.0}; " +
                $"избранное {(favorite ? "+4.0" : "0.0")}.";
            string details = $"{roleLabel}. Контрпики считаются против всей команды, " +
                "керри/мид/оффлейн имеют больший вес, враги на линии — дополнительный. " +
                "STRATZ матчи сгруппированы по двум соседним рангам; резервные данные OpenDota " +
                "относятся к лигам. Позиции противников не проверяются в статистике матчапов. " +
                "Синергии союзников — STRATZ, если выборка доступна, с меньшим весом, " +
                "чем контрпики. Данные пока не фильтруются по текущему патчу.";
            var counterSummary = counterDetails.Count == 0
                ? "Контрпики: достоверных результатов по выбранным противникам нет."
                : "Контрпики по всей команде (вклад в балл): " +
                  string.Join("; ", counterDetails.Select(x =>
                      $"{x.Name}: {(x.Edge * x.Weight / possibleWeight * 70):+0.0;-0.0;0.0}" +
                      $" ({x.Games:N0} матчей, {x.Source})"));
            var synergySummary = pickedAllies.Count == 0
                ? "Синергии: союзники пока не выбраны."
                : synergyDetails.Count == 0
                    ? $"Синергии: нет достаточных данных для {pickedAllies.Count} союзников."
                    : "Синергии с союзниками (вклад в балл): " +
                      string.Join("; ", synergyDetails);

            ranked.Add(new Recommendation(hero, score, measured, pickedEnemies.Count,
                games, favorite, details)
            {
                MetaLabel = metaLabel,
                PopularityLabel = popularityLabel,
                EvidenceLabel = evidence,
                ReliabilityLabel = evidenceSummary,
                ScoreBreakdown = contributions,
                CounterpickSummary = counterSummary,
                SynergySummary = synergySummary,
                CompactSynergy = synergyDetails.Count > 0
                    ? $"Союзники {synergyAdjustment:+0.0;-0.0;0.0}"
                    : "Союзники —",
                CompactWinRate = compactWinRate,
                CompactCounterpick = measured > 0
                    ? $"Контрпик {matchupAdjustment:+0.0;-0.0;0.0}"
                    : "Контрпик —",
                IsRare = rare,
                IsNonStandard = !isStandard,
                PositionLabel = isStandard
                    ? $"✓ STRATZ: роль {position}, {sample!.Games:N0} матчей, {roleShare:P0}"
                    : $"⚠ Статистика роли {position} недостаточна"
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
