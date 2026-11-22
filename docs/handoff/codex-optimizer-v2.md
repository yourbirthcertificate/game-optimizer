# Handoff to Codex: precise optimizer and deeper config search (v2)

Everything here is written to be pasted into Codex or read by it. The repo's `AGENTS.md` holds the standing rules.
Start with the prompt, then the work packages.

## 1. The prompt to paste

```
You are joining OptiGame+, a Windows 11 WPF/.NET 10 app that finds a gaming laptop's installed games, recommends
graphics settings for each, previews every change, applies it with a backup and can undo it. Read AGENTS.md first,
then docs/handoff/codex-optimizer-v2.md in this repo, then README.md.

The author wants two things, and you do the engine side of both:
  A. A more precise optimization system: recommendations that come from this PC's real specs and from published
     guides and other online sources, with the evidence shown, instead of one PC-wide step.
  B. A deeper config search for every game: find where each game keeps its settings, in the store folders, AppData,
     Documents, Saved Games, the registry and public databases, and make as many games as possible changeable.

You work in src/OptiGame.Core, src/OptiGame.Cli and tests/OptiGame.Tests. Claude Code owns the WPF UI (src/OptiGame.App),
the installer and CI, and reviews anything that writes user files, so do not edit those; expose data and tell me what
the UI needs. Use one branch and one pull request per work package (codex/<name>, based on main), smallest useful
slice first. Do not merge, tag or release.

Hard rules: never write to a real game's settings, registry or the real %LOCALAPPDATA%\GameOpt; tests use temp folders,
fixtures and dummy processes; never launch the GUI without --no-auto --data-dir <temp>; numbers written anywhere use
InvariantCulture (the PC is nl-NL) with a nl-NL test; edit settings files in place, byte-exact, with a byte-exact undo
test; nothing guessed (skip with a reason); network is GET-only, allowlisted, cached, optional and sends only ids.

Start now: (1) build and run the tests, (2) read Optimize/Planner.cs, Optimize/Planner.Red.cs, Formats/RedSettings.cs,
Optimize/Rules.cs, Library/SettingsLocator.cs, Recommend/Recommender.cs and Safety/Backups.cs, (3) write a short design
note for work package 1 in docs/handoff/ (interfaces, data shapes, what you will not do), and open it as a PR for
review before writing the code. Then proceed package by package. Ask me before anything that changes the safety rules.
```

## 2. State of the code you start from

- `main` has OptiGame+ 1.0.0. A follow-up PR (branch `fix/menu-fonts-redengine-culture`) adds a REDengine 4 handler
  for Cyberpunk 2077, `SettingsLocator`, the culture audit and tests. Rebase on it once it is merged.
- Handlers today: Unreal (`GameUserSettings.ini`), Unity (registry PlayerPrefs), Source 1 (`video.txt`), REDengine 4
  (`UserSettings.json`, in-place token edits), plus JSON rules for anything else in four formats: `ini`, `json`, `kv`, `gvas`.
- 82 games on the author's PC: 25 changeable, 57 skipped. Why the 57 are skipped (counted by `optigame-cli why --all-skipped`):
  about 22 are Unity or Unreal games that already have a handler and only need to be started once, 2 have the settings
  in an unexpected place, and 33 have no handler (30 with an unrecognised engine). Only 5 of those had a settings
  folder that `SettingsLocator` could find by name. Many of the rest are 2D indie games with few graphics options, so
  coverage matters most for the 3D games: Elden Ring (`%APPDATA%\EldenRing\GraphicsConfig.xml`), Helldivers 2, Planet
  Coaster (XML under Saved Games), Persona 4 Arena Ultimax (ini), Metal Gear Rising, Tiny Glade, Project Zomboid.
- The recommendation is one step per game from `Recommend/Recommender.cs`: the card the publisher lists (Steam, then
  PCGamingWiki) against this PC's card in `data/gpus.json`, scaled for resolution. It cannot tell which option to turn
  down first for a given game.

## 3. Work packages

Order matters: 1 and 2 unblock the rest. Each is a separate PR.

### WP1: config discovery engine ("deeper config search")

Goal: for any installed game, produce a ranked, evidence-backed list of candidate settings locations, read-only.

- New `Library/ConfigDiscovery` namespace: `IConfigSource` returning `ConfigCandidate { Path, Kind (file/registry), Format guess,
  Source, Evidence, Confidence }`. Sources, cheapest first:
  1. `SettingsLocator` (exists; name match in the usual roots). Extend with publisher-folder matching, Steam app id folders
     (Planet Coaster uses `Saved Games\Frontier Developments\Planet Coaster\<steamid>`), `LocalLow`, `ProgramData`,
     and the install folder itself (`*.ini`, `*.cfg`, `*.json`, `*.xml` named like settings).
  2. Registry: `HKCU\Software\<Publisher>\<Game>` and similar, listing value names only.
  3. Community databases (leads to verify before relying on them): PCGamingWiki's "Game data" configuration file locations
     (its MediaWiki API; parse the `{{Game data/config}}` template with path variables), and the Ludusavi manifest
     (`mtkennerly/ludusavi-manifest`, check its license and update cadence). Expand path variables such as
     `<winAppData>`, `%USERPROFILE%`, `<root>`, `<storeUserId>` on this PC.
  4. Process-based hints: files a game's exe name appears next to in `%LOCALAPPDATA%` (read directory listings only).
- A candidate is only "confirmed" when the file exists and parses in a known format (WP2).
- Cache lookups in `%LOCALAPPDATA%\GameOpt\cache` like `Requirements.cs` does; allowlist hosts; `--offline` works.
- CLI: `optigame-cli config find --game NAME [--offline]` and `config find --all-skipped` with a table of results and evidence.
- Tests: fixtures for each source's response shapes (no live network in tests), nl-NL where numbers appear.
- Acceptance: on the author's 57 skipped games, report how many gain a confirmed candidate (target: report honestly; no number is promised).

