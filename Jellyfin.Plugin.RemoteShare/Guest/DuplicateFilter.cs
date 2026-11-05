using Jellyfin.Data.Enums;
using Jellyfin.Plugin.RemoteShare.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.RemoteShare.Guest;

/// <summary>
/// Knows which movies and episodes this server already has in its own libraries, matched by
/// IMDb / TMDb / TVDb id, so shared copies of them can be left out.
/// Items in Remote Share's own storage folder (any share) don't count as "already have".
/// </summary>
public class DuplicateFilter
{
    /// <summary>Provider ids reliable enough to say two items are the same title.</summary>
    private static readonly string[] MatchProviders = { "imdb", "tmdb", "tvdb" };

    private readonly HashSet<string> _movies = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _series = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"{series key}|{season}|{episode}" for every local episode.</summary>
    private readonly HashSet<string> _episodes = new(StringComparer.OrdinalIgnoreCase);

    private DuplicateFilter()
    {
    }

    /// <summary>Indexes this server's own movies and episodes.</summary>
    public static DuplicateFilter Build(ILibraryManager libraryManager, string remoteShareRoot)
    {
        var filter = new DuplicateFilter();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(remoteShareRoot)) + Path.DirectorySeparatorChar;
        bool IsOwn(BaseItem i) =>
            !string.IsNullOrEmpty(i.Path) && !Path.GetFullPath(i.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase);

        var items = libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.Episode },
            Recursive = true,
            IsVirtualItem = false,
        });

        var seriesKeys = new Dictionary<Guid, List<string>>();
        foreach (var series in items.OfType<Series>().Where(IsOwn))
        {
            var keys = Keys(series.ProviderIds);
            seriesKeys[series.Id] = keys;
            filter._series.UnionWith(keys);
        }

        foreach (var item in items.Where(IsOwn))
        {
            switch (item)
            {
                case MediaBrowser.Controller.Entities.Movies.Movie:
                    filter._movies.UnionWith(Keys(item.ProviderIds));
                    break;
                case Episode ep when ep.ParentIndexNumber is { } season && ep.IndexNumber is { } first
                                     && seriesKeys.TryGetValue(ep.SeriesId, out var keys):
                    var last = ep.IndexNumberEnd is { } end && end > first ? end : first;
                    for (var n = first; n <= last; n++)
                    {
                        foreach (var key in keys)
                        {
                            filter._episodes.Add(EpisodeKey(key, season, n));
                        }
                    }

                    break;
            }
        }

        return filter;
    }

    /// <summary>
    /// Removes items this server already has. Movies match by id. Episodes match by their series' id
    /// plus season and episode number, so a show you have part of keeps only the episodes you're missing.
    /// A series is dropped only when it matches one of yours and none of its episodes are left.
    /// </summary>
    /// <returns>The items to keep, and how many movies/episodes were hidden.</returns>
    public (List<RemoteItemDto> Kept, int Hidden) Apply(IReadOnlyList<RemoteItemDto> items)
    {
        var seriesById = items.Where(i => i.Type == "Series").ToDictionary(i => i.Id);
        var kept = new List<RemoteItemDto>(items.Count);
        var hidden = 0;
        var seriesWithEpisodes = new HashSet<Guid>();

        foreach (var item in items)
        {
            switch (item.Type)
            {
                case "Movie" when Keys(item.ProviderIds).Any(_movies.Contains):
                    hidden++;
                    continue;
                case "Episode" when IsLocalEpisode(item, seriesById):
                    hidden++;
                    continue;
                case "Episode" when item.SeriesId is { } sid:
                    seriesWithEpisodes.Add(sid);
                    break;
            }

            kept.Add(item);
        }

        kept.RemoveAll(i => i.Type == "Series" && !seriesWithEpisodes.Contains(i.Id) && Keys(i.ProviderIds).Any(_series.Contains));
        return (kept, hidden);
    }

    private bool IsLocalEpisode(RemoteItemDto ep, Dictionary<Guid, RemoteItemDto> seriesById)
    {
        if (ep.SeriesId is not { } sid || !seriesById.TryGetValue(sid, out var series)
            || ep.SeasonNumber is not { } season || ep.EpisodeNumber is not { } first)
        {
            return false;
        }

        var keys = Keys(series.ProviderIds);
        var last = ep.EpisodeNumberEnd is { } end && end > first ? end : first;

        // A multi-episode file is only hidden if you have every episode in it.
        for (var n = first; n <= last; n++)
        {
            if (!keys.Any(k => _episodes.Contains(EpisodeKey(k, season, n))))
            {
                return false;
            }
        }

        return true;
    }

    private static string EpisodeKey(string seriesKey, int season, int episode) => $"{seriesKey}|{season}|{episode}";

    private static List<string> Keys(IReadOnlyDictionary<string, string>? providerIds) =>
        providerIds is null
            ? new List<string>()
            : providerIds
                .Where(p => !string.IsNullOrWhiteSpace(p.Value) && MatchProviders.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
                .Select(p => $"{p.Key.ToLowerInvariant()}:{p.Value.Trim()}")
                .ToList();
}
