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
        var all = managed.Select(r => (Theme: r, Job: jobs.FirstOrDefault(j => j.Theme.ItemId == r.ItemId)))
            .Concat(jobs.Where(j => !j.Replacement && !managed.Any(r => r.ItemId == j.Theme.ItemId)).Select(j => (Theme: j.Theme, Job: (ThemeProcessingJob?)j)))
            .OrderByDescending(row => row.Theme.AddedAt ?? row.Theme.Date).ToArray();
        var rows = all.Where(row => string.IsNullOrEmpty(search) || row.Theme.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
            row.Theme.Library.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new { Total = rows.Length, AllTotal = all.Length, ManagedTotal = managed.Count,
            Processing = jobs.Any(j => j.Processing), Items = rows.Skip((Math.Max(1, page) - 1) * JellyScoreConstants.AdminPageSize)
            .Take(JellyScoreConstants.AdminPageSize).Select(row => { var r = row.Theme; return new {
            r.ItemId, r.Name, r.Kind, r.Year, r.Library, r.Path, r.VideoTitle, r.Score, r.Evidence, r.Date,
            Source = r.SourceUrl ?? (string.IsNullOrEmpty(r.VideoId) ? null : "https://www.youtube.com/watch?v=" + r.VideoId),
            YouTubeUrl = !r.VideoId.StartsWith("tvdb:", StringComparison.Ordinal) && YouTube.VideoId(r.SourceUrl ?? "https://www.youtube.com/watch?v=" + r.VideoId) is { } videoId
                ? "https://www.youtube.com/watch?v=" + videoId : null,
            Pending = row.Job is { Replacement: false }, Processing = row.Job?.Processing ?? false,
            Stage = row.Job is { Processing: true, Stage: not "queued" } ? YouTube.ToolSetupStage ?? row.Job.Stage : row.Job?.Stage,
            Code = row.Job?.Code, Result = row.Job?.Result,
            Status = row.Job is { Replacement: false } ? row.Job.Stage : ThemeService.Status(r) }; }) };
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

    [HttpPost("scan")]
    public IActionResult StartScan()
    {
        tasks.QueueIfNotRunning<ThemeScan>();
        return Accepted();
    }

    [HttpPost("scan/cancel")]
    public IActionResult CancelScan() { tasks.CancelIfRunning<ThemeScan>(); return Accepted(); }

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
            youtubeUrl = "https://www.youtube.com/watch?v=" + videoId;
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
        return Enqueue(id, "https://www.youtube.com/watch?v=" + videoId, replacement: true);
    }

    private IActionResult Enqueue(Guid id, string? youtubeUrl, bool replacement)
    {
        if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
        try { processing.Enqueue(id, youtubeUrl, replacement); return Accepted(); }
        catch (InvalidOperationException e) { return Conflict(new { Error = e.Message, Code = ErrorCode(e, "requestFailed") }); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return UnprocessableEntity(new { Code = "themeLocationUnavailable" }); }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        if (scan.Status.Running) return Conflict(new { Code = "availableAfterScan" });
        try { await themes.Delete(id, ct); return NoContent(); }
        catch (InvalidOperationException e) { return Conflict(new { Error = e.Message, Code = ErrorCode(e, "deleteFailed") }); }
        catch (IOException e) { return UnprocessableEntity(new { Error = e.Message, Code = ErrorCode(e, "deleteFailed") }); }
    }

    internal static string ErrorCode(Exception e, string fallback) => e.Message switch
    {
        "Theme changed elsewhere. The file was left untouched." or "Theme changed elsewhere. It was not deleted." or
            "Theme changed during download. The file was left untouched." => "themeChanged",
        "Another theme appeared. The file was left untouched." => "anotherTheme",
        "No managed theme to refresh." or "No managed theme." or "Item no longer exists." => "themeUnavailable",
        "Item is not in a selected library." or "Unsupported item." or "Movie needs a dedicated physical folder." or
            "Item needs a physical folder inside its library." or "Item folder is missing or unwritable." or
            "Collection has no matching movie or physical folder." => "themeLocationUnavailable",
        "Item title is not ready; retry after metadata refresh." => "itemNotReady",
        _ when e is SearchFailure => "searchFailed",
        _ when e is DownloadFailure => "downloadFailed",
        _ => fallback
    };
}
