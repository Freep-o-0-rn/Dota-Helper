using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// Anonymous OpenDota API client. Cached responses reduce API traffic;
/// stale cached responses are used if the network is unavailable.
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

    public async Task<IReadOnlyList<Matchup>> GetMatchupsAsync(int enemyHeroId)
    {
        // Each opposing hero needs one request, never one request per candidate.
        var task = _inflight.GetOrAdd(enemyHeroId, FetchMatchupsAsync);
        try
        {
            return await task;
        }
        finally
        {
            if (_inflight.TryGetValue(enemyHeroId, out var current) && ReferenceEquals(current, task))
                _inflight.TryRemove(enemyHeroId, out _);
        }
    }

    private async Task<IReadOnlyList<Matchup>> FetchMatchupsAsync(int heroId)
    {
        var results = await GetCachedAsync<List<Matchup>>(
            $"matchups-{heroId}.json",
            $"heroes/{heroId}/matchups",
            TimeSpan.FromHours(24));
        return results;
    }

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
                var json = await File.ReadAllTextAsync(path);
                return JsonSerializer.Deserialize<T>(json);
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
                catch (IOException) { /* Network data can still be used. */ }
                return result;
            }
            finally
            {
                _requestGate.Release();
            }
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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DotaHelper/0.1");
        return client;
    }
}
