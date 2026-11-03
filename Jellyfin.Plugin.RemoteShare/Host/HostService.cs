using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.RemoteShare.Api;
using Jellyfin.Plugin.RemoteShare.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.RemoteShare.Host;

/// <summary>Host role: authorizes guests and maps this server's libraries to the wire contract.</summary>
public class HostService
{
    private static readonly string[] ImageTypesToShare = { "Primary", "Backdrop", "Thumb", "Logo", "Banner" };

    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<HostService> _logger;

    public HostService(ILibraryManager libraryManager, ILogger<HostService> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public static string NewSecret() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Finds the enabled share a token belongs to (constant-time compare).</summary>
    public HostShare? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var presented = Encoding.UTF8.GetBytes(token);
        foreach (var share in Config.HostShares)
        {
            if (share.Enabled && CryptographicOperations.FixedTimeEquals(presented, Encoding.UTF8.GetBytes(share.Token)))
            {
                if (share.LastSeenUtc is null || DateTime.UtcNow - share.LastSeenUtc > TimeSpan.FromMinutes(5))
                {
                    var id = share.Id;
                    Plugin.Instance!.Update(c =>
                    {
                        var s = c.HostShares.FirstOrDefault(x => x.Id == id);
                        if (s is not null)
                        {
                            s.LastSeenUtc = DateTime.UtcNow;
                        }
                    });
                }

                return share;
            }
        }

        return null;
    }

    /// <summary>All of this server's libraries that the plugin can share (movies and TV).</summary>
    public List<RemoteLibraryDto> GetShareableLibraries()
    {
        var result = new List<RemoteLibraryDto>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            var type = NormalizeCollectionType(folder.CollectionType?.ToString());
            if (type is null || !Guid.TryParse(folder.ItemId, out var id))
            {
                continue;
            }

            result.Add(new RemoteLibraryDto { Id = id, Name = folder.Name, CollectionType = type });
        }

        return result;
    }

    public List<RemoteLibraryDto> GetLibrariesFor(HostShare share) =>
        GetShareableLibraries().Where(l => share.LibraryIds.Contains(l.Id)).ToList();

    public RemoteItemPageDto GetItems(HostShare share, Guid libraryId, int startIndex, int limit)
    {
        if (!share.LibraryIds.Contains(libraryId))
        {
            throw new UnauthorizedAccessException();
        }

        var result = _libraryManager.GetItemsResult(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.Episode },
            AncestorIds = new[] { libraryId },
            Recursive = true,
            IsVirtualItem = false,
            StartIndex = startIndex,
            Limit = Math.Clamp(limit, 1, 1000),
        });

        return new RemoteItemPageDto
        {
            TotalCount = result.TotalRecordCount,
            Items = result.Items.Select(ToDto).Where(d => d is not null).Select(d => d!).ToList(),
        };
    }

    /// <summary>Resolves an item only if it lives in one of the share's libraries.</summary>
    public BaseItem? GetSharedItem(HostShare share, Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return null;
        }

        var inShare = _libraryManager.GetCollectionFolders(item).Any(f => share.LibraryIds.Contains(f.Id));
        if (!inShare)
        {
            _logger.LogWarning("Remote Share guest {Guest} asked for item {Item} outside its libraries", share.GuestName, itemId);
            return null;
        }

        return item;
    }

    public static string? NormalizeCollectionType(string? collectionType) =>
        collectionType?.ToLowerInvariant() switch
        {
            "movies" => "movies",
            "tvshows" => "tvshows",
            _ => null,
        };

    private static RemoteItemDto? ToDto(BaseItem item)
    {
        var dto = new RemoteItemDto
        {
            Id = item.Id,
            Name = item.Name ?? string.Empty,
            OriginalTitle = item.OriginalTitle,
            Year = item.ProductionYear,
            Overview = item.Overview,
            PremiereDate = item.PremiereDate,
            OfficialRating = item.OfficialRating,
            CommunityRating = item.CommunityRating,
            Genres = item.Genres ?? Array.Empty<string>(),
            Studios = item.Studios ?? Array.Empty<string>(),
            ProviderIds = item.ProviderIds is null ? new() : new Dictionary<string, string>(item.ProviderIds),
            RunTimeTicks = item.RunTimeTicks,
            DateModified = item.DateModified,
            Images = ImageTypesToShare
                .Where(t => Enum.TryParse<ImageType>(t, out var it) && item.HasImage(it, 0))
                .ToArray(),
        };

        switch (item)
        {
            case Movie:
                dto.Type = "Movie";
                dto.Container = ContainerOf(item);
                break;
            case Series:
                dto.Type = "Series";
                break;
            case Episode ep:
                dto.Type = "Episode";
                dto.SeriesId = ep.SeriesId;
                dto.SeriesName = ep.SeriesName;
                dto.SeasonNumber = ep.ParentIndexNumber;
                dto.EpisodeNumber = ep.IndexNumber;
                dto.EpisodeNumberEnd = ep.IndexNumberEnd;
                dto.Container = ContainerOf(item);
                break;
            default:
                return null;
        }

        // Only offer playable things that are plain files we can serve.
        if (dto.Type != "Series" && dto.Container is null)
        {
            return null;
        }

        return dto;
    }

    private static string? ContainerOf(BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path) || !item.IsFileProtocol || !File.Exists(item.Path))
        {
            return null;
        }

        var ext = Path.GetExtension(item.Path).TrimStart('.').ToLowerInvariant();
        return ext.Length == 0 || ext == "strm" ? null : ext;
    }
}
