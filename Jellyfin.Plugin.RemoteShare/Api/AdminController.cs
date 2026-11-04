using Jellyfin.Plugin.RemoteShare.Configuration;
using Jellyfin.Plugin.RemoteShare.Guest;
using Jellyfin.Plugin.RemoteShare.Host;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.RemoteShare.Api;

/// <summary>Admin-only endpoints used by the plugin's settings page.</summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("RemoteShare/Admin")]
public class AdminController : ControllerBase
{
    private readonly HostService _host;
    private readonly GuestSyncService _guest;

    public AdminController(HostService host, GuestSyncService guest)
    {
        _host = host;
        _guest = guest;
    }

    private static Plugin P => Plugin.Instance!;

    [HttpGet("State")]
    public ActionResult<object> State()
    {
        var c = P.Configuration;
        return new
        {
            c.HostPublicUrl,
            c.GuestLocalUrl,
            c.GuestStoragePath,
            GuestStorageRoot = P.GuestStorageRoot,
            Libraries = _host.GetShareableLibraries(),
            HostShares = c.HostShares.Select(s => new
            {
                s.Id,
                s.GuestName,
                s.LibraryIds,
                s.Enabled,
                s.CreatedUtc,
                s.LastSeenUtc,
                Code = ShareCodeFor(s),
            }),
            GuestShares = c.GuestShares.Select(s => new
            {
                s.Id,
                s.FriendlyName,
                s.HostUrl,
                s.LastSyncUtc,
                s.LastSyncError,
                SyncStatus = _guest.StatusOf(s.Id),
                s.Libraries,
            }),
        };
    }

    public class SettingsRequest
    {
        public string? HostPublicUrl { get; set; }

        public string? GuestLocalUrl { get; set; }

        public string? GuestStoragePath { get; set; }
    }

    [HttpPost("Settings")]
    public ActionResult Settings([FromBody] SettingsRequest req)
    {
        string? hostPublicUrl, guestLocalUrl;
        try
        {
            hostPublicUrl = ServerUrl.Normalize(req.HostPublicUrl);
            guestLocalUrl = ServerUrl.Normalize(req.GuestLocalUrl);
        }
        catch (FormatException ex)
        {
            return BadRequest(ex.Message);
        }

        P.Update(c =>
        {
            if (req.HostPublicUrl is not null)
            {
                c.HostPublicUrl = hostPublicUrl ?? string.Empty;
            }

            if (guestLocalUrl is not null)
            {
                c.GuestLocalUrl = guestLocalUrl;
            }

            if (req.GuestStoragePath is not null)
            {
                c.GuestStoragePath = req.GuestStoragePath.Trim();
            }
        });
        return NoContent();
    }

    // ---------- Host role ----------

    public class HostShareRequest
    {
        public string GuestName { get; set; } = string.Empty;

        public List<Guid> LibraryIds { get; set; } = new();

        public bool? Enabled { get; set; }
    }

    [HttpPost("HostShares")]
    public ActionResult<object> CreateHostShare([FromBody] HostShareRequest req)
    {
        if (string.IsNullOrWhiteSpace(P.Configuration.HostPublicUrl))
        {
            return BadRequest("Set this server's public URL first; it goes into the share code.");
        }

        if (string.IsNullOrWhiteSpace(req.GuestName) || req.LibraryIds.Count == 0)
        {
            return BadRequest("Give the guest a name and pick at least one library.");
        }

        var share = new HostShare
        {
            GuestName = req.GuestName.Trim(),
            LibraryIds = ValidLibraryIds(req.LibraryIds),
            Token = HostService.NewSecret(),
        };
        P.Update(c => c.HostShares.Add(share));
        return new { share.Id, Code = ShareCodeFor(share) };
    }

    [HttpPut("HostShares/{id:guid}")]
    public ActionResult UpdateHostShare(Guid id, [FromBody] HostShareRequest req)
    {
        var found = false;
        P.Update(c =>
        {
            var share = c.HostShares.FirstOrDefault(s => s.Id == id);
            if (share is null)
            {
                return;
            }

            found = true;
            if (!string.IsNullOrWhiteSpace(req.GuestName))
            {
                share.GuestName = req.GuestName.Trim();
            }

            share.LibraryIds = ValidLibraryIds(req.LibraryIds);
            share.Enabled = req.Enabled ?? share.Enabled;
        });
        return found ? NoContent() : NotFound();
    }

    [HttpDelete("HostShares/{id:guid}")]
    public ActionResult RevokeHostShare(Guid id)
    {
        P.Update(c => c.HostShares.RemoveAll(s => s.Id == id));
        return NoContent();
    }

    // ---------- Guest role ----------

    public class JoinRequest
    {
        public string Code { get; set; } = string.Empty;

        public string FriendlyName { get; set; } = string.Empty;

        public bool TrustCertificate { get; set; }
    }

    [HttpPost("GuestShares")]
    public async Task<ActionResult<object>> Join([FromBody] JoinRequest req, CancellationToken ct)
    {
        try
        {
            var share = await _guest.JoinAsync(req.Code, req.FriendlyName, req.TrustCertificate, ct).ConfigureAwait(false);
            _guest.StartSync(share.Id);
            return new { share.Id };
        }
        catch (Exception ex) when (ex is FormatException or HostAccessException or InvalidOperationException or HttpRequestException)
        {
            return BadRequest(ex.Message);
        }
    }

    public class GuestShareEdit
    {
        public string? FriendlyName { get; set; }

        public List<GuestLibraryEdit> Libraries { get; set; } = new();
    }

    public class GuestLibraryEdit
    {
        public Guid RemoteId { get; set; }

        public string? LocalName { get; set; }

        public bool? Enabled { get; set; }
    }

    [HttpPut("GuestShares/{id:guid}")]
    public ActionResult EditGuestShare(Guid id, [FromBody] GuestShareEdit req)
    {
        try
        {
            _guest.Edit(id, req.FriendlyName, req.Libraries.Select(l => (l.RemoteId, l.LocalName, l.Enabled)));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }

        _guest.StartSync(id);
        return NoContent();
    }

    [HttpPost("GuestShares/{id:guid}/Sync")]
    public ActionResult Sync(Guid id)
    {
        _guest.StartSync(id);
        return Accepted();
    }

    [HttpDelete("GuestShares/{id:guid}")]
    public async Task<ActionResult> Leave(Guid id, CancellationToken ct)
    {
        try
        {
            await _guest.LeaveAsync(id, ct).ConfigureAwait(false);
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    private List<Guid> ValidLibraryIds(IEnumerable<Guid> ids)
    {
        var shareable = _host.GetShareableLibraries().Select(l => l.Id).ToHashSet();
        return ids.Where(shareable.Contains).Distinct().ToList();
    }

    private static string ShareCodeFor(HostShare share) =>
        new ShareCode { HostUrl = P.Configuration.HostPublicUrl, Token = share.Token }.Encode();
}
