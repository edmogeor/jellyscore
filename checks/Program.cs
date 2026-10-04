using Jellyfin.Plugin.JellyScore;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

var work = new Work("Dune", null, 2021, false);
const string licensed = "Provided to YouTube by Warner Records\nDune Main Theme · Hans Zimmer\nAlbum: Dune 2021 (Original Motion Picture Soundtrack)";
Video video(string id, string title, string description, int? length = 120) => new(id, title, description, "Soundtrack", length);
void check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
using (var page = new StreamReader(typeof(YouTube).Assembly.GetManifestResourceStream("Jellyfin.Plugin.JellyScore.config.html")!))
{
    var html = page.ReadToEnd();
    check(html.Contains("Scan after Jellyfin scans the media library", StringComparison.Ordinal) && !html.Contains("{{", StringComparison.Ordinal),
        "the bundled admin page has English fallbacks from the translation dictionary");
}
foreach (var locale in new[] { "da", "de", "en-us", "es", "fi", "fr", "it", "ja", "ko", "nb", "nl", "pl", "pt-br", "ru", "sv", "zh-cn" })
{
    using var stream = typeof(YouTube).Assembly.GetManifestResourceStream($"Jellyfin.Plugin.JellyScore.Strings.{locale}.json")!;
    using var strings = JsonDocument.Parse(stream);
    check(new[] { "cookiesLabel", "cookiesHelp", "cookiesGuide", "invalidCookies", "runtimeInstallFailed",
        "tvThemeSourceLabel", "tvThemeSourceHelp", "invalidTvThemeUrl",
        "scanItemStage", "stagePreparing", "stagePreparingTools",
        "stageSearching", "stageDownloading", "stageProcessing" }.All(key =>
        strings.RootElement.TryGetProperty(key, out var value) && !string.IsNullOrWhiteSpace(value.GetString())) &&
        strings.RootElement.GetProperty("rateLimited").GetString()!.Contains("{0}", StringComparison.Ordinal) &&
        strings.RootElement.GetProperty("scanItemStage").GetString()!.Contains("{1}", StringComparison.Ordinal),
        $"cookie settings and scan status are translated for {locale}");
}

var original = video("aaaaaaaaaaa", "Dune Main Theme", licensed);
foreach (var url in new[] {
    "https://www.youtube.com/watch?v=aaaaaaaaaaa", "https://youtu.be/aaaaaaaaaaa?t=10",
    "https://m.youtube.com/watch?v=aaaaaaaaaaa&list=playlist", "https://music.youtube.com/watch?v=aaaaaaaaaaa",
    "https://youtube.com/shorts/aaaaaaaaaaa", "https://www.youtube.com/embed/aaaaaaaaaaa", "https://youtube.com/live/aaaaaaaaaaa"
}) check(YouTube.VideoId(url) == "aaaaaaaaaaa", "YouTube video links resolve to a canonical video ID: " + url);
foreach (var url in new string?[] {
    null, "", "aaaaaaaaaaa", "https://example.com/theme.mp3", "file:///theme.mp3",
    "https://youtube.com.evil.example/watch?v=aaaaaaaaaaa", "https://youtube.com@evil.example/watch?v=aaaaaaaaaaa",
    "https://evil.example@youtube.com/watch?v=aaaaaaaaaaa", "https://youtube.com:8443/watch?v=aaaaaaaaaaa",
    "https://youtube.com/playlist?list=aaaaaaaaaaa", "https://youtube.com/watch?v=short",
    "https://youtu.be/aaaaaaaaaaa/extra", "https://youtube.com/watch?v=aaaaaaaaaaa&v=bbbbbbbbbbb"
}) check(YouTube.VideoId(url) is null, "custom, ambiguous, and non-video URLs cannot be edited sources: " + url);
check(Matcher.Evaluate(work, original) is { Score: > 0 }, "soundtrack theme accepted");
check(Matcher.Evaluate(work, original with { UploadDate = new DateOnly(2019, 12, 31) }) is null,
    "film uploads predating the previous calendar year are rejected");
check(Matcher.Evaluate(work, original with { UploadDate = new DateOnly(2020, 1, 1) }) is not null,
    "previous-year film promotion remains eligible");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune Part Two Main Theme", licensed)) is null, "sequel rejected");
var partTwo = new Work("Dune: Part Two", null, 2024, false);
var partTwoVideo = video("COELrJTyosw", "Dune: Part Two Soundtrack | Only I Will Remain - Hans Zimmer | WaterTower", "Only I Will Remain, from the Official Soundtrack of Dune: Part Two", 404);
check(Matcher.Evaluate(work, partTwoVideo) is null, "Part Two soundtrack is not the first Dune film");
check(Matcher.Evaluate(partTwo, partTwoVideo) is { Score: > 0 }, "Part Two soundtrack can match Part Two without a year in the title");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune Main Theme", licensed.Replace("2021", "1984"))) is null, "adaptation rejected");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune Main Theme", licensed, 481)) is null, "overlong video rejected");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune Main Theme", licensed, null)) is null, "unknown duration rejected");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme", "", 10)) is not null, "movie minimum duration included");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme", "", 9)) is null, "movie below minimum duration rejected");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme", "", 480)) is not null, "movie maximum duration included");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune Main Theme", "Dune 2021 theme")) is { Score: > 0 }, "description resolves edition");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune Main Theme", "Dune theme")) is null, "ambiguous edition rejected");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme", "")) is { Score: > 0 }, "theme identified by title without distributor metadata");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 Original Soundtrack", "")) is { Score: > 0 }, "soundtrack title accepted without theme keyword");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 OST", "")) is { Score: > 0 }, "OST title accepted");
var dream = video("M-bWFbJlwXk", "Dream of Arrakis", "", 189) with
{ Album = "Dune (Original Motion Picture Soundtrack)", Track = "Dream of Arrakis", Artist = "Hans Zimmer", ReleaseYear = 2021 };
check(YouTube.FirstTrack("DUNE Official Soundtrack\nTracklist:\n1. Dream of Arrakis\n2. Herald of the Change") == "Dream of Arrakis",
    "album tracklist supplies a generic search hint");
