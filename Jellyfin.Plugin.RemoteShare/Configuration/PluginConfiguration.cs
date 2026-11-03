using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.RemoteShare.Configuration;

/// <summary>
/// Persisted plugin state. One server can be a host (sharing out), a guest (receiving), or both.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---------- Host role ----------

    /// <summary>
    /// The URL guests use to reach this server, e.g. https://jellyfin.example.com.
    /// Baked into share codes.
    /// </summary>
    public string HostPublicUrl { get; set; } = string.Empty;

    /// <summary>Guests this server shares libraries with.</summary>
    public List<HostShare> HostShares { get; set; } = new();

    // ---------- Guest role ----------

    /// <summary>
    /// How this server reaches itself. The .strm files point at this server's relay endpoint,
    /// and Jellyfin/ffmpeg on this machine fetch them, so localhost is right in almost every setup.
    /// Include the base path if one is configured, e.g. http://127.0.0.1:8096/jellyfin.
    /// </summary>
    public string GuestLocalUrl { get; set; } = "http://127.0.0.1:8096";

    /// <summary>Where synced .strm/.nfo trees are written. Empty = plugin data folder.</summary>
    public string GuestStoragePath { get; set; } = string.Empty;

    /// <summary>Hours between automatic syncs (the scheduled task trigger uses this as its default).</summary>
    public int GuestSyncIntervalHours { get; set; } = 6;

    /// <summary>Shares this server has joined.</summary>
    public List<GuestShare> GuestShares { get; set; } = new();
}

/// <summary>A guest server that may read a set of this server's libraries.</summary>
public class HostShare
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Label for the guest, e.g. "Mike's server".</summary>
    public string GuestName { get; set; } = string.Empty;

    /// <summary>Secret the guest presents in the X-RemoteShare-Token header.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Library (collection folder) ids the guest may see.</summary>
    public List<Guid> LibraryIds { get; set; } = new();

    public bool Enabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenUtc { get; set; }
}

/// <summary>A share this server has joined as a guest.</summary>
public class GuestShare
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Friendly name for the whole share, e.g. "Mike". Used as the default suffix for
    /// local library names so two hosts' "Movies" libraries don't collide.
    /// </summary>
    public string FriendlyName { get; set; } = string.Empty;

    public string HostUrl { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    /// <summary>
    /// Secret embedded in this server's .strm URLs so the relay endpoint can't be used
    /// by anyone who merely reaches this server.
    /// </summary>
    public string RelayKey { get; set; } = string.Empty;

    public List<GuestLibrary> Libraries { get; set; } = new();

    public DateTime? LastSyncUtc { get; set; }

    public string? LastSyncError { get; set; }
}

/// <summary>One remote library within a joined share.</summary>
public class GuestLibrary
{
    /// <summary>The library id on the host.</summary>
    public Guid RemoteId { get; set; }

    public string RemoteName { get; set; } = string.Empty;

    /// <summary>"movies" or "tvshows".</summary>
    public string CollectionType { get; set; } = string.Empty;

    /// <summary>
    /// Name of the local library this becomes. Defaults to "{RemoteName} ({FriendlyName})";
    /// editable to resolve conflicts or taste.
    /// </summary>
    public string LocalName { get; set; } = string.Empty;

    /// <summary>The local library name currently registered, so renames can replace it.</summary>
    public string? RegisteredLocalName { get; set; }

    public bool Enabled { get; set; } = true;

    public int ItemCount { get; set; }
}
