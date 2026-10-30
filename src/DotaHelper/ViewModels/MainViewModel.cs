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
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private UserPreferences _preferences = new();
    private IReadOnlyDictionary<int, HeroStats> _heroStats = new Dictionary<int, HeroStats>();
    private bool _statsAvailable;
    private int _selectedPosition = 3;
    private int _revision;
    private string _status = "Загрузка...";
    private Recommendation? _selectedRecommendation;
    private RankOption? _selectedRank;
    private PoolOption? _selectedPool;
    private MetaOption? _selectedMeta;
    private string _playerInput = "";
    private string _profileSummary = "Укажи свой Steam Account ID или SteamID64.";

    private string _heroSearchText = "";

    public ObservableCollection<Hero> Heroes { get; } = [];
    public ObservableCollection<DraftSlot> Allies { get; } = [];
    public ObservableCollection<DraftSlot> Enemies { get; } = [];
    public ObservableCollection<Recommendation> Recommendations { get; } = [];
    public ObservableCollection<Recommendation> VisibleRecommendations { get; } = [];

    public string HeroSearchText
    {
        get => _heroSearchText;
        set
        {
            if (_heroSearchText == value) return;
            _heroSearchText = value ?? "";
            OnPropertyChanged();
            ApplyRecommendationSearch();
        }
    }

    public string ResultsLabel => $"Показано {VisibleRecommendations.Count} из {Recommendations.Count} подходящих героев";

    private void ApplyRecommendationSearch()
    {
        var text = HeroSearchText.Trim();
        var filtered = string.IsNullOrEmpty(text)
            ? Recommendations.ToList()
            : Recommendations.Where(r =>
                r.Hero.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                r.Hero.InternalName.Contains(text, StringComparison.OrdinalIgnoreCase)).ToList();

        VisibleRecommendations.Clear();
        foreach (var result in filtered)
            VisibleRecommendations.Add(result);
        OnPropertyChanged(nameof(ResultsLabel));
    }
    public int[] Positions { get; } = [1, 2, 3, 4, 5];

    public IReadOnlyList<EnemyPositionOption> EnemyPositionOptions { get; } =
    [
        new(0, "Роль неизвестна"), new(1, "1 · Керри"),
        new(2, "2 · Мид"), new(3, "3 · Оффлейн"),
        new(4, "4 · Саппорт"), new(5, "5 · Саппорт")
    ];

    public IReadOnlyList<MetaOption> MetaOptions { get; } =
    [
        new(MetaMode.PlayerRank, "Мой рейтинг"),
        new(MetaMode.Professional, "Профессиональная мета")
    ];

    public MetaOption? SelectedMeta
    {
        get => _selectedMeta;
        set
        {
            if (value is null || Equals(_selectedMeta, value)) return;
            _selectedMeta = value;
            OnPropertyChanged();
            DraftChanged?.Invoke(this, EventArgs.Empty);
            _ = SavePreferencesAsync();
        }
    }

    public string PlayerInput
    {
        get => _playerInput;
        set
        {
            if (_playerInput == value) return;
            _playerInput = value ?? "";
            OnPropertyChanged();
        }
    }

    public string ProfileSummary
    {
        get => _profileSummary;
        private set { _profileSummary = value; OnPropertyChanged(); }
    }

    public async Task LoadPlayerProfileAsync()
    {
        var raw = PlayerInput.Trim();
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            raw = uri.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault() ?? "";

        if (!ulong.TryParse(raw, out var numeric))
        {
            ProfileSummary = "Неверный ID. Введи Account ID, SteamID64 или числовую ссылку на профиль.";
            return;
        }

        const ulong steam64Base = 76561197960265728UL;
        if (numeric >= steam64Base) numeric -= steam64Base;
        if (numeric == 0 || numeric > uint.MaxValue)
        {
            ProfileSummary = "ID вне допустимого диапазона Steam Account ID.";
            return;
        }

        var accountId = (long)numeric;
        try
        {
            ProfileSummary = "Получаем публичный рейтинг...";
            var profile = await _api.GetPlayerProfileAsync(accountId);
            _preferences.PlayerAccountId = accountId;
            PlayerInput = accountId.ToString();
            var rankTier = profile.RankTier ?? 0;
            var playerName = string.IsNullOrWhiteSpace(profile.Identity?.Name)
                ? $"ID {accountId}" : profile.Identity.Name;
            if (rankTier >= 10 && rankTier < 80)
            {
                var bracket = rankTier / 10;
                SelectedRank = RankOptions.First(o => o.Id == bracket);
                ProfileSummary = $"{playerName}: ранг {SelectedRank.Label} (медаль {rankTier}).";
            }
            else if (rankTier >= 80)
            {
                SelectedRank = RankOptions.First(o => o.Id == 8);
                ProfileSummary = $"{playerName}: Титан (медаль {rankTier}). " +
                    "Статистика этого ранга может быть недоступна в OpenDota.";
            }
            else
            {
                ProfileSummary = $"{playerName}: публичная медаль недоступна, ранг выбери вручную.";
            }
            await SavePreferencesAsync();
        }
        catch (Exception ex)
        {
            ProfileSummary = $"Не удалось загрузить профиль: {ex.Message}";
        }
    }


    public IReadOnlyList<RankOption> RankOptions { get; } =
    [
        new(0, "Все доступные ранги"),
        new(1, "Рекрут"), new(2, "Страж"), new(3, "Рыцарь"),
        new(4, "Герой"), new(5, "Легенда"),
        new(6, "Властелин"), new(7, "Божество"), new(8, "Титан")
    ];
    public IReadOnlyList<PoolOption> PoolOptions { get; } =
    [
        new(HeroPoolMode.All, "По ролям"),
        new(HeroPoolMode.Experimental, "Эксперимент")
    ];

    public MainViewModel()
    {
        for (var i = 1; i <= 4; i++) Allies.Add(new DraftSlot($"Союзник {i}"));
        for (var i = 1; i <= 5; i++) Enemies.Add(new DraftSlot($"Противник {i}"));
        foreach (var slot in Allies.Concat(Enemies))
            slot.Changed += (_, _) => DraftChanged?.Invoke(this, EventArgs.Empty);
        _selectedRank = RankOptions[0];
        _selectedPool = PoolOptions[0];
        _selectedMeta = MetaOptions[0];
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
        SelectedPool = _preferences.SelectedPoolMode == (int)HeroPoolMode.Experimental
            ? PoolOptions[1] : PoolOptions[0];
        SelectedMeta = MetaOptions.FirstOrDefault(x => (int)x.Mode == _preferences.MetaMode)
                       ?? MetaOptions[0];
        if (_preferences.PlayerAccountId is > 0)
            PlayerInput = _preferences.PlayerAccountId.Value.ToString();

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
            foreach (var hero in Heroes)
            {
                if (_heroStats.TryGetValue(hero.Id, out var stat) &&
                    !string.IsNullOrWhiteSpace(stat.ImagePath))
                    hero.ImagePath = stat.ImagePath;
            }
        }
        catch
        {
            _statsAvailable = false;
        }
        if (_preferences.PlayerAccountId is > 0)
            await LoadPlayerProfileAsync();
        await RecalculateAsync();
    }

    public void ClearSlot(DraftSlot? slot)
    {
        if (slot is null) return;
        slot.SelectedHero = null;
        slot.IsLaneOpponent = false;
        slot.EnemyPosition = 0;
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
            SelectedRank?.Id ?? 0, SelectedPool?.Mode ?? HeroPoolMode.All,
            SelectedMeta?.Mode == MetaMode.Professional);

        Recommendations.Clear();
        foreach (var item in ranked) Recommendations.Add(item);
        ApplyRecommendationSearch();
        SelectedRecommendation = priorSelectionId.HasValue
            ? VisibleRecommendations.FirstOrDefault(r => r.Hero.Id == priorSelectionId.Value)
            : null;

        var unavailable = results.Count(r => r.Error);
        var proMode = SelectedMeta?.Mode == MetaMode.Professional;
        var baselinePresent = _heroStats.Values.Any(x =>
            proMode ? x.InProMatches().Picks > 0 : x.InBracket(SelectedRank?.Id ?? 0).Picks > 0);
        if (Recommendations.Count == 0)
        {
            Status = "Нет подходящих героев для выбранной роли; попробуй «Эксперимент».";
            return;
        }
        Status = !_statsAvailable || !baselinePresent
            ? "Выбранная мета: статистика WR недоступна. Контрпики — общие данные лиг OpenDota."
            : unavailable > 0
                ? $"Недоступны матчапы {unavailable} врагов. Контрпики рассчитаны частично."
                : "0.4: контрпики всех врагов; приоритет 1–3. WR по всем ролям; матчапы OpenDota — лиги.";
    }

    private async Task SavePreferencesAsync()
    {
        // Multiple selection changes can arrive almost simultaneously.
        // Serialize writes to prevent conflicting preferences.json.tmp moves.
        await _saveGate.WaitAsync();
        try
        {
            _preferences.SelectedPosition = SelectedPosition;
            _preferences.SelectedRankBracket = SelectedRank?.Id ?? 0;
            _preferences.SelectedPoolMode = (int)(SelectedPool?.Mode ?? HeroPoolMode.All);
            _preferences.MetaMode = (int)(SelectedMeta?.Mode ?? MetaMode.PlayerRank);
            await _store.SaveAsync(_preferences);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"Не удалось сохранить настройки: {ex.Message}";
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
