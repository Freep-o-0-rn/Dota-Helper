using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using DotaHelper.Models;

namespace DotaHelper.Services;

/// <summary>
/// GSI data available to the local player may be partial during All Pick.
/// Never fabricate picks or infer identity from UI order.
/// </summary>
public sealed record GsiDraftState(
    string Phase,
    bool? PlayerRadiant,
    string? OwnHeroName,
    IReadOnlyList<int> RadiantPicks,
    IReadOnlyList<int> DirePicks,
    IReadOnlyList<int> RadiantBans,
    IReadOnlyList<int> DireBans,
    long? MatchId = null)
{
    public bool HasDraft => RadiantPicks.Count + DirePicks.Count +
        RadiantBans.Count + DireBans.Count > 0;
}

public static class GsiDraftParser
{
    public static GsiDraftState Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Expected GSI object.");

        static JsonElement Property(JsonElement parent, string key) =>
            parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(key, out var result) ? result : default;
        static string? String(JsonElement obj, string key)
        {
            var value = Property(obj, key);
            return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        var map = Property(root, "map");
        var player = Property(root, "player");
        var hero = Property(root, "hero");
        var phase = String(map, "game_state") ?? "unknown";
        var matchIdElement = Property(map, "matchid");
        if (matchIdElement.ValueKind != JsonValueKind.Number)
            matchIdElement = Property(map, "match_id");
        long? matchId = matchIdElement.ValueKind == JsonValueKind.Number &&
            matchIdElement.TryGetInt64(out var id) && id > 0 ? id : null;
        var teamName = String(player, "team_name");
        bool? radiant = teamName switch
        {
            "radiant" or "Radiant" => true,
            "dire" or "Dire" => false,
            _ => null
        };
        if (radiant is null)
        {
            var team = Property(player, "team");
            if (team.ValueKind == JsonValueKind.Number && team.TryGetInt32(out var teamNumber))
                radiant = teamNumber switch { 2 => true, 3 => false, _ => null };
        }
        var ownHero = String(hero, "name");
        var draft = Property(root, "draft");

        static IReadOnlyList<int> ParseTeam(JsonElement draft, string side, string type)
        {
            var group = draft.ValueKind == JsonValueKind.Object &&
                draft.TryGetProperty(side, out var value) ? value : default;
            if (group.ValueKind != JsonValueKind.Object) return [];
            var pairs = new List<(int Order, int HeroId)>();
            foreach (var item in group.EnumerateObject())
            {
                if (!item.Name.StartsWith(type, StringComparison.OrdinalIgnoreCase) ||
                    !item.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase)) continue;
                var middle = item.Name.Substring(type.Length,
                    item.Name.Length - type.Length - "_id".Length);
                if (!int.TryParse(middle, out var order) ||
                    item.Value.ValueKind != JsonValueKind.Number ||
                    !item.Value.TryGetInt32(out var heroId) || heroId <= 0) continue;
                pairs.Add((order, heroId));
            }
            return pairs.OrderBy(x => x.Order).Select(x => x.HeroId)
                .Distinct().ToArray();
        }

        return new(phase, radiant, ownHero,
            ParseTeam(draft, "team2", "pick"),
            ParseTeam(draft, "team3", "pick"),
            ParseTeam(draft, "team2", "ban"),
            ParseTeam(draft, "team3", "ban"), matchId);
    }
}

/// <summary>
/// Conservatively merge only observed Dota GSI picks/bans into empty slots.
/// A missing or partial GSI field must NEVER erase manually selected heroes.
/// </summary>
public static class GsiDraftApplier
{
    public static int MergePicks(IEnumerable<int> observedIds,
        IReadOnlyList<DraftSlot> slots, IReadOnlyDictionary<int, Hero> byId,
        int excludeHeroId = 0, Action<DraftSlot, int>? onAdded = null)
    {
        var changes = 0;
        foreach (var id in observedIds.Distinct())
        {
            if (id <= 0 || id == excludeHeroId || !byId.TryGetValue(id, out var hero))
                continue;
            if (slots.Any(s => s.SelectedHero?.Id == id)) continue;
            var slot = slots.FirstOrDefault(s => s.SelectedHero is null && !s.IsPreview);
            if (slot is null) break;
            slot.SelectedHero = hero;
            onAdded?.Invoke(slot, id);
            changes++;
        }
        return changes;
    }

    public static int MergeBans(IEnumerable<int> observedIds,
        IReadOnlyList<BanSlot> slots, IReadOnlyDictionary<int, Hero> byId,
        Action<BanSlot, int>? onAdded = null)
    {
        var changes = 0;
        foreach (var id in observedIds.Distinct())
        {
            if (id <= 0 || !byId.TryGetValue(id, out var hero))
                continue;
            if (slots.Any(s => s.SelectedHero?.Id == id)) continue;
            var slot = slots.FirstOrDefault(s => s.SelectedHero is null);
            if (slot is null) break;
            slot.SelectedHero = hero;
            onAdded?.Invoke(slot, id);
            changes++;
        }
        return changes;
    }
}

