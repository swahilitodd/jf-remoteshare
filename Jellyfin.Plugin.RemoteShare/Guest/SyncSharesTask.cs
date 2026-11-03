using MediaBrowser.Model.Tasks;

namespace Jellyfin.Plugin.RemoteShare.Guest;

/// <summary>Scheduled task (Dashboard → Scheduled Tasks) that re-syncs every joined share.</summary>
public class SyncSharesTask : IScheduledTask
{
    private readonly GuestSyncService _guest;

    public SyncSharesTask(GuestSyncService guest)
    {
        _guest = guest;
    }

    public string Name => "Sync remote shares";

    public string Key => "RemoteShareSync";

    public string Description => "Updates libraries shared with this server from their host servers.";

    public string Category => "Remote Share";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken) =>
        _guest.SyncAllAsync(progress, cancellationToken);

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Math.Max(1, Plugin.Instance?.Configuration.GuestSyncIntervalHours ?? 6);
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(hours).Ticks,
        };
        yield return new TaskTriggerInfo { Type = TaskTriggerInfoType.StartupTrigger };
    }
}
