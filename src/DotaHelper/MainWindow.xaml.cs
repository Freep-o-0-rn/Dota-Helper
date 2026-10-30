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

    private async void ToggleFavorite_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.ToggleFavoriteAsync();

    private async void CheckStratz_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.CheckStratzConnectionAsync();

    private async void LoadProfile_Click(object sender, RoutedEventArgs e) =>
        await _viewModel.LoadPlayerProfileAsync();
}
