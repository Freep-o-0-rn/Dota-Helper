using System.Text.Json;
using DotaHelper.Models;
using DotaHelper.Services;

static void Check(bool value, string message)
{
    if (!value) throw new InvalidOperationException("Test failed: " + message);
}

var engine = new RecommendationEngine();
var largo = new Hero { Id = 155, Name = "Largo", InternalName = "npc_dota_hero_largo" };
var willow = new Hero { Id = 119, Name = "Dark Willow", InternalName = "npc_dota_hero_dark_willow" };
var wk = new Hero { Id = 42, Name = "Wraith King", InternalName = "npc_dota_hero_skeleton_king" };
var kez = new Hero { Id = 145, Name = "Kez", InternalName = "npc_dota_hero_kez" };
var rubick = new Hero { Id = 86, Name = "Rubick", InternalName = "npc_dota_hero_rubick" };
var heroes = new[] { largo, willow, wk, kez, rubick };

// The real source schema groups by POSITION_1 ... POSITION_5.
var json = """
{
 "data": { "heroStats": { "stats": [
   {"heroId":155,"position":"POSITION_3","matchCount":350,"winCount":190},
   {"heroId":155,"position":"POSITION_4","matchCount":230,"winCount":120},
   {"heroId":155,"position":"POSITION_5","matchCount":180,"winCount":90},
   {"heroId":119,"position":"POSITION_4","matchCount":1200,"winCount":640},
   {"heroId":119,"position":"POSITION_5","matchCount":350,"winCount":190},
   {"heroId":119,"position":"POSITION_2","matchCount":9,"winCount":6},
   {"heroId":42,"position":"POSITION_1","matchCount":5000,"winCount":2550},
   {"heroId":42,"position":"POSITION_3","matchCount":12,"winCount":8},
   {"heroId":145,"position":"POSITION_1","matchCount":350,"winCount":180},
   {"heroId":86,"position":"POSITION_4","matchCount":1400,"winCount":700}
 ] } }
}
""";
var parsed = StratzClient.Parse(json);
var samples = PositionEvidence.Index(parsed);

Check(parsed.Count == 10, "All 10 STRATZ position rows must parse");
Check(PositionEvidence.IsEligible(samples, 155, 3) &&
      PositionEvidence.IsEligible(samples, 155, 4) &&
      PositionEvidence.IsEligible(samples, 155, 5),
      "Largo's actual matches qualify him for offlane and both supports");
Check(PositionEvidence.IsEligible(samples, 119, 5),
      "Dark Willow position 5 must be accepted from real observations");
Check(!PositionEvidence.IsEligible(samples, 42, 3),
      "Small offlane Wraith King sample must not count as solid evidence");
Check(!PositionEvidence.IsEligible(samples, 119, 2),
      "Experimental tiny mid sample must not become standard mid recommendation");

IReadOnlyDictionary<int, IReadOnlyList<Matchup>> emptyMatchups =
    new Dictionary<int, IReadOnlyList<Matchup>>();

var offlane = engine.Rank(heroes, [], [], 3, new HashSet<int>(),
    emptyMatchups, null, 0, HeroPoolMode.All, observedPositions: samples);
Check(offlane.Count == 1 && offlane[0].Hero.Id == 155,
    "Role-3 pool must be inferred from samples rather than manual lists");
Check(offlane[0].PositionLabel.Contains("350"),
    "Show actual count of offlane games in position evidence");

var support5 = engine.Rank(heroes, [], [], 5, new HashSet<int>(),
    emptyMatchups, null, 0, HeroPoolMode.All, observedPositions: samples);
Check(support5.Select(x => x.Hero.Id).ToHashSet().SetEquals([155, 119]),
    "Largo and Dark Willow must be data-driven hard-support options");

var carry = engine.Rank(heroes, [], [], 1, new HashSet<int>(),
    emptyMatchups, null, 0, HeroPoolMode.All, observedPositions: samples);
Check(carry.Select(x => x.Hero.Id).ToHashSet().SetEquals([42, 145]),
    "Carry pool must follow observed position-1 usage");

var noData = engine.Rank(heroes, [], [], 5, new HashSet<int>(),
    emptyMatchups, null, 0, HeroPoolMode.All);
Check(noData.Count == 0,
    "Standard role pool must fail closed without a real position dataset");

