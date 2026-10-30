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
        var heroes = await GetCachedAsync<List<Hero>>(
            "heroes.json", "heroes", TimeSpan.FromDays(7));
        return heroes.OrderBy(h => h.Name).ToList();
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