check(Matcher.Promising(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme", "", 120)), "flat theme is shortlisted");
var flatCandidates = Enumerable.Range(0, 18).Select(i => video(i.ToString("D11"), "Dune 2021 Main Theme", "")).ToArray();
var excludedUploads = new HashSet<string> { flatCandidates[0].Id, flatCandidates[3].Id };
check(YouTube.Shortlist(work, flatCandidates, excludedUploads, false).Select(v => v.Id).SequenceEqual(
        new[] { 1, 2, 4, 5, 6, 7, 8, 9 }.Select(i => flatCandidates[i].Id)) &&
    YouTube.Shortlist(work, flatCandidates, excludedUploads, true).Select(v => v.Id).SequenceEqual(
        Enumerable.Range(10, 8).Select(i => flatCandidates[i].Id)),
    "excluded uploads do not use slots on either shortlist page");
var shawshank = new Work("The Shawshank Redemption", null, 1994, false);
var endTitle = new Video("Q2ctsooeJBU", "End Title", "End Title · Thomas Newman The Shawshank Redemption ℗ 1994 Epic Records", "Epic Soundtrax", 246,
    Album: "The Shawshank Redemption", Track: "End Title", Artist: "Thomas Newman", ReleaseYear: 1994);
check(Matcher.Promising(shawshank, endTitle) && Matcher.Select(shawshank, [endTitle], new HashSet<string>(), new HashSet<string>(), 50)?.Video.Id == endTitle.Id,
    "named soundtrack track linked by search description reaches metadata evaluation");
check(!Matcher.Promising(shawshank, endTitle with { Description = "End Title · Thomas Newman" }),
    "a generic track title without a work link is not shortlisted");
check(YouTube.IsRateLimitError("ERROR: HTTP Error 429: Too Many Requests") &&
    YouTube.IsRateLimitError("Sign in to confirm you're not a bot") &&
    YouTube.IsRateLimitError("This content isn't available, try again later") &&
    !YouTube.IsRateLimitError("HTTP Error 403: Forbidden") &&
    !YouTube.IsRateLimitError("Video unavailable"),
    "YouTube challenges pause requests without treating unrelated failures as rate limits");
check(YouTube.ValidCookies("# Netscape HTTP Cookie File\n.youtube.com\tTRUE\t/\tTRUE\t2147483647\tVISITOR_INFO1_LIVE\ttest\n") &&
    !YouTube.ValidCookies("VISITOR_INFO1_LIVE=test") && !YouTube.ValidCookies("# HTTP Cookie File\n\0"),
    "only bounded Netscape cookie files can be saved");
const string tvTemplate = "https://example.com/themes/{tvdbId}.mp3";
check(TvThemeSource.ValidTemplate(tvTemplate) &&
    TvThemeSource.Url(tvTemplate, "73244")?.AbsoluteUri == "https://example.com/themes/73244.mp3" &&
    TvThemeSource.Url(tvTemplate, "not-an-id") is null &&
    !TvThemeSource.ValidTemplate("http://example.com/{tvdbId}.mp3") &&
    !TvThemeSource.ValidTemplate("https://127.0.0.1/{tvdbId}.mp3") &&
    !TvThemeSource.ValidTemplate("https://example.com/?id={tvdbId}") &&
    !TvThemeSource.ValidTemplate("https://example.com/theme.mp3"),
    "TV theme templates require public HTTPS URLs with one numeric TVDB ID in the path");
check(YouTube.DenoAsset(false, false, Architecture.X64) == "deno-x86_64-unknown-linux-gnu.zip" &&
    YouTube.DenoAsset(true, false, Architecture.Arm64) == "deno-aarch64-pc-windows-msvc.zip" &&
    YouTube.DenoAsset(false, false, Architecture.X64, musl: true) is null,
    "the bundled Deno fallback is only offered on supported platforms");
(string? Id, string? Title, int? Year) knownFilm = ("123", shawshank.Title, 1994);
(string? Id, string? Title, int? Year) otherFilm = ("456", "Another Film", 2020);
check(Matcher.NoCompetingEdition(shawshank, "123", [knownFilm, otherFilm], []),
    "unrelated search results do not make an identified film ambiguous");
check(!Matcher.NoCompetingEdition(shawshank, "123", [knownFilm, ("456", shawshank.Title, 2020)], []),
    "a same-title remake keeps the release-year requirement");
check(!Matcher.NoCompetingEdition(shawshank, "123", [knownFilm], [("789", shawshank.Title, 1994)]) &&
    !Matcher.NoCompetingEdition(shawshank, "123", [knownFilm], Enumerable.Repeat<(string?, string?, int?)>(("789", "Other Show", 2020), 20).ToArray()),
    "same-title TV edition or truncated TV results keep the release-year requirement");
check(!Matcher.NoCompetingEdition(shawshank, "123", [otherFilm], []) &&
    !Matcher.NoCompetingEdition(shawshank, "123", [], []) &&
    !Matcher.NoCompetingEdition(shawshank, "123", Enumerable.Repeat(knownFilm, 20).ToArray(), []),
    "missing target, failed search, and truncated pages cannot establish an unambiguous edition");
var yearlessTheme = video("bbbbbbbbbbb", "The Shawshank Redemption Main Theme", "");
check(Matcher.Evaluate(shawshank, yearlessTheme) is null &&
    Matcher.Evaluate(shawshank with { NoCompetingEdition = true }, yearlessTheme) is not null,
    "catalog evidence permits a yearless theme linked in its title");
check(Matcher.Select(shawshank, [endTitle, yearlessTheme], new HashSet<string>(), new HashSet<string>(), 50)?.Video.Id == endTitle.Id &&
    Matcher.Select(shawshank with { NoCompetingEdition = true }, [endTitle, yearlessTheme], new HashSet<string>(), new HashSet<string>(), 50)?.Video.Id == yearlessTheme.Id,
    "edition lookup can promote a yearless main theme over a soundtrack fallback");
check(Matcher.Evaluate(shawshank with { NoCompetingEdition = true }, video("bbbbbbbbbbb", "Main Theme", shawshank.Title)) is null &&
    Matcher.Evaluate(shawshank with { NoCompetingEdition = true }, yearlessTheme with { Title = "The Shawshank Redemption 2026 Main Theme" }) is null,
    "catalog evidence never replaces a work link or overrides a conflicting year");
check(!Matcher.Promising(work, video("bbbbbbbbbbb", "Dune 2021 scene", "", 120)), "scene clip avoids full metadata fetch");
check(!Matcher.Promising(work, video("bbbbbbbbbbb", "Dune 2021 Full Album", "", 4460)), "full album is not shortlisted for download");
check(Matcher.Select(work, [dream], new HashSet<string>(), new HashSet<string>())?.Video.Id == dream.Id,
    "single soundtrack track is eligible without theme in its title");
var soundtrackUpload = video("Phf-AC28SCY", "Dream of Arrakis | Dune OST",
    "Music from Dune (2021) distributed by Warner Bros.\nDune (Original Motion Picture Soundtrack) by Hans Zimmer.", 190);
check(Matcher.Evaluate(work, soundtrackUpload) is { Score: > 0 }, "description explicitly links film and year without structured album metadata");
check(Matcher.Evaluate(new Work("Dune", null, 1984, false), soundtrackUpload) is null, "description year rejects wrong Dune adaptation");
check(Matcher.Evaluate(new Work("Dune", null, 1984, false), dream) is null, "soundtrack release year rejects wrong adaptation");
check(Matcher.Evaluate(work, dream with { Album = null }) is null, "named soundtrack track still needs a work link");
var closing = video("bbbbbbbbbbb", "Dune 2021 Official Closing Credits Original Soundtrack", "");
check(Matcher.Select(work, [closing], new HashSet<string>(), new HashSet<string>())?.Video.Id == closing.Id,
    "closing credits are eligible when no main theme exists");
check(Matcher.Select(work, [closing, video("ccccccccccc", "Dune 2021 Main Theme", "")], new HashSet<string>(), new HashSet<string>())?.Video.Id == "ccccccccccc",
    "main theme wins over closing credits regardless of ranking signals");
check(Matcher.Select(work, [dream, video("ccccccccccc", "Dune 2021 Main Theme", "")], new HashSet<string>(), new HashSet<string>())?.Video.Id == "ccccccccccc",
    "main theme wins over soundtrack track fallback");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 1984 Main Theme", "")) is null, "wrong edition without metadata rejected");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme cover", "")) is null, "cover without metadata rejected");
