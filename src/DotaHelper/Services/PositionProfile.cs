using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Conservative human-maintained candidate pools, NOT patch-specific pick/win data.
/// Heroes absent from the lists can be viewed via the Experimental pool.
/// Keys are Valve/OpenDota hero internal names, without "npc_dota_hero_" prefix.
/// </summary>
public static class PositionProfile
{
    private static readonly HashSet<string> SoftSupports = new(StringComparer.OrdinalIgnoreCase)
    {
        "abaddon", "ancient_apparition", "bane", "bounty_hunter", "chen",
        "rattletrap", "crystal_maiden", "dark_willow", "dazzle",
        "disruptor", "earth_spirit", "earthshaker", "elder_titan",
        "enchantress", "enigma", "furion", "grimstroke", "hoodwink",
        "wisp", "jakiro", "keeper_of_the_light", "leshrac", "lich",
        "lion", "marci", "mirana", "nyx_assassin", "ogre_magi",
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
        "jakiro", "keeper_of_the_light", "lich", "lion", "marci",
        "mirana", "ogre_magi", "omniknight", "oracle", "phoenix",
        "pugna", "ringmaster", "rubick", "shadow_demon",
        "shadow_shaman", "silencer", "skywrath_mage", "snapfire",
        "techies", "treant", "undying", "vengefulspirit",
        "venomancer", "warlock", "winter_wyvern", "witch_doctor"
    };

    public static bool IsStandard(Hero hero, int position)
    {
        if (position is not (4 or 5)) return true;
        const string prefix = "npc_dota_hero_";
        var name = hero.InternalName ?? "";
        var slug = name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? name[prefix.Length..]
            : name;
        return position == 4 ? SoftSupports.Contains(slug) : HardSupports.Contains(slug);
    }
}
