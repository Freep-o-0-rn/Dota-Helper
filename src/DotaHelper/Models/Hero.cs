using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DotaHelper.Models;

public sealed class Hero
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string InternalName { get; set; } = "";

    [JsonPropertyName("localized_name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("roles")]
    public string[] Roles { get; set; } = [];

    public override string ToString() => Name;
}

public sealed class Matchup
{
    [JsonPropertyName("hero_id")]
    public int HeroId { get; set; }

    [JsonPropertyName("games_played")]
    public int GamesPlayed { get; set; }

    // Wins by the QUERIED hero, not its opponent.
    [JsonPropertyName("wins")]
    public int Wins { get; set; }
}

/// <summary>
/// OpenDota /heroStats pick and win counts, keyed by rank tier.
/// Tier 8 is often absent from public data and deliberately not selectable.
/// </summary>
public sealed class HeroStats
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("hero_id")] public int HeroId { get; set; }
    public int EffectiveId => Id > 0 ? Id : HeroId;
    [JsonPropertyName("1_pick")] public long? Picks1 { get; set; }
    [JsonPropertyName("1_win")] public long? Wins1 { get; set; }
    [JsonPropertyName("2_pick")] public long? Picks2 { get; set; }
    [JsonPropertyName("2_win")] public long? Wins2 { get; set; }
    [JsonPropertyName("3_pick")] public long? Picks3 { get; set; }
    [JsonPropertyName("3_win")] public long? Wins3 { get; set; }
    [JsonPropertyName("4_pick")] public long? Picks4 { get; set; }
    [JsonPropertyName("4_win")] public long? Wins4 { get; set; }
    [JsonPropertyName("5_pick")] public long? Picks5 { get; set; }
    [JsonPropertyName("5_win")] public long? Wins5 { get; set; }
    [JsonPropertyName("6_pick")] public long? Picks6 { get; set; }
    [JsonPropertyName("6_win")] public long? Wins6 { get; set; }
    [JsonPropertyName("7_pick")] public long? Picks7 { get; set; }
    [JsonPropertyName("7_win")] public long? Wins7 { get; set; }
    [JsonPropertyName("8_pick")] public long? Picks8 { get; set; }
    [JsonPropertyName("8_win")] public long? Wins8 { get; set; }
    [JsonPropertyName("pro_pick")] public long? ProPicks { get; set; }
    [JsonPropertyName("pro_win")] public long? ProWins { get; set; }

    public (long Picks, long Wins) InProMatches() => (ProPicks ?? 0, ProWins ?? 0);

    public (long Picks, long Wins) InBracket(int bracket) => bracket switch
    {
        1 => (Picks1 ?? 0, Wins1 ?? 0), 2 => (Picks2 ?? 0, Wins2 ?? 0),
        3 => (Picks3 ?? 0, Wins3 ?? 0), 4 => (Picks4 ?? 0, Wins4 ?? 0),
        5 => (Picks5 ?? 0, Wins5 ?? 0), 6 => (Picks6 ?? 0, Wins6 ?? 0),
        7 => (Picks7 ?? 0, Wins7 ?? 0),
        _ => ((Picks1 ?? 0) + (Picks2 ?? 0) + (Picks3 ?? 0) +
              (Picks4 ?? 0) + (Picks5 ?? 0) + (Picks6 ?? 0) + (Picks7 ?? 0),
              (Wins1 ?? 0) + (Wins2 ?? 0) + (Wins3 ?? 0) +
              (Wins4 ?? 0) + (Wins5 ?? 0) + (Wins6 ?? 0) + (Wins7 ?? 0))
    };
}

public enum HeroPoolMode { Common = 0, All = 1, Favorites = 2, Experimental = 3 }

public enum MetaMode { PlayerRank = 0, Professional = 1 }

public sealed record MetaOption(MetaMode Mode, string Label)
{
    public override string ToString() => Label;
}

/// <summary>OpenDota player profile contains a public medal if available.</summary>
public sealed class PlayerProfile
{
    [JsonPropertyName("rank_tier")] public int? RankTier { get; set; }
    [JsonPropertyName("leaderboard_rank")] public int? LeaderboardRank { get; set; }
    [JsonPropertyName("profile")] public PlayerIdentity? Identity { get; set; }
}

public sealed class PlayerIdentity
{
    [JsonPropertyName("personaname")] public string? Name { get; set; }
    [JsonPropertyName("account_id")] public long? AccountId { get; set; }
}

public sealed record RankOption(int Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record PoolOption(HeroPoolMode Mode, string Label)
{
    public override string ToString() => Label;
}

public sealed record EnemyPositionOption(int Position, string Label)
{
    public override string ToString() => Label;
}

public sealed class DraftSlot : INotifyPropertyChanged
{
    private Hero? _selectedHero;
    private bool _isLaneOpponent;
    private int _enemyPosition;

    public DraftSlot(string label) => Label = label;

    public string Label { get; }

    public Hero? SelectedHero
    {
        get => _selectedHero;
        set
        {
            if (ReferenceEquals(_selectedHero, value)) return;
            _selectedHero = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Enemy lane position: 1 carry, 2 mid, 3 offlane, 4/5 supports; 0 unknown.</summary>
    public int EnemyPosition
    {
        get => _enemyPosition;
        set
        {
            if (value is < 0 or > 5 || _enemyPosition == value) return;
            _enemyPosition = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsLaneOpponent
    {
        get => _isLaneOpponent;
        set
        {
            if (_isLaneOpponent == value) return;
            _isLaneOpponent = value;
            OnPropertyChanged();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Changed;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record Recommendation(
    Hero Hero,
    double Score,
    int OpponentsMeasured,
    int OpponentsTotal,
    int SampleGames,
    bool IsFavorite,
    string Explanation)
{
    public string ScoreLabel => $"{Score:F1}";
    public int PositionInRanking { get; init; }
    public string RankLabel => PositionInRanking > 0 ? $"#{PositionInRanking}" : "";
    public string ScoreBreakdown { get; init; } = "";
    public string CounterpickSummary { get; init; } = "";
    public string FavoriteMark => IsFavorite ? "★" : "☆";
    public string MetaLabel { get; init; } = "Общий WR: нет данных";
    public string PopularityLabel { get; init; } = "Популярность: нет данных";
    public string EvidenceLabel { get; init; } = "Матчапы: нет данных";
    public bool IsRare { get; init; }
    public bool IsNonStandard { get; init; }
    public string PositionLabel { get; init; } = "";
    public string RareLabel => IsRare ? "Редкий выбор" : "";
}
