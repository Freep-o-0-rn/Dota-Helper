using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace DotaHelper.Services;

/// <summary>
/// Patch version reported by Valve's public patchnoteslist endpoint.
/// This does NOT make STRATZ historical match aggregates patch-specific.
/// </summary>
public sealed record PatchSnapshot(string Number, DateTimeOffset ReleasedAt,
    bool FromStaleCache)
{
    public string DisplayLabel => $"Патч {Number} ({ReleasedAt:dd.MM.yyyy})" +
        (FromStaleCache ? " · кэш" : "");
}

public sealed class PatchInfoClient
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(10)
    };

    private readonly string _cacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaHelper", "cache", "valve-patchnotes-list.json");

    public static PatchSnapshot Parse(string json, bool stale = false)
    {
        using var doc = JsonDocument.Parse(json);
        var list = doc.RootElement.GetProperty("patches");
        if (list.ValueKind != JsonValueKind.Array)
            throw new JsonException("Valve patch list is not an array.");

        PatchSnapshot? latest = null;
        foreach (var p in list.EnumerateArray())
        {
            if (!p.TryGetProperty("patch_number", out var number) ||
                number.ValueKind != JsonValueKind.String ||
                !p.TryGetProperty("patch_timestamp", out var ts) ||
                ts.ValueKind != JsonValueKind.Number ||
                !ts.TryGetInt64(out var unix)) continue;
            var name = number.GetString() ?? "";
            if (string.IsNullOrWhiteSpace(name) || name.Length > 16 ||
                !name.All(c => char.IsAsciiLetterOrDigit(c) || c == '.')) continue;
            DateTimeOffset date;
            try { date = DateTimeOffset.FromUnixTimeSeconds(unix); }
            catch (ArgumentOutOfRangeException) { continue; }
            if (date > DateTimeOffset.UtcNow.AddDays(2)) continue;
            if (latest is null || date > latest.ReleasedAt)
                latest = new PatchSnapshot(name, date, stale);
        }
        return latest ?? throw new JsonException(
            "Valve patchnoteslist has no valid patch entries.");
    }

    public async Task<PatchSnapshot> GetCurrentAsync()
    {
        var dir = Path.GetDirectoryName(_cacheFile)!;
        Directory.CreateDirectory(dir);
        async Task<PatchSnapshot?> Read(bool stale)
        {
            try
            {
                if (!File.Exists(_cacheFile)) return null;
                return Parse(await File.ReadAllTextAsync(_cacheFile), stale);
            }
            catch (Exception ex) when (ex is IOException or JsonException
                or UnauthorizedAccessException) { return null; }
        }

        if (File.Exists(_cacheFile) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(_cacheFile) < TimeSpan.FromHours(12))
        {
            var fresh = await Read(false);
            if (fresh is not null) return fresh;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://www.dota2.com/datafeed/patchnoteslist?language=english");
            using var response = await Http.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var text = await response.Content.ReadAsStringAsync();
            var parsed = Parse(text);
            try { await File.WriteAllTextAsync(_cacheFile, text); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return parsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException)
        {
            var cached = await Read(true);
            if (cached is not null) return cached;
            throw new InvalidOperationException(
                "Список патчей Valve недоступен (статистика STRATZ продолжает работать).", ex);
        }
    }
}
