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

    /// <summary>
    /// Remove common copy/paste wrappers, never log or display the secret.
    /// Accept either a raw token or text copied as "Bearer <token>".
    /// </summary>
    public static string NormalizeToken(string? raw)
    {
        var token = (raw ?? "").Trim().Trim('"', '\'').Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = token["Bearer ".Length..].Trim().Trim('"', '\'').Trim();
        return token;
    }

    /// <summary>
    /// Differentiate credential rejection from an upstream protection challenge.
    /// Only returns diagnostic labels; response bodies and secrets are never logged.
    /// </summary>
    public static string DescribeHttpError(HttpStatusCode status, string? contentType,
        string? responseBody, string? cfMitigated = null)
    {
        var code = (int)status;
        var cloudflareChallenge =
            string.Equals(cfMitigated, "challenge", StringComparison.OrdinalIgnoreCase) ||
            (responseBody?.Contains("cf-chl", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (responseBody?.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ?? false) ||
            (responseBody?.Contains("Attention Required", StringComparison.OrdinalIgnoreCase) ?? false);
        if (status == HttpStatusCode.Forbidden && cloudflareChallenge)
            return "HTTP 403: запрос заблокирован проверкой Cloudflare ДО проверки токена. " +
                   "Токен может быть действительным. Проверь доступ из браузера и обратись в поддержку STRATZ.";
        if (status == HttpStatusCode.Forbidden &&
            (contentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false))
            return "HTTP 403: доступ запрещён HTML-защитой сайта (возможно Cloudflare). " +
                   "Это НЕ подтверждение неправильного токена.";
        if (status == HttpStatusCode.Forbidden)
            return "HTTP 403: STRATZ запретил доступ. Возможны ограничения токена, IP-адреса " +
                   "или защита сайта. Причину нужно уточнить, токен не обязательно неверный.";
        if (status == HttpStatusCode.Unauthorized)
            return "HTTP 401: STRATZ не принимает авторизацию. Проверь, что скопирован " +
                   "полный API-токен без лишних символов и что он ещё действителен.";
        if (code == 429)
            return "HTTP 429: превышен лимит запросов STRATZ; повтори проверку позднее.";
        if (code is 400 or 422)
            return $"HTTP {code}: STRATZ отклонил формат запроса или GraphQL-схему, а не обязательно токен.";
        if (code >= 500)
            return $"HTTP {code}: временная ошибка сервера STRATZ.";
        return $"HTTP {code}: запрос STRATZ завершился ошибкой; токен не проверен.";
    }

    private static HttpRequestMessage CreateGraphQlRequest(string token, string query)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.stratz.com/graphql");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.ParseAdd("STRATZ_API");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task CheckHttpResponseAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync();
        response.Headers.TryGetValues("cf-mitigated", out var mitigated);
        response.Headers.TryGetValues("cf-ray", out var rays);
        var info = DescribeHttpError(response.StatusCode,
            response.Content.Headers.ContentType?.MediaType, body, mitigated?.FirstOrDefault());
        // CF ray identifies only the edge request, not the user's credential.
        var ray = rays?.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(ray) && ray.Length <= 64)
            info += $" CF-RAY: {ray}.";
        throw new InvalidOperationException(info);
    }

    public async Task<string> CheckConnectionAsync()
    {
        var token = NormalizeToken(Environment.GetEnvironmentVariable("STRATZ_API_TOKEN"));
        if (string.IsNullOrWhiteSpace(token))
            return "STRATZ_API_TOKEN не найден в переменных окружения текущего процесса.";
        try
        {
            using var request = CreateGraphQlRequest(token, "query { __typename }");
            using var response = await Http.SendAsync(request);
            await CheckHttpResponseAsync(response);
            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
                return "STRATZ ответил на запрос, но вернул ошибку GraphQL (HTTP 200).";
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("__typename", out _))
                return "STRATZ ответил, но структура ответа неожиданная.";
            return "Соединение STRATZ: HTTP 200, токен принят. Проверка доступа успешна.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException
            or TaskCanceledException or JsonException)
        {
            // Exception from CheckHttpResponseAsync never contains token or body.
            return ex is InvalidOperationException ? ex.Message :
                $"Не удалось связаться с STRATZ ({ex.GetType().Name}). Проверь сеть и доступ к API.";
        }
    }


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

        var token = NormalizeToken(Environment.GetEnvironmentVariable("STRATZ_API_TOKEN"));
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
            using var request = CreateGraphQlRequest(token, query);
            using var response = await Http.SendAsync(request);
            await CheckHttpResponseAsync(response);
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


    private readonly SemaphoreSlim _matchupGate = new(2, 2);

    /// <summary>
    /// STRATZ heroStats.matchUp: each row counts wins by the queried enemy.
    /// Only bracketBasicIds is filtered (two adjacent medals). This query
    /// is NOT patch-specific or position-specific. Results are disk-cached.
    /// </summary>
    public async Task<(IReadOnlyList<Matchup> Rows, bool Stale)> GetMatchupsAsync(
        int enemyHeroId, int rank, bool professional)
    {
        if (enemyHeroId <= 0) throw new ArgumentOutOfRangeException(nameof(enemyHeroId));
        var bracket = BracketName(rank, professional);
        var cacheFile = Path.Combine(_cacheDirectory,
            $"stratz-matchup-{enemyHeroId}-{bracket}.json");
        Directory.CreateDirectory(_cacheDirectory);

        async Task<(IReadOnlyList<Matchup> Rows, bool Stale)?> ReadCacheAsync(bool stale)
        {
            try
            {
                if (!File.Exists(cacheFile)) return null;
                var result = ParseMatchups(await File.ReadAllTextAsync(cacheFile), enemyHeroId);
                if (result.Count == 0) return null;
                return (result, stale);
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (File.Exists(cacheFile) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(6))
        {
            var existing = await ReadCacheAsync(false);
            if (existing.HasValue) return existing.Value;
        }

        var token = NormalizeToken(Environment.GetEnvironmentVariable("STRATZ_API_TOKEN"));
        if (string.IsNullOrWhiteSpace(token))
        {
            var offline = await ReadCacheAsync(true);
            if (offline.HasValue) return offline.Value;
            throw new InvalidOperationException("Нет STRATZ_API_TOKEN для матчапов.");
        }

        await _matchupGate.WaitAsync();
        try
        {
            if (File.Exists(cacheFile) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(cacheFile) < TimeSpan.FromHours(6))
            {
                var cached = await ReadCacheAsync(false);
                if (cached.HasValue) return cached.Value;
            }

            var bracketFilter = bracket == "ALL" ? "" : $", bracketBasicIds: [{bracket}]";
            var query = "query { heroStats { matchUp(heroId: " +
                enemyHeroId + bracketFilter +
                ", take: 200) { vs { heroId2 winCount matchCount } } } }";
            using var request = CreateGraphQlRequest(token, query);
            using var response = await Http.SendAsync(request);
            await CheckHttpResponseAsync(response);
            var json = await response.Content.ReadAsStringAsync();
            var parsed = ParseMatchups(json, enemyHeroId);
            if (parsed.Count == 0)
                throw new InvalidOperationException("STRATZ не вернул достоверных матчапов.");
            try { await File.WriteAllTextAsync(cacheFile, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return (parsed, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException)
        {
            var cached = await ReadCacheAsync(true);
            if (cached.HasValue) return cached.Value;
            if (ex is InvalidOperationException) throw;
            throw new InvalidOperationException("Запрос матчапов STRATZ недоступен.", ex);
        }
        finally
        {
            _matchupGate.Release();
        }
    }

    /// <summary>
    /// Parses STRATZ GraphQL heroStats.matchUp, preserving enemy wins.
    /// Returns empty list for legitimate "no matches" results.
    /// </summary>
    public static IReadOnlyList<Matchup> ParseMatchups(string json, int queriedHeroId)
    {
        using var root = JsonDocument.Parse(json);
        if (root.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new JsonException("STRATZ вернул ошибку GraphQL matchUp.");
        if (!root.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("heroStats", out var stats) ||
            !stats.TryGetProperty("matchUp", out var groups) ||
            groups.ValueKind != JsonValueKind.Array)
            throw new JsonException("Некорректный ответ STRATZ matchUp.");

        var aggregate = new Dictionary<int, Matchup>();
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("vs", out var vs) ||
                vs.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in vs.EnumerateArray())
            {
                if (!row.TryGetProperty("heroId2", out var idField) ||
                    !row.TryGetProperty("matchCount", out var countField) ||
                    !row.TryGetProperty("winCount", out var winsField) ||
                    idField.ValueKind != JsonValueKind.Number ||
                    countField.ValueKind != JsonValueKind.Number ||
                    winsField.ValueKind != JsonValueKind.Number) continue;
                var id = idField.GetInt32();
                var games = countField.GetInt32();
                var wins = winsField.GetInt32();
                if (id <= 0 || id == queriedHeroId || games < 20 ||
                    wins < 0 || wins > games) continue;

                // Multiple groups may describe the same opponent. Prefer
                // the largest sample, rather than double-counting overlap.
                if (!aggregate.TryGetValue(id, out var prev) ||
                    prev.GamesPlayed < games)
                    aggregate[id] = new Matchup
                    {
                        HeroId = id, GamesPlayed = games, Wins = wins
                    };
            }
        }
        return aggregate.Values.ToArray();
    }


    /// <summary>
    /// STRATZ matchUp.with: observed results when the queried hero and
    /// candidate are teammates. No invented synergy when data unavailable.
    /// </summary>
    public async Task<(IReadOnlyList<Matchup> Rows, bool Stale)> GetSynergiesAsync(
        int allyHeroId, int rank, bool professional)
    {
        if (allyHeroId <= 0) throw new ArgumentOutOfRangeException(nameof(allyHeroId));
        var bracket = BracketName(rank, professional);
        Directory.CreateDirectory(_cacheDirectory);
        var path = Path.Combine(_cacheDirectory, $"stratz-synergy-{allyHeroId}-{bracket}.json");

        async Task<(IReadOnlyList<Matchup>, bool)?> ReadCache(bool stale)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var parsed = ParseSynergies(await File.ReadAllTextAsync(path), allyHeroId);
                return parsed.Count > 0 ? (parsed, stale) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        if (File.Exists(path) &&
            DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(6))
        {
            var cached = await ReadCache(false);
            if (cached.HasValue) return cached.Value;
        }

        var token = NormalizeToken(Environment.GetEnvironmentVariable("STRATZ_API_TOKEN"));
        if (string.IsNullOrWhiteSpace(token))
        {
            var cached = await ReadCache(true);
            if (cached.HasValue) return cached.Value;
            throw new InvalidOperationException("Нет STRATZ_API_TOKEN для синергий.");
        }

        // Shared gate prevents too many concurrent STRATZ GraphQL requests.
        await _matchupGate.WaitAsync();
        try
        {
            if (File.Exists(path) &&
                DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromHours(6))
            {
                var cached = await ReadCache(false);
                if (cached.HasValue) return cached.Value;
            }
            var filter = bracket == "ALL" ? "" : $", bracketBasicIds: [{bracket}]";
            var query = "query { heroStats { matchUp(heroId: " +
                allyHeroId + filter +
                ", take: 200) { with { heroId2 winCount matchCount } } } }";
            using var request = CreateGraphQlRequest(token, query);
            using var response = await Http.SendAsync(request);
            await CheckHttpResponseAsync(response);
            var json = await response.Content.ReadAsStringAsync();
            var result = ParseSynergies(json, allyHeroId);
            if (result.Count == 0)
                throw new InvalidOperationException("STRATZ не вернул статистику совместных игр.");
            try { await File.WriteAllTextAsync(path, json); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return (result, false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or JsonException or InvalidOperationException)
        {
            var cached = await ReadCache(true);
            if (cached.HasValue) return cached.Value;
            if (ex is InvalidOperationException) throw;
            throw new InvalidOperationException("Не удалось получить синергии STRATZ.", ex);
        }
        finally { _matchupGate.Release(); }
    }

    public static IReadOnlyList<Matchup> ParseSynergies(string json, int queriedHeroId)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            throw new JsonException("STRATZ вернул ошибку GraphQL with.");
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("heroStats", out var stats) ||
            !stats.TryGetProperty("matchUp", out var groups) ||
            groups.ValueKind != JsonValueKind.Array)
            throw new JsonException("Некорректный ответ STRATZ with.");
        var output = new Dictionary<int, Matchup>();
        foreach (var group in groups.EnumerateArray())
        {
            if (!group.TryGetProperty("with", out var teammates) ||
                teammates.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in teammates.EnumerateArray())
            {
                if (!row.TryGetProperty("heroId2", out var hero) ||
                    !row.TryGetProperty("matchCount", out var count) ||
                    !row.TryGetProperty("winCount", out var wins) ||
                    hero.ValueKind != JsonValueKind.Number ||
                    count.ValueKind != JsonValueKind.Number ||
                    wins.ValueKind != JsonValueKind.Number) continue;

                var id = hero.GetInt32();
                var played = count.GetInt32();
                var won = wins.GetInt32();
                if (id <= 0 || id == queriedHeroId || played < 30 ||
                    won < 0 || won > played) continue;
                if (!output.TryGetValue(id, out var previous) ||
                    previous.GamesPlayed < played)
                    output[id] = new Matchup { HeroId = id, GamesPlayed = played, Wins = won };
            }
        }
        return output.Values.ToArray();
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
