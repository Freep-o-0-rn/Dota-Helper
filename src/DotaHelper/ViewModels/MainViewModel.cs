using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
using DotaHelper.Models;
using DotaHelper.Services;

namespace DotaHelper.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly OpenDotaClient _api = new();
    private readonly StratzClient _positions = new();
    private readonly DraftSlot _previewSlot = new("ТВОЙ ПИК", isPreview: true);
    private DraftModeOption? _selectedDraftMode;
    private TeamSideOption? _selectedSide;
    private readonly PatchInfoClient _patches = new();
    private readonly SteamProfileResolver _steam = new();
    private readonly RecommendationEngine _engine = new();
    private readonly PreferencesStore _store = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private UserPreferences _preferences = new();
    private IReadOnlyDictionary<int, HeroStats> _heroStats = new Dictionary<int, HeroStats>();
    private bool _statsAvailable;
    private int _selectedPosition = 3;
    private int _revision;
    private string _status = "Загрузка...";
    private string _patchDisplay = "Патч: проверка Valve · данные STRATZ исторические";
    private Recommendation? _selectedRecommendation;
    private RankOption? _selectedRank;
    private PoolOption? _selectedPool;
    private MetaOption? _selectedMeta;
    private string _playerInput = "";
    private string _profileSummary = "Укажи SteamID64 или ссылку steamcommunity.com/id/...";

    private string _heroSearchText = "";

    public ObservableCollection<Hero> Heroes { get; } = [];
    public ObservableCollection<DraftSlot> Allies { get; } = [];
    public ObservableCollection<DraftSlot> Enemies { get; } = [];
    public ObservableCollection<BanSlot> AllPickBans { get; } = [];
    public ObservableCollection<BanSlot> RadiantCaptainBans { get; } = [];
    public ObservableCollection<BanSlot> DireCaptainBans { get; } = [];

    public IReadOnlyList<DraftModeOption> DraftModes { get; } =
    [
        new(DraftGameMode.AllPick, "All Pick"),
        new(DraftGameMode.CaptainsMode, "Captains Mode")
    ];
    public IReadOnlyList<TeamSideOption> TeamSides { get; } =
    [
        new(true, "Radiant"),
        new(false, "Dire")
    ];

    public DraftModeOption? SelectedDraftMode
    {
        get => _selectedDraftMode;
        set
        {
            if (value is null || Equals(_selectedDraftMode, value)) return;
            _selectedDraftMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsAllPick));
            OnPropertyChanged(nameof(IsCaptainsMode));
            OnPropertyChanged(nameof(ActiveBans));
            OnPropertyChanged(nameof(BanSummary));
            DraftChanged?.Invoke(this, EventArgs.Empty);
            _ = SavePreferencesAsync();
        }
    }
    public TeamSideOption? SelectedSide
    {
        get => _selectedSide;
        set
        {
            if (value is null || Equals(_selectedSide, value)) return;
            _selectedSide = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(RadiantSlots));
            OnPropertyChanged(nameof(DireSlots));
            OnPropertyChanged(nameof(RadiantAreAllies));
            OnPropertyChanged(nameof(DireAreAllies));
            DraftChanged?.Invoke(this, EventArgs.Empty);
            _ = SavePreferencesAsync();
        }
    }

    public bool IsAllPick => SelectedDraftMode?.Mode != DraftGameMode.CaptainsMode;
    public bool IsCaptainsMode => !IsAllPick;
    public bool RadiantAreAllies => SelectedSide?.IsRadiant != false;
    public bool DireAreAllies => !RadiantAreAllies;

    public IReadOnlyList<DraftSlot> RadiantSlots => RadiantAreAllies
        ? Allies.Append(_previewSlot).ToList() : Enemies.ToList();
    public IReadOnlyList<DraftSlot> DireSlots => DireAreAllies
        ? Allies.Append(_previewSlot).ToList() : Enemies.ToList();

    public IEnumerable<BanSlot> ActiveBans => IsAllPick
        ? AllPickBans : RadiantCaptainBans.Concat(DireCaptainBans);
    public string BanSummary => IsAllPick
        ? $"{AllPickBans.Count(b => b.SelectedHero is not null)}/{AllPickBans.Count} (общие)"
        : $"{RadiantCaptainBans.Count(b => b.SelectedHero is not null)}/7 + " +
          $"{DireCaptainBans.Count(b => b.SelectedHero is not null)}/7";

    public void ClearBans()
    {
        foreach (var slot in ActiveBans)
            slot.SelectedHero = null;
    }

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
    public string AlliesSummary => $"{Allies.Count(s => s.SelectedHero is not null)}/4";
    public string EnemiesSummary => $"{Enemies.Count(s => s.SelectedHero is not null)}/5";

    public void ClearRecommendationSearch() => HeroSearchText = "";

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

    public async Task CheckStratzConnectionAsync()
    {
        Status = "Проверка соединения STRATZ...";
        Status = await _positions.CheckConnectionAsync();
    }

    public string ProfileSummary
    {
        get => _profileSummary;
        private set { _profileSummary = value; OnPropertyChanged(); }
    }

    public async Task LoadPlayerProfileAsync()
    {
        try
        {
            ProfileSummary = "Определяем Steam ID и публичный рейтинг...";
            var accountId = await _steam.ResolveAsync(PlayerInput);
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
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
            or HttpRequestException or TaskCanceledException)
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
        for (var i = 1; i <= 5; i++) Enemies.Add(new DraftSlot($"Противник {i}", isEnemy: true));
        // Matchwide bans in All Pick have no team attribution.
        // Their displayed capacity follows the historically used 18-slot total,
        // not the 4 preferences entered by a single player.
        for (var i = 1; i <= 18; i++) AllPickBans.Add(new BanSlot(i));
        for (var i = 1; i <= 7; i++)
        {
            RadiantCaptainBans.Add(new BanSlot(i));
            DireCaptainBans.Add(new BanSlot(i));
        }
        foreach (var ban in AllPickBans.Concat(RadiantCaptainBans).Concat(DireCaptainBans))
            ban.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(BanSummary));
                DraftChanged?.Invoke(this, EventArgs.Empty);
            };
        foreach (var slot in Allies.Concat(Enemies))
            slot.Changed += (_, _) =>
            {
                OnPropertyChanged(nameof(AlliesSummary));
                OnPropertyChanged(nameof(EnemiesSummary));
                DraftChanged?.Invoke(this, EventArgs.Empty);
            };
        _selectedRank = RankOptions[0];
        _selectedPool = PoolOptions[0];
        _selectedMeta = MetaOptions[0];
        _selectedDraftMode = DraftModes[0];
        _selectedSide = TeamSides[0];
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

    public string PatchDisplay
    {
        get => _patchDisplay;
        private set { _patchDisplay = value; OnPropertyChanged(); }
    }

    public string Status
    {
        get => _status;
        private set { _status = value; OnPropertyChanged(); }
    }

    public Recommendation? SelectedRecommendation
    {
        get => _selectedRecommendation;
        set
        {
            _selectedRecommendation = value;
            _previewSlot.SelectedHero = value?.Hero;
            OnPropertyChanged();
        }
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
        SelectedDraftMode = DraftModes.FirstOrDefault(x => x.Mode == _preferences.DraftMode)
            ?? DraftModes[0];
        SelectedSide = TeamSides.FirstOrDefault(x => x.IsRadiant == _preferences.PlayingRadiant)
            ?? TeamSides[0];
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
        // Valve's patch list is an independent status indicator. We do not
        // use its patch number to relabel historical STRATZ aggregates.
        try
        {
            var patch = await _patches.GetCurrentAsync();
            PatchDisplay = patch.DisplayLabel + " · STRATZ: несколько патчей";
        }
        catch (Exception)
        {
            PatchDisplay = "Патч Valve не определён · STRATZ: исторические данные";
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

    public void ClearDraft()
    {
        foreach (var slot in Allies.Concat(Enemies))
            ClearSlot(slot);
        ClearBans();
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
        var selectedAllies = Allies.Where(s => s.SelectedHero is not null)
            .Select(s => s.SelectedHero!.Id).Distinct().ToList();

        Status = "Получение позиций и матчапов по выбранному рангу...";

        var rank = SelectedRank?.Id ?? 0;
        var professional = SelectedMeta?.Mode == MetaMode.Professional;

        // Query position data in parallel with enemy counter data; each source
        // caches by bracket. On STRATZ failure, OpenDota is a labelled fallback.
        var positionTask = _positions.GetPositionSamplesAsync(
            Heroes.ToList(), rank, professional);
        var counterTasks = selectedEnemies.Select(async enemyId =>
        {
            try
            {
                var (data, stale) = await _positions.GetMatchupsAsync(
                    enemyId, rank, professional);
                return (Id: enemyId, Data: data,
                    Origin: stale ? "STRATZ-кэш" : "STRATZ", Error: false);
            }
            catch
            {
                try
                {
                    var data = await _api.GetMatchupsAsync(enemyId);
                    return (Id: enemyId, Data: data,
                        Origin: "OpenDota (резерв)", Error: false);
                }
                catch
                {
                    return (Id: enemyId, Data: (IReadOnlyList<Matchup>)[],
                        Origin: "Недоступно", Error: true);
                }
            }
        }).ToArray();
        var allyTasks = selectedAllies.Select(async allyId =>
        {
            try
            {
                var (rows, stale) = await _positions.GetSynergiesAsync(
                    allyId, rank, professional);
                return (Id: allyId, Rows: rows,
                    Origin: stale ? "STRATZ-кэш" : "STRATZ", Error: false);
            }
            catch
            {
                // Unlike enemy matchups, OpenDota has no matching ally
                // synergy endpoint. Missing samples are neutral.
                return (Id: allyId, Rows: (IReadOnlyList<Matchup>)[],
                    Origin: "Недоступно", Error: true);
            }
        }).ToArray();

        var results = await Task.WhenAll(counterTasks);
        var alliesFound = await Task.WhenAll(allyTasks);
        if (revision != _revision) return;
        var synergies = alliesFound.Where(r => !r.Error)
            .ToDictionary(r => r.Id, r => r.Rows);
        var synergySources = alliesFound.Where(r => !r.Error)
            .ToDictionary(r => r.Id, r => r.Origin);
        var matchups = results.Where(r => !r.Error)
            .ToDictionary(r => r.Id, r => r.Data);
        var matchupSources = results.Where(r => !r.Error)
            .ToDictionary(r => r.Id, r => r.Origin);

        PositionSnapshot? observed = null;
        string? positionError = null;
        try
        {
            observed = await positionTask;
        }
        catch (Exception ex)
        {
            positionError = ex.Message;
        }
        if (revision != _revision) return;

        var priorSelectionId = SelectedRecommendation?.Hero.Id;
        // Banned heroes cannot be recommended. Keep position/counter/synergy
        // scoring unchanged, then filter only the ban selection.
        var bannedIds = ActiveBans.Where(s => s.SelectedHero is not null)
            .Select(s => s.SelectedHero!.Id).ToHashSet();
        var ranked = _engine.Rank(
            Heroes.ToList(), Allies.ToList(), Enemies.ToList(), SelectedPosition,
            _preferences.FavoriteHeroIds, matchups, _heroStats,
            SelectedRank?.Id ?? 0, SelectedPool?.Mode ?? HeroPoolMode.All,
            SelectedMeta?.Mode == MetaMode.Professional,
            observed?.ByHero, matchupSources, synergies, synergySources)
            .Where(r => !bannedIds.Contains(r.Hero.Id))
            .Select((r, index) => r with { PositionInRanking = index + 1 })
            .ToList();

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
            Status = positionError is not null
                ? "По ролям: " + positionError
                : "Нет достаточной статистики STRATZ по выбранной позиции. " +
                  "Можно выбрать «Эксперимент», но позиции не будут подтверждены.";
            return;
        }
        if (positionError is not null)
        {
            Status = "ВНИМАНИЕ: позиционная статистика недоступна. " +
                positionError + " Контрпики OpenDota — отдельная выборка лиг.";
            return;
        }
        var info = observed?.Description ?? "Нет позиционной статистики";
        var synergiesAvailable = alliesFound.Count(r => !r.Error);
        var sources = selectedEnemies.Count == 0 ? "Врагов нет"
            : $"Контрпики: STRATZ {results.Count(r => r.Origin.StartsWith("STRATZ"))}" +
              $", OpenDota {results.Count(r => r.Origin.StartsWith("OpenDota"))}";
        Status = $"{info}. {sources}. " +
            (selectedAllies.Count > 0
                ? $"Синергии STRATZ: {synergiesAvailable}/{selectedAllies.Count} союзников. "
                : "") +
            (unavailable > 0 ? $"Неизвестные матчапы: {unavailable}. " : "") +
            (!_statsAvailable || !baselinePresent ? "Общая мета недоступна. " : "") +
            "Роли и матчапы сгруппированы по рангу; фильтр текущего патча не применяется.";
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
            _preferences.DraftMode = SelectedDraftMode?.Mode ?? DraftGameMode.AllPick;
            _preferences.PlayingRadiant = SelectedSide?.IsRadiant != false;
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
