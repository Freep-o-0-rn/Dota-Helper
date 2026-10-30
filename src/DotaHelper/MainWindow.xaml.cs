using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DotaHelper.Models;
using DotaHelper.ViewModels;
using DotaHelper.Services;
using Microsoft.Win32;
using System.Net.Http;

namespace DotaHelper;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private DotaGsiService? _gsi;
    private ScreenDraftReader? _screenReader;
    private readonly DispatcherTimer _screenTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2.5)
    };
    private bool _screenBusy;
    private readonly Dictionary<string, (int HeroId, int Streak)> _screenEvidence = [];
    private DateTimeOffset? _lastGsiPacket;
    private readonly DispatcherTimer _gsiHealth = new()
    {
        Interval = TimeSpan.FromSeconds(4)
    };
    private readonly DispatcherTimer _debounce = new()
    {
        Interval = TimeSpan.FromMilliseconds(450)
    };

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _viewModel.DraftChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        Closed += (_, _) =>
        {
            DisconnectGsi();
            StopScreenCapture();
            _screenReader?.Dispose();
        };
        _screenTimer.Tick += ScreenScan_Tick;
        _gsiHealth.Tick += (_, _) =>
        {
            if (_gsi is null) return;
            if (_lastGsiPacket is null)
                _viewModel.SetGsiStatus("GSI: ожидаю пакеты Dota 2. После установки конфигурации перезапусти игру.");
            else if (DateTimeOffset.UtcNow - _lastGsiPacket > TimeSpan.FromSeconds(12))
                _viewModel.SetGsiStatus("GSI: данные устарели (нет пакетов более 12 секунд). " +
                    "Проверь, запущена ли Dota 2.");
        };
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            await _viewModel.RecalculateAsync();
        };
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) =>
        await _viewModel.InitializeAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        await _viewModel.RecalculateAsync();
    }

    private void ClearSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button { Tag: DraftSlot slot })
            _viewModel.ClearSlot(slot);
    }

    private void ClearDraft_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this,
            "Очистить выбранных союзников и противников? Настройки и избранное сохранятся.",
            "Очистка драфта", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
        {
            _debounce.Stop();
            _viewModel.ClearDraft();
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e) =>
        _viewModel.ClearRecommendationSearch();

    private void PickHero_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: DraftSlot slot } || slot.IsPreview) return;
        var picker = new HeroPickerWindow(
            _viewModel.Heroes, slot.IsEnemy ? "Пик противника" : "Пик союзника",
            slot.SelectedHero) { Owner = this };
        if (picker.ShowDialog() == true)
            slot.SelectedHero = picker.SelectedHero;
    }

    private void PickBan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: BanSlot slot }) return;
        var picker = new HeroPickerWindow(
            _viewModel.Heroes, "Забаненный герой", slot.SelectedHero)
        { Owner = this };
        if (picker.ShowDialog() == true)
            slot.SelectedHero = picker.SelectedHero;
    }

    private void ClearBans_Click(object sender, RoutedEventArgs e)
    {
        if (System.Windows.MessageBox.Show(this, "Очистить список банов?",
            "Очистка банов", MessageBoxButton.YesNo, MessageBoxImage.Question)
            == MessageBoxResult.Yes)
            _viewModel.ClearBans();
    }

    private void ConnectGsi_Click(object sender, RoutedEventArgs e)
    {
        if (_gsi is not null)
        {
            DisconnectGsi();
            return;
        }

        DotaGsiService? service = null;
        try
        {
            // This is an explicit user action: write only our own GSI cfg,
            // never modify the game's binaries or other integrations.
            var dotaRoot = DotaGsiService.FindDotaRoot();
            if (dotaRoot is null)
            {
                var picker = new OpenFolderDialog
                {
                    Title = "Выбери папку dota 2 beta (где находится game\\dota\\cfg)",
                    Multiselect = false
                };
                if (picker.ShowDialog(this) != true) return;
                dotaRoot = picker.FolderName;
            }

            service = new DotaGsiService();
            var activeService = service;
            service.Updated += state => Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_gsi, activeService)) return;
                _lastGsiPacket = DateTimeOffset.UtcNow;
                _viewModel.ApplyGsiDraft(state);
            });
            service.Start();
            var file = service.InstallConfig(dotaRoot);
            _gsi = service;
            _lastGsiPacket = null;
            GsiButton.Content = "Отключить GSI";
            _gsiHealth.Start();
            _viewModel.SetGsiStatus("GSI включён. Конфигурация установлена: " +
                file + ". Перезапусти Dota 2 для получения событий.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            System.IO.IOException or UnauthorizedAccessException or
            System.Net.HttpListenerException or System.IO.DirectoryNotFoundException)
        {
            service?.Dispose();
            _viewModel.SetGsiStatus("GSI: не удалось подключить: " + ex.Message);
            System.Windows.MessageBox.Show(this, ex.Message, "Dota Helper: GSI",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void DisconnectGsi()
    {
        _gsiHealth.Stop();
        _lastGsiPacket = null;
        _gsi?.Dispose();
        _gsi = null;
        GsiButton.Content = "Подключить Dota 2";
        _viewModel.SetGsiStatus("GSI отключён. Ручной драфт доступен.");
    }

    private async void CalibrateScreen_Click(object sender, RoutedEventArgs e)
    {
        _screenTimer.Stop();
        _screenEvidence.Clear();
        _viewModel.SetScreenStatus("Экран: создание снимка. Dota 2 должна быть открыта на экране выбора.");

        try
        {
            // Hide helper before taking a screenshot, so its own UI never
            // pollutes the hero grid/portrait calibration.
            Hide();
            await Task.Delay(550);
            using var screenshot = ScreenDraftReader.CapturePrimary();
            Show();
            Activate();
            var dialog = new ScreenCalibrationWindow(screenshot) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Calibration is null)
            {
                _viewModel.SetScreenStatus("Экран: настройка отменена.");
                return;
            }
            _screenReader ??= new ScreenDraftReader();
            _screenReader.SaveCalibration(dialog.Calibration);
            _viewModel.SetScreenStatus("Экран: области сохранены. Теперь нажми «Начать сканирование».");
        }
        catch (Exception ex) when (ex is System.IO.IOException or
            UnauthorizedAccessException or InvalidOperationException or
            ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            _viewModel.SetScreenStatus("Ошибка снимка: " + ex.Message);
        }
        finally
        {
            if (!IsVisible) { Show(); Activate(); }
        }
    }

    private async void ToggleScreen_Click(object sender, RoutedEventArgs e)
    {
        if (_screenTimer.IsEnabled)
        {
            StopScreenCapture();
            return;
        }
        if (_screenBusy) return;
        _screenReader ??= new ScreenDraftReader();
        if (_screenReader.Calibration is null)
        {
            _viewModel.SetScreenStatus("Экран: сначала нажми «Настроить области» и выдели три зоны.");
            return;
        }
        if (_viewModel.Heroes.Count == 0)
        {
            _viewModel.SetScreenStatus("Экран: каталог героев ещё не загружен.");
            return;
        }

        _screenBusy = true;
        ScreenStartButton.IsEnabled = false;
        try
        {
            _viewModel.SetScreenStatus("Экран: загружаю эталонные портреты Valve…");
            var progress = new Progress<int>(n =>
            {
                if (_screenBusy && n % 15 == 0)
                    _viewModel.SetScreenStatus($"Экран: загружено {n} портретов.");
            });
            var count = await _screenReader.LoadPortraitsAsync(_viewModel.Heroes, progress);
            if (count < 60)
                throw new InvalidOperationException(
                    $"Загружено только {count} эталонов героев. Проверь доступ к Steam CDN.");
            _screenEvidence.Clear();
            _screenTimer.Start();
            ScreenStartButton.Content = "Остановить сканирование";
            _viewModel.SetScreenStatus(
                $"Экран: активен, {count} эталонов. Переключись в Dota 2 (оконный без рамки).");
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            HttpRequestException or System.IO.IOException or UnauthorizedAccessException)
        {
            _viewModel.SetScreenStatus("Экран: " + ex.Message);
        }
        finally
        {
            _screenBusy = false;
            ScreenStartButton.IsEnabled = true;
        }
    }

    private void StopScreenCapture()
    {
        _screenTimer.Stop();
        _screenEvidence.Clear();
        ScreenStartButton.Content = "Начать сканирование";
        _viewModel.SetScreenStatus("Экран: остановлен. Ручной драфт и GSI доступны.");
    }

    private RecognizedHero? ConfirmScreenHit(string key, RecognizedHero? hit)
    {
        if (hit is null)
        {
            _screenEvidence.Remove(key);
            return null;
        }
        if (_screenEvidence.TryGetValue(key, out var previous) &&
            previous.HeroId == hit.HeroId)
            _screenEvidence[key] = (hit.HeroId, previous.Streak + 1);
        else
            _screenEvidence[key] = (hit.HeroId, 1);
        // Consecutive stable scans reduce accidental UI false positives.
        return _screenEvidence[key].Streak >= 2 ? hit : null;
    }

    private async void ScreenScan_Tick(object? sender, EventArgs e)
    {
        if (_screenBusy || _screenReader?.Calibration is not { } calibration)
            return;
        if (!ScreenDraftReader.IsDotaForeground())
        {
            _viewModel.SetScreenStatus(
                "Экран: ожидаю активное окно Dota 2 — снимки других программ не анализируются.");
            return;
        }

        _screenBusy = true;
        try
        {
            var reader = _screenReader;
            var frame = await Task.Run(() =>
            {
                using var bmp = ScreenDraftReader.CapturePrimary();
                return reader.Analyze(bmp, calibration);
            });
            var radiant = frame.RadiantPicks
                .Select((h,i) => ConfirmScreenHit($"r{i}", h)).ToArray();
            var dire = frame.DirePicks
                .Select((h,i) => ConfirmScreenHit($"d{i}", h)).ToArray();
            var bans = frame.BannedHeroes
                .Where(h => ConfirmScreenHit($"b{h.HeroId}", h) is not null)
                .ToArray();

            var observation = new ScreenDraftObservation(radiant, dire, bans);
            var updated = _viewModel.ApplyScreenObservation(observation);
            _viewModel.SetScreenStatus(
                $"Экран: Radiant {radiant.Count(h=>h is not null)}/5, " +
                $"Dire {dire.Count(h=>h is not null)}/5; " +
                $"подтверждено перечёркнутых героев {bans.Length}. " +
                (updated > 0 ? $"Добавлено {updated}." :
                "Нет новых уверенных совпадений."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or
            ArgumentException or System.Runtime.InteropServices.ExternalException)
        {
            _viewModel.SetScreenStatus("Экран: " + ex.Message);
            _screenTimer.Stop();
            ScreenStartButton.Content = "Начать сканирование";
        }
        finally { _screenBusy = false; }
    }

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleFavoriteAsync();

    private async void CheckStratz_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.CheckStratzConnectionAsync();

    private async void LoadProfile_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.LoadPlayerProfileAsync();
}
