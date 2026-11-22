# Changelog

Versions follow [Semantic Versioning](https://semver.org). The number lives in one place,
`Directory.Build.props`; the app, the setup program, the updater and the exe properties all read it.

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
