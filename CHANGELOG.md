## [0.3.2.0]

### Improved

- Inset the scrollbars in Manage Downloads and add-theme results, with rounded thumb ends, top and bottom clearance, and a subtle hover color.

## [0.3.1.0]

### Improved

- Keep Manage Downloads in a height-capped scroll panel with sticky column headers and pagination below it. Preserve inner scroll position during updates and reset it for new searches and pages.
- Place add-theme search results in an inset scroll panel sized to the viewport, with thin scrollbars and keyboard scrolling.
- Float row action menus above the scroll panel so they stay usable near its edges, reposition them during scrolling, and close them when their row leaves view.
- Fix the mobile horizontal scrollbar and give table rows consistent padding on desktop and mobile.

## [0.3.0.0]

### Added

- Add themes to movies, TV shows, and eligible collections from Manage Downloads. Live-search all libraries for titles without themes, then find a theme automatically or provide a YouTube video link.
- Process adds, refreshes, and source edits in one server-side queue. Show queued items, processing stages, and outcomes in the downloads table, and continue processing after the browser closes.
- Retry failed adds, choose another YouTube source, or remove the failed entry from the list without changing media files.

### Improved

- Match the add dialog to Jellyfin's existing controls, show selectable result cards with library names and type icons, and simplify the wording.
- Support keyboard navigation, keep focus inside the add dialog, announce search results to screen readers, and confirm queued items with a toast.
- Translate the new flow into all 16 supported languages.
- Block queueing and file-changing actions during scans in both the page and API, with consistent disabled styles. Block full scans while administrator queue work is pending or active. Keep playback, search, pagination, and scan cancellation available.
- Prepare checksum-verified yt-dlp and Deno on the first manually added item without needing a prior scan.
- Simplify processing-queue handoff, downloads-table lookups, error mapping, and admin conditionals while preserving file-ownership checks.

### Maintenance

- Update oxlint from 1.85.0 to 1.86.0.

## [0.2.0.0]

### Added

- Preview managed themes in an audio dialog with locally bundled Video.js controls and translations matching the admin page's language. Remember preview volume and mute for the browser session.
- Remember the selected admin tab, warn before leaving unsaved settings, and preserve settings edits during background reloads.

### Improved

- Refine the configuration page with Jellyfin-style Material fields, compact action dialogs, clearer row menus, red deletion actions, and responsive mobile layouts.
- Keep theme ordering stable after replacements while updating the displayed date. Preserve search, pagination, scroll position, and row focus, and add filtered-result counts and a search clear button.
- Group scan status in an inner panel and show audio loading and buffering inside the player's play control while keeping timeline and volume controls visible.
- Lower the default theme loudness target from -26 to -30 LUFS for future downloads using the default setting.
- Update all 16 supported translations and validate translation keys and placeholders during checks.
- Simplify theme-processing and admin control flow, and expand browser coverage for dialogs, keyboard navigation, audio playback, and YouTube source validation.

## [0.1.30.0]

### Added

- Edit the YouTube source of a managed download and queue the selected video for reprocessing. Prefill existing YouTube sources and leave the field empty for themes from the configured TV URL source.
- Translate the edit dialog, buttons, and validation messages into every supported language.

### Improved

- Use Jellyfin's native edit dialog with a red Cancel button on the left, Save on the right, and recovery from dialog setup errors.
- Validate YouTube video URLs in both the page and management API. Keep the current theme intact until reprocessing succeeds and preserve all file-ownership checks.

## [0.1.29.0]

### Improved

- Queue administrator refreshes across items and sessions, show waiting items in the admin page, and retain inline refresh errors after the list updates.
- Exclude previously used video IDs before fetching metadata so they do not occupy any search or album-track shortlist slots.
- Wait for GitHub's new release listing before regenerating the plugin repository manifest.

## [0.1.28.0]

### Improved

- Reject clearly labeled live performances before downloading, without rejecting film titles that contain “Live.” Keep excluded-format patterns together for easier review.
- Pace consecutive yt-dlp searches and metadata checks as well as requests within each invocation. Retain the longer delay after downloads.

## [0.1.27.0]

### Improved

- Keep the last scan's outcome and counts visible, with a bounded, scrollable list of timestamped skipped items and failures. Copy logged failure details from the admin page.
- Verify ambiguous yearless TV themes against Jellyfin's TMDb searches before accepting them.
- Estimate scan duration from separate timings for existing themes and new searches, without showing an unreliable ETA in the admin page.
- Have `make up` prepare a local administrator and media libraries for manual scans, using the same setup as the end-to-end checks.

## [0.1.26.0]

### Improved

- Clarify the franchise-theme preference in every supported language and document its TMDb metadata requirements.
- Place the TV theme URL template above YouTube cookies in the admin settings.

## [0.1.25.0]

### Added

- Optionally prefer shared franchise themes for movies using Jellyfin's TMDb collection metadata, even without a Jellyfin collection. Fall back to film-specific themes when no shared recording qualifies.
- Add managed themes to selected Jellyfin collections independently of the movie preference. Show the Collections library option only when collections exist.

### Improved

- Preserve existing fades in downloaded audio instead of applying another fade at the same end.
- Allow Docker smoke checks to reach Jellyfin through its container when the published host port is unavailable.

## [0.1.24.0]

### Added

- Let administrators set an optional TV theme URL template keyed by TVDB ID. Try it first for series, then use YouTube if the source is unavailable or its audio is invalid. Movies continue using YouTube.

### Improved

- Show one continuous tool-preparation status while yt-dlp and the JavaScript runtime are being set up.

