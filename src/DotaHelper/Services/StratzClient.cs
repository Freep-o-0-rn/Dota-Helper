using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DotaHelper.Models;

namespace DotaHelper.Services;

public sealed record PositionSnapshot(
    IReadOnlyDictionary<int, IReadOnlyList<PositionSample>> ByHero,
    string Description,
    bool IsStale);

/// <summary>
/// Actual position 1..5 match statistics via STRATZ GraphQL. The user supplies
/// a personal free token in STRATZ_API_TOKEN; the token never enters Git or logs.
/// </summary>
public sealed class StratzClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(18) };
    private readonly string _cacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaHelper", "cache");
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    // STRATZ bracketBasicIds groups two medals into one statistical bracket.
    public static string BracketName(int rank, bool professional) =>
        professional ? "DIVINE_IMMORTAL" : rank switch
        {
            1 or 2 => "HERALD_GUARDIAN",
            3 or 4 => "CRUSADER_ARCHON",
            5 or 6 => "LEGEND_ANCIENT",
            7 or 8 => "DIVINE_IMMORTAL",
            _ => "ALL"
        };

    public async Task<PositionSnapshot> GetPositionSamplesAsync(
        IReadOnlyList<Hero> heroes, int rank, bool professional)
    {
        var bracket = BracketName(rank, professional);
        Directory.CreateDirectory(_cacheDirectory);
        var path = Path.Combine(_cacheDirectory, $"stratz-positions-{bracket}.json");
        var label = bracket switch
        {
            "HERALD_GUARDIAN" => "Рекрут–Страж",
            "CRUSADER_ARCHON" => "Рыцарь–Герой",
            "LEGEND_ANCIENT" => "Легенда–Властелин",
            "DIVINE_IMMORTAL" => "Божество–Титан",
            _ => "все ранги"
        };
        PositionSnapshot FromJson(string json, bool stale) =>
            new(PositionEvidence.Index(Parse(json)),
                $"STRATZ: реальные позиции 1–5, {label}" +
                (stale ? " (устаревший кэш)" : "") +
                (professional ? "; для позиций применена выборка высокого рейтинга, НЕ только про-матчи" : ""),
                stale);

        // Cache is shared across the user's numbered installation folders.
        if (File.Exists(path) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(3))
        {
            try { return FromJson(await File.ReadAllTextAsync(path), false); }
            catch (Exception ex) when (ex is IOException or JsonException) { }
        }

        var token = Environment.GetEnvironmentVariable("STRATZ_API_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            if (File.Exists(path))
            {
                try { return FromJson(await File.ReadAllTextAsync(path), true); }
                catch (Exception ex) when (ex is IOException or JsonException) { }
            }
            throw new InvalidOperationException(
                "Для реальных позиций 1–5 нужен бесплатный STRATZ_API_TOKEN (stratz.com/api). " +
                "Без токена используй «Эксперимент» — роли будут отмечены как неподтверждённые.");
        }

        await _requestLock.WaitAsync();
        try
        {
            // Recheck cache after waiting for an earlier refresh.
            if (File.Exists(path) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(3))
                return FromJson(await File.ReadAllTextAsync(path), false);

            var ids = heroes.Select(h => h.Id).Where(id => id > 0)
                .Distinct().OrderBy(id => id).ToArray();
            if (ids.Length == 0)
                throw new InvalidOperationException("Пустой каталог героев.");
            var bracketArg = bracket == "ALL" ? "" : $", bracketBasicIds: [{bracket}]";
            var query = "query { heroStats { stats(heroIds: [" +
                string.Join(",", ids) + "]" + bracketArg +
                ", groupByPosition: true) { heroId position matchCount winCount } } }";
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "https://api.stratz.com/graphql");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.UserAgent.ParseAdd("DotaHelper/0.5");
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new InvalidOperationException(
                    "STRATZ отклонил токен. Получи новый на stratz.com/api.");
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var snapshot = FromJson(json, false); // Validate response before caching.
            if (snapshot.ByHero.Count == 0)
                throw new InvalidOperationException("STRATZ вернул пустую статистику по позициям.");
            try { await File.WriteAllTextAsync(path, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return snapshot;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException)
        {
            if (File.Exists(path))
            {
                try { return FromJson(await File.ReadAllTextAsync(path), true); }
                catch (Exception io) when (io is IOException or JsonException) { }
            }
            if (ex is InvalidOperationException) throw;
            throw new InvalidOperationException(
                "Не удалось получить позиционную статистику STRATZ.", ex);
        }
        finally { _requestLock.Release(); }
    }

    // Public so deterministic tests can validate real API schema without a token.
    public static IReadOnlyList<PositionSample> Parse(string json)
    {
        using var root = JsonDocument.Parse(json);
        if (root.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new JsonException("STRATZ вернул ошибку GraphQL.");

        var rows = root.RootElement.GetProperty("data")
            .GetProperty("heroStats").GetProperty("stats");
        if (rows.ValueKind != JsonValueKind.Array)
            throw new JsonException("STRATZ не вернул массив stats.");

        var output = new List<PositionSample>();
        foreach (var row in rows.EnumerateArray())
        {
            if (!row.TryGetProperty("position", out var p) ||
                p.ValueKind != JsonValueKind.String) continue;
            var positionString = p.GetString();
            if (positionString is null ||
                !positionString.StartsWith("POSITION_", StringComparison.Ordinal)) continue;
            if (!int.TryParse(positionString["POSITION_".Length..], out var pos) ||
                pos is < 1 or > 5) continue;
            if (!row.TryGetProperty("heroId", out var hero) || hero.ValueKind != JsonValueKind.Number ||
                !row.TryGetProperty("matchCount", out var count) || count.ValueKind != JsonValueKind.Number ||
                !row.TryGetProperty("winCount", out var wins) || wins.ValueKind != JsonValueKind.Number) continue;
            output.Add(new PositionSample(hero.GetInt32(), pos, count.GetInt32(), wins.GetInt32()));
        }
        return output;
    }
}
