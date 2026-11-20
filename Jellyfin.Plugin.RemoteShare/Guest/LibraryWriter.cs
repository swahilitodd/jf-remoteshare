using System.Globalization;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.RemoteShare.Api;

namespace Jellyfin.Plugin.RemoteShare.Guest;

/// <summary>
/// Turns a remote item list into a local folder of .strm + .nfo + artwork that Jellyfin's
/// normal movie / TV scanners understand. Only rewrites files whose content changed, so
/// re-syncs don't trigger needless metadata refreshes.
/// </summary>
public class LibraryWriter
{
    private static readonly Dictionary<string, string> ImageFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Primary"] = "poster",
        ["Backdrop"] = "fanart",
        ["Thumb"] = "landscape",
        ["Logo"] = "clearlogo",
        ["Banner"] = "banner",
    };

    private readonly string _root;
    private readonly Func<RemoteItemDto, string> _streamUrl;
    private readonly HashSet<string> _keep = new(StringComparer.Ordinal);

    public LibraryWriter(string root, Func<RemoteItemDto, string> streamUrl)
    {
        _root = root;
        _streamUrl = streamUrl;
        Directory.CreateDirectory(root);
    }

    /// <summary>Images to fetch: (item id, image type, absolute destination path).</summary>
    public List<(Guid ItemId, string ImageType, string Path)> PendingImages { get; } = new();

    public int Written { get; private set; }

    public void WriteMovies(IEnumerable<RemoteItemDto> items)
    {
        foreach (var movie in items.Where(i => i.Type == "Movie"))
        {
            var name = WithYear(movie.Name, movie.Year);
            var dir = Path.Combine(_root, Safe(name));
            var stem = Path.Combine(dir, Safe(name));

            WriteIfChanged(stem + ".strm", _streamUrl(movie));
            WriteIfChanged(stem + ".nfo", Nfo("movie", movie, extra: null));
            WriteEdl(stem, movie);
            QueueImages(movie, img => Path.Combine(dir, ImageFileNames[img] + ".jpg"));
        }
    }

    public void WriteShows(IEnumerable<RemoteItemDto> items)
    {
        var list = items.ToList();
        var seriesById = list.Where(i => i.Type == "Series").ToDictionary(i => i.Id);
        var seriesDirs = new Dictionary<Guid, string>();

        string SeriesDir(Guid? seriesId, string? fallbackName)
        {
            if (seriesId is { } id && seriesDirs.TryGetValue(id, out var known))
            {
                return known;
            }

            string dir;
            if (seriesId is { } sid && seriesById.TryGetValue(sid, out var series))
            {
                dir = Path.Combine(_root, Safe(WithYear(series.Name, series.Year)));
                WriteIfChanged(Path.Combine(dir, "tvshow.nfo"), Nfo("tvshow", series, extra: null));
                QueueImages(series, img => Path.Combine(dir, ImageFileNames[img] + ".jpg"));
            }
            else
            {
                dir = Path.Combine(_root, Safe(fallbackName ?? "Unknown Series"));
            }

            if (seriesId is { } key)
            {
                seriesDirs[key] = dir;
            }

            return dir;
        }

        foreach (var ep in list.Where(i => i.Type == "Episode"))
        {
            var season = ep.SeasonNumber ?? 1;
            var seriesDir = SeriesDir(ep.SeriesId, ep.SeriesName);
            var seasonDir = Path.Combine(seriesDir, season == 0 ? "Specials" : $"Season {season:00}");

            var code = ep.EpisodeNumber is { } n
                ? $"S{season:00}E{n:00}" + (ep.EpisodeNumberEnd is { } end && end > n ? $"-E{end:00}" : string.Empty)
                : Safe(ep.Name);
            var seriesName = Path.GetFileName(seriesDir);
            var stem = Path.Combine(seasonDir, $"{seriesName} {code}");

            WriteIfChanged(stem + ".strm", _streamUrl(ep));
            WriteIfChanged(stem + ".nfo", Nfo("episodedetails", ep, w =>
            {
                Element(w, "season", season.ToString(CultureInfo.InvariantCulture));
                if (ep.EpisodeNumber is { } e)
                {
                    Element(w, "episode", e.ToString(CultureInfo.InvariantCulture));
                }
            }));

            WriteEdl(stem, ep);

            if (ep.Images.Contains("Primary"))
            {
                QueueImage(ep.Id, "Primary", stem + "-thumb.jpg");
            }
        }

        // Series with no shared episodes (all filtered) still get a folder so they show up.
        foreach (var series in seriesById.Values)
        {
            SeriesDir(series.Id, series.Name);
        }
    }

    /// <summary>Deletes everything under the root that this sync didn't produce.</summary>
    public int RemoveStale()
    {
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            if (!_keep.Contains(file))
            {
                File.Delete(file);
                removed++;
            }
        }

        foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }

        return removed;
    }

    /// <summary>
    /// Writes the host's EDL beside the .strm (Movie.strm -> Movie.edl), where the Ember EDL
    /// Sidecars plugin looks for it. If the host no longer has one, RemoveStale deletes it.
    /// </summary>
    private void WriteEdl(string stem, RemoteItemDto item)
    {
        if (!string.IsNullOrEmpty(item.Edl))
        {
            WriteIfChanged(stem + ".edl", item.Edl);
        }
    }

    private void QueueImages(RemoteItemDto item, Func<string, string> pathFor)
    {
        foreach (var type in item.Images.Where(ImageFileNames.ContainsKey))
        {
            QueueImage(item.Id, type, pathFor(type));
        }
    }

    private void QueueImage(Guid itemId, string type, string path)
    {
        _keep.Add(path);
        if (!File.Exists(path))
        {
            PendingImages.Add((itemId, type, path));
        }
    }

    private void WriteIfChanged(string path, string content)
    {
        _keep.Add(path);
        if (File.Exists(path) && File.ReadAllText(path) == content)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        Written++;
    }

    private static string Nfo(string root, RemoteItemDto item, Action<XmlWriter>? extra)
    {
        var sb = new StringBuilder();
        var settings = new XmlWriterSettings { Indent = true, OmitXmlDeclaration = false, Encoding = new UTF8Encoding(false) };
        using (var w = XmlWriter.Create(new StringWriterUtf8(sb), settings))
        {
            w.WriteStartElement(root);
            Element(w, "title", item.Name);
            Element(w, "originaltitle", item.OriginalTitle);
            Element(w, "plot", item.Overview);
            Element(w, "year", item.Year?.ToString(CultureInfo.InvariantCulture));
            Element(w, root == "episodedetails" ? "aired" : "premiered", item.PremiereDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            Element(w, "mpaa", item.OfficialRating);
            Element(w, "rating", item.CommunityRating?.ToString(CultureInfo.InvariantCulture));
            if (item.RunTimeTicks is { } ticks)
            {
                Element(w, "runtime", ((int)TimeSpan.FromTicks(ticks).TotalMinutes).ToString(CultureInfo.InvariantCulture));
            }

            foreach (var g in item.Genres)
            {
                Element(w, "genre", g);
            }

            foreach (var s in item.Studios)
            {
                Element(w, "studio", s);
            }

            var first = true;
            foreach (var (provider, value) in item.ProviderIds)
            {
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                w.WriteStartElement("uniqueid");
                w.WriteAttributeString("type", provider.ToLowerInvariant());
                if (first)
                {
                    w.WriteAttributeString("default", "true");
                    first = false;
                }

                w.WriteString(value);
                w.WriteEndElement();
            }

            extra?.Invoke(w);
            w.WriteEndElement();
        }

        return sb.ToString();
    }

    private static void Element(XmlWriter w, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            w.WriteElementString(name, value);
        }
    }

    private static string WithYear(string name, int? year) => year is { } y ? $"{name} ({y})" : name;

    /// <summary>Makes a string safe as a file or folder name on every OS Jellyfin runs on.</summary>
    public static string Safe(string name)
    {
        var invalid = new HashSet<char>(Path.GetInvalidFileNameChars()) { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };
        var cleaned = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return cleaned.Length == 0 ? "_" : cleaned.Length > 120 ? cleaned[..120] : cleaned;
    }

    private sealed class StringWriterUtf8 : StringWriter
    {
        public StringWriterUtf8(StringBuilder sb)
            : base(sb, CultureInfo.InvariantCulture)
        {
        }

        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