var livePerformance = video("bbbbbbbbbbb", "Hans Zimmer Performs the Dune Soundtrack LIVE", "Dune 2021 soundtrack", 189);
check(!Matcher.Promising(work, livePerformance) && Matcher.Evaluate(work, livePerformance) is null,
    "live performances cannot be shortlisted or selected");
check(!Matcher.Promising(work, video("bbbbbbbbbbb", "Dune 2021 Main Theme (Live)", "")),
    "parenthesized live recording labels are excluded");
var theyLive = new Work("They Live", null, 1988, false);
var theyLiveTheme = video("bbbbbbbbbbb", "They Live 1988 Main Theme", "");
check(Matcher.Promising(theyLive, theyLiveTheme) && Matcher.Evaluate(theyLive, theyLiveTheme) is not null,
    "live in the work title is not a live-performance label");
check(Matcher.Evaluate(work, video("bbbbbbbbbbb", "Top 10 Dune 2021 Themes", "")) is null, "ranking video rejected");
check(Matcher.Select(work, [original, video("bbbbbbbbbbb", "Dune Main Theme", licensed.Replace("Hans Zimmer", "Other Artist"))],
    new HashSet<string>(), new HashSet<string>())?.Video.Id == original.Id, "first equally ranked recording selected");
var chosen = Matcher.Evaluate(work, original)!;
check(Matcher.Select(work, [original], new HashSet<string>(), new HashSet<string> { chosen.Recording }) is null, "refresh excludes installed recording");
check(Matcher.Select(work, [original, original with { Id = "bbbbbbbbbbb" }], new HashSet<string>(), new HashSet<string>()) is not null,
    "duplicate uploads of one recording do not create an ambiguous tie");
check(Matcher.Select(work, [original, original with { Id = "bbbbbbbbbbb" }], new HashSet<string> { original.Id }, new HashSet<string>())?.Video.Id == "bbbbbbbbbbb",
    "excluding one upload leaves another source of the same recording");
check(Matcher.RejectionReason(work, [original], new HashSet<string>(), new HashSet<string> { chosen.Recording }) ==
    "Only previously used recordings were found", "excluded recording has distinct reason");
check(Matcher.RejectionReason(work, [video("bbbbbbbbbbb", "Dune 1984 Main Theme", "")], new HashSet<string>(), new HashSet<string>()).Contains("Different release year"),
    "rejected edition reports why");
Matcher.RejectionReason(work, [video("bbbbbbbbbbb", "Dune 1984 Main Theme", "")], new HashSet<string>(), new HashSet<string>(), out var rejectionCode);
check(rejectionCode == "reasonEdition", "rejection also has a localized UI code");
var oak = new Work("The End of Oak Street", null, 2026, false);
var tutorial = new Video("GneFZPhfN7o", "The End of Oak Street – Main Theme | Piano Tutorial (Synthesia)",
    "Learn how to play the Main Theme from The End of Oak Street (2026) on piano with this Synthesia tutorial.", "Noud van Harskamp", 123);
