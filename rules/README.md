# Game rules

The engine handlers cover most Unreal, Unity and Source games without any per-game work.
A rule adds what a handler cannot know: a game-specific save file, a settings file in an
unusual place, or an engine the tool does not handle.

One JSON file per game. Files here are built into the program. You can also drop your own
in `%LOCALAPPDATA%\GameOpt\rules`; those load last, so a file with the same `id` replaces
the built-in one. Comments (`//`) and trailing commas are allowed.

```jsonc
{
  "id": "voidbreaker",                    // unique, lowercase
  "name": "VOID/BREAKER",
  "match": {                              // any one of these is enough
    "steamAppIds": ["2615540"],
    "epicAppNames": [],
    "installDirs": ["VOIDBREAKER"]        // folder name under steamapps/common etc.
  },
  "replaceEngineHandler": false,          // true: skip the engine handler for this game
  "unrealConfigName": null,               // Unreal: %LOCALAPPDATA% folder, if not the project name
  "files": [
    {
      "format": "gvas",                   // gvas (Unreal .sav), ini, json or kv (Valve text)
      "path": "%LOCALAPPDATA%/VOIDBREAKER/Saved/SaveGames/Settings.sav",
      "steamCloud": true,                 // adds a warning in the preview
      "set": {
        "FrameRateLimit": "$fpsCap",
        "MotionBlurQuality": 0,
        "ShadowQuality": [0, 1, 1, 2, 3, 3]   // one value per slider step
      }
    }
  ]
}
```

## Paths

Use forward slashes. You can use `%ENVIRONMENT%` variables, `%DOCUMENTS%`, `%SAVEDGAMES%`
and `{install}` (the game's install folder).

## Keys

- `gvas`: the property name in the save. Blueprint names with a `_12_ABCD...` suffix match
  without the suffix. Only top-level int, float, double, bool, byte, enum, string and
  IntPoint properties can be set.
- `ini`: `Section/Key`. The split is at the last `/`, so `/Script/Engine.GameUserSettings/bUseVSync` works.
- `json`: a dotted path, such as `graphics.shadows`.
- `kv`: the key name, such as `setting.mat_vsync`.

A key is only changed if it already exists in the file with a value of the same kind.
Anything else is listed as skipped in the preview.

## Values

- A literal: `0`, `1.5`, `true`, `"EWindowMode::Fullscreen"`, `[2560, 1440]`.
- A list of exactly 6 values: one per slider step, Low to Ultra.
- A variable:

| Variable | Meaning |
|---|---|
| `$step` | Slider step, 0 (Low) to 5 (Ultra) |
| `$fpsCap` | Panel refresh rate minus 3 |
| `$refreshHz` | Panel refresh rate |
| `$nativeWidth`, `$nativeHeight` | Native resolution |
| `$nativeResolution` | `[width, height]`, for IntPoint properties |
| `$vsync` | Always `false` (frames are capped instead) |
| `$textureCap` | Highest texture level (0-3) for this GPU's memory |
| `$ue.<Group>` | The Unreal scalability level (0-3) for this step: `ViewDistance`, `AntiAliasing`, `Shadow`, `GlobalIllumination`, `Reflection`, `PostProcess`, `Texture`, `Effects`, `Foliage`, `Shading`, `Landscape` |

## Checking a rule

Run `gameopt preview --game "<name>"`. The preview lists every change as `old -> new`
and every key it left alone, with the reason. Nothing is written until `gameopt apply`.
