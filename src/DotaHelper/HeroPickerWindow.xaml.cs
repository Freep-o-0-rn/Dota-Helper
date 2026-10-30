using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotaHelper.Models;
using DotaHelper.Services;

namespace DotaHelper;

public sealed class HeroPickerTile : INotifyPropertyChanged
{
    private bool _isMatch = true;
    public HeroPickerTile(Hero hero) => Hero = hero;
    public Hero Hero { get; }
    public bool IsMatch
    {
        get => _isMatch;
        set
        {
            if (_isMatch == value) return;
            _isMatch = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMatch)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record HeroAttributeGroup(string Label, string Glyph,
    string Accent, IReadOnlyList<HeroPickerTile> Heroes);

/// <summary>
/// Same-layout, Dota-like search: never removes or rearranges the portraits.
/// As each letter is entered, nonmatching portraits become grey and unselectable.
/// </summary>
public partial class HeroPickerWindow : Window
{
    private readonly IReadOnlyList<Hero> _heroes;
    private readonly List<HeroAttributeGroup> _groups = [];
    public Hero? SelectedHero { get; private set; }

    public HeroPickerWindow(IEnumerable<Hero> heroes, string caption, Hero? selected = null)
    {
        InitializeComponent();
        Title = caption;
        _heroes = heroes.DistinctBy(h => h.Id)
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        _groups.AddRange(BuildGroups(_heroes));
        GroupList.ItemsSource = _groups;
        ApplySearch();
        Loaded += (_, _) => SearchBox.Focus();
    }

    private static IReadOnlyList<HeroAttributeGroup> BuildGroups(
        IReadOnlyList<Hero> heroes)
    {
        var known = new (string Attribute, string Label, string Glyph, string Accent)[]
        {
            ("str", "СИЛА", "◉", "#D66249"),
            ("agi", "ЛОВКОСТЬ", "◉", "#55C881"),
            ("int", "ИНТЕЛЛЕКТ", "◉", "#53C3DA"),
            ("all", "УНИВЕРСАЛЬНЫЕ", "◉", "#C6A26D"),
        };
        var groups = known.Select(x => new HeroAttributeGroup(
            x.Label, x.Glyph, x.Accent,
            heroes.Where(h => string.Equals(h.PrimaryAttribute, x.Attribute,
                StringComparison.OrdinalIgnoreCase))
                .Select(h => new HeroPickerTile(h)).ToArray())).ToList();

        var unclassified = heroes.Where(h => !known.Any(x =>
            string.Equals(x.Attribute, h.PrimaryAttribute, StringComparison.OrdinalIgnoreCase)))
            .Select(h => new HeroPickerTile(h)).ToArray();
        if (unclassified.Length > 0)
            groups.Add(new HeroAttributeGroup("БЕЗ ДАННЫХ", "?", "#92A0B0", unclassified));

        return groups;
    }

    private int ApplySearch()
    {
        var query = SearchBox.Text;
        var matches = 0;
        // Only try an alternate keyboard layout when there are no direct
        // name/nickname matches. "ам" is Anti-Mage, not Faceless Void ("fv").
        var eligible = HeroSearch.MatchingIds(_heroes, query);
        foreach (var tile in _groups.SelectMany(group => group.Heroes))
        {
            tile.IsMatch = eligible.Contains(tile.Hero.Id);
            if (tile.IsMatch) matches++;
        }
        CounterLabel.Text = string.IsNullOrWhiteSpace(query)
            ? $"{_heroes.Count} героев"
            : $"Совпадений: {matches} из {_heroes.Count}";
        return matches;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_groups.Count > 0) ApplySearch();
    }

    private void Hero_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: HeroPickerTile { IsMatch: true } tile })
            return;
        SelectedHero = tile.Hero;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedHero = null;
        DialogResult = true;
    }

    private void Window_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        // Typing works even if a portrait/button currently owns keyboard focus.
        if (SearchBox.IsKeyboardFocusWithin || string.IsNullOrEmpty(e.Text)) return;
        SearchBox.Focus();
        SearchBox.Text += e.Text;
        SearchBox.CaretIndex = SearchBox.Text.Length;
        e.Handled = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && !SearchBox.IsKeyboardFocusWithin &&
            SearchBox.Text.Length > 0)
        {
            SearchBox.Text = SearchBox.Text[..^1];
            SearchBox.Focus();
            SearchBox.CaretIndex = SearchBox.Text.Length;
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Enter || string.IsNullOrWhiteSpace(SearchBox.Text)) return;
        var matches = _groups.SelectMany(x => x.Heroes).Where(t => t.IsMatch).ToArray();
        if (matches.Length != 1) return;
        SelectedHero = matches[0].Hero;
        DialogResult = true;
        e.Handled = true;
    }
}