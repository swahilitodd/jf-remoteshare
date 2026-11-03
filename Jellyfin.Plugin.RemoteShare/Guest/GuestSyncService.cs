using Jellyfin.Plugin.RemoteShare.Api;
using Jellyfin.Plugin.RemoteShare.Configuration;
using Jellyfin.Plugin.RemoteShare.Host;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteShare.Guest;

/// <summary>
/// Guest role: joins shares, mirrors each shared library into a local .strm library,
/// and keeps the local Jellyfin library registration (name, path) in step.
/// </summary>
public class GuestSyncService
{
    private readonly ILibraryManager _libraryManager;
    private readonly HostClient _client;
    private readonly ILogger<GuestSyncService> _logger;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public GuestSyncService(ILibraryManager libraryManager, HostClient client, ILogger<GuestSyncService> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _logger = logger;
    }

    private static Plugin P => Plugin.Instance!;

    public static string DefaultLocalName(string remoteName, string friendlyName) =>
        string.IsNullOrWhiteSpace(friendlyName) ? remoteName : $"{remoteName} ({friendlyName.Trim()})";

    /// <summary>Validates a share code against the host and saves the share. Does not sync.</summary>
    public async Task<GuestShare> JoinAsync(string code, string friendlyName, CancellationToken ct)
    {
        var parsed = ShareCode.Decode(code);
        if (P.Configuration.GuestShares.Any(s => s.HostUrl == parsed.HostUrl && s.Token == parsed.Token))
        {
            throw new InvalidOperationException("You've already joined this share.");
        }

        var libraries = await _client.GetLibrariesAsync(parsed.HostUrl, parsed.Token, ct).ConfigureAwait(false);
        var share = new GuestShare
        {
            FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? new Uri(parsed.HostUrl).Host : friendlyName.Trim(),
            HostUrl = parsed.HostUrl,
            Token = parsed.Token,
            RelayKey = HostService.NewSecret(),
        };
        share.Libraries = libraries.Select(l => NewGuestLibrary(l, share.FriendlyName)).ToList();
        EnsureUniqueNames(share, P.Configuration.GuestShares);

        P.Update(c => c.GuestShares.Add(share));
        _logger.LogInformation("Joined Remote Share {Name} at {Host} ({Count} libraries)", share.FriendlyName, share.HostUrl, share.Libraries.Count);
        return share;
    }

    /// <summary>Applies friendly-name / per-library edits. Local library renames happen on the next sync.</summary>
    public void Edit(Guid shareId, string? friendlyName, IEnumerable<(Guid RemoteId, string? LocalName, bool? Enabled)> libraries)
    {
        var c = P.Configuration;
        var share = c.GuestShares.FirstOrDefault(s => s.Id == shareId) ?? throw new KeyNotFoundException();
        var newFriendly = string.IsNullOrWhiteSpace(friendlyName) ? share.FriendlyName : friendlyName.Trim();
        var edits = libraries.ToDictionary(l => l.RemoteId);

        // Work out the result first so a conflict leaves the saved settings untouched.
        var planned = share.Libraries.ToDictionary(l => l.RemoteId, l =>
        {
            var name = l.LocalName;
            if (l.LocalName == DefaultLocalName(l.RemoteName, share.FriendlyName))
            {
                // Still on the auto-generated name: follow the new friendly name.
                name = DefaultLocalName(l.RemoteName, newFriendly);
            }

            var enabled = l.Enabled;
            if (edits.TryGetValue(l.RemoteId, out var e))
            {
                name = string.IsNullOrWhiteSpace(e.LocalName) ? name : e.LocalName.Trim();
                enabled = e.Enabled ?? enabled;
            }

            return (Name: name, Enabled: enabled);
        });

        var conflict = c.GuestShares.Where(s => s.Id != shareId).SelectMany(s => s.Libraries)
            .Where(l => l.Enabled).Select(l => l.LocalName)
            .Concat(planned.Values.Where(p => p.Enabled).Select(p => p.Name))
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (conflict is not null)
        {
            throw new InvalidOperationException($"Two shared libraries would both be called \"{conflict.Key}\". Give one a different name.");
        }

        P.Update(_ =>
        {
            share.FriendlyName = newFriendly;
            foreach (var lib in share.Libraries)
            {
                (lib.LocalName, lib.Enabled) = planned[lib.RemoteId];
            }
        });
    }

