using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.RemoteShare.Api;

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

    private readonly IHttpClientFactory _httpClientFactory;

    public HostClient(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public Task<List<RemoteLibraryDto>> GetLibrariesAsync(string hostUrl, string token, CancellationToken ct) =>
        GetJsonAsync<List<RemoteLibraryDto>>(hostUrl, token, "RemoteShare/Host/Libraries", ct);

    public async Task<List<RemoteItemDto>> GetAllItemsAsync(string hostUrl, string token, Guid libraryId, CancellationToken ct)
    {
        const int pageSize = 500;
        var all = new List<RemoteItemDto>();
        for (var start = 0; ; start += pageSize)
        {
            var page = await GetJsonAsync<RemoteItemPageDto>(
                hostUrl, token, $"RemoteShare/Host/Libraries/{libraryId:N}/Items?startIndex={start}&limit={pageSize}", ct).ConfigureAwait(false);
            all.AddRange(page.Items);
            if (start + pageSize >= page.TotalCount)
            {
                return all;
            }
        }
    }

    /// <summary>Downloads an image to <paramref name="destination"/>; returns false if the host has none.</summary>
    public async Task<bool> DownloadImageAsync(string hostUrl, string token, Guid itemId, string imageType, string destination, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, hostUrl, token, $"RemoteShare/Host/Items/{itemId:N}/Images/{imageType}");
        using var response = await Client().SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
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

    public static HttpRequestMessage NewRequest(HttpMethod method, string hostUrl, string token, string path)
    {
        var request = new HttpRequestMessage(method, $"{hostUrl.TrimEnd('/')}/{path}");
        request.Headers.TryAddWithoutValidation(RemoteShareHeaders.Token, token);
        return request;
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

    private async Task<T> GetJsonAsync<T>(string hostUrl, string token, string path, CancellationToken ct)
    {
        using var request = NewRequest(HttpMethod.Get, hostUrl, token, path);
        using var response = await Client().SendAsync(request, ct).ConfigureAwait(false);
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
}
