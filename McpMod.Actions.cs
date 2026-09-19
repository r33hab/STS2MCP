using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline.UnlockScreens;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using Godot;

namespace STS2_MCP;

public static partial class McpMod
{
    private static Dictionary<string, object?> ExecuteAction(string action, Dictionary<string, JsonElement> data)
    {
        if (!RunManager.Instance.IsInProgress)
            return Error("No run in progress");

        var runState = RunManager.Instance.DebugOnlyGetState()!;
        var player = LocalContext.GetMe(runState);
        if (player == null)
            return Error("Could not find local player");

        var tree = (Godot.Engine.GetMainLoop()) as SceneTree;
        if (tree?.Root != null && IsAnyFtueVisible(tree.Root))
            return Error("Blocking popup active. Use menu_select with one of the advertised popup options before gameplay actions.");

        return action switch
        {
            "play_card" => ExecutePlayCard(player, data),
            "use_potion" => ExecuteUsePotion(player, data),
            "discard_potion" => ExecuteDiscardPotion(player, data),
            "end_turn" => ExecuteEndTurn(player),
            "choose_map_node" => ExecuteChooseMapNode(data),
            "choose_event_option" => ExecuteChooseEventOption(data),
            "advance_dialogue" => ExecuteAdvanceDialogue(),
            "choose_rest_option" => ExecuteChooseRestOption(data),
            "shop_purchase" => ExecuteShopPurchase(player, data),
            "claim_reward" => ExecuteClaimReward(data),
            "select_card_reward" => ExecuteSelectCardReward(data),
            "skip_card_reward" => ExecuteSkipCardReward(),
            "select_card_reward_alternative" => ExecuteSelectCardRewardAlternative(data),
            "proceed" => ExecuteProceed(),
            "select_card" => ExecuteSelectCard(data),
            "confirm_selection" => ExecuteConfirmSelection(),
            "cancel_selection" => ExecuteCancelSelection(),
            "select_bundle" => ExecuteSelectBundle(data),
            "confirm_bundle_selection" => ExecuteConfirmBundleSelection(),
            "cancel_bundle_selection" => ExecuteCancelBundleSelection(),
            "combat_select_card" => ExecuteCombatSelectCard(data),
            "combat_confirm_selection" => ExecuteCombatConfirmSelection(),
            "select_relic" => ExecuteSelectRelic(data),
            "skip_relic_selection" => ExecuteSkipRelicSelection(),
            "claim_treasure_relic" => ExecuteClaimTreasureRelic(data),
            "crystal_sphere_set_tool" => ExecuteCrystalSphereSetTool(data),
            "crystal_sphere_click_cell" => ExecuteCrystalSphereClickCell(data),
            "crystal_sphere_proceed" => ExecuteCrystalSphereProceed(),
            _ => Error($"Unknown action: {action}")
        };
    }

