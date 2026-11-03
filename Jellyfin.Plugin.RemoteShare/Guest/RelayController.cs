using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteShare.Guest;

/// <summary>
/// What the guest's .strm files point at. Forwards the request (including Range, so seeking works)
/// to the host with the share token attached, and streams the response back. Clients and ffmpeg
/// only ever talk to the guest; the host token never leaves this server.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("RemoteShare/Relay")]
public class RelayController : ControllerBase
{
    private static readonly string[] ForwardedResponseHeaders =
        { "Content-Type", "Content-Length", "Content-Range", "Accept-Ranges", "Last-Modified", "ETag" };

    private readonly HostClient _client;
    private readonly ILogger<RelayController> _logger;

    public RelayController(HostClient client, ILogger<RelayController> logger)
    {
        _client = client;
        _logger = logger;
    }

    [HttpGet("{shareId:guid}/{file}")]
    [HttpHead("{shareId:guid}/{file}")]
    public async Task Relay(Guid shareId, string file, [FromQuery] string? key)
    {
        var share = Plugin.Instance!.Configuration.GuestShares.FirstOrDefault(s => s.Id == shareId);
        if (share is null || key is null
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(share.RelayKey)))
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out var itemId))
        {
            Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var method = HttpMethods.IsHead(Request.Method) ? HttpMethod.Head : HttpMethod.Get;
        using var upstream = HostClient.NewRequest(method, share.HostUrl, share.Token, $"RemoteShare/Host/Items/{itemId:N}/Stream");
        foreach (var header in new[] { "Range", "If-Range", "If-None-Match", "If-Modified-Since" })
        {
            if (Request.Headers.TryGetValue(header, out var value))
            {
                upstream.Headers.TryAddWithoutValidation(header, value.ToArray());
            }
        }

        HttpResponseMessage response;
        try
        {
            response = await _client.StreamingClient()
                .SendAsync(upstream, HttpCompletionOption.ResponseHeadersRead, HttpContext.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Remote Share relay could not reach {Host}", share.HostUrl);
            Response.StatusCode = StatusCodes.Status502BadGateway;
            return;
        }

        using (response)
        {
            Response.StatusCode = (int)response.StatusCode;
            foreach (var name in ForwardedResponseHeaders)
            {
                if (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
                {
                    Response.Headers[name] = values.ToArray();
                }
            }

            if (method == HttpMethod.Head)
            {
                return;
            }

            HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            try
            {
                await using var body = await response.Content.ReadAsStreamAsync(HttpContext.RequestAborted).ConfigureAwait(false);
                await body.CopyToAsync(Response.Body, 256 * 1024, HttpContext.RequestAborted).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Client stopped or seeked; normal.
            }
            catch (IOException)
            {
                // Connection dropped on either side; normal during seeks.
            }
        }
    }
}