## [0.1.23.0]

### Improved

- Show when JellyScore is downloading or verifying yt-dlp or Deno during a scan, with status text in every supported language.

## [0.1.22.0]

### Added

- Optionally save YouTube cookies in the administrator settings, with guidance and labels in every supported language.
- Use an installed JavaScript runtime or download a checksum-verified Deno release for YouTube extraction.

### Improved

- Pace yt-dlp requests and pause after YouTube rate-limit errors, while allowing audio conversion to overlap the next search.
- Show each active scan item's search, download, or audio-processing stage.
- Retry transient yt-dlp and Deno installation failures.

## [0.1.21.0]

### Added

- Control new-item processing and full scans after Jellyfin media library scans separately, with both enabled by default and labels in every supported language.

### Improved

- Generate English admin-page fallback text from the translation dictionary at build time, keeping one source for the wording.

## [0.1.20.0]

### Improved

- Measure theme loudness and true peak with FFmpeg's faster EBU R128 filter before applying fixed gain, retaining a conservative peak margin. Existing themes are unchanged.

## [0.1.19.0]

### Improved

- Find named soundtrack tracks linked by YouTube search descriptions, including *The Shawshank Redemption*'s verified 1994 recording.
- Prefer eligible movie themes over end titles when Jellyfin's TMDb film and TV searches find no competing edition. Keep the year requirement when the provider is unavailable, disabled, or finds a clash.

## [0.1.18.0]

### Improved

- Let the match-strength and loudness setting descriptions use the available page width.

## [0.1.17.0]

### Added

- Set a target loudness for new and refreshed themes in the admin page, with labels in every supported language.

### Improved

- Lower the default theme loudness target from -20 to -26 LUFS. Existing downloads stay unchanged.

## [0.1.16.0]

### Improved

- Fade downloaded themes in and out over one second, and lower the fixed-gain loudness target to -20 LUFS.

## [0.1.15.0]

### Improved

- Match the minimum-strength label to the Libraries heading, make its helper text quieter, and add more space above Save settings.

## [0.1.14.0]

### Improved

- Remove the divider below automatic processing and give Save settings more breathing room.

## [0.1.13.0]

### Improved

- Use the same default match strength of 50 for new and existing installations while retaining existing settings.
- Shorten the match-strength helper text in every supported language.
- Keep matching weights, download and audio limits, scan timing, and shared plugin identifiers in one named constants file.

## [0.1.12.0]

### Added

- Set a minimum match strength from 0 to 100 in the admin page. New installations default to 50; existing installations retain their previous matching behavior at 0 until changed.
- Report when search results fall below the chosen minimum in all supported admin languages.

## [0.1.11.0]

### Improved

- Give verified work years and matching film or TV editions more weight when ranking eligible themes, including short series openings.
- Cap weak soundtrack labels and channel signals so promotional wording cannot outweigh stronger work evidence. Document the scoring weights and selection categories.

## [0.1.10.0]

### Fixed

- Reject TV theme uploads that borrow another work's named music, including an *Interview with the Vampire* fan edit uploaded before the 2022 series existed.
- Distinguish film and TV soundtrack editions using titles and identified albums without treating incidental words in descriptions as edition evidence.
- Keep named TV tracks eligible when their description explicitly links them to the correct series and year.

## [0.1.9.0]

### Improved

- Reduce the plugin archive from roughly 224 MB to under 100 KB by downloading and verifying only the server's yt-dlp binary on first use, then caching it.
- Show installation failures and a retry button in the admin page. Pause manual scans and individual theme refreshes until yt-dlp is available again.

## [0.1.8.0]

### Fixed

- Exclude episode character introductions and opening scenes from TV theme matches without rejecting openings merely labelled with an episode number.
- Let explicit TV themes compete with openings and intros, so an otherwise equal theme song ranks above a bare intro.

## [0.1.7.0]

### Fixed

- Retry loading administrator translations after an initial failure so localized settings and diagnostics appear when the translations become available.

## [0.1.6.0]

### Improved

- Translate administrator-visible scan rejection reasons and refresh/delete errors in all supported languages while retaining detailed diagnostics for troubleshooting.
- Display scan estimates with singular and plural minute labels, including "Under 1 min" for shorter estimates.

## [0.1.5.0]

### Improved

- Show a scan time estimate as soon as the item count is known, using prior scan throughput and adjusting it as items finish.
- Keep the estimate on the server to avoid client clock differences and large jumps during slow searches.

## [0.1.4.0]

### Improved

- Prefer an eligible TV opening or intro over a higher-scoring soundtrack track, as seen with *Interview with the Vampire* (2022).
- Use the same 10-second to 8-minute theme duration range for movies and series.

## [0.1.3.0]

### Added

- Delete all managed themes across pages and search results while preserving files changed outside the plugin.

### Improved

- Confirm individual and bulk deletion with Jellyfin dialogs featuring a centered title and red Delete button.

## [0.1.2.0]

### Fixed

- Skip piano tutorials and other how-to-play videos when finding theme music.

## [0.1.1.0]

### Fixed

- Preserve theme music dynamics by applying one fixed gain per track, capped by true peak, instead of dynamic loudness normalization.
- Select the first eligible recording when top-scoring soundtrack tracks tie instead of leaving the item without a theme.

## [0.1.0.0]

### Added

- Theme song discovery for selected movie and TV libraries, with conservative matching and two-pass loudness normalization.
- Admin dashboard for settings, rescan, managed downloads, refresh, and delete.
- One plugin package containing yt-dlp binaries for supported server platforms.
