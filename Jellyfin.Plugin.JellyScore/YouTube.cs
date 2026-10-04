using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Globalization;
using System.IO.Compression;

namespace Jellyfin.Plugin.JellyScore;

public sealed record Work(string Title, string? OriginalTitle, int? Year, bool Series, bool NoCompetingEdition = false,
    bool Franchise = false, IReadOnlyList<string>? Installments = null);
public sealed record Video(string Id, string Title, string Description, string Channel, int? Seconds,
    string? Album = null, string? Track = null, string? Artist = null, int? ReleaseYear = null, DateOnly? UploadDate = null);
public sealed record Choice(Video Video, string Recording, int Score, string Evidence);

public class SearchFailure(string message) : Exception(message);
public sealed class RateLimitFailure(string message) : SearchFailure(message);
public sealed class DownloadFailure(string message) : IOException(message);
public sealed class SourceUnavailable(string message) : IOException(message);

public sealed class YouTube
{
    private sealed class ChecksumFailure(string message) : IOException(message);

    private static readonly SemaphoreSlim DownloaderGate = new(1);
    private static readonly SemaphoreSlim RuntimeGate = new(1);
    // ponytail: one gate per Jellyfin process; coordinate across servers only if they share an egress IP.
    private static readonly SemaphoreSlim RequestGate = new(1);
    private static readonly HttpClient DownloaderClient = new() { Timeout = TimeSpan.FromMinutes(JellyScoreConstants.DownloaderTimeoutMinutes) };
    private static string? _downloaderPath;
    private static string? _runtime;
    private static int _preparingTools;
    private static DateTimeOffset _nextRequest;
    private static long _rateLimitedUntilTicks;
    public static string? DownloaderError { get; private set; }
    public static string? RuntimeError { get; private set; }
    public static string? ToolSetupStage => Volatile.Read(ref _preparingTools) != 0 ? "stagePreparingTools" : null;
    public static DateTimeOffset? RateLimitedUntil
    {
        get
        {
            var ticks = Interlocked.Read(ref _rateLimitedUntilTicks);
            return ticks > DateTimeOffset.UtcNow.UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
        }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static string DownloaderName(bool windows, bool macos, Architecture architecture, bool musl = false) => (windows, macos, architecture, musl) switch
    {
        (true, _, Architecture.X64, _) => JellyScoreConstants.DownloaderWindowsX64,
        (true, _, Architecture.Arm64, _) => JellyScoreConstants.DownloaderWindowsArm64,
        (_, true, Architecture.X64 or Architecture.Arm64, _) => JellyScoreConstants.DownloaderMacos,
        (_, _, Architecture.X64, true) => JellyScoreConstants.DownloaderMuslX64,
        (_, _, Architecture.Arm64, true) => JellyScoreConstants.DownloaderMuslArm64,
        (_, _, Architecture.X64, _) => JellyScoreConstants.DownloaderLinuxX64,
        (_, _, Architecture.Arm64, _) => JellyScoreConstants.DownloaderLinuxArm64,
        _ => throw new PlatformNotSupportedException("This plugin package has no yt-dlp binary for this server architecture.")
    };

    public static bool DownloaderAvailable => File.Exists(Path.Combine(Path.GetDirectoryName(typeof(YouTube).Assembly.Location)!, JellyScoreConstants.DownloaderVersionFile)) &&
        File.Exists(Path.Combine(Path.GetDirectoryName(typeof(YouTube).Assembly.Location)!, JellyScoreConstants.DownloaderChecksumsFile));

    private static async Task Executable(CancellationToken ct)
    {
        if (_downloaderPath is not null) return;
        await DownloaderGate.WaitAsync(ct);
        try
        {
            if (_downloaderPath is not null) return;
            var directory = Path.GetDirectoryName(typeof(YouTube).Assembly.Location)!;
            var version = (await File.ReadAllTextAsync(Path.Combine(directory, JellyScoreConstants.DownloaderVersionFile), ct)).Trim();
            var asset = DownloaderName(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture,
                RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase));
            var checksum = File.ReadLines(Path.Combine(directory, JellyScoreConstants.DownloaderChecksumsFile))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1] == asset)?[0];
            if (checksum is null || !Regex.IsMatch(version, "^[a-zA-Z0-9._-]+$") || !Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$"))
                throw new SearchFailure("Plugin package has no valid yt-dlp release or checksum for this platform.");
            var path = Path.Combine(Plugin.Instance.DownloaderFolder, version, asset);
            var url = new Uri($"{JellyScoreConstants.DownloaderReleaseUrl}/{version}/{asset}");
            _downloaderPath = await EnsureDownloader(path, checksum, token => DownloaderClient.GetStreamAsync(url, token), ct);
            DownloaderError = null;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or SearchFailure or UnauthorizedAccessException or PlatformNotSupportedException ||
            e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            DownloaderError = $"Could not install yt-dlp for this server: {e.Message}";
            throw new SearchFailure(DownloaderError);
        }
        finally { DownloaderGate.Release(); }
    }

    public static async Task RetryDownloader(CancellationToken ct)
    {
        var preparing = _downloaderPath is null || _runtime is null;
        if (preparing) Interlocked.Increment(ref _preparingTools);
        try
        {
            SearchFailure? failure = null;
            try { await Executable(ct); }
            catch (SearchFailure e) { failure = e; }
            try { await JavaScriptRuntime(ct); }
            catch (SearchFailure e) { failure ??= e; }
            if (failure is not null) throw failure;
        }
        finally { if (preparing) Interlocked.Decrement(ref _preparingTools); }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static string? DenoAsset(bool windows, bool macos, Architecture architecture, bool musl = false) => (windows, macos, architecture, musl) switch
    {
        (true, _, Architecture.X64, _) => "deno-x86_64-pc-windows-msvc.zip",
        (true, _, Architecture.Arm64, _) => "deno-aarch64-pc-windows-msvc.zip",
        (_, true, Architecture.X64, _) => "deno-x86_64-apple-darwin.zip",
        (_, true, Architecture.Arm64, _) => "deno-aarch64-apple-darwin.zip",
        (_, _, Architecture.X64, false) => "deno-x86_64-unknown-linux-gnu.zip",
        (_, _, Architecture.Arm64, false) => "deno-aarch64-unknown-linux-gnu.zip",
        _ => null
    };

    private static async Task JavaScriptRuntime(CancellationToken ct)
    {
        if (_runtime is not null) return;
        await RuntimeGate.WaitAsync(ct);
        try
        {
            if (_runtime is not null) return;
            foreach (var (runtime, versionPattern) in new[]
            {
                ("deno", @"^deno (?:[3-9]|2\.(?:[3-9]|\d{2,}))\."),
                ("node", @"^v(?:2[2-9]|[3-9]\d)\.")
            })
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(JellyScoreConstants.RuntimeCheckTimeoutSeconds));
                try
                {
                    if (!Regex.IsMatch(await Audio.Run(runtime, ["--version"], timeout.Token), versionPattern)) continue;
                    RuntimeError = null;
                    _runtime = runtime;
                    return;
                }
                catch (Exception e) when (e is IOException or OperationCanceledException && !ct.IsCancellationRequested) { }
            }
            var asset = DenoAsset(OperatingSystem.IsWindows(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture,
                RuntimeInformation.RuntimeIdentifier.Contains("musl", StringComparison.OrdinalIgnoreCase));
            if (asset is null) throw new SearchFailure("A supported Deno or Node.js runtime is required on this server platform.");
            var directory = Path.GetDirectoryName(typeof(YouTube).Assembly.Location)!;
            var version = (await File.ReadAllTextAsync(Path.Combine(directory, JellyScoreConstants.RuntimeVersionFile), ct)).Trim();
            var checksum = File.ReadLines(Path.Combine(directory, JellyScoreConstants.RuntimeChecksumsFile))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1] == asset)?[0];
            if (checksum is null || !Regex.IsMatch(version, "^v[0-9.]+$") || !Regex.IsMatch(checksum, "^[a-fA-F0-9]{64}$"))
                throw new SearchFailure("Plugin package has no valid Deno release or checksum for this platform.");
            var folder = Path.Combine(Plugin.Instance.DownloaderFolder, version);
            var archive = await EnsureDownloader(Path.Combine(folder, asset), checksum,
                token => DownloaderClient.GetStreamAsync(new Uri($"{JellyScoreConstants.RuntimeReleaseUrl}/{version}/{asset}"), token), ct);
            var executable = Path.Combine(folder, OperatingSystem.IsWindows() ? "deno.exe" : "deno");
            var temporary = executable + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using var zip = ZipFile.OpenRead(archive);
                var entry = zip.GetEntry(Path.GetFileName(executable)) ?? throw new IOException("Verified Deno archive has no executable.");
                await using (var input = entry.Open())
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, ct);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.Move(temporary, executable, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            RuntimeError = null;
            _runtime = "deno:" + executable;
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or SearchFailure ||
            e is TaskCanceledException && !ct.IsCancellationRequested)
        {
            RuntimeError = $"Could not prepare a JavaScript runtime for yt-dlp: {e.Message}";
            throw new SearchFailure(RuntimeError);
        }
        finally { RuntimeGate.Release(); }
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static async Task<string> EnsureDownloader(string path, string checksum, Func<CancellationToken, Task<Stream>> download, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            await using var cached = File.OpenRead(path);
            if (string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(cached, ct)), checksum, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (var attempt = 0; ; attempt++)
        {
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await using (var input = await download(ct))
                await using (var output = File.Create(temporary)) await input.CopyToAsync(output, ct);
                await using (var file = File.OpenRead(temporary))
                    if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(file, ct)), checksum, StringComparison.OrdinalIgnoreCase))
                        throw new ChecksumFailure("Downloaded asset checksum did not match the pinned release.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                File.Move(temporary, path, true);
                return path;
            }
            catch (Exception e) when (attempt < JellyScoreConstants.InstallerAttempts - 1 && !ct.IsCancellationRequested &&
                e is IOException and not ChecksumFailure or HttpRequestException or TaskCanceledException)
            {
                await Task.Delay(TimeSpan.FromSeconds(JellyScoreConstants.DownloadRetryBaseSeconds << attempt), ct);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static IEnumerable<Video> Shortlist(Work work, IEnumerable<Video> flat, IReadOnlySet<string> excludedIds, bool nextPage) =>
        flat.Where(video => !excludedIds.Contains(video.Id) && Matcher.Promising(work, video))
            .Skip(nextPage ? JellyScoreConstants.SearchShortlistSize : 0).Take(JellyScoreConstants.SearchShortlistSize);

    public async Task<IReadOnlyList<Video>> Search(Work work, IReadOnlySet<string> excludedIds, CancellationToken ct, bool nextPage = false)
    {
        var titles = new[] { work.Title, work.OriginalTitle }.Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var videos = new Dictionary<string, Video>();
        foreach (var title in titles)
        {
            var query = work.Series ? $"{title} theme song" : work.Franchise ? $"{title} main theme soundtrack" : $"{title} {work.Year} main theme soundtrack";
            var flat = await Flat(query, JellyScoreConstants.SearchResultCount, ct);
            var shortlist = Shortlist(work, flat, excludedIds, nextPage);
            foreach (var video in await Details(shortlist, ct)) videos[video.Id] = video;
        }
        return videos.Values.ToArray();
    }

    public async Task<IReadOnlyList<Video>> SearchAlbumTrack(Work work, IReadOnlySet<string> excludedIds, CancellationToken ct)
    {
        var query = $"{work.Title} {work.Year} soundtrack album";
        var flat = await Flat(query, JellyScoreConstants.AlbumSearchResultCount, ct);
        var fullAlbum = flat.FirstOrDefault(video => video.Seconds > JellyScoreConstants.MaximumThemeSeconds &&
            video.Title.Contains("album", StringComparison.OrdinalIgnoreCase) &&
            video.Title.Contains(work.Title, StringComparison.OrdinalIgnoreCase));
        if (fullAlbum is null) return [];
        var album = await Recheck(fullAlbum.Id, ct);
        // ponytail: first album track is only a search hint; expand the tracklist if measured misses warrant it.
        var firstTrack = FirstTrack(album.Description);
        if (firstTrack is null) return [];
        var soundtrack = work.Series ? "TV soundtrack" : "Original Motion Picture Soundtrack";
        var tracks = await Flat($"{firstTrack} {work.Title} {soundtrack}", JellyScoreConstants.TrackSearchResultCount, ct);
        return await Details(tracks.Where(video => !excludedIds.Contains(video.Id) && (Matcher.Promising(work, video) ||
            video.Seconds is > 0 and <= JellyScoreConstants.MaximumThemeSeconds && video.Title.Contains(firstTrack, StringComparison.OrdinalIgnoreCase)))
            .Take(JellyScoreConstants.AlbumTrackShortlistSize), ct);
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static string? FirstTrack(string description)
    {
        var match = Regex.Match(description, @"(?im)\btracklist:?\s*\n\s*1[.)]\s*(.+)$");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static async Task<IReadOnlyList<Video>> Flat(string query, int count, CancellationToken ct)
    {
        var output = await Tool(["--no-warnings", "--flat-playlist", "--dump-json", "--socket-timeout", $"{JellyScoreConstants.DownloaderSocketTimeoutSeconds}",
            "--retries", $"{JellyScoreConstants.DownloaderRetries}", $"ytsearch{count}:" + query], ct);
        var videos = new List<Video>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith('{')) continue;
            using var doc = JsonDocument.Parse(line);
            videos.Add(Parse(doc.RootElement));
        }
        if (videos.Count == 0) throw new SearchFailure("Search returned no video details; retry when YouTube is available.");
        return videos;
    }

    private static async Task<IReadOnlyList<Video>> Details(IEnumerable<Video> candidates, CancellationToken ct)
    {
        var videos = new List<Video>();
        foreach (var candidate in candidates)
        {
            try { videos.Add(await Recheck(candidate.Id, ct)); }
            catch (SourceUnavailable) { }
        }
        return videos;
    }

    internal static string? VideoId(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !uri.IsDefaultPort) return null;
        string? id = null;
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) id = uri.AbsolutePath.TrimStart('/');
        else if (new[] { "youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
        {
            if (uri.AbsolutePath == "/watch")
            {
                var values = uri.Query.TrimStart('?').Split('&').Where(part => part.StartsWith("v=", StringComparison.Ordinal)).ToArray();
                if (values.Length == 1) id = values[0][2..];
            }
            else
            {
                var parts = uri.AbsolutePath.Split('/');
                if (parts.Length == 3 && parts[1] is "shorts" or "embed" or "live") id = parts[2];
            }
        }
        return id is not null && Regex.IsMatch(id, "^[a-zA-Z0-9_-]{11}$") ? id : null;
    }

    internal static async Task<Choice> ManualChoice(string id, Work work, CancellationToken ct)
    {
        var video = await Recheck(id, ct);
        if (video.Id != id || video.Seconds is not { } seconds || seconds is < JellyScoreConstants.MinimumThemeSeconds or > JellyScoreConstants.MaximumThemeSeconds)
            throw new DownloadFailure("Duration is missing or outside the theme range");
        var matched = Matcher.Evaluate(work, video);
        return new Choice(video, matched?.Recording ?? "youtube:" + id, matched?.Score ?? 0, "YouTube source selected by administrator");
    }

    private static async Task<Video> Recheck(string id, CancellationToken ct)
    {
        if (!Regex.IsMatch(id, "^[a-zA-Z0-9_-]{11}$")) throw new SearchFailure("Invalid source video ID.");
        var output = await Tool(["--no-warnings", "--skip-download", "--dump-json", "--no-playlist", "https://www.youtube.com/watch?v=" + id], ct);
        using var doc = JsonDocument.Parse(output);
        return Parse(doc.RootElement);
    }

    public static async Task Download(string id, string path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < JellyScoreConstants.ToolAttempts; attempt++)
        {
            try
            {
                await RunTool(["--no-playlist", "--no-progress", "--no-part", "--no-continue", "--retries", $"{JellyScoreConstants.DownloaderRetries}",
                    "--socket-timeout", $"{JellyScoreConstants.DownloaderSocketTimeoutSeconds}", "-f", "bestaudio", "--max-filesize", JellyScoreConstants.DownloaderMaximumFileSize,
                    "-o", path, "https://www.youtube.com/watch?v=" + id], true, ct);
                return;
            }
            catch (IOException) when (attempt < JellyScoreConstants.ToolAttempts - 1)
            {
                if (File.Exists(path)) File.Delete(path);
                await Task.Delay(TimeSpan.FromSeconds(JellyScoreConstants.DownloadRetryBaseSeconds << attempt), ct);
            }
            catch (IOException e) { throw new DownloadFailure(e.Message); }
        }
    }

    private static Video Parse(JsonElement item)
    {
        var duration = item.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
        var uploaded = DateOnly.TryParseExact(Text(item, "upload_date"), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : (DateOnly?)null;
        return new Video(item.GetProperty("id").GetString()!, item.GetProperty("title").GetString()!,
            item.TryGetProperty("description", out value) ? value.GetString() ?? "" : "",
            item.TryGetProperty("channel", out value) ? value.GetString() ?? "" : "", duration > 0 ? (int)duration : null,
            Text(item, "album"), Text(item, "track"), Text(item, "artist"),
            item.TryGetProperty("release_year", out value) && value.ValueKind == JsonValueKind.Number ? value.GetInt32() : null, uploaded);
    }

    private static string? Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // ReSharper disable once MemberCanBePrivate.Global
    public static bool IsRateLimitError(string message) =>
        message.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Sign in to confirm you're not a bot", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Sign in to confirm you’re not a bot", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("This content isn't available, try again later", StringComparison.OrdinalIgnoreCase);

    // ReSharper disable once MemberCanBePrivate.Global
    public static bool ValidCookies(string cookies) => cookies.Length <= JellyScoreConstants.MaximumCookiesCharacters &&
        !cookies.Contains('\0') && (cookies.StartsWith("# Netscape HTTP Cookie File\n", StringComparison.Ordinal) ||
            cookies.StartsWith("# HTTP Cookie File\n", StringComparison.Ordinal) ||
            cookies.StartsWith("# Netscape HTTP Cookie File\r\n", StringComparison.Ordinal) ||
            cookies.StartsWith("# HTTP Cookie File\r\n", StringComparison.Ordinal));

    private static async Task<string> RunTool(string[] args, bool download, CancellationToken ct)
    {
        await RetryDownloader(ct);
        var executable = _downloaderPath!;
        var runtime = _runtime!;
        await RequestGate.WaitAsync(ct);
        string? cookiesPath = null;
        try
        {
            if (RateLimitedUntil is { } until) throw new RateLimitFailure($"YouTube is rate limiting this server; requests are paused until {until:u}.");
            var delay = _nextRequest - DateTimeOffset.UtcNow;
            if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
            var cookies = Plugin.Instance.Configuration.YouTubeCookies;
            if (!string.IsNullOrEmpty(cookies))
            {
                if (!ValidCookies(cookies)) throw new SearchFailure("The saved YouTube cookies are invalid. Clear or replace them in settings.");
                cookiesPath = Path.Combine(Plugin.Instance.DownloaderFolder, "youtube-cookies-" + Guid.NewGuid().ToString("N") + ".txt");
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                await using (var file = new FileStream(cookiesPath, options))
                await using (var writer = new StreamWriter(file)) await writer.WriteAsync(cookies.AsMemory(), ct);
            }
            var cookieArgs = cookiesPath is null ? Array.Empty<string>() : new[] { "--cookies", cookiesPath };
            try { return await Audio.Run(executable, ["--js-runtimes", runtime, "--impersonate", "chrome", "--sleep-requests", JellyScoreConstants.InternalRequestSpacingSeconds.ToString(CultureInfo.InvariantCulture), .. cookieArgs, .. args], ct); }
            catch (IOException e) when (IsRateLimitError(e.Message))
            {
                var cooldown = DateTimeOffset.UtcNow.AddMinutes(JellyScoreConstants.RateLimitCooldownMinutes);
                Interlocked.Exchange(ref _rateLimitedUntilTicks, cooldown.UtcTicks);
                throw new RateLimitFailure($"YouTube is rate limiting this server; requests are paused until {cooldown:u}.");
            }
            finally
            {
                _nextRequest = DateTimeOffset.UtcNow.AddSeconds(download ? JellyScoreConstants.DownloadSpacingSeconds : JellyScoreConstants.InternalRequestSpacingSeconds);
            }
        }
        finally
        {
            try { if (cookiesPath is not null && File.Exists(cookiesPath)) File.Delete(cookiesPath); }
            finally { RequestGate.Release(); }
        }
    }

    private static async Task<string> Tool(string[] args, CancellationToken ct)
    {
        for (var attempt = 0; attempt < JellyScoreConstants.ToolAttempts; attempt++)
        {
            try { return await RunTool(args, false, ct); }
            catch (IOException e) when (e.Message.Contains("This video is not available", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase))
            { throw new SourceUnavailable(e.Message); }
            catch (IOException) when (attempt < JellyScoreConstants.ToolAttempts - 1)
            { await Task.Delay(TimeSpan.FromSeconds(JellyScoreConstants.DownloadRetryBaseSeconds << attempt), ct); }
        }
        throw new SearchFailure("Bundled yt-dlp failed. Check network access and update the plugin package.");
    }
}