    /// <summary>Removes a share: its local libraries, its files, and its configuration.</summary>
    public async Task LeaveAsync(Guid shareId, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var share = P.Configuration.GuestShares.FirstOrDefault(s => s.Id == shareId) ?? throw new KeyNotFoundException();
            foreach (var lib in share.Libraries)
            {
                await UnregisterAsync(lib).ConfigureAwait(false);
            }

            var dir = ShareDir(share);
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }

            P.Update(c => c.GuestShares.RemoveAll(s => s.Id == shareId));
            _libraryManager.QueueLibraryScan();
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task SyncAllAsync(IProgress<double>? progress, CancellationToken ct)
    {
        var shares = P.Configuration.GuestShares.Select(s => s.Id).ToList();
        for (var i = 0; i < shares.Count; i++)
        {
            await SyncAsync(shares[i], ct).ConfigureAwait(false);
            progress?.Report(100.0 * (i + 1) / shares.Count);
        }
    }

    public async Task SyncAsync(Guid shareId, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var share = P.Configuration.GuestShares.FirstOrDefault(s => s.Id == shareId);
            if (share is null)
            {
                return;
            }

            try
            {
                await SyncCoreAsync(share, ct).ConfigureAwait(false);
                Save(share, error: null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Remote Share sync of {Name} failed", share.FriendlyName);
                Save(share, ex.Message);
            }
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncCoreAsync(GuestShare share, CancellationToken ct)
    {
        // Refresh the host's library list: pick up newly shared libraries, drop unshared ones.
        var remote = await _client.GetLibrariesAsync(share.HostUrl, share.Token, ct).ConfigureAwait(false);
        foreach (var gone in share.Libraries.Where(l => remote.All(r => r.Id != l.RemoteId)).ToList())
        {
            await UnregisterAsync(gone).ConfigureAwait(false);
            DeleteDir(LibraryDir(share, gone));
            share.Libraries.Remove(gone);
        }

        foreach (var r in remote)
        {
            var existing = share.Libraries.FirstOrDefault(l => l.RemoteId == r.Id);
            if (existing is null)
            {
                share.Libraries.Add(NewGuestLibrary(r, share.FriendlyName));
            }
            else
            {
                existing.RemoteName = r.Name;
                existing.CollectionType = r.CollectionType;
            }
        }

        EnsureUniqueNames(share, P.Configuration.GuestShares.Where(s => s.Id != share.Id));

        var changed = false;
        foreach (var lib in share.Libraries)
        {
            if (!lib.Enabled)
            {
                changed |= await UnregisterAsync(lib).ConfigureAwait(false);
                DeleteDir(LibraryDir(share, lib));
                lib.ItemCount = 0;
                continue;
            }

            var items = await _client.GetAllItemsAsync(share.HostUrl, share.Token, lib.RemoteId, ct).ConfigureAwait(false);
            var writer = new LibraryWriter(LibraryDir(share, lib), item => StreamUrl(share, item));
            if (lib.CollectionType == "tvshows")
            {
                writer.WriteShows(items);
            }
            else
            {
                writer.WriteMovies(items);
            }

            var removed = writer.RemoveStale();
            foreach (var (itemId, type, path) in writer.PendingImages)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await _client.DownloadImageAsync(share.HostUrl, share.Token, itemId, type, path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Image {Type} for {Item} failed", type, itemId);
                }
            }

            lib.ItemCount = items.Count(i => i.Type is "Movie" or "Episode");
            changed |= writer.Written > 0 || removed > 0 || writer.PendingImages.Count > 0;
            changed |= await EnsureRegisteredAsync(share, lib).ConfigureAwait(false);

            _logger.LogInformation(
                "Remote Share {Share}/{Library}: {Count} items, {Written} files written, {Removed} removed",
                share.FriendlyName, lib.LocalName, lib.ItemCount, writer.Written, removed);
        }

        if (changed)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    /// <summary>Makes sure a local library named LocalName points at this library's folder.</summary>
    private async Task<bool> EnsureRegisteredAsync(GuestShare share, GuestLibrary lib)
    {
        var path = LibraryDir(share, lib);
        var folders = _libraryManager.GetVirtualFolders();

        if (lib.RegisteredLocalName is { } registered && !string.Equals(registered, lib.LocalName, StringComparison.Ordinal))
        {
            await UnregisterAsync(lib).ConfigureAwait(false);
            folders = _libraryManager.GetVirtualFolders();
        }

        var match = folders.FirstOrDefault(f => string.Equals(f.Name, lib.LocalName, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            if (match.Locations.Any(l => string.Equals(Path.GetFullPath(l), Path.GetFullPath(path), StringComparison.Ordinal)))
            {
                lib.RegisteredLocalName = match.Name;
                return false;
            }

            throw new InvalidOperationException(
                $"A library called \"{lib.LocalName}\" already exists on this server. Give the shared library a different name.");
        }

        var options = new LibraryOptions
        {
            PathInfos = new[] { new MediaPathInfo(path) },
            EnableRealtimeMonitor = false,

            // Everything below would make this server pull whole files across the internet.
            EnableChapterImageExtraction = false,
            ExtractChapterImagesDuringLibraryScan = false,
            EnableTrickplayImageExtraction = false,
            ExtractTrickplayImagesDuringLibraryScan = false,
            EnableEmbeddedTitles = false,
            SaveLocalMetadata = false,
        };

        var collectionType = lib.CollectionType == "tvshows" ? CollectionTypeOptions.tvshows : CollectionTypeOptions.movies;
        await _libraryManager.AddVirtualFolder(lib.LocalName, collectionType, options, false).ConfigureAwait(false);
        lib.RegisteredLocalName = lib.LocalName;
        _logger.LogInformation("Added local library {Name} for Remote Share {Share}", lib.LocalName, share.FriendlyName);
        return true;
    }

    private async Task<bool> UnregisterAsync(GuestLibrary lib)
    {
        if (lib.RegisteredLocalName is not { } name)
        {
            return false;
        }

        if (_libraryManager.GetVirtualFolders().Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            await _libraryManager.RemoveVirtualFolder(name, false).ConfigureAwait(false);
        }

        lib.RegisteredLocalName = null;
        return true;
    }

    private static string StreamUrl(GuestShare share, RemoteItemDto item) =>
        $"{P.Configuration.GuestLocalUrl.TrimEnd('/')}/RemoteShare/Relay/{share.Id:N}/{item.Id:N}.{item.Container ?? "mkv"}?key={Uri.EscapeDataString(share.RelayKey)}";

    private static string ShareDir(GuestShare share) => Path.Combine(P.GuestStorageRoot, share.Id.ToString("N"));

    private static string LibraryDir(GuestShare share, GuestLibrary lib) => Path.Combine(ShareDir(share), lib.RemoteId.ToString("N"));

    private static void DeleteDir(string dir)
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static GuestLibrary NewGuestLibrary(RemoteLibraryDto remote, string friendlyName) => new()
    {
        RemoteId = remote.Id,
        RemoteName = remote.Name,
        CollectionType = remote.CollectionType,
        LocalName = DefaultLocalName(remote.Name, friendlyName),
    };

    /// <summary>Appends " 2", " 3"... to local names that collide with other shared libraries.</summary>
    private static void EnsureUniqueNames(GuestShare share, IEnumerable<GuestShare> others)
    {
        var taken = new HashSet<string>(others.SelectMany(s => s.Libraries).Select(l => l.LocalName), StringComparer.OrdinalIgnoreCase);
        foreach (var lib in share.Libraries)
        {
            var name = lib.LocalName;
            for (var n = 2; taken.Contains(name); n++)
            {
                name = $"{lib.LocalName} {n}";
            }

            lib.LocalName = name;
            taken.Add(name);
        }
    }

    /// <summary>Writes the sync result back to the stored share (the sync works on a live reference).</summary>
    private static void Save(GuestShare share, string? error)
    {
        P.Update(_ =>
        {
            share.LastSyncUtc = DateTime.UtcNow;
            share.LastSyncError = error;
        });
    }
}
