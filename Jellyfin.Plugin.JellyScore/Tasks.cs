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

public sealed record ThemeProcessingJob(ManagedTheme Theme, string? YouTubeUrl, bool Replacement, string Stage = "queued", bool Processing = true, string? Code = null, string? Result = null)
{
    public Guid RequestId { get; } = Guid.NewGuid();
}

public sealed class ThemeProcessingWorker(ThemeService themes, ILogger<ThemeProcessingWorker> logger) : BackgroundService
{
    private readonly Channel<ThemeProcessingJob> _queue = Channel.CreateBounded<ThemeProcessingJob>(new BoundedChannelOptions(JellyScoreConstants.ScanQueueCapacity) { SingleReader = true });
    private readonly Dictionary<Guid, ThemeProcessingJob> _jobs = new();
    private readonly Lock _gate = new();
    private ScanStatus _status = new() { Kind = "queue" };
    private ScanStatus _lastStatus = new() { Kind = "queue" };
    private CancellationTokenSource? _runCancellation;

    public ScanStatus Status { get { lock (_gate) return _status.Snapshot(); } }
    public ScanStatus LastStatus { get { lock (_gate) return _lastStatus.Snapshot(); } }

    public bool IsBusy { get { lock (_gate) return _status.Running; } }

    public ThemeProcessingJob[] List()
    {
        lock (_gate) return _jobs.Values.GroupBy(job => job.Theme.ItemId)
            .Select(jobs => jobs.FirstOrDefault(job => job.Processing) ?? jobs.Last()).ToArray();
    }

    public void Enqueue(Guid id, string? youtubeUrl, bool replacement = false)
    {
        lock (_gate)
        {
            if (_status.Cancelling) throw new InvalidOperationException("Theme processing is cancelling; retry after it stops.");
            var job = new ThemeProcessingJob(themes.PrepareRequest(id, replacement, youtubeUrl), youtubeUrl, replacement);
            _jobs[job.RequestId] = job;
            if (!_queue.Writer.TryWrite(job))
            {
                _jobs.Remove(job.RequestId);
                throw new InvalidOperationException("The processing queue is full. Please retry.");
            }
            if (!_status.Running)
            {
                _runCancellation = new CancellationTokenSource();
                _status = new ScanStatus { Kind = "queue", RunId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow,
                    LastCompletedAt = DateTimeOffset.UtcNow, Running = true, Prepared = true,
                    PriorOtherSecondsPerItem = _lastStatus.Processed > _lastStatus.KnownProcessed ? _lastStatus.OtherSeconds / (_lastStatus.Processed - _lastStatus.KnownProcessed) : null };
            }
            _status.Total++;
            RemoveCompleted(id, job.RequestId);
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (!_status.Running) return;
            _status.Cancelling = true;
            _status.Cancelled = true;
            foreach (var job in _jobs.Values.Where(job => job.Processing && job.Stage == "queued").ToArray())
                _jobs.Remove(job.RequestId);
            _runCancellation?.Cancel();
            FinishIfIdle();
        }
    }

    private void FinishIfIdle()
    {
        if (!_status.Running || _jobs.Values.Any(job => job.Processing)) return;
        _status.Finish();
        _lastStatus = _status.Snapshot();
        _runCancellation?.Dispose();
        _runCancellation = null;
    }

    private void RemoveCompleted(Guid itemId, Guid except)
    {
        foreach (var job in _jobs.Values.Where(job => job.Theme.ItemId == itemId && !job.Processing && job.RequestId != except).ToArray())
            _jobs.Remove(job.RequestId);
    }

    public bool Dismiss(Guid itemId)
    {
        lock (_gate)
        {
            var jobs = _jobs.Values.Where(job => job.Theme.ItemId == itemId).ToArray();
            if (jobs.Length == 0 || jobs.Any(job => job.Processing || job.Replacement || job.Code is null)) return false;
            foreach (var job in jobs) _jobs.Remove(job.RequestId);
            return true;
        }
    }

