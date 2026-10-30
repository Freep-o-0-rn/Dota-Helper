using System.Text;
using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Hero picker search. Official English/Russian names cover the entire roster;
/// spelling, keyboard-layout variants and gamer aliases add discoverability.
/// The search never filters the roster out of the grid; the UI only dims misses.
/// </summary>
public static class HeroSearch
{
    // Community nicknames are intentionally separate from the official names.
    // Multiple heroes can share a nickname ("ск", "бист", "войд").
    private static readonly Dictionary<string, string[]> Nicknames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["witch_doctor"] = ["вд", "вич доктор", "витч доктор", "ведьмак", "wd"],
        ["shadow_demon"] = ["шд", "шадоу демон", "sd"],
        ["legion_commander"] = ["легион", "лега", "легионка", "лк", "lc"],
        ["shadow_fiend"] = ["сф", "сфка", "невермор", "sf"],
        ["shadow_shaman"] = ["раста", "шаман", "шш"],
        ["spirit_breaker"] = ["бара", "баратрум", "сб", "sb"],
        ["anti_mage"] = ["ам", "антимаг", "магина", "am"],
        ["ancient_apparition"] = ["аппарат", "аппа", "аа", "aa"],
        ["arc_warden"] = ["арк", "варден", "зет", "ав"],
        ["axe"] = ["акс", "могул"],
        ["abaddon"] = ["абадон", "аба"],
        ["alchemist"] = ["алхимик", "алхим"],
        ["batrider"] = ["бат", "бэт", "бетрайдер"],
        ["beastmaster"] = ["бист", "рексар", "бистмастер"],
        ["bloodseeker"] = ["блад", "бс", "сикер", "blood"],
        ["bounty_hunter"] = ["бх", "баунти", "гондар", "bh"],
        ["brewmaster"] = ["панда", "брю", "пандарен"],
        ["bristleback"] = ["бристл", "брист", "еж"],
        ["broodmother"] = ["бруда", "брудка"],
        ["centaur"] = ["кент", "кентавр"],
        ["chaos_knight"] = ["цк", "хаос", "чаос", "ck", "ск"],
        ["clinkz"] = ["клинкз", "клинк", "боник", "боня"],
        ["clockwerk"] = ["клок", "клокверк", "болтозвяк"],
        ["crystal_maiden"] = ["цм", "цмка", "кристалка", "рилка", "cm"],
        ["dark_seer"] = ["дс", "дарк сир", "ds"],
        ["dark_willow"] = ["дв", "виллоу", "дарк виллоу", "dw"],
        ["dazzle"] = ["дазл", "даззл"],
        ["death_prophet"] = ["дп", "кробелус", "dp"],
        ["disruptor"] = ["дизраптор", "диз", "траля"],
        ["doom_bringer"] = ["дум", "доом", "люцифер"],
        ["drow_ranger"] = ["дровка", "дровка", "траxа", "траха", "трекса", "др"],
        ["earth_spirit"] = ["земеля", "ес", "earth spirit"],
        ["earthshaker"] = ["шейкер", "шейк", "еш", "эрсшейкер"],
        ["elder_titan"] = ["титан", "ет", "эт"],
        ["ember_spirit"] = ["эмбер", "эмберка", "эмбер спирит"],
        ["enchantress"] = ["энча", "коза", "энчантрес"],
        ["enigma"] = ["энигма", "эниг"],
        ["faceless_void"] = ["войд", "фв", "фейсслес войд", "fv"],
        ["furion"] = ["фура", "фурион", "натуральный", "проповедник", "нп", "np"],
        ["grimstroke"] = ["грим", "гримстрок"],
        ["gyrocopter"] = ["гиро", "гирокоптер"],
        ["huskar"] = ["хускар", "хуск"],
        ["invoker"] = ["инвокер", "инвок", "карл"],
        ["jakiro"] = ["джакиро", "двуглавый"],
        ["juggernaut"] = ["джагер", "джаггер", "юра", "джаг"],
        ["keeper_of_the_light"] = ["котл", "котел", "эзалор", "кoтл", "kotl"],
        ["kunkka"] = ["кунка", "адмирал"],
        ["leshrac"] = ["лешрак", "леший", "леш"],
        ["lich"] = ["лич"],
        ["life_stealer"] = ["найкс", "гуля", "наикс", "нс", "naix"],
        ["lina"] = ["лина"],
        ["lion"] = ["лион", "лайон"],
        ["lone_druid"] = ["лд", "медведь", "силла", "ld"],
        ["luna"] = ["луна"],
        ["lycan"] = ["ликан", "волк"],
        ["magnataur"] = ["магнус", "маг", "магнатавр", "автобус"],
        ["marci"] = ["марси"],
        ["medusa"] = ["медуза", "медуcа", "дуза"],
        ["meepo"] = ["мипо", "мипарь"],
        ["mirana"] = ["мирана", "потма", "потм"],
        ["monkey_king"] = ["мк", "макака", "обезьяна", "mk"],
        ["morphling"] = ["морф", "морфлинг"],
        ["muerta"] = ["муэрта", "мерта"],
        ["naga_siren"] = ["нага", "сирена"],
        ["necrolyte"] = ["некр", "некрофос", "некролит", "некрфос"],
        ["nevermore"] = ["сф", "сфка", "невермор", "sf"],
        ["night_stalker"] = ["нс", "баланар", "ночник", "ns"],
        ["nyx_assassin"] = ["никс", "нюкс", "няша"],
        ["obsidian_destroyer"] = ["од", "одшка", "аутворлд", "outworld destroyer", "od"],
        ["ogre_magi"] = ["огр", "огр маг", "огрмаг"],
        ["omniknight"] = ["омник", "омни"],
        ["oracle"] = ["оракл"],
        ["pangolier"] = ["панго", "панголин"],
        ["phantom_assassin"] = ["па", "пака", "мортра", "фантомка", "pa"],
        ["phantom_lancer"] = ["пл", "лансер", "плка", "pl"],
        ["phoenix"] = ["феникс", "петух"],
        ["primal_beast"] = ["примал", "праймал", "бист", "динозавр"],
        ["puck"] = ["пак", "пэк"],
        ["pudge"] = ["пудж", "падж", "пудге", "паджеро", "мясник", "бучер", "пудочка"],
        ["pugna"] = ["пугна"],
        ["queenofpain"] = ["квопа", "квоп", "акаша", "куин оф пейн", "qop"],
        ["razor"] = ["разор", "рейзор", "бритва"],
        ["riki"] = ["рики", "рикимару", "рик"],
        ["rubick"] = ["рубик", "рубен"],
        ["sand_king"] = ["ск", "сенд кинг", "санд кинг", "песок", "sk"],
        ["silencer"] = ["сало", "сайленсер", "сайленс"],
        ["skywrath_mage"] = ["скай", "скаймаг", "петух", "драгонус"],
        ["slardar"] = ["слардар", "селедка", "селёдка"],
        ["slark"] = ["сларк", "рыба"],
        ["snapfire"] = ["снеп", "снэп", "бабка", "снап"],
        ["sniper"] = ["снайп", "снайпер", "кардел"],
        ["spectre"] = ["спектра", "меркуриал"],
        ["storm_spirit"] = ["шторм", "сторм"],
        ["sven"] = ["свен"],
        ["techies"] = ["течис", "течки", "течка", "минер", "скви", "спун"],
        ["templar_assassin"] = ["та", "темпларка", "ланая", "ланайя", "ta"],
        ["terrorblade"] = ["тб", "террор", "иллич", "иллидан", "tb"],
        ["tidehunter"] = ["тайд", "тайдхантер", "арбуз", "левиафан"],
        ["shredder"] = ["тимбер", "тимберсо", "риззрак"],
        ["tinker"] = ["тинкер", "тинк"],
        ["tiny"] = ["тини", "камень"],
        ["treant"] = ["трент", "дерево", "руфтреллен"],
        ["troll_warlord"] = ["тролль", "трол"],
        ["tusk"] = ["таск", "тускар"],
        ["abyssal_underlord"] = ["андерлорд", "питлорд", "лорд"],
        ["undying"] = ["зомби", "андинг", "даир"],
        ["ursa"] = ["урса", "урс", "ульфсаар"],
        ["vengefulspirit"] = ["венга", "вендж"],
        ["venomancer"] = ["веник", "веном"],
        ["viper"] = ["вайпер", "випер", "токсик"],
        ["visage"] = ["визаж", "гаргулья"],
        ["void_spirit"] = ["войд спирит", "вс", "всик"],
        ["warlock"] = ["варлок", "варл", "чернокнижник"],
        ["weaver"] = ["вивер", "жук"],
        ["windrunner"] = ["вр", "врка", "виндранер", "виндка", "алерия", "wr"],
        ["winter_wyvern"] = ["виверна", "вв", "ww"],
        ["skeleton_king"] = ["вк", "врайс кинг", "леорик", "скелет", "wk"],
        ["zuus"] = ["зевс", "зеус", "зюс"]
    };

    private const string EnglishKeys = "qwertyuiop[]asdfghjkl;'zxcvbnm,./";
    private const string RussianKeys = "йцукенгшщзхъфывапролджэячсмитьбю.";

    private static readonly (string From, string To)[] Digraphs =
    [
        ("shch", "щ"), ("sch", "щ"), ("tch", "ч"), ("sh", "ш"),
        ("ch", "ч"), ("zh", "ж"), ("kh", "х"), ("ts", "ц"),
        ("th", "т"), ("ph", "ф"), ("ck", "к"), ("qu", "кв"),
        ("oo", "у"), ("ee", "и"), ("yo", "ё"), ("yu", "ю"),
        ("ya", "я"), ("ey", "ей")
    ];

    private static readonly Dictionary<char, string> Transliteration = new()
    {
        ['a']="а", ['b']="б", ['c']="к", ['d']="д", ['e']="е",
        ['f']="ф", ['g']="г", ['h']="х", ['i']="и", ['j']="дж",
        ['k']="к", ['l']="л", ['m']="м", ['n']="н", ['o']="о",
        ['p']="п", ['q']="к", ['r']="р", ['s']="с", ['t']="т",
        ['u']="у", ['v']="в", ['w']="в", ['x']="кс", ['y']="й", ['z']="з"
    };

    public static bool Matches(Hero hero, string? input)
    {
        var query = Normalize(input);
        if (query.Length == 0) return true;

        var names = new List<string>(8)
        {
            hero.Name,
            hero.InternalName.Replace("npc_dota_hero_", "", StringComparison.OrdinalIgnoreCase),
            hero.RussianName ?? "",
            EnglishToRussian(hero.Name),
        };

        var slug = hero.InternalName.Replace("npc_dota_hero_", "", StringComparison.OrdinalIgnoreCase);
        if (Nicknames.TryGetValue(slug, out var aliases))
            names.AddRange(aliases);

        var variants = new HashSet<string>(StringComparer.Ordinal)
        {
            query,
            Normalize(SwapLayout(input ?? "", EnglishKeys, RussianKeys)),
            Normalize(SwapLayout(input ?? "", RussianKeys, EnglishKeys)),
        };

        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var normalized = Normalize(name);
            if (variants.Any(v => v.Length > 0 && normalized.StartsWith(v, StringComparison.Ordinal)))
                return true;
            var tokens = name.Split([' ', '-', '_', '\'', '.'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Any(token => variants.Any(v => v.Length > 0 &&
                Normalize(token).StartsWith(v, StringComparison.Ordinal))))
                return true;
            if (tokens.Length > 1)
            {
                var acronym = string.Concat(tokens.Select(t => Normalize(t).FirstOrDefault()));
                if (variants.Contains(acronym)) return true;
            }
        }
        return false;
    }

    public static string Normalize(string? input)
    {
        var text = (input ?? "").Trim().ToLowerInvariant().Replace('ё', 'е');
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        return sb.ToString();
    }

    public static string EnglishToRussian(string english)
    {
        var s = english.ToLowerInvariant();
        foreach (var (from, to) in Digraphs)
            s = s.Replace(from, to, StringComparison.Ordinal);
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(Transliteration.TryGetValue(c, out var value) ? value : c.ToString());
        return sb.ToString();
    }

    public static string SwapLayout(string input, string from, string to)
    {
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input.ToLowerInvariant())
        {
            var index = from.IndexOf(ch);
            sb.Append(index >= 0 && index < to.Length ? to[index] : ch);
        }
        return sb.ToString();
    }
}
