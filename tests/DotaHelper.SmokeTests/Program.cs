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
var data = new Dictionary<int, IReadOnlyList<Matchup>>
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
    new HashSet<int>(), data);
Check(onlyA[0].Hero.Id == 1, "Querying enemy matchups must invert enemy wins");
Check(onlyA.All(x => x.Hero.Id != opponentA.Id), "Picked heroes must be excluded");

var neutral = engine.Rank([carryA, carryB, opponentA, opponentB],
    [], [laneSlot, otherSlot], 1, new HashSet<int>(), data);
var neutralScore = neutral.Single(x => x.Hero.Id == 1).Score;

laneSlot.IsLaneOpponent = true;
var weighted = engine.Rank([carryA, carryB, opponentA, opponentB],
    [], [laneSlot, otherSlot], 1, new HashSet<int>(), data);
Check(weighted.Single(x => x.Hero.Id == 1).Score > neutralScore,
    "Lane opponent must receive higher weight");

var withFavorite = engine.Rank([carryA, carryB, opponentA, opponentB],
    [], [laneSlot, otherSlot], 1, new HashSet<int> { 1 }, data);
Check(Math.Abs(withFavorite.Single(x => x.Hero.Id == 1).Score -
               weighted.Single(x => x.Hero.Id == 1).Score - 5.0) < 0.001,
    "Favorites should receive a five-point bonus");

Console.WriteLine("Dota Helper engine smoke tests passed.");
