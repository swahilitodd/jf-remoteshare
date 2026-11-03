using Jellyfin.Plugin.RemoteShare.Api;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;

namespace Jellyfin.Plugin.RemoteShare.Host;

/// <summary>
/// Endpoints guest servers call. These bypass Jellyfin user auth and are guarded by
/// the share token instead; every item lookup is checked against the share's libraries.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("RemoteShare/Host")]
public class HostController : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private readonly HostService _host;

    public HostController(HostService host)
    {
        _host = host;
    }

    [HttpGet("Libraries")]
    public ActionResult<List<RemoteLibraryDto>> GetLibraries()
    {
        var share = Auth();
        return share is null ? Unauthorized() : _host.GetLibrariesFor(share);
    }

    [HttpGet("Libraries/{libraryId:guid}/Items")]
    public ActionResult<RemoteItemPageDto> GetItems(Guid libraryId, [FromQuery] int startIndex = 0, [FromQuery] int limit = 500)
    {
        var share = Auth();
        if (share is null)
        {
            return Unauthorized();
        }

        try
        {
            return _host.GetItems(share, libraryId, startIndex, limit);
        }
        catch (UnauthorizedAccessException)
        {
            return NotFound();
        }
    }

    [HttpGet("Items/{itemId:guid}/Stream")]
    [HttpHead("Items/{itemId:guid}/Stream")]
    public ActionResult Stream(Guid itemId)
    {
        var share = Auth();
        if (share is null)
        {
            return Unauthorized();
        }

        var item = _host.GetSharedItem(share, itemId);
        if (item is null || string.IsNullOrEmpty(item.Path) || !item.IsFileProtocol || !System.IO.File.Exists(item.Path))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(item.Path, out var contentType))
        {
            contentType = "application/octet-stream";
        }

        return PhysicalFile(item.Path, contentType, enableRangeProcessing: true);
    }

    [HttpGet("Items/{itemId:guid}/Images/{imageType}")]
    public ActionResult Image(Guid itemId, string imageType)
    {
        var share = Auth();
        if (share is null)
        {
            return Unauthorized();
        }

        if (!Enum.TryParse<ImageType>(imageType, ignoreCase: true, out var type))
        {
            return BadRequest();
        }

        var item = _host.GetSharedItem(share, itemId);
        var info = item?.GetImageInfo(type, 0);
        if (info is null || !info.IsLocalFile || !System.IO.File.Exists(info.Path))
        {
            return NotFound();
        }

        if (!ContentTypes.TryGetContentType(info.Path, out var contentType))
        {
            contentType = "image/jpeg";
        }

        return PhysicalFile(info.Path, contentType);
    }

    private Configuration.HostShare? Auth() =>
        _host.Authenticate(Request.Headers[RemoteShareHeaders.Token].FirstOrDefault());
}
