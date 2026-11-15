# Contributing a game rule

Most games need no rule: the Unreal, Unity and Source handlers read their engine's settings
directly. A rule helps when a game keeps its graphics settings somewhere those handlers do not
look, or uses an engine without a handler. This project only ships rules that someone has
checked against the game. A guessed rule can quietly break a game's settings, so evidence
matters more than coverage.

## What counts as evidence

For each key in the rule:

1. **Where it lives.** The settings file (or `HKCU/...` registry key) and the game version or
   build you checked.
2. **That it is the setting.** A before/after pair: change only that setting in the game's own
   menu, save, close the game, and capture the file again. The rule wizard does this and shows
   the difference (`gameopt rule snapshot` and `gameopt rule diff` do the same on the command line).
3. **What each value means.** Say which value the game writes for each menu choice. A per-step
   list (`[0, 1, 1, 2, 3, 3]`) needs every value seen in the game, not filled in by pattern.
   If you only know one value, use one value.
4. **That the game accepts it.** Apply the rule with `gameopt apply --game NAME`, start the game,
   and check the menu shows what the preview said. Then restore with `gameopt restore --apply`
   and check the game starts normally.

For "no frame cap", only set `fpsUncappedValue` if you saw what the game writes when its own
menu is set to unlimited.

## What to send

Open a pull request (or an issue if you prefer) with:

- the rule file in `rules/`, passing `gameopt rule validate`;
- `gameVersion`, `source` (how you found the values), `author` and `verified` (the date) filled in;
- the before/after evidence, as text. The output of `gameopt rule diff BEFORE.json AFTER.json`
  is enough. Remove anything personal first (account names, user ids, paths with your
  username); keep only the keys that matter;
- for a new format quirk, a small synthetic test file in `tests/GameOptimizer.Tests/fixtures`
  rather than your real save.

Please do not send real save files, full registry exports or benchmark captures; they can hold
personal data and are not needed.

## Games that need evidence

`gameopt scan` and the app list every game the tool cannot change, with the reason. The usual
cases, each needing someone with the game to collect the evidence above:

- Games on their own engines, which have no handler at all.
- Source 2 games (their `.vcfg` format has no handler yet).
- Godot and GameMaker games.
- Unity games that keep their quality in their own file instead of Unity's built-in levels.
- Unreal games whose menu saves to a `.sav` file that overrides `GameUserSettings.ini`, like VOID/BREAKER.

## Code changes

Keep the conventions in the README: nothing written without a preview, a backup before every
write, no guessing on ambiguous data, and plain wording in user-facing text. Run `dotnet test`
before sending; tests use synthetic files only.
