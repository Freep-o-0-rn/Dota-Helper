using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotaHelper.Models;

namespace DotaHelper;

/// <summary>Virtualized, searchable original Valve-portrait hero picker.</summary>
public partial class HeroPickerWindow : Window
{
    private readonly List<Hero> _heroes;
    public Hero? SelectedHero { get; private set; }

    public HeroPickerWindow(IEnumerable<Hero> heroes, string caption, Hero? selected = null)
    {
        InitializeComponent();
        Title = caption;
        _heroes = heroes.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyFilter();
        if (selected is not null)
            HeroList.SelectedItem = _heroes.FirstOrDefault(h => h.Id == selected.Id);
        Loaded += (_, _) => SearchBox.Focus();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var matching = string.IsNullOrWhiteSpace(query)
            ? _heroes
            : _heroes.Where(h =>
                h.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                h.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        HeroList.ItemsSource = matching;
        if (!string.IsNullOrEmpty(query) && matching.Count > 0)
            HeroList.SelectedIndex = 0;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Choose()
    {
        if (HeroList.SelectedItem is not Hero hero) return;
        SelectedHero = hero;
        DialogResult = true;
    }

    private void Choose_Click(object sender, RoutedEventArgs e) => Choose();
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedHero = null;
        DialogResult = true;
    }

    private void HeroList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Choose();

    private void HeroList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Choose();
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SearchBox.IsKeyboardFocused)
        {
            Choose();
            e.Handled = true;
        }
    }
}