var experiments = engine.Rank(heroes, [], [], 5, new HashSet<int>(),
    emptyMatchups, null, 0, HeroPoolMode.Experimental);
Check(experiments.Count == heroes.Length && experiments.All(x => x.IsNonStandard),
    "Experiment must still work with no token, but disclose unknown roles");

// Predefined labels cannot promote a hero without an observed sample.
wk.Roles = ["Carry", "Support", "Durable", "Disabler"];
var tagOnly = engine.Rank([wk], [], [], 5, new HashSet<int>(),
    emptyMatchups, null, 0, HeroPoolMode.All, observedPositions: samples);
Check(tagOnly.Count == 0, "Valve generic role tags cannot bypass position evidence");

// Strong enemy core counters continue to outrank generic overall WR.
var enemyCarry = new Hero { Id = 8001, Name = "Enemy carry" };
var enemySupport = new Hero { Id = 8002, Name = "Enemy support" };
var slots = new[]
{
    new DraftSlot("Enemy carry") { SelectedHero = enemyCarry },
    new DraftSlot("Enemy support") { SelectedHero = enemySupport }
};
var mu = new Dictionary<int, IReadOnlyList<Matchup>>
{
    [8001] = [new Matchup { HeroId = 145, GamesPlayed = 500, Wins = 100 }],
    [8002] = [new Matchup { HeroId = 145, GamesPlayed = 500, Wins = 400 }]
};
double Score() => engine.Rank([kez], [], slots, 1,
    new HashSet<int>(), mu, null, 0, HeroPoolMode.All,
    observedPositions: samples)[0].Score;
var equal = Score();
slots[0].EnemyPosition = 1;
slots[1].EnemyPosition = 5;
var strongCore = Score();
Check(strongCore > equal, "Core counter must be weighted above support matchup");
slots[0].EnemyPosition = 5;
slots[1].EnemyPosition = 2;
Check(Score() < equal, "Unfavorable mid matchup must lower candidate rating");
slots[0].EnemyPosition = 3;
slots[1].EnemyPosition = 5;
slots[0].IsLaneOpponent = true;
Check(Score() > strongCore, "Lane opponent bonus should add to core weight");

// Overall professional aggregates are separate from STRATZ ranked role evidence.
var stats = new Dictionary<int, HeroStats>
{
    [42] = new HeroStats { Id = 42, Picks4 = 1000, Wins4 = 400,
        ProPicks = 600, ProWins = 400 },
    [145] = new HeroStats { Id = 145, Picks4 = 1000, Wins4 = 650,
        ProPicks = 600, ProWins = 210 }
};
var pub = engine.Rank([wk, kez], [], [], 1, new HashSet<int>(),
    emptyMatchups, stats, 4, HeroPoolMode.All, false, samples);
var pro = engine.Rank([wk, kez], [], [], 1, new HashSet<int>(),
    emptyMatchups, stats, 4, HeroPoolMode.All, true, samples);
Check(pro[0].Hero.Id == 42, "Professional baseline must use pro stats");
Check(pro[0].MetaLabel.Contains("Профи"), "Professional overall WR must be labelled");
Check(pub.All(x => x.MetaLabel.StartsWith("STRATZ")),
    "Ranked mode uses position-specific STRATZ WR, not general OpenDota WR");

// STRATZ combines adjacent medal brackets; never pretend otherwise.
Check(StratzClient.BracketName(8, false) == "DIVINE_IMMORTAL" &&
      StratzClient.BracketName(3, false) == "CRUSADER_ARCHON" &&
      StratzClient.BracketName(6, false) == "LEGEND_ANCIENT",
    "STRATZ basic rank bracket selection must match actual schema");

var invalidGraphQl = """{"errors":[{"message":"bad request"}],"data":null}""";
try
{
    _ = StratzClient.Parse(invalidGraphQl);
    throw new Exception("GraphQL errors must not be mistaken for zero games");
}
catch (JsonException) { }

Check(largo.PortraitUrl.EndsWith("/heroes/largo.png") &&
      kez.PortraitUrl.EndsWith("/heroes/kez.png"),
      "Original Valve hero art must remain available");

