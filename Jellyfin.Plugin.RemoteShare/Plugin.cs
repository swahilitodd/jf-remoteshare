using Jellyfin.Plugin.RemoteShare.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.RemoteShare;

/// <summary>
/// Remote Share: share libraries with other Jellyfin servers (host role) and mount libraries
/// shared with you as local libraries (guest role).
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly object _configLock = new();

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Remote Share";

    public override Guid Id => Guid.Parse("9a871b46-6581-4f0c-a1cd-54da8de66932");

    public override string Description =>
        "Share libraries with other Jellyfin servers, and add libraries shared with you to your own.";

    /// <summary>Folder for guest .strm/.nfo trees.</summary>
    public string GuestStorageRoot =>
        string.IsNullOrWhiteSpace(Configuration.GuestStoragePath)
            ? Path.Combine(DataFolderPath, "shares")
            : Configuration.GuestStoragePath;

    /// <summary>Mutates and saves configuration under a lock.</summary>
    public void Update(Action<PluginConfiguration> mutate)
    {
        lock (_configLock)
        {
            mutate(Configuration);
            SaveConfiguration();
        }
    }

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "RemoteShare",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }
}
