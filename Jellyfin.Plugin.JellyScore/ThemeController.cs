using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyScore;

[ApiController]
[Route("ThemeSongs")]
[Authorize(Policy = "RequiresElevation")]
public sealed class ThemeController(ThemeService themes, ThemeScan scan, ITaskManager tasks, ILibraryManager library, ThemeProcessingWorker processing) : ControllerBase
{
    [HttpGet("settings")]
    public object Settings() => new { Plugin.Instance.Configuration.Enabled, Plugin.Instance.Configuration.ScanOnLibraryRefresh,
        Plugin.Instance.Configuration.PreferFranchiseThemes,
        Libraries = Plugin.Instance.Configuration.SelectedLibraries(library),
        MinimumMatchStrength = Plugin.Instance.Configuration.EffectiveMinimumMatchStrength,
        TargetLufs = Plugin.Instance.Configuration.EffectiveTargetLufs,
        Plugin.Instance.Configuration.YouTubeCookies,
        Plugin.Instance.Configuration.TvThemeUrlTemplate,
        YouTube.DownloaderAvailable, YouTube.DownloaderError, YouTube.RuntimeError,
        LibrariesAvailable = library.GetVirtualFolders().Where(f => f.CollectionType != CollectionTypeOptions.boxsets ||
            library.GetCount(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.BoxSet] }) > 0)
            .Select(f => new { f.Name, f.ItemId }) };

    [HttpPost("downloader/retry")]
    public async Task<IActionResult> RetryDownloader(CancellationToken ct)
    {
        try { await YouTube.RetryDownloader(ct); return NoContent(); }
        catch (SearchFailure e) { return UnprocessableEntity(new { Error = e.Message, Code = "downloaderFailed" }); }
    }

    [HttpGet("strings/{locale}")]
    public IActionResult Strings(string locale)
    {
        var stream = typeof(ThemeController).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.JellyScore.Strings.{locale}.json");
        return stream is null ? NotFound() : File(stream, "application/json");
    }

    public sealed record SettingsRequest(bool Enabled, Guid[]? Libraries, int? MinimumMatchStrength, int? TargetLufs, bool? ScanOnLibraryRefresh, string? YouTubeCookies, string? TvThemeUrlTemplate, bool? PreferFranchiseThemes);

    [HttpPost("settings")]
    public IActionResult Save([FromBody] SettingsRequest request)
    {
        if (request.MinimumMatchStrength is < JellyScoreConstants.MinimumMatchStrength or > JellyScoreConstants.MaximumMatchStrength) return BadRequest();
        if (request.TargetLufs is < JellyScoreConstants.MinimumTargetLufs or > JellyScoreConstants.MaximumTargetLufs) return BadRequest();
        if (request.YouTubeCookies is { Length: > 0 } cookies && !YouTube.ValidCookies(cookies))
            return BadRequest(new { Code = "invalidCookies" });
        var tvThemeUrlTemplate = request.TvThemeUrlTemplate?.Trim();
        if (!string.IsNullOrEmpty(tvThemeUrlTemplate) && !TvThemeSource.ValidTemplate(tvThemeUrlTemplate))
            return BadRequest(new { Code = "invalidTvThemeUrl" });
        var config = Plugin.Instance.Configuration;
        config.Enabled = request.Enabled;
        if (request.PreferFranchiseThemes is { } preferFranchiseThemes) config.PreferFranchiseThemes = preferFranchiseThemes;
        if (request.ScanOnLibraryRefresh is { } scanOnLibraryRefresh) config.ScanOnLibraryRefresh = scanOnLibraryRefresh;
        config.Libraries = request.Libraries ?? [];
        if (request.MinimumMatchStrength is { } minimumMatchStrength) config.MinimumMatchStrength = minimumMatchStrength;
        if (request.TargetLufs is { } targetLufs) config.TargetLufs = targetLufs;
        if (request.YouTubeCookies is not null) config.YouTubeCookies = request.YouTubeCookies.Length == 0 ? null : request.YouTubeCookies;
        if (tvThemeUrlTemplate is not null) config.TvThemeUrlTemplate = tvThemeUrlTemplate.Length == 0 ? null : tvThemeUrlTemplate;
        Plugin.Instance.UpdateConfiguration(config);
        return NoContent();
    }

    [HttpGet("downloads")]
    public object Downloads([FromQuery] string? search = null, [FromQuery] int page = 1)
    {
        var managed = themes.List();
        var jobs = processing.List();
        var managedIds = managed.Select(theme => theme.ItemId).ToHashSet();
        var jobsByItem = jobs.ToDictionary(job => job.Theme.ItemId);
        var all = managed.Select(theme => (Theme: theme, Job: jobsByItem.GetValueOrDefault(theme.ItemId)))
            .Concat(jobs.Where(job => !job.Replacement && !managedIds.Contains(job.Theme.ItemId)).Select(job => (Theme: job.Theme, Job: (ThemeProcessingJob?)job)))
            .OrderByDescending(row => row.Theme.AddedAt ?? row.Theme.Date).ToArray();
        var rows = all.Where(row => string.IsNullOrEmpty(search) || row.Theme.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.Theme.Library.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { Total = rows.Length, AllTotal = all.Length, ManagedTotal = managed.Count,
            Processing = jobs.Any(j => j.Processing), Items = rows.Skip((Math.Max(1, page) - 1) * JellyScoreConstants.AdminPageSize)
            .Take(JellyScoreConstants.AdminPageSize).Select(row => DownloadRow(row.Theme, row.Job)) };
    }

    private static object DownloadRow(ManagedTheme theme, ThemeProcessingJob? job)
    {
        var source = theme.SourceUrl;
        if (source is null && !string.IsNullOrEmpty(theme.VideoId)) source = YouTube.VideoUrl(theme.VideoId);
        var videoId = theme.VideoId.StartsWith("tvdb:", StringComparison.Ordinal) ? null : YouTube.VideoId(source);
        var stage = job?.Stage;
        if (job is { Processing: true, Stage: not "queued" }) stage = YouTube.ToolSetupStage ?? job.Stage;
        return new
        {
            theme.ItemId, theme.Name, theme.Kind, theme.Year, theme.Library, theme.Path, theme.VideoTitle, theme.Score, theme.Evidence, theme.Date,
            Source = source, YouTubeUrl = videoId is null ? null : YouTube.VideoUrl(videoId),
            Pending = job is { Replacement: false }, Processing = job?.Processing ?? false,
            Stage = stage, Code = job?.Code, Result = job?.Result,
            Status = job is { Replacement: false } ? job.Stage : ThemeService.Status(theme)
        };
    }

    [HttpDelete("downloads")]
    public async Task<IActionResult> DeleteAll(CancellationToken ct)
    {
        if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
        var result = await themes.DeleteAll(ct);
        return Ok(new { result.Deleted, result.Skipped });
    }

    [HttpGet("scan")]
    public object Progress() => scan.Status;

    [HttpPost("downloads/redownload")]
    public IActionResult RedownloadAll()
    {
        lock (processing.Gate)
        {
            if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
            try { return Accepted(new { Queued = processing.RedownloadAll() }); }
            catch (InvalidOperationException) { return Conflict(new { Code = "queueBusy" }); }
        }
    }

    [HttpGet("activity")]
    public object Activity()
    {
        var status = scan.Status;
        lock (status)
        {
            var queue = processing.Status;
            var lastScan = scan.LastStatus;
            var lastQueue = processing.LastStatus;
            var last = (lastScan.FinishedAt ?? DateTimeOffset.MinValue) >= (lastQueue.FinishedAt ?? DateTimeOffset.MinValue) ? lastScan : lastQueue;
            ScanStatus? current = null;
            if (status.Running) current = status.Snapshot();
            else if (queue.Running) current = queue;
            return new { Current = current, Last = last.Snapshot() };
        }
    }

    [HttpPost("queue/cancel")]
    public IActionResult CancelQueue() { processing.Cancel(); return Accepted(); }

    [HttpPost("scan")]
    public IActionResult StartScan()
    {
        if (processing.IsBusy) return Conflict(new { Code = "queueBusy" });
        tasks.QueueIfNotRunning<ThemeScan>();
        return Accepted();
    }

    [HttpPost("scan/cancel")]
    public IActionResult CancelScan()
    {
        if (scan.Status.Running) scan.Status.Cancelling = true;
        tasks.CancelIfRunning<ThemeScan>();
        return Accepted();
    }

    [HttpPost("{id:guid}/refresh")]
    public IActionResult Refresh(Guid id) => Enqueue(id, null, replacement: true);

    public sealed record EditRequest(string? YouTubeUrl);

    [HttpGet("items")]
    public object AddItems([FromQuery] string? search = null) => themes.AddItems(search, processing.List().Where(j => j.Processing).Select(j => j.Theme.ItemId).ToHashSet());

    [HttpPost("{id:guid}/add")]
    public IActionResult Add(Guid id, [FromBody] EditRequest request)
    {
        string? youtubeUrl = null;
        if (request.YouTubeUrl is not null)
        {
            var videoId = YouTube.VideoId(request.YouTubeUrl);
            if (videoId is null) return BadRequest(new { Code = "invalidYouTubeUrl" });
            youtubeUrl = YouTube.VideoUrl(videoId);
        }
        return Enqueue(id, youtubeUrl, replacement: false);
    }

    [HttpDelete("{id:guid}/pending")]
    public IActionResult Dismiss(Guid id)
    {
        if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
        return processing.Dismiss(id) ? NoContent() : Conflict(new { Code = "requestFailed" });
    }

    [HttpPost("{id:guid}/edit")]
    public IActionResult Edit(Guid id, [FromBody] EditRequest request)
    {
        var videoId = YouTube.VideoId(request.YouTubeUrl);
        if (videoId is null) return BadRequest(new { Code = "invalidYouTubeUrl" });
        return Enqueue(id, YouTube.VideoUrl(videoId), replacement: true);
    }

    private IActionResult Enqueue(Guid id, string? youtubeUrl, bool replacement)
    {
        lock (processing.Gate)
        {
            if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
            try { processing.Enqueue(id, youtubeUrl, replacement); return Accepted(); }
            catch (InvalidOperationException e) { return Conflict(new { Error = e.Message, Code = ThemeService.ErrorCode(e, "requestFailed") }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return UnprocessableEntity(new { Code = "themeLocationUnavailable" }); }
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
        try { await themes.Delete(id, ct); return NoContent(); }
        catch (InvalidOperationException e) { return Conflict(new { Error = e.Message, Code = ThemeService.ErrorCode(e, "deleteFailed") }); }
        catch (IOException e) { return UnprocessableEntity(new { Error = e.Message, Code = ThemeService.ErrorCode(e, "deleteFailed") }); }
    }

}
