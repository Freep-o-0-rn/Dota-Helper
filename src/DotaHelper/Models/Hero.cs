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
    [JsonPropertyName("1_pick")] public long Picks1 { get; set; }
    [JsonPropertyName("1_win")] public long Wins1 { get; set; }
    [JsonPropertyName("2_pick")] public long Picks2 { get; set; }
    [JsonPropertyName("2_win")] public long Wins2 { get; set; }
    [JsonPropertyName("3_pick")] public long Picks3 { get; set; }
    [JsonPropertyName("3_win")] public long Wins3 { get; set; }
    [JsonPropertyName("4_pick")] public long Picks4 { get; set; }
    [JsonPropertyName("4_win")] public long Wins4 { get; set; }
    [JsonPropertyName("5_pick")] public long Picks5 { get; set; }
    [JsonPropertyName("5_win")] public long Wins5 { get; set; }
    [JsonPropertyName("6_pick")] public long Picks6 { get; set; }
    [JsonPropertyName("6_win")] public long Wins6 { get; set; }
    [JsonPropertyName("7_pick")] public long Picks7 { get; set; }
    [JsonPropertyName("7_win")] public long Wins7 { get; set; }
    [JsonPropertyName("8_pick")] public long Picks8 { get; set; }
    [JsonPropertyName("8_win")] public long Wins8 { get; set; }

    public (long Picks, long Wins) InBracket(int bracket) => bracket switch
    {
        1 => (Picks1, Wins1), 2 => (Picks2, Wins2),
        3 => (Picks3, Wins3), 4 => (Picks4, Wins4),
        5 => (Picks5, Wins5), 6 => (Picks6, Wins6),
        7 => (Picks7, Wins7),
        _ => (Picks1 + Picks2 + Picks3 + Picks4 + Picks5 + Picks6 + Picks7,
              Wins1 + Wins2 + Wins3 + Wins4 + Wins5 + Wins6 + Wins7)
    };
}

public enum HeroPoolMode { Common = 0, All = 1, Favorites = 2 }

public sealed record RankOption(int Id, string Label)
{
    public override string ToString() => Label;
}

public sealed record PoolOption(HeroPoolMode Mode, string Label)
{
    public override string ToString() => Label;
}

public sealed class DraftSlot : INotifyPropertyChanged
{
    private Hero? _selectedHero;
    private bool _isLaneOpponent;

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
    public string FavoriteMark => IsFavorite ? "★" : "☆";
    public string MetaLabel { get; init; } = "Общий WR: нет данных";
    public string PopularityLabel { get; init; } = "Популярность: нет данных";
    public string EvidenceLabel { get; init; } = "Матчапы: нет данных";
    public bool IsRare { get; init; }
    public string RareLabel => IsRare ? "Редкий выбор" : "";
}
