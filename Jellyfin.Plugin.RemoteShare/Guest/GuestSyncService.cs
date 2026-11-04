using System.Collections.Concurrent;
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

    /// <summary>What each share's sync is doing right now, for the settings page. Absent = idle.</summary>
    private readonly ConcurrentDictionary<Guid, string> _status = new();

    public GuestSyncService(ILibraryManager libraryManager, HostClient client, ILogger<GuestSyncService> logger)
    {
        _libraryManager = libraryManager;
        _client = client;
        _logger = logger;
    }

    private static Plugin P => Plugin.Instance!;

    /// <summary>Current sync activity for a share, or null when no sync is running or queued.</summary>
    public string? StatusOf(Guid shareId) => _status.TryGetValue(shareId, out var s) ? s : null;

    private void SetStatus(Guid shareId, string status) => _status[shareId] = status;

    public static string DefaultLocalName(string remoteName, string friendlyName) =>
        string.IsNullOrWhiteSpace(friendlyName) ? remoteName : $"{remoteName} ({friendlyName.Trim()})";

    /// <summary>Validates a share code against the host and saves the share. Does not sync.</summary>
    public async Task<GuestShare> JoinAsync(string code, string friendlyName, bool trustCertificate, CancellationToken ct)
    {
        var parsed = ShareCode.Decode(code);
        if (P.Configuration.GuestShares.Any(s => s.HostUrl == parsed.HostUrl && s.Token == parsed.Token))
        {
            throw new InvalidOperationException("You've already joined this share.");
        }

        var share = new GuestShare
        {
            FriendlyName = string.IsNullOrWhiteSpace(friendlyName) ? new Uri(parsed.HostUrl).Host : friendlyName.Trim(),
            HostUrl = parsed.HostUrl,
            Token = parsed.Token,
            RelayKey = HostService.NewSecret(),
        };
        var libraries = trustCertificate
            ? await _client.GetLibrariesTrustingCertificateAsync(share, ct).ConfigureAwait(false)
            : await _client.GetLibrariesAsync(share, ct).ConfigureAwait(false);
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
        _status.TryAdd(shareId, "Waiting for another sync to finish");
        try
        {
            await _syncLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _status.TryRemove(shareId, out _);
            throw;
        }

        try
        {
            var share = P.Configuration.GuestShares.FirstOrDefault(s => s.Id == shareId);
            if (share is null)
            {
                return;
            }

            _logger.LogInformation("Remote Share sync of {Name} started", share.FriendlyName);
            SetStatus(shareId, "Starting");
            try
            {
                await SyncCoreAsync(share, ct).ConfigureAwait(false);
                Save(share, error: null);
                _logger.LogInformation("Remote Share sync of {Name} finished", share.FriendlyName);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning("Remote Share sync of {Name} was cancelled", share.FriendlyName);
                Save(share, "The last sync was cancelled before it finished.");
                throw;
            }
            catch (Exception ex)
            {
                // Includes cancellations we didn't ask for (timeouts): those must show up as errors, not vanish.
                _logger.LogError(ex, "Remote Share sync of {Name} failed", share.FriendlyName);
                Save(share, ex is OperationCanceledException ? "Timed out talking to the host." : ex.Message);
            }
        }
        finally
        {
            _status.TryRemove(shareId, out _);
            _syncLock.Release();
        }
    }

    /// <summary>Starts a sync in the background; failures are recorded on the share, never thrown.</summary>
    public void StartSync(Guid shareId)
    {
        _status.TryAdd(shareId, "Waiting for another sync to finish");
        _ = Task.Run(async () =>
        {
            try
            {
                await SyncAsync(shareId, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Remote Share background sync crashed");
            }
        });
    }

    private async Task SyncCoreAsync(GuestShare share, CancellationToken ct)
    {
        // Refresh the host's library list: pick up newly shared libraries, drop unshared ones.
        SetStatus(share.Id, "Contacting host");
        var remote = await _client.GetLibrariesAsync(share, ct).ConfigureAwait(false);
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

            SetStatus(share.Id, $"{lib.LocalName}: fetching item list");
            var items = await _client.GetAllItemsAsync(share, lib.RemoteId, ct).ConfigureAwait(false);
            SetStatus(share.Id, $"{lib.LocalName}: writing files");
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
            lib.ItemCount = items.Count(i => i.Type is "Movie" or "Episode");

            // Register before fetching artwork so the library exists even if artwork is slow or partly fails.
            SetStatus(share.Id, $"{lib.LocalName}: adding library");
            changed |= await EnsureRegisteredAsync(share, lib).ConfigureAwait(false);

            var imageFailures = await DownloadImagesAsync(share, lib, writer.PendingImages, ct).ConfigureAwait(false);
            changed |= writer.Written > 0 || removed > 0 || writer.PendingImages.Count > imageFailures;

            _logger.LogInformation(
                "Remote Share {Share}/{Library}: {Count} items, {Written} files written, {Removed} removed, {Images} images fetched, {Failed} images failed",
                share.FriendlyName, lib.LocalName, lib.ItemCount, writer.Written, removed, writer.PendingImages.Count - imageFailures, imageFailures);
        }

        if (changed)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    /// <summary>Fetches artwork a few at a time. A failed image is skipped (retried next sync); returns how many failed.</summary>
    private async Task<int> DownloadImagesAsync(GuestShare share, GuestLibrary lib, List<(Guid ItemId, string ImageType, string Path)> images, CancellationToken ct)
    {
        if (images.Count == 0)
        {
            return 0;
        }

        var done = 0;
        var failed = 0;
        SetStatus(share.Id, $"{lib.LocalName}: artwork 0 of {images.Count}");
        var options = new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct };
        await Parallel.ForEachAsync(images, options, async (img, token) =>
        {
            try
            {
                await _client.DownloadImageAsync(share, img.ItemId, img.ImageType, img.Path, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                Interlocked.Increment(ref failed);
                _logger.LogDebug(ex, "Image {Type} for {Item} failed", img.ImageType, img.ItemId);
            }

            var n = Interlocked.Increment(ref done);
            if (n % 20 == 0 || n == images.Count)
            {
                SetStatus(share.Id, $"{lib.LocalName}: artwork {n} of {images.Count}");
            }
        }).ConfigureAwait(false);

        if (failed > 0)
        {
            _logger.LogWarning("Remote Share {Share}/{Library}: {Failed} of {Total} images couldn't be fetched; they'll be retried next sync",
                share.FriendlyName, lib.LocalName, failed, images.Count);
        }

        return failed;
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