check(!Matcher.Promising(oak, tutorial), "piano tutorial is not shortlisted");
check(Matcher.Evaluate(oak, tutorial) is null, "piano tutorial cannot be downloaded even when full metadata is available");
Video oakTrack(string id, string title, int seconds) => new(id, title, "Composed by Michael Giacchino.", "OfficialMovieSoundtrack", seconds);
var endOfOakSuite = oakTrack("48Jx-37AFVY", "27. The End of Oak Suite (The End of Oak Street Soundtrack)", 286);
var mainOnEndOfDays = oakTrack("KzScQcXFImg", "26. Main on End of Days (The End of Oak Street Soundtrack)", 105);
check(Matcher.Promising(oak, endOfOakSuite) && Matcher.Promising(oak, mainOnEndOfDays),
    "End of Oak Street soundtrack tracks pass the search shortlist");
check(Matcher.Evaluate(oak, mainOnEndOfDays)?.Score == Matcher.Evaluate(oak, endOfOakSuite)?.Score,
    "End of Oak Street soundtrack recordings tie on ranking evidence");
check(Matcher.Select(oak, [endOfOakSuite, mainOnEndOfDays], new HashSet<string>(), new HashSet<string>())?.Video.Id == endOfOakSuite.Id,
    "End of Oak Street tie selects the first eligible recording");
check(Matcher.Select(oak, [endOfOakSuite, mainOnEndOfDays], new HashSet<string> { endOfOakSuite.Id }, new HashSet<string>())?.Video.Id == mainOnEndOfDays.Id,
    "excluded source leaves the other soundtrack recording eligible");
var harry = new Work("Harry Potter and the Sorcerer's Stone", null, 2001, false);
var franchise = new Work("Harry Potter", null, null, false, Franchise: true,
    Installments: ["Harry Potter and the Sorcerer's Stone", "Harry Potter and the Chamber of Secrets"]);
var sharedTheme = video("fffffffffff", "Harry Potter Main Theme", "", 150);
check(Matcher.Promising(franchise, sharedTheme) && Matcher.Select(franchise, [sharedTheme], new HashSet<string>(), new HashSet<string>(), 50)?.Video.Id == sharedTheme.Id,
    "a TMDb franchise can match a yearless shared theme independently of a film title");
check(Matcher.Evaluate(harry, sharedTheme) is null,
    "a yearless shared franchise theme does not weaken film-specific matching");
check(Matcher.Evaluate(franchise, video("ggggggggggg", "Harry Potter and the Chamber of Secrets Main Theme", "")) is null &&
    Matcher.Evaluate(franchise, video("hhhhhhhhhhh", "Harry Potter Main Theme", "Album: Harry Potter and the Sorcerer's Stone")) is null &&
    Matcher.Evaluate(franchise, video("iiiiiiiiiii", "Harry Potter 2002 Main Theme", "")) is null &&
    Matcher.Evaluate(franchise, video("jjjjjjjjjjj", "Harry Potter and the Goblet of Fire Main Theme", "")) is null,
    "an installment-specific upload is not a shared franchise theme");
check(Matcher.Select(franchise, [sharedTheme], new HashSet<string>(), new HashSet<string>(), 100) is null,
    "franchise preference never bypasses the minimum match strength");
var duneFranchise = new Work("Dune", null, null, false, Franchise: true, Installments: ["Dune"]);
check(Matcher.Evaluate(duneFranchise, video("kkkkkkkkkkk", "Dune Main Theme", "")) is null &&
    Matcher.Evaluate(duneFranchise, video("lllllllllll", "Dune Collection Main Theme", "")) is not null,
    "a franchise sharing a film title requires explicit collection evidence");
var named = video("wtHra9tFISY", "Hedwig's Theme", "Provided to YouTube by Atlantic Records\n\nHedwig's Theme · John Williams\n\nHarry Potter and The Sorcerer's Stone Original Motion Picture Soundtrack\n\n℗ 2001 Warner Records Inc.", 309);
check(Matcher.Promising(harry, named), "named themes remain in the cheap shortlist");
check(Matcher.Evaluate(harry, named) is { Score: > 0 }, "named theme linked via soundtrack album");
var alternate = video("bbbbbbbbbbb", "Harry Potter and the Sorcerer's Stone 2001 Main Theme", "");
check(Matcher.Select(harry, [alternate, named],
    new HashSet<string>(), new HashSet<string>())?.Video.Id == named.Id, "unique higher-scoring soundtrack beats title-only candidate");
check(Matcher.Select(harry, [alternate, named], new HashSet<string> { named.Id }, new HashSet<string>())?.Video.Id == alternate.Id,
    "failed source can fall back to a different eligible recording");
check(Matcher.Evaluate(harry, named with { Description = named.Description.Replace("Provided to YouTube by Atlantic Records\n\n", "") }) is { Score: > 0 },
    "named theme linked via soundtrack album without distributor metadata");
check(Matcher.Evaluate(harry, video("ccccccccccc", "Hedwig's Theme", "Theme from Harry Potter and the Sorcerer's Stone", 309)) is { Score: > 0 },
    "named theme linked by description without album or artist");
check(Matcher.Evaluate(harry, video("ccccccccccc", "Hedwig's Theme", "", 309)) is null,
    "search term alone does not establish which film a named track belongs to");
var office = new Work("The Office (US)", null, 2005, true);
var supernatural = new Work("Supernatural", null, 2005, true);
var knownShow = (Id: (string?)"123", Title: (string?)"Supernatural", Year: (int?)2005);
check(Matcher.NoCompetingEdition(supernatural, "123", [], [knownShow]) &&
    !Matcher.NoCompetingEdition(supernatural, "123", [("456", "Supernatural", 2020)], [knownShow]) &&
    !Matcher.NoCompetingEdition(supernatural, "123", [], [knownShow, ("789", "Supernatural", 2021)]) &&
    !Matcher.NoCompetingEdition(supernatural, "123", [], [("789", "Supernatural", 2005)]) &&
    !Matcher.NoCompetingEdition(supernatural, "123", [], Enumerable.Repeat(knownShow, 20).ToArray()),
    "yearless short TV titles need a unique, complete catalog match across films and shows");
