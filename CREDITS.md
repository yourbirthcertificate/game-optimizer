# Credits

GameOptimizer is a personal tool that was written with AI and is licensed for
noncommercial use only. See [NOTICE](NOTICE) for the short version and
[LICENSE](LICENSE) for the terms.

## Written with

- [Claude](https://www.anthropic.com/claude), an AI model from Anthropic, used through
  [Claude Code](https://claude.com/claude-code). It wrote the code and the documentation
  from the author's instructions.

## Libraries

- [AssetsTools.NET](https://github.com/nesrak1/AssetsTools.NET) by nesrak1, MIT License.
  Reads Unity's `globalgamemanagers` file to find a game's quality levels.
- [.NET](https://github.com/dotnet/runtime) and System.Management by Microsoft and the
  .NET Foundation, MIT License.

## Data

- **Graphics card scores.** `data/gpus.json` is exported from
  [Game Spec Check](https://github.com/yourbirthcertificate/game-spec-check) (same author) by
  `tools/export_gpus.py`. The scores are hand-maintained approximations on a PassMark-like scale.
- **Steam store.** Published system requirements and release dates come from Valve's public
  store API when a game is looked up. Steam is a trademark of Valve Corporation.
- **PCGamingWiki.** Its system requirement tables are the second source. The wiki's content is
  available under a Creative Commons Attribution-NonCommercial-ShareAlike license, as stated
  on its [copyright page](https://www.pcgamingwiki.com/wiki/PCGamingWiki:Copyrights). The app
  reads these tables when it looks a game up and keeps a local copy in its cache. None of the
  wiki's content is stored in this repository.

## Data on this PC

- **Launcher records.** Installed games are found from the files that Steam
  (`libraryfolders.vdf`, `appmanifest_*.acf`) and the Epic Games Launcher (`*.item`
  manifests) write, and the registry entries GOG writes, on this PC. Nothing is read from
  those companies' services.
- **Frame-time captures.** The benchmark import reads the CSV layout of
  [PresentMon](https://github.com/GameTechDev/PresentMon) (MIT License), which CapFrameX and
  other tools also write. No PresentMon code is included.
- **Game settings.** The tool reads and writes only the settings files and registry values
  each game already created on this PC.

## Game rules

The files in `rules/` were written for this project from settings files on the author's
own PC. Pull requests that add games are welcome; see `rules/README.md`.
