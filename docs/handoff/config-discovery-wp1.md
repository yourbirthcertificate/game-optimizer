# Config discovery design for WP1

Give every installed game a read-only search for settings locations. Report what linked each candidate to the
game, what exists on this PC, and why any lead could not be verified. This design uses the locator and REDengine
work in [PR #2](https://github.com/yourbirthcertificate/game-optimizer/pull/2) as its implementation baseline.

## Core interface

Put the search in `Library/ConfigDiscovery`. The CLI and a future UI call
`ConfigDiscovery.FindAsync(game, options, cancellationToken)` and get a `ConfigDiscoveryResult` with ranked
candidates and per-source diagnostics. Options carry the matched rule, the store IDs already known to the session
and the network choice. Each `IConfigSource` adapter returns candidate locations and reasons it had to skip.
The discovery module owns validation, deduplication and ranking.

| Record | Fields |
| --- | --- |
| `ConfigCandidate` | Local path or HKCU key, kind (file or registry), optional format hint, confidence, evidence list |
| `ConfigEvidence` | Source, observed fact, optional source URL, revision or retrieval time |
| `ConfigDiagnostic` | Source, reason code and message for misses, partial searches or unavailable data |

Confidence has three levels. **Lead** means a source reports a location. **Present** adds an observed file or key.
**Confirmed** requires evidence that ties the file to the game and a successful read-only parse with a known,
applicable parser. Use a shape check specific to the settings type, such as recognized REDengine options in a
located user settings file. WP2 will supply the remaining parsers. HKCU value names provide presence evidence.
Keep all provenance when two sources resolve to the same Windows path or key. Rank by confidence, then game
association (engine, matched rule or exact store ID before name hints), then ordinal path. Ties stay visible.

## Search order and scope

1. Reuse known engine and matched-rule locations and the matching logic in `SettingsLocator`. Preserve `Find` for
   existing callers. Search all exact normalized game and install-folder matches across Local, LocalLow and Roaming
   AppData, Documents, My Games, Saved Games, ProgramData and the known install directory. Check publisher/product
   paths when local metadata supplies those names and exact Steam app-ID children under matched folders. Treat
   settings-like names in the install folder as leads, since they can be shipped defaults.
2. Open HKCU Software keys identified by known company/product metadata or a matched rule in read-only mode.
   List value names only. Report when the metadata is missing.
3. Resolve community configuration entries gated on the source decision below. Require an exact ID match and
   compatible Windows and store conditions. Expand tokens such as `<winAppData>`, `%USERPROFILE%` and `<root>`
   from resolved local roots. Resolve `<storeUserId>` only from an unambiguous local store account. Unknown tokens,
   ambiguous IDs, save-only entries, traversal and paths outside those roots produce a skip reason.
4. Use executable names already discovered under the install directory to link directory listing hints in Local
   AppData. Require an exact name match and keep it as evidence. This step only inspects directory entries.

Bound enumeration by root, depth and work per source. Skip reparse points and network paths. Share the directory
index within a scan and discard it on rescan. Surface unreadable paths, missing files, ambiguous matches and search
limits in diagnostics. The discovery interface returns locations and evidence only. Settings writes stay with
`Applier` and its existing backup, stale-preview and running-game checks. WP2 owns format writers and requires
in-place edits, byte-exact key-level undo, refusal tests and an `nl-NL` test.

## Community sources

The review decision is whether to use runtime sources or a reviewed pack. Start with local discovery. For
PCGamingWiki, verify an exact Steam ID to wiki page ID lookup and a recorded
Windows config-template payload before relying on it. The requirements lookup fetches by title and caches only
requirements blocks, so it cannot supply config locations under the IDs-only rule. MediaWiki supports
`action=parse` by numeric `pageid`. The template page returned HTTP 403 during design verification.
[MediaWiki parse interface](https://www.mediawiki.org/wiki/API:Parsing_wikitext)
[PCGamingWiki config template](https://www.pcgamingwiki.com/wiki/Template:Game_data/config)

Ludusavi provides a YAML manifest with config tags, conditions and store IDs. Its repository lists an MIT license
and schedules an import every six hours. A fixed data snapshot could be fetched once and matched by ID locally.
Verify attribution and upstream data reuse terms before selecting runtime lookup or a reviewed pack.
[Ludusavi manifest](https://github.com/mtkennerly/ludusavi-manifest)
[Import workflow](https://github.com/mtkennerly/ludusavi-manifest/blob/master/.github/workflows/import.yaml)

If runtime sources are approved, use explicit opt-in, GET over HTTPS to allowlisted hosts, a generic user agent,
bounded responses and paced requests. Reject cross-host redirects and treat all response paths as untrusted.
Transmit only validated store or wiki IDs for game-specific lookups. Read fresh cached source data under the active data
directory's cache in offline mode. Cache IDs, relative templates and provenance, with local path expansion at read
time. Keep the local report available when cache entries expire or upstream is unavailable.

## CLI and UI handoff

`optigame-cli config find --game NAME [--offline]` reports path, kind, format hint, confidence and evidence.
Use a stable game ID when a name is ambiguous. `config find --all-skipped` uses the same skipped-game selection
as `why --all-skipped`, retaining each original skip reason. Show paths through `PathDisplay.Short`. The proposed
runtime opt-in is `--online`, with local discovery and available cache by default.

The future UI can consume `GamePlan.SettingsCandidates` and `GamePlan.Evidence` as empty-by-default read-only lists. WP3 adds
`Recommendation { Step, PerOption[], Evidence[], Confidence, Assumptions }`. WP4 adds
`GuideAdvice { GameId, OptionId, ValueOrDirection, Source, Url, RetrievedUtc, Summary, Confidence }`.
Discovery runs on demand, outside normal planning.

## Verification

Use temp roots, a registry reader fixture and recorded HTTP responses. Cover exact matches, collisions, ranking,
missing or invalid files, unsupported formats, constrained path expansion, offline/cache behavior and blocked
requests. Check numeric output with `CultureInfo.InvariantCulture` under `nl-NL`. Compare the same skipped-game
cohort before and after, counting games with a newly confirmed candidate and reporting present-only leads
separately. The handoff reports 57 skipped games. Record the actual cohort and results when the command exists.