check(Matcher.Evaluate(supernatural, video("bbbbbbbbbbb", "Supernatural Theme Song", "")) is null &&
    Matcher.Evaluate(supernatural with { NoCompetingEdition = true }, video("bbbbbbbbbbb", "Supernatural Theme Song", "")) is not null &&
    Matcher.Evaluate(supernatural with { NoCompetingEdition = true }, video("bbbbbbbbbbb", "Supernatural 2024 Theme Song", "")) is null,
    "catalog verification permits missing years but never conflicting years for TV");
check(Matcher.Evaluate(supernatural, video("bbbbbbbbbbb", "Supernatural 2005 Theme Song", ""))!.Score >
    Matcher.Evaluate(supernatural with { NoCompetingEdition = true }, video("bbbbbbbbbbb", "Supernatural Theme Song", ""))!.Score,
    "a matching TV year ranks higher than a verified yearless upload");
supernatural = supernatural with { NoCompetingEdition = true };
var deathScene = video("5EcsBgxXDqc", "Death's Intro... Supernatural S5E21", "", 120);
var supernaturalOpening = video("bbbbbbbbbbb", "Supernatural S5E21 Opening Theme", "", 20);
var titleCardMontage = new Video("wg0yCihdKio", "Supernatural Seasons 1-15 Main Title Cards", "Property of Warner Bros and the CW\nI don't own anything", "Tye Judy", 87,
    UploadDate: new DateOnly(2019, 10, 11));
check(Matcher.MatchStrength(-10) == 0 && Matcher.MatchStrength(150) == 100,
    "displayed match strength stays within 0-100");
check(Matcher.MatchStrength(Matcher.Evaluate(supernatural, titleCardMontage)!.Score) == 42,
    "multi-season collection has a bounded match strength of 42");
check(Matcher.Select(supernatural, [titleCardMontage], new HashSet<string>(), new HashSet<string>(), 50) is null,
    "a higher minimum skips the weak collection even when it is the only source");
Matcher.RejectionReason(supernatural, [titleCardMontage], new HashSet<string>(), new HashSet<string>(), out var strengthCode, 50);
check(strengthCode == "reasonBelowStrength", "below-minimum results have their own admin reason");
var waywardSon = new Video("DJcX6Tpv9RI", "Carry on Wayward Son - Kansas (Supernatural Main Theme)",
    "Carry On Wayward Son by Kansas, officially released in December 1976, and now the main theme of TV-show Supernatural", "Anna Lovén", 321,
    UploadDate: new DateOnly(2014, 11, 27));
var genericSupernaturalTheme = new Video("FtYRMGnj-_A", "Supernatural Theme Song With Lyrics", "Supernatural Theme Song With Lyrics", "Theme Lyric", 316,
    UploadDate: new DateOnly(2012, 6, 16));
check(Matcher.Promising(supernatural, titleCardMontage) && Matcher.Evaluate(supernatural, titleCardMontage) is { Score: > 0 },
    "multi-season title-card montages remain eligible as a fallback");
check(Matcher.Select(supernatural, [titleCardMontage], new HashSet<string>(), new HashSet<string>())?.Video.Id == titleCardMontage.Id,
    "the title-card collection can still be selected when it is the only candidate");
check(Matcher.Evaluate(supernatural, video("ccccccccccc", "Supernatural Season 15 Main Title", "", 20)) is not null,
    "one season's short main title is not mistaken for a compilation");
check(Matcher.Evaluate(supernatural, waywardSon) is not null,
    "named music explicitly identified as the TV show's theme remains eligible");
check(Matcher.Evaluate(supernatural, waywardSon with { Description = "Carry On Wayward Son by Kansas" }) is null,
    "a named recording still needs evidence that it belongs to the series");
check(Matcher.Select(supernatural, [titleCardMontage, genericSupernaturalTheme, waywardSon], new HashSet<string>(), new HashSet<string>())?.Video.Id == waywardSon.Id,
    "named series song beats a title-card montage and generic theme upload");
check(Matcher.Select(supernatural, [titleCardMontage, genericSupernaturalTheme, waywardSon], new HashSet<string>(), new HashSet<string>(), 60)?.Video.Id == waywardSon.Id,
    "thresholds filter before ranking series themes");
check(Matcher.Select(supernatural, [titleCardMontage, genericSupernaturalTheme], new HashSet<string>(), new HashSet<string>())?.Video.Id == genericSupernaturalTheme.Id,
    "a standalone theme upload beats a multi-season title-card collection");
check(!Matcher.Promising(supernatural, deathScene) && Matcher.Evaluate(supernatural, deathScene) is null,
    "episode character intro is neither shortlisted nor eligible as the series theme");
check(Matcher.Promising(supernatural, supernaturalOpening) && Matcher.Evaluate(supernatural, supernaturalOpening) is not null,
    "episode number alone does not exclude a genuine series opening");
check(Matcher.Evaluate(supernatural, video("ccccccccccc", "Supernatural Opening Scene", "", 120)) is null,
    "opening scene is not theme music");
check(Matcher.Select(supernatural, [deathScene, supernaturalOpening], new HashSet<string>(), new HashSet<string>())?.Video.Id == supernaturalOpening.Id,
    "series opening wins instead of episode scene");
var plainIntro = video("ddddddddddd", "Supernatural Intro", "", 70);
var explicitTheme = video("eeeeeeeeeee", "Supernatural Theme Song", "", 70);
check(Matcher.Select(supernatural, [plainIntro, explicitTheme], new HashSet<string>(), new HashSet<string>())?.Video.Id == explicitTheme.Id,
    "explicit series theme scores above a bare intro");
check(Matcher.Evaluate(supernatural, explicitTheme with { Title = "Supernatural Original Soundtrack OST Official Theme Song" })!.Score -
    Matcher.Evaluate(supernatural, explicitTheme)!.Score == 15,
    "weak soundtrack labels count once and official claims contribute only a small bonus");
