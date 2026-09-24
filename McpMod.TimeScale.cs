using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;

namespace STS2_MCP;

/// <summary>
/// Keeps the speed set_time_scale chose through the game's hit-stops.
///
/// NHitStop.HitStopTask, the freeze frame on a heavy hit (Vantom, Mecha Knight and Ceremonial Beast moves),
/// drops Engine.TimeScale to 0.1, ramps it back up over a fraction of a second, and ends by setting it to
/// exactly 1. Before this, any speed the API had set silently went back to 1x after such a hit. Now
/// ExecuteSetTimeScale remembers the scale it applied, and KeepApiTimeScale puts it back every frame the engine
/// reads 1 again while no hit-stop is running. HitStopTaskPatch records every hit-stop the game starts, so the scale
/// is not touched while any of them still ramps.
/// </summary>
public static partial class McpMod
{
    /// <summary>The scale the last successful set_time_scale applied; null until one does. Main thread only.</summary>
    private static double? _apiTimeScale;

    /// <summary>
    /// The hit-stops still running, recorded by HitStopTaskPatch. Every one counts, not only the newest:
    /// NHitStop.DoHitStop cancels the previous hit-stop's token but then replaces _cancelToken with a new one, and
    /// HitStopTask checks the field, not the token it started with, so an older hit-stop never sees its cancel. It
    /// runs on to its own end, setting the scale each frame and finally to exactly 1. Main thread only.
    /// </summary>
    private static readonly List<Task> _runningHitStops = new();

    private static bool _timeScaleKeeperFailureReported;

    /// <summary>
    /// Once per frame, from ProcessMainThreadQueue: puts the API's time scale back after a hit-stop. It acts only
    /// when the engine reads exactly 1 (where a finished hit-stop leaves it), the API asked for another scale, and
    /// no hit-stop is still running. Any other scale is left alone.
    /// </summary>
    private static void KeepApiTimeScale()
    {
        if (_apiTimeScale is not double scale || scale == 1)
            return;

        try
        {
            _runningHitStops.RemoveAll(task => task.IsCompleted);
            if (_runningHitStops.Count > 0)
                return;
            if (Engine.TimeScale == 1)
                Engine.TimeScale = scale;
        }
        catch (Exception ex)
        {
            // Once, not every frame: a scale that cannot be set now will not be settable next frame either.
            if (!_timeScaleKeeperFailureReported)
            {
                _timeScaleKeeperFailureReported = true;
                GD.PrintErr($"[STS2 MCP] Could not restore the time scale after a hit-stop: {ex}");
            }
        }
    }

    /// <summary>
    /// NHitStop.HitStopTask, looked up by name, so a game update that renames it (or its class) costs only the
    /// hit-stop tracking. Null when it is not there.
    /// </summary>
    private static MethodInfo? HitStopTaskMethod()
    {
        try
        {
            return AccessTools.Method("MegaCrit.Sts2.Core.Nodes.Vfx.Utilities.NHitStop:HitStopTask");
        }
        catch (AmbiguousMatchException)
        {
            // An overload appeared: which one runs the hit-stop is no longer known.
            return null;
        }
    }

    /// <summary>
    /// Records the task of every hit-stop the game starts. HitStopTask is async, so the postfix runs when it
    /// returns its Task, at its first await, just after the hit-stop has begun. A task already complete is not kept.
    /// </summary>
    [HarmonyPatch]
    private static class HitStopTaskPatch
    {
        // TargetMethods with yield break is not enough on its own here: the game's Harmony (2.4) throws on a
        // patch class that has no target, and that exception would abort PatchAll and take the settings patches
        // with it. Prepare returning false makes Harmony skip the class instead.
        private static bool Prepare()
        {
            if (HitStopTaskMethod() != null)
                return true;
            GD.Print("[STS2 MCP] NHitStop.HitStopTask not found: after a hit-stop the time scale is restored "
                + "without waiting for the hit-stop to end");
            return false;
        }

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var method = HitStopTaskMethod();
            if (method == null)
                yield break;
            yield return method;
        }

        private static void Postfix(Task __result)
        {
            // Pruned here as well as by the keeper, which returns early while no API scale is set.
            _runningHitStops.RemoveAll(task => task.IsCompleted);
            if (!__result.IsCompleted)
                _runningHitStops.Add(__result);
        }
    }
}