public static partial class Matcher
{
    private static readonly string[] ExcludedFormatPatterns =
    [
        "cover", "remix", @"fan.?edit", "extended", "reaction", "trailer", "review", "full album", "compilation",
        "livestream", "live stream", @"live (?:performance|concert|at|session|version|recording)", @"live(?=\)|\])",
        @"(?:performed|recorded) live", @"performs?\b.*\blive", "karaoke", "piano cover", "tutorials?",
        "how to play", "game", "parody", "tribute", "ranked", @"top\s?10"
    ];
    private static readonly Regex ExcludedFormat = new(@"\b(" + string.Join("|", ExcludedFormatPatterns) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    [GeneratedRegex(@"\b(?:s\d{1,2}\s*e\d{1,3}|\d{1,2}x\d{1,3}|season\s+\d+\s+episode\s+\d+)\b", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumber();
    [GeneratedRegex(@"\b(?:opening|intro(?:duction)?)\s+scene\b", RegexOptions.IgnoreCase)]
    private static partial Regex IntroScene();
    [GeneratedRegex(@"\b[\p{L}]+['’]s\s+intro(?:duction)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex CharacterIntro();
    [GeneratedRegex(@"\bseasons?\s+\d+\s*(?:[-–—]|to|through|&|and)\s*\d+\b|\b(?:all|every)\s+(?:\w+\s+){0,3}(?:seasons?|title cards?|openings?|intros?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesCollection();
    [GeneratedRegex(@"\b(part two|part 2|sequel)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Sequel();
    [GeneratedRegex(@"\b(opening|theme|main title|title sequence|credits|end title|intro|soundtrack|score|suite|overture|ost)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Theme();
    [GeneratedRegex(@"\b(closing|end)\s+(credits?|titles?|theme)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Closing();
    [GeneratedRegex(@"\b(opening(?!\s+scene\b)|intro|main titles?|title sequence)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesOpening();
    [GeneratedRegex(@"\b(theme|opening|main title|title sequence|intro|overture)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MainTheme();
    [GeneratedRegex(@"(?im)^Album:\s*(.+)$")]
    private static partial Regex AlbumLine();
    [GeneratedRegex(@"\b(19\d{2}|20\d{2})\b")]
    private static partial Regex Years();
    [GeneratedRegex(@"\b(?:motion picture|(?:movie|film)\s+(?:soundtrack|score|theme|opening|ost))\b", RegexOptions.IgnoreCase)]
    private static partial Regex FilmEdition();
    [GeneratedRegex(@"\b(?:(?:tv|television)\s+series|(?:tv|television)\s+(?:soundtrack|score|theme|opening|ost))\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesEdition();

    private static string Normal(string value) => Regex.Replace(value.ToLowerInvariant(), @"[^\p{L}\p{N}]+", " ").Trim();
    private static bool SameWorkTitle(Work work, string title) => new[] { work.Title, work.OriginalTitle }
        .Where(s => !string.IsNullOrWhiteSpace(s)).Any(s => Normal(s!) == Normal(title));
    public static bool NoCompetingEdition(Work work, string id, IReadOnlyList<(string? Id, string? Title, int? Year)> films,
        IReadOnlyList<(string? Id, string? Title, int? Year)> shows)
    {
        var target = work.Series ? shows : films;
        var other = work.Series ? films : shows;
        return films.Count < 20 && shows.Count < 20 && target.Count > 0 &&
            target.Any(r => r.Id == id && r.Year == work.Year) &&
            !target.Any(r => r.Id != id && (r.Title is null || SameWorkTitle(work, r.Title))) &&
            !other.Any(r => r.Title is null || SameWorkTitle(work, r.Title));
    }
    private static bool Contains(string text, string title) => (" " + Normal(text) + " ").Contains(" " + Normal(title) + " ", StringComparison.Ordinal);
    private static bool EpisodeClip(string title) => IntroScene().IsMatch(title) ||
        EpisodeNumber().IsMatch(title) && CharacterIntro().IsMatch(title);
    private static bool OtherNamedTheme(Work work, Video video)
    {
        if (!work.Series || !Regex.IsMatch(video.Title, @"\btheme\b", RegexOptions.IgnoreCase)) return false;
        var remainder = " " + Normal(video.Title) + " ";
        foreach (var title in new[] { work.Title, work.OriginalTitle }.Where(s => !string.IsNullOrWhiteSpace(s)))
            remainder = remainder.Replace(" " + Normal(title!) + " ", " ", StringComparison.Ordinal);
        remainder = Regex.Replace(remainder, @"\b(?:\d+|official|original|main|theme|song|opening|intro|title|credits|soundtrack|score|ost|tv|television|series|season)\b", " ");
        return remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3;
    }

    private static bool LinkedSeriesTheme(Work work, string description)
    {
        var text = Normal(description);
        return new[] { work.Title, work.OriginalTitle }.Where(s => !string.IsNullOrWhiteSpace(s)).Any(title =>
            Regex.IsMatch(text, $@"\b(?:main )?theme (?:of|for) (?:the )?(?:tv show|tv series|television series) {Regex.Escape(Normal(title!))}\b"));
    }

    private static int? LinkedYear(Work work, string description)
    {
        var text = Normal(description);
        foreach (var title in new[] { work.Title, work.OriginalTitle }.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var year = Regex.Match(text, $@"(?:^| ){Regex.Escape(Normal(title!))} (19\d{{2}}|20\d{{2}})(?: |$)");
            if (year.Success) return int.Parse(year.Groups[1].Value);
        }
        return null;
    }

    public static bool Promising(Work work, Video video) =>
        (video.Seconds is not { } seconds || seconds is >= JellyScoreConstants.MinimumThemeSeconds and <= JellyScoreConstants.MaximumThemeSeconds) &&
        !ExcludedFormat.IsMatch(video.Title) && !EpisodeClip(video.Title) &&
        (!Sequel().IsMatch(video.Title) || Sequel().IsMatch(work.Title)) &&
        Theme().IsMatch(video.Title) &&
        (Contains(video.Title, work.Title) || Contains(video.Description, work.Title) ||
            work.OriginalTitle is not null && (Contains(video.Title, work.OriginalTitle) || Contains(video.Description, work.OriginalTitle)) ||
            MainTheme().IsMatch(video.Title));

    private static Choice Rank(Work work, Video video, string recording, string album, int? linkedYear, bool soundtrackMatch, bool hasArtist = false)
    {
        var title = video.Title;
        var identity = title + " " + album;
        var evidence = new List<string>();
        var score = 0;
        void Add(int points, string source) { score += points; evidence.Add($"{source} {points:+#;-#;0}"); }

        if (new[] { work.Title, work.OriginalTitle }.Where(s => !string.IsNullOrWhiteSpace(s)).Any(s => Contains(video.Title, s!))) Add(JellyScoreConstants.WorkTitlePoints, "Work in title");
        else
        {
            var words = Normal(work.Title).Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= JellyScoreConstants.PartialTitleWordMinimumLength);
            var matchingWords = words.Count(w => Contains(video.Title, w));
            if (matchingWords > 0) Add(Math.Min(JellyScoreConstants.PartialTitleMaximumPoints,
                JellyScoreConstants.PartialTitleWordPoints * matchingWords), "Partial title");
        }
        if (work.Year is { } year)
        {
            if (Contains(identity, year.ToString(CultureInfo.InvariantCulture))) Add(JellyScoreConstants.TitleYearPoints, "Edition year in title or album");
            else if (video.ReleaseYear == year || linkedYear == year) Add(JellyScoreConstants.MetadataYearPoints, "Edition year in metadata");
        }
        if (work.Series ? SeriesEdition().IsMatch(identity) : FilmEdition().IsMatch(identity)) Add(JellyScoreConstants.MatchingEditionPoints, "Matching film/TV edition");
        if (Contains(title, "main theme") || Contains(title, "main title")) Add(JellyScoreConstants.MainThemePoints, "Main theme or title");
        else if (Contains(title, "theme")) Add(JellyScoreConstants.ThemePoints, "Theme");
        if (!Closing().IsMatch(title) && SeriesOpening().IsMatch(title)) Add(JellyScoreConstants.OpeningPoints, "Opening or intro");
        if (Contains(title, "soundtrack") || Contains(title, "ost") || Contains(title, "score")) Add(JellyScoreConstants.SoundtrackLabelPoints, "Soundtrack label");
        if (Contains(title, "official")) Add(JellyScoreConstants.OfficialLabelPoints, "Official label");
        if (soundtrackMatch) { Add(JellyScoreConstants.SoundtrackTrackPoints, "Matching soundtrack track"); if (hasArtist) Add(JellyScoreConstants.ArtistPoints, "Identified artist"); }
        if (video.Seconds is >= JellyScoreConstants.TypicalMinimumSeconds and <= JellyScoreConstants.TypicalMaximumSeconds)
            Add(JellyScoreConstants.TypicalDurationPoints, "Typical music duration");
        else if (video.Seconds < JellyScoreConstants.TypicalMinimumSeconds && SeriesOpening().IsMatch(title)) Add(JellyScoreConstants.ShortOpeningPoints, "Short opening");
        else if (video.Seconds < JellyScoreConstants.TypicalMinimumSeconds) Add(JellyScoreConstants.ShortRecordingPenalty, "Very short recording");
        else Add(JellyScoreConstants.LongRecordingPoints, "Long recording");
        if (new[] { "music", "records", "soundtrack", "score", "film", "cinema" }.Any(s => video.Channel.Contains(s, StringComparison.OrdinalIgnoreCase))) Add(JellyScoreConstants.MusicChannelPoints, "Music channel");
        if (SeriesCollection().IsMatch(title)) Add(JellyScoreConstants.MultiSeasonCollectionPenalty, "Multi-season collection");
        else if (title.Contains("every ", StringComparison.OrdinalIgnoreCase) || title.Contains("all ", StringComparison.OrdinalIgnoreCase) && Contains(title, "theme")) Add(JellyScoreConstants.CollectionPenalty, "Collection video");
        return new Choice(video, recording, score, string.Join("; ", evidence));
    }

    // ReSharper disable once MemberCanBePrivate.Global
    public static int MatchStrength(int score) => Math.Clamp(score, JellyScoreConstants.MinimumMatchStrength, JellyScoreConstants.MaximumMatchStrength);

    // ReSharper disable once MemberCanBePrivate.Global
    public static Choice? Evaluate(Work work, Video video) => Evaluate(work, video, out _);

    // ReSharper disable once MemberCanBePrivate.Global
    public static Choice? Evaluate(Work work, Video video, out string reason)
    {
        reason = "";
        if (video.Seconds is not { } length || length is < JellyScoreConstants.MinimumThemeSeconds or > JellyScoreConstants.MaximumThemeSeconds)
        { reason = "Duration is missing or outside the theme range"; return null; }
        if (work.Year is { } workYear && video.UploadDate is { Year: var uploadYear } && uploadYear < workYear - JellyScoreConstants.EarlyUploadYears)
        { reason = "Upload predates this work"; return null; }
        var lines = video.Description.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var trackIndex = Array.FindIndex(lines, s => s.Contains('·'));
        var album = video.Album ?? AlbumLine().Match(video.Description).Groups[1].Value.Trim();
        if (album.Length == 0 && trackIndex >= 0 && lines.Length > trackIndex + 1)
            album = lines[trackIndex + 1];
        var title = video.Title;
        var identityText = title + " " + album;
        if (work.Franchise && work.Installments?.Any(name => Normal(name) != Normal(work.Title) &&
            Contains(identityText, name)) == true)
        { reason = "Theme belongs to a specific installment"; return null; }
        if (work.Franchise && Years().Matches(identityText).Any(match => !Contains(work.Title, match.Value)))
        { reason = "Theme belongs to a specific installment"; return null; }
        if (work.Franchise && work.Installments?.Any(name => Normal(name) == Normal(work.Title)) == true &&
            !Regex.IsMatch(Normal(identityText + " " + video.Description),
                $@"\b{Regex.Escape(Normal(work.Title))} (?:film |movie )?(?:collection|franchise|series)\b"))
        { reason = "No evidence of a shared franchise theme"; return null; }
        if (work.Franchise && Regex.IsMatch(Normal(album), $@"^{Regex.Escape(Normal(work.Title))} (?:and|part|episode|chapter)\b"))
        { reason = "Theme belongs to a specific installment"; return null; }
        if (ExcludedFormat.IsMatch(title) || ExcludedFormat.IsMatch(album) || EpisodeClip(title))
        { reason = "Cover, remix, sequel, or other excluded format"; return null; }
        if (Sequel().IsMatch(identityText) && !Sequel().IsMatch(work.Title))
        { reason = "Soundtrack belongs to a different sequel"; return null; }
        var workTitles = new[] { work.Title, work.OriginalTitle }.Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();
        if (!workTitles.Any(s => Contains(identityText, s!) || Contains(video.Description, s!)))
        { reason = "Title or description does not identify this work"; return null; }
        if (work.Franchise && !Regex.IsMatch(Normal(title),
            $@"(?:^| ){Regex.Escape(Normal(work.Title))} (?:official |original |main |collection |film |movie |soundtrack |ost |score |music |song )*(?:theme|main title|opening|intro)\b") &&
            Normal(album) != Normal(work.Title) && Normal(album) != Normal(work.Title) + " collection" &&
            !Regex.IsMatch(Normal(video.Description), $@"\btheme (?:for|of) (?:the )?{Regex.Escape(Normal(work.Title))} (?:series|collection|franchise)\b"))
        { reason = "No evidence of a shared franchise theme"; return null; }
        var linkedYear = LinkedYear(work, video.Description);
        if (work.Year is { } year && (video.ReleaseYear is { } releaseYear && releaseYear != year ||
            linkedYear is { } descriptionYear && descriptionYear != year ||
            Years().Matches(identityText).Select(m => int.Parse(m.Value)).Any(y => y != year)))
        { reason = "Different release year or adaptation"; return null; }
        if (work.Series ? FilmEdition().IsMatch(identityText) : SeriesEdition().IsMatch(identityText))
        { reason = "Soundtrack belongs to a different film or series edition"; return null; }
        // Unknown edition/year is deliberately insufficient for ambiguous remakes.
        if (work.Year is not null && video.ReleaseYear is null && linkedYear is null && !Years().IsMatch(identityText) &&
            Normal(work.Title).Split(' ').Length <= JellyScoreConstants.AmbiguousTitleWordLimit &&
            !(work.NoCompetingEdition && workTitles.Any(s => Contains(identityText, s!))) &&
            !(Sequel().IsMatch(work.Title) && Contains(title, work.Title)))
        { reason = "Release year missing for an ambiguous title"; return null; }
        var albumMatches = Contains(album, work.Title) || work.OriginalTitle is not null && Contains(album, work.OriginalTitle);
        var parts = trackIndex >= 0 ? lines[trackIndex].Split('·', StringSplitOptions.TrimEntries) : [];
        var track = video.Track ?? (parts.Length >= 2 ? parts[0] : null);
        var artist = video.Artist ?? (parts.Length >= 2 ? parts[1] : null);
        var soundtrackTrack = albumMatches && track is not null && (Contains(title, track) || Contains(track, title));
        if (!soundtrackTrack && OtherNamedTheme(work, video) && !(albumMatches && SeriesEdition().IsMatch(album)) &&
            !(work.Year is { } seriesYear && linkedYear == seriesYear && SeriesEdition().IsMatch(video.Description)) &&
            !LinkedSeriesTheme(work, video.Description))
        { reason = "Title names a different theme"; return null; }
        if (!Theme().IsMatch(title) && !soundtrackTrack)
        { reason = "Neither the title nor a matching soundtrack identifies this as music"; return null; }
        var candidate = soundtrackTrack
            ? Rank(work, video, Normal(track!) + "|" + Normal(artist ?? "") + "|" + Normal(album) + "|" + work.Year, album, linkedYear, true, artist is not null)
            : Rank(work, video, Normal(title) + "|" + work.Year, album, linkedYear, false);
        if (candidate.Score <= 0) { reason = "Ranking score is too low"; return null; }
        return candidate;
    }

    // ReSharper disable once UnusedMember.Global
    public static string RejectionReason(Work work, IEnumerable<Video> videos, IReadOnlySet<string> excludedVideos, IReadOnlySet<string> excludedRecordings) =>
        RejectionReason(work, videos, excludedVideos, excludedRecordings, out _);

    public static string RejectionReason(Work work, IEnumerable<Video> videos, IReadOnlySet<string> excludedVideos, IReadOnlySet<string> excludedRecordings, out string code, int minimumMatchStrength = 0)
    {
        code = "reasonNoMatch";
        var reasons = new List<string>();
        var eligible = new List<Choice>();
        foreach (var video in videos)
        {
            var choice = Evaluate(work, video, out var reason);
            if (choice is null) reasons.Add(reason);
            else eligible.Add(choice);
        }
        if (eligible.Count > 0)
        {
            if (eligible.All(c => excludedVideos.Contains(c.Video.Id) || excludedRecordings.Contains(c.Recording)))
            {
                code = "reasonPreviouslyUsed";
                return "Only previously used recordings were found";
            }
            if (eligible.Where(c => !excludedVideos.Contains(c.Video.Id) && !excludedRecordings.Contains(c.Recording))
                .All(c => MatchStrength(c.Score) < minimumMatchStrength))
            {
                code = "reasonBelowStrength";
                return "Only recordings below the minimum match strength were found";
            }
            return "Eligible recordings were found";
        }
        if (reasons.Count == 0) { code = "reasonNoResults"; return "No search results passed the title and duration shortlist"; }
        var groups = reasons.GroupBy(reason => reason).OrderByDescending(group => group.Count()).ToArray();
        code = groups[0].Key switch
        {
            "Duration is missing or outside the theme range" => "reasonDuration",
            "Cover, remix, sequel, or other excluded format" => "reasonExcludedFormat",
            "Soundtrack belongs to a different sequel" or "Different release year or adaptation" or "Upload predates this work" or
                 "Soundtrack belongs to a different film or series edition" or "Theme belongs to a specific installment" or
                "Release year missing for an ambiguous title" => "reasonEdition",
            "Title or description does not identify this work" or "Title names a different theme" or "No evidence of a shared franchise theme" => "reasonWrongWork",
            "Neither the title nor a matching soundtrack identifies this as music" => "reasonNotMusic",
            "Ranking score is too low" => "reasonLowScore",
            _ => "reasonNoMatch"
        };
        var counts = groups.Take(3).Select(group => $"{group.Count()} {group.Key}");
        return $"Rejected {reasons.Count} candidates: {string.Join("; ", counts)}";
    }

    public static Choice? Select(Work work, IEnumerable<Video> videos, IReadOnlySet<string> excludedVideos, IReadOnlySet<string> excludedRecordings, int minimumMatchStrength = 0)
    {
        var choices = videos.Where(v => !excludedVideos.Contains(v.Id)).Select(v => Evaluate(work, v))
            .OfType<Choice>().Where(c => !excludedRecordings.Contains(c.Recording) && MatchStrength(c.Score) >= minimumMatchStrength).ToArray();
        if (work.Series)
        {
            var openings = choices.Where(c => !Closing().IsMatch(c.Video.Title) &&
                (SeriesOpening().IsMatch(c.Video.Title) || Contains(c.Video.Title, "theme"))).ToArray();
            if (openings.Length > 0) choices = openings;
        }
        else
        {
            var mainThemes = choices.Where(c => !Closing().IsMatch(c.Video.Title) && MainTheme().IsMatch(c.Video.Title)).ToArray();
            var closingThemes = choices.Where(c => Closing().IsMatch(c.Video.Title)).ToArray();
            if (mainThemes.Length > 0) choices = mainThemes;
            else if (closingThemes.Length > 0) choices = closingThemes;
        }
        return choices.OrderByDescending(c => c.Score).FirstOrDefault();
    }
}