check(Matcher.Evaluate(office, video("bbbbbbbbbbb", "The Office (US) Opening Credits", "", 70)) is null &&
    Matcher.Evaluate(office with { NoCompetingEdition = true }, video("bbbbbbbbbbb", "The Office (US) Opening Credits", "", 70)) is { Score: > 0 },
    "series with a regional qualifier need catalog verification when the premiere year is missing");
office = office with { NoCompetingEdition = true };
check(Matcher.Evaluate(office, video("bbbbbbbbbbb", "The Office UK Opening Credits", "", 70)) is null,
    "different regional version rejected");
check(Matcher.Evaluate(office, video("bbbbbbbbbbb", "The Office (US) Opening Credits", "", 10)) is not null,
    "series minimum duration included");
check(Matcher.Promising(office, video("bbbbbbbbbbb", "The Office (US) Opening Credits", "", 480)) &&
    Matcher.Evaluate(office, video("bbbbbbbbbbb", "The Office (US) Opening Credits", "", 480)) is not null,
    "series shares the movie maximum duration");
check(Matcher.Evaluate(office, video("bbbbbbbbbbb", "The Office (US) Opening Credits", "", 481)) is null,
    "series above maximum duration rejected");
var vampire = new Work("Interview with the Vampire", null, 2022, true);
var vampireOpening = new Video("JPeuE8uh9FY", "Interview with the Vampire (1 season) | 2022 | Opening", "", "Илья Якуба", 22, ReleaseYear: 2022);
var vampireSoundtrack = new Video("NWTRlUYij6M", "Come to Me | Interview with the Vampire (Original Television Series Soundtrack)",
    "Music video by Daniel Hart performing Come to Me. (C) 2022 AMC Film Holdings LLC", "SonySoundtracksVEVO", 159);
var vampireFilm = new Work("Interview with the Vampire", null, 1994, false);
var filmSoundtrack = video("bbbbbbbbbbb", "Interview with the Vampire Theme", "Album: Interview with the Vampire (Original Motion Picture Soundtrack)");
check(Matcher.Evaluate(vampire, filmSoundtrack) is null, "1994 film soundtrack album cannot qualify for the TV series without a year");
check(Matcher.Evaluate(vampire, video("bbbbbbbbbbb", "Interview with the Vampire 2022 Official Main Theme Original Motion Picture Soundtrack", "")) is null,
    "a matching year and high-scoring words cannot override a conflicting film edition");
check(Matcher.Evaluate(vampireFilm, filmSoundtrack) is not null, "film soundtrack remains eligible for the film");
check(Matcher.Evaluate(vampireFilm, vampireSoundtrack) is null, "TV soundtrack cannot qualify for the film without a year");
check(Matcher.Evaluate(vampire, vampireSoundtrack) is not null, "TV soundtrack remains eligible for the series");
var genericVampireTheme = new Video("bbbbbbbbbbb", "Interview with the Vampire Official Original Soundtrack OST Main Theme", "", "Music Channel", 159);
check(Matcher.Evaluate(vampire, genericVampireTheme) is not null && Matcher.Evaluate(vampire, vampireOpening)!.Score > Matcher.Evaluate(vampire, genericVampireTheme)!.Score,
    "the verified series opening ranks above an eligible title padded with promotional words");
check(Matcher.Select(vampire, [genericVampireTheme, vampireOpening], new HashSet<string>(), new HashSet<string>())?.Video.Id == vampireOpening.Id,
    "an edition-specific short opening beats a longer generic theme with stacked promotional words");
check(Matcher.Evaluate(vampire, video("bbbbbbbbbbb", "Interview with the Vampire 2022 Opening", "My first movie edit")) is not null,
    "an incidental movie mention in a description does not override the TV edition");
var namedTvTheme = video("bbbbbbbbbbb", "Come to Me Theme | Interview with the Vampire",
    "Music from Interview with the Vampire 2022 Original Television Series Soundtrack");
check(Matcher.Evaluate(vampire, namedTvTheme) is not null,
    "a named track linked to the correct TV edition in its description remains eligible");
check(Matcher.Evaluate(vampire, namedTvTheme with { Description = "Music from Interview with the Vampire" }) is null,
    "a named theme without edition evidence remains uncertain");
var vampireFanEdit = new Video("vldqvBpuACY", "Interview With The Vampire Requiem For A Dream Theme Song",
    "Interview With The Vampire Requiem For A Dream Theme. My first movie :). I put Lestat at the end not because I don't like him, but because I wanted him to appear when the powerful music starts.", "summerrainnnn", 364,
    UploadDate: new DateOnly(2010, 2, 27));
check(Matcher.Evaluate(vampire, vampireFanEdit with { UploadDate = null }) is null,
    "another work's named theme cannot pass as the TV series theme even without an upload date");
check(Matcher.Evaluate(vampire, vampireFanEdit) is null, "the 2010 fan edit cannot be the 2022 TV series theme");
check(Matcher.Evaluate(vampire, video("bbbbbbbbbbb", "Interview with the Vampire Theme Song", "") with { UploadDate = new DateOnly(2010, 2, 27) }) is null,
    "an otherwise plausible upload from before the TV series was made is rejected");
check(Matcher.Evaluate(vampire, video("bbbbbbbbbbb", "Interview with the Vampire Theme Song", "") with { UploadDate = new DateOnly(2020, 12, 31) }) is null,
    "uploads older than the previous calendar year are too early");
check(Matcher.Evaluate(vampire, video("bbbbbbbbbbb", "Interview with the Vampire Theme Song", "") with { UploadDate = new DateOnly(2021, 1, 1) }) is not null,
    "previous-year promotional uploads remain eligible without a precise premiere date");
check(Matcher.Evaluate(vampire, video("bbbbbbbbbbb", "Interview with the Vampire Theme Song", "")) is not null,
    "missing upload dates are not treated as negative evidence");
check(Matcher.Select(vampire, [vampireFanEdit, vampireOpening], new HashSet<string>(), new HashSet<string>())?.Video.Id == vampireOpening.Id,
    "TV opening wins when a high-scoring fan edit borrows music from another work");
