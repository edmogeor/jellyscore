using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("checks")]

namespace Jellyfin.Plugin.JellyScore;

public sealed class NewItemWorker(ILibraryManager library, ICollectionManager collections, ThemeService themes, ILogger<NewItemWorker> logger) : BackgroundService
{
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(JellyScoreConstants.ScanQueueCapacity) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
    private readonly HashSet<Guid> _queued = [];
    private readonly Lock _gate = new();

    public override Task StartAsync(CancellationToken ct)
    {
        library.ItemAdded += Added;
        collections.ItemsAddedToCollection += CollectionUpdated;
        return base.StartAsync(ct);
    }

    private void Added(object? sender, ItemChangeEventArgs args)
    {
        if (!Plugin.Instance.Configuration.Enabled || args.Item is not (Movie or Series or BoxSet) || args.Item.ExtraType is not null || args.Item.IsVirtualItem) return;
        Enqueue(args.Item.Id);
    }

    private void CollectionUpdated(object? sender, CollectionModifiedEventArgs args)
    {
        if (!Plugin.Instance.Configuration.Enabled) return;
        Enqueue(args.Collection.Id);
    }

    private void Enqueue(Guid id)
    {
        lock (_gate)
        {
            if (_queued.Add(id) && !_queue.Writer.TryWrite(id)) _queued.Remove(id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
        {
            try
            {
                // Library item creation often precedes metadata and directory availability.
                await Task.Delay(TimeSpan.FromSeconds(JellyScoreConstants.ItemReadyDelaySeconds), ct);
                for (var attempt = 0; attempt < JellyScoreConstants.MetadataRetryAttempts; attempt++)
                {
                    try { await themes.Process(id, false, ct); break; }
                    catch (InvalidOperationException e) when (e.Message.Contains("not ready", StringComparison.Ordinal) && attempt < JellyScoreConstants.MetadataRetryAttempts - 1)
                    { await Task.Delay(TimeSpan.FromSeconds(JellyScoreConstants.MetadataRetryDelaySeconds), ct); }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Theme processing for {ItemId} failed: {Message}", id, e.Message); }
            finally { lock (_gate) _queued.Remove(id); }
        }
    }

    public override async Task StopAsync(CancellationToken ct)
    {
        library.ItemAdded -= Added;
        collections.ItemsAddedToCollection -= CollectionUpdated;
        _queue.Writer.TryComplete();
        await base.StopAsync(ct);
    }
}

public sealed class LibraryScanWorker(ITaskManager tasks) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        tasks.TaskCompleted += Completed;
        return Task.CompletedTask;
    }

    private void Completed(object? sender, TaskCompletionEventArgs args)
    {
        if (args.Task.ScheduledTask.Key == JellyScoreConstants.LibraryRefreshKey && args.Result.Status == TaskCompletionStatus.Completed && Plugin.Instance.Configuration.ScanOnLibraryRefresh)
            tasks.QueueIfNotRunning<ThemeScan>();
    }

    public Task StopAsync(CancellationToken ct)
    {
        tasks.TaskCompleted -= Completed;
        return Task.CompletedTask;
    }
}

// ReSharper disable UnusedAutoPropertyAccessor.Global
public sealed class ScanStatus
{
    public Guid RunId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    internal DateTimeOffset LastCompletedAt { get; set; }
    internal double? PriorKnownSecondsPerItem { get; init; }
    internal double? PriorOtherSecondsPerItem { get; init; }
    internal int KnownTotal { get; set; }
    internal int KnownProcessed { get; set; }
    internal double KnownSeconds { get; set; }
    internal double OtherSeconds { get; set; }
    internal bool CurrentKnown { get; set; }
    public bool Running { get; set; }
    public bool Cancelled { get; set; }
    public string? StoppedReason { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CurrentItem { get; set; }
    public ScanActiveItem[] ActiveItems { get; set; } = [];
    public int Processed { get; set; }
    public int Total { get; set; }
    public int Added { get; set; }
    public int AlreadyThemed { get; set; }
    public int Excluded { get; set; }
    public int NoMatch { get; set; }
    public ScanIssue[] Issues { get; set; } = [];
    public int Unsupported { get; set; }
    public int Failed { get; set; }
    // ReSharper disable once UnusedMember.Global
    public string? ToolSetupStage => YouTube.ToolSetupStage;
    // ReSharper disable once UnusedMember.Global
    public DateTimeOffset? RateLimitedUntil => YouTube.RateLimitedUntil;
    // ReSharper disable once UnusedMember.Global
    public double? RemainingSeconds
    {
        get
        {
            if (!Running || Total <= Processed || Total == 0) return null;
            var knownRemaining = KnownTotal - KnownProcessed;
            var otherProcessed = Processed - KnownProcessed;
            var otherRemaining = Total - KnownTotal - otherProcessed;
            var known = Average(KnownSeconds, KnownProcessed, PriorKnownSecondsPerItem, JellyScoreConstants.ScanDefaultKnownSecondsPerItem);
            var other = Average(OtherSeconds, otherProcessed, PriorOtherSecondsPerItem, JellyScoreConstants.ScanDefaultOtherSecondsPerItem);
            var current = CurrentKnown ? known : other;
            var stalledFor = Math.Max(0, (DateTimeOffset.UtcNow - LastCompletedAt).TotalSeconds - current);
            return knownRemaining * known + otherRemaining * other + stalledFor;
        }
    }

    private static double Average(double seconds, int count, double? prior, double initial)
    {
        if (prior is > 0 and < JellyScoreConstants.ScanMaximumPriorSecondsPerItem)
            return (seconds + JellyScoreConstants.ScanPriorItems * prior.Value) / (count + JellyScoreConstants.ScanPriorItems);
        if (count >= JellyScoreConstants.ScanPriorItems) return seconds / count;
        return (seconds + (JellyScoreConstants.ScanPriorItems - count) * initial) / JellyScoreConstants.ScanPriorItems;
    }
}
// ReSharper restore UnusedAutoPropertyAccessor.Global

// ReSharper disable NotAccessedPositionalProperty.Global
public sealed record ScanIssue(string Name, string Code, bool Failed, DateTimeOffset At, string? Diagnostic);
public sealed record ScanActiveItem(string Name, string Stage);
// ReSharper restore NotAccessedPositionalProperty.Global

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ThemeScan(ILibraryManager library, ThemeService themes, Store store, ILogger<ThemeScan> logger) : IScheduledTask
{
    private static ScanStatus _status = new();
    public string Name => "Scan with JellyScore";
    public string Key => JellyScoreConstants.ScanTaskKey;
    public string Description => "Find themes in selected movie and TV libraries.";
    public string Category => "Library";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    public ScanStatus Status => _status;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var (knownIds, knownPrior, otherPrior) = store.Read(s => (s.Themes.Keys.ToHashSet(), s.ScanKnownSecondsPerItem, s.ScanOtherSecondsPerItem));
        _status = new ScanStatus { RunId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Running = true,
            PriorKnownSecondsPerItem = knownPrior, PriorOtherSecondsPerItem = otherPrior };
        var status = _status;
        try
        {
            themes.ResetSuppression();
            var selected = Plugin.Instance.Configuration.SelectedLibraries(library);
            if (selected.Length == 0) { progress.Report(JellyScoreConstants.ProgressComplete); return; }
            var query = new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Series, BaseItemKind.BoxSet], AncestorIds = selected, Recursive = true };
            var items = new List<(Guid Id, string Name, bool Known)>();
            for (var offset = 0; ; offset += JellyScoreConstants.ScanBatchSize)
            {
                ct.ThrowIfCancellationRequested();
                query.StartIndex = offset;
                query.Limit = JellyScoreConstants.ScanBatchSize;
                var batch = library.GetItemList(query).ToArray();
                items.AddRange(batch.Select(item => (item.Id, item.Name, knownIds.Contains(item.Id))));
                if (batch.Length < JellyScoreConstants.ScanBatchSize) break;
            }
            status.Total = items.Count;
            status.KnownTotal = items.Count(item => item.Known);
            status.StartedAt = DateTimeOffset.UtcNow;
            status.LastCompletedAt = status.StartedAt;
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                lock (status)
                {
                    status.CurrentKnown = item.Known;
                    status.ActiveItems = [new ScanActiveItem(item.Name, "stagePreparing")];
                    status.CurrentItem = item.Name;
                }
                try
                {
                    var result = await themes.Process(item.Id, false, ct, stage =>
                    {
                        lock (status) status.ActiveItems = [new ScanActiveItem(item.Name, stage)];
                    });
                    lock (status)
                    {
                        if (result.Result == "Added") status.Added++;
                        else if (result.Result == "Already themed") status.AlreadyThemed++;
                        else if (result.Result == "Previously used recording excluded") status.Excluded++;
                        else status.NoMatch++;
                        if (result.ReasonCode is { } code)
                        {
                            AddIssue(status, new ScanIssue(item.Name, code, false, DateTimeOffset.UtcNow, null));
                            logger.LogDebug("Skipped {ItemId}: {Reason}", item.Id, themes.Outcome(item.Id));
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    lock (status)
                    {
                        status.Unsupported++;
                        AddIssue(status, new ScanIssue(item.Name, "scanUnsupported", false, DateTimeOffset.UtcNow, null));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (RateLimitFailure e)
                {
                    var diagnostic = $"Theme scan paused for {item.Name} ({item.Id}): {e.Message}";
                    lock (status)
                    {
                        status.StoppedReason = "scanRateLimited";
                        AddIssue(status, new ScanIssue(item.Name, "scanRateLimited", true, DateTimeOffset.UtcNow, diagnostic));
                    }
                    logger.LogWarning("Theme scan paused for {Name} ({ItemId}): {Message}", item.Name, item.Id, e.Message);
                    throw;
                }
                catch (Exception e)
                {
                    var diagnostic = $"Theme scan failed for {item.Name} ({item.Id}): {e.Message}";
                    lock (status)
                    {
                        status.Failed++;
                        AddIssue(status, new ScanIssue(item.Name, e switch
                        {
                            SearchFailure => "searchFailed",
                            DownloadFailure => "downloadFailed",
                            _ => "scanItemFailed"
                        }, true, DateTimeOffset.UtcNow, diagnostic));
                    }
                    logger.LogWarning("Theme scan failed for {Name} ({ItemId}): {Message}", item.Name, item.Id, e.Message);
                }
                finally { lock (status) { status.ActiveItems = []; status.CurrentItem = null; } }
                lock (status)
                {
                    var completedAt = DateTimeOffset.UtcNow;
                    if (item.Known) { status.KnownProcessed++; status.KnownSeconds += (completedAt - status.LastCompletedAt).TotalSeconds; }
                    else status.OtherSeconds += (completedAt - status.LastCompletedAt).TotalSeconds;
                    status.Processed++;
                    status.LastCompletedAt = completedAt;
                    progress.Report((double)JellyScoreConstants.ProgressComplete * status.Processed / status.Total);
                }
            }
            progress.Report(JellyScoreConstants.ProgressComplete);
            if (status.Processed > 0)
                store.Change(s =>
                {
                    if (status.KnownProcessed > 0) s.ScanKnownSecondsPerItem = status.KnownSeconds / status.KnownProcessed;
                    var otherProcessed = status.Processed - status.KnownProcessed;
                    if (otherProcessed > 0) s.ScanOtherSecondsPerItem = status.OtherSeconds / otherProcessed;
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { status.Cancelled = true; throw; }
        catch (Exception) { status.StoppedReason ??= "scanStopped"; throw; }
        finally { status.Running = false; status.FinishedAt = DateTimeOffset.UtcNow; status.CurrentItem = null; status.ActiveItems = []; }
    }

    private static void AddIssue(ScanStatus status, ScanIssue issue) =>
        status.Issues = [.. status.Issues.TakeLast(JellyScoreConstants.ScanRecentIssues - 1), issue];
}