    private void Update(Guid id, string stage, bool processing = true, string? code = null, string? result = null)
    {
        lock (_gate)
        {
            _jobs[id] = _jobs[id] with { Stage = stage, Processing = processing, Code = code, Result = result };
            if (processing) _status.ActiveItems = [new ScanActiveItem(_jobs[id].Theme.Name, stage)];
            if (!processing) RemoveCompleted(_jobs[id].Theme.ItemId, id);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct))
        {
            var id = job.RequestId;
            CancellationTokenSource cancellation;
            lock (_gate)
            {
                if (!_jobs.ContainsKey(id)) continue;
                Update(id, "stagePreparing");
                _status.CurrentItem = job.Theme.Name;
                _status.LastCompletedAt = DateTimeOffset.UtcNow;
                cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _runCancellation!.Token);
            }
            using var runCancellation = cancellation;
            try
            {
                var result = await themes.Process(job.Theme.ItemId, job.Replacement, cancellation.Token, stage => Update(id, stage), job.YouTubeUrl, administratorAdd: !job.Replacement);
                lock (_gate)
                {
                    _status.RecordResult(job.Theme.Name, result.ReasonCode is null && result.Result is not ("Added" or "Replaced" or "Already themed")
                        ? result with { ReasonCode = "reasonNoMatch" } : result, job.Theme.ItemId);
                    _status.CompleteItem(false);
                    if (!job.Replacement && result.Result is "Added" or "Already themed") _jobs.Remove(id);
                    else Update(id, "Finished", false, result.ReasonCode ?? (job.Replacement ? null : "reasonNoMatch"), result.Result);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                lock (_gate) { _jobs.Remove(id); _status.Cancelled = true; }
                if (ct.IsCancellationRequested) break;
            }
            catch (Exception e)
            {
                var code = ThemeService.ErrorCode(e, job.Replacement ? "refreshFailed" : "requestFailed");
                var diagnostic = $"Queued theme processing failed for {job.Theme.Name} ({job.Theme.ItemId}): {e.Message}";
                lock (_gate)
                {
                    _status.Failed++;
                    _status.AddIssue(new ScanIssue(job.Theme.Name, code, true, DateTimeOffset.UtcNow, diagnostic, job.Theme.ItemId));
                    _status.CompleteItem(false);
                    Update(id, "Failed", false, code);
                }
                logger.LogWarning("{Diagnostic}", diagnostic);
            }
            finally
            {
                lock (_gate)
                {
                    _status.ActiveItems = [];
                    _status.CurrentItem = null;
                    FinishIfIdle();
                }
            }
        }
    }
}

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
    public string Kind { get; init; } = "scan";
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
    public bool Prepared { get; set; }
    public bool Cancelling { get; set; }
    public bool Cancelled { get; set; }
    public string? StoppedReason { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? CurrentItem { get; set; }
    public ScanActiveItem[] ActiveItems { get; set; } = [];
    public int Processed { get; set; }
    public int Total { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int AlreadyThemed { get; set; }
    public int Excluded { get; set; }
    public int NoMatch { get; set; }
    public ScanIssue[] Issues { get; set; } = [];
    public int Unsupported { get; set; }
    public int Failed { get; set; }

    internal ScanStatus Snapshot() => (ScanStatus)MemberwiseClone();

    internal void AddIssue(ScanIssue issue) =>
        Issues = [.. Issues.TakeLast(JellyScoreConstants.ScanRecentIssues - 1), issue];

    internal void RecordResult(string name, ThemeResult result, Guid? itemId = null)
    {
        if (result.Result == "Added") Added++;
        else if (result.Result == "Replaced") Updated++;
        else if (result.Result == "Already themed") AlreadyThemed++;
        else if (result.Result == "Previously used recording excluded") Excluded++;
        else NoMatch++;
        if (result.ReasonCode is { } code) AddIssue(new ScanIssue(name, code, false, DateTimeOffset.UtcNow, null, itemId));
    }

    internal void CompleteItem(bool known)
    {
        var completedAt = DateTimeOffset.UtcNow;
        if (known) { KnownProcessed++; KnownSeconds += (completedAt - LastCompletedAt).TotalSeconds; }
        else OtherSeconds += (completedAt - LastCompletedAt).TotalSeconds;
        Processed++;
        LastCompletedAt = completedAt;
    }

    internal void Finish()
    {
        Running = false;
        Cancelling = false;
        FinishedAt = DateTimeOffset.UtcNow;
        CurrentItem = null;
        ActiveItems = [];
    }
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
public sealed record ScanIssue(string Name, string Code, bool Failed, DateTimeOffset At, string? Diagnostic, Guid? ItemId = null);
public sealed record ScanActiveItem(string Name, string Stage);
// ReSharper restore NotAccessedPositionalProperty.Global

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ThemeScan(ILibraryManager library, ThemeService themes, Store store, ILogger<ThemeScan> logger, ThemeProcessingWorker processing) : IScheduledTask
{
    private static ScanStatus _status = new();
    private static ScanStatus _lastStatus = new();
    public string Name => "Scan with JellyScore";
    public string Key => JellyScoreConstants.ScanTaskKey;
    public string Description => "Find themes in selected movie and TV libraries.";
    public string Category => "Library";
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];
    public ScanStatus Status => _status;
    public ScanStatus LastStatus => _lastStatus;

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (processing.IsBusy) throw new InvalidOperationException("Theme processing is queued or running; retry the scan after it finishes.");
        var (knownIds, knownPrior, otherPrior) = store.Read(s => (s.Themes.Keys.ToHashSet(), s.ScanKnownSecondsPerItem, s.ScanOtherSecondsPerItem));
        _status = new ScanStatus { RunId = Guid.NewGuid(), StartedAt = DateTimeOffset.UtcNow, Running = true,
            PriorKnownSecondsPerItem = knownPrior, PriorOtherSecondsPerItem = otherPrior };
        var status = _status;
        try
        {
            themes.ResetSuppression();
            var selected = Plugin.Instance.Configuration.SelectedLibraries(library);
            if (selected.Length == 0) { status.Prepared = true; progress.Report(JellyScoreConstants.ProgressComplete); return; }
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
            status.Prepared = true;
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
                        status.RecordResult(item.Name, result, item.Id);
                        if (result.ReasonCode is not null)
                        {
                            logger.LogDebug("Skipped {ItemId}: {Reason}", item.Id, themes.Outcome(item.Id));
                        }
                    }
                }
                catch (InvalidOperationException)
                {
                    lock (status)
                    {
                        status.Unsupported++;
                        AddIssue(status, new ScanIssue(item.Name, "scanUnsupported", false, DateTimeOffset.UtcNow, null, item.Id));
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (RateLimitFailure e)
                {
                    var diagnostic = $"Theme scan paused for {item.Name} ({item.Id}): {e.Message}";
                    lock (status)
                    {
                        status.StoppedReason = "scanRateLimited";
                        AddIssue(status, new ScanIssue(item.Name, "scanRateLimited", true, DateTimeOffset.UtcNow, diagnostic, item.Id));
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
                        }, true, DateTimeOffset.UtcNow, diagnostic, item.Id));
                    }
                    logger.LogWarning("Theme scan failed for {Name} ({ItemId}): {Message}", item.Name, item.Id, e.Message);
                }
                finally { lock (status) { status.ActiveItems = []; status.CurrentItem = null; } }
                lock (status)
                {
                    status.CompleteItem(item.Known);
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
        finally { lock (status) { status.Finish(); _lastStatus = status.Snapshot(); } }
    }

    private static void AddIssue(ScanStatus status, ScanIssue issue) =>
        status.AddIssue(issue);
}