check(Matcher.Evaluate(new Work("Breaking Bad", null, 2008, true), video("bbbbbbbbbbb", "Breaking Bad Better Call Saul Theme Song", "")) is null,
    "other named themes are rejected independently of the series title");
check(Matcher.Evaluate(supernatural, explicitTheme) is not null, "a plain series theme without another named work remains eligible");
check(Matcher.Promising(vampire, vampireOpening), "the short 2022 opening reaches full metadata evaluation");
check(Matcher.Select(vampire, [vampireSoundtrack, vampireOpening], new HashSet<string>(), new HashSet<string>())?.Video.Id == vampireOpening.Id,
    "series opening wins over a soundtrack track");
var reliableSoundtrack = vampireSoundtrack with { Album = "Interview with the Vampire (Original Television Series Soundtrack)", Track = "Come to Me", Artist = "Daniel Hart", ReleaseYear = 2022 };
check(Matcher.Select(vampire, [vampireOpening, reliableSoundtrack], new HashSet<string>(), new HashSet<string>(), 90)?.Video.Id == reliableSoundtrack.Id,
    "a strict minimum can fall back to a verified soundtrack when a short opening is below the threshold");
check(Matcher.Select(vampire, [vampireSoundtrack], new HashSet<string>(), new HashSet<string>())?.Video.Id == vampireSoundtrack.Id,
    "soundtrack track remains a fallback when no opening is found");
check(Matcher.Evaluate(vampire, video("7jLOWfP3Lmc", "Interview with the Vampire - Opening",
    "A clip from Interview with the Vampire (1994) of the opening scene.", 164)) is null,
    "1994 film opening is not eligible for the 2022 series");
check(YouTube.FirstTrack("No tracklist here") is null, "album without a tracklist has no search hint");
check(YouTube.FirstTrack("Tracklist:\n1) First Track\n2) Next Track") == "First Track", "parenthesized track number parsed");
check(YouTube.DownloaderName(false, false, Architecture.X64) == "yt-dlp_linux", "Linux x64 binary");
check(YouTube.DownloaderName(false, false, Architecture.Arm64, true) == "yt-dlp_musllinux_aarch64", "Alpine arm64 binary");
check(YouTube.DownloaderName(true, false, Architecture.Arm64) == "yt-dlp_arm64.exe", "Windows arm64 binary");
check(YouTube.DownloaderName(false, true, Architecture.Arm64) == "yt-dlp_macos", "macOS universal binary");
check(Audio.FixedGain(-30, -9, -26) == 4, "fixed gain brings a quiet track to the default target");
check(Audio.FixedGain(-30, -1, -26) == -2, "true peak caps gain even when average loudness stays below target");
check(Audio.FixedGain(-30, -9, -20) == 6, "chosen volume changes gain while peak headroom still wins");
var measured = Audio.Stats("Integrated loudness:\n    I:         -19.3 LUFS\n    Threshold: -29.5 LUFS\nTrue peak:\n    Peak:       -3.1 dBFS");
check(measured.Loudness == -19.3 && Math.Abs(measured.TruePeak - -3.0) < 0.0001 &&
    Math.Abs(Audio.FixedGain(measured.Loudness, measured.TruePeak, -26) - -6.7) < 0.0001 &&
    Audio.FixedGain(-30, measured.TruePeak, -26) == 0,
    "fast EBU R128 analysis measures loudness and conservatively caps true peak");
check(Audio.Filter(4, 10) == "volume=4dB,afade=t=in:d=1,afade=t=out:st=9:d=1", "short track fades at both ends");
check(Audio.Filter(-2, 120) == "volume=-2dB,afade=t=in:d=1,afade=t=out:st=119:d=1", "fade-out follows actual track duration");
var ramp = new[] { -55, -45, -39, -34, -31, -28, -26, -24, -22, -21 };
string frames(IEnumerable<int> values) => string.Join('\n', values.Select(v => $"lavfi.astats.Overall.RMS_level={v}"));
check(Audio.ExistingFades(frames(ramp.Concat(Enumerable.Repeat(-21, 20)).Concat(ramp.Reverse()))) == (true, true),
    "existing fades at both ends are detected");
check(Audio.ExistingFades(frames(Enumerable.Repeat(-21, 40))) == (false, false) &&
    Audio.ExistingFades(frames(new[] { -100, -21, -21, -21, -21, -21, -21, -21, -21, -21 }.Concat(Enumerable.Repeat(-21, 30)))) == (false, false),
    "steady audio and a silent lead-in still receive fades");
check(Audio.Filter(4, 10, false, true) == "volume=4dB,afade=t=out:st=9:d=1" &&
    Audio.Filter(4, 10, true, false) == "volume=4dB,afade=t=in:d=1" &&
    Audio.Filter(4, 10, false, false) == "volume=4dB", "only missing fades are applied");
var scanEstimate = new ScanStatus { Running = true, Total = 18, KnownTotal = 9, LastCompletedAt = DateTimeOffset.UtcNow };
check(scanEstimate.RemainingSeconds is > 548 and < 550, "a first scan estimates ownership checks and searches separately using defaults");
scanEstimate.KnownProcessed = 3;
scanEstimate.KnownSeconds = 6;
scanEstimate.Processed = 3;
check(scanEstimate.RemainingSeconds is > 551 and < 553, "observed ownership timings do not replace the default for unsearched items");
scanEstimate.Processed = 4;
scanEstimate.OtherSeconds = 30;
check(scanEstimate.RemainingSeconds is > 411 and < 413, "initial search samples gradually replace the first-run default");
scanEstimate.Processed = 6;
scanEstimate.OtherSeconds = 90;
check(scanEstimate.RemainingSeconds is > 191 and < 193, "remaining known themes and searches use their own observed durations");
scanEstimate.CurrentKnown = true;
scanEstimate.LastCompletedAt = DateTimeOffset.UtcNow.AddSeconds(-12);
check(scanEstimate.RemainingSeconds is > 201 and < 203, "an overdue item adds its excess time only once");
scanEstimate.Running = false;
check(scanEstimate.RemainingSeconds is null, "completed scans do not show an ETA");
var priorEstimate = new ScanStatus { Running = true, Total = 2, KnownTotal = 1, LastCompletedAt = DateTimeOffset.UtcNow,
    PriorKnownSecondsPerItem = 2, PriorOtherSecondsPerItem = 30 };
