using DotaHelper.Models;
using DotaHelper.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception("Test failed: " + message);
}

var engine = new RecommendationEngine();
var carryA = new Hero { Id = 1, Name = "Carry A", Roles = ["Carry"] };
var carryB = new Hero { Id = 2, Name = "Carry B", Roles = ["Carry"] };
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
Check(noSample[0].MetaLabel.Contains("недостаточно"),
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
    new Hero { Id = id, Name = $"Sample {id}", Roles = ["Support"] }).ToArray();
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

Console.WriteLine("Dota Helper 0.2 engine smoke tests passed.");
