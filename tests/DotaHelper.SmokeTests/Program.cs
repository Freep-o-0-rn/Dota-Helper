using DotaHelper.Models;
using DotaHelper.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("Test failed: " + message);
}

var engine = new RecommendationEngine();
var carryA = new Hero { Id = 1, Name = "Carry A", InternalName = "npc_dota_hero_juggernaut", Roles = ["Carry"] };
var carryB = new Hero { Id = 2, Name = "Carry B", InternalName = "npc_dota_hero_luna", Roles = ["Carry"] };
var opponentA = new Hero { Id = 3, Name = "Enemy A" };
var opponentB = new Hero { Id = 4, Name = "Enemy B" };
var laneSlot = new DraftSlot("Enemy A") { SelectedHero = opponentA };
var otherSlot = new DraftSlot("Enemy B") { SelectedHero = opponentB };
var matchupData = new Dictionary<int, IReadOnlyList<Matchup>>
{
    [3] =
    [
        new Matchup { HeroId = 1, GamesPlayed = 100, Wins = 20 },
        new Matchup { HeroId = 2, GamesPlayed = 100, Wins = 80 }
    ],
    [4] =
    [
        new Matchup { HeroId = 1, GamesPlayed = 100, Wins = 80 },
        new Matchup { HeroId = 2, GamesPlayed = 100, Wins = 20 }
    ]
};

var onlyA = engine.Rank([carryA, carryB, opponentA], [], [laneSlot], 1,
    new HashSet<int>(), matchupData);
Check(onlyA[0].Hero.Id == 1, "Enemy wins must be inverted for candidate");
Check(onlyA.All(x => x.Hero.Id != opponentA.Id), "Picked heroes must be excluded");

var neutral = engine.Rank([carryA, carryB, opponentA, opponentB],
    [], [laneSlot, otherSlot], 1, new HashSet<int>(), matchupData);
var neutralScore = neutral.Single(x => x.Hero.Id == 1).Score;
laneSlot.IsLaneOpponent = true;
var weighted = engine.Rank([carryA, carryB, opponentA, opponentB],
    [], [laneSlot, otherSlot], 1, new HashSet<int>(), matchupData);
Check(weighted.Single(x => x.Hero.Id == 1).Score > neutralScore,
    "Lane opponent must receive more weight than other opponent");

var withFavorite = engine.Rank([carryA, carryB, opponentA, opponentB],
    [], [laneSlot, otherSlot], 1, new HashSet<int> { 1 }, matchupData);
Check(Math.Abs(withFavorite.Single(x => x.Hero.Id == 1).Score -
               weighted.Single(x => x.Hero.Id == 1).Score - 4.0) < 0.001,
    "Favorites must receive four point bonus");

var stats = new Dictionary<int, HeroStats>
{
    [1] = new HeroStats { Id = 1, Picks4 = 2000, Wins4 = 1200, Picks5 = 2000, Wins5 = 1200 },
    [2] = new HeroStats { Id = 2, Picks4 = 1000, Wins4 = 450, Picks5 = 1200, Wins5 = 500 }
};
var rank4 = engine.Rank([carryA, carryB], [], [], 1, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), stats, rankBracket: 4);
Check(rank4[0].Hero.Id == 1, "Higher rank-specific win rate should contribute positively");
Check(rank4[0].MetaLabel.Contains("60,0") || rank4[0].MetaLabel.Contains("60.0"),
    "Rank-specific win rate should be displayed");
var rank5 = engine.Rank([carryA, carryB], [], [], 1, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), stats, rankBracket: 5);
Check(rank5[0].MetaLabel.Contains("60,0") || rank5[0].MetaLabel.Contains("60.0"),
    "Rank bracket data should be selected, not pooled across tiers");

var noSample = engine.Rank([carryA], [], [], 1, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), new Dictionary<int, HeroStats>(), 7);
Check(noSample[0].MetaLabel.Contains("нет данных"),
    "Missing stats must not pretend to be available");

var favoriteOnly = engine.Rank([carryA, carryB], [], [], 1, new HashSet<int> { 2 },
    new Dictionary<int, IReadOnlyList<Matchup>>(), stats, 0, HeroPoolMode.Favorites);
Check(favoriteOnly.Count == 1 && favoriteOnly[0].Hero.Id == 2,
    "Favorites-only pool should exclude other heroes");

// The remote API may send null pick fields; JSON parsing must remain resilient.
var json = """{"id":1,"hero_id":0,"4_pick":null,"4_win":null,"5_pick":120,"5_win":66}""";
var parsed = System.Text.Json.JsonSerializer.Deserialize<HeroStats>(json)!;
Check(parsed.EffectiveId == 1 && parsed.InBracket(4).Picks == 0 &&
      parsed.InBracket(5).Wins == 66, "Nullable OpenDota data should parse");

