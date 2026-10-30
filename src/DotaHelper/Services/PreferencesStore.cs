using System.IO;
using System.Text.Json;

namespace DotaHelper.Services;

public sealed class UserPreferences
{
    public int SelectedPosition { get; set; } = 3;
    public HashSet<int> FavoriteHeroIds { get; set; } = [];
}

public sealed class PreferencesStore
{
    private readonly string _path = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DotaHelper", "preferences.json");

    public async Task<UserPreferences> LoadAsync()
    {
        try
        {
            if (!File.Exists(_path)) return new UserPreferences();
            var json = await File.ReadAllTextAsync(_path);
            return JsonSerializer.Deserialize<UserPreferences>(json) ?? new UserPreferences();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UserPreferences();
        }
    }

    public async Task SaveAsync(UserPreferences settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        await File.WriteAllTextAsync(temp,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, overwrite: true);
    }
}
