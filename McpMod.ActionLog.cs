using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Runs;

namespace STS2_MCP;

/// <summary>
/// The player's moves - card plays, potion uses and end turns - each tagged with who made it:
/// "human" for a click in the game, "api" for an action this mod performed for a client. A coach
/// reads it to know exactly which move was made, and grade that move. Served by GET /api/v1/actions;
/// every state carries the newest entry's seq as action_log_seq.
///
/// Where the moves come from, all of it on the game's main thread:
/// <list type="bullet">
/// <item>ActionQueueSet.ActionEnqueued sees every card play (PlayCardAction), potion use
/// (UsePotionAction) and End Turn button press (EndPlayerTurnAction) as the game queues it, the mod's
/// own play_card and use_potion included. An entry is the request, logged when it is made; the game
/// can still cancel a queued request (the combat ended first, say), and the log does not follow that
/// up - the next state shows what happened.</item>
/// <item>CombatManager.PlayerEndedTurn sees every turn end. The mod's end_turn calls PlayerCmd.EndTurn
/// directly, with no GameAction to queue, so this is where it is logged. The event also fires for turns
/// nobody chose to end (Void Form, a player dying on their own turn, a skipped turn) and when the End
/// Turn button's action finally runs; those are not moves, or already logged, and are skipped.</item>
/// </list>
///
/// Source tagging: ExecuteAction raises _apiDepth around its dispatch, and everything the dispatch does
/// to the game - RequestEnqueue, PlayerCmd.EndTurn - raises these events synchronously inside it. A
/// human click is handled by the engine outside any request, so it always sees _apiDepth == 0.
/// </summary>
public static partial class McpMod
{
    /// <summary>How many entries the log keeps; older ones are dropped, oldest first.</summary>
    internal const int ActionLogCapacity = 2000;

    // _actionLog and _actionSeq are written on the main thread (the hooks) and read by HTTP threads
    // (GET /api/v1/actions) and state reads, always under _logLock. An entry is never changed once it
    // is in the log, so a reader may serialize the entries it copied out after releasing the lock.
    private static readonly object _logLock = new();
    private static readonly Queue<Dictionary<string, object?>> _actionLog = new();
    private static long _actionSeq;

    /// <summary>
    /// Above 0 while ExecuteAction dispatches a client's action on this thread (the main thread).
    /// Thread-static, so it describes this thread's call stack and nothing else.
    /// </summary>
    [ThreadStatic] private static int _apiDepth;

    /// <summary>True while ExecuteEndTurn itself is inside PlayerCmd.EndTurn; see OnPlayerEndedTurn.</summary>
    [ThreadStatic] private static bool _apiEndingTurn;

    // Main thread only (EnsureActionHooks runs from ProcessMainThreadQueue).
    private static ActionQueueSet? _hookedQueueSet;
    private static bool _endTurnHooked;
    private static bool _actionHookFailureReported;
    private static bool _actionLogFailureReported;

    /// <summary>The seq of the newest entry, 0 before the first. Reported as `action_log_seq`.</summary>
    internal static long ActionLogSeq
    {
        get
        {
            lock (_logLock)
                return _actionSeq;
        }
    }

    private static string ActionSource() => _apiDepth > 0 ? "api" : "human";

    /// <summary>
    /// Keeps the hooks attached. Runs every frame from ProcessMainThreadQueue: RunManager creates a new
    /// ActionQueueSet for every run, so the handler moves to the new one as soon as it appears (well
    /// before the run's first combat) and leaves the old one. CombatManager lives for the whole game.
    /// </summary>
    private static void EnsureActionHooks()
    {
        try
        {
            var queueSet = RunManager.Instance.ActionQueueSet;
            if (!ReferenceEquals(queueSet, _hookedQueueSet))
            {
                if (_hookedQueueSet != null)
                    _hookedQueueSet.ActionEnqueued -= OnActionEnqueued;
                if (queueSet != null)
                    queueSet.ActionEnqueued += OnActionEnqueued;
                _hookedQueueSet = queueSet;
            }

            if (!_endTurnHooked)
            {
                CombatManager.Instance.PlayerEndedTurn += OnPlayerEndedTurn;
                _endTurnHooked = true;
            }
        }
        catch (Exception ex)
        {
            // Once, not every frame: a hook that cannot attach will not attach next frame either.
            if (!_actionHookFailureReported)
            {
                _actionHookFailureReported = true;
                GD.PrintErr($"[STS2 MCP] Could not attach the action log: {ex}");
            }
        }
    }