check(priorEstimate.RemainingSeconds is > 31 and < 33, "saved timings take precedence over first-run defaults");
check(new ScanStatus { Running = true }.RemainingSeconds is null, "empty scans have no ETA");
check(!File.Exists("dist/JellyScore.zip") || System.IO.Compression.ZipFile.OpenRead("dist/JellyScore.zip").Entries.Select(e => e.Name).Order().SequenceEqual(
    new[] { "Jellyfin.Plugin.JellyScore.dll", "SHA2-256SUMS", "deno-checksums", "deno-version", "yt-dlp-version" }.Order()),
    "plugin archive contains only the DLL and pinned release metadata");
var folder = Path.Combine(Path.GetTempPath(), "theme-songs-checks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    async Task<double[]> levels(string file)
    {
        var output = await Audio.Run("ffmpeg", ["-hide_banner", "-nostats", "-i", file, "-af",
            "aresample=48000,asetnsamples=n=4800:p=0,astats=metadata=1:reset=1,ametadata=print:key=lavfi.astats.Overall.RMS_level", "-f", "null", "-"], CancellationToken.None);
        return Regex.Matches(output, @"lavfi\.astats\.Overall\.RMS_level=(-?\d+(?:\.\d+)?)")
            .Select(match => double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
    }
    foreach (var (fadeIn, fadeOut) in new[] { (false, false), (true, false), (false, true), (true, true) })
    {
        var name = $"{fadeIn}-{fadeOut}";
        var source = Path.Combine(folder, name + ".wav");
        var destination = Path.Combine(folder, name + ".mp3");
        var filter = Audio.Filter(0, 12, fadeIn, fadeOut);
        await Audio.Run("ffmpeg", ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "sine=frequency=440:duration=12",
            "-af", filter, "-c:a", "pcm_s16le", source], CancellationToken.None);
        await Audio.ConvertAudio(source, destination, "ffmpeg", 12, -26, CancellationToken.None);
        var before = await levels(source);
        var after = await levels(destination);
        check(before.Length >= 100 && after.Length >= 100, "FFmpeg produced measurable audio windows");
        var beforeIn = before[8] - before[0];
        var afterIn = after[8] - after[0];
        var beforeOut = before[^9] - before[^1];
        var afterOut = after[^9] - after[^1];
        check(fadeIn ? Math.Abs(afterIn - beforeIn) < 3 : afterIn > 15,
            $"{name}: existing fade-in preserved or missing fade-in added");
        check(fadeOut ? Math.Abs(afterOut - beforeOut) < 3 : afterOut > 15,
            $"{name}: existing fade-out preserved or missing fade-out added");
    }
    var binary = Path.Combine(folder, "yt-dlp", "test-binary");
    var bytes = System.Text.Encoding.UTF8.GetBytes("verified downloader");
    var hash = Convert.ToHexString(SHA256.HashData(bytes));
    var downloads = 0;
    Task<Stream> fetch(CancellationToken _) { downloads++; return Task.FromResult<Stream>(new MemoryStream(bytes)); }
    check(await YouTube.EnsureDownloader(binary, hash, fetch, CancellationToken.None) == binary && downloads == 1,
        "first use downloads the selected binary");
    check(await YouTube.EnsureDownloader(binary, hash, fetch, CancellationToken.None) == binary && downloads == 1,
        "valid cached binary avoids another download");
    File.WriteAllText(binary, "corrupt");
    var mismatches = 0;
    Task<Stream> wrongChecksum(CancellationToken _) { mismatches++; return Task.FromResult<Stream>(new MemoryStream([1, 2, 3])); }
    try { await YouTube.EnsureDownloader(binary, hash, wrongChecksum, CancellationToken.None); }
    catch (IOException) { }
    check(File.ReadAllText(binary) == "corrupt" && mismatches == 1 && Directory.GetFiles(Path.GetDirectoryName(binary)!, "*.tmp").Length == 0,
        "failed verification does not retry, install an executable, or leave temporary files");
    check(await YouTube.EnsureDownloader(binary, hash, fetch, CancellationToken.None) == binary && downloads == 2 && File.ReadAllBytes(binary).SequenceEqual(bytes),
        "corrupt cached executable is replaced by a verified copy");
    var transient = Path.Combine(folder, "yt-dlp", "transient-binary");
    var attempts = 0;
    Task<Stream> flaky(CancellationToken token)
    {
        if (++attempts < 3) throw new HttpRequestException("Temporary download failure");
        return fetch(token);
    }
    check(await YouTube.EnsureDownloader(transient, hash, flaky, CancellationToken.None) == transient && attempts == 3 &&
        Directory.GetFiles(Path.GetDirectoryName(transient)!, "*.tmp").Length == 0,
        "transient installer failures retry and leave no temporary files");
    var path = Path.Combine(folder, "theme.mp3");
    File.WriteAllText(path, "plugin theme");
    var managed = new ManagedTheme { Folder = folder, Path = path, Hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) };
    check(ThemeService.Status(managed) == "Active", "unchanged theme is active");
    var other = Path.Combine(folder, "other.mp3");
    File.Copy(path, other);
    check(ThemeService.Status(new ManagedTheme { Folder = folder, Path = other, Hash = managed.Hash }) == "Missing or externally modified",
        "identical audio at another path is not managed");
    File.WriteAllText(path, "user edited theme");
    check(ThemeService.Status(managed) == "Missing or externally modified", "edited theme is not active");
    File.Delete(path);
    check(ThemeService.Status(managed) == "Missing or externally modified", "deleted theme is not active");
}
finally { Directory.Delete(folder, true); }
Console.WriteLine("Theme checks passed");
