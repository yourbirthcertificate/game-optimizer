# GameOptimizer

A small Windows app that finds the games installed on your PC and sets their graphics
settings for your hardware in one click. It works on games that GPU vendor tools do not
know about, because it reads each game's engine settings directly instead of relying on
a list of supported titles.

## About this project

- **A personal tool.** It was made for the author's own use and is shared as is, with no support and no promise of updates.
- **Written with AI.** The code was written by Claude, an AI model from Anthropic, through Claude Code. It has had no professional review or security audit, and some of its choices will be wrong for some games.
- **Noncommercial use only.** It is licensed under the PolyForm Noncommercial License 1.0.0. Personal, hobby and study use is fine. Commercial use is not.
- **No donations.** The author is not accepting donations or sponsorship.

## What it does

1. Reads your hardware: graphics card and its memory, processor, RAM, and the screen's
   native resolution and highest refresh rate.
2. Finds installed games in every Steam library (`libraryfolders.vdf`) and in the Epic
   Games Launcher. A library on a drive that is not connected is reported and skipped.
3. Works out each game's engine (Unreal, Unity, Source, Source 2, Godot, GameMaker) and
   where that engine keeps its settings.
4. Shows a preview of every change as `old -> new`, then backs up each file and registry
   value before writing it.

### The slider

There are six steps: Low, Medium, Balanced, High, Very high and Ultra. The app marks one as
**Recommended** for your PC from the graphics card's class, its memory and the screen
resolution. An upper-mid laptop graphics card at 2560x1440 lands on High, for example.

The **All games** slider moves every game you have not set yourself. Moving a game's own
slider marks it **custom**; it then stays where you put it until you press the reset
arrow next to it. Your choices are saved.

Every step also sets the game to your native resolution, turns VSync off and caps the
frame rate 3 fps under your screen's refresh rate (162 fps on a 165 Hz screen). That keeps
frame times even without the input delay of VSync.

### What it changes per engine

| Engine | Where | What |
|---|---|---|
| Unreal | `%LOCALAPPDATA%\<Project>\Saved\Config\Windows*\GameUserSettings.ini` | Resolution, fullscreen mode, VSync, frame cap, the eleven `sg.*` scalability groups, render resolution 100% |
| Unity | `HKEY_CURRENT_USER\Software\<Company>\<Product>` | Resolution, borderless fullscreen, and the quality level when the game uses Unity's built-in levels (read from the game's `globalgamemanagers`) |
| Source | `<game>\<mod>\cfg\video.txt` | Resolution, fullscreen, VSync, effect, shader and texture detail |
| Others | A rule file for that game | Whatever the rule lists |

Texture quality is capped by graphics memory, so a 4 GB card never gets the top texture
level even on Ultra.

### Game-specific saves

Some games keep their menu settings in their own save file and apply it over the engine
settings at startup. VOID/BREAKER does this with an Unreal `Settings.sav`. For those
games a small JSON rule in [`rules/`](rules/README.md) says which values to set. The app
can edit Unreal `.sav` (GVAS) files from UE4 through UE5.4 and later, and it only edits one
when writing the parsed file back reproduces the original byte for byte.

## Safety

- **It never touches a running game.** Before writing, it checks whether any program is
  running from the game's install folder. If one is, that game is skipped.
- **Backups first.** Every file and registry value is copied to
  `%LOCALAPPDATA%\GameOpt\backups\<run>` before it is written. The last 20 runs are kept.
- **One-step undo.** "Restore last run" in the app, or `gameopt restore`, puts everything
  from a run back.
- **No guessing.** If a settings file cannot be read with confidence, a key appears more
  than once, a value has an unexpected type, or the game uses its own settings menu, that
  part is left alone and the preview says why.
- **Stale previews are refused.** If a game rewrote its settings after the preview, the
  write is cancelled and you are asked to preview again.
- **Steam Cloud.** When a settings file syncs through Steam Cloud the preview warns you.
  If Steam asks which copy to keep on the next launch, choose the one on this PC.

## Running it

### With the exe

Download `GameOptimizer.exe` (the app) or `gameopt.exe` (command line) from the Releases
page. They need Windows 10 or 11 and nothing else.

The exe is not code-signed, so Windows may show "Windows protected your PC" the first
time. Choose "More info", then "Run anyway".

### Command line

```
gameopt hw                      Hardware and the recommended step
gameopt scan                    Every game, its engine and what can be done
gameopt preview [--game NAME] [--step STEP]
gameopt apply   [--game NAME] [--step STEP]
gameopt restore [--run ID] [--game NAME]
gameopt restore --list
gameopt step --global high | recommended
gameopt step --game NAME veryhigh | reset
```

`preview` never writes anything. `apply` uses the same options.

### From source

You need the .NET 10 SDK.

```
dotnet test
dotnet run --project src/GameOptimizer.App
dotnet run --project src/GameOptimizer.Cli -- scan
```

Single-file builds:

```
dotnet publish src/GameOptimizer.App -c Release -r win-x64
dotnet publish src/GameOptimizer.Cli -c Release -r win-x64
```

## Known limits

- Games that never started have no settings yet, so there is nothing to change.
- Unity games that build their own settings menu (many do) only get resolution and display
  mode, unless someone writes a rule.
- When a Unity build leaves out type information, only the first quality level name can be
  read; the others show as numbers in the preview.
- Source 2 games, and engines without a handler such as Elden Ring's or Helldivers 2's, need
  a rule file. None exist yet.
- The graphics card scores are rough. If the recommendation looks wrong for your PC, move
  the slider; it is only a starting point.

## Adding a game

See [rules/README.md](rules/README.md). Rules are plain JSON: a file path, a format, and the
values for each key. A value can be a list of six, one per slider step.

## Legal

[LICENSE](LICENSE) (PolyForm Noncommercial 1.0.0), [NOTICE](NOTICE), [CREDITS.md](CREDITS.md).
This project is not affiliated with Valve, Epic Games, Unity, NVIDIA, AMD, Intel or
Microsoft, or with the makers of any game.
