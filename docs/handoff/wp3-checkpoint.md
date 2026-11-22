# WP3 checkpoint for Claude and Codex

## Where to resume

- Repository: `yourbirthcertificate/game-optimizer`.
- Branch: `codex/per-setting-recommendations`, based on `main` at `0678e60` after PR #9.
- WP1 discovery and the WP2 parser and registry work are already on `main`.
- This branch is the first WP3 slice. It was paused at the user's request to conserve remaining usage.

## What is implemented

- Cyberpunk 2077 gets advisory recommendations for volumetric fog resolution and volumetric cloud quality. The comparison uses the local GPU score, VRAM, RAM, requested resolution, and target FPS against CD PROJEKT RED's 1080p High raster reference. The suggested setting names must occur uniquely in the current file's list. The report respects kept and excluded settings. [Publisher requirements](https://support.cdprojektred.com/en/cyberpunk/pc/sp-technical/issue/1556/cyberpunk-2077-system-requirements) [Original settings tests](https://www.pcgamer.com/the-best-settings-for-cyberpunk-2077/)
- Core attaches the report to `GamePlan.SettingAdvice`. The CLI displays the target, individual suggestions, evidence and assumptions in recommendation details and preview. Both commands accept `--target-fps N`. Without an explicit target, the model uses the current custom frame cap or the detected display's auto cap.
- The existing quality-step recommendation and write plan are the fallback when the GPU, memory, resolution, or compatible raster settings cannot be verified. The report is separate from the edits Apply would write. CPU and power throughput, frame generation, upscaling, and measured FPS are outside this estimate. The per-setting model makes no network requests.
- Main code is in `src/OptiGame.Core/Recommend/CyberpunkSettingRecommender.cs`, `SettingRecommendations.cs`, `Optimize/Planner.Red.cs`, and `src/OptiGame.Cli/CliRecommendation.cs`.

## Verification and resume

- Release solution build passed with the existing CS8604 and CA2022 warnings. All 229 selected tests passed. The HKCU-writing test was explicitly excluded.
- `PerSettingRecommendationTests.cs` covers higher target FPS, memory and unknown-GPU fallback, kept options, unchanged config bytes, source attribution, and CLI numbers under `nl-NL`.
- Before merging, review the draft and confirm CI passed for its current head. The initial WP3 request authorized merging after green CI. The later pause request is why this checkpoint leaves the merge pending.
- Continue with local benchmark integration only when capture metadata can establish the hardware, resolution, frame settings, and options it actually measured. WPF presentation and broader game coverage are follow-up work. Keep fixtures, temporary data folders, and the HKCU test exclusion for verification.