// The common pool should exclude rare choices; All should still expose them.
var sampleHeroes = Enumerable.Range(10, 20).Select(id =>
    new Hero { Id = id, Name = $"Sample {id}",
        InternalName = "npc_dota_hero_crystal_maiden", Roles = ["Support"] }).ToArray();
var sampleStats = sampleHeroes.ToDictionary(h => h.Id, h => new HeroStats
{
    Id = h.Id,
    Picks4 = h.Id < 16 ? 1 : 500,
    Wins4 = h.Id < 16 ? 1 : 250
});
var common = engine.Rank(sampleHeroes, [], [], 5, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), sampleStats, 4, HeroPoolMode.Common);
var all = engine.Rank(sampleHeroes, [], [], 5, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), sampleStats, 4, HeroPoolMode.All);
Check(common.All(x => x.Hero.Id >= 16), "Common pool must filter the rare bottom quarter");
Check(all.Any(x => x.Hero.Id < 16), "All pool must allow rare heroes");

// Regression: broad "Support" or "Disabler" tags and high overall winrate
// must never make hard carries normal position 4 or 5 recommendations.
var wk = new Hero { Id = 42, Name = "Wraith King",
    InternalName = "npc_dota_hero_skeleton_king",
    Roles = ["Carry", "Support", "Durable", "Disabler"] };
var naix = new Hero { Id = 54, Name = "Lifestealer",
    InternalName = "npc_dota_hero_life_stealer", Roles = ["Carry", "Durable"] };
var rubick = new Hero { Id = 86, Name = "Rubick",
    InternalName = "npc_dota_hero_rubick", Roles = ["Support", "Disabler"] };
var lich = new Hero { Id = 31, Name = "Lich",
    InternalName = "npc_dota_hero_lich", Roles = ["Support", "Nuker"] };
var candidates = new[] { wk, naix, rubick, lich };
var inflated = candidates.ToDictionary(h => h.Id, h => new HeroStats
{
    Id = h.Id, Picks4 = 10000, Wins4 = h.Id == 42 ? 8000 : 5000
});
var standardFour = engine.Rank(candidates, [], [], 4, new HashSet<int> { 42 },
    new Dictionary<int, IReadOnlyList<Matchup>>(), inflated, 4, HeroPoolMode.All);
Check(!standardFour.Any(x => x.Hero.Id is 42 or 54),
    "Position 4 must exclude WK and Lifestealer regardless of overall winrate");
Check(standardFour.Any(x => x.Hero.Id == 86), "Position 4 must include Rubick");
var standardFive = engine.Rank(candidates, [], [], 5, new HashSet<int> { 42 },
    new Dictionary<int, IReadOnlyList<Matchup>>(), inflated, 4, HeroPoolMode.Favorites);
Check(standardFive.Count == 0, "Off-role favorite must not bypass position filter");
var experimental = engine.Rank(candidates, [], [], 4, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), inflated, 4, HeroPoolMode.Experimental);
Check(experimental.Any(x => x.Hero.Id == 42 && x.IsNonStandard),
    "Off-role picks must appear only in explicit experimental mode with warning");
Check(experimental[0].Hero.Id != 42,
    "Even experimental off-role WK must not top rankings only due to global WR");
Check(PositionProfile.IsStandard(lich, 5) && PositionProfile.IsStandard(rubick, 4),
    "Typical supports must remain eligible");
// All five positions must be filtered by a role-specific candidate pool.
// A global high winrate must not put Crystal Maiden onto pos 1 or
// Wraith King onto pos 2,3,4,5.
var axe = new Hero { Id = 2_001, Name = "Axe",
    InternalName = "npc_dota_hero_axe", Roles = ["Durable", "Initiator"] };
var sf = new Hero { Id = 2_002, Name = "Shadow Fiend",
    InternalName = "npc_dota_hero_nevermore", Roles = ["Carry", "Nuker"] };
var cm = new Hero { Id = 2_003, Name = "Crystal Maiden",
    InternalName = "npc_dota_hero_crystal_maiden", Roles = ["Support"] };
