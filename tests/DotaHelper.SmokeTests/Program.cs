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


// Version 0.9: draft preview and banned heroes remain independent.
var previewSlot = new DraftSlot("Мой пик", isPreview: true);
var enemyDraftSlot = new DraftSlot("Противник", isEnemy: true);
Check(previewSlot.IsPreview && !previewSlot.IsEnemy,
    "Own recommendation preview must never be counted as a teammate");
Check(enemyDraftSlot.IsEnemy && !enemyDraftSlot.IsPreview,
    "Enemy slot must support role and lane controls");
var banSlot = new BanSlot(1);
var banChanges = 0;
banSlot.Changed += (_, _) => banChanges++;
banSlot.SelectedHero = riki;
Check(banChanges == 1 && banSlot.SelectedHero?.Id == 32,
    "A ban selection must notify the UI");
banSlot.SelectedHero = null;
Check(banChanges == 2 && banSlot.SelectedHero is null,
    "Clearing a ban must notify the UI");
Check(DraftGameMode.AllPick != DraftGameMode.CaptainsMode,
    "All Pick and Captains Mode bans must remain distinct");
Console.WriteLine("Dota Helper 0.9 draft UI model tests passed.");

var wd = new Hero { Id = 30, Name = "Witch Doctor",
    InternalName = "npc_dota_hero_witch_doctor" };
var shadowDemon = new Hero { Id = 79, Name = "Shadow Demon",
    InternalName = "npc_dota_hero_shadow_demon" };
var legion = new Hero { Id = 104, Name = "Legion Commander",
    InternalName = "npc_dota_hero_legion_commander" };
var newLargo = new Hero { Id = 155, Name = "Largo",
    InternalName = "npc_dota_hero_largo", RussianName = "Ларго" };

Check(HeroSearch.Matches(wd, "вд") && HeroSearch.Matches(wd, "вич") &&
      HeroSearch.Matches(wd, "Witch Doctor") && HeroSearch.Matches(wd, "wd"),
    "Witch Doctor must match Russian/English common names and abbreviations");
Check(HeroSearch.Matches(shadowDemon, "шд") &&
      HeroSearch.Matches(shadowDemon, "шадоу") &&
      !HeroSearch.Matches(wd, "шд"),
    "Shadow Demon must match SHD without selecting Witch Doctor");
Check(HeroSearch.Matches(legion, "легион") &&
      HeroSearch.Matches(legion, "lc") &&
      HeroSearch.Matches(legion, "ktubjy"),
    "Legion Commander must match Russian alias and wrong keyboard layout");
Check(HeroSearch.Matches(wd, "dl"),
    "Russian VD typed with English keyboard layout should match WD");
Check(HeroSearch.Matches(newLargo, "ларго") &&
      !HeroSearch.Matches(newLargo, "легион") &&
      HeroSearch.Matches(newLargo, ""),
    "New heroes must match official Russian names; blank search shows all");
Check(HeroSearch.Matches(new Hero {
    Id = 1, Name = "Crystal Maiden", InternalName = "npc_dota_hero_crystal_maiden"
}, "кристалка"),
    "Common community nickname search must be supported");
Check(HeroSearch.Matches(new Hero {
    Id = 9999, Name = "Fantasy Test", InternalName = "npc_dota_hero_fantasy_test"
}, "фантасй"),
    "Automatic transliteration should cover heroes without manual aliases");
Check(HeroSearch.Matches(new Hero {
    Id = 42, Name = "Wraith King", InternalName = "npc_dota_hero_skeleton_king"
}, "вк"),
    "Community nicknames may use a different internal slug");

