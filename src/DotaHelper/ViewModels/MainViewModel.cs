using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using DotaHelper.Models;
using DotaHelper.Services;

namespace DotaHelper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OpenDotaClient _api = new();
    private readonly RecommendationEngine _engine = new();
    private readonly PreferencesStore _store = new();
    private UserPreferences _preferences = new();
    private int _selectedPosition = 3;
    private int _revision;
    private string _status = "Загрузка...";
    private Recommendation? _selectedRecommendation;

    public ObservableCollection<Hero> Heroes { get; } = [];
    public ObservableCollection<DraftSlot> Allies { get; } = [];
    public ObservableCollection<DraftSlot> Enemies { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public int[] Positions { get; } = [1, 2, 3, 4, 5];

    public MainViewModel()
    {
        for (var i = 1; i <= 4; i++) Allies.Add(new DraftSlot($"Союзник {i}"));
        for (var i = 1; i <= 5; i++) Enemies.Add(new DraftSlot($"Противник {i}"));
        foreach (var slot in Allies.Concat(Enemies))
            slot.Changed += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    public int SelectedPosition
    {
        get => _selectedPosition;
        set
        {
            if (value is < 1 or > 5 || _selectedPosition == value) return;
            _selectedPosition = value;
            OnPropertyChanged();
            DraftChanged?.Invoke(this, EventArgs.Empty);
            _ = SavePreferencesAsync();
        }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public Recommendation? SelectedRecommendation
    {
        get => _selectedRecommendation;
        set { _selectedRecommendation = value; OnPropertyChanged(); }
    }

    public event EventHandler? DraftChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public async Task InitializeAsync()
    {
        _preferences = await _store.LoadAsync();
        SelectedPosition = _preferences.SelectedPosition;
        try
        {
            var heroes = await _api.GetHeroesAsync();
            foreach (var hero in heroes) Heroes.Add(hero);
            Status = $"Загружено героев: {Heroes.Count}. Укажи врагов и союзников.";
            await RecalculateAsync();
        }
        catch (Exception ex)
        {
            Status = $"Не удалось загрузить каталог героев: {ex.Message}";
        }
    }

    public void ClearSlot(DraftSlot? slot)
    {
        if (slot is null) return;
        slot.SelectedHero = null;
        slot.IsLaneOpponent = false;
    }

    public async Task ToggleFavoriteAsync()
    {
        if (SelectedRecommendation is not { } recommendation) return;
        if (!_preferences.FavoriteHeroIds.Add(recommendation.Hero.Id))
            _preferences.FavoriteHeroIds.Remove(recommendation.Hero.Id);
        await SavePreferencesAsync();
        await RecalculateAsync();
    }

    public async Task RecalculateAsync()
    {
        if (Heroes.Count == 0) return;
        var revision = ++_revision;
        var selectedEnemies = Enemies.Where(s => s.SelectedHero is not null)
            .Select(s => s.SelectedHero!.Id).Distinct().ToList();

        Status = "Рассчитываем рекомендации...";
        var results = await Task.WhenAll(selectedEnemies.Select(async enemyId =>
        {
            try { return (Id: enemyId, Data: await _api.GetMatchupsAsync(enemyId), Error: false); }
            catch { return (Id: enemyId, Data: (IReadOnlyList<Matchup>)[], Error: true); }
        }));

        if (revision != _revision) return;

        var matchups = results.Where(r => !r.Error)
            .ToDictionary(r => r.Id, r => r.Data);

        var recommendations = _engine.Rank(
            Heroes.ToList(), Allies.ToList(), Enemies.ToList(),
            SelectedPosition, _preferences.FavoriteHeroIds, matchups);

        Recommendations.Clear();
        foreach (var item in recommendations) Recommendations.Add(item);

        var unavailable = results.Count(r => r.Error);
        Status = unavailable > 0
            ? $"Рекомендации частичные: нет данных по {unavailable} противникам. Проверь соединение."
            : "Условный рейтинг, НЕ шанс победы. Матчапы OpenDota; по ролям — эвристика. Данные могут быть кэшированы.";
    }

    private async Task SavePreferencesAsync()
    {
        _preferences.SelectedPosition = SelectedPosition;
        try { await _store.SaveAsync(_preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Не удалось сохранить настройки: {ex.Message}";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
