using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.CompilerServices;
using Jellyfin.Plugin.RemoteShare.Api;
using Jellyfin.Plugin.RemoteShare.Configuration;

namespace Jellyfin.Plugin.RemoteShare.Guest;

/// <summary>Guest-side client for a host's /RemoteShare/Host endpoints.</summary>
public class HostClient
{
    public const string HttpClientName = "RemoteShare";

    /// <summary>Jellyfin serializes GUIDs without dashes ("N"), which System.Text.Json won't read by default.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new FlexibleGuidConverter(), new FlexibleNullableGuidConverter() },
    };

    /// <summary>SHA-256 of the host certificate to accept even if it fails normal validation.</summary>
    private static readonly HttpRequestOptionsKey<string> PinnedCertKey = new("RemoteShare.PinnedCert");

    /// <summary>Set on a request to accept whatever certificate the host presents and record its hash here.</summary>
    private static readonly HttpRequestOptionsKey<StrongBox<string?>> CaptureCertKey = new("RemoteShare.CaptureCert");

    private readonly IHttpClientFactory _httpClientFactory;

    public HostClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>Primary handler for the named client: normal TLS validation, plus per-share certificate pinning.</summary>
    public static HttpMessageHandler CreateHandler() => new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = ValidateCertificate,
    };

    public Task<List<RemoteLibraryDto>> GetLibrariesAsync(GuestShare share, CancellationToken ct) =>
        GetJsonAsync<List<RemoteLibraryDto>>(NewRequest(HttpMethod.Get, share, "RemoteShare/Host/Libraries"), ct);

    /// <summary>
    /// Used when joining with "trust this host's certificate": accepts whatever certificate the host
    /// presents and pins it on <paramref name="share"/>, so later requests only accept that exact certificate.
    /// </summary>
    public async Task<List<RemoteLibraryDto>> GetLibrariesTrustingCertificateAsync(GuestShare share, CancellationToken ct)
    {
        var captured = new StrongBox<string?>();
        var request = NewRequest(HttpMethod.Get, share, "RemoteShare/Host/Libraries");
        request.Options.Set(CaptureCertKey, captured);
        var result = await GetJsonAsync<List<RemoteLibraryDto>>(request, ct).ConfigureAwait(false);
        share.PinnedCertificateSha256 = captured.Value;
        return result;
    }

    public async Task<List<RemoteItemDto>> GetAllItemsAsync(GuestShare share, Guid libraryId, CancellationToken ct)
    {
        const int pageSize = 500;
        var all = new List<RemoteItemDto>();
        for (var start = 0; ; start += pageSize)
        {
            var page = await GetJsonAsync<RemoteItemPageDto>(
                NewRequest(HttpMethod.Get, share, $"RemoteShare/Host/Libraries/{libraryId:N}/Items?startIndex={start}&limit={pageSize}"), ct).ConfigureAwait(false);
            all.AddRange(page.Items);
            if (start + pageSize >= page.TotalCount)
            {
                return all;
            }
        }
    }

    /// <summary>Downloads an image to <paramref name="destination"/>; returns false if the host has none.</summary>
    public async Task<bool> DownloadImageAsync(GuestShare share, Guid itemId, string imageType, string destination, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, share, $"RemoteShare/Host/Items/{itemId:N}/Images/{imageType}");
        using var response = await SendAsync(Client(), request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        EnsureOk(response);
        var tmp = destination + ".part";
        await using (var file = File.Create(tmp))
        {
            await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
        }

        File.Move(tmp, destination, overwrite: true);
        return true;
    }

    public static HttpRequestMessage NewRequest(HttpMethod method, GuestShare share, string path)
    {
        var request = new HttpRequestMessage(method, $"{share.HostUrl.TrimEnd('/')}/{path}");
        request.Headers.TryAddWithoutValidation(RemoteShareHeaders.Token, share.Token);
        if (!string.IsNullOrEmpty(share.PinnedCertificateSha256))
        {
            request.Options.Set(PinnedCertKey, share.PinnedCertificateSha256);
        }

        return request;
    }

    /// <summary>Sends a request, turning TLS failures into an explanation the admin can act on.</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        // HttpClient rewrites RequestUri when it follows a redirect, so remember what we asked for.
        var requested = request.RequestUri;
        try
        {
            return await client.SendAsync(request, completion, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException auth)
        {
            var requestedHost = requested?.GetLeftPart(UriPartial.Authority);
            var finalHost = request.RequestUri?.GetLeftPart(UriPartial.Authority);
            string advice;
            if (requested?.Scheme == Uri.UriSchemeHttp)
            {
                advice = $"This share uses {requestedHost}, but the host redirected to {finalHost}. Something in front of the host "
                    + "(a reverse proxy, Cloudflare \"Always Use HTTPS\", or Jellyfin's \"Require HTTPS\") is upgrading the connection. "
                    + "Point the host's public URL at an address that answers plain HTTP (e.g. http://<ip>:8096) and send a new share code.";
            }
            else if (requested?.Port == 8096)
            {
                advice = $"This share uses {requestedHost}, but port 8096 is Jellyfin's plain-HTTP port. "
                    + "Change the host's public URL to http://… and send a new share code.";
            }
            else
            {
                advice = $"This share uses {requestedHost}. To use plain HTTP, change the host's public URL to http://… and send a new share code. "
                    + "If the host deliberately uses a self-signed certificate, re-add the share with \"Trust this host's certificate\" ticked.";
            }

            throw new HostAccessException($"Couldn't make an HTTPS connection to {finalHost} ({auth.Message}). {advice}", ex);
        }
    }

    /// <summary>Client for streaming: no overall timeout, since a relayed movie can run for hours.</summary>
    public HttpClient StreamingClient()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = Timeout.InfiniteTimeSpan;
        return client;
    }

    private HttpClient Client()
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    private static bool ValidateCertificate(HttpRequestMessage request, X509Certificate2? cert, X509Chain? chain, SslPolicyErrors errors)
    {
        if (cert is null)
        {
            return false;
        }

        if (request.Options.TryGetValue(CaptureCertKey, out var capture))
        {
            capture.Value = errors == SslPolicyErrors.None ? null : HashOf(cert);
            return true;
        }

        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return request.Options.TryGetValue(PinnedCertKey, out var pinned)
            && string.Equals(pinned, HashOf(cert), StringComparison.OrdinalIgnoreCase);
    }

    private static string HashOf(X509Certificate2 cert) => Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256));

    private async Task<T> GetJsonAsync<T>(HttpRequestMessage request, CancellationToken ct)
    {
        using var _ = request;
        using var response = await SendAsync(Client(), request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        EnsureOk(response);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Host returned an empty response.");
    }

    private static void EnsureOk(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new HostAccessException("The host rejected this share's token (revoked or disabled).");
        }

        if (response.StatusCode == HttpStatusCode.NotFound && response.RequestMessage?.RequestUri?.AbsolutePath.EndsWith("/Libraries", StringComparison.Ordinal) == true)
        {
            throw new HostAccessException("The host doesn't have the Remote Share plugin installed (or the URL is wrong).");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HostAccessException($"Host returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
    }
}

internal sealed class FlexibleGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("N"));
}

internal sealed class FlexibleNullableGuidConverter : JsonConverter<Guid?>
{
    public override Guid? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null || string.IsNullOrEmpty(reader.GetString()) ? null : Guid.Parse(reader.GetString()!);

    public override void Write(Utf8JsonWriter writer, Guid? value, JsonSerializerOptions options)
    {
        if (value is { } v)
        {
            writer.WriteStringValue(v.ToString("N"));
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}

public class HostAccessException : Exception
{
    public HostAccessException(string message)
        : base(message)
    {
    }

    public HostAccessException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