var ruFixture = """
{"result":{"data":{"heroes":[
  {"id":30,"name_loc":"Вич Доктор"},
  {"id":104,"name_loc":"Легион-командир"},
  {"id":999,"name_loc":"English Only"}
]}}}
""";
using (var ruDocument = JsonDocument.Parse(ruFixture))
{
    var localized = OpenDotaClient.ParseRussianHeroNames(ruDocument.RootElement);
    Check(localized.Count == 2 && localized[30] == "Вич Доктор" &&
          localized[104] == "Легион-командир",
        "Official Valve RU feed must be parsed by hero ID and exclude English fallback");
}
// UI search only falls back to a different keyboard layout when needed.
var antiMageHero = new Hero
{
    Id = 1, Name = "Anti-Mage", InternalName = "npc_dota_hero_antimage"
};
var voidHero = new Hero
{
    Id = 2, Name = "Faceless Void", InternalName = "npc_dota_hero_faceless_void"
};
var testRoster = new[] { antiMageHero, voidHero, wd, shadowDemon, legion };
var russianAm = HeroSearch.MatchingIds(testRoster, "ам");
Check(russianAm.SetEquals([1]),
    "Russian AM abbreviation must highlight Anti-Mage only, not Faceless Void through 'fv'");
Check(HeroSearch.MatchingIds(testRoster, "fv").SetEquals([2]),
    "English FV should still match Faceless Void");
Check(HeroSearch.MatchingIds(testRoster, "dl").SetEquals([30]),
    "Wrong-layout VD must still match Witch Doctor when direct matches are absent");
Check(HeroSearch.MatchingIds(testRoster, "шд").SetEquals([79]),
    "Russian SHD must match Shadow Demon only");
Check(HeroSearch.MatchingIds(testRoster, "").Count == testRoster.Length,
    "Empty search highlights all portraits");

Console.WriteLine("Dota Helper 0.9.2 portrait dimensions and keyboard-fallback tests passed.");


// 0.10 — Valve GSI parser and conservative live-draft merge tests.
var liveJson = """
{
  "map": {"game_state": "DOTA_GAMERULES_STATE_HERO_SELECTION", "matchid": 7654321},
  "player": {"team_name": "radiant"},
  "hero": {"name": "npc_dota_hero_witch_doctor"},
  "draft": {
    "team2": {
      "pick0_id": 30, "pick1_id": 104, "ban0_id": 1,
      "pick2_id": -1, "pick3_class": 22
    },
    "team3": {
      "pick0_id": 2, "pick1_id": 104, "ban0_id": 79
    }
  }
}
""";
var live = GsiDraftParser.Parse(liveJson);
Check(live.MatchId == 7654321 &&
      live.PlayerRadiant == true &&
      live.RadiantPicks.SequenceEqual([30, 104]) &&
      live.DirePicks.SequenceEqual([2, 104]) &&
      live.RadiantBans.SequenceEqual([1]) &&
      live.DireBans.SequenceEqual([79]) &&
      live.OwnHeroName == "npc_dota_hero_witch_doctor",
    "GSI must parse ranked team2/team3 pick and ban IDs without inventing hidden heroes");
var noDraft = GsiDraftParser.Parse("""
{"map":{"game_state":"DOTA_GAMERULES_STATE_HERO_SELECTION"},
 "player":{"team_name":"dire"}, "hero":{"name":"npc_dota_hero_riki"}}
""");
Check(!noDraft.HasDraft && noDraft.DirePicks.Count == 0 &&
      noDraft.PlayerRadiant == false,
    "All Pick GSI without draft fields must remain empty, not fabricated");

var liveHeroes = new Dictionary<int, Hero>
{
    [30] = wd, [104] = legion, [2] = voidHero, [1] = antiMageHero, [79] = shadowDemon
};
var liveAllies = Enumerable.Range(0, 4)
    .Select(n => new DraftSlot("A" + n)).ToList();
var liveEnemies = Enumerable.Range(0, 5)
    .Select(n => new DraftSlot("E" + n, isEnemy: true)).ToList();
liveAllies[0].SelectedHero = legion; // explicitly manually chosen
var synced = GsiDraftApplier.MergePicks(live.RadiantPicks,
    liveAllies, liveHeroes, excludeHeroId: 30);
Check(synced == 0 && liveAllies[0].SelectedHero?.Id == 104,
    "Own hero must be skipped, manual ally never overwritten");