// STRATZ matchUp query returns WINS OF THE QUERIED ENEMY, not the candidate.
// Invalid rows and duplicates must be handled conservatively.
var stratzMatchupsJson = """
{
 "data": { "heroStats": { "matchUp": [
    { "vs": [
        {"heroId2":145,"matchCount":500,"winCount":100},
        {"heroId2":119,"matchCount":500,"winCount":350},
        {"heroId2":8001,"matchCount":100,"winCount":50},
        {"heroId2":155,"matchCount":3,"winCount":2},
        {"heroId2":145,"matchCount":300,"winCount":180}
    ] }
 ] } }
}
""";
var rankCounters = StratzClient.ParseMatchups(stratzMatchupsJson, 8001);
Check(rankCounters.Count == 2 &&
      rankCounters.Single(x => x.HeroId == 145).GamesPlayed == 500 &&
      rankCounters.Single(x => x.HeroId == 145).Wins == 100,
    "STRATZ counter parser must keep largest sample and queried enemy wins");
try
{
    StratzClient.ParseMatchups("""{"errors":[{"message":"invalid bracket"}],"data":null}""", 8001);
    throw new Exception("STRATZ errors should never be treated as neutral games");
}
catch (JsonException) { }

var fiveEnemySlots = Enumerable.Range(1, 5).Select(pos =>
{
    var hero = new Hero { Id = 8100 + pos, Name = "Enemy " + pos };
    return new DraftSlot("Enemy " + pos)
    {
        SelectedHero = hero, EnemyPosition = pos, IsLaneOpponent = pos == 3
    };
}).ToArray();
var fiveData = Enumerable.Range(1, 5).ToDictionary(pos => 8100 + pos,
    pos => (IReadOnlyList<Matchup>)[new Matchup {
        HeroId = 145, GamesPlayed = 500, Wins = pos <= 3 ? 100 : 300
    }]);
var fiveSources = Enumerable.Range(1, 5).ToDictionary(pos => 8100 + pos,
    pos => pos <= 3 ? "STRATZ" : "OpenDota (резерв)");
var allFiveCounters = engine.Rank([kez], [], fiveEnemySlots, 1,
    new HashSet<int>(), fiveData, null, 0, HeroPoolMode.All, false, samples,
    fiveSources)[0];
Check(allFiveCounters.OpponentsMeasured == 5 &&
      allFiveCounters.OpponentsTotal == 5 &&
      allFiveCounters.CounterpickSummary.Contains("Enemy 1") &&
      allFiveCounters.CounterpickSummary.Contains("Enemy 5") &&
      allFiveCounters.CounterpickSummary.Contains("STRATZ") &&
      allFiveCounters.CounterpickSummary.Contains("OpenDota"),
    "Counters must cover all five enemies and disclose per-enemy source");
Check(allFiveCounters.SampleGames == 2500,
    "Sample sizes from each enemy must be accounted for");
Check(allFiveCounters.CompactCounterpick.Contains("Контрпик"),
    "Compact recommendation must include counter contribution");

var oldOpenDota = engine.Rank([kez], [], fiveEnemySlots, 1,
    new HashSet<int>(), fiveData, null, 0, HeroPoolMode.All, false, samples)[0];
Check(Math.Abs(allFiveCounters.Score - oldOpenDota.Score) > .01,
    "STRATZ counter evidence should use a rank baseline, unlike OpenDota fallback");

Check(StratzClient.NormalizeToken("  Bearer abc123  ") == "abc123",
    "Bearer prefix copied into token must be removed");
Check(StratzClient.NormalizeToken(" 'some-token' ") == "some-token",
    "Quotes copied around token must be removed");
Check(StratzClient.NormalizeToken("  ") == "",
    "Empty secret must stay empty");
Check(StratzClient.DescribeHttpError(System.Net.HttpStatusCode.Unauthorized,
    "application/json", "{}").Contains("401"),
    "HTTP 401 must be distinguished from Cloudflare");
Check(StratzClient.DescribeHttpError(System.Net.HttpStatusCode.Forbidden,
    "text/html", "Just a moment cf-chl").Contains("Cloudflare"),
    "Cloudflare challenge must not blame token");
Check(StratzClient.DescribeHttpError(System.Net.HttpStatusCode.Forbidden,
    "application/json", "{}").Contains("403"),
    "HTTP 403 without challenge must be classified separately");
Check(StratzClient.DescribeHttpError((System.Net.HttpStatusCode)429,
    "application/json", "{}").Contains("429"),
    "Rate limiting must be explained");
Console.WriteLine("Dota Helper 0.6.1 STRATZ authentication diagnostics passed.");
