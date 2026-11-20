using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.RemoteShare.Api;

// Wire contract between a host and its guests. Both ends run this same plugin,
// so these types are shared; keep changes backward compatible (add, don't rename).

/// <summary>Header guests send their share token in.</summary>
public static class RemoteShareHeaders
{
    public const string Token = "X-RemoteShare-Token";
}

public static class ServerUrl
{
    /// <summary>
    /// Normalizes a server address. Plain http is fine; when no scheme is given
    /// (e.g. "192.168.1.50:8096"), http is assumed. Returns null for an empty input.
    /// </summary>
    /// <exception cref="FormatException">Not a usable http(s) URL.</exception>
    public static string? Normalize(string? url)
    {
        url = url?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (!url.Contains("://", StringComparison.Ordinal))
        {
            url = "http://" + url;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrEmpty(uri.Host))
        {
            throw new FormatException($"\"{url}\" isn't a valid server address. Use e.g. http://192.168.1.50:8096 or https://jellyfin.example.com.");
        }

        return url;
    }
}

public class RemoteLibraryDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>"movies" or "tvshows".</summary>
    public string CollectionType { get; set; } = string.Empty;
}

public class RemoteItemDto
{
    public Guid Id { get; set; }

    /// <summary>"Movie", "Series" or "Episode".</summary>
    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string? OriginalTitle { get; set; }

    public int? Year { get; set; }

    public string? Overview { get; set; }

    public DateTime? PremiereDate { get; set; }

    public string? OfficialRating { get; set; }

    public float? CommunityRating { get; set; }

    public string[] Genres { get; set; } = Array.Empty<string>();

    public string[] Studios { get; set; } = Array.Empty<string>();

    public Dictionary<string, string> ProviderIds { get; set; } = new();

    public long? RunTimeTicks { get; set; }

    // Episodes
    public Guid? SeriesId { get; set; }

    public string? SeriesName { get; set; }

    public int? SeasonNumber { get; set; }

    public int? EpisodeNumber { get; set; }

    public int? EpisodeNumberEnd { get; set; }

    /// <summary>File extension without the dot, e.g. "mkv". Null for series.</summary>
    public string? Container { get; set; }

    /// <summary>Image types available from the host, e.g. Primary, Backdrop, Thumb, Logo.</summary>
    public string[] Images { get; set; } = Array.Empty<string>();

    public DateTime DateModified { get; set; }

    /// <summary>
    /// Text of the host's EDL sidecar (Movie.mkv -> Movie.edl), if there is one. Guests write it
    /// next to the .strm so the Ember EDL Sidecars plugin serves it there too. Null when absent.
    /// </summary>
    public string? Edl { get; set; }
}

public class RemoteItemPageDto
{
    public List<RemoteItemDto> Items { get; set; } = new();

    public int TotalCount { get; set; }
}

/// <summary>
/// What a host hands a guest: where to connect and the token to use.
/// Serialized as base64url JSON so it can be pasted as one string.
/// </summary>
public class ShareCode
{
    [JsonPropertyName("u")]
    public string HostUrl { get; set; } = string.Empty;

    [JsonPropertyName("t")]
    public string Token { get; set; } = string.Empty;

    public string Encode()
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(this);
        return "rs1." + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static ShareCode Decode(string code)
    {
        code = code.Trim();
        if (!code.StartsWith("rs1.", StringComparison.Ordinal))
        {
            throw new FormatException("Not a Remote Share code (expected it to start with \"rs1.\").");
        }

        var b64 = code[4..].Replace('-', '+').Replace('_', '/');
        b64 = b64.PadRight(b64.Length + ((4 - (b64.Length % 4)) % 4), '=');
        var parsed = JsonSerializer.Deserialize<ShareCode>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        if (parsed is null || string.IsNullOrWhiteSpace(parsed.HostUrl) || string.IsNullOrWhiteSpace(parsed.Token))
        {
            throw new FormatException("Share code is incomplete.");
        }

        parsed.HostUrl = ServerUrl.Normalize(parsed.HostUrl)!;
        return parsed;
    }
}
