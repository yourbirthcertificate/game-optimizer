# Game rules

The engine handlers cover most Unreal, Unity and Source games without any per-game work.
A rule adds what a handler cannot know: a game-specific save file, a settings file in an
unusual place, a registry value, or an engine the tool does not handle.

One JSON file per game. Files directly in this folder are built into the program; files in
`examples/` are illustrations and are not loaded. Your own rules go in
`%LOCALAPPDATA%\GameOpt\rules`; they load last, so a file with the same `id` replaces a
built-in one. Comments (`//`) and trailing commas are allowed.

The quickest way to make one is the rule wizard (Tools > Rule wizard, or
`gameopt rule wizard --game NAME --target PATH`). To check a file: `gameopt rule validate FILE`.
The JSON schema is [`rule.schema.json`](rule.schema.json).

```jsonc
{
  "id": "voidbreaker",                    // unique, lowercase letters, digits and dashes
  "name": "VOID/BREAKER",
  "match": {                              // any one of these is enough
    "steamAppIds": ["2615540"],
    "epicAppNames": [],
    "gogIds": [],
    "installDirs": ["VOIDBREAKER"]        // folder name under steamapps/common etc.
  },
  "replaceEngineHandler": false,          // true: skip the engine handler for this game
  "unrealConfigName": null,               // Unreal: %LOCALAPPDATA% folder, if not the project name
  "fpsUncappedValue": null,               // what the game writes for "no frame cap", if known
  // Optional metadata; older rules without it are still valid.
  "gameVersion": "build CL-4516",
  "source": "how the values were found",
  "author": "your name",
  "verified": "2026-11-15",
  "files": [
    {
      "format": "gvas",                   // gvas (Unreal .sav), ini, json, kv (Valve text) or registry
      "path": "%LOCALAPPDATA%/VOIDBREAKER/Saved/SaveGames/Settings.sav",
      "steamCloud": true,                 // adds a warning in the preview
      "set": {
        "FrameRateLimit": "$fpsCap",
        "WindowMode": "$ueWindowMode",
        "MotionBlurQuality": 0,
        "ShadowQuality": [0, 1, 1, 2, 3, 3]   // one value per slider step
      },
      "categories": { "MotionBlurQuality": "quality" },   // for "keep my settings" (optional)
      "labels": { "WindowMode": "Display mode" }          // shown in previews (optional)
    }
  ]
}
```

## Paths

Use forward slashes. You can use `%ENVIRONMENT%` variables, `%DOCUMENTS%`, `%SAVEDGAMES%` and
`{install}` (the game's install folder). For `registry`, the path is `HKCU/Software/...`;
only the current user's registry can be changed.

## Keys

- `gvas`: the property name in the save. Blueprint names with a `_12_ABCD...` suffix match
  without the suffix. Only top-level int, float, double, bool, byte, enum, string and IntPoint
  properties can be set.
- `ini`: `Section/Key`. The split is at the last `/`, so `/Script/Engine.GameUserSettings/bUseVSync` works.
- `json`: a dotted path, such as `graphics.shadows`.
- `kv`: the key name, such as `setting.mat_vsync`. It must appear once in the file.
- `registry`: the value name. DWORD, QWORD and string values can be set. Unity names end in
  `_h` plus a hash; copy them from `gameopt rule snapshot`.

A key is only changed if it already exists with a value of the same kind. Anything else is
listed as skipped in the preview.

## Values

- A literal: `0`, `1.5`, `true`, `"EWindowMode::Fullscreen"`, `[2560, 1440]`.
- A list of exactly 6 values: one per slider step, Low to Ultra. Only write six values you
  have checked in the game; two snapshots do not tell you the other four.
- A variable:

| Variable | Meaning |
|---|---|
| `$step` | Slider step, 0 (Low) to 5 (Ultra) |
| `$fpsCap` | The frame cap the user chose: automatic (active refresh minus 3), a custom number, or "no cap" when the rule sets `fpsUncappedValue`. When the user chose "keep", the key is left alone |
| `$resolution` (alias `$nativeResolution`) | `[width, height]` the user chose; left alone on "keep" |
| `$width`, `$height` (aliases `$nativeWidth`, `$nativeHeight`) | The chosen width or height |
| `$vsync` | `true`/`false` from the user's VSync choice; left alone on "keep" |
| `$displayMode` | `"fullscreen"`, `"borderless"` or `"windowed"`; left alone on "keep" |
| `$ueWindowMode` | The same as Unreal enum text: `EWindowMode::Fullscreen`, `::WindowedFullscreen`, `::Windowed` |
| `$refreshHz`, `$activeRefreshHz` | The display's active refresh rate (may be fractional) |
| `$textureCap` | Highest texture level (0-3) for this GPU's memory |
| `$ue.<Group>` | The Unreal scalability level (0-3) for this step: `ViewDistance`, `AntiAliasing`, `Shadow`, `GlobalIllumination`, `Reflection`, `PostProcess`, `Texture`, `Effects`, `Foliage`, `Shading`, `Landscape` |

Older rules keep working: `$nativeResolution`, `$fpsCap` and `$vsync` mean what they meant
before when the user keeps the default Balanced profile, and follow the user's choices when
they change them.

## Categories

Used by "keep my settings" and profiles. Values using a display or frame-rate variable get the
right category on their own; give others one with `categories`: `resolution`, `displayMode`,
`vsync`, `fpsCap`, `renderScale`, `texture`, `quality` or `other`.

## Checking a rule

`gameopt rule validate FILE` reports unknown variables, lists that are not 6 values long, wrong
value types for a format, keys listed twice, files listed twice, bad paths and unknown
categories. Then `gameopt preview --game "<name>"` lists every change as `old -> new` and every
key it left alone, with the reason. Nothing is written until you apply.