synced = GsiDraftApplier.MergePicks(live.DirePicks, liveEnemies, liveHeroes);
Check(synced == 2 && liveEnemies[0].SelectedHero?.Id == 2 &&
      liveEnemies[1].SelectedHero?.Id == 104,
    "Only actually observed enemy picks are added into empty slots");
synced = GsiDraftApplier.MergePicks(live.DirePicks, liveEnemies, liveHeroes);
Check(synced == 0, "Repeated GSI packets must not duplicate picks or trigger recalc");
synced = GsiDraftApplier.MergePicks(noDraft.DirePicks, liveEnemies, liveHeroes);
Check(synced == 0 && liveEnemies[0].SelectedHero?.Id == 2,
    "Empty/partial GSI updates must never clear visible manual or known picks");
var matchBans = Enumerable.Range(1, 18).Select(n => new BanSlot(n)).ToList();
var seenBans = GsiDraftApplier.MergeBans(
    live.RadiantBans.Concat(live.DireBans), matchBans, liveHeroes);
Check(seenBans == 2 && matchBans[0].SelectedHero?.Id == 1 &&
      matchBans[1].SelectedHero?.Id == 79,
    "Known All Pick bans from both teams must be merged without duplicates");
Check(GsiDraftApplier.MergeBans(
    [1, 1, 79], matchBans, liveHeroes) == 0,
    "Repeated ban IDs must not create multiple ban tiles");
Check(previewSlot.PreviewText.Contains("ПРЕДПРОСМОТР"), "Without GSI the own slot remains a preview");
previewSlot.IsGsiConfirmed = true;
Check(previewSlot.PreviewText.Contains("GSI") &&
      previewSlot.TileHint.Contains("подтверждён"),
    "GSI-confirmed own pick should be labeled as confirmed, not as a suggestion");
previewSlot.IsGsiConfirmed = false;
Check(previewSlot.PreviewText.Contains("ПРЕДПРОСМОТР"),
    "The own hero preview can be restored after clearing the game");
Console.WriteLine("Dota Helper 0.10 local GSI parsing and live-draft merge tests passed.");


// 0.11 — synthetic deterministic screen recognition checks.
using (var image = new System.Drawing.Bitmap(80, 46,
    System.Drawing.Imaging.PixelFormat.Format24bppRgb))
using (var graphics = System.Drawing.Graphics.FromImage(image))
{
    graphics.Clear(System.Drawing.Color.FromArgb(22, 33, 51));
    graphics.FillRectangle(System.Drawing.Brushes.Cyan, 3, 4, 19, 29);
    graphics.FillEllipse(System.Drawing.Brushes.Gold, 30, 10, 40, 26);
    var a = PortraitRecognition.Signature(image);
    Check(a.Length == 24 * 14 && PortraitRecognition.Similarity(a, a) > .999,
        "Screen matching must preserve deterministic normalized grayscale signatures");
    var b = new Dictionary<int, double[]>
    {
        [101] = a, [102] = a.Select((v,i) => v * (i%2==0?1:-1)).ToArray()
    };
    Check(PortraitRecognition.Match(a, b)?.HeroId == 101,
        "Confident hero portrait must match the expected ID");
    Check(PortraitRecognition.Match(new double[a.Length], b) is null,
        "A solid/empty slot must never be interpreted as a hero");

    using var slash = new System.Drawing.Pen(System.Drawing.Color.FromArgb(238, 58, 88), 3);
    graphics.DrawLine(slash, 11, 35, 59, 4);
    var regions = PortraitRecognition.FindRedBanSlashes(image,
        new ScreenArea(0, 0, 80, 46));
    Check(regions.Any(r => r.Width >= 14 && r.Height >= 7),
        "Red diagonal lines over a hero gallery must be recognized as ban candidates");
}
var calibration = new DraftScreenCalibration(1920, 1080,
    new ScreenArea(60, 10, 500, 60), new ScreenArea(1200, 10, 500, 60),
    new ScreenArea(30, 100, 1600, 750));
Check(calibration.Valid && !(calibration with {
    DirePicks = new ScreenArea(2000, 30, 500, 60) }).Valid,
    "Calibration must not accept regions outside the physical display");
