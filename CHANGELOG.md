# Changelog

Versions follow [Semantic Versioning](https://semver.org). The number lives in one place,
`Directory.Build.props`; the app, the setup program, the updater and the exe properties all read it.

## 1.2.0

Advice with sources, a look at where a game keeps its settings, and more engine work. Advice is shown apart from the
edits Apply writes; nothing in it is changed for you.

- Game details show the plan step and the estimate behind it, then suggestions for single settings with their reasons,
  assumptions, what was left out and the fallback when the evidence is thin. Sources are listed with links.
- "Where its settings are" looks for a game's settings on demand and lists each possible location with how sure it is
  and why. It reads files only.
- Cyberpunk 2077 and Elden Ring advice now comes from a reviewed, offline guide pack. Every citation has a retrieval date,
  a review date and the game version or period it covers. Where sources differ, both values are shown with their scope.
- Benchmark results appear as evidence only when two captures differ by one setting, and describe those captures, not
  your game in general.
- Advice priorities: Balanced, Look, Speed and Quiet. Quiet says when noise or power measurements are missing.
- Counter-Strike 2 VSync can be changed, with preview, backup and byte-exact undo, for one verified local Steam account.
- Rules can be drafted from before and after snapshots, for one game or a report across skipped games. A draft records the
  step it was observed at, empty slots keep the setting, and a saved draft stays inactive until you review and enable it.
- On a skipped game, "Draft a rule from snapshots" previews a draft from a before and an after snapshot: the step it
  was observed at, and each setting's value per step with "keep" for the empty slots. Saving writes an inactive draft.
- Game art: the list shows each game's own icon, and the detail header shows the whole key art at its own shape instead
  of a cropped strip. Newer Steam games (no `header.jpg` on Steam's server) and Epic launcher games such as Fortnite now
  get their art.
- The setup program shows what to expect before installing and getting-started tips afterwards.
- Fixed: a test stand-in named after a game (for example `Cyberpunk2077.exe` in the temp folder) was detected as a game
  and boosted. Anything running from the temp folder is now ignored.

## 1.1.0

New game support and a read-only config search from the engine work, plus the fixes after 1.0.0.

- Settings discovery: for any game, a read-only search for where its settings probably are, with the evidence for each
  candidate. A shared registry of file formats now handles the older formats too.
- More games can be changed, each with preview, backup and key-level undo: Elden Ring (XML, resolution), Helldivers 2
  (VSync) and Project Zomboid (VSync).
- Game covers are kept apart from the small list icons.
- Advice for single settings in Cyberpunk 2077 and Elden Ring: each graphics option gets a recommendation with the
  evidence behind it and a fallback when the evidence is thin. This is advice only; nothing extra is written.
- In-app update window: when a new version is out, a banner on the Dashboard opens a window with what changed. You
  download it, then restart when it suits you. Remind me later and Skip this version are there too, and the restart
  waits while a game is running or Boost is active.
- Fixed menus and tooltips on icon buttons showing boxes instead of text (popups inherited the icon font). One set of
  styles now pins the text font for every menu, menu item and tooltip. The More menu opens under its button.
- Cyberpunk 2077 (REDengine 4) is supported: quality steps map to the game's own options in `UserSettings.json`, edited
  in place (only `value` and a list's `index` change), with backup, preview and key-level undo. Ray tracing is only ever
  switched off at Low and Medium, never on.
- Skipped games now say where their settings probably are when a folder named after the game exists, and offer Open
  settings folder. `optigame-cli why --all-skipped` lists every skipped game with its reason.
- Numbers written to files, exports and benchmark records no longer depend on the PC's regional format. The audit found
  one real leak: the hardware description stored with a benchmark used the regional decimal separator ("15,7 GB RAM" on
  a nl-NL PC). Settings files, rules and exports were already invariant; other calls now say so explicitly.
  `.editorconfig` makes culture-sensitive calls a build warning, and tests run the writers under nl-NL.
- The detail panel is hidden when no game matches the search.
- Added `AGENTS.md` and a handoff document for further engine work.

## 1.0.0

The first OptiGame+ release: GameOptimizer 0.2.0 and GameClear 1.0.0 merged into one app.

- One app with a Windows 11 Fluent look (Mica, light and dark, native title bar) and four pages:
  Dashboard (Boost, live meters, suggestions), Apps, Games and Settings.
- One game list from Steam, Epic, GOG and folders you add, with the selected game's quality steps,
  exact changes, frame rate and display settings, Apply and Undo beside the list.
- Game art for every game: Steam's local cache, then online sources (switchable), then the game's icon.
- Boost, Use RTX GPU and Can this PC run it? (opens Game Spec Check) on each game.
- Your laptop's own app (MSI Center, Armoury Crate, Legion Space, OMEN Gaming Hub, Alienware Command
  Center, PredatorSense, Gigabyte Control Center, Razer Synapse) and the NVIDIA App, found from a
  table you can edit, with Open buttons and cooling tips.
- A setup program (per user, no administrator) and an updater that checks GitHub releases and verifies
  the download's SHA-256 before installing.
- The command line is `optigame-cli` (`gameopt` still works) and takes `--data-dir`; new `vendor` and
  `speccheck` commands.
- Backups, history, profiles, benchmarks and cache stay in `%LOCALAPPDATA%\GameOpt`; GameClear's data is
  imported once.

## 0.2.0 (GameOptimizer)

Profiles, display and frame-rate control, selective apply, safer restore, the rule wizard and benchmarks.
