using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DotaHelper.Models;
using DotaHelper.ViewModels;
using DotaHelper.Services;
using Microsoft.Win32;

namespace DotaHelper;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
    private DotaGsiService? _gsi;
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
        Closed += (_, _) => DisconnectGsi();
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
        if (sender is Button { Tag: DraftSlot slot })
            _viewModel.ClearSlot(slot);
    }

    private void ClearDraft_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
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
        if (sender is not Button { Tag: DraftSlot slot } || slot.IsPreview) return;
        var picker = new HeroPickerWindow(
            _viewModel.Heroes, slot.IsEnemy ? "Пик противника" : "Пик союзника",
            slot.SelectedHero) { Owner = this };
        if (picker.ShowDialog() == true)
            slot.SelectedHero = picker.SelectedHero;
    }

    private void PickBan_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BanSlot slot }) return;
        var picker = new HeroPickerWindow(
            _viewModel.Heroes, "Забаненный герой", slot.SelectedHero)
        { Owner = this };
        if (picker.ShowDialog() == true)
            slot.SelectedHero = picker.SelectedHero;
    }

    private void ClearBans_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Очистить список банов?",
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
            MessageBox.Show(this, ex.Message, "Dota Helper: GSI",
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

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleFavoriteAsync();

    private async void CheckStratz_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.CheckStratzConnectionAsync();

    private async void LoadProfile_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.LoadPlayerProfileAsync();
}
