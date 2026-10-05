using System.Text.Json;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.JellyScore;

public sealed class ManagedTheme
{
    public Guid ItemId { get; init; }
    public Guid LibraryId { get; init; }
    public string Library { get; init; } = "";
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public int? Year { get; init; }
    public string Path { get; init; } = "";
    public string Folder { get; init; } = "";
    public string VideoId { get; init; } = "";
    public string? SourceUrl { get; init; }
    public string Recording { get; init; } = "";
    public string VideoTitle { get; init; } = "";
    public string Hash { get; init; } = "";
    public int Score { get; init; }
    public string Evidence { get; init; } = "";
    public DateTimeOffset Date { get; init; }
    public DateTimeOffset? AddedAt { get; init; }
}

public sealed class Store
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly State _state;

    public Store(IApplicationPaths paths)
    {
        _path = Path.Combine(paths.PluginConfigurationsPath, JellyScoreConstants.StateFile);
        _state = File.Exists(_path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(_path)) ?? new() : new();
    }

    public sealed class State
    {
        public Dictionary<Guid, ManagedTheme> Themes { get; init; } = new();
        public Dictionary<Guid, HashSet<string>> ExcludedVideos { get; init; } = new();
        public Dictionary<Guid, HashSet<string>> ExcludedRecordings { get; init; } = new();
        public HashSet<Guid> Suppressed { get; init; } = [];
        public Dictionary<Guid, string> Outcomes { get; init; } = new();
        public double? ScanKnownSecondsPerItem { get; set; }
        public double? ScanOtherSecondsPerItem { get; set; }
    }

    public T Read<T>(Func<State, T> read) { lock (_gate) return read(_state); }

    public void Change(Action<State> change)
    {
        lock (_gate)
        {
            change(_state);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state));
            File.Move(tmp, _path, true);
        }
    }
}
