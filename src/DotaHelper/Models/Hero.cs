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

    // OpenDota reports victories of the QUERIED hero against HeroId.
    [JsonPropertyName("wins")]
    public int Wins { get; set; }
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
    public string ScoreLabel => $"{Score:F1} / 100";
    public string FavoriteMark => IsFavorite ? "★" : "☆";
}
