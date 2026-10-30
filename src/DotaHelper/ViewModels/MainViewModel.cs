using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
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
    private IReadOnlyDictionary<int, HeroStats> _heroStats = new Dictionary<int, HeroStats>();
    private bool _statsAvailable;
    private int _selectedPosition = 3;
    private int _revision;
    private string _status = "Загрузка...";
    private Recommendation? _selectedRecommendation;
    private RankOption? _selectedRank;
    private PoolOption? _selectedPool;

    public ObservableCollection<Hero> Heroes { get; } = [];
    public ObservableCollection<DraftSlot> Allies { get; } = [];
    public ObservableCollection<DraftSlot> Enemies { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public int[] Positions { get; } = [1, 2, 3, 4, 5];

    public IReadOnlyList<RankOption> RankOptions { get; } =
    [
        new(0, "Все доступные ранги"),
        new(1, "Рекрут"), new(2, "Страж"), new(3, "Рыцарь"),
        new(4, "Герой"), new(5, "Легенда"),
        new(6, "Властелин"), new(7, "Божество")
    ];
    public IReadOnlyList<PoolOption> PoolOptions { get; } =
    [
        new(HeroPoolMode.Common, "По роли, без редких"),
        new(HeroPoolMode.All, "Все подходящие по роли"),
        new(HeroPoolMode.Favorites, "Избранные по роли"),
        new(HeroPoolMode.Experimental, "Эксперимент: все герои")
    ];

    public MainViewModel()
    {
        for (var i = 1; i <= 4; i++) Allies.Add(new DraftSlot($"Союзник {i}"));
        for (var i = 1; i <= 5; i++) Enemies.Add(new DraftSlot($"Противник {i}"));
        foreach (var slot in Allies.Concat(Enemies))
            slot.Changed += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        _selectedRank = RankOptions[0];
        _selectedPool = PoolOptions[0];
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

    public RankOption? SelectedRank
    {
        get => _selectedRank;
        set
        {
            if (value is null || Equals(_selectedRank, value)) return;
            _selectedRank = value;
            OnPropertyChanged();
            DraftChanged?.Invoke(this, EventArgs.Empty);
            _ = SavePreferencesAsync();
        }
    }

    public PoolOption? SelectedPool
    {
        get => _selectedPool;
        set
        {
            if (value is null || Equals(_selectedPool, value)) return;
            _selectedPool = value;
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
        SelectedRank = RankOptions.FirstOrDefault(x => x.Id == _preferences.SelectedRankBracket)
                       ?? RankOptions[0];
        SelectedPool = PoolOptions.FirstOrDefault(x => (int)x.Mode == _preferences.SelectedPoolMode)
                       ?? PoolOptions[0];

        try
        {
            var heroes = await _api.GetHeroesAsync();
            foreach (var hero in heroes) Heroes.Add(hero);
        }
        catch (Exception ex)
        {
            Status = $"Не удалось загрузить каталог героев: {ex.Message}";
            return;
        }

        try
        {
            var stats = await _api.GetHeroStatsAsync();
            _heroStats = stats.Where(s => s.EffectiveId > 0)
                .GroupBy(s => s.EffectiveId)
                .ToDictionary(g => g.Key, g => g.First());
            _statsAvailable = _heroStats.Count > 0;
        }
        catch
        {
            _statsAvailable = false;
        }
        await RecalculateAsync();
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

        Status = "Расчёт рекомендаций...";
        var results = await Task.WhenAll(selectedEnemies.Select(async enemyId =>
        {
            try { return (Id: enemyId, Data: await _api.GetMatchupsAsync(enemyId), Error: false); }
            catch { return (Id: enemyId, Data: (IReadOnlyList<Matchup>)[], Error: true); }
        }));
        if (revision != _revision) return;

        var matchups = results.Where(r => !r.Error)
            .ToDictionary(r => r.Id, r => r.Data);
        var priorSelectionId = SelectedRecommendation?.Hero.Id;
        var ranked = _engine.Rank(
            Heroes.ToList(), Allies.ToList(), Enemies.ToList(), SelectedPosition,
            _preferences.FavoriteHeroIds, matchups, _heroStats,
            SelectedRank?.Id ?? 0, SelectedPool?.Mode ?? HeroPoolMode.Common);

        Recommendations.Clear();
        foreach (var item in ranked) Recommendations.Add(item);
        SelectedRecommendation = priorSelectionId.HasValue
            ? Recommendations.FirstOrDefault(r => r.Hero.Id == priorSelectionId.Value)
            : null;

        var unavailable = results.Count(r => r.Error);
        if (Recommendations.Count == 0)
        {
            Status = SelectedPool?.Mode == HeroPoolMode.Favorites
                ? "Нет подходящих избранных героев. Измени пул или отметь своих героев."
                : "Нет подходящих героев с выбранными настройками.";
            return;
        }
        Status = !_statsAvailable
            ? "Внимание: /heroStats недоступен. Популярность и общий WR отсутствуют; оценки предварительные."
            : unavailable > 0
                ? $"Недоступны матчапы для {unavailable} врагов. Показаны частичные данные."
                : "0.3: роли 1–5 — ручные пулы; WR по всем ролям. Патч, синергии и статистика линий не учтены.";
    }

    private async Task SavePreferencesAsync()
    {
        _preferences.SelectedPosition = SelectedPosition;
        _preferences.SelectedRankBracket = SelectedRank?.Id ?? 0;
        _preferences.SelectedPoolMode = (int)(SelectedPool?.Mode ?? HeroPoolMode.Common);
        try { await _store.SaveAsync(_preferences); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Не удалось сохранить настройки: {ex.Message}";
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
