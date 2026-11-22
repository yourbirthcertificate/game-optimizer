# AGENTS.md

Guidance for AI coding agents (Codex, Claude Code and others) working in this repository.
Humans: the README is the place to start.

## What this is

OptiGame+ is a Windows 11 desktop app (WPF, .NET 10) for gaming laptops. It finds installed games, recommends
graphics settings for each, previews every change, applies it with a backup and can undo it, and clears resources
live while a game runs. Personal tool, written with AI, PolyForm Noncommercial 1.0.0, no donations.

## Layout

| Path | What lives there |
|---|---|
| `src/OptiGame.Core` | All logic, no UI. `Optimize/` (Planner, Rules, Profiles, Prefs, Session), `Formats/` (ini, kv, gvas, Unity, REDengine JSON), `Library/` (store scanners, engine detection, `SettingsLocator`), `Recommend/` (requirements, GPU table), `Safety/` (`Applier`, backups, restore), `Bench/`, `Live/` (boost, scanner, detector), `Art/`, `Vendors/`, `SpecCheck/`, `Updates/` |
| `src/OptiGame.App` | WPF app: Fluent theme, pages, view models. Owned by Claude Code: see "Who owns what" |
| `src/OptiGame.Cli` | `optigame-cli` (alias `gameopt`): same Core, command line |
| `tests/OptiGame.Tests` | xUnit. Fixtures in `tests/OptiGame.Tests/fixtures` |
| `rules/` | Built-in game rules (JSON, schema in `rules/rule.schema.json`) |
| `data/` | `gpus.json` (card scores, shared with Game Spec Check), `vendors.json` (laptop vendor apps) |
| `installer/` | Inno Setup script and `build.ps1` |

Build and test: `dotnet build OptiGame.slnx` and `dotnet test tests/OptiGame.Tests`. All tests must pass before a PR.

## Safety rules (hard)

The author plays on the same PC the code runs on, often while you work.

1. **Never write to a real game's settings file, registry value, or backup folder.** Tests use temp folders, fixtures
   and dummy processes only. `Planner.LocalAppData` (internal) and `GAMEOPT_DATA_DIR` / `--data-dir` redirect paths.
2. **Never boost, close, throttle or trigger anything on real processes.** Launch the GUI only with
   `--no-auto --data-dir <temp folder>`. Do not leave windows on top of the screen; they steal keystrokes.
3. Reading real game folders and settings files is fine (the scanner does it), copy first if you want to keep them.
4. Apply must refuse while the game runs (`Applier`, `ProcessGuard`). Keep it that way for every new handler.
5. Every write goes through `Applier.Apply`: backup first, stale-preview check, key-level undo. A new format needs a
   byte-exact undo test.
6. Nothing guessed: if a value cannot be read with confidence, skip it with a reason. Never compute a list index by
   assumption; read it from the file.
7. Network: GET only, a generic user agent, only ids (or an exact title where a game has no id), from an allowlist of
   hosts, cached, and fully optional (`--offline`). Nothing about the PC is sent. Treat everything fetched as untrusted.

## Conventions

- C# nullable enabled, file-scoped namespaces, `ImplicitUsings` on in Core. No new dependencies without a reason in the PR.
- **Numbers into text that is written anywhere use `CultureInfo.InvariantCulture`.** The author's PC is nl-NL (decimal
  comma) with an English UI. `.editorconfig` turns CA1305, CA1304 and CA1311 into warnings; keep the build free of
  them. New writers need a test under `new CultureInfo("nl-NL")` (see `tests/OptiGame.Tests/CultureTests.cs`).
- Preserve formats: edit settings files in place and change only the bytes that must change (see
  `Formats/RedSettings.cs`). Do not re-serialize a whole file when a surgical edit is possible.
- Paths shown to the user go through `PathDisplay.Short` (`%LOCALAPPDATA%`, `%USERPROFILE%`), so a screenshot never carries an account name.
- User-facing text: sentence case, no em dashes, no hype words, no "simply" or "just", say what is true. Plain words.
- The data folder is `%LOCALAPPDATA%\GameOpt` (the old optimizer's name, kept so backups and history keep working).
- Tests: prefer small xUnit facts with fixtures. Name tests after the behaviour. Mutation-check new tests once
  (break the code, see the test fail).
- Use `rg`/grep to find code; do not reformat files you are not changing (files use CRLF in the working tree; git normalises).

## Who owns what (to avoid merge conflicts)

- **Claude Code** owns `src/OptiGame.App/**`, `installer/**`, `.github/**`, the README and the final review of anything
  that writes to a user's files.
- **Codex** owns new engine work in `src/OptiGame.Core` (discovery, handlers, recommendation, guide sources) and the
  matching CLI commands and tests, as described in `docs/handoff/codex-optimizer-v2.md`.
- Shared files (`Optimize/Planner.cs`, `Library/GameInfo.cs`, `Optimize/Plan.cs`, `Safety/Backups.cs`): keep edits tiny
  (a registry line, a new enum value) and put the logic in new files, as `Planner.Red.cs` does.

## Pull requests

One work package per branch and PR, branch `codex/<short-name>`, based on `main`. Describe what changed, the tests,
and anything you could not verify. Do not merge, tag or release; the author does that.
