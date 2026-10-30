using System.IO;
using System.Net.Http;
using System.Xml;
using System.Xml.Linq;

namespace DotaHelper.Services;

/// <summary>
/// Resolve numeric Steam account IDs and vanity /id/ links without an API key.
/// Steam Community XML is deprecated and may be unavailable; fail explicitly.
/// Never request a user-supplied host, only steamcommunity.com over HTTPS.
/// </summary>
public sealed class SteamProfileResolver
{
    public const ulong SteamId64Offset = 76561197960265728UL;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

    public static long? ParseSteamId64FromXml(string xml)
    {
        using var reader = XmlReader.Create(new StringReader(xml),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 512_000
            });
        var document = XDocument.Load(reader);
        var idText = document.Root?.Element("steamID64")?.Value;
        if (!ulong.TryParse(idText, out var steamId) ||
            steamId <= SteamId64Offset ||
            steamId - SteamId64Offset > uint.MaxValue)
            return null;
        return (long)(steamId - SteamId64Offset);
    }

    public static (long? AccountId, string? Vanity) ParseInput(string input)
    {
        var raw = input.Trim();
        if (ulong.TryParse(raw, out var number))
        {
            if (number >= SteamId64Offset)
                number -= SteamId64Offset;
            if (number > 0 && number <= uint.MaxValue)
                return ((long)number, null);
            throw new ArgumentException("Неверный числовой Steam ID.");
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.Equals(uri.Host, "steamcommunity.com", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "Введи Steam Account ID, SteamID64 или HTTPS-ссылку steamcommunity.com/id/...");

        var parts = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            throw new ArgumentException("Неверный адрес Steam-профиля.");

        var tail = Uri.UnescapeDataString(parts[1]);
        if (parts[0].Equals("profiles", StringComparison.OrdinalIgnoreCase))
            return ParseInput(tail);

        if (!parts[0].Equals("id", StringComparison.OrdinalIgnoreCase) ||
            tail.Length is < 1 or > 100 ||
            tail.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')))
            throw new ArgumentException("Неверная пользовательская ссылка Steam /id/.");
        return (null, tail);
    }

    public async Task<long> ResolveAsync(string input)
    {
        var parsed = ParseInput(input);
        if (parsed.AccountId.HasValue) return parsed.AccountId.Value;

        // Only interpolate a validated single path segment into a trusted URL.
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "https://steamcommunity.com/id/" + Uri.EscapeDataString(parsed.Vanity!) + "/?xml=1");
        using var response = await Http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Steam Community недоступен (HTTP {(int)response.StatusCode}). " +
                "Введи числовой SteamID64 вручную.");
        var xml = await response.Content.ReadAsStringAsync();
        try
        {
            return ParseSteamId64FromXml(xml) ??
                throw new InvalidOperationException(
                    "Steam не предоставил публичный SteamID64 для этой ссылки. " +
                    "Попробуй числовой SteamID64.");
        }
        catch (XmlException)
        {
            throw new InvalidOperationException(
                "Steam вернул не XML-профиль. Устаревший XML-эндпоинт мог стать недоступен; " +
                "используй SteamID64.");
        }
    }
}