var roleSet = new[] { wk, naix, axe, sf, cm, rubick, lich };
var expectedStandard = new Dictionary<int, Hero[]>
{
    [1] = [wk, naix, sf],
    [2] = [sf],
    [3] = [axe],
    [4] = [cm, rubick, lich],
    [5] = [cm, rubick, lich]
};
foreach (var position in Enumerable.Range(1, 5))
{
    var recs = engine.Rank(roleSet, [], [], position, new HashSet<int>(),
        new Dictionary<int, IReadOnlyList<Matchup>>(), null, 0, HeroPoolMode.All);
    var actualIds = recs.Select(x => x.Hero.Id).ToHashSet();
    var expectedIds = expectedStandard[position].Select(x => x.Id).ToHashSet();
    Check(actualIds.SetEquals(expectedIds),
        $"Position {position} must only recommend its standard pool");
    var offRoleFavorite = engine.Rank(roleSet, [], [], position,
        new HashSet<int> { expectedStandard[position][0].Id, cm.Id, wk.Id },
        new Dictionary<int, IReadOnlyList<Matchup>>(), null, 0, HeroPoolMode.Favorites);
    Check(offRoleFavorite.All(x => PositionProfile.IsStandard(x.Hero, position)),
        $"Favorite hero must not bypass position {position} filter");
}
Check(!PositionProfile.IsStandard(cm, 1) &&
      !PositionProfile.IsStandard(wk, 2) &&
      !PositionProfile.IsStandard(wk, 3) &&
      PositionProfile.IsStandard(axe, 3) &&
      PositionProfile.IsStandard(sf, 2),
      "Manual position profiles must distinguish all five positions");

// Dark Willow is a legitimate position-5 candidate and must not disappear
// even when she ranks lower than 40 other eligible heroes.
var willow = new Hero { Id = 119, Name = "Dark Willow",
    InternalName = "npc_dota_hero_dark_willow", Roles = ["Support", "Disabler"] };
Check(PositionProfile.IsStandard(willow, 5), "Dark Willow must qualify as pos 5");

var manyCandidates = Enumerable.Range(10000, 60)
    .Select(id => new Hero
    {
        Id = id, Name = "Hero " + id,
        InternalName = "npc_dota_hero_crystal_maiden", Roles = ["Support"]
    }).Concat([willow]).ToArray();
var allFives = engine.Rank(manyCandidates, [], [], 5, new HashSet<int>(),
    new Dictionary<int, IReadOnlyList<Matchup>>(), null, 0, HeroPoolMode.All);
Check(allFives.Count == 61 && allFives.Any(x => x.Hero.Id == willow.Id),
    "Eligible heroes must not be truncated to a top 40");
Check(allFives.Select(r => r.PositionInRanking)
    .SequenceEqual(Enumerable.Range(1, allFives.Count)),
    "All candidates must retain their actual ranking for search");
Check(allFives.All(x => x.ScoreBreakdown.Contains("противники")),
    "Show factors affecting heuristic scores");

// Regression: counterpicks must count ALL five opponents, emphasizing
// enemy carry/mid/offlane, while retaining other enemies and lane opponents.
var enemyCarry = new Hero { Id = 9001, Name = "Enemy Carry" };
var enemySupport = new Hero { Id = 9002, Name = "Enemy Support" };
var enemySlots = new[]
{
    new DraftSlot("A") { SelectedHero = enemyCarry },
    new DraftSlot("B") { SelectedHero = enemySupport }
};
var opposingData = new Dictionary<int, IReadOnlyList<Matchup>>
{
    [9001] = [new Matchup { HeroId = carryA.Id, GamesPlayed = 500, Wins = 100 }],
    [9002] = [new Matchup { HeroId = carryA.Id, GamesPlayed = 500, Wins = 400 }]
};
double Evaluate() => engine.Rank([carryA], [], enemySlots, 1,
    new HashSet<int>(), opposingData, null, 0, HeroPoolMode.All)[0].Score;
var bothUnknown = Evaluate();
enemySlots[0].EnemyPosition = 1; // Beneficial counterpick to enemy carry
enemySlots[1].EnemyPosition = 5;
var coreFavorable = Evaluate();
Check(coreFavorable > bothUnknown, "Positive matchup vs enemy carry must weigh more");
enemySlots[0].EnemyPosition = 5;
enemySlots[1].EnemyPosition = 2; // Unfavorable matchup vs enemy mid
var coreUnfavorable = Evaluate();
Check(coreUnfavorable < bothUnknown, "Negative matchup vs enemy mid must weigh more");
enemySlots[0].EnemyPosition = 3;
enemySlots[1].EnemyPosition = 5;
enemySlots[0].IsLaneOpponent = true;
Check(Evaluate() > coreFavorable, "Lane opponent bonus compounds with core priority");
var summary = engine.Rank([carryA], [], enemySlots, 1, new HashSet<int>(),
    opposingData, null, 0, HeroPoolMode.All)[0].CounterpickSummary;
Check(summary.Contains("Enemy Carry") && summary.Contains("Enemy Support"),
    "All enemy matchups must appear in explanation");
Check(new DraftSlot("Unknown").EnemyPosition == 0,
    "New enemies must have unknown roles by default");

Console.WriteLine("Dota Helper 0.4 core-priority counterpick tests passed.");
