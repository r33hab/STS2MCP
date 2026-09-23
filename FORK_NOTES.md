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
- **`debug_start_event {event:"SelfHelpBook"}`** — enter the named event's room,
  jumping straight to that event. The sibling of `debug_start_encounter`, and the
  reason it exists: without it there is no way to reach a chosen event on demand, so
  the emulator's per-event tests had no way to take ground truth from the game.
  `EventModel` seeds its own Rng from the run seed, the current floor and a hash of the
  event's id, so capture the floor alongside the readout. Note the game updates the
  reported event id before its option list, so a caller must wait for the readout to
  settle rather than reading the first state that names the right event.
  (`McpMod.Debug.cs`)
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

## Known issues

Rapid `abandon → re-embark → debug_start_encounter` cycling crashes the game (error
popup). Use single clean sequences with generous waits; hardening these actions with
settled-state guards is a tracked follow-on.

`GET /api/v1/singleplayer` can return HTTP 500 while a room transition is still in
flight — seen reliably when entering Fake Merchant, whose screen is built as a shop. It
clears once the room settles, so a caller polling across a `debug_start_*` should treat a
failed read as "not there yet" rather than as an error; a batch that gives up on the read
never posts its next jump and strands itself in the room it could not read.

The state also reports the event id before it reports that event's option list, so a
caller that waits for the first state naming the right event gets the *previous* event's
options under the new event's name. Wait for the option list to both change and hold
still.
