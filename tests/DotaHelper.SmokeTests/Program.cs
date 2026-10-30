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
// STRATZ matchUp.with contains win rates when the queried ally and
// candidate play in the SAME TEAM; matchUp.vs is not synergy data.
var synergyJson = """
{
 "data":{"heroStats":{"matchUp":[
  {"with":[
   {"heroId2":145,"matchCount":700,"winCount":430},
   {"heroId2":119,"matchCount":300,"winCount":145},
   {"heroId2":145,"matchCount":50,"winCount":10},
   {"heroId2":155,"matchCount":500,"winCount":250},
   {"heroId2":42,"matchCount":4,"winCount":3}
  ],"vs":[{"heroId2":42,"matchCount":200,"winCount":30}]}
 ]}}
}
""";
var partnerSamples = StratzClient.ParseSynergies(synergyJson, 155);
Check(partnerSamples.Count == 2, "Synergies must ignore self, tiny samples and vs opponents");
Check(partnerSamples.Single(x => x.HeroId == 145).GamesPlayed == 700 &&
      partnerSamples.Single(x => x.HeroId == 145).Wins == 430,
    "Synergy parser must preserve larger samples and wins");
try
{
    _ = StratzClient.ParseSynergies("""{"errors":[{"message":"bad"}],"data":null}""", 155);
    throw new Exception("GraphQL synergy error must not be accepted as neutral data");
}
catch (JsonException) { }

var allySlot = new DraftSlot("Largo") { SelectedHero = largo };
var noAllyEvidence = engine.Rank([kez], [allySlot], [], 1,
    new HashSet<int>(), emptyMatchups, null, 0, HeroPoolMode.All,
    observedPositions: samples)[0];
var allAllyData = new Dictionary<int, IReadOnlyList<Matchup>> { [155] = partnerSamples };
var withAllyEvidence = engine.Rank([kez], [allySlot], [], 1,
    new HashSet<int>(), emptyMatchups, null, 0, HeroPoolMode.All,
    observedPositions: samples, allySynergies: allAllyData)[0];
Check(withAllyEvidence.Score > noAllyEvidence.Score,
    "Positive teammate results should increase a candidate's rating");
Check(withAllyEvidence.SynergySummary.Contains("Largo") &&
      withAllyEvidence.CompactSynergy.Contains("Союзники"),
    "Explain teammate sample and show short synergy score");
allAllyData[155] =
[
    new Matchup { HeroId = 145, GamesPlayed = 700, Wins = 220 }
];
var badAlly = engine.Rank([kez], [allySlot], [], 1,
    new HashSet<int>(), emptyMatchups, null, 0, HeroPoolMode.All,
    observedPositions: samples, allySynergies: allAllyData)[0];
Check(badAlly.Score < noAllyEvidence.Score,
    "Negative joint win-rate must penalize a candidate");

// Synergies must not be able to outweigh counters in raw score contribution.
Check(Math.Abs(withAllyEvidence.Score - noAllyEvidence.Score) <= 7.001,
    "Synergy adjustment must never exceed seven points");

// Steam numeric ID and custom /id/ links are resolved without API credentials.
// Public XML is deprecated, so parsing failures must remain explicit.
var (_, vanity) = SteamProfileResolver.ParseInput(
    "https://steamcommunity.com/id/_FreeP0rn/");
Check(vanity == "_FreeP0rn", "User's custom Steam vanity must be accepted");
var (steamAccount, ignored) = SteamProfileResolver.ParseInput(
    "https://steamcommunity.com/profiles/76561198108247405/");
Check(steamAccount.HasValue && ignored is null,
    "Numeric SteamID64 links must remain supported");
var xmlAccount = SteamProfileResolver.ParseSteamId64FromXml(
    "<profile><steamID64>76561198108247405</steamID64></profile>");
Check(xmlAccount == steamAccount, "Public XML SteamID64 must map to same account");
try
{
    SteamProfileResolver.ParseInput("https://example.com/id/_FreeP0rn/");
    throw new Exception("Unexpected hosts must be rejected");
}
catch (ArgumentException) { }
try
{
    SteamProfileResolver.ParseSteamId64FromXml(
        "<!DOCTYPE foo [<!ENTITY xxe SYSTEM 'file:///etc/passwd'>]><profile>&xxe;</profile>");
    throw new Exception("XML DTD must be prohibited");
}
catch (System.Xml.XmlException) { }

var patchFixture = """
{
  "patches":[
    {"patch_number":"7.41f","patch_timestamp":1789455600},
    {"patch_number":"7.41e","patch_timestamp":1785394800},
    {"patch_number":"7.41","patch_timestamp":1774335600}
  ],"success":true
}
""";
var patch = PatchInfoClient.Parse(patchFixture);
Check(patch.Number == "7.41f" && patch.DisplayLabel.Contains("7.41f"),
    "Official Valve patch feed parser must identify newest patch");
