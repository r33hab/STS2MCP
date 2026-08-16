using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

// Debug actions used by the emulator's differential-test harness
// (validate_real_game_trace.py): jump straight into a chosen encounter, and
// force the combat into the player play phase. These mirror capabilities that
// existed only in an unpublished local mod; reconstructed here from the
// decompiled game's CombatRoom / RunManager / CombatManager APIs.
public static partial class McpMod
{
    // {"action":"debug_start_encounter","encounter":"CorpseSlugsWeak"}
    // Builds a CombatRoom for the named encounter against the current run state
    // and enters it, replacing whatever room the run is currently in.
    private static Dictionary<string, object?> ExecuteDebugStartEncounter(
        Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("encounter", out var encEl)
            || encEl.ValueKind != JsonValueKind.String)
        {
            return Error("debug_start_encounter requires a string 'encounter' field");
        }

        var name = encEl.GetString()!;
        var encounter = ModelDb.AllEncounters
            .FirstOrDefault(e => e.GetType().Name == name);
        if (encounter == null)
            return Error($"Unknown encounter: {name}");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return Error("No run state available");

        // Fresh, mutable copy so we don't scribble on the shared registry model.
        var room = new CombatRoom(encounter.ToMutable(), runState);

        // Fire-and-forget: the game processes the room transition on its own loop;
        // the caller polls /api/v1/singleplayer until the battle appears.
        _ = RunManager.Instance.EnterRoom(room);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Starting encounter {name}",
        };
    }

    // {"action":"debug_force_play_phase"}
    // After debug_start_encounter the combat may still be in its start sequence
    // (banner / setup) rather than the player play phase. Report readiness; the
    // natural StartTurn transition drives IsPlayPhase, which the caller polls.
    private static Dictionary<string, object?> ExecuteDebugForcePlayPhase(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = IsPlayPhase(player)
                ? "Already in play phase"
                : "Combat in progress; awaiting play phase",
        };
    }

    // {"action":"return_to_main_menu"}
    // Save-and-quit to the main menu, leaving the run in progress (the pause menu's
    // "Save and Quit" path: NPauseMenu.CloseToMenu -> NGame.Instance.ReturnToMainMenu).
    // v0.107 removed the equivalent action; the differential harness's
    // abandon_existing_run() depends on it.
    private static Dictionary<string, object?> ExecuteReturnToMainMenu()
    {
        var game = NGame.Instance;
        if (game == null)
            return Error("NGame not available");

        _ = game.ReturnToMainMenu();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Returning to main menu",
        };
    }
}