### WP2: format handler registry

Goal: add formats without touching `Planner.cs` each time.

- `IFormatHandler` (`Detect`, `Read` to a typed option model, `TrySet`, `Undo` locator) and a registry that the Planner
  consults. Port `ini`, `kv`, `json`, `gvas`, REDengine to it without changing behaviour (existing tests must stay green).
- New handlers, each with a fixture, an in-place edit test and a byte-exact key-level undo test:
  XML (Elden Ring `GraphicsConfig.xml`, Planet Coaster, Lossless Scaling), a Lua-ish/`.config` reader for Helldivers 2
  (`user_settings.config`), ini without sections (Project Zomboid `options.ini`), Godot `project`/`.cfg`, Source 2 `.vcfg`.
- Preserve whitespace, comments, attribute order and encoding. Refuse what you cannot round-trip.
- Acceptance: for each new format, apply then undo on a fixture gives back identical bytes; a stale-file test refuses.

### WP3: spec-driven recommendation model ("more precise")

Goal: per-option, per-game recommendations from specs and evidence, with a visible "why".

- Data model: `OptionCost` (how much an option costs in GPU, VRAM, CPU, RAM terms and how much it changes the picture),
  `HardwareBudget` (this PC: GPU score, VRAM, CPU threads and hybrid cores, RAM, display resolution and refresh, power
  limits on laptops, battery state), and a solver that picks per-option levels to fit a target frame rate on the
  display. It must degrade to today's behaviour when no data exists.
- Targets as user choices, not guesses: target fps (default: the display's active rate minus 3, the existing auto cap),
  priority (look, speed, quiet). Never invent a measured number; label what is estimated.
- Use measured data when it exists: the benchmark store (`Bench/`) records settings and results; use a user's own
  captures to nudge the estimate for that game, and say so.
- Output a `Recommendation { Step, PerOption[], Evidence[], Confidence, Assumptions }`; `GamePlan` carries it so the UI can show "why".
- Tests: deterministic fixtures of hardware and option tables; property tests that a lower budget never yields higher levels.

### WP4: guide and online sources layer

Goal: turn published guidance into reviewable, attributed data, not scraped prose.

- `IGuideSource` implementations that yield `GuideAdvice { Game, Option, Value or Direction, Source name and URL, Retrieved, Quote-free summary, Confidence }`:
  PCGamingWiki settings and "Essential improvements" tables, Steam guides (links only, never scraped text without
  checking the terms), manufacturer pages. Store parameter mappings and a link, not copied text (copyright).
- Conflict rules: user choice > measured benchmark > curated rule > guide advice > model estimate. Every item carries provenance.
- A curated pack format under `rules/guides/*.json` (schema + validator + `optigame-cli rule validate`), each entry with
  `source`, `retrieved`, `verified` (date) and `appliesTo` (game version or build). Unverified entries are shown as suggestions and never applied silently.
- Rate limits and caching as in `Requirements.cs`; hosts allowlisted; all of it optional offline.
- Acceptance: the same game gives the same plan offline from cache; each recommendation lists its sources.

### WP5: rule authoring pipeline

Goal: make covering a game cheap and safe.

- `optigame-cli rule draft --game NAME` combines WP1 (where) and WP2 (format) to draft a rule from a before/after snapshot
  pair (the wizard already diffs two snapshots: reuse `Rules/RuleWizard.cs`), marks every value's evidence, validates it
  against `rules/rule.schema.json`, and writes it to the user rules folder only.
- Batch mode over `--all-skipped` that produces a report of drafts that need a human to confirm in the game. It never
  writes a built-in rule and never applies anything.
- Tests: wizard-style fixtures; a draft with one observed value never fills in the other steps (nothing guessed).

### WP6: data contracts for the UI (small)

Expose, without any UI code: `ConfigCandidate`, `Recommendation` and `GuideAdvice` as plain records in Core, a
`GamePlan.Evidence` list and a `GamePlan.SettingsCandidates` list. Claude Code builds the panels ("Why this
recommendation", "Where its settings probably are", "Add a rule from this"). Put the shapes in the WP1 design note so both of us agree first.

## 4. Division of labour

| Claude Code | Codex |
|---|---|
| Fluent UI, pages, dialogs, view models | WP1 to WP5 engine code and CLI |
| Installer, updater, CI, release | Tests and fixtures for all of it |
| Safety review of every writer, final say on anything that touches user files | Design notes before code |
| Merging order, rebases, README and credits | Reports of what could not be verified |
| Screenshots and UI verification | Nothing that needs the GUI |

## 5. Definition of done for a package

1. `dotnet build OptiGame.slnx` has no CA1305/CA1304/CA1311 warnings and `dotnet test` is green.
2. New writers: byte-exact apply/undo test, stale-file refusal, running-game refusal, nl-NL test.
3. New network code: allowlist, cache, offline path, a test with a recorded response (no live calls in tests).
4. Every skip has a reason; nothing is guessed; every recommendation shows its source.
5. The PR says what changed, how it was tested, and what could not be verified (for example anything needing the real game or a live service).

## 6. Questions to ask the author before starting

- Is it acceptable to read PCGamingWiki and the Ludusavi manifest at runtime, or should they only feed a checked-in, reviewed pack?
- How aggressive may "deeper search" be on disk (only the usual roots, or also scanning whole drives)? Default: usual roots only.
- Should unverified guide advice ever be applied with one click, or only shown as a suggestion? Default: suggestion only.
- Which games are the first targets? Default: Elden Ring, Helldivers 2, Planet Coaster, Persona 4 Arena Ultimax, Project Zomboid.
