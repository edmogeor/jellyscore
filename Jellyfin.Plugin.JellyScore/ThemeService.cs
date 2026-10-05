using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text.Json;
using MediaBrowser.Controller.BaseItemManager;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.JellyScore;

public sealed record ThemeResult(string Result, string? ReasonCode = null);

// ReSharper disable once ClassNeverInstantiated.Global
public sealed class ThemeService(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem, IMediaEncoder encoder, Store store, YouTube youtube,
    IBaseItemManager baseItemManager, ILogger<ThemeService> logger)
{
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".flac", ".ogg", ".opus", ".wav", ".wma", ".aac" };
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".m4v", ".ts", ".webm" };

    public IReadOnlyList<ManagedTheme> List()
    {
        var rows = store.Read(s => s.Themes.Values.OrderByDescending(t => t.AddedAt ?? t.Date).ToArray());
        var stale = new List<(ManagedTheme Theme, bool MissingItem)>();
        foreach (var record in rows)
        {
            var gate = _locks.GetOrAdd(record.ItemId, _ => new SemaphoreSlim(1));
            if (!gate.Wait(0)) continue;
            try
            {
                var item = library.GetItemById(record.ItemId);
                if (item is null) { stale.Add((record, true)); continue; }
                var currentFolder = item is Series or BoxSet ? item.Path : Path.GetDirectoryName(item.Path);
                if (!Directory.Exists(record.Folder) && !Directory.Exists(currentFolder)) continue;
                try
                {
                    var (folder, _, libraryId) = Location(item);
                    if (libraryId != record.LibraryId || !Owned(record, item, folder)) stale.Add((record, false));
                }
                catch (InvalidOperationException) { stale.Add((record, false)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            finally { gate.Release(); }
        }
        if (stale.Count == 0) return rows;
        store.Change(s =>
        {
            foreach (var (record, missingItem) in stale)
            {
                if (!s.Themes.TryGetValue(record.ItemId, out var current) || !ReferenceEquals(current, record)) continue;
                s.Themes.Remove(record.ItemId);
                if (missingItem)
                {
                    s.ExcludedVideos.Remove(record.ItemId);
                    s.ExcludedRecordings.Remove(record.ItemId);
                    s.Suppressed.Remove(record.ItemId);
                    s.Outcomes.Remove(record.ItemId);
                }
                else s.Outcomes[record.ItemId] = "No longer managed; theme or library item changed outside plugin";
            }
        });
        var removed = stale.Select(s => s.Theme.ItemId).ToHashSet();
        return rows.Where(r => !removed.Contains(r.ItemId)).ToArray();
    }
    public string? Outcome(Guid id) => store.Read(s => s.Outcomes.GetValueOrDefault(id));
    public ManagedTheme PrepareRequest(Guid id, bool replacement, string? youtubeUrl)
    {
        var item = library.GetItemById(id) ?? throw new InvalidOperationException("Item no longer exists.");
        var (folder, libraryName, libraryId) = Location(item);
        if (replacement)
        {
            var managed = store.Read(s => s.Themes.GetValueOrDefault(id)) ?? throw new InvalidOperationException("No managed theme to refresh.");
            if (!Owned(managed, item, folder)) throw new InvalidOperationException("Theme changed elsewhere. The file was left untouched.");
            return managed;
        }
        if (store.Read(s => s.Themes.ContainsKey(id)) || OtherTheme(item, folder, null))
            throw new InvalidOperationException("Another theme appeared. The file was left untouched.");
        return new ManagedTheme { ItemId = id, Name = item.Name, Year = item.ProductionYear,
            Kind = item switch { Movie => "Movie", BoxSet => "Collection", _ => "Series" },
            Library = libraryName, LibraryId = libraryId, Folder = folder,
            Path = Path.Combine(folder, JellyScoreConstants.ThemeFile), SourceUrl = youtubeUrl, Date = DateTimeOffset.UtcNow };
    }
    public object[] AddItems(string? search, IReadOnlySet<Guid>? queued = null)
    {
        if (string.IsNullOrWhiteSpace(search) || search.Trim().Length < 2) return [];
        return library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series, Jellyfin.Data.Enums.BaseItemKind.BoxSet],
            Recursive = true, SearchTerm = search.Trim(), Limit = 100
        }).Select(item =>
        {
            try
            {
                var (folder, libraryName, _) = Location(item);
                if (queued?.Contains(item.Id) != true && !store.Read(s => s.Themes.ContainsKey(item.Id)) && !OtherTheme(item, folder, null))
                    return (object)new
                    {
                        ItemId = item.Id, item.Name, Year = item.ProductionYear, Library = libraryName,
                        Kind = item switch { Movie => "Movie", BoxSet => "Collection", _ => "Series" }
                    };
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { }
            return null;
        }).OfType<object>().Take(20).ToArray();
    }
    public static string Status(ManagedTheme record)
    {
        try
        {
            if (!File.Exists(record.Path) || File.GetAttributes(record.Path).HasFlag(FileAttributes.ReparsePoint) ||
                !string.Equals(Canonical(record.Path), Path.Combine(Canonical(record.Folder), JellyScoreConstants.ThemeFile), StringComparison.Ordinal))
                return "Missing or externally modified";
            using var file = File.OpenRead(record.Path);
            return Convert.ToHexString(SHA256.HashData(file)) == record.Hash ? "Active" : "Missing or externally modified";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "Missing or externally modified"; }
    }

    private static string Canonical(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path)) return (new DirectoryInfo(path).ResolveLinkTarget(true)?.FullName ?? path);
        if (File.Exists(path)) return (new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path);
        return path;
    }

    private (string Folder, string Library, Guid LibraryId) Location(BaseItem item, bool requireSelected = false)
    {
        if (item is not (Movie or Series or BoxSet) || item.IsVirtualItem || item.ExtraType is not null || item.SourceType != SourceType.Library)
            throw new InvalidOperationException("Unsupported item.");
        if (item is BoxSet boxSet)
        {
            var member = boxSet.GetLinkedChildren().OfType<Movie>()
                .FirstOrDefault(movie => !string.IsNullOrWhiteSpace(movie.TmdbCollectionName) &&
                    string.Equals(FranchiseTitle(movie.TmdbCollectionName), FranchiseTitle(boxSet.Name), StringComparison.OrdinalIgnoreCase));
            if (member is null || string.IsNullOrWhiteSpace(boxSet.Path) || !Directory.Exists(boxSet.Path))
                throw new InvalidOperationException("Collection has no matching movie or physical folder.");
            var selectedLibrary = library.GetCollectionFolders(boxSet).FirstOrDefault(f => !requireSelected || Plugin.Instance.Configuration.Libraries is null ||
                Plugin.Instance.Configuration.Libraries.Contains(f.Id)) ?? throw new InvalidOperationException("Item is not in a selected library.");
            var collectionFolder = Canonical(boxSet.Path);
            if (!selectedLibrary.PhysicalLocations.Append(selectedLibrary.Path).Where(Directory.Exists).Select(Canonical)
                .Any(root => collectionFolder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new InvalidOperationException("Item needs a physical folder inside its library.");
            return (collectionFolder, selectedLibrary.Name, selectedLibrary.Id);
        }
        var folders = library.GetCollectionFolders(item);
        var selectedLibraries = Plugin.Instance.Configuration.Libraries;
        var selected = folders.FirstOrDefault(f => !requireSelected || selectedLibraries is null || selectedLibraries.Contains(f.Id));
        if (selected is null) throw new InvalidOperationException("Item is not in a selected library.");
        var folder = Canonical(item is Series ? item.Path : Path.GetDirectoryName(item.Path)!);
        var roots = selected.PhysicalLocations.Append(selected.Path).Where(Directory.Exists).Select(Canonical).ToArray();
        if (!Directory.Exists(folder)) throw new InvalidOperationException("Item folder is missing or unwritable.");
        if (item is Movie && (!File.Exists(item.Path) || item.IsInMixedFolder || roots.Contains(folder, StringComparer.Ordinal) ||
            Directory.EnumerateFiles(folder).Count(p => VideoExtensions.Contains(Path.GetExtension(p))) > 1))
            throw new InvalidOperationException("Movie needs a dedicated physical folder.");
        if (!roots.Any(root => folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) ||
            item is Series && !string.Equals(Canonical(item.Path), folder, StringComparison.Ordinal))
            throw new InvalidOperationException("Item needs a physical folder inside its library.");
        return (folder, selected.Name, selected.Id);
    }

    private static bool Owned(ManagedTheme entry, BaseItem item, string folder)
    {
        var path = Path.Combine(folder, JellyScoreConstants.ThemeFile);
        if (entry.ItemId != item.Id || !string.Equals(entry.Folder, folder, StringComparison.Ordinal) ||
            !string.Equals(entry.Path, path, StringComparison.Ordinal) || !File.Exists(path) ||
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return false;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)) == entry.Hash;
    }

    private static bool OtherTheme(BaseItem item, string folder, string? owned)
    {
        if (item.GetThemeSongs().Any(song => song.Path is not null &&
            string.Equals(Canonical(Path.GetDirectoryName(song.Path)!), folder, StringComparison.Ordinal) &&
            !string.Equals(Canonical(song.Path), owned, StringComparison.Ordinal))) return true;
        if (Directory.EnumerateFiles(folder).Any(p => Path.GetFileNameWithoutExtension(p).Equals(JellyScoreConstants.ThemeBaseName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(p, owned, StringComparison.Ordinal))) return true;
        var subfolder = Path.Combine(folder, JellyScoreConstants.ThemeMusicFolder);
        return Directory.Exists(subfolder) && Directory.EnumerateFiles(subfolder, "*", SearchOption.AllDirectories).Any(p => AudioExtensions.Contains(Path.GetExtension(p)));
    }

    private void Refresh(BaseItem item) => providers.QueueRefresh(item.Id, new MetadataRefreshOptions(new DirectoryService(fileSystem)), RefreshPriority.High);

    private static string FranchiseTitle(string name) => name.EndsWith(" Collection", StringComparison.OrdinalIgnoreCase)
        ? name[..^" Collection".Length].Trim() : name.Trim();

    private async Task<bool> NoCompetingEdition(BaseItem item, Work work, CancellationToken ct)
    {
        if (item is not (Movie or Series) || work.Year is null || !item.TryGetProviderId(MetadataProvider.Tmdb, out var tmdbId)) return false;
        var options = library.GetLibraryOptions(item);
        if (!baseItemManager.IsMetadataFetcherEnabled(item is Movie ? item : new Movie(), options.GetTypeOptions(nameof(Movie)), "TheMovieDb") ||
            !baseItemManager.IsMetadataFetcherEnabled(new Series(), options.GetTypeOptions(nameof(Series)), "TheMovieDb")) return false;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var films = (await providers.GetRemoteSearchResults<Movie, MovieInfo>(new RemoteSearchQuery<MovieInfo>
            {
                SearchProviderName = "TheMovieDb",
                SearchInfo = new MovieInfo { Name = work.Title }
            }, timeout.Token)).Select(r => (r.GetProviderId(MetadataProvider.Tmdb), r.Name, r.ProductionYear)).ToArray();
            var shows = (await providers.GetRemoteSearchResults<Series, SeriesInfo>(new RemoteSearchQuery<SeriesInfo>
            {
                SearchProviderName = "TheMovieDb",
                SearchInfo = new SeriesInfo { Name = work.Title }
            }, timeout.Token)).Select(r => (r.GetProviderId(MetadataProvider.Tmdb), r.Name, r.ProductionYear)).ToArray();
            // ponytail: Jellyfin exposes only the first page and hides provider errors; a full page or missing target cannot establish uniqueness.
            return Matcher.NoCompetingEdition(work, tmdbId, films, shows);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Edition lookup failed for {ItemId}; retaining release-year requirement", item.Id);
            return false;
        }
    }

    public async Task<ThemeResult> Process(Guid id, bool replacement, CancellationToken ct, Action<string>? reportStage = null, string? youtubeUrl = null, bool administratorAdd = false)
    {
        var manualVideoId = youtubeUrl is null ? null : YouTube.VideoId(youtubeUrl) ?? throw new InvalidOperationException("Invalid YouTube URL.");
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            var item = library.GetItemById(id) ?? throw new InvalidOperationException("Item no longer exists.");
            var (folder, libraryName, libraryId) = Location(item, requireSelected: !replacement && !administratorAdd);
            if (!replacement && !administratorAdd && item is Movie movie && !string.IsNullOrWhiteSpace(movie.TmdbCollectionName))
            {
                var collection = library.GetItemList(new InternalItemsQuery { IncludeItemTypes = [Jellyfin.Data.Enums.BaseItemKind.BoxSet] })
                    .OfType<BoxSet>().FirstOrDefault(boxSet =>
                        string.Equals(FranchiseTitle(boxSet.Name), FranchiseTitle(movie.TmdbCollectionName), StringComparison.OrdinalIgnoreCase) &&
                        boxSet.GetLinkedChildren().Any(child => child.Id == movie.Id));
                if (collection is not null && library.GetCollectionFolders(collection).Any(f =>
                    Plugin.Instance.Configuration.Libraries is null || Plugin.Instance.Configuration.Libraries.Contains(f.Id)))
                {
                    try { await Process(collection.Id, false, ct, reportStage); }
                    catch (Exception e) when (e is not OperationCanceledException and not RateLimitFailure)
                    { logger.LogWarning(e, "Collection theme processing failed for {ItemId}", collection.Id); }
                }
            }
            var existing = store.Read(s => s.Themes.GetValueOrDefault(id));
            if (replacement && existing is null) throw new InvalidOperationException("No managed theme to refresh.");
            if (existing is not null && !Owned(existing, item, folder)) throw new InvalidOperationException("Theme changed elsewhere. The file was left untouched.");
            if (!replacement && existing is not null) return new("Already themed");
            if (!replacement && !administratorAdd && store.Read(s => s.Suppressed.Contains(id))) return new("Suppressed until rescan");
            if (OtherTheme(item, folder, existing?.Path)) return new("Already themed");
            if (string.IsNullOrWhiteSpace(item.Name)) throw new InvalidOperationException("Item title is not ready; retry after metadata refresh.");
            var work = new Work(item.Name, item.OriginalTitle, item.ProductionYear, item is Series);
            Work? franchise = item switch
            {
                Movie { TmdbCollectionName: { } name } movieItem when !string.IsNullOrWhiteSpace(name) =>
                    new Work(FranchiseTitle(name), null, null, false, Franchise: true, Installments: [movieItem.Name]),
                BoxSet collectionItem => new Work(FranchiseTitle(collectionItem.Name), null, null, false, Franchise: true,
                    Installments: collectionItem.GetLinkedChildren().OfType<Movie>().Select(m => m.Name).ToArray()),
                _ => null
            };
            var minimumMatchStrength = Plugin.Instance.Configuration.EffectiveMinimumMatchStrength;
            var excludedIds = store.Read(s => new HashSet<string>(s.ExcludedVideos.GetValueOrDefault(id) ?? []));
            var excludedRecordings = store.Read(s => new HashSet<string>(s.ExcludedRecordings.GetValueOrDefault(id) ?? []));
            if (replacement && manualVideoId is null)
            {
                excludedIds.Add(existing!.VideoId);
                excludedRecordings.Add(existing.Recording);
                store.Change(s =>
                {
                    if (!s.ExcludedVideos.TryGetValue(id, out var ids)) s.ExcludedVideos[id] = ids = [];
                    ids.Add(existing.VideoId);
                    if (!s.ExcludedRecordings.TryGetValue(id, out var recordings)) s.ExcludedRecordings[id] = recordings = [];
                    recordings.Add(existing.Recording);
                });
            }
            var path = Path.Combine(folder, JellyScoreConstants.ThemeFile);
            async Task<ThemeResult> Install(string temporary, Choice source, string? sourceUrl = null)
            {
                ct.ThrowIfCancellationRequested();
                if (OtherTheme(item, folder, existing?.Path)) throw new IOException("Another theme appeared. The file was left untouched.");
                if (existing is not null)
                {
                    if (!Owned(existing, item, folder)) throw new IOException("Theme changed during download. The file was left untouched.");
                    File.Move(temporary, path, true);
                }
                else File.Move(temporary, path);
                await using var stream = File.OpenRead(path);
                // Complete ownership recording after the file is in place, even if cancellation arrives.
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, CancellationToken.None));
                var result = replacement ? "Replaced" : "Added";
                var date = DateTimeOffset.UtcNow;
                store.Change(s =>
                {
                    s.Themes[id] = new ManagedTheme { ItemId = id, Folder = folder, Path = path, LibraryId = libraryId, Library = libraryName,
                        Kind = item switch { Movie => "Movie", BoxSet => "Collection", _ => "Series" }, Name = item.Name, Year = item.ProductionYear,
                        VideoId = source.Video.Id, VideoTitle = source.Video.Title, Recording = source.Recording, SourceUrl = sourceUrl,
                        Hash = hash, Score = source.Score, Evidence = source.Evidence, Date = date,
                        AddedAt = existing?.AddedAt ?? existing?.Date ?? date };
                    s.Outcomes[id] = result;
                    s.Suppressed.Remove(id);
                });
                Refresh(item);
                return new(result);
            }
            if (manualVideoId is not null)
            {
                reportStage?.Invoke("stageSearching");
                var manual = await YouTube.ManualChoice(manualVideoId, item is BoxSet ? franchise! : work, ct);
                var temporary = Path.Combine(folder, ".theme-" + Guid.NewGuid().ToString("N") + ".mp3");
                try
                {
                    reportStage?.Invoke("stageDownloading");
                    await Audio.Convert(manual, temporary, encoder, Plugin.Instance.Configuration.EffectiveTargetLufs, ct,
                        reportStage is null ? null : () => reportStage("stageProcessing"));
                    return await Install(temporary, manual);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            if (!replacement && item is Series && Plugin.Instance.Configuration.TvThemeUrlTemplate is { } template &&
                item.TryGetProviderId(MetadataProvider.Tvdb, out var tvdbId) && TvThemeSource.Url(template, tvdbId) is { } url)
            {
                var temporary = Path.Combine(folder, ".theme-" + Guid.NewGuid().ToString("N") + ".mp3");
                try
                {
                    var converted = false;
                    reportStage?.Invoke("stageDownloading");
                    try
                    {
                        await Audio.ConvertUrl(url, temporary, encoder, Plugin.Instance.Configuration.EffectiveTargetLufs, ct,
                            reportStage is null ? null : () => reportStage("stageProcessing"));
                        converted = true;
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested && e is IOException or HttpRequestException or SocketException or JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
                    { logger.LogWarning(e, "Configured TV theme source failed for {ItemId}; trying YouTube", id); }
                    if (converted)
                    {
                        var sourceId = "tvdb:" + tvdbId;
                        var direct = new Choice(new Video(sourceId, item.Name + " (TV theme)", "", "", null), sourceId, 100,
                            "Configured TV theme source for TVDB ID " + tvdbId);
                        return await Install(temporary, direct, url.AbsoluteUri);
                    }
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            async Task<(Work Work, Choice? Choice, bool Checked)> Select(Work currentWork, IReadOnlyList<Video> candidates, bool checkedEdition)
            {
                var selected = Matcher.Select(currentWork, candidates, excludedIds, excludedRecordings, minimumMatchStrength);
                if (item is not (Movie or Series) || checkedEdition || currentWork.Year is null) return (currentWork, selected, checkedEdition);
                var withoutYear = currentWork with { NoCompetingEdition = true };
                var alternative = Matcher.Select(withoutYear, candidates, excludedIds, excludedRecordings, minimumMatchStrength);
                if (alternative is null || alternative.Video.Id == selected?.Video.Id) return (currentWork, selected, false);
                if (!await NoCompetingEdition(item, currentWork, ct)) return (currentWork, selected, true);
                return (withoutYear, alternative, true);
            }
            reportStage?.Invoke("stageSearching");
            var preferFranchise = franchise is not null && (item is BoxSet || Plugin.Instance.Configuration.PreferFranchiseThemes);
            IReadOnlyList<Video> videos = [];
            Choice? choice = null;
            var editionChecked = false;
            if (preferFranchise)
            {
                videos = await youtube.Search(franchise!, excludedIds, ct);
                choice = Matcher.Select(franchise!, videos, excludedIds, excludedRecordings, minimumMatchStrength);
                if (choice is null)
                {
                    videos = videos.Concat(await youtube.Search(franchise!, excludedIds, ct, nextPage: true)).DistinctBy(video => video.Id).ToArray();
                    choice = Matcher.Select(franchise!, videos, excludedIds, excludedRecordings, minimumMatchStrength);
                }
                if (choice is not null) work = franchise!;
            }
            if (choice is null && item is BoxSet)
            {
                var reason = Matcher.RejectionReason(franchise!, videos, excludedIds, excludedRecordings, out var code, minimumMatchStrength);
                store.Change(s => s.Outcomes[id] = "No match found: " + reason);
                return new("No match found", code);
            }
            if (choice is null)
            {
                videos = await youtube.Search(work, excludedIds, ct);
                (work, choice, editionChecked) = await Select(work, videos, false);
            }
            if (choice is null)
            {
                videos = videos.Concat(await youtube.Search(work, excludedIds, ct, nextPage: true)).DistinctBy(video => video.Id).ToArray();
                (work, choice, editionChecked) = await Select(work, videos, editionChecked);
            }
            if (choice is null)
            {
                videos = videos.Concat(await youtube.SearchAlbumTrack(work, excludedIds, ct)).DistinctBy(video => video.Id).ToArray();
                (work, choice, _) = await Select(work, videos, editionChecked);
            }
            if (choice is null)
            {
                var reason = Matcher.RejectionReason(work, videos, excludedIds, excludedRecordings, out var reasonCode, minimumMatchStrength);
                var excluded = reason == "Only previously used recordings were found";
                var result = (replacement, excluded) switch
                {
                    (true, _) => "No replacement found",
                    (_, true) => "Previously used recording excluded",
                    _ => "No match found"
                };
                store.Change(s => s.Outcomes[id] = excluded ? result : result + ": " + reason);
                return new(result, reasonCode);
            }
            for (var sourceAttempt = 0; sourceAttempt < JellyScoreConstants.SourceAttempts; sourceAttempt++)
            {
                var temporary = Path.Combine(folder, ".theme-" + Guid.NewGuid().ToString("N") + ".mp3");
                try
                {
                    reportStage?.Invoke("stageDownloading");
                    try { await Audio.Convert(choice, temporary, encoder, Plugin.Instance.Configuration.EffectiveTargetLufs, ct,
                        reportStage is null ? null : () => reportStage("stageProcessing")); }
                    catch (DownloadFailure) when (sourceAttempt == 0)
                    {
                        excludedIds.Add(choice.Video.Id);
                        choice = Matcher.Select(work, videos, excludedIds, excludedRecordings, minimumMatchStrength);
                        if (choice is null) throw;
                        continue;
                    }
                    return await Install(temporary, choice);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
            throw new DownloadFailure("Download failed. No other match was available.");
        }
        finally { gate.Release(); }
    }

    public async Task Delete(Guid id, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(id, _ => new SemaphoreSlim(1));
        await gate.WaitAsync(ct);
        try
        {
            var item = library.GetItemById(id) ?? throw new InvalidOperationException("Item no longer exists.");
            var (folder, _, _) = Location(item);
            var record = store.Read(s => s.Themes.GetValueOrDefault(id)) ?? throw new InvalidOperationException("No managed theme.");
            if (!Owned(record, item, folder)) throw new InvalidOperationException("Theme changed elsewhere. It was not deleted.");
            File.Delete(record.Path);
            store.Change(s => { s.Themes.Remove(id); s.Suppressed.Add(id); s.Outcomes[id] = "Deleted; automatic downloads paused until full rescan"; });
            Refresh(item);
        }
        finally { gate.Release(); }
    }

    public async Task<(int Deleted, int Skipped)> DeleteAll(CancellationToken ct)
    {
        var deleted = 0;
        var skipped = 0;
        foreach (var record in List())
        {
            ct.ThrowIfCancellationRequested();
            try { await Delete(record.ItemId, ct); deleted++; }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { skipped++; }
        }
        return (deleted, skipped);
    }

    public void ResetSuppression() => store.Change(s => s.Suppressed.Clear());
}
