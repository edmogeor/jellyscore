namespace Jellyfin.Plugin.JellyScore;

/// <summary>Shared JellyScore names and matching, download, and scan limits.</summary>
internal static class JellyScoreConstants
{
    internal const string PluginGuid = "129e8a8b-87f1-48d3-802b-7dd151d72920";
    internal const string PluginName = "JellyScore";
    internal const string DownloaderFolder = "jellyscore-yt-dlp";
    internal const string DownloaderVersionFile = "yt-dlp-version";
    internal const string DownloaderChecksumsFile = "SHA2-256SUMS";
    internal const string DownloaderReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/download";
    internal const string RuntimeVersionFile = "deno-version";
    internal const string RuntimeChecksumsFile = "deno-checksums";
    internal const string RuntimeReleaseUrl = "https://github.com/denoland/deno/releases/download";
    internal const string DownloaderWindowsX64 = "yt-dlp.exe";
    internal const string DownloaderWindowsArm64 = "yt-dlp_arm64.exe";
    internal const string DownloaderMacos = "yt-dlp_macos";
    internal const string DownloaderLinuxX64 = "yt-dlp_linux";
    internal const string DownloaderLinuxArm64 = "yt-dlp_linux_aarch64";
    internal const string DownloaderMuslX64 = "yt-dlp_musllinux";
    internal const string DownloaderMuslArm64 = "yt-dlp_musllinux_aarch64";
    internal const string StateFile = "theme-songs-state.json";
    internal const string ThemeBaseName = "theme";
    internal const string ThemeFile = ThemeBaseName + ".mp3";
    internal const string ThemeMusicFolder = ThemeBaseName + "-music";
    internal const string LibraryRefreshKey = "RefreshLibrary";
    internal const string ScanTaskKey = "ThemeSongsRescan";

    internal const int MinimumMatchStrength = 0;
    internal const int MaximumMatchStrength = 100;
    internal const int DefaultMatchStrength = 50;
    internal const int MinimumThemeSeconds = 10;
    internal const int MaximumThemeSeconds = 480;
    internal const int TypicalMinimumSeconds = 30;
    internal const int TypicalMaximumSeconds = 360;
    internal const int EarlyUploadYears = 1;
    internal const int AmbiguousTitleWordLimit = 3;
    internal const int PartialTitleWordMinimumLength = 4;

    internal const int WorkTitlePoints = 30;
    internal const int PartialTitleWordPoints = 8;
    internal const int PartialTitleMaximumPoints = 16;
    internal const int TitleYearPoints = 30;
    internal const int MetadataYearPoints = 20;
    internal const int MatchingEditionPoints = 20;
    internal const int MainThemePoints = 20;
    internal const int ThemePoints = 15;
    internal const int OpeningPoints = 12;
    internal const int SoundtrackLabelPoints = 10;
    internal const int OfficialLabelPoints = 5;
    internal const int SoundtrackTrackPoints = 40;
    internal const int ArtistPoints = 15;
    internal const int TypicalDurationPoints = 10;
    internal const int ShortOpeningPoints = 10;
    internal const int ShortRecordingPenalty = -10;
    internal const int LongRecordingPoints = 5;
    internal const int MusicChannelPoints = 3;
    internal const int MultiSeasonCollectionPenalty = -30;
    internal const int CollectionPenalty = -20;

    internal const int SearchResultCount = 30;
    internal const int AlbumSearchResultCount = 20;
    internal const int TrackSearchResultCount = 10;
    internal const int SearchShortlistSize = 8;
    internal const int AlbumTrackShortlistSize = 4;
    internal const int ToolAttempts = 3;
    internal const int InstallerAttempts = 3;
    internal const int DownloaderRetries = 2;
    internal const int DownloaderSocketTimeoutSeconds = 20;
    internal const int DownloaderTimeoutMinutes = 2;
    internal const int RuntimeCheckTimeoutSeconds = 5;
    internal const int ThemeSourceConnectTimeoutSeconds = 3;
    internal const int ThemeSourceTimeoutSeconds = 10;
    internal const int MaximumThemeUrlLength = 2048;
    internal const int MaximumCookiesCharacters = 1_000_000;
    internal const int DownloadRetryBaseSeconds = 2;
    internal const double InternalRequestSpacingSeconds = 0.75;
    internal const int DownloadSpacingSeconds = 7;
    internal const int RateLimitCooldownMinutes = 30;
    internal const string DownloaderMaximumFileSize = "30M";

    internal const int DefaultTargetLufs = -30;
    internal const int MinimumTargetLufs = -70;
    internal const int MaximumTargetLufs = -5;
    internal const double MaximumTruePeakDbtp = -3;
    internal const int FadeSeconds = 1;
    internal const int Mp3BitrateKbps = 192;
    internal const int RawAudioMaximumBytes = 30_000_000;
    internal const int ConvertedAudioMaximumBytes = 15_000_000;
    internal const int DurationToleranceSeconds = 2;

    internal const int SourceAttempts = 2;
    internal const int ScanQueueCapacity = 256;
    internal const int ItemReadyDelaySeconds = 20;
    internal const int MetadataRetryDelaySeconds = 30;
    internal const int MetadataRetryAttempts = 3;
    internal const int ScanPriorItems = 3;
    internal const int ScanDefaultKnownSecondsPerItem = 1;
    internal const int ScanDefaultOtherSecondsPerItem = 60;
    internal const int ScanMaximumPriorSecondsPerItem = 3600;
    internal const int ScanBatchSize = 100;
    internal const int ScanRecentIssues = 50;
    internal const int ProgressComplete = 100;
    internal const int AdminPageSize = 25;
    internal const int ToolErrorMessageMaximumLength = 400;
}
