using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Human-maintained conservative candidate pools for ALL five positions.
/// This is a temporary compatibility safeguard, NOT patch-based evidence,
/// lane win rates, an exhaustive list, or a professional drafting tier list.
/// Off-meta assignments can be explored via Experimental mode.
/// </summary>
public static class PositionProfile
{
    private static readonly HashSet<string> Carries = new(StringComparer.OrdinalIgnoreCase)
    {
        "alchemist", "antimage", "arc_warden", "bloodseeker", "broodmother",
        "chaos_knight", "clinkz", "drow_ranger", "faceless_void",
        "gyrocopter", "juggernaut", "kez", "life_stealer", "lone_druid",
        "luna", "medusa", "meepo", "monkey_king", "morphling",
        "muerta", "naga_siren", "nevermore", "phantom_assassin",
        "phantom_lancer", "razor", "riki", "skeleton_king", "slark",
        "sniper", "spectre", "sven", "templar_assassin", "terrorblade",
        "troll_warlord", "ursa", "weaver", "windrunner"
    };

    private static readonly HashSet<string> Midlaners = new(StringComparer.OrdinalIgnoreCase)
    {
        "alchemist", "arc_warden", "batrider", "bloodseeker",
        "broodmother", "clinkz", "death_prophet", "dragon_knight",
        "ember_spirit", "huskar", "invoker", "keeper_of_the_light",
        "kunkka", "leshrac", "lina", "lone_druid", "magnataur",
        "meepo", "monkey_king", "morphling", "nevermore",
        "obsidian_destroyer", "pangolier", "primal_beast", "puck",
        "pugna", "queenofpain", "razor", "riki", "skywrath_mage",
        "sniper", "storm_spirit", "templar_assassin", "tinker",
        "tiny", "viper", "visage", "void_spirit", "windrunner", "zuus"
    };

    private static readonly HashSet<string> Offlaners = new(StringComparer.OrdinalIgnoreCase)
    {
        "abyssal_underlord", "axe", "batrider", "beastmaster",
        "brewmaster", "bristleback", "centaur", "chaos_knight",
        "dark_seer", "dawnbreaker", "death_prophet", "doom_bringer",
        "dragon_knight", "earthshaker", "elder_titan", "enigma",
        "furion", "legion_commander", "lycan", "magnataur",
        "mars", "necrolyte", "night_stalker", "omniknight",
        "pangolier", "primal_beast", "pudge", "razor",
        "sand_king", "shredder", "slardar", "spirit_breaker",
        "tidehunter", "tiny", "undying", "venomancer",
        "viper", "visage", "windrunner"
    };

    private static readonly HashSet<string> SoftSupports = new(StringComparer.OrdinalIgnoreCase)
    {
        "abaddon", "ancient_apparition", "bane", "bounty_hunter", "chen",
        "rattletrap", "crystal_maiden", "dark_willow", "dazzle",
        "disruptor", "earth_spirit", "earthshaker", "elder_titan",
        "enchantress", "enigma", "furion", "grimstroke", "hoodwink",
        "wisp", "jakiro", "keeper_of_the_light", "leshrac", "lich",
        "largo", "lion", "marci", "mirana", "nyx_assassin", "ogre_magi",
        "omniknight", "oracle", "phoenix", "pudge", "pugna",
        "ringmaster", "rubick", "shadow_demon", "shadow_shaman",
        "silencer", "skywrath_mage", "snapfire", "spirit_breaker",
        "techies", "tiny", "treant", "tusk", "undying",
        "vengefulspirit", "venomancer", "warlock", "weaver",
        "windrunner", "winter_wyvern", "witch_doctor", "zuus"
    };

    private static readonly HashSet<string> HardSupports = new(StringComparer.OrdinalIgnoreCase)
    {
        "abaddon", "ancient_apparition", "bane", "chen",
        "rattletrap", "crystal_maiden", "dark_willow", "dazzle",
        "disruptor", "enchantress", "grimstroke", "hoodwink", "wisp",
        "jakiro", "keeper_of_the_light", "largo", "lich", "lion", "marci",
        "mirana", "ogre_magi", "omniknight", "oracle", "phoenix",
        "pugna", "ringmaster", "rubick", "shadow_demon",
        "shadow_shaman", "silencer", "skywrath_mage", "snapfire",
        "techies", "treant", "undying", "vengefulspirit",
        "venomancer", "warlock", "winter_wyvern", "witch_doctor"
    };

    public static bool IsStandard(Hero hero, int position)
    {
        const string prefix = "npc_dota_hero_";
        var name = hero.InternalName ?? "";
        var slug = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;

        return position switch
        {
            1 => Carries.Contains(slug),
            2 => Midlaners.Contains(slug),
            3 => Offlaners.Contains(slug),
            4 => SoftSupports.Contains(slug),
            5 => HardSupports.Contains(slug),
            _ => false
        };
    }
}