Check(PatchInfoClient.Parse(patchFixture, true).FromStaleCache,
    "Stale Valve patch list must be disclosed");
var reversed = """
{"patches":[
 {"patch_number":"7.41","patch_timestamp":1774335600},
 {"patch_number":"7.41f","patch_timestamp":1789455600}
]}
""";
Check(PatchInfoClient.Parse(reversed).Number == "7.41f",
    "Patch parser must sort by published UTC timestamp, not list position");
try
{
    _ = PatchInfoClient.Parse("""{"patches":[{"patch_number":"x","patch_timestamp":999999999999}]}""");
    throw new Exception("Out-of-range future patch must be rejected");
}
catch (JsonException) { }

// A 20-game extreme matchup must not dominate the draft.
var oneEnemy = new DraftSlot("Enemy") {
    SelectedHero = enemyCarry, EnemyPosition = 1
};
var tinyExtreme = new Dictionary<int, IReadOnlyList<Matchup>> {
    [enemyCarry.Id] = [new Matchup { HeroId = 145, GamesPlayed = 20, Wins = 0 }]
};
var largeExtreme = new Dictionary<int, IReadOnlyList<Matchup>> {
    [enemyCarry.Id] = [new Matchup { HeroId = 145, GamesPlayed = 2000, Wins = 0 }]
};
Recommendation ScoreMatchup(IReadOnlyDictionary<int, IReadOnlyList<Matchup>> m) =>
    engine.Rank([kez], [], [oneEnemy], 1, new HashSet<int>(), m,
        null, 0, HeroPoolMode.All, observedPositions: samples)[0];
var lowEvidence = ScoreMatchup(tinyExtreme);
var highEvidence = ScoreMatchup(largeExtreme);
Check(highEvidence.Score > lowEvidence.Score,
    "Reliable 2000-game extreme counter should outweigh 20-game result");
Check(highEvidence.ReliabilityLabel.Contains("высокая") &&
      lowEvidence.ReliabilityLabel.Contains("низкая"),
    "Show quality based on weighted sample size and matchup coverage");

// Role eligibility remains fully empirical: Riki is allowed as mid
// when actual games support it in either hero pool. Frequency is
// shown separately from skill or win probability.
var riki = new Hero { Id = 32, Name = "Riki", InternalName = "npc_dota_hero_riki" };
var snapfire = new Hero { Id = 128, Name = "Snapfire", InternalName = "npc_dota_hero_snapfire" };
var fringe = new Hero { Id = 999, Name = "Niche", InternalName = "npc_dota_hero_niche" };
var roleEvidence = PositionEvidence.Index([
    new PositionSample(32, 1, 600, 300),
    new PositionSample(32, 2, 350, 195),
    new PositionSample(32, 4, 50, 20),
    new PositionSample(128, 2, 400, 210),
    new PositionSample(128, 4, 900, 450),
    new PositionSample(999, 2, 30, 15),
    new PositionSample(999, 1, 570, 280)
]);
var rikiRole = PositionEvidence.Evaluate(roleEvidence, 32, 2);
var nicheRole = PositionEvidence.Evaluate(roleEvidence, 999, 2);
Check(rikiRole.Eligible && rikiRole.Category == "Частая",
    "Riki must not be excluded from mid merely by archetype");
Check(nicheRole.Eligible && nicheRole.Category == "Редкая",
    "Borderline 30-game role evidence must remain visible but marked as rare");
Check(rikiRole.ScoreAdjustment > nicheRole.ScoreAdjustment,
    "A 30-game marginal role must not get the same bonus as established mid usage");
Check(nicheRole.ScoreAdjustment <= 0.0,
    "Marginal usage must not grant a large role bonus");
Check(PositionEvidence.Evaluate(roleEvidence, 128, 2).Eligible,
    "Snapfire should also qualify as a data-driven mid option");
var allMids = engine.Rank([riki, snapfire, fringe], [], [], 2,
    new HashSet<int>(), emptyMatchups, null, 0, HeroPoolMode.All,
    observedPositions: roleEvidence);
Check(allMids.Count == 3 &&
      allMids.Any(x => x.Hero.Id == 32) &&
      allMids.Any(x => x.Hero.Id == 128),
    "Both Riki and Snapfire mid must appear if real position samples support them");
Check(allMids.Single(x => x.Hero.Id == 32).CompactRole.Contains("35%") &&
      allMids.Single(x => x.Hero.Id == 999).PositionLabel.Contains("редкая"),
    "Short and detailed labels must expose role usage without inventing strength");
Console.WriteLine("Dota Helper 0.8.1 position familiarity checks passed.");
