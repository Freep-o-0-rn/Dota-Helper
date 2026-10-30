using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DotaHelper.Models;

namespace DotaHelper;

public sealed record HeroAttributeGroup(string Label, string Glyph,
    string Accent, IReadOnlyList<Hero> Heroes);

/// <summary>
/// Compact four-column gallery with Valve hero portraits, ordered by attribute.
/// Raw OpenDota primary_attr metadata is never inferred from a manual role list.
/// </summary>
public partial class HeroPickerWindow : Window
{
    private readonly IReadOnlyList<Hero> _heroes;
    public Hero? SelectedHero { get; private set; }

    public HeroPickerWindow(IEnumerable<Hero> heroes, string caption, Hero? selected = null)
    {
        InitializeComponent();
        Title = caption;
        _heroes = heroes.OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        ApplyFilter();
        Loaded += (_, _) => SearchBox.Focus();
    }

    public static IReadOnlyList<HeroAttributeGroup> GroupHeroes(
        IEnumerable<Hero> heroes, string query = "")
    {
        var filtered = heroes.Where(h =>
            string.IsNullOrWhiteSpace(query) ||
            h.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            h.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

        var known = new (string Attr, string Label, string Glyph, string Accent)[]
        {
            ("str", "СИЛА", "◉", "#D66249"),
            ("agi", "ЛОВКОСТЬ", "◉", "#55C881"),
            ("int", "ИНТЕЛЛЕКТ", "◉", "#53C3DA"),
            ("all", "УНИВЕРСАЛЬНЫЕ", "◉", "#C6A26D"),
        };
        var groups = known.Select(x => new HeroAttributeGroup(x.Label, x.Glyph, x.Accent,
            filtered.Where(h => string.Equals(h.PrimaryAttribute, x.Attr,
                StringComparison.OrdinalIgnoreCase)).ToList())).ToList();
        var unknown = filtered.Where(h => !known.Any(x =>
            string.Equals(x.Attr, h.PrimaryAttribute, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (unknown.Count > 0)
            groups.Add(new HeroAttributeGroup("БЕЗ ДАННЫХ", "?", "#92A0B0", unknown));
        return groups;
    }

    private void ApplyFilter()
    {
        var matches = GroupHeroes(_heroes, SearchBox.Text.Trim());
        GroupList.ItemsSource = matches;
        CounterLabel.Text = $"{matches.Sum(x => x.Heroes.Count)} героев";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void Hero_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: Hero hero }) return;
        SelectedHero = hero;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        SelectedHero = null;
        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
        }
    }
}