    /// <summary>
    /// A game action was queued. Must never throw: the game reports an exception from this event to its
    /// crash reporter.
    /// </summary>
    private static void OnActionEnqueued(GameAction action)
    {
        try
        {
            // Singleplayer only: in a multiplayer run the API's own moves come through
            // /api/v1/multiplayer, which does not raise _apiDepth, so they could not be told from the
            // player's. (IsMultiplayerRun is exact here: the hooks run on the main thread.)
            if (IsMultiplayerRun())
                return;

            switch (action)
            {
                case PlayCardAction play when LocalContext.IsMe(play.Player):
                {
                    var combatState = play.Player.Creature?.CombatState;
                    var target = SafeActionRead(() => play.Target);
                    var entry = NewActionLogEntry("play_card");
                    entry["card_id"] = SafeActionRead(() => play.CardModelId.Entry);
                    entry["card_uid"] = SafeActionRead(() => PlayedCardUid(play, combatState));
                    entry["target"] = TargetEntityId(target, combatState);
                    entry["target_combat_id"] = play.TargetId;
                    AppendActionLogEntry(entry);
                    break;
                }
                case UsePotionAction potion when LocalContext.IsMe(potion.Player):
                {
                    var combatState = potion.Player.Creature?.CombatState;
                    var target = potion.TargetId is uint targetId
                        ? SafeActionRead(() => combatState?.GetCreature(targetId))
                        : null;
                    var entry = NewActionLogEntry("use_potion");
                    entry["slot"] = (int)potion.PotionIndex;
                    entry["target"] = TargetEntityId(target, combatState);
                    entry["target_combat_id"] = potion.TargetId;
                    AppendActionLogEntry(entry);
                    break;
                }
                // In singleplayer only a click creates one: the End Turn button, or the tutorial popup's
                // stand-in for it. Its owner is all it exposes, which is enough.
                case EndPlayerTurnAction endTurn when LocalContext.NetId == endTurn.OwnerId:
                    AppendActionLogEntry(NewActionLogEntry("end_turn"));
                    break;
            }
        }
        catch (Exception ex)
        {
            ReportActionLogFailure(ex);
        }
    }

    /// <summary>
    /// A player's turn ended. Logs only the mod's own end_turn (ExecuteEndTurn sets _apiEndingTurn around
    /// its PlayerCmd.EndTurn call): the End Turn button's end was logged when its action was queued, and
    /// every other way this fires is the game ending the turn by itself, including from inside an API play
    /// (Void Form), which _apiDepth alone would mistake for the API's end_turn. Must never throw: the game
    /// raises this in the middle of ending the turn, and does not catch.
    /// </summary>
    private static void OnPlayerEndedTurn(Player player, bool canBackOut)
    {
        try
        {
            if (_apiEndingTurn && LocalContext.IsMe(player) && !IsMultiplayerRun())   // singleplayer only, as above
                AppendActionLogEntry(NewActionLogEntry("end_turn"));
        }
        catch (Exception ex)
        {
            ReportActionLogFailure(ex);
        }
    }

