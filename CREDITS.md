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

## Data on this PC

- **Launcher records.** Installed games are found from the files that Steam
  (`libraryfolders.vdf`, `appmanifest_*.acf`) and the Epic Games Launcher (`*.item`
  manifests) write on this PC. Nothing is read from those companies' services.
- **Game settings.** The tool reads and writes only the settings files and registry values
  each game already created on this PC.

## Game rules

The files in `rules/` were written for this project from settings files on the author's
own PC. Pull requests that add games are welcome; see `rules/README.md`.