Console.WriteLine("Dota Helper 0.11 screenshot pick and ban recognition smoke tests passed.");


// 0.12: auto matching must never treat gray pre-picks as locked heroes.
using (var gray=new System.Drawing.Bitmap(72,56))
using (var locked=new System.Drawing.Bitmap(72,56))
{
    for(int y=0;y<56;y++)
    for(int x=0;x<72;x++)
    {
        var v=55+(x*7+y*13)%33;
        gray.SetPixel(x,y,System.Drawing.Color.FromArgb(v-10,v-5,v+5));
        locked.SetPixel(x,y,((x+y)%3==0)
            ?System.Drawing.Color.FromArgb(135,67,167)
            :System.Drawing.Color.FromArgb(70,82,141));
    }
    Check(!PortraitRecognition.IsConfirmedColor(gray),
        "Blue-gray pre-pick must never become a confirmed pick");
    Check(PortraitRecognition.IsConfirmedColor(locked),
        "Colored purple hero must be accepted after lock-in");
}
using (var top=new System.Drawing.Bitmap(1280,720))
{
    using var g=System.Drawing.Graphics.FromImage(top);
    g.Clear(System.Drawing.Color.FromArgb(18,25,37));
    for(int team=0;team<2;team++)
    for(int i=0;i<5;i++)
    {
        int x=(team==0?70:690)+i*94;
        for(int yy=12;yy<57;yy+=3)
        for(int xx=x;xx<x+77;xx+=3)
        {
            var color=((i+xx/3+yy/3)%3) switch
            {
                0=>System.Drawing.Brushes.CornflowerBlue,
                1=>System.Drawing.Brushes.OrangeRed,
                _=>System.Drawing.Brushes.DarkGoldenrod
            };
            g.FillRectangle(color,xx,yy,3,3);
        }
    }
    var found=AutoDraftLayout.Locate(top);
    Check(found is not null,
        "Two independently positioned portrait strips should be found without calibration");
    Check(!AutoDraftLayout.HasVisibleHeroGrid(top),
        "Draft bar alone must not be mistaken for a gallery of banned heroes");
}
Console.WriteLine("Dota Helper 0.12 automatic window-layout and pre-pick classification tests passed.");

// 0.12.1: a plain gray UI tile is not evidence of a hero pre-pick.
using (var blank = new System.Drawing.Bitmap(90, 58))
using (var face = new System.Drawing.Bitmap(90, 58))
{
    using (var g = System.Drawing.Graphics.FromImage(blank))
        g.Clear(System.Drawing.Color.FromArgb(49, 57, 68));
    using (var g = System.Drawing.Graphics.FromImage(face))
    {
        g.Clear(System.Drawing.Color.FromArgb(57, 62, 68));
        g.FillEllipse(System.Drawing.Brushes.LightGray, 20, 3, 50, 46);
        g.FillRectangle(System.Drawing.Brushes.Gray, 40, 9, 8, 30);
        g.DrawLine(System.Drawing.Pens.White, 10, 15, 73, 45);
        g.DrawLine(System.Drawing.Pens.Black, 7, 38, 83, 22);
    }
    Check(!PortraitRecognition.HasPortraitTexture(blank),
        "Solid gray empty HUD cells must not be counted as pre-picks");
    Check(PortraitRecognition.HasPortraitTexture(face),
        "Textured gray hero face remains eligible for identity recognition");
    Check(!PortraitRecognition.IsConfirmedColor(blank) &&
          !PortraitRecognition.IsConfirmedColor(face),
        "Gray only determines whether an identified portrait is a pre-pick");
}
// A completely empty hero strip is not a valid five-portrait layout.
using (var blank = new System.Drawing.Bitmap(1919, 1079))
{
    using var g = System.Drawing.Graphics.FromImage(blank);
    g.Clear(System.Drawing.Color.FromArgb(21, 28, 37));
    Check(AutoDraftLayout.Locate(blank) is null,
        "No top bar should be detected on a blank scene");
}
Console.WriteLine("Dota Helper 0.12.1 no-phantom-prepicks and blank-layout tests passed.");

