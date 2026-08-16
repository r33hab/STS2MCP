# Fork notes (evangambit/STS2MCP)

Fork of [Gennadiyev/STS2MCP](https://github.com/Gennadiyev/STS2MCP) used as the
observability/oracle mod for an **AlphaZero-style Slay the Spire 2 agent**. The main
project (the emulator + full context) lives at **github.com/evangambit/sts2** — see its
`HANDOFF.md`.

## What this fork adds (on top of upstream)

Actions reconstructed from the decompiled v0.107.1 game APIs so the emulator's
differential-test harness (`scripts/validate_real_game_trace.py` in the sts2 repo) can
drive the game — these existed only in the emulator author's unpublished local mod:

- **`debug_start_encounter {encounter:"CorpseSlugsWeak"}`** — build a `CombatRoom` for
  the named encounter and enter it, jumping straight into that combat. (`McpMod.Debug.cs`)
- **`debug_force_play_phase`** — report play-phase readiness. (`McpMod.Debug.cs`)
- **`return_to_main_menu`** — save-and-quit to the main menu via
  `NGame.Instance.ReturnToMainMenu()` (the pause menu's Save-and-Quit path). The
  harness's abandon flow depends on it; v0.107 removed the old action. (`McpMod.Debug.cs`)
- **Custom-run screen support** — `NCustomRunScreen` now reports as
  `menu_screen: "character_select"` (`custom_run: true`) and `menu_select` drives it:
  select character, `confirm` with a `seed` (custom mode accepts `Lobby.SetSeed`;
  standard mode rejects seeds), set `ascension`. This is how you start a **seeded** run
  for exact seed-for-seed differential testing. (`McpMod.CustomRun.cs`, routing in
  `McpMod.Actions.cs`, reporting in `McpMod.StateBuilder.cs`)

## Build / install (macOS)

```bash
GAMEDIR="~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2"
dotnet build STS2_MCP.csproj -c Release -o out/STS2_MCP -p:STS2GameDir="$GAMEDIR"
cp out/STS2_MCP/STS2_MCP.dll "$GAMEDIR/SlayTheSpire2.app/Contents/MacOS/mods/STS2_MCP/STS2_MCP.dll"
cp mod_manifest.json         "$GAMEDIR/SlayTheSpire2.app/Contents/MacOS/mods/STS2_MCP/mod_manifest.json"
# mod loads at boot — RESTART the game after installing.
```

Needs .NET 9 SDK. API at `http://localhost:15526` (`/api/v1/singleplayer`,
`/api/v1/compendium`). `upstream` remote = Gennadiyev (pull upstream fixes from there).

## Known issue

Rapid `abandon → re-embark → debug_start_encounter` cycling crashes the game (error
popup). Use single clean sequences with generous waits; hardening these actions with
settled-state guards is a tracked follow-on.