/// <summary>
/// Local read-only Valve Game State Integration receiver. Does not inject,
/// read memory, press buttons or transmit match data to third-party servers.
/// </summary>
public sealed class DotaGsiService : IDisposable
{
    public const int Port = 30127;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string _tokenFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotaHelper", "gsi-local-token.txt");
    private readonly string _secret;
    private bool _started;

    public event Action<GsiDraftState>? Updated;
    public event Action<string>? StatusChanged;

    public DotaGsiService()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_tokenFile)!);
        _secret = File.Exists(_tokenFile) ? File.ReadAllText(_tokenFile).Trim() : "";
        if (_secret.Length < 32)
        {
            _secret = RandomNumberGenerator.GetHexString(48);
            File.WriteAllText(_tokenFile, _secret);
        }
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
    }

    public void Start()
    {
        if (_started) return;
        _listener.Start();
        _started = true;
        StatusChanged?.Invoke("GSI: слушаю только 127.0.0.1; ожидаю Dota 2");
        _ = ListenLoopAsync(_stop.Token);
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().WaitAsync(ct); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException
                or OperationCanceledException) { break; }
            _ = HandleAsync(context, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        var response = context.Response;
        try
        {
            if (!IPAddress.IsLoopback(context.Request.RemoteEndPoint?.Address ??
                                      IPAddress.None) ||
                context.Request.HttpMethod != "POST")
            {
                response.StatusCode = 403;
                return;
            }
            if (context.Request.ContentLength64 > 256_000)
            {
                response.StatusCode = 413;
                return;
            }

            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var buffer = new char[4096];
            var body = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            {
                body.Append(buffer, 0, count);
                if (body.Length > 256_000)
                {
                    response.StatusCode = 413;
                    return;
                }
            }

            using var doc = JsonDocument.Parse(body.ToString());
            if (!doc.RootElement.TryGetProperty("auth", out var auth) ||
                !auth.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(token.GetString() ?? ""),
                    Encoding.UTF8.GetBytes(_secret)))
            {
                response.StatusCode = 403;
                return;
            }

            var state = GsiDraftParser.Parse(body.ToString());
            StatusChanged?.Invoke(
                $"GSI: {state.Phase}, Radiant {state.RadiantPicks.Count} / " +
                $"Dire {state.DirePicks.Count} пиков; " +
                (state.HasDraft ? "драфт получен" : "пики не передаются в этой фазе"));
            Updated?.Invoke(state);
            response.StatusCode = 200;
        }
        catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException)
        {
            response.StatusCode = 400;
        }
        finally
        {
            try { response.Close(); } catch (ObjectDisposedException) { }
        }
    }

    public string InstallConfig(string dotaRoot)
    {
        // dotaRoot = "...\steamapps\common\dota 2 beta".
        var cfg = Path.Combine(dotaRoot, "game", "dota", "cfg", "gamestate_integration");
        if (!Directory.Exists(Path.Combine(dotaRoot, "game", "dota", "cfg")))
            throw new DirectoryNotFoundException(
                "Не найдена папка Dota 2 game\\dota\\cfg.");
        Directory.CreateDirectory(cfg);
        var filename = Path.Combine(cfg, "gamestate_integration_dota_helper.cfg");
        // Only our own config; never overwrite other GSI integrations.
        var content = $$"""
        "Dota Helper GSI"
        {
            "uri" "http://127.0.0.1:{{Port}}/"
            "timeout" "5.0"
            "buffer" "0.1"
            "throttle" "0.5"
            "heartbeat" "2.0"
            "data"
            {
                "provider" "1"
                "map" "1"
                "player" "1"
                "hero" "1"
                "draft" "1"
            }
            "auth" { "token" "{{_secret}}" }
        }
        """;
        File.WriteAllText(filename, content);
        return filename;
    }

    public static string? FindDotaRoot()
    {
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static string? SteamFromRegistry()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                return key?.GetValue("SteamPath") as string;
            }
            catch { return null; }
        }
        var steam = SteamFromRegistry();
        if (!string.IsNullOrWhiteSpace(steam))
            candidates.Add(steam.Replace('/', Path.DirectorySeparatorChar));
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFiles))
            candidates.Add(Path.Combine(programFiles, "Steam"));
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady &&
            d.DriveType == DriveType.Fixed))
        {
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
            candidates.Add(Path.Combine(drive.RootDirectory.FullName, "Steam"));
        }

        var libraries = new HashSet<string>(candidates, StringComparer.OrdinalIgnoreCase);
        foreach (var steamRoot in candidates)
        {
            try
            {
                var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                foreach (Match match in Regex.Matches(File.ReadAllText(vdf),
                    "\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase))
                    libraries.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var library in libraries)
        {
            var root = Path.Combine(library, "steamapps", "common", "dota 2 beta");
            if (Directory.Exists(Path.Combine(root, "game", "dota", "cfg")))
                return root;
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (_started) _listener.Close();
        _stop.Dispose();
    }
}
