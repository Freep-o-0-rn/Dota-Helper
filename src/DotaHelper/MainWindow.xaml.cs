using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using DotaHelper.Models;
using DotaHelper.ViewModels;

namespace DotaHelper;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();
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

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleFavoriteAsync();

    private async void CheckStratz_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.CheckStratzConnectionAsync();

    private async void LoadProfile_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.LoadPlayerProfileAsync();
}