    /// <summary>
    /// A new entry: seq (filled in by AppendActionLogEntry, first so it leads the JSON object), kind,
    /// source, the UTC time, and act/floor/round from the same objects the state reads them from.
    /// </summary>
    private static Dictionary<string, object?> NewActionLogEntry(string kind)
    {
        var runState = SafeActionRead(() => RunManager.Instance.DebugOnlyGetState());
        int? round = SafeActionRead(() => CombatManager.Instance.IsInProgress
            ? CombatManager.Instance.DebugOnlyGetState()?.RoundNumber
            : null);
        return new Dictionary<string, object?>
        {
            ["seq"] = 0L,
            ["kind"] = kind,
            ["source"] = ActionSource(),
            ["utc"] = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["act"] = runState != null ? runState.CurrentActIndex + 1 : null,
            ["floor"] = runState?.TotalFloor,
            ["round"] = round
        };
    }

    /// <summary>The played card's uid (the one the state reports), when the card can still be found.</summary>
    private static string? PlayedCardUid(PlayCardAction play, ICombatState? combatState)
    {
        var card = play.NetCombatCard.ToCardModelOrNull();
        if (card == null || combatState == null)
            return null;
        RefreshCardUidRegistry(combatState);
        return GetStableCardUid(card);
    }

    /// <summary>
    /// The target's entity_id, as battle.enemies / battle.allies report it; null for no target or a
    /// target that is not a monster (a potion used on yourself).
    /// </summary>
    private static string? TargetEntityId(Creature? target, ICombatState? combatState)
    {
        if (target?.Monster == null || combatState == null)
            return null;
        return SafeActionRead(() =>
        {
            RefreshEntityIdRegistry(combatState);
            return GetStableEntityId(target);
        });
    }

    /// <summary>One field of an entry: a read that fails leaves that field null, never the entry out.</summary>
    private static T? SafeActionRead<T>(Func<T?> read)
    {
        try { return read(); }
        catch (Exception ex)
        {
            ReportActionLogFailure(ex);
            return default;
        }
    }

    private static void ReportActionLogFailure(Exception ex)
    {
        if (_actionLogFailureReported)
            return;
        _actionLogFailureReported = true;
        GD.PrintErr($"[STS2 MCP] Action log: {ex}");
    }

    /// <summary>Stamps the entry with the next seq and appends it, dropping the oldest past the capacity.</summary>
    private static long AppendActionLogEntry(Dictionary<string, object?> entry)
    {
        lock (_logLock)
        {
            long seq = ++_actionSeq;
            entry["seq"] = seq;
            _actionLog.Enqueue(entry);
            while (_actionLog.Count > ActionLogCapacity)
                _actionLog.Dequeue();
            return seq;
        }
    }

    /// <summary>The entries newer than <paramref name="since"/>, oldest first, and the newest seq.</summary>
    private static (long LatestSeq, List<Dictionary<string, object?>> Entries) ReadActionLog(long since)
    {
        lock (_logLock)
        {
            var entries = new List<Dictionary<string, object?>>();
            foreach (var entry in _actionLog)
            {
                if ((long)entry["seq"]! > since)
                    entries.Add(entry);
            }
            return (_actionSeq, entries);
        }
    }

    /// <summary>
    /// GET /api/v1/actions?since=&lt;seq&gt;. Answered from the log itself, not on the game's main
    /// thread, so it works (and never returns 503) while the game is loading or paused.
    /// </summary>
    private static void HandleGetActions(HttpListenerRequest request, HttpListenerResponse response)
    {
        bool pretty = WantsPretty(request);
        long since = 0;
        string? sinceText = request.QueryString["since"];
        if (!string.IsNullOrEmpty(sinceText)
            && !long.TryParse(sinceText, NumberStyles.None, CultureInfo.InvariantCulture, out since))
        {
            SendError(response, 400, "'since' must be a whole number of 0 or more: the seq of the last entry you have", pretty);
            return;
        }

        var (latestSeq, entries) = ReadActionLog(since);
        SendJson(response, new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["latest_seq"] = latestSeq,
            ["entries"] = entries
        }, pretty);
    }
}