    /// <summary>
    /// Reads an integer parameter, accepting any of the given names.
    /// Actions historically disagreed on whether the card index was called
    /// "card_index" or "index", which produced a run of silent no-ops and
    /// "Missing 'index'" errors for callers that guessed the other spelling.
    /// Every name listed here is accepted; the first one is what the error names.
    /// </summary>
    private static bool TryGetIntParam(
        Dictionary<string, JsonElement> data,
        out int value,
        params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetValue(name, out var elem))
                continue;
            if (elem.ValueKind == JsonValueKind.Number && elem.TryGetInt32(out value))
                return true;
            if (elem.ValueKind == JsonValueKind.String
                && int.TryParse(elem.GetString(), out value))
                return true;
        }
        value = 0;
        return false;
    }

    private static Dictionary<string, object?> MissingIntParam(string description, params string[] names)
        => Error($"Missing '{names[0]}' ({description}). Accepted names: {string.Join(", ", names)}");

    /// <summary>Reads a string parameter under any of the given names.</summary>
    private static string? TryReadStringParam(Dictionary<string, JsonElement> data, params string[] names)
    {
        foreach (var name in names)
        {
            if (!data.TryGetValue(name, out var elem))
                continue;
            if (elem.ValueKind != JsonValueKind.String)
                continue;
            var value = elem.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    /// <summary>
    /// Reads the 'target' parameter as a string, accepting a bare JSON number so that
    /// a combat_id can be passed as either 3 or "3". GetString() throws on a number,
    /// which used to surface as a 500 rather than a usable error.
    /// Returns null when no usable target was supplied.
    /// </summary>
    private static string? TryReadTargetId(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("target", out var elem))
            return null;

        string? raw = elem.ValueKind switch
        {
            JsonValueKind.String => elem.GetString(),
            JsonValueKind.Number => elem.ToString(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    private static Dictionary<string, object?> ExecutePlayCard(Player player, Dictionary<string, JsonElement> data)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!IsPlayPhase(player))
            return Error("Not in play phase - cannot act during enemy turn");
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");
        if (!player.Creature.IsAlive)
            return Error("Player creature is dead - cannot play cards");

        var combatState = player.Creature.CombatState;
        if (combatState == null)
            return Error("No combat state");

        // Get card by index in hand
        var hand = player.PlayerCombatState?.Hand;
        if (hand == null)
            return Error("No hand available");

        // Prefer the uid: card_index shifts as soon as a card leaves the hand, so a
        // multi-card plan built from one state read goes wrong after the first play.
        CardModel card;
        string? cardUid = TryReadStringParam(data, "card_uid", "uid");
        if (cardUid != null)
        {
            RefreshCardUidRegistry(combatState);
            card = hand.Cards.FirstOrDefault(c => GetStableCardUid(c) == cardUid)!;
            if (card == null)
                return Error(
                    $"Card '{cardUid}' is not in hand. Hand: "
                    + string.Join(", ", hand.Cards.Select(GetStableCardUid)));
        }
        else
        {
            if (!TryGetIntParam(data, out int cardIndex, "card_index", "index"))
                return Error("Missing card selector. Provide 'card_uid' (preferred, stable across plays) or 'card_index' (position in hand).");
            if (cardIndex < 0 || cardIndex >= hand.Cards.Count)
                return Error($"card_index {cardIndex} out of range (hand has {hand.Cards.Count} cards)");
            card = hand.Cards[cardIndex];
        }

        if (!card.CanPlay(out var reason, out _))
            return Error($"Card '{card.Title}' cannot be played: {reason}");

        // Resolve target
        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy)
        {
            string? targetId = TryReadTargetId(data);
            if (targetId == null)
            {
                var alive = combatState.Enemies.Where(e => e.IsAlive).ToList();
                if (alive.Count != 1)
                    return Error(
                        $"Card '{card.Title}' requires a target. Provide 'target' with an entity_id "
                        + $"or combat_id ({alive.Count} enemies alive).");
                target = alive[0];
            }
            else
            {
                target = ResolveTarget(combatState, targetId);
                if (target == null)
                    return Error($"Target '{targetId}' not found among alive enemies");
            }
        }

        // Play the card via the action queue (same path as the game UI)
        RunManager.Instance.ActionQueueSynchronizer.RequestEnqueue(new PlayCardAction(card, target));

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Playing '{card.Title}'" + (target != null ? $" targeting {SafeGetText(() => target.Monster?.Title) ?? "target"}" : "")
        };
    }

    private static Dictionary<string, object?> ExecuteEndTurn(Player player)
    {
        if (!CombatManager.Instance.IsInProgress)
            return Error("Not in combat");
        if (!IsPlayPhase(player))
            return Error("Not in play phase - cannot act during enemy turn");
        if (CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled (turn may already be ending)");

        // Match the game's own CanTurnBeEnded guard (NEndTurnButton.cs:114-123)
        var hand = NCombatRoom.Instance?.Ui?.Hand;
        if (hand != null && (hand.InCardPlay || hand.CurrentMode != NPlayerHand.Mode.Play))
            return Error("Cannot end turn while a card is being played or hand is in selection mode");

        PlayerCmd.EndTurn(player, canBackOut: false);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Ending turn"
        };
    }

    /// <summary>
    /// "slot" is the fixed slot number on the potion belt, not an index into the
    /// non-empty potions. After using the potion in slot 0, the survivor stays in
    /// slot 1 — asking for slot 0 then has to say so, and say where the potions are.
    /// </summary>
    private static string DescribeEmptyPotionSlot(Player player, int slot)
    {
        var occupied = new List<string>();
        for (int i = 0; i < player.PotionSlots.Count; i++)
        {
            var p = player.GetPotionAtSlotIndex(i);
            if (p != null)
                occupied.Add($"{i}={SafeGetText(() => p.Title) ?? "?"}");
        }

        return occupied.Count == 0
            ? $"No potion in slot {slot} (no potions held)"
            : $"No potion in slot {slot}. Occupied slots: {string.Join(", ", occupied)}. "
              + "'slot' is the belt slot from state player.potions[].slot, not a position in the list.";
    }

    private static Dictionary<string, object?> ExecuteUsePotion(Player player, Dictionary<string, JsonElement> data)
    {
        if (!TryGetIntParam(data, out int slot, "slot", "potion_index", "index"))
            return MissingIntParam("potion slot index", "slot", "potion_index", "index");
        if (slot < 0 || slot >= player.PotionSlots.Count)
            return Error($"Potion slot {slot} out of range (player has {player.PotionSlots.Count} slots)");

        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion == null)
            return Error(DescribeEmptyPotionSlot(player, slot));
        if (potion.IsQueued)
            return Error($"Potion '{SafeGetText(() => potion.Title)}' is already queued for use");
        if (potion.Owner.Creature.IsDead)
            return Error("Cannot use potion - player creature is dead");
        if (!potion.PassesCustomUsabilityCheck)
            return Error($"Potion '{SafeGetText(() => potion.Title)}' cannot be used right now");

        bool inCombat = CombatManager.Instance.IsInProgress;
        if (potion.Usage == PotionUsage.CombatOnly)
        {
            if (!inCombat)
                return Error($"Potion '{SafeGetText(() => potion.Title)}' can only be used in combat");
            if (!IsPlayPhase(player))
                return Error("Cannot use potions outside of play phase");
        }
        else if (potion.Usage == PotionUsage.Automatic)
            return Error($"Potion '{SafeGetText(() => potion.Title)}' is automatic and cannot be manually used");

        if (inCombat && CombatManager.Instance.PlayerActionsDisabled)
            return Error("Player actions are currently disabled");

        // Resolve target
        Creature? target = null;
        var combatState = player.Creature.CombatState;

        switch (potion.TargetType)
        {
            case TargetType.AnyEnemy:
            {
                if (combatState == null)
                    return Error("No combat state for target resolution");

                string? targetId = TryReadTargetId(data);
                if (targetId == null)
                {
                    // With a single living enemy there is nothing to disambiguate, so
                    // pick it rather than refusing. Otherwise say so loudly: this used
                    // to fall through to target = null and the potion was consumed
                    // against nothing while the call reported success.
                    var alive = combatState.Enemies.Where(e => e.IsAlive).ToList();
                    if (alive.Count != 1)
                        return Error(
                            $"Potion '{SafeGetText(() => potion.Title)}' requires a target enemy. "
                            + $"Provide 'target' with an entity_id or combat_id ({alive.Count} enemies alive).");
                    target = alive[0];
                    break;
                }

                target = ResolveTarget(combatState, targetId);
                if (target == null)
                    return Error($"Target '{targetId}' not found among alive enemies");
                break;
            }
            case TargetType.Self:
            case TargetType.AnyAlly:
            case TargetType.AnyPlayer:
                target = player.Creature;
                break;
            default:
                target = null;
                break;
        }

        if (target != null && !potion.IsValidTarget(target))
            return Error($"Potion '{SafeGetText(() => potion.Title)}' cannot target {SafeGetText(() => target.Monster?.Title) ?? "that creature"}");

        potion.EnqueueManualUse(target);

        string targetMsg = potion.TargetType switch
        {
            TargetType.AnyEnemy => $" targeting {SafeGetText(() => target?.Monster?.Title) ?? "enemy"}",
            TargetType.Self or TargetType.AnyPlayer or TargetType.AnyAlly => " on self",
            _ => ""
        };

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Using potion '{SafeGetText(() => potion.Title)}' from slot {slot}{targetMsg}"
        };
    }

    private static Dictionary<string, object?> ExecuteDiscardPotion(Player player, Dictionary<string, JsonElement> data)
    {
        if (!TryGetIntParam(data, out int slot, "slot", "potion_index", "index"))
            return MissingIntParam("potion slot index", "slot", "potion_index", "index");
        if (slot < 0 || slot >= player.PotionSlots.Count)
            return Error($"Potion slot {slot} out of range (player has {player.PotionSlots.Count} slots)");

        var potion = player.GetPotionAtSlotIndex(slot);
        if (potion == null)
            return Error(DescribeEmptyPotionSlot(player, slot));

        string potionName = SafeGetText(() => potion.Title) ?? "unknown";
        _ = PotionCmd.Discard(potion);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Discarded potion '{potionName}' from slot {slot}"
        };
    }

    private static Dictionary<string, object?> ExecuteChooseEventOption(Dictionary<string, JsonElement> data)
    {
        var uiRoom = NEventRoom.Instance;
        if (uiRoom == null)
            return Error("Event room is not open");

        if (!TryGetIntParam(data, out int index, "index", "option_index"))
            return MissingIntParam("event option index", "index", "option_index");

        var buttons = FindAll<NEventOptionButton>(uiRoom);

        if (buttons.Count == 0)
            return Error("No event options available");
        if (index < 0 || index >= buttons.Count)
            return Error($"Event option index {index} out of range ({buttons.Count} options)");

        var button = buttons[index];
        if (button.Option.IsLocked)
            return Error($"Event option {index} is locked");
        string title = SafeGetText(() => button.Option.Title) ?? "option";
        button.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Choosing event option: {title}"
        };
    }

    private static Dictionary<string, object?> ExecuteAdvanceDialogue()
    {
        var uiRoom = NEventRoom.Instance;
        if (uiRoom == null)
            return Error("Event room is not open");

        var ancientLayout = FindFirst<NAncientEventLayout>(uiRoom);
        if (ancientLayout == null)
            return Error("No ancient dialogue active");

        var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
        if (hitbox == null || !hitbox.Visible || !hitbox.IsEnabled)
            return Error("Dialogue hitbox not available - dialogue may have ended");

        hitbox.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Advancing dialogue"
        };
    }

    private static Dictionary<string, object?> ExecuteChooseRestOption(Dictionary<string, JsonElement> data)
    {
        if (!TryGetIntParam(data, out int index, "index", "option_index"))
            return MissingIntParam("rest site option index", "index", "option_index");

        var restRoom = NRestSiteRoom.Instance;
        if (restRoom == null)
            return Error("Rest site room is not open");

        var buttons = FindAll<NRestSiteButton>(restRoom);

        if (buttons.Count == 0)
            return Error("No rest site options available");
        if (index < 0 || index >= buttons.Count)
            return Error($"Rest option index {index} out of range ({buttons.Count} options)");

        var button = buttons[index];
        if (!button.Option.IsEnabled)
            return Error($"Rest option {index} ({button.Option.OptionId}) is disabled");
        string optionName = SafeGetText(() => button.Option.Title) ?? button.Option.OptionId;
        button.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Selecting rest site option: {optionName}"
        };
    }

    private static Dictionary<string, object?> ExecuteShopPurchase(Player player, Dictionary<string, JsonElement> data)
    {
        MerchantInventory? inventory = null;

        if (player.RunState.CurrentRoom is MerchantRoom merchantRoom)
        {
            // Regular merchant - auto-open inventory if needed
            var merchUI = NMerchantRoom.Instance;
            if (merchUI?.Inventory != null && !merchUI.Inventory.IsOpen)
                merchUI.OpenInventory();
            // v0.107 split the merchant into per-player inventories; take the local one.
            inventory = merchantRoom.GetLocalInventory();
        }
        else if (player.RunState.CurrentRoom is EventRoom eventRoom
                 && eventRoom.CanonicalEvent is FakeMerchant
                 && (eventRoom.LocalMutableEvent ?? eventRoom.CanonicalEvent) is FakeMerchant fakeMerchant)
        {
            // Fake merchant event - auto-open via button if needed
            if (!fakeMerchant.StartedFight)
            {
                var uiRoom = NEventRoom.Instance;
                if (uiRoom != null)
                {
                    var fmNode = FindFirst<NFakeMerchant>(uiRoom);
                    if (fmNode != null)
                    {
                        var inventoryUI = FindFirst<NMerchantInventory>(fmNode);
                        if (inventoryUI != null && !inventoryUI.IsOpen)
                        {
                            var btn = fmNode.MerchantButton;
                            if (btn != null && btn.Visible && btn.IsEnabled)
                                btn.ForceClick();
                        }
                    }
                }
            }
            inventory = fakeMerchant.Inventory;
        }
        else
        {
            return Error("Not in a shop");
        }

        if (inventory == null)
            return Error("Shop inventory not ready yet; wait a moment and retry");

        if (!TryGetIntParam(data, out int index, "index", "item_index"))
            return MissingIntParam("shop item index", "index", "item_index");

        var allEntries = inventory.AllEntries.ToList();
        if (index < 0 || index >= allEntries.Count)
            return Error($"Shop item index {index} out of range ({allEntries.Count} items)");

        var entry = allEntries[index];
        if (!entry.IsStocked)
            return Error("Item is sold out");
        if (!entry.EnoughGold)
            return Error($"Not enough gold (need {entry.Cost}, have {player.Gold})");

        // Fire-and-forget purchase (same path as AutoSlay)
        _ = entry.OnTryPurchaseWrapper(inventory);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Purchasing item for {entry.Cost} gold"
        };
    }

    private static Dictionary<string, object?> ExecuteChooseMapNode(Dictionary<string, JsonElement> data)
    {
        var mapScreen = NMapScreen.Instance;
        if (mapScreen == null || (!mapScreen.IsOpen && !IsNodeVisible(mapScreen)))
            return Error("Map screen is not open");

        var travelable = FindAll<NMapPoint>(mapScreen)
            .Where(mp => mp.State == MapPointState.Travelable && mp.Point != null)
            .OrderBy(mp => mp.Point!.coord.col)
            .ToList();

        if (travelable.Count == 0)
            return Error("No travelable map nodes available");

        // Coordinates are the safe way to name a node: "index" is a position in the
        // current option list, so it shifts whenever the set of travelable nodes does,
        // and a stale index silently routes the run down the wrong branch.
        bool hasCol = TryGetIntParam(data, out int col, "col");
        bool hasRow = TryGetIntParam(data, out int row, "row");
        NMapPoint target;

        if (hasCol && hasRow)
        {
            target = travelable.FirstOrDefault(mp =>
                mp.Point!.coord.col == col && mp.Point!.coord.row == row)!;
            if (target == null)
                return Error(
                    $"No travelable node at ({col},{row}). Travelable: "
                    + string.Join(", ", travelable.Select(mp => $"{mp.Point!.PointType} ({mp.Point!.coord.col},{mp.Point!.coord.row})")));
        }
        else if (hasCol || hasRow)
        {
            return Error("Provide both 'col' and 'row' to choose a node by coordinate, or 'index' to choose by list position.");
        }
        else
        {
            if (!TryGetIntParam(data, out int index, "index", "node_index"))
                return Error("Missing node selector. Provide 'col' and 'row' (preferred, stable), or 'index' (position in next_options).");
            if (index < 0 || index >= travelable.Count)
                return Error($"Map node index {index} out of range ({travelable.Count} options available)");
            target = travelable[index];
        }

        var pt = target.Point!;
        mapScreen.OnMapPointSelectedLocally(target);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Traveling to {pt.PointType} at ({pt.coord.col},{pt.coord.row})"
        };
    }

    private static Dictionary<string, object?> ExecuteClaimReward(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NRewardsScreen rewardsScreen)
            return Error("Rewards screen is not open");

        if (!TryGetIntParam(data, out int index, "index", "reward_index"))
            return MissingIntParam("reward index", "index", "reward_index");

        var enabledButtons = FindAll<NRewardButton>(rewardsScreen)
            .Where(b => b.IsEnabled && b.Reward != null)
            .ToList();

        if (index < 0 || index >= enabledButtons.Count)
            return Error($"Reward index {index} out of range (screen has {enabledButtons.Count} claimable rewards)");

        var button = enabledButtons[index];
        var reward = button.Reward!;
        string rewardDesc = GetRewardTypeName(reward);
        if (reward is GoldReward g)
            rewardDesc = $"gold ({g.Amount})";
        else if (reward is PotionReward p)
        {
            rewardDesc = $"potion ({SafeGetText(() => p.Potion?.Title)})";

            // The game silently refuses a potion reward when the belt is full: the
            // button stays enabled, the reward stays in the list, and nothing happens.
            // Reporting that as "ok" turned a full belt into an infinite claim loop.
            var localPlayer = LocalContext.GetMe(RunManager.Instance.DebugOnlyGetState()!);
            if (localPlayer != null && !localPlayer.HasOpenPotionSlots)
                return Error(
                    $"Potion slots full ({localPlayer.PotionSlots.Count(slot => slot != null)}/{localPlayer.MaxPotionCount}); "
                    + $"cannot claim {rewardDesc}. Use discard_potion first, or claim the other rewards and proceed.");
        }
        else if (reward is CardReward)
            rewardDesc = "card (opens card selection)";

        button.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Claiming reward: {rewardDesc}"
        };
    }

    private static Dictionary<string, object?> ExecuteSelectCardReward(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCardRewardSelectionScreen cardScreen)
            return Error("Card reward selection screen is not open");

        if (!TryGetIntParam(data, out int cardIndex, "card_index", "index"))
            return MissingIntParam("index of the offered card", "card_index", "index");

        var cardHolders = FindAllSortedByPosition<NCardHolder>(cardScreen);
        if (cardIndex < 0 || cardIndex >= cardHolders.Count)
            return Error($"Card index {cardIndex} out of range (screen has {cardHolders.Count} cards)");

        var holder = cardHolders[cardIndex];
        string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Selecting card: {cardName}"
        };
    }

    private static Dictionary<string, object?> ExecuteSkipCardReward()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCardRewardSelectionScreen cardScreen)
            return Error("Card reward selection screen is not open");

        var alternatives = BuildCardRewardAlternatives(cardScreen);
        if (alternatives.Count == 0)
            return Error("No skip option available on this card reward");

        // Pick the Skip option by id rather than "whatever button is first". A relic
        // can add its own alternative (Pael's Wing's SACRIFICE), and clicking the wrong
        // one here silently spends the reward on something else.
        int skipIndex = alternatives.FindIndex(a =>
            string.Equals(a.GetValueOrDefault("option_id")?.ToString(), "Skip", System.StringComparison.OrdinalIgnoreCase));
        if (skipIndex < 0)
            return Error(
                "No Skip option on this card reward. Available: "
                + DescribeAlternatives(alternatives)
                + ". Use select_card_reward_alternative.");

        return ClickCardRewardAlternative(cardScreen, alternatives, skipIndex);
    }

    /// <summary>
    /// Takes one of the card reward's alternative options by id (preferred) or index.
    ///
    /// Skip used to be the only one reachable, so a relic that adds its own alternative
    /// was unusable from the API — Pael's Wing turns a card reward into progress towards
    /// a relic, and skip_card_reward does not count as sacrificing, so the relic did
    /// nothing at all for a whole run.
    /// </summary>
    private static Dictionary<string, object?> ExecuteSelectCardRewardAlternative(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCardRewardSelectionScreen cardScreen)
            return Error("Card reward selection screen is not open");

        var alternatives = BuildCardRewardAlternatives(cardScreen);
        if (alternatives.Count == 0)
            return Error("This card reward has no alternative options");

        string? optionId = TryReadStringParam(data, "option_id", "option", "alternative");
        int chosen;

        if (optionId != null)
        {
            chosen = alternatives.FindIndex(a =>
                string.Equals(a.GetValueOrDefault("option_id")?.ToString(), optionId, System.StringComparison.OrdinalIgnoreCase)
                || string.Equals(a.GetValueOrDefault("title")?.ToString(), optionId, System.StringComparison.OrdinalIgnoreCase));
            if (chosen < 0)
                return Error($"No alternative '{optionId}' on this card reward. Available: {DescribeAlternatives(alternatives)}");
        }
        else
        {
            if (!TryGetIntParam(data, out chosen, "index", "option_index"))
                return Error($"Missing 'option_id' (or 'index'). Available: {DescribeAlternatives(alternatives)}");
            if (chosen < 0 || chosen >= alternatives.Count)
                return Error($"Alternative index {chosen} out of range ({alternatives.Count} options). Available: {DescribeAlternatives(alternatives)}");
        }

        return ClickCardRewardAlternative(cardScreen, alternatives, chosen);
    }

    private static Dictionary<string, object?> ClickCardRewardAlternative(
        NCardRewardSelectionScreen cardScreen,
        List<Dictionary<string, object?>> alternatives,
        int index)
    {
        var buttons = FindAll<NCardRewardAlternativeButton>(cardScreen);
        if (index < 0 || index >= buttons.Count)
            return Error($"Alternative index {index} is no longer on screen; re-read state");

        var button = buttons[index];
        if (!button.IsEnabled)
            return Error($"Alternative '{alternatives[index].GetValueOrDefault("option_id")}' is disabled");

        button.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Taking card reward alternative: {alternatives[index].GetValueOrDefault("title") ?? alternatives[index].GetValueOrDefault("option_id")}",
            ["option_id"] = alternatives[index].GetValueOrDefault("option_id")
        };
    }

    private static string DescribeAlternatives(List<Dictionary<string, object?>> alternatives)
        => string.Join(", ", alternatives.Select(a => $"[{a.GetValueOrDefault("index")}] {a.GetValueOrDefault("option_id")}"));

    private static Dictionary<string, object?> ExecuteProceed()
    {
        // Try rewards overlay
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NRewardsScreen rewardsScreen)
        {
            var btn = FindFirst<NProceedButton>(rewardsScreen);
            if (btn is { IsEnabled: true })
            {
                btn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Proceeding from rewards" };
            }
        }

        // Try rest site
        if (NRestSiteRoom.Instance is { } restRoom && restRoom.ProceedButton.IsEnabled)
        {
            restRoom.ProceedButton.ForceClick();
            return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Proceeding from rest site" };
        }

        // Try merchant - close inventory first if open, then proceed
        if (NMerchantRoom.Instance is { } merchRoom)
        {
            if (merchRoom.Inventory?.IsOpen == true)
            {
                var backBtn = FindFirst<NBackButton>(merchRoom);
                if (backBtn is { IsEnabled: true })
                    backBtn.ForceClick();
            }
            if (merchRoom.ProceedButton.IsEnabled)
            {
                merchRoom.ProceedButton.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Proceeding from shop" };
            }
        }

        // Try fake merchant - close inventory first if open, then proceed
        if (NEventRoom.Instance is { } evtRoom)
        {
            var fmNode = FindFirst<NFakeMerchant>(evtRoom);
            if (fmNode != null)
            {
                var fmInventory = FindFirst<NMerchantInventory>(fmNode);
                if (fmInventory is { IsOpen: true })
                {
                    var backBtn = FindFirst<NBackButton>(fmNode);
                    if (backBtn is { IsEnabled: true })
                        backBtn.ForceClick();
                }
                var proceedBtn = FindFirst<NProceedButton>(fmNode);
                if (proceedBtn is { IsEnabled: true })
                {
                    proceedBtn.ForceClick();
                    return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Proceeding from fake merchant" };
                }
            }
        }

        // Try treasure room
        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);
        if (treasureUI != null && treasureUI.ProceedButton.IsEnabled)
        {
            treasureUI.ProceedButton.ForceClick();
            return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Proceeding from treasure room" };
        }

        return Error("No proceed button available or enabled");
    }

    private static Dictionary<string, object?> ExecuteSelectCard(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();

        if (!TryGetIntParam(data, out int index, "index", "card_index"))
            return MissingIntParam("card index in the grid", "index", "card_index");

        if (overlay is NCardGridSelectionScreen gridScreen)
        {
            var grid = FindFirst<NCardGrid>(gridScreen);
            if (grid == null)
                return Error("Card grid not found in selection screen");

            var holders = FindAllSortedByPosition<NGridCardHolder>(gridScreen);
            if (index < 0 || index >= holders.Count)
                return Error($"Card index {index} out of range ({holders.Count} cards available)");

            var holder = holders[index];
            string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";
            grid.EmitSignal(NCardGrid.SignalName.HolderPressed, holder);

            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Toggling card selection: {cardName}"
            };
        }
        else if (overlay is NChooseACardSelectionScreen chooseScreen)
        {
            var holders = FindAllSortedByPosition<NGridCardHolder>(chooseScreen);
            if (index < 0 || index >= holders.Count)
                return Error($"Card index {index} out of range ({holders.Count} cards available)");

            var holder = holders[index];
            string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";
            holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);

            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Choosing card: {cardName}"
            };
        }

        return Error("No card selection screen is open");
    }

    private static Dictionary<string, object?> ExecuteConfirmSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is NChooseACardSelectionScreen)
            return Error("Choose-a-card screen requires no confirmation - use select_card(index) to pick directly");
        if (overlay is not NCardGridSelectionScreen screen)
            return Error("No card selection screen is open");

        // Check all preview containers (upgrade uses UpgradeSinglePreviewContainer / UpgradeMultiPreviewContainer,
        // NDeckCardSelectScreen uses PreviewContainer with %PreviewConfirm)
        foreach (var containerName in new[] { "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer", "%PreviewContainer" })
        {
            var container = screen.GetNodeOrNull<Godot.Control>(containerName);
            if (container?.Visible == true)
            {
                var confirm = container.GetNodeOrNull<NConfirmButton>("Confirm")
                              ?? container.GetNodeOrNull<NConfirmButton>("%PreviewConfirm");
                if (confirm is { IsEnabled: true })
                {
                    confirm.ForceClick();
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "ok",
                        ["message"] = "Confirming selection from preview"
                    };
                }
            }
        }

        // Try main confirm button
        var mainConfirm = screen.GetNodeOrNull<NConfirmButton>("Confirm")
                          ?? screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (mainConfirm is { IsEnabled: true })
        {
            mainConfirm.ForceClick();
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Confirming selection"
            };
        }

        // Fallback: find ANY enabled NConfirmButton in the screen tree.
        // Covers NCardGridSelectionScreen subclasses (like NDeckEnchantSelectScreen)
        // whose confirm button isn't in any of the known container paths above.
        var allConfirmButtons = FindAll<NConfirmButton>(screen);
        foreach (var btn in allConfirmButtons)
        {
            if (btn.IsEnabled && btn.IsVisibleInTree())
            {
                btn.ForceClick();
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = "Confirming selection"
                };
            }
        }

        return Error("No confirm button is currently enabled - select more cards first");
    }

    private static Dictionary<string, object?> ExecuteCancelSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();

        // Handle choose-a-card screen (skip button)
        if (overlay is NChooseACardSelectionScreen chooseScreen)
        {
            var skipButton = chooseScreen.GetNodeOrNull<NClickableControl>("SkipButton");
            if (skipButton is { IsEnabled: true })
            {
                skipButton.ForceClick();
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = "Skipping card choice"
                };
            }
            return Error("No skip option available - a card must be chosen");
        }

        if (overlay is not NCardGridSelectionScreen screen)
            return Error("No card selection screen is open");

        // If preview is showing, cancel back to selection
        foreach (var containerName in new[] { "%UpgradeSinglePreviewContainer", "%UpgradeMultiPreviewContainer", "%PreviewContainer" })
        {
            var container = screen.GetNodeOrNull<Godot.Control>(containerName);
            if (container?.Visible == true)
            {
                var cancelBtn = container.GetNodeOrNull<NBackButton>("Cancel")
                                ?? container.GetNodeOrNull<NBackButton>("%PreviewCancel");
                if (cancelBtn is { IsEnabled: true })
                {
                    cancelBtn.ForceClick();
                    return new Dictionary<string, object?>
                    {
                        ["status"] = "ok",
                        ["message"] = "Cancelling preview - returning to card selection"
                    };
                }
            }
        }

        // Close the screen entirely
        var closeButton = screen.GetNodeOrNull<NBackButton>("%Close");
        if (closeButton is { IsEnabled: true })
        {
            closeButton.ForceClick();
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Closing card selection screen"
            };
        }

        return Error("No cancel/close button is currently enabled - selection may be mandatory");
    }

    private static Dictionary<string, object?> ExecuteSelectBundle(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseABundleSelectionScreen screen)
            return Error("No bundle selection screen is open");

        if (!TryGetIntParam(data, out int index, "index", "bundle_index"))
            return MissingIntParam("bundle index", "index", "bundle_index");

        var previewContainer = screen.GetNodeOrNull<Godot.Control>("%BundlePreviewContainer");
        if (previewContainer?.Visible == true)
            return Error("A bundle preview is already open - confirm or cancel it first");

        var bundles = FindAll<NCardBundle>(screen);
        if (index < 0 || index >= bundles.Count)
            return Error($"Bundle index {index} out of range ({bundles.Count} bundles available)");

        bundles[index].Hitbox.ForceClick();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Selecting bundle {index}"
        };
    }

    private static Dictionary<string, object?> ExecuteConfirmBundleSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseABundleSelectionScreen screen)
            return Error("No bundle selection screen is open");

        var confirmButton = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        if (confirmButton is not { IsEnabled: true })
            return Error("Bundle confirm button is not enabled");

        confirmButton.ForceClick();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Confirming bundle selection"
        };
    }

    private static Dictionary<string, object?> ExecuteCancelBundleSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseABundleSelectionScreen screen)
            return Error("No bundle selection screen is open");

        var cancelButton = screen.GetNodeOrNull<NBackButton>("%Cancel");
        if (cancelButton is not { IsEnabled: true })
            return Error("Bundle cancel button is not enabled");

        cancelButton.ForceClick();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Cancelling bundle selection"
        };
    }

    private static Dictionary<string, object?> ExecuteCombatSelectCard(Dictionary<string, JsonElement> data)
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection)
            return Error("No in-combat card selection is active");

        if (!TryGetIntParam(data, out int index, "card_index", "index"))
            return MissingIntParam("index of the card in hand", "card_index", "index");

        var holders = hand.ActiveHolders;
        if (index < 0 || index >= holders.Count)
            return Error($"Card index {index} out of range ({holders.Count} selectable cards)");

        var holder = holders[index];
        string cardName = SafeGetText(() => holder.CardModel?.Title) ?? "unknown";

        // Emit the Pressed signal - same path the game UI uses
        holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Selecting card from hand: {cardName}"
        };
    }

    private static Dictionary<string, object?> ExecuteCombatConfirmSelection()
    {
        var hand = NPlayerHand.Instance;
        if (hand == null || !hand.IsInCardSelection)
            return Error("No in-combat card selection is active");

        var confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
        if (confirmBtn == null || !confirmBtn.IsEnabled)
            return Error("Confirm button is not enabled - select more cards first");

        confirmBtn.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Confirming combat card selection"
        };
    }

    private static Dictionary<string, object?> ExecuteSelectRelic(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseARelicSelection screen)
            return Error("No relic selection screen is open");

        if (!TryGetIntParam(data, out int index, "index", "relic_index"))
            return MissingIntParam("relic index", "index", "relic_index");

        var holders = FindAll<NRelicBasicHolder>(screen);
        if (index < 0 || index >= holders.Count)
            return Error($"Relic index {index} out of range ({holders.Count} relics available)");

        var holder = holders[index];
        string relicName = SafeGetText(() => holder.Relic?.Model?.Title) ?? "unknown";
        holder.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Selecting relic: {relicName}"
        };
    }

    private static Dictionary<string, object?> ExecuteSkipRelicSelection()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NChooseARelicSelection screen)
            return Error("No relic selection screen is open");

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        if (skipButton is not { IsEnabled: true })
            return Error("No skip option available");

        skipButton.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Skipping relic selection"
        };
    }

    private static Dictionary<string, object?> ExecuteClaimTreasureRelic(Dictionary<string, JsonElement> data)
    {
        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);
        if (treasureUI == null)
            return Error("Treasure room is not open");

        var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (relicCollection?.Visible != true)
            return Error("Relic collection is not visible - chest may not be opened yet");

        if (!TryGetIntParam(data, out int index, "index", "relic_index"))
            return MissingIntParam("relic index", "index", "relic_index");

        var holders = FindAll<NTreasureRoomRelicHolder>(relicCollection)
            .Where(h => h.IsEnabled && h.Visible)
            .ToList();

        if (index < 0 || index >= holders.Count)
            return Error($"Relic index {index} out of range ({holders.Count} relics available)");

        var holder = holders[index];
        string relicName = SafeGetText(() => holder.Relic?.Model?.Title) ?? "unknown";
        holder.ForceClick();

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Claiming treasure relic: {relicName}"
        };
    }

    private static Dictionary<string, object?> ExecuteCrystalSphereSetTool(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCrystalSphereScreen screen)
            return Error("Crystal Sphere screen is not open");

        if (!data.TryGetValue("tool", out var toolElem))
            return Error("Missing 'tool' (expected 'big' or 'small')");

        string tool = toolElem.GetString() ?? "";
        var button = tool switch
        {
            "big" => screen.GetNodeOrNull<NClickableControl>("%BigDivinationButton"),
            "small" => screen.GetNodeOrNull<NClickableControl>("%SmallDivinationButton"),
            _ => null
        };

        if (button == null)
            return Error($"Unknown Crystal Sphere tool: {tool}");
        if (!button.Visible || !button.IsEnabled)
            return Error($"Crystal Sphere tool '{tool}' is not available");

        button.ForceClick();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Setting Crystal Sphere tool to {tool}"
        };
    }

    private static Dictionary<string, object?> ExecuteCrystalSphereClickCell(Dictionary<string, JsonElement> data)
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCrystalSphereScreen screen)
            return Error("Crystal Sphere screen is not open");

        if (!data.TryGetValue("x", out var xElem))
            return Error("Missing 'x' (cell x-coordinate)");
        if (!data.TryGetValue("y", out var yElem))
            return Error("Missing 'y' (cell y-coordinate)");

        int x = xElem.GetInt32();
        int y = yElem.GetInt32();

        var cell = FindAll<NCrystalSphereCell>(screen)
            .FirstOrDefault(c => c.Entity.X == x && c.Entity.Y == y);
        if (cell == null)
            return Error($"Crystal Sphere cell ({x}, {y}) was not found");
        if (!cell.Entity.IsHidden || !cell.Visible)
            return Error($"Crystal Sphere cell ({x}, {y}) is not clickable");

        cell.EmitSignal(NClickableControl.SignalName.Released, cell);
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Clicking Crystal Sphere cell ({x}, {y})"
        };
    }

    private static Dictionary<string, object?> ExecuteCrystalSphereProceed()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay is not NCrystalSphereScreen screen)
            return Error("Crystal Sphere screen is not open");

        var proceedButton = FindCrystalSphereProceedButton(screen);
        if (proceedButton == null)
            return Error("Crystal Sphere proceed button is not enabled");

        proceedButton.ForceClick();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = "Proceeding from Crystal Sphere"
        };
    }

    private static NProceedButton? FindCrystalSphereProceedButton(NCrystalSphereScreen screen)
    {
        var namedButton = screen.GetNodeOrNull<NProceedButton>("%ProceedButton");
        if (IsControlVisibleOrActionable(namedButton))
            return namedButton;

        return FindAll<NProceedButton>(screen)
            .FirstOrDefault(IsControlVisibleOrActionable);
    }

    private static Creature? ResolveTarget(ICombatState combatState, string entityId)
    {
        // Try to match by entity_id pattern: "model_entry_N"
        // First try matching by combat_id if it's a pure number
        if (uint.TryParse(entityId, out uint combatId))
            return combatState.GetCreature(combatId);

        // Match by entity_id (e.g., "jaw_worm_0"). Ids come from the same registry the
        // state builder uses, so they keep pointing at the creature they were read for
        // even if another enemy died in between.
        RefreshEntityIdRegistry(combatState);
        foreach (var creature in combatState.Enemies)
        {
            if (!creature.IsAlive) continue;
            if (GetStableEntityId(creature) == entityId)
                return creature;
        }

        return null;
    }

    /// <summary>
    /// Reveals every obtained-but-unrevealed epoch, opening the Timeline if needed.
    ///
    /// A finished run leaves its epochs "Obtained". Until they are revealed the main
    /// menu hides `singleplayer` entirely, so the API could not start the next run —
    /// the single thing that made unattended play impossible, because every run ended
    /// in a screenshot and a hand-placed click.
    ///
    /// One step per call so the caller can poll; the response carries `done` and
    /// `pending_epoch_ids`. Reveals go through NEpochSlot.RevealEpoch, the same method
    /// the slot's own click handler calls, so the epoch's unlocks are granted and the
    /// progress file is written exactly as a manual reveal would.
    /// </summary>
    internal static Dictionary<string, object?> ExecuteTimelineRevealEpochs()
    {
        var tree = (Engine.GetMainLoop()) as SceneTree;
        if (tree?.Root == null)
            return Error("Cannot access scene tree");

        var pending = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");

        var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
        bool timelineVisible = timelineScreen != null && IsNodeVisible(timelineScreen);

        if (!timelineVisible)
        {
            if (pending.Count == 0)
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = "No epochs pending reveal",
                    ["pending_epoch_ids"] = pending,
                    ["done"] = true
                };

            var mainMenu = FindFirst<NMainMenu>(tree.Root);
            if (mainMenu == null)
                return Error("Not on the main menu; open the main menu before revealing epochs");

            var opened = ClickMenuButtonField(mainMenu, "_timelineButton", "Opening timeline to reveal epochs");
            if ((string?)opened["status"] == "error")
                return opened;

            opened["pending_epoch_ids"] = pending;
            opened["done"] = false;
            opened["retry"] = true;
            return opened;
        }

        // Dismiss the tutorial, any open unlock/inspect screen, and drain the queue
        // before looking for the next slot. ExecuteMenuSelect("advance") already
        // encodes that order, and finishes by revealing one slot.
        var step = ExecuteMenuSelect("advance");
        bool done = step.TryGetValue("done", out var d) && d is true;

        pending = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");
        step["pending_epoch_ids"] = pending;

        if (done && pending.Count == 0)
        {
            // Leave the timeline so the caller lands back on a main menu that has
            // `singleplayer` again.
            var back = ExecuteMenuSelect("back");
            step["message"] = "All epochs revealed; returned to the main menu";
            step["left_timeline"] = (string?)back["status"] == "ok";
            step["done"] = true;
            return step;
        }

        step["done"] = false;
        step["retry"] = true;
        return step;
    }

    /// <summary>
    /// Reveals the first epoch slot that is obtained but not yet revealed.
    /// Returns null when there is nothing left to reveal.
    /// </summary>
    private static Dictionary<string, object?>? TryRevealOneEpochSlot(NTimelineScreen timelineScreen)
    {
        var pending = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");
        if (pending.Count == 0)
            return null;

        // Look for the slot first. IsTimelineScreenBusy is a coarse heuristic (it reads
        // _isUiVisible and the input blocker) and reports "busy" on a timeline that is
        // sitting still and perfectly interactive, so it must not gate the reveal —
        // only explain why no slot is available yet.
        var slot = FindAll<NEpochSlot>(timelineScreen)
            .FirstOrDefault(s => s.State == EpochSlotState.Obtained);

        if (slot == null)
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = IsTimelineScreenBusy(timelineScreen)
                    ? "Timeline is still animating; retry after the next state poll"
                    : "Epochs are obtained but no revealable slot is mounted on the timeline yet; retry after the next state poll",
                ["pending_epoch_ids"] = pending,
                ["retry"] = true
            };

        string epochId = SafeGetText(() => slot.model?.Id) ?? "unknown";

        // Mirror NEpochSlot.OnRelease for the Obtained branch: block input, drop focus,
        // then reveal. RevealEpoch writes the progress state, opens the inspect screen
        // and runs the unlock animation, which is what actually grants the epoch's cards
        // and relics. It is private only because nothing but the click handler calls it.
        try
        {
            timelineScreen.DisableInput();
            slot.GetViewport()?.GuiReleaseFocus();

            var reveal = typeof(NEpochSlot).GetMethod(
                "RevealEpoch",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (reveal == null)
            {
                timelineScreen.EnableInput();
                return Error("NEpochSlot.RevealEpoch is missing - the game's timeline API changed");
            }

            reveal.Invoke(slot, null);
        }
        catch (System.Exception ex)
        {
            try { timelineScreen.EnableInput(); } catch { }
            return Error($"Failed to reveal epoch '{epochId}': {ex.InnerException?.Message ?? ex.Message}");
        }

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Revealing epoch {epochId}",
            ["revealed_epoch_id"] = epochId,
            ["pending_epoch_ids"] = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot"),
            ["retry"] = true
        };
    }

    internal static Dictionary<string, object?> ExecuteMenuSelect(string option, string? seed = null, int? ascension = null)
    {
        option = option.Trim();

        // An empty option is allowed when there is another instruction to carry out
        // (currently: setting the ascension level on character select).
        if (string.IsNullOrEmpty(option) && ascension == null)
            return Error("Missing menu option");

        var tree = (Engine.GetMainLoop()) as SceneTree;
        if (tree?.Root == null)
            return Error("Cannot access scene tree");

        // Game over screen. Prefer the active overlay stack entry so hidden or
        // preloaded game-over nodes cannot hijack normal menu navigation.
        var gameOver = NOverlayStack.Instance?.Peek() as NGameOverScreen;
        gameOver ??= FindAll<NGameOverScreen>(tree.Root).FirstOrDefault(IsNodeVisible);
        if (gameOver != null)
        {
            if (string.Equals(option, "continue", System.StringComparison.OrdinalIgnoreCase))
                return Error("Game-over option 'continue' is not actionable. Use: main_menu");

            if (!string.Equals(option, "main_menu", System.StringComparison.OrdinalIgnoreCase))
                return Error($"Unknown game over option: {option}. Use: main_menu");

            var result = ClickMenuButtonField(gameOver, "_mainMenuButton", "Returning to main menu");
            if ((string?)result["status"] == "ok")
                return result;

            var returnMethod = gameOver.GetType().GetMethod(
                "ReturnToMainMenu",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (returnMethod != null)
            {
                returnMethod.Invoke(gameOver, null);
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Returning to main menu" };
            }

            return Error("Game-over main_menu option is not available");
        }

        // Tutorial/FTUE popup - "Enable Tutorials?" dialog
        var tutorialFtue = FindVisibleAcceptTutorialsFtue(tree.Root);
        if (tutorialFtue != null && IsFtueNodeActive(tutorialFtue))
        {
            if (!string.Equals(option, "yes", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(option, "no", System.StringComparison.OrdinalIgnoreCase))
            {
                return Error($"Unknown tutorial prompt option: {option}. Use: yes, no");
            }

            var popup = tutorialFtue.GetType().GetField("_verticalPopup", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tutorialFtue);
            if (popup != null)
            {
                var isYes = string.Equals(option, "yes", System.StringComparison.OrdinalIgnoreCase);
                var btnField = isYes ? "<YesButton>k__BackingField" : "<NoButton>k__BackingField";
                var btn = popup.GetType().GetField(btnField, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(popup);
                if (btn is NClickableControl clickable &&
                    IsPopupButtonActionable(clickable))
                {
                    clickable.ForceClick();
                    return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = $"Tutorials: {(isYes ? "enabled" : "disabled")}" };
                }
            }
            return Error("Tutorial popup visible but buttons not accessible");
        }

        // Any other FTUE/tutorial popup with a confirm button.
        var ftue = FindVisibleGenericFtue(tree.Root);
        if (ftue != null)
        {
            if (!string.Equals(option, "advance", System.StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(option, "proceed", System.StringComparison.OrdinalIgnoreCase))
            {
                return Error($"Unknown tutorial option: {option}. Use: advance, proceed");
            }

            var ftueClickable = FindFtueAdvanceButton(ftue);
            if (ftueClickable != null)
            {
                ftueClickable.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Dismissed tutorial popup" };
            }

            return Error("Tutorial popup visible but no actionable advance/proceed control is available; retry after the next state poll");
        }

        // Generic blocking menu popups, such as the first-run warning that appears
        // over character select on fresh profiles.
        var verticalPopup = FindVisibleVerticalPopup(tree.Root);
        if (verticalPopup != null)
            return ExecutePopupOption(GetPopupOptions(verticalPopup), option);

        var popupButtonOptions = GetVisiblePopupButtonOptions(tree.Root);
        if (popupButtonOptions.Count > 0)
            return ExecutePopupOption(popupButtonOptions, option);

        // Timeline screen - advance through epoch reveals.
        var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
        if (timelineScreen != null && IsNodeVisible(timelineScreen))
        {
            if (string.Equals(option, "advance", System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(option, "proceed", System.StringComparison.OrdinalIgnoreCase))
            {
                // Check for timeline tutorial screen (first-time "Proceed" button).
                var tutorial = FindFirst<NTimelineTutorial>(tree.Root);
                if (tutorial != null && IsNodeVisible(tutorial))
                {
                    var ackBtn = tutorial.GetType().GetField("_acknowledgeButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(tutorial);
                    if (ackBtn is NClickableControl ackClickable &&
                        ackClickable.IsEnabled &&
                        IsNodeVisible(ackClickable))
                    {
                        ackClickable.ForceClick();
                        return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Clicked tutorial proceed button" };
                    }
                }

                // Check for confirm button
                var confirmBtn = FindFirst<NConfirmButton>(tree.Root);
                if (confirmBtn != null && IsNodeVisible(confirmBtn) && confirmBtn.IsEnabled)
                {
                    confirmBtn.ForceClick();
                    return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Clicked confirm button" };
                }

                // Check for any clickable proceed button
                var proceedBtn = FindFirst<NProceedButton>(tree.Root);
                if (proceedBtn != null && IsNodeVisible(proceedBtn) && proceedBtn.IsEnabled)
                {
                    proceedBtn.ForceClick();
                    return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Clicked proceed button" };
                }

                // Check for inspect screen (epoch detail view) - close it.
                var inspectScreen = FindFirst<NEpochInspectScreen>(tree.Root);
                if (inspectScreen != null && IsNodeVisible(inspectScreen))
                {
                    var closeBtn = inspectScreen.GetType().GetField("_closeButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(inspectScreen);
                    if (closeBtn is NClickableControl closeClickable &&
                        closeClickable.IsEnabled &&
                        IsNodeVisible(closeClickable))
                    {
                        closeClickable.ForceClick();
                        return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Closed epoch inspect screen" };
                    }
                    inspectScreen.Close();
                    return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Closed epoch inspect screen" };
                }

                // An unlock screen that is already open ("<character> is now playable",
                // "you unlocked these cards") blocks everything behind it until its
                // confirm button is pressed.
                var openUnlockResult = TryConfirmOpenUnlockScreen(timelineScreen);
                if (openUnlockResult != null)
                    return openUnlockResult;

                // Check for queued unlock screens.
                var queuedUnlockResult = TryHandleQueuedTimelineUnlock(timelineScreen);
                if (queuedUnlockResult != null)
                    return queuedUnlockResult;

                var revealResult = TryRevealOneEpochSlot(timelineScreen);
                if (revealResult != null)
                    return revealResult;

                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "No more epochs to advance", ["done"] = true };
            }
            else if (string.Equals(option, "back", System.StringComparison.OrdinalIgnoreCase))
            {
                // Try NBackButton directly on NTimelineScreen
                var backBtn = timelineScreen.GetType().GetField("_backButton", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(timelineScreen);
                if (backBtn is NClickableControl backClickable)
                {
                    if (backClickable.IsEnabled)
                    {
                        backClickable.ForceClick();
                        return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Going back from timeline" };
                    }
                }
                // Try the submenu stack
                var stack = timelineScreen.GetType().GetField("_stack", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(timelineScreen);
                if (stack != null)
                {
                    var popMethod = stack.GetType().GetMethod("Pop");
                    popMethod?.Invoke(stack, null);
                    return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Popped timeline from stack" };
                }
                return Error("Back button not available on timeline");
            }
            return Error($"Unknown timeline option: {option}. Use: advance, back");
        }

        // Multiplayer Join Friend screen — friend list, refresh, back, or join_<index>/<player_id>.
        var joinScreen = FindFirst<NJoinFriendScreen>(tree.Root);
        if (joinScreen != null && IsNodeVisible(joinScreen))
        {
            return ExecuteJoinScreenMenuOption(joinScreen, option);
        }

        // Multiplayer Load Game lobby — confirm/embark/unready/back to resume saved MP run.
        var loadLobby = FindFirst<NMultiplayerLoadGameScreen>(tree.Root);
        if (loadLobby != null && IsNodeVisible(loadLobby))
        {
            return ExecuteLoadLobbyMenuOption(loadLobby, option);
        }

        // Character select can outlive or be mounted separately from NMainMenu,
        // so handle it before main-menu-specific routing.
        var charSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
        if (charSelect != null && IsNodeVisible(charSelect))
        {
            return ExecuteCharacterSelectMenuOption(charSelect, option, seed, ascension);
        }

        var profileScreen = FindFirst<NProfileScreen>(tree.Root);
        if (profileScreen != null && IsNodeVisible(profileScreen))
        {
            return ExecuteProfileSelectMenuOption(profileScreen, option);
        }

        // Main menu — click a menu button
        var mainMenu = FindFirst<NMainMenu>(tree.Root);
        if (mainMenu != null)
        {
            // Check if we're on singleplayer submenu
            var spSubmenu = FindFirst<NSingleplayerSubmenu>(tree.Root);
            if (spSubmenu != null && IsNodeVisible(spSubmenu))
            {
                if (string.Equals(option, "back", System.StringComparison.OrdinalIgnoreCase))
                {
                    return ClickMenuButtonField(spSubmenu, "_backButton", "Going back", "Back button is not available");
                }

                var fieldName = option.ToLowerInvariant() switch
                {
                    "standard" => "_standardButton",
                    "daily" => "_dailyButton",
                    "custom" => "_customButton",
                    _ => null
                };
                if (fieldName == null)
                    return Error($"Unknown singleplayer option: {option}. Use: standard, daily, custom, back");

                return ClickMenuButtonField(spSubmenu, fieldName, $"Selected {option}", $"Option '{option}' is not available (locked)");
            }

            // Check if we're on multiplayer host submenu
            var mpHostSubmenu = FindFirst<NMultiplayerHostSubmenu>(tree.Root);
            if (mpHostSubmenu != null && IsNodeVisible(mpHostSubmenu))
            {
                if (string.Equals(option, "back", System.StringComparison.OrdinalIgnoreCase))
                {
                    return ClickMenuButtonField(mpHostSubmenu, "_backButton", "Going back", "Back button is not available");
                }

                var fieldName = option.ToLowerInvariant() switch
                {
                    "standard" => "_standardButton",
                    "daily" => "_dailyButton",
                    "custom" => "_customButton",
                    _ => null
                };
                if (fieldName == null)
                    return Error($"Unknown multiplayer host option: {option}. Use: standard, daily, custom, back");

                return ClickMenuButtonField(mpHostSubmenu, fieldName, $"Selected {option}", $"Option '{option}' is not available (locked)");
            }

            // Check if we're on multiplayer submenu
            var mpSubmenu = FindFirst<NMultiplayerSubmenu>(tree.Root);
            if (mpSubmenu != null && IsNodeVisible(mpSubmenu))
            {
                if (string.Equals(option, "back", System.StringComparison.OrdinalIgnoreCase))
                {
                    return ClickMenuButtonField(mpSubmenu, "_backButton", "Going back", "Back button is not available");
                }

                var fieldName = option.ToLowerInvariant() switch
                {
                    "host" => "_hostButton",
                    "join" => "_joinButton",
                    "load" => "_loadButton",
                    "abandon" => "_abandonButton",
                    _ => null
                };
                if (fieldName == null)
                    return Error($"Unknown multiplayer option: {option}. Use: host, join, load, abandon, back");

                return ClickMenuButtonField(mpSubmenu, fieldName, $"Selected {option}", $"Option '{option}' is not available");
            }

            // Main menu buttons
            var normalizedMainMenuOption = option.ToLowerInvariant();

            var menuFieldName = normalizedMainMenuOption switch
            {
                "singleplayer" => "_singleplayerButton",
                "multiplayer" => "_multiplayerButton",
                "compendium" => "_compendiumButton",
                "timeline" => "_timelineButton",
                "settings" => "_settingsButton",
                "continue" => "_continueButton",
                "abandon_run" => "_abandonRunButton",
                "quit" => "_quitButton",
                _ => null
            };
            if (menuFieldName == null)
                return Error($"Unknown menu option: {option}");

            return ClickMenuButtonField(mainMenu, menuFieldName, $"Selected {option}", $"Option '{option}' is not available");
        }

        return Error("Not on a menu screen");
    }

    private static Dictionary<string, object?> ExecutePopupOption(
        List<(string Name, NClickableControl Button)> options,
        string option)
    {
        foreach (var candidate in options)
        {
            if (!string.Equals(candidate.Name, option, System.StringComparison.OrdinalIgnoreCase))
                continue;

            if (!IsPopupButtonActionable(candidate.Button))
                return Error($"Popup option '{candidate.Name}' is disabled");

            candidate.Button.ForceClick();
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Selected popup option: {candidate.Name}"
            };
        }

        return Error($"Popup option '{option}' is not actionable. Use: {string.Join(", ", options.Select(candidate => candidate.Name))}");
    }

    private static Dictionary<string, object?> ExecuteProfileSelectMenuOption(
        NProfileScreen profileScreen,
        string option)
    {
        if (string.Equals(option, "back", System.StringComparison.OrdinalIgnoreCase))
        {
            var backBtn = GetInstanceFieldValue(profileScreen, "_backButton")
                ?? FindFirst<NBackButton>(profileScreen);
            if (backBtn is NClickableControl backClickable &&
                backClickable.IsEnabled &&
                IsNodeVisible(backClickable))
            {
                backClickable.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Going back from profile select" };
            }
            return Error("Back button not available on profile select");
        }

        var normalized = option.ToLowerInvariant();
        if (normalized.StartsWith("profile_", System.StringComparison.Ordinal))
            normalized = normalized["profile_".Length..];
        else if (normalized.StartsWith("slot_", System.StringComparison.Ordinal))
            normalized = normalized["slot_".Length..];

        if (int.TryParse(normalized, out var profileId))
            return ExecuteProfileAction("switch", profileId);

        return Error($"Unknown profile select option: {option}. Use: profile_1, profile_2, profile_3, back");
    }

    private static Dictionary<string, object?> ExecuteJoinScreenMenuOption(
        NJoinFriendScreen joinScreen,
        string option)
    {
        var normalized = option.ToLowerInvariant();

        if (normalized == "back")
        {
            return ClickMenuButtonField(joinScreen, "_backButton", "Going back from join screen", "Back button is not available");
        }

        if (normalized == "refresh")
        {
            var refreshBtn = GetInstanceFieldValue(joinScreen, "_refreshButton") as NClickableControl;
            if (refreshBtn != null && refreshBtn.IsEnabled && IsNodeVisible(refreshBtn))
            {
                refreshBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Refreshing friend list" };
            }
            return Error("Refresh button not available");
        }

        // join_<index> or join_<player_id>
        if (normalized.StartsWith("join_", System.StringComparison.Ordinal))
        {
            var key = normalized["join_".Length..];
            var buttonContainer = GetInstanceFieldValue(joinScreen, "_buttonContainer") as Control;
            if (buttonContainer == null)
                return Error("Friend list container not available");

            // Build the in-order friend list once
            var friendButtons = new List<NJoinFriendButton>();
            foreach (var child in buttonContainer.GetChildren())
            {
                if (child is NJoinFriendButton fbtn)
                    friendButtons.Add(fbtn);
            }

            if (friendButtons.Count == 0)
                return Error("No friends with open lobbies. Use 'refresh' to retry.");

            // Try as index first
            if (int.TryParse(key, out int idx))
            {
                if (idx < 0 || idx >= friendButtons.Count)
                    return Error($"Friend index {idx} out of range (0..{friendButtons.Count - 1})");
                if (!friendButtons[idx].IsEnabled)
                    return Error($"Friend at index {idx} is not joinable right now");
                friendButtons[idx].ForceClick();
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = $"Joining friend at index {idx}"
                };
            }

            // Otherwise treat as ulong player id
            if (ulong.TryParse(key, out ulong playerId))
            {
                var match = friendButtons.FirstOrDefault(b => b.PlayerId == playerId);
                if (match == null)
                    return Error($"No friend with player_id {playerId} in current list. Available indices: 0..{friendButtons.Count - 1}");
                if (!match.IsEnabled)
                    return Error($"Friend {playerId} is not joinable right now");
                match.ForceClick();
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = $"Joining friend {playerId}"
                };
            }

            return Error($"Unknown join target: '{key}'. Use join_<index> (e.g. join_0) or join_<player_id>.");
        }

        return Error($"Unknown join screen option: {option}. Use: refresh, back, join_<index>, join_<player_id>");
    }

    private static Dictionary<string, object?> ExecuteLoadLobbyMenuOption(
        NMultiplayerLoadGameScreen loadLobby,
        string option)
    {
        var normalized = option.ToLowerInvariant();

        if (normalized == "confirm" || normalized == "embark" || normalized == "ready")
        {
            var confirmBtn = GetInstanceFieldValue(loadLobby, "_confirmButton") as NClickableControl;
            if (confirmBtn != null && confirmBtn.IsEnabled && IsNodeVisible(confirmBtn))
            {
                confirmBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Readied up to load saved MP run" };
            }
            return Error("Confirm button not available — already ready, or lobby not yet initialized");
        }

        if (normalized == "unready")
        {
            var unreadyBtn = GetInstanceFieldValue(loadLobby, "_unreadyButton") as NClickableControl;
            if (unreadyBtn != null && unreadyBtn.IsEnabled && IsNodeVisible(unreadyBtn))
            {
                unreadyBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Retracted ready vote" };
            }
            return Error("Unready button not available — you have not confirmed yet");
        }

        if (normalized == "back")
        {
            return ClickMenuButtonField(loadLobby, "_backButton", "Going back from load lobby", "Back button is not available");
        }

        return Error($"Unknown load lobby option: {option}. Use: confirm, embark, unready, back");
    }

    /// <summary>
    /// Sets the ascension level on the character select screen.
    /// SetAscensionLevel emits AscensionLevelChanged, which is what
    /// NCharacterSelectScreen listens to before calling StartRunLobby.SyncAscensionChange —
    /// i.e. the same path the on-screen arrows take.
    /// </summary>
    private static Dictionary<string, object?> ApplyAscension(NCharacterSelectScreen charSelect, int ascension)
    {
        var panel = GetInstanceFieldValue(charSelect, "_ascensionPanel") as NAscensionPanel;
        if (panel == null)
            return Error("Ascension panel is not available on this screen");

        int maxAscension = GetInstanceFieldValue(panel, "_maxAscension") is int max ? max : 0;
        if (ascension < 0 || ascension > maxAscension)
            return Error($"Ascension {ascension} out of range (0-{maxAscension} unlocked for the selected character)");

        panel.SetAscensionLevel(ascension);

        int applied = panel.Ascension;
        if (applied != ascension)
            return Error($"Ascension did not take: asked for {ascension}, panel reports {applied}");

        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Ascension set to {applied}",
            ["ascension"] = applied,
            ["max_ascension"] = maxAscension
        };
    }

    private static Dictionary<string, object?> ExecuteCharacterSelectMenuOption(
        NCharacterSelectScreen charSelect,
        string option,
        string? seed,
        int? ascension = null)
    {
        // "ascension" is applied before anything else so it can accompany either the
        // character pick or the confirm, and can also be sent on its own.
        if (ascension != null)
        {
            var ascensionResult = ApplyAscension(charSelect, ascension.Value);
            if ((string?)ascensionResult["status"] == "error")
                return ascensionResult;
            if (string.IsNullOrEmpty(option))
                return ascensionResult;
        }

        if (string.Equals(option, "back", System.StringComparison.OrdinalIgnoreCase))
        {
            // _backButton leaves the lobby (and disconnects in MP). _unreadyButton is a
            // separate control that only becomes enabled after the player has hit
            // confirm/embark in MP — it retracts the ready vote without leaving. Surface
            // them as distinct options so callers can pick deliberately. Fall back to
            // unready when only it is actionable so older callers don't get stuck.
            var backBtn = GetInstanceFieldValue(charSelect, "_backButton") as NClickableControl;
            if (backBtn != null && backBtn.IsEnabled && IsControlVisibleOrActionable(backBtn))
            {
                backBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Going back" };
            }
            var unreadyFallback = GetInstanceFieldValue(charSelect, "_unreadyButton") as NClickableControl;
            if (unreadyFallback != null && unreadyFallback.IsEnabled && IsControlVisibleOrActionable(unreadyFallback))
            {
                unreadyFallback.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Retracted ready vote (back fell through to unready)" };
            }
            return Error("Back button not available");
        }

        if (string.Equals(option, "unready", System.StringComparison.OrdinalIgnoreCase))
        {
            var unreadyBtn = GetInstanceFieldValue(charSelect, "_unreadyButton") as NClickableControl;
            if (unreadyBtn != null && unreadyBtn.IsEnabled && IsControlVisibleOrActionable(unreadyBtn))
            {
                unreadyBtn.ForceClick();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = "Retracted ready vote" };
            }
            return Error("Unready button not available — you have not confirmed yet");
        }

        if (string.Equals(option, "confirm", System.StringComparison.OrdinalIgnoreCase) ||
            string.Equals(option, "embark", System.StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(seed))
            {
                seed = seed.Trim();
                if (charSelect.Lobby == null)
                {
                    return Error("Seeded embark is not supported for standard singleplayer from this API. Seed was not applied and the run was not started.");
                }

                try
                {
                    charSelect.Lobby.SetSeed(seed);
                }
                catch (System.Exception ex)
                {
                    return Error($"Seeded embark failed before starting the run: {ex.Message}");
                }
            }

            var embarkBtn = GetInstanceFieldValue(charSelect, "_embarkButton");
            if (embarkBtn is NClickableControl embarkClickable && embarkClickable.IsEnabled)
            {
                var panel = GetInstanceFieldValue(charSelect, "_ascensionPanel") as NAscensionPanel;
                int? startingAscension = panel?.Ascension;
                var msg = string.IsNullOrEmpty(seed) ? "Embarking on run" : $"Embarking on run (seed: {seed})";
                if (startingAscension != null)
                    msg += $" at ascension {startingAscension}";
                embarkClickable.ForceClick();
                return new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["message"] = msg,
                    ["ascension"] = startingAscension
                };
            }
            return Error("Embark button not available — select a character first");
        }

        var buttons = FindAll<NCharacterSelectButton>(charSelect);
        foreach (var btn in buttons)
        {
            if (btn.Character != null && (
                string.Equals(btn.Character.Id.Entry, option, System.StringComparison.OrdinalIgnoreCase) ||
                string.Equals(SafeGetText(() => btn.Character.Title), option, System.StringComparison.OrdinalIgnoreCase)))
            {
                if (btn.IsLocked)
                    return Error($"Character '{option}' is locked");
                btn.Select();
                return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = $"Selected {SafeGetText(() => btn.Character.Title)}. Use 'confirm' to embark." };
            }
        }
        return Error($"Character '{option}' not found. Available: {string.Join(", ", buttons.Where(b => !b.IsLocked).Select(b => b.Character?.Id.Entry))}");
    }

    /// <summary>
    /// Presses the confirm button on an unlock screen that is currently open.
    /// Revealing an epoch queues these ("Regent is now playable"), and nothing else on
    /// the timeline responds until one is acknowledged. Returns null when none is open.
    /// </summary>
    private static Dictionary<string, object?>? TryConfirmOpenUnlockScreen(NTimelineScreen timelineScreen)
    {
        NUnlockScreen? screen;
        try { screen = timelineScreen.CurrentUnlockScreen; }
        catch { return null; }

        if (screen == null || !IsNodeVisible(screen))
            return null;

        var confirm = GetInstanceFieldValue(screen, "_unlockConfirmButton") as NClickableControl
            ?? FindFirst<NUnlockConfirmButton>(screen)
            ?? (NClickableControl?)FindFirst<NAcknowledgeButton>(screen);

        if (confirm == null || !confirm.IsEnabled || !IsControlVisibleOrActionable(confirm))
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"{screen.GetType().Name} is open but its confirm button is not actionable yet; retry after the next state poll",
                ["open_unlock_type"] = screen.GetType().Name,
                ["retry"] = true
            };

        confirm.ForceClick();
        return new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["message"] = $"Confirmed {screen.GetType().Name}",
            ["open_unlock_type"] = screen.GetType().Name,
            ["retry"] = true
        };
    }

    private static Dictionary<string, object?>? TryHandleQueuedTimelineUnlock(NTimelineScreen timelineScreen)
    {
        bool isQueued;
        try
        {
            isQueued = timelineScreen.IsScreenQueued();
        }
        catch (System.ObjectDisposedException)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Timeline changed while checking queued unlocks; retry after the next state poll",
                ["retry"] = true
            };
        }

        if (!isQueued)
            return null;

        var queuedScreen = TryPeekQueuedTimelineScreen(timelineScreen);
        var queuedType = queuedScreen?.GetType().Name ?? "unlock";
        var queuedEpochIds = GetQueuedTimelineEpochIds(queuedScreen);
        var alreadyRevealed = queuedEpochIds
            .Where(id => string.Equals(GetProgressEpochState(id), "Revealed", System.StringComparison.OrdinalIgnoreCase))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (alreadyRevealed.Count > 0)
        {
            var alreadyRevealedSet = new HashSet<string>(alreadyRevealed, System.StringComparer.OrdinalIgnoreCase);
            var pendingEpochIds = queuedEpochIds
                .Where(id => !alreadyRevealedSet.Contains(id))
                .Distinct(System.StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Queued timeline unlock references already revealed epochs; not opening it to avoid an invalid unlock path",
                ["queued_unlock_type"] = queuedType,
                ["already_revealed_epoch_ids"] = alreadyRevealed,
                ["pending_epoch_ids"] = pendingEpochIds,
                ["manual_action_required"] = pendingEpochIds.Count > 0,
                ["done"] = true
            };
        }

        if (string.Equals(queuedType, "NUnlockTimelineScreen", System.StringComparison.Ordinal) &&
            IsTimelineScreenBusy(timelineScreen))
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Timeline expansion is queued, but the timeline is still animating; retry after the next state poll",
                ["queued_unlock_type"] = queuedType,
                ["pending_epoch_ids"] = queuedEpochIds,
                ["retry"] = true
            };
        }

        try
        {
            timelineScreen.OpenQueuedScreen();
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Opening queued {queuedType}",
                ["queued_unlock_type"] = queuedType,
                ["pending_epoch_ids"] = queuedEpochIds
            };
        }
        catch (System.ObjectDisposedException)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = "Timeline changed before the queued unlock could open; retry after the next state poll",
                ["queued_unlock_type"] = queuedType,
                ["retry"] = true
            };
        }
        catch (System.Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["status"] = "ok",
                ["message"] = $"Skipped queued {queuedType}: {ex.Message}",
                ["queued_unlock_type"] = queuedType,
                ["retry"] = true
            };
        }
    }

    private static object? TryPeekQueuedTimelineScreen(NTimelineScreen timelineScreen)
    {
        try
        {
            var queue = GetInstanceFieldValue(timelineScreen, "_unlockScreens");
            if (queue == null)
                return null;

            var count = queue.GetType().GetProperty("Count")?.GetValue(queue) as int?;
            if (count <= 0)
                return null;

            return queue.GetType().GetMethod("Peek")?.Invoke(queue, null);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTimelineScreenBusy(NTimelineScreen timelineScreen)
    {
        try
        {
            var isUiVisible = GetInstanceFieldValue(timelineScreen, "_isUiVisible") as bool?;
            if (isUiVisible == false)
                return true;

            var inputBlocker = GetInstanceFieldValue(timelineScreen, "_inputBlocker") as Control;
            if (inputBlocker != null && IsNodeVisible(inputBlocker))
                return true;
        }
        catch (System.ObjectDisposedException)
        {
            return true;
        }

        return false;
    }

    private static List<string> GetQueuedTimelineEpochIds(object? queuedScreen)
    {
        var ids = new List<string>();
        if (queuedScreen == null)
            return ids;

        AddEpochIdsFromObject(GetInstanceFieldValue(queuedScreen, "_epoch"), ids);
        AddEpochIdsFromObject(GetInstanceFieldValue(queuedScreen, "_unlockedEpochs"), ids);
        AddEpochIdsFromObject(GetInstanceFieldValue(queuedScreen, "_erasToUnlock"), ids);

        return ids.Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void AddEpochIdsFromObject(object? value, List<string> ids)
    {
        if (value == null)
            return;

        if (value is string id)
        {
            ids.Add(id);
            return;
        }

        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable)
                AddEpochIdsFromObject(item, ids);
            return;
        }

        var model = value.GetType().GetProperty("Model")?.GetValue(value) ?? value;
        var modelId = model.GetType().GetProperty("Id")?.GetValue(model)?.ToString();
        if (!string.IsNullOrEmpty(modelId))
            ids.Add(modelId);
    }

    private static string? GetProgressEpochState(string epochId)
    {
        try
        {
            var progress = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.Progress;
            return progress?.Epochs
                .FirstOrDefault(epoch => string.Equals(epoch.Id, epochId, System.StringComparison.OrdinalIgnoreCase))
                ?.State
                .ToString();
        }
        catch
        {
            return null;
        }
    }

    private static List<string> GetProgressEpochIdsByState(params string[] states)
    {
        var stateSet = new HashSet<string>(states, System.StringComparer.OrdinalIgnoreCase);
        try
        {
            var progress = MegaCrit.Sts2.Core.Saves.SaveManager.Instance?.Progress;
            if (progress == null)
                return new List<string>();

            return progress.Epochs
                .Where(epoch => stateSet.Contains(epoch.State.ToString()))
                .Select(epoch => epoch.Id)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static Dictionary<string, object?> ClickMenuButtonField(
        object owner,
        string fieldName,
        string successMessage,
        string? disabledMessage = null)
    {
        var btn = GetInstanceFieldValue(owner, fieldName);
        if (btn is NClickableControl clickable)
        {
            if (!clickable.Visible || !clickable.IsVisibleInTree())
                return Error(disabledMessage ?? $"Option '{fieldName}' is not available");

            if (!clickable.IsEnabled)
                return Error(disabledMessage ?? $"Option '{fieldName}' is not available");

            clickable.ForceClick();
            return new Dictionary<string, object?> { ["status"] = "ok", ["message"] = successMessage };
        }

        return Error($"Could not find button '{fieldName}'");
    }
}
