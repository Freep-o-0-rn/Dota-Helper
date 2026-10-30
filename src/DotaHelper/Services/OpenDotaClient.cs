using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Read-only OpenDota API client. Disk cache works across numbered installations.
/// </summary>
public sealed class OpenDotaClient
{
    private static readonly HttpClient Http = CreateClient();
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaHelper", "cache");

    private readonly SemaphoreSlim _requestGate = new(2, 2);
    private readonly ConcurrentDictionary<int, Task<IReadOnlyList<Matchup>>> _inflight = new();

    public async Task<IReadOnlyList<Hero>> GetHeroesAsync()
    {
        var byId = new Dictionary<int, Hero>();
        Exception? openDotaError = null;
        try
        {
            // Shorter TTL so recently introduced heroes are not hidden for a week.
            var heroes = await GetCachedAsync<List<Hero>>(
                "heroes.json", "heroes", TimeSpan.FromDays(1));
            foreach (var hero in heroes.Where(h => h.Id > 0))
                byId[hero.Id] = hero;
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            openDotaError = ex;
        }

        try
        {
            // Valve's authoritative roster supplements OpenDota's catalog.
            // Valve feed is key-free; it does not supply position-specific win rates.
            using var doc = await GetCachedAsync<JsonDocument>(
                "valve-herolist.json",
                "https://www.dota2.com/datafeed/herolist?language=english",
                TimeSpan.FromDays(1));
            var list = doc.RootElement
                .GetProperty("result").GetProperty("data").GetProperty("heroes");
            foreach (var item in list.EnumerateArray())
            {
                var id = item.GetProperty("id").GetInt32();
                if (id <= 0) continue;
                var internalName = item.GetProperty("name").GetString() ?? "";
                var displayName = item.TryGetProperty("name_loc", out var loc)
                    ? loc.GetString() : null;
                if (string.IsNullOrWhiteSpace(displayName) &&
                    item.TryGetProperty("name_english_loc", out var en))
                    displayName = en.GetString();
                displayName ??= internalName.Replace("npc_dota_hero_", "");

                if (byId.TryGetValue(id, out var existing))
                {
                    if (string.IsNullOrWhiteSpace(existing.Name))
                        existing.Name = displayName;
                    if (string.IsNullOrWhiteSpace(existing.InternalName))
                        existing.InternalName = internalName;
                }
                else
                {
                    // Brand-new heroes have unknown statistical role tags,
                    // but can still appear via explicit position profiles.
                    byId[id] = new Hero
                    {
                        Id = id, InternalName = internalName, Name = displayName,
                        Roles = []
                    };
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException
            or KeyNotFoundException or HttpRequestException)
        {
            if (byId.Count == 0)
                throw new InvalidOperationException(
                    "Не удалось загрузить героев из OpenDota или Valve.", ex);
        }

        if (byId.Count == 0)
            throw new InvalidOperationException(
                "Каталог героев пуст.", openDotaError);
        return byId.Values.OrderBy(h => h.Name).ToList();
    }

    public async Task<PlayerProfile> GetPlayerProfileAsync(long accountId)
    {
        if (accountId <= 0 || accountId > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(accountId), "Нужен Steam account_id (32-bit).");

        return await GetCachedAsync<PlayerProfile>(
            $"player-{accountId}.json", $"players/{accountId}", TimeSpan.FromMinutes(30));
    }

    public async Task<IReadOnlyList<HeroStats>> GetHeroStatsAsync() =>
        await GetCachedAsync<List<HeroStats>>(
            "hero-stats.json", "heroStats", TimeSpan.FromHours(3));

    public async Task<IReadOnlyList<Matchup>> GetMatchupsAsync(int enemyHeroId)
    {
        // Only one request per selected enemy even with concurrent recalculations.
        var task = _inflight.GetOrAdd(enemyHeroId, FetchMatchupsAsync);
        try { return await task; }
        finally
        {
            if (_inflight.TryGetValue(enemyHeroId, out var current) && ReferenceEquals(current, task))
                _inflight.TryRemove(enemyHeroId, out _);
        }
    }

    private async Task<IReadOnlyList<Matchup>> FetchMatchupsAsync(int heroId) =>
        await GetCachedAsync<List<Matchup>>(
            $"matchups-{heroId}.json",
            $"heroes/{heroId}/matchups",
            TimeSpan.FromHours(24));

    private async Task<T> GetCachedAsync<T>(string fileName, string endpoint, TimeSpan lifetime)
        where T : class
    {
        Directory.CreateDirectory(_cacheDirectory);
        var path = Path.Combine(_cacheDirectory, fileName);

        async Task<T?> ReadCacheAsync()
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path));
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < lifetime)
        {
            var cached = await ReadCacheAsync();
            if (cached is not null) return cached;
        }

        try
        {
            await _requestGate.WaitAsync();
            try
            {
                using var response = await Http.GetAsync(endpoint);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<T>(json)
                             ?? throw new JsonException("Пустой ответ OpenDota.");
                try { await File.WriteAllTextAsync(path, json); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Keep the fresh network result even if caching fails.
                }
                return result;
            }
            finally { _requestGate.Release(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return await ReadCacheAsync()
                   ?? throw new InvalidOperationException(
                       "OpenDota недоступна, а локального кэша нет.", ex);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            BaseAddress = new Uri("https://api.opendota.com/api/"),
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaHelper/0.2");
        return client;
    }
}
