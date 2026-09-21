using System;
using System.Collections.Generic;
using System.Linq;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Hooks;
using MegaCrit.Sts2.Core.HoverTips;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.MonsterMoves.MonsterMoveStateMachine;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.RestSite;
using MegaCrit.Sts2.Core.Events;
using MegaCrit.Sts2.Core.Models.Events;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.Events.Custom;
using MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes.Cards;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Relics;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Models.Monsters;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models.RelicPools;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;
using MegaCrit.Sts2.Core.Nodes.Screens.MainMenu;
using MegaCrit.Sts2.Core.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Multiplayer.Game.Lobby;
using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Platform;
using MegaCrit.Sts2.Core.Platform.Steam;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Nodes.Screens.GameOverScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.Timeline;
using MegaCrit.Sts2.Core.Nodes.Screens.Settings;
using MegaCrit.Sts2.Core.Nodes.Screens.ProfileScreen;
using MegaCrit.Sts2.Core.Nodes.Screens.PauseMenu;
using MegaCrit.Sts2.Core.Nodes.Screens.Capstones;
using Godot;

namespace STS2_MCP;

public static partial class McpMod
{
    /// <summary>
    /// A top-level state dictionary already carrying the build stamp every payload must report.
    ///
    /// Seeded at creation rather than stamped on the way out because the state builders have
    /// ~20 return paths (menu, unknown, error, FTUE); anything added at the end is missed by the
    /// early ones. `state_type` is seeded first so it keeps its leading position when a caller
    /// overwrites it, and "unknown" is the right answer for a path that never sets one.
    /// </summary>
    internal static Dictionary<string, object?> NewStateResult(string stateType = "unknown")
    {
        return new Dictionary<string, object?>
        {
            ["state_type"] = stateType,
            ["mod_version"] = Version,
            ["mod_commit"] = BuildCommit,
            ["schema_version"] = StateSchemaVersion
        };
    }

    /// <summary>Called through BuildGameState(), which brackets it with the warning collector.</summary>
    private static Dictionary<string, object?> BuildGameStateCore()
    {
        var result = NewStateResult();
        var tree = (Godot.Engine.GetMainLoop()) as SceneTree;

        if (tree?.Root != null)
        {
            var ftueState = BuildVisibleFtueState(tree.Root);
            if (ftueState != null)
                return ftueState;
        }

        if (!RunManager.Instance.IsInProgress)
        {
            result["state_type"] = "menu";

            // Detect which menu screen is active
            if (tree?.Root != null)
            {
                if (!result.ContainsKey("menu_screen"))
                {
                // Check for singleplayer submenu (Standard / Daily / Custom)
                var spSubmenu = FindFirst<NSingleplayerSubmenu>(tree.Root);
                if (spSubmenu != null && IsNodeVisible(spSubmenu))
                {
                    result["menu_screen"] = "singleplayer";
                    result["message"] = "Select game mode.";

                    var modeOptions = new List<Dictionary<string, object?>>();
                    var modeFields = new[] { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };
                    foreach (var (fieldName, label) in modeFields)
                    {
                        try
                        {
                            var btn = GetInstanceFieldValue(spSubmenu, fieldName);
                            if (btn is Control ctrl && IsNodeVisible(ctrl))
                            {
                                var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                modeOptions.Add(new Dictionary<string, object?>
                                {
                                    ["name"] = label,
                                    ["enabled"] = isEnabled ?? true
                                });
                            }
                        }
                        catch (Exception ex) { Warn($"menu.singleplayer.options.{label}", ex); }
                    }
                    AddMenuOptionIfVisible(modeOptions, spSubmenu, "_backButton", "back");
                    result["options"] = modeOptions;
                }
                // Check for multiplayer host submenu (Standard / Daily / Custom for multiplayer)
                else
                {
                    var mpHostSubmenu = FindFirst<NMultiplayerHostSubmenu>(tree.Root);
                    if (mpHostSubmenu != null && IsNodeVisible(mpHostSubmenu))
                    {
                        result["menu_screen"] = "multiplayer_host";
                        result["message"] = "Multiplayer host: select game mode.";

                        var modeOptions = new List<Dictionary<string, object?>>();
                        var modeFields = new[] { ("_standardButton", "standard"), ("_dailyButton", "daily"), ("_customButton", "custom") };
                        foreach (var (fieldName, label) in modeFields)
                        {
                            try
                            {
                                var btn = GetInstanceFieldValue(mpHostSubmenu, fieldName);
                                if (btn is Control ctrl && IsNodeVisible(ctrl))
                                {
                                    var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                    modeOptions.Add(new Dictionary<string, object?>
                                    {
                                        ["name"] = label,
                                        ["enabled"] = isEnabled ?? true
                                    });
                                }
                            }
                            catch (Exception ex) { Warn($"menu.multiplayer_host.options.{label}", ex); }
                        }
                        AddMenuOptionIfVisible(modeOptions, mpHostSubmenu, "_backButton", "back");
                        result["options"] = modeOptions;
                    }
                    else
                    {
                        // Check for multiplayer submenu (Host / Join / Load / Abandon)
                        var mpSubmenu = FindFirst<NMultiplayerSubmenu>(tree.Root);
                        if (mpSubmenu != null && IsNodeVisible(mpSubmenu))
                        {
                            result["menu_screen"] = "multiplayer";
                            result["message"] = "Multiplayer menu.";

                            var mpOptions = new List<Dictionary<string, object?>>();
                            var mpFields = new[] { ("_hostButton", "host"), ("_joinButton", "join"), ("_loadButton", "load"), ("_abandonButton", "abandon") };
                            foreach (var (fieldName, label) in mpFields)
                            {
                                try
                                {
                                    var btn = GetInstanceFieldValue(mpSubmenu, fieldName);
                                    if (btn is Control ctrl && IsNodeVisible(ctrl))
                                    {
                                        var isEnabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                        mpOptions.Add(new Dictionary<string, object?>
                                        {
                                            ["name"] = label,
                                            ["enabled"] = isEnabled ?? true
                                        });
                                    }
                                }
                                catch (Exception ex) { Warn($"menu.multiplayer.options.{label}", ex); }
                            }
                            AddMenuOptionIfVisible(mpOptions, mpSubmenu, "_backButton", "back");
                            result["options"] = mpOptions;
                        }
                    }
                }
                // Multiplayer Join Friend screen (lives in the same submenu stack as
                // NMultiplayerSubmenu; pushed when the user clicks "join")
                if (result.ContainsKey("menu_screen") == false)
                {
                    var joinScreen = FindFirst<NJoinFriendScreen>(tree.Root);
                    if (joinScreen != null && IsNodeVisible(joinScreen))
                    {
                        AddMultiplayerJoinMenuState(result, joinScreen);
                    }
                }

                // Multiplayer Load lobby — resume saved MP run. Pushed by NMultiplayerSubmenu
                // when "load" is clicked (host) or by JoinFlow when joining a save in progress (client).
                if (result.ContainsKey("menu_screen") == false)
                {
                    var loadLobby = FindFirst<NMultiplayerLoadGameScreen>(tree.Root);
                    if (loadLobby != null && IsNodeVisible(loadLobby))
                    {
                        AddMultiplayerLoadLobbyMenuState(result, loadLobby);
                    }
                }

                // Check for character select screen
                if (result.ContainsKey("menu_screen") == false)
                {
                    var charSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
                    if (charSelect != null && IsNodeVisible(charSelect))
                    {
                        AddCharacterSelectMenuState(result, charSelect);
                    }
                    else
                    {
                        // Check for other screens
                        var timelineScreen = FindFirst<NTimelineScreen>(tree.Root);
                        var compendiumSubmenu = FindFirst<NCompendiumSubmenu>(tree.Root);
                        var settingsScreen = FindFirst<NSettingsScreen>(tree.Root);

                        if (timelineScreen != null && IsNodeVisible(timelineScreen))
                        {
                            result["menu_screen"] = "timeline";
                            result["message"] = "Timeline screen.";
                            result["options"] = new List<Dictionary<string, object?>>
                            {
                                new() { ["name"] = "advance", ["enabled"] = true },
                                new() { ["name"] = "back", ["enabled"] = true }
                            };

                            // Read epochs from ProgressState (stable, not hover-dependent)
                            try
                            {
                                var progress = SaveManager.Instance?.Progress;
                                if (progress != null)
                                {
                                    var epochList = new List<Dictionary<string, object?>>();
                                    var revealedCount = 0;
                                    var obtainedCount = 0;
                                    var lockedCount = 0;
                                    var noSlotCount = 0;
                                    foreach (var epoch in progress.Epochs)
                                    {
                                        var eraName = epoch.Id;
                                        var state = epoch.State.ToString();
                                        // Clean up ID to readable name
                                        var name = System.Text.RegularExpressions.Regex.Replace(eraName, @"(\d+)$", "");
                                        name = System.Text.RegularExpressions.Regex.Replace(name, @"(?<=[a-z])(?=[A-Z])", " ");

                                        switch (epoch.State)
                                        {
                                            case EpochState.Revealed:
                                                revealedCount++;
                                                break;
                                            case EpochState.Obtained:
                                            case EpochState.ObtainedNoSlot:
                                                obtainedCount++;
                                                break;
                                            case EpochState.NotObtained:
                                                lockedCount++;
                                                break;
                                            case EpochState.NoSlot:
                                                noSlotCount++;
                                                break;
                                        }

                                        epochList.Add(new Dictionary<string, object?>
                                        {
                                            ["id"] = eraName,
                                            ["name"] = name,
                                            ["state"] = state,
                                            ["obtained"] = epoch.ObtainDate
                                        });
                                    }

                                    result["epochs"] = epochList;
                                    result["total_slots"] = epochList.Count;
                                    result["completed_count"] = revealedCount;
                                    result["revealed_count"] = revealedCount;
                                    result["obtained_unrevealed_count"] = obtainedCount;
                                    result["locked_count"] = lockedCount;
                                    result["no_slot_count"] = noSlotCount;
                                }
                            }
                            catch (Exception ex) { Warn("menu.timeline.epochs", ex); }
                        }
                        else if (compendiumSubmenu != null && IsNodeVisible(compendiumSubmenu))
                        {
                            result["menu_screen"] = "compendium";
                            result["message"] = "Compendium screen.";
                        }
                        else if (settingsScreen != null && IsNodeVisible(settingsScreen))
                        {
                            foreach (var entry in BuildSettingsMenuState(settingsScreen, inRun: false))
                                result[entry.Key] = entry.Value;
                        }
                        else
                        {
                            var profileScreen = FindFirst<NProfileScreen>(tree.Root);
                            if (profileScreen != null && IsNodeVisible(profileScreen))
                            {
                                result["menu_screen"] = "profile_select";
                                result["message"] = "Profile select screen.";
                                result["current_profile_id"] = SaveManager.Instance?.CurrentProfileId;

                                var options = new List<Dictionary<string, object?>>();
                                var buttons = GetInstanceFieldValue(profileScreen, "_profileButtons") as System.Collections.IEnumerable;
                                if (buttons != null)
                                {
                                    foreach (var btn in buttons)
                                    {
                                        var btnId = GetInstanceFieldValue(btn, "_profileId");
                                        if (btnId is int id)
                                        {
                                            var enabled = btn.GetType().GetProperty("IsEnabled")?.GetValue(btn) as bool?;
                                            options.Add(new Dictionary<string, object?>
                                            {
                                                ["name"] = $"profile_{id}",
                                                ["enabled"] = enabled ?? true
                                            });
                                        }
                                    }
                                }

                                var backBtn = GetInstanceFieldValue(profileScreen, "_backButton")
                                    ?? FindFirst<NBackButton>(profileScreen);
                                if (backBtn is NClickableControl backClickable && IsNodeVisible(backClickable))
                                {
                                    options.Add(new Dictionary<string, object?>
                                    {
                                        ["name"] = "back",
                                        ["enabled"] = backClickable.IsEnabled
                                    });
                                }

                                if (options.Count > 0)
                                    result["options"] = options;
                            }
                        }
                        if (!result.ContainsKey("menu_screen"))
                        {
                            result["menu_screen"] = "main";
                            result["message"] = "Main menu.";

                        var mainMenu = FindFirst<NMainMenu>(tree.Root);
                        if (mainMenu != null)
                        {
                            var options = new List<string>();
                            var blockedOptions = new List<Dictionary<string, object?>>();
                            var fields = new[] { "_continueButton", "_abandonRunButton", "_singleplayerButton", "_multiplayerButton", "_compendiumButton", "_timelineButton", "_settingsButton", "_quitButton" };
                            var labels = new[] { "continue", "abandon_run", "singleplayer", "multiplayer", "compendium", "timeline", "settings", "quit" };
                            var unrevealedEpochs = GetProgressEpochIdsByState("Obtained", "ObtainedNoSlot");
                            for (int i = 0; i < fields.Length; i++)
                            {
                                try
                                {
                                    var btn = GetInstanceFieldValue(mainMenu, fields[i]);
                                    if (btn is NClickableControl clickable &&
                                        clickable.IsEnabled &&
                                        clickable.Visible &&
                                        clickable.IsVisibleInTree())
                                    {
                                        options.Add(labels[i]);
                                    }
                                }
                                catch (Exception ex) { Warn($"menu.main.options.{labels[i]}", ex); }
                            }
                            if (options.Count > 0)
                                result["options"] = options;
                            if (blockedOptions.Count > 0)
                                result["blocked_options"] = blockedOptions;

                            // A finished run leaves its epochs obtained-but-unrevealed,
                            // and the main menu hides `singleplayer` until they are
                            // revealed. Say so, and name the action that clears it.
                            if (unrevealedEpochs.Count > 0)
                            {
                                result["pending_epoch_ids"] = unrevealedEpochs;
                                result["message"] =
                                    $"Main menu. {unrevealedEpochs.Count} epoch(s) obtained but not revealed - "
                                    + "the singleplayer option stays hidden until they are. "
                                    + "Run the 'timeline_reveal_epochs' action (repeat until done) to clear this.";
                            }
                        }
                        }
                    }
                }
            }
            } // close if (!result.ContainsKey("menu_screen"))
            else
            {
                result["message"] = "No run in progress.";
            }

            return result;
        }

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            if (tree?.Root != null)
            {
                var activeCharSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
                if (activeCharSelect != null && IsNodeVisible(activeCharSelect))
                {
                    AddCharacterSelectMenuState(result, activeCharSelect);
                    return result;
                }
            }

            result["state_type"] = "unknown";
            return result;
        }

        // Overlays can appear on top of any room (events, rest sites, combat).
        // Rewards/card-reward overlays defer to the map - they may linger on the
        // overlay stack while the map opens after the player clicks proceed.
        var topOverlay = NOverlayStack.Instance?.Peek();
        var currentRoom = runState.CurrentRoom;
        bool mapIsOpen = IsMapScreenOpenOrVisible();
        // The settings screen is pushed on the run's submenu stack, over everything below.
        // It is checked first so the room underneath is not reported as the live screen.
        var settingsState = TryBuildSettingsMenuState(tree?.Root, inRun: true);
        // The pause menu shares that stack, and settings can be pushed on top of the
        // pause menu, so settings has to keep winning when both are up.
        var pauseState = settingsState == null ? TryBuildPauseMenuState(tree?.Root) : null;
        if (settingsState != null)
        {
            foreach (var entry in settingsState)
                result[entry.Key] = entry.Value;
        }
        else if (pauseState != null)
        {
            foreach (var entry in pauseState)
                result[entry.Key] = entry.Value;
        }
        else if (topOverlay is NCardGridSelectionScreen cardSelectScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildCardSelectState(cardSelectScreen, runState);
        }
        else if (topOverlay is NChooseACardSelectionScreen chooseCardScreen)
        {
            result["state_type"] = "card_select";
            result["card_select"] = BuildChooseCardState(chooseCardScreen, runState);
        }
        else if (topOverlay is NChooseABundleSelectionScreen bundleScreen)
        {
            result["state_type"] = "bundle_select";
            result["bundle_select"] = BuildBundleSelectState(bundleScreen, runState);
        }
        else if (topOverlay is NChooseARelicSelection relicSelectScreen)
        {
            result["state_type"] = "relic_select";
            result["relic_select"] = BuildRelicSelectState(relicSelectScreen, runState);
        }
        else if (!mapIsOpen && topOverlay is NCrystalSphereScreen crystalSphereScreen)
        {
            // Mirror the NRewardsScreen guard below: the Crystal Sphere overlay
            // lingers on the stack after proceed is clicked (only ClearScreens
            // on the next room transition pops it). Once the map opens on top,
            // report "map" so state matches the screen the player can interact
            // with. See issue #73.
            result["state_type"] = "crystal_sphere";
            result["crystal_sphere"] = BuildCrystalSphereState(crystalSphereScreen, runState);
        }
        else if (!mapIsOpen && topOverlay is NCardRewardSelectionScreen cardRewardScreen)
        {
            result["state_type"] = "card_reward";
            result["card_reward"] = BuildCardRewardState(cardRewardScreen);
        }
        else if (!mapIsOpen && topOverlay is NRewardsScreen rewardsScreen)
        {
            result["state_type"] = "rewards";
            result["rewards"] = BuildRewardsState(rewardsScreen, runState);
        }
        else if (topOverlay is NGameOverScreen gameOverScreen)
        {
            result["state_type"] = "game_over";
            result["game_over"] = new Dictionary<string, object?>
            {
                ["message"] = "Run ended.",
                ["options"] = new List<string> { "main_menu" }
            };
        }
        else if (topOverlay is IOverlayScreen
                 && topOverlay is not NRewardsScreen
                 && topOverlay is not NCardRewardSelectionScreen
                 && topOverlay is not NCrystalSphereScreen)
        {
            // Catch-all for unhandled overlays - prevents soft-locks.
            // Overlays that linger on the stack while the map takes over
            // (rewards, card reward, Crystal Sphere) are excluded so the
            // fallback below reports "map" once mapIsOpen is true.
            result["state_type"] = "overlay";
            result["overlay"] = new Dictionary<string, object?>
            {
                ["screen_type"] = topOverlay.GetType().Name,
                ["message"] = $"An overlay ({topOverlay.GetType().Name}) is active. It may require manual interaction in-game."
            };
        }
        else if (mapIsOpen)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is CombatRoom combatRoom)
        {
            if (CombatManager.Instance.IsInProgress)
            {
                // Check for in-combat hand card selection (e.g., "Select a card to exhaust")
                var playerHand = NPlayerHand.Instance;
                if (playerHand != null && playerHand.IsInCardSelection)
                {
                    result["state_type"] = "hand_select";
                    result["hand_select"] = BuildHandSelectState(playerHand, runState);
                    result["battle"] = BuildBattleState(runState, combatRoom);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLower(); // monster, elite, boss
                    result["battle"] = BuildBattleState(runState, combatRoom);
                }
            }
            else
            {
                // After combat ends - reward/card overlays are caught by top-level checks above.
                // Only handle map and the brief transition before rewards appear.
                if (IsMapScreenOpenOrVisible())
                {
                    result["state_type"] = "map";
                    result["map"] = BuildMapState(runState);
                }
                else
                {
                    result["state_type"] = combatRoom.RoomType.ToString().ToLower();
                    result["message"] = "Combat ended. Waiting for rewards...";
                }
            }
        }
        else if (currentRoom is EventRoom eventRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else if (eventRoom.CanonicalEvent is FakeMerchant)
            {
                result["state_type"] = "fake_merchant";
                result["fake_merchant"] = BuildFakeMerchantState(eventRoom, runState);
            }
            else
            {
                result["state_type"] = "event";
                result["event"] = BuildEventState(eventRoom, runState);
            }
        }
        else if (currentRoom is MapRoom)
        {
            result["state_type"] = "map";
            result["map"] = BuildMapState(runState);
        }
        else if (currentRoom is MerchantRoom merchantRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                // Auto-open the shopkeeper's inventory if not already open.
                // NMerchantRoom.Inventory (UI node) can be null before the scene is fully ready;
                // OpenInventory() itself accesses Inventory.IsOpen, so guard against null.
                var merchUI = NMerchantRoom.Instance;
                if (merchUI?.Inventory != null && !merchUI.Inventory.IsOpen)
                {
                    merchUI.OpenInventory();
                }
                result["state_type"] = "shop";
                result["shop"] = BuildShopState(merchantRoom, runState);
            }
        }
        else if (currentRoom is RestSiteRoom restSiteRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "rest_site";
                result["rest_site"] = BuildRestSiteState(restSiteRoom, runState);
            }
        }
        else if (currentRoom is TreasureRoom treasureRoom)
        {
            if (IsMapScreenOpenOrVisible())
            {
                result["state_type"] = "map";
                result["map"] = BuildMapState(runState);
            }
            else
            {
                result["state_type"] = "treasure";
                result["treasure"] = BuildTreasureState(treasureRoom, runState);
            }
        }
        else if (currentRoom == null && tree?.Root != null)
        {
            var activeCharSelect = FindFirst<NCharacterSelectScreen>(tree.Root);
            if (activeCharSelect != null && IsNodeVisible(activeCharSelect))
            {
                AddCharacterSelectMenuState(result, activeCharSelect);
            }
            else
            {
                result["state_type"] = "unknown";
                result["room_type"] = currentRoom?.GetType().Name;
            }
        }
        else
        {
            result["state_type"] = "unknown";
            result["room_type"] = currentRoom?.GetType().Name;
        }

        // Common run info
        result["run"] = new Dictionary<string, object?>
        {
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["ascension"] = runState.AscensionLevel
        };

        // Always include full player data (relics, potions, deck, etc.) on every screen
        var _player = LocalContext.GetMe(runState);
        if (_player != null)
        {
            result["player"] = BuildPlayerState(_player);
        }

        AddResolvingState(result, result.GetValueOrDefault("state_type")?.ToString());

        return result;
    }

    /// <summary>
    /// Whether the game is still resolving what was last asked of it.
    ///
    /// Everything else in this state is read straight off the live models, so a read
    /// taken while an action is still animating reports pre-resolution numbers: enemy
    /// HP before a multi-hit finished landing, a hand that has not been redrawn yet.
    /// That is the difference between "cannot kill this turn" and a lethal, and there
    /// was no way to tell a settled state from a mid-animation one.
    ///
    /// is_resolving is false only when the action queue is empty and nothing is
    /// blocking player input. resolving_reasons names what is outstanding.
    /// </summary>
    /// <summary>
    /// Screens that exist to take a decision from the player. The game is parked on
    /// them, not working - reporting "still resolving" here deadlocks any caller that
    /// waits for it to clear, because only the caller can clear it.
    /// </summary>
    private static readonly HashSet<string> _playerInputStates = new()
    {
        "card_select", "hand_select", "bundle_select", "relic_select", "card_reward",
        "rewards", "event", "rest_site", "shop", "fake_merchant", "treasure",
        "crystal_sphere", "map", "menu", "game_over"
    };

    private static void AddResolvingState(Dictionary<string, object?> result, string? stateType)
    {
        var reasons = new List<string>();

        try
        {
            var synchronizer = RunManager.Instance.ActionQueueSynchronizer;
            var queueSet = GetInstanceFieldValue(synchronizer, "_actionQueueSet");
            if (queueSet != null && GetPropertyValue(queueSet, "IsEmpty") is bool isEmpty && !isEmpty
                && !IsActionQueueWaitingForPlayer(queueSet))
                reasons.Add("action_queue_not_empty");
        }
        catch (Exception ex) { Warn("is_resolving.action_queue", ex); }

        try
        {
            var combat = CombatManager.Instance;
            if (combat.IsInProgress)
            {
                if (combat.PlayerActionsDisabled) reasons.Add("player_actions_disabled");
                if (combat.IsStarting) reasons.Add("combat_starting");
                if (combat.IsOverOrEnding) reasons.Add("combat_ending");
            }
        }
        catch (Exception ex) { Warn("is_resolving.combat", ex); }

        try
        {
            var hand = NPlayerHand.Instance;
            if (hand != null && hand.InCardPlay)
                reasons.Add("card_still_being_played");
        }
        catch (Exception ex) { Warn("is_resolving.card_play", ex); }

        // A screen that is waiting for the player is not "resolving", whatever the
        // queue says: the action sitting in the queue is the one paused on this very
        // screen, and it cannot progress until the caller answers it.
        if (stateType != null && _playerInputStates.Contains(stateType))
            reasons.Clear();

        result["is_resolving"] = reasons.Count > 0;
        if (reasons.Count > 0)
            result["resolving_reasons"] = reasons;
    }

    /// <summary>
    /// True when the action queue is non-empty only because an action is parked
    /// waiting for a player choice (a card selection, a bundle pick, ...).
    /// </summary>
    private static bool IsActionQueueWaitingForPlayer(object queueSet)
    {
        try
        {
            if (GetInstanceFieldValue(queueSet, "_actionsWaitingForResumption") is System.Collections.IEnumerable waiting)
            {
                foreach (var _ in waiting)
                    return true;
            }
        }
        catch (Exception ex) { Warn("is_resolving.actions_waiting_for_player", ex); }

        return false;
    }

    /// <summary>
    /// The settings screen lives on a submenu stack (NMainMenuSubmenuStack outside a run,
    /// NRunSubmenuStack inside one), not on the overlay stack and not in the room, so
    /// nothing else in this state builder notices it. Left unreported, the run-time path
    /// describes the room underneath and every action sent against that description is
    /// applied to a screen the player cannot see.
    ///
    /// Returns null when the screen is not up, so callers can fall through unchanged.
    /// A sibling helper can be added here for the pause menu, which shares the stack.
    /// </summary>
    private static Dictionary<string, object?>? TryBuildSettingsMenuState(Node? root, bool inRun)
    {
        if (root == null)
            return null;

        var settingsScreen = FindFirst<NSettingsScreen>(root);
        if (settingsScreen == null || !IsNodeVisible(settingsScreen))
            return null;

        return BuildSettingsMenuState(settingsScreen, inRun);
    }

    /// <summary>
    /// "back" is the only option exposed. Tabs, tickboxes and sliders are deliberately
    /// not modelled - the point here is that the API can leave a screen it can enter.
    /// </summary>
    private static Dictionary<string, object?> BuildSettingsMenuState(NSettingsScreen settingsScreen, bool inRun)
    {
        var backButton = GetSubmenuBackButton(settingsScreen);

        return new Dictionary<string, object?>
        {
            ["state_type"] = "menu",
            ["menu_screen"] = "settings",
            ["in_run"] = inRun,
            ["message"] = inRun
                ? "Settings screen. The run is paused underneath; 'back' returns to it."
                : "Settings screen.",
            ["options"] = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "back", ["enabled"] = backButton?.IsEnabled ?? true }
            }
        };
    }

    /// <summary>
    /// The back button of any screen on a submenu stack - the one exit every such screen
    /// is guaranteed to have. _backButton is declared on NSubmenu, not on the concrete
    /// screens; GetInstanceFieldValue walks base types, and the tree search is the
    /// fallback for a future layout change.
    /// </summary>
    private static NClickableControl? GetSubmenuBackButton(NSubmenu submenu)
    {
        try
        {
            if (GetInstanceFieldValue(submenu, "_backButton") is NClickableControl clickable)
                return clickable;
        }
        catch (Exception ex) { Warn("menu.settings.back_button", ex); }

        return FindFirst<NBackButton>(submenu);
    }

    /// <summary>
    /// The submenu currently covering the run, or null when nothing is.
    ///
    /// NCapstoneContainer is the authority - NTopBarPauseButton.IsOpen() reads the same
    /// property. A visibility test alone is not enough: NCapstoneSubmenuStack.ShowScreen
    /// pushes the screen before NCapstoneContainer.Open reparents it, so the menu is
    /// live before it is visible in the tree. The node search is the fallback.
    /// </summary>
    internal static NSubmenu? GetOpenRunSubmenu(Node? root)
    {
        try
        {
            if (NCapstoneContainer.Instance?.CurrentCapstoneScreen is NCapstoneSubmenuStack submenuStack)
            {
                var top = submenuStack.Stack?.Peek();
                if (top != null && IsLiveNode(top))
                    return top;
            }
        }
        catch { /* no capstone container outside a run; fall through */ }

        if (root == null)
            return null;

        var pauseMenu = FindFirst<NPauseMenu>(root);
        return pauseMenu != null && IsNodeVisible(pauseMenu) ? pauseMenu : null;
    }

    /// <summary>
    /// The pause menu (top-bar gear / Esc) is pushed on the run's submenu stack inside
    /// the capstone container, exactly like the settings screen, so nothing else in this
    /// builder notices it. Left unreported, state describes the room underneath while
    /// the player is looking at a paused menu, and every action is aimed at a screen
    /// that is not on top. See A-5 of the operator-side issue log.
    ///
    /// Anything else pushed on that stack (the in-run compendium and the screens it
    /// opens) is reported generically with a `back` option, so entering one can never
    /// become a screen the API has no way out of.
    ///
    /// Returns null when no such screen is up, so callers fall through unchanged.
    /// </summary>
    private static Dictionary<string, object?>? TryBuildPauseMenuState(Node? root)
    {
        var submenu = GetOpenRunSubmenu(root);
        if (submenu == null)
            return null;

        return submenu is NPauseMenu pauseMenu
            ? BuildPauseMenuState(pauseMenu)
            : BuildRunSubmenuState(submenu);
    }

    /// <summary>
    /// The six NPauseMenu button fields, in the order NPauseMenu.Buttons declares them.
    /// </summary>
    private static readonly (string Field, string Name)[] _pauseMenuButtons =
    {
        ("_resumeButton", "resume"),
        ("_settingsButton", "settings"),
        ("_compendiumButton", "compendium"),
        ("_giveUpButton", "give_up"),
        ("_disconnectButton", "disconnect"),
        ("_saveAndQuitButton", "save_and_quit")
    };

    private static List<Dictionary<string, object?>> BuildPauseMenuOptions(NPauseMenu pauseMenu)
    {
        var options = new List<Dictionary<string, object?>>();
        foreach (var (field, name) in _pauseMenuButtons)
        {
            try
            {
                // Only the button's own Visible flag is read. NPauseMenu._Ready and
                // Initialize set it per game mode (give_up / save_and_quit are hidden
                // for multiplayer clients, disconnect only for them, compendium until
                // it is unlocked), while IsVisibleInTree is still false on the frame
                // the menu is being reparented into the capstone container - which
                // would report a pause menu with no options at all.
                if (GetInstanceFieldValue(pauseMenu, field) is not Control button || !button.Visible)
                    continue;

                options.Add(new Dictionary<string, object?>
                {
                    ["name"] = name,
                    ["enabled"] = (button as NClickableControl)?.IsEnabled ?? true
                });
            }
            catch { /* a missing field means the game moved it; skip the option */ }
        }
        return options;
    }

    private static Dictionary<string, object?> BuildPauseMenuState(NPauseMenu pauseMenu)
    {
        return new Dictionary<string, object?>
        {
            ["state_type"] = "menu",
            ["menu_screen"] = "pause",
            ["in_run"] = true,
            ["message"] =
                "Run paused. 'resume' closes this menu and returns to the run; 'settings' and "
                + "'compendium' open screens that 'back' leaves again; 'give_up' abandons the run "
                + "permanently (it asks for confirmation first, answered with yes/no); "
                + "'save_and_quit' returns to the main menu, where 'continue' resumes this run.",
            ["options"] = BuildPauseMenuOptions(pauseMenu)
        };
    }

    /// <summary>
    /// Any other screen on the run's submenu stack (compendium and what it opens).
    /// Only `back` is modelled - the point is that the API can leave a screen it can
    /// enter, not that it can drive the compendium.
    /// </summary>
    private static Dictionary<string, object?> BuildRunSubmenuState(NSubmenu submenu)
    {
        var screen = SubmenuScreenName(submenu);
        var backButton = GetSubmenuBackButton(submenu);

        return new Dictionary<string, object?>
        {
            ["state_type"] = "menu",
            ["menu_screen"] = screen,
            ["in_run"] = true,
            ["screen_class"] = submenu.GetType().Name,
            ["message"] = $"{screen} screen, opened from the pause menu. The run is paused "
                          + "underneath; 'back' returns to the previous screen.",
            ["options"] = new List<Dictionary<string, object?>>
            {
                new() { ["name"] = "back", ["enabled"] = backButton?.IsEnabled ?? true }
            }
        };
    }

    /// <summary>
    /// "NCompendiumSubmenu" -> "compendium", "NCardLibrary" -> "card_library". Derived
    /// rather than listed so a screen the game adds later still gets a usable name and a
    /// working `back`, instead of being reported as an unnamed screen with no way out.
    /// </summary>
    private static string SubmenuScreenName(NSubmenu submenu)
    {
        var name = submenu.GetType().Name;
        if (name.Length > 1 && name[0] == 'N' && char.IsUpper(name[1]))
            name = name[1..];
        foreach (var suffix in new[] { "Submenu", "Screen" })
        {
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.Ordinal))
                name = name[..^suffix.Length];
        }

        name = System.Text.RegularExpressions.Regex
            .Replace(name, "(?<=[a-z0-9])(?=[A-Z])", "_")
            .ToLowerInvariant();
        return name.Length > 0 ? name : "submenu";
    }

    private static void AddCharacterSelectMenuState(
        Dictionary<string, object?> result,
        NCharacterSelectScreen charSelect)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "character_select";
        result["message"] = "Select a character.";

        var buttons = FindAll<NCharacterSelectButton>(charSelect);
        var characters = new List<Dictionary<string, object?>>();
        var options = new List<Dictionary<string, object?>>();
        foreach (var btn in buttons)
        {
            try
            {
                if (btn.Character is { } cm && IsNodeVisible(btn))
                {
                    var characterId = cm.Id.Entry;
                    var characterName = SafeGetText(() => cm.Title);
                    options.Add(new Dictionary<string, object?>
                    {
                        ["name"] = characterId,
                        ["enabled"] = !btn.IsLocked
                    });

                    var charData = new Dictionary<string, object?>
                    {
                        ["name"] = characterName,
                        ["id"] = characterId,
                        ["locked"] = btn.IsLocked,
                        ["hp"] = cm.StartingHp,
                        ["gold"] = cm.StartingGold,
                        ["energy"] = cm.MaxEnergy,
                        ["description"] = SafeGetText(() => cm.CardsModifierDescription),
                    };

                    var startRelics = new List<Dictionary<string, object?>>();
                    foreach (var relic in cm.StartingRelics)
                    {
                        startRelics.Add(new Dictionary<string, object?>
                        {
                            ["name"] = SafeGetText(() => relic.Title),
                            ["description"] = SafeGetText(() => relic.DynamicDescription)
                        });
                    }
                    if (startRelics.Count > 0)
                        charData["starting_relics"] = startRelics;

                    var deckCards = new List<string>();
                    foreach (var card in cm.StartingDeck)
                        deckCards.Add(SafeGetText(() => card.Title) ?? "?");
                    if (deckCards.Count > 0)
                        charData["starting_deck"] = deckCards;

                    try
                    {
                        var allCards = cm.CardPool?.AllCards;
                        if (allCards != null)
                            charData["total_cards"] = System.Linq.Enumerable.Count(allCards);
                    }
                    catch (Exception ex) { Warn("menu.character_select.total_cards", ex); }

                    try
                    {
                        var allRelics = cm.RelicPool?.AllRelics;
                        if (allRelics != null)
                            charData["total_relics"] = System.Linq.Enumerable.Count(allRelics);
                    }
                    catch (Exception ex) { Warn("menu.character_select.total_relics", ex); }

                    try
                    {
                        var allPotions = cm.PotionPool?.AllPotions;
                        if (allPotions != null)
                            charData["total_potions"] = System.Linq.Enumerable.Count(allPotions);
                    }
                    catch (Exception ex) { Warn("menu.character_select.total_potions", ex); }

                    characters.Add(charData);
                }
            }
            catch (Exception ex) { Warn("menu.character_select.characters", ex); }
        }
        if (characters.Count > 0)
            result["characters"] = characters;

        var embarkBtn = GetInstanceFieldValue(charSelect, "_embarkButton");
        if (embarkBtn is NClickableControl embarkClickable && IsNodeVisible(embarkClickable))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "confirm",
                ["enabled"] = embarkClickable.IsEnabled
            });
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "embark",
                ["enabled"] = embarkClickable.IsEnabled
            });
        }

        // _backButton and _unreadyButton are surfaced as distinct options so MP callers
        // can distinguish "leave the lobby" from "retract my ready vote". In SP, only
        // _backButton ever becomes enabled. See NCharacterSelectScreen.OnEmbarkPressed /
        // OnUnreadyPressed for the toggle logic.
        var backBtn = GetInstanceFieldValue(charSelect, "_backButton");
        if (backBtn is NClickableControl backClickable && IsNodeVisible(backClickable))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "back",
                ["enabled"] = backClickable.IsEnabled
            });
        }

        // Ascension is chosen on this screen and decides the run's difficulty, so it
        // belongs in state whether or not a lobby is involved — a caller that cannot
        // read the current level cannot check what it is about to start.
        var ascensionPanel = GetInstanceFieldValue(charSelect, "_ascensionPanel") as NAscensionPanel;
        if (ascensionPanel != null)
        {
            result["ascension"] = ascensionPanel.Ascension;
            var maxAscension = GetInstanceFieldValue(ascensionPanel, "_maxAscension");
            if (maxAscension is int maxAsc)
                result["max_ascension"] = maxAsc;
            result["ascension_selectable"] = IsNodeVisible(ascensionPanel);
        }

        // MP lobby block — surfaces roster / ready state / ascension when this character
        // select is part of a host or client lobby. SP runs leave the field absent.
        bool isMpCharSelect = false;
        try
        {
            var lobby = charSelect.Lobby;
            if (lobby != null && lobby.NetService != null && lobby.NetService.Type.IsMultiplayer())
            {
                isMpCharSelect = true;
                result["lobby"] = BuildStartRunLobbyState(lobby);
            }
        }
        catch (Exception ex) { Warn("menu.character_select.lobby", ex); }

        // _unreadyButton is part of the scene in SP too but never becomes enabled there.
        // Only surface it as an option in MP, where it has a real role.
        if (isMpCharSelect)
        {
            var unreadyBtn = GetInstanceFieldValue(charSelect, "_unreadyButton");
            if (unreadyBtn is NClickableControl unreadyClickable && IsNodeVisible(unreadyClickable))
            {
                options.Add(new Dictionary<string, object?>
                {
                    ["name"] = "unready",
                    ["enabled"] = unreadyClickable.IsEnabled
                });
            }
        }

        if (options.Count > 0)
            result["options"] = options;
    }

    // v0.111: StartRunLobby no longer exposes a public MaxPlayers; read the private
    // backing field (set from the ctor's maxPlayers arg). Falls back to current count.
    private static int GetStartRunLobbyMaxPlayers(StartRunLobby lobby)
    {
        try
        {
            var f = typeof(StartRunLobby).GetField("_maxPlayers",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (f != null && f.GetValue(lobby) is int n)
                return n;
        }
        catch (Exception ex) { Warn("lobby.max_players", ex); }
        return lobby.Players.Count;
    }

    private static Dictionary<string, object?> BuildStartRunLobbyState(StartRunLobby lobby)
    {
        var lobbyState = new Dictionary<string, object?>
        {
            ["type"] = lobby.NetService.Type switch
            {
                NetGameType.Host => "host",
                NetGameType.Client => "client",
                NetGameType.Singleplayer => "singleplayer",
                _ => lobby.NetService.Type.ToString().ToLowerInvariant()
            },
            ["game_mode"] = lobby.GameMode.ToString().ToLowerInvariant(),
            ["max_players"] = GetStartRunLobbyMaxPlayers(lobby),
            ["ascension"] = lobby.Ascension,
            ["max_ascension"] = lobby.MaxAscension,
            ["all_ready"] = lobby.Players.Count > 0 && lobby.Players.All(p => p.isReady),
            ["is_about_to_begin"] = SafeIsAboutToBeginGame(lobby)
        };

        // is_local_ready === local player has hit Embark in MP and is now waiting.
        // Mirrors NCharacterSelectScreen._readyAndWaitingContainer.Visible.
        try
        {
            var local = lobby.LocalPlayer;
            lobbyState["is_local_ready"] = local.isReady;
            lobbyState["local_player_id"] = local.id.ToString();
        }
        catch (Exception ex) { Warn("lobby.is_local_ready", ex); }

        var players = new List<Dictionary<string, object?>>();
        ulong localId;
        try { localId = lobby.LocalPlayer.id; }
        catch (Exception ex) { Warn("lobby.local_player_id", ex); localId = 0; }
        ulong hostId = lobby.NetService.Type == NetGameType.Host ? localId : 0;

        foreach (var p in lobby.Players)
        {
            var entry = new Dictionary<string, object?>
            {
                ["id"] = p.id.ToString(),
                ["slot_id"] = p.slotId,
                ["is_local"] = p.id == localId,
                // We can only positively identify the host as "us" when we ARE the host;
                // a client doesn't know which remote id is the host without inspecting
                // the net service. Keep it simple and only flag is_host=true for self
                // when hosting — clients can infer host-ness by player_id when needed.
                ["is_host"] = p.id == hostId && hostId != 0,
                ["character"] = SafeGetText(() => p.character?.Title)
                                ?? p.character?.Id.Entry,
                ["character_id"] = p.character?.Id.Entry,
                ["is_ready"] = p.isReady,
                ["platform_name"] = SafeGetPlayerName(lobby.NetService.Platform, p.id)
            };
            players.Add(entry);
        }
        lobbyState["players"] = players;
        lobbyState["player_count"] = players.Count;

        if (!string.IsNullOrEmpty(lobby.Seed))
            lobbyState["seed"] = lobby.Seed;

        return lobbyState;
    }

    private static bool SafeIsAboutToBeginGame(StartRunLobby lobby)
    {
        try { return lobby.IsAboutToBeginGame(); }
        catch (Exception ex) { Warn("lobby.is_about_to_begin", ex); return false; }
    }

    private static string? SafeGetPlayerName(PlatformType platform, ulong playerId)
    {
        try { return PlatformUtil.GetPlayerName(platform, playerId); }
        catch (Exception ex) { Warn("lobby.players.platform_name", ex); return null; }
    }

    private static void AddMultiplayerJoinMenuState(
        Dictionary<string, object?> result,
        NJoinFriendScreen joinScreen)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "multiplayer_join";

        // FastMP: when Steam isn't initialized OR --fastmp is set, OnSubmenuOpened auto-
        // joins localhost:33771 instead of presenting friends. This is a debug/local-dev
        // path. We surface it so callers don't try to hit "refresh" expecting a list.
        bool fastMp = !SteamInitializer.Initialized || CommandLineHelper.HasArg("fastmp");
        result["fast_mp"] = fastMp;

        var loadingFriends = GetInstanceFieldValue(joinScreen, "_loadingFriendsIndicator") as Control;
        var loadingOverlay = GetInstanceFieldValue(joinScreen, "_loadingOverlay") as Control;
        bool loading = (loadingFriends != null && loadingFriends.Visible)
                       || (loadingOverlay != null && loadingOverlay.Visible);
        result["loading"] = loading;

        var noFriendsLabel = GetInstanceFieldValue(joinScreen, "_noFriendsLabel") as Control;
        bool noFriends = noFriendsLabel != null && noFriendsLabel.Visible;
        result["no_friends"] = noFriends;

        var friends = new List<Dictionary<string, object?>>();
        var options = new List<Dictionary<string, object?>>();

        var buttonContainer = GetInstanceFieldValue(joinScreen, "_buttonContainer") as Control;
        if (buttonContainer != null)
        {
            int index = 0;
            foreach (var child in buttonContainer.GetChildren())
            {
                if (child is NJoinFriendButton friendBtn)
                {
                    string? name = null;
                    try { name = PlatformUtil.GetPlayerName(PlatformUtil.PrimaryPlatform, friendBtn.PlayerId); }
                    catch (Exception ex) { Warn("menu.multiplayer_join.friends.name", ex); }

                    friends.Add(new Dictionary<string, object?>
                    {
                        ["index"] = index,
                        ["name"] = name,
                        ["player_id"] = friendBtn.PlayerId.ToString(),
                        ["enabled"] = friendBtn.IsEnabled
                    });
                    options.Add(new Dictionary<string, object?>
                    {
                        ["name"] = $"join_{index}",
                        ["enabled"] = friendBtn.IsEnabled
                    });
                    index++;
                }
            }
        }
        result["friends"] = friends;

        var refreshBtn = GetInstanceFieldValue(joinScreen, "_refreshButton") as NClickableControl;
        if (refreshBtn != null && IsNodeVisible(refreshBtn))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "refresh",
                ["enabled"] = refreshBtn.IsEnabled && !loading
            });
        }
        AddMenuOptionIfVisible(options, joinScreen, "_backButton", "back");

        if (fastMp)
        {
            result["message"] = loading
                ? "FastMP join flow is connecting to localhost:33771..."
                : "FastMP mode (no Steam): join screen auto-connects to localhost:33771.";
        }
        else if (loading)
        {
            result["message"] = "Refreshing friend list...";
        }
        else if (noFriends)
        {
            result["message"] = "No friends with open lobbies. Use 'refresh' to retry, or 'back' to return.";
        }
        else
        {
            result["message"] = "Pick a friend to join, or 'refresh' to update the list.";
        }

        result["options"] = options;
    }

    private static void AddMultiplayerLoadLobbyMenuState(
        Dictionary<string, object?> result,
        NMultiplayerLoadGameScreen loadLobby)
    {
        result["state_type"] = "menu";
        result["menu_screen"] = "multiplayer_load_lobby";

        var lobby = GetInstanceFieldValue(loadLobby, "_runLobby") as LoadRunLobby;
        if (lobby != null)
        {
            var info = new Dictionary<string, object?>
            {
                ["type"] = lobby.NetService.Type switch
                {
                    NetGameType.Host => "host",
                    NetGameType.Client => "client",
                    _ => lobby.NetService.Type.ToString().ToLowerInvariant()
                },
                ["game_mode"] = lobby.GameMode.ToString().ToLowerInvariant(),
                ["ascension"] = lobby.Run?.Ascension ?? 0,
                ["act"] = (lobby.Run?.CurrentActIndex ?? 0) + 1,
                ["floor"] = lobby.Run?.VisitedMapCoords?.Count ?? 0
            };

            try
            {
                var localPlayer = lobby.Run?.Players?.FirstOrDefault(p => p.NetId == lobby.NetService.NetId);
                if (localPlayer != null)
                {
                    info["character_id"] = localPlayer.CharacterId?.Entry;
                    info["current_hp"] = localPlayer.CurrentHp;
                    info["max_hp"] = localPlayer.MaxHp;
                    info["gold"] = localPlayer.Gold;
                }
            }
            catch (Exception ex) { Warn("menu.multiplayer_load_lobby.local_player", ex); }

            info["expected_player_count"] = lobby.Run?.Players?.Count ?? 0;
            // v0.111: connected players are the lobby's Players (PlayerCount/PlayerIds);
            // the old ConnectedPlayerIds property was removed.
            info["connected_player_count"] = lobby.PlayerCount;

            // v0.111 restores LoadRunLobby.IsAboutToBeginGame(); use it directly.
            bool aboutToBegin = false;
            try { aboutToBegin = lobby.IsAboutToBeginGame(); }
            catch (Exception ex) { Warn("menu.multiplayer_load_lobby.is_about_to_begin", ex); }
            info["all_ready"] = aboutToBegin;
            info["is_about_to_begin"] = aboutToBegin;

            // Per-player ready/connected breakdown
            var players = new List<Dictionary<string, object?>>();
            try
            {
                if (lobby.Run?.Players != null)
                {
                    foreach (var sp in lobby.Run.Players)
                    {
                        bool isConnected = lobby.PlayerIds.Contains(sp.NetId);
                        bool isReady = false;
                        try { isReady = lobby.IsPlayerReady(sp.NetId); }
                        catch (Exception ex) { Warn("menu.multiplayer_load_lobby.players.is_ready", ex); }
                        players.Add(new Dictionary<string, object?>
                        {
                            ["id"] = sp.NetId.ToString(),
                            ["is_local"] = sp.NetId == lobby.NetService.NetId,
                            ["character_id"] = sp.CharacterId?.Entry,
                            ["is_connected"] = isConnected,
                            ["is_ready"] = isReady,
                            ["platform_name"] = SafeGetPlayerName(lobby.NetService.Platform, sp.NetId)
                        });
                    }
                }
            }
            catch (Exception ex) { Warn("menu.multiplayer_load_lobby.players", ex); }
            info["players"] = players;

            result["lobby"] = info;
        }

        var options = new List<Dictionary<string, object?>>();

        var confirmBtn = GetInstanceFieldValue(loadLobby, "_confirmButton") as NClickableControl;
        if (confirmBtn != null && IsNodeVisible(confirmBtn))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "confirm",
                ["enabled"] = confirmBtn.IsEnabled
            });
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "embark",
                ["enabled"] = confirmBtn.IsEnabled
            });
        }

        var backBtn2 = GetInstanceFieldValue(loadLobby, "_backButton") as NClickableControl;
        if (backBtn2 != null && IsNodeVisible(backBtn2))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "back",
                ["enabled"] = backBtn2.IsEnabled
            });
        }

        var unreadyBtn2 = GetInstanceFieldValue(loadLobby, "_unreadyButton") as NClickableControl;
        if (unreadyBtn2 != null && IsNodeVisible(unreadyBtn2))
        {
            options.Add(new Dictionary<string, object?>
            {
                ["name"] = "unready",
                ["enabled"] = unreadyBtn2.IsEnabled
            });
        }

        result["options"] = options;
        result["message"] = "Multiplayer load lobby. Confirm to ready up; once everyone is connected and ready, the run resumes.";
    }

    private static Dictionary<string, object?> BuildBattleState(RunState runState, CombatRoom combatRoom)
    {
        var combatState = CombatManager.Instance.DebugOnlyGetState();
        var battle = new Dictionary<string, object?>();

        if (combatState == null)
        {
            battle["error"] = "Combat state unavailable";
            return battle;
        }

        RefreshCardUidRegistry(combatState);

        battle["round"] = combatState.RoundNumber;
        battle["turn"] = combatState.CurrentSide.ToString().ToLower();
        battle["is_play_phase"] = IsPlayPhase(combatState);

        battle["enemies"] = BuildEnemyList(combatState);

        return battle;
    }

    private static Dictionary<string, object?> BuildPlayerState(Player player)
    {
        var state = new Dictionary<string, object?>();
        var creature = player.Creature;
        var combatState = player.PlayerCombatState;

        state["character"] = SafeGetText(() => player.Character.Title);
        state["hp"] = creature.CurrentHp;
        state["max_hp"] = creature.MaxHp;
        state["block"] = creature.Block;

        // PlayerCombatState can linger after combat while on map/rest/shop. Energy/MaxEnergy getters
        // run hooks (e.g. Hook.ModifyMaxEnergy) that null-ref without a live combat - only serialize
        // combat fields when a fight is actually in progress.
        bool inCombat = false;
        if (combatState != null && CombatManager.Instance.IsInProgress)
        {
            inCombat = true;
            state["energy"] = combatState.Energy;
            state["max_energy"] = combatState.MaxEnergy;

            // Stars (The Regent's resource, conditionally shown)
            if (player.Character.ShouldAlwaysShowStarCounter || combatState.Stars > 0)
            {
                state["stars"] = combatState.Stars;
            }

            // Hand
            var hand = new List<Dictionary<string, object?>>();
            int cardIndex = 0;
            foreach (var card in combatState.Hand.Cards)
            {
                hand.Add(BuildCardState(card, cardIndex));
                cardIndex++;
            }
            state["hand"] = hand;

            // Pile counts
            state["draw_pile_count"] = combatState.DrawPile.Cards.Count;
            state["discard_pile_count"] = combatState.DiscardPile.Cards.Count;
            state["exhaust_pile_count"] = combatState.ExhaustPile.Cards.Count;

            // Pile contents (draw pile sorted by rarity then card ID, matching in-game display)
            var drawCards = combatState.DrawPile.Cards.ToList();
            drawCards.Sort((c1, c2) => c1.Rarity != c2.Rarity
                ? c1.Rarity.CompareTo(c2.Rarity)
                : string.Compare(c1.Id.Entry, c2.Id.Entry, StringComparison.Ordinal));
            state["draw_pile"] = BuildPileCardList(drawCards, PileType.Draw);
            state["discard_pile"] = BuildPileCardList(combatState.DiscardPile.Cards, PileType.Discard);
            state["exhaust_pile"] = BuildPileCardList(combatState.ExhaustPile.Cards, PileType.Exhaust);

            // Orbs
            var orbQueue = combatState.OrbQueue;
            if (orbQueue != null && orbQueue.Capacity > 0)
            {
                var orbs = new List<Dictionary<string, object?>>();
                foreach (var orb in orbQueue.Orbs)
                {
                    // Populate SmartDescription placeholders with Focus-modified values,
                    // mirroring OrbModel.HoverTips getter (OrbModel.cs:92-94)
                    string? description = SafeGetText(() =>
                    {
                        var desc = orb.SmartDescription;
                        desc.Add("energyPrefix", orb.Owner.Character.CardPool.Title);
                        desc.Add("Passive", orb.PassiveVal);
                        desc.Add("Evoke", orb.EvokeVal);
                        return desc;
                    });
                    orbs.Add(new Dictionary<string, object?>
                    {
                        ["id"] = orb.Id.Entry,
                        ["name"] = SafeGetText(() => orb.Title),
                        ["description"] = description,
                        ["passive_val"] = orb.PassiveVal,
                        ["evoke_val"] = orb.EvokeVal,
                        ["keywords"] = BuildHoverTips(orb.HoverTips)
                    });
                }
                state["orbs"] = orbs;
                state["orb_slots"] = orbQueue.Capacity;
                state["orb_empty_slots"] = orbQueue.Capacity - orbQueue.Orbs.Count;
            }

            // Pets (Osty for Necrobinder)
            var pets = BuildPetsState(player);
            if (pets.Count > 0)
            {
                state["pets"] = pets;
            }
        }

        state["gold"] = player.Gold;

        // Powers (status effects)
        state["status"] = BuildPowersState(creature);

        // Relics
        var relics = new List<Dictionary<string, object?>>();
        foreach (var relic in player.Relics)
        {
            relics.Add(new Dictionary<string, object?>
            {
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["counter"] = relic.ShowCounter ? relic.DisplayAmount : null,
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
        }
        state["relics"] = relics;

        // Potions
        var potions = new List<Dictionary<string, object?>>();
        int slotIndex = 0;
        foreach (var potion in player.PotionSlots)
        {
            if (potion != null)
            {
                potions.Add(new Dictionary<string, object?>
                {
                    ["id"] = potion.Id.Entry,
                    ["name"] = SafeGetText(() => potion.Title),
                    ["description"] = SafeGetText(() => potion.DynamicDescription),
                    ["slot"] = slotIndex,
                    ["can_use_in_combat"] = potion.Usage == PotionUsage.CombatOnly || potion.Usage == PotionUsage.AnyTime,
                    ["target_type"] = potion.TargetType.ToString(),
                    ["keywords"] = BuildHoverTips(potion.ExtraHoverTips)
                });
            }
            slotIndex++;
        }
        state["potions"] = potions;
        state["max_potion_slots"] = player.MaxPotionCount;

        AddDeckState(state, player, inCombat);

        return state;
    }

    /// <summary>
    /// The master deck, which every out-of-combat decision is made against: a card reward,
    /// a smith, a shop removal and a transform event all asked the caller to choose without
    /// ever showing what it already owns.
    ///
    /// Combat clones the deck into the draw pile (Player.PopulateCombatState), so Player.Deck
    /// stays intact and its count is meaningful on every screen. The card list is only sent
    /// outside combat: in a fight the hand plus the three piles already enumerate every card,
    /// and a second full copy would roughly double the payload of the largest state there is.
    /// </summary>
    private static void AddDeckState(Dictionary<string, object?> state, Player player, bool inCombat)
    {
        // Player.Deck is created with the Player, but a run in character select has not been
        // through PopulateStartingDeck yet, so an empty deck is a normal state, not an error.
        var deckCards = player.Deck?.Cards;
        if (deckCards == null)
            return;

        state["deck_count"] = deckCards.Count;
        if (inCombat)
            return;

        var deck = new List<Dictionary<string, object?>>();
        int deckIndex = 0;
        foreach (var card in deckCards)
        {
            var cardInfo = BuildCardInfo(card, PileType.Deck);
            cardInfo["index"] = deckIndex;
            deck.Add(cardInfo);
            deckIndex++;
        }
        state["deck"] = deck;
    }

    private static string GetCostDisplay(CardModel card)
        => card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetAmountToSpend().ToString();

    private static string? GetStarCostDisplay(CardModel card)
    {
        if (card.HasStarCostX) return "X";
        if (card.CurrentStarCost >= 0) return card.GetStarCostWithModifiers().ToString();
        return null;
    }

    /// <summary>
    /// Builds the common card display fields shared across all card serialization contexts.
    /// Callers merge context-specific fields (e.g. index, can_play, target_type) on top.
    /// </summary>
    private static Dictionary<string, object?> BuildCardInfo(CardModel card, PileType pile = PileType.None)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = card.Id.Entry,
            ["name"] = SafeGetText(() => card.Title),
            ["type"] = card.Type.ToString(),
            ["cost"] = GetCostDisplay(card),
            ["star_cost"] = GetStarCostDisplay(card),
            ["description"] = SafeGetCardDescription(card, pile),
            ["rarity"] = card.Rarity.ToString(),
            ["is_upgraded"] = card.IsUpgraded,
            ["is_upgradable"] = card.IsUpgradable,
            ["upgrade_level"] = card.CurrentUpgradeLevel,
            ["max_upgrade_level"] = card.MaxUpgradeLevel,
            ["keywords"] = BuildHoverTips(card.HoverTips)
        };
    }

    /// <summary>
    /// The card as it would read after one upgrade, or null when it cannot be upgraded
    /// or the preview clone failed. Built from the real upgraded clone, so the numbers
    /// are the game's own rather than something derived from the card text.
    /// </summary>
    private static Dictionary<string, object?>? BuildUpgradePreviewInfo(CardModel card)
    {
        var preview = SafeBuildUpgradedCardPreview(card);
        if (preview == null) return null;

        return new Dictionary<string, object?>
        {
            ["name"] = SafeGetText(() => preview.Title),
            ["cost"] = GetCostDisplay(preview),
            ["star_cost"] = GetStarCostDisplay(preview),
            ["description"] = SafeGetCardDescription(preview),
            ["keywords"] = BuildHoverTips(preview.HoverTips)
        };
    }

    /// <summary>
    /// Adds "upgrade_preview" to a card entry on the screens where an upgrade or a pick
    /// is being decided. Cards already at their last upgrade level are skipped:
    /// IsUpgraded alone is not the test, because a multi-level card at level 1 of 2 is
    /// upgraded and still has an upgrade left.
    /// </summary>
    private static void AttachUpgradePreview(Dictionary<string, object?> cardInfo, CardModel card)
    {
        if (!card.IsUpgradable || card.CurrentUpgradeLevel >= card.MaxUpgradeLevel) return;

        var preview = BuildUpgradePreviewInfo(card);
        if (preview != null) cardInfo["upgrade_preview"] = preview;
    }

    private static Dictionary<string, object?> BuildCardState(CardModel card, int index)
    {
        card.CanPlay(out var unplayableReason, out _);

        var state = BuildCardInfo(card);
        state["index"] = index;
        state["uid"] = GetStableCardUid(card);
        state["description"] = SafeGetCardDescription(card); // hand cards use default pile
        state["target_type"] = card.TargetType.ToString();
        state["can_play"] = unplayableReason == UnplayableReason.None;
        state["unplayable_reason"] = unplayableReason != UnplayableReason.None ? unplayableReason.ToString() : null;

        var vsTargets = BuildCardTargetPreviews(card);
        if (vsTargets.Count > 0)
            state["vs_targets"] = vsTargets;

        return state;
    }

    /// <summary>
    /// What this card actually does to each living enemy right now.
    ///
    /// The number printed on a card has an inconsistent set of modifiers baked in:
    /// Strength and Pen Nib show up, Vulnerable and Weak sometimes do and sometimes
    /// don't, because the game only folds target-side modifiers in while the card is
    /// hovered over that target. So a caller reading the card text had to guess which
    /// multipliers were already applied — 60 expected, 43 dealt.
    ///
    /// This runs the same preview the UI runs on hover (CardModel.UpdateDynamicVarPreview,
    /// whose CalculatedDamageVar goes through Hook.ModifyDamage with the target) once per
    /// enemy, reports the resolved damage, hit count and target-specific description, then
    /// clears the preview so the live card is left exactly as it was found.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildCardTargetPreviews(CardModel card)
    {
        var previews = new List<Dictionary<string, object?>>();

        if (card.TargetType is not (TargetType.AnyEnemy or TargetType.AllEnemies or TargetType.RandomEnemy))
            return previews;

        ICombatState? combatState;
        try { combatState = card.Owner?.Creature?.CombatState; }
        catch (Exception ex) { Warn("card.vs_targets.combat_state", ex); return previews; }
        if (combatState == null)
            return previews;

        try
        {
            foreach (var enemy in combatState.Enemies)
            {
                if (!enemy.IsAlive) continue;

                try
                {
                    card.UpdateDynamicVarPreview(CardPreviewMode.Normal, enemy, card.DynamicVars);

                    var preview = new Dictionary<string, object?>
                    {
                        ["target"] = GetStableEntityId(enemy),
                        ["combat_id"] = enemy.CombatId
                    };

                    // Var names differ per card (Damage, CalculatedDamage, ExtraDamage...),
                    // and the typed accessors throw when a card has no var of that name,
                    // so read whatever the card actually carries.
                    var values = ReadDynamicVarValues(card);
                    int? damage = PickDynamicVar(values, "CalculatedDamage", "Damage");
                    if (damage != null)
                        preview["damage"] = damage;

                    int? hits = PickDynamicVar(values, "Repeat");
                    if (hits != null && hits > 1)
                    {
                        preview["hits"] = hits;
                        if (damage != null)
                            preview["total_damage"] = damage * hits;
                    }

                    int? block = PickDynamicVar(values, "CalculatedBlock", "Block");
                    if (block != null)
                        preview["block"] = block;

                    if (damage != null)
                    {
                        var cap = BuildDamageCap(enemy, damage.Value, hits ?? 1);
                        if (cap != null)
                            preview["damage_cap"] = cap;
                    }

                    if (values.Count > 0)
                        preview["values"] = values;

                    preview["description"] = SafeGetTargetedCardDescription(card, enemy);
                    previews.Add(preview);
                }
                catch (Exception ex) { Warn("card.vs_targets", ex); }
            }
        }
        finally
        {
            // Always hand the card back the way it was found.
            try { card.DynamicVars.ClearPreview(); }
            catch (Exception ex) { Warn("card.vs_targets.clear_preview", ex); }
            try { card.UpdateDynamicVarPreview(CardPreviewMode.Normal, null, card.DynamicVars); }
            catch (Exception ex) { Warn("card.vs_targets.restore_preview", ex); }
        }

        return previews;
    }

    /// <summary>
    /// Every dynamic var on this card, by name, at its previewed value.
    ///
    /// Reads PreviewValue, not IntValue: IntValue is <c>(int)BaseValue</c>, i.e. the
    /// printed number before any modifier. The preview run just before this is what
    /// folds in Strength, the target's Vulnerable and everything else ModifyDamage
    /// applies, and it writes its result to PreviewValue.
    /// </summary>
    private static Dictionary<string, object?> ReadDynamicVarValues(CardModel card)
    {
        var values = new Dictionary<string, object?>();
        try
        {
            foreach (var (name, dynamicVar) in card.DynamicVars)
            {
                int? value = SafeGetInt(() => (int)System.Math.Round(dynamicVar.PreviewValue,
                    System.MidpointRounding.ToZero));
                value ??= SafeGetInt(() => dynamicVar.IntValue);
                if (value != null)
                    values[name] = value;
                else
                    Warn($"card.values.{name}", "neither PreviewValue nor IntValue could be read");
            }
        }
        catch (Exception ex) { Warn("card.values", ex); }
        return values;
    }

    /// <summary>
    /// Powers that cap how much HP a creature can lose, per hit. Damage modifiers do not
    /// see these — they are applied when the damage lands — so a 18-damage card against
    /// Slippery takes the enemy down by 1, and reporting 18 sends the caller into a
    /// fight it cannot win. Keyed by power type because that survives an id rename.
    /// </summary>
    private static readonly Dictionary<string, string> _perHitDamageCaps = new()
    {
        ["SlipperyPower"] = "next hit reduced to 1",
        ["IntangiblePower"] = "all damage reduced to 1",
        ["HardToKillPower"] = "all damage reduced to the power's amount"
    };

    private const string HardenedShellPowerName = "HardenedShellPower";

    /// <summary>
    /// Describes any cap on what this target can actually lose from one hit, and the
    /// resulting effective damage. Returns null when nothing caps it.
    /// </summary>
    private static Dictionary<string, object?>? BuildDamageCap(Creature target, int damage, int hits)
    {
        try
        {
            foreach (var power in target.Powers)
            {
                string typeName = power.GetType().Name;

                if (_perHitDamageCaps.TryGetValue(typeName, out var note))
                {
                    int perHit = typeName == "HardToKillPower" ? System.Math.Max(power.Amount, 0) : 1;
                    int capped = System.Math.Min(damage, perHit);
                    int remaining = typeName == "SlipperyPower" ? System.Math.Max(power.Amount, 0) : int.MaxValue;
                    int cappedHits = System.Math.Min(hits, remaining);

                    return new Dictionary<string, object?>
                    {
                        ["capped_by"] = SafeGetText(() => power.Title) ?? typeName,
                        ["power_id"] = SafeGetText(() => power.Id.Entry),
                        ["per_hit_max"] = perHit,
                        ["note"] = note,
                        ["effective_damage"] = capped * cappedHits + System.Math.Max(hits - cappedHits, 0) * damage
                    };
                }

                if (typeName == HardenedShellPowerName)
                {
                    return new Dictionary<string, object?>
                    {
                        ["capped_by"] = SafeGetText(() => power.Title) ?? typeName,
                        ["power_id"] = SafeGetText(() => power.Id.Entry),
                        ["per_turn_max"] = power.Amount,
                        ["note"] = "total HP lost this turn is capped, not per hit"
                    };
                }
            }
        }
        catch (Exception ex) { Warn("card.damage_cap", ex); }

        return null;
    }

    private static int? PickDynamicVar(Dictionary<string, object?> values, params string[] names)
    {
        foreach (var name in names)
        {
            if (values.TryGetValue(name, out var value) && value is int i)
                return i;
        }
        return null;
    }

    private static string? SafeGetTargetedCardDescription(CardModel card, Creature target)
    {
        try { return StripRichTextTags(card.GetDescriptionForPile(PileType.Hand, target)).Replace("\n", " "); }
        catch (Exception ex) { Warn("card.vs_targets.description", ex); return null; }
    }

    private static void AddPreviewCardsFromContainer(
        Godot.Control? container,
        List<Dictionary<string, object?>> previewCards)
    {
        if (container?.Visible != true)
            return;

        var cardHolders = FindAllSortedByPosition<NCardHolder>(container);
        if (cardHolders.Count > 0)
        {
            foreach (var holder in cardHolders)
            {
                var card = holder.CardModel;
                if (card == null) continue;

                var cardInfo = BuildCardInfo(card);
                cardInfo["index"] = previewCards.Count;
                previewCards.Add(cardInfo);
            }
            return;
        }

        foreach (var holder in FindAll<NPreviewCardHolder>(container))
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = previewCards.Count;
            previewCards.Add(cardInfo);
        }
    }

    private static List<Dictionary<string, object?>> BuildPileCardList(IEnumerable<CardModel> cards, PileType pile)
    {
        var list = new List<Dictionary<string, object?>>();
        foreach (var card in cards)
        {
            // Pile cards only need a subset - keep it lightweight
            list.Add(new Dictionary<string, object?>
            {
                ["uid"] = GetStableCardUid(card),
                ["name"] = SafeGetText(() => card.Title),
                ["cost"] = GetCostDisplay(card),
                ["star_cost"] = GetStarCostDisplay(card),
                ["description"] = SafeGetCardDescription(card, pile)
            });
        }
        return list;
    }

    // entity_id registry
    //
    // entity_ids used to be generated by counting the *alive* enemies each time state
    // was built (and again, independently, when resolving a target). That made the same
    // string point at a different creature as soon as one died between the read and the
    // action, so "attack EXOSKELETON_1" silently hit the wrong bug or failed outright.
    //
    // Ids are now assigned once per creature, on first sight, and keyed by the stable
    // CombatId. A creature keeps its id for the whole fight, even after its neighbours die.
    private static readonly Dictionary<uint, string> _entityIdByCombatId = new();
    private static readonly Dictionary<string, int> _entityIdNextIndex = new();
    private static ICombatState? _entityIdCombatToken;

    // card uid registry
    //
    // card_index is a position in the hand, so playing one card renumbers every card
    // behind it. A plan built from one state read ("play 4, then 2, then 0") therefore
    // has to be recomputed after every single play, and getting the arithmetic wrong
    // plays the wrong card without any error.
    //
    // Each card instance gets a uid on first sight that stays with it for the whole
    // combat, wherever it moves between hand, draw, discard and exhaust.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CardModel, string> _cardUids = new();
    private static readonly Dictionary<string, int> _cardUidNextIndex = new();
    private static object? _cardUidCombatToken;

    /// <summary>
    /// Returns this card instance's uid, assigning one on first sight.
    /// Format is "<card_id>#<n>", counting instances of that card in the order first seen.
    /// </summary>
    internal static string GetStableCardUid(CardModel card)
    {
        if (_cardUids.TryGetValue(card, out var existing))
            return existing;

        string baseId = SafeGetText(() => card.Id.Entry) ?? "unknown";
        if (!_cardUidNextIndex.TryGetValue(baseId, out int next))
            next = 0;
        _cardUidNextIndex[baseId] = next + 1;

        string uid = $"{baseId}#{next}";
        _cardUids.Add(card, uid);
        return uid;
    }

    /// <summary>Clears card uids when a new combat starts.</summary>
    internal static void RefreshCardUidRegistry(ICombatState combatState)
    {
        if (ReferenceEquals(_cardUidCombatToken, combatState))
            return;

        _cardUidCombatToken = combatState;
        _cardUids.Clear();
        _cardUidNextIndex.Clear();
    }

    /// <summary>
    /// Assigns stable entity_ids for every enemy in the given combat, clearing the
    /// registry when a new combat starts. Call before serializing enemies.
    /// </summary>
    internal static void RefreshEntityIdRegistry(ICombatState combatState)
    {
        if (!ReferenceEquals(_entityIdCombatToken, combatState))
        {
            _entityIdCombatToken = combatState;
            _entityIdByCombatId.Clear();
            _entityIdNextIndex.Clear();
        }

        foreach (var creature in combatState.Enemies)
            _ = GetStableEntityId(creature);
    }

    /// <summary>
    /// Returns this creature's entity_id, assigning one on first sight.
    /// The suffix counts creatures of the same model in the order they were first seen,
    /// so it never shifts when another enemy dies.
    /// </summary>
    internal static string GetStableEntityId(Creature creature)
    {
        string baseId = creature.Monster?.Id.Entry ?? "unknown";

        uint? combatId = creature.CombatId;
        if (combatId == null)
            return $"{baseId}_0";

        if (_entityIdByCombatId.TryGetValue(combatId.Value, out var existing))
            return existing;

        if (!_entityIdNextIndex.TryGetValue(baseId, out int next))
            next = 0;
        _entityIdNextIndex[baseId] = next + 1;

        string entityId = $"{baseId}_{next}";
        _entityIdByCombatId[combatId.Value] = entityId;
        return entityId;
    }

    /// <summary>
    /// Every enemy the fight still contains, alive or waiting to come back.
    ///
    /// A killed enemy normally leaves ICombatState.Enemies, so listing the survivors used
    /// to be the same thing as listing the list. A Decimillipede segment breaks that: its
    /// ReattachPower answers false to ShouldCreatureBeRemovedFromCombatAfterDeath, so the
    /// corpse stays in the fight and heals itself back two enemy turns later. Dropping it
    /// hid the revive entirely - the caller spent those turns hitting the other segments
    /// without knowing one was about to stand back up.
    ///
    /// Alive enemies are reported as before. Dead ones are reported with alive:false only
    /// when the game itself refuses to remove them; a corpse that is merely still being
    /// unwound from the list (CreatureCmd defers removal while its monster is mid-move) is
    /// left out, because it is leaving.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildEnemyList(ICombatState combatState)
    {
        var enemies = new List<Dictionary<string, object?>>();
        RefreshEntityIdRegistry(combatState);
        // Enemies can appear twice in the list while the combat state is being rebuilt
        // (splits and spawns), which printed every line of that enemy twice in markdown.
        var seenCombatIds = new HashSet<uint>();
        foreach (var creature in combatState.Enemies)
        {
            if (!creature.IsAlive && !StaysInCombatWhileDead(combatState, creature))
                continue;
            if (creature.CombatId is uint id && !seenCombatIds.Add(id))
                continue;
            enemies.Add(BuildEnemyState(creature));
        }
        return enemies;
    }

    /// <summary>
    /// Whether the game is deliberately keeping this dead creature in the fight.
    ///
    /// CreatureCmd.KillWithoutCheckingWinCondition removes a dead enemy from
    /// ICombatState.Enemies only when Hook.ShouldCreatureBeRemovedFromCombatAfterDeath
    /// agrees, and every power that wants to outlive its owner (ReattachPower,
    /// DieForYouPower, IllusionPower...) votes no there. Asking the same predicate is
    /// therefore the game's own answer, and a sharper test than "dead but still listed":
    /// removal is also deferred for a monster that is mid-move, so the list briefly holds
    /// corpses that are on their way out.
    /// </summary>
    private static bool StaysInCombatWhileDead(ICombatState combatState, Creature creature)
    {
        try { return !Hook.ShouldCreatureBeRemovedFromCombatAfterDeath(combatState, creature); }
        catch (Exception ex) { Warn("enemy.stays_in_combat_while_dead", ex); return false; }
    }

    private static Dictionary<string, object?> BuildEnemyState(Creature creature)
    {
        var monster = creature.Monster;
        string entityId = GetStableEntityId(creature);
        bool alive = creature.IsAlive;

        var state = new Dictionary<string, object?>
        {
            ["entity_id"] = entityId,
            ["combat_id"] = creature.CombatId,
            ["name"] = SafeGetText(() => monster?.Title),
            // Explicit on every enemy, alive or not: a caller that wants only living
            // targets can filter on one field instead of inferring death from hp == 0.
            ["alive"] = alive,
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["block"] = creature.Block,
            ["status"] = BuildPowersState(creature)
        };

        if (!alive)
        {
            var revive = BuildReviveState(creature);
            if (revive != null)
                state["revive"] = revive;
        }

        // Intents
        if (monster?.NextMove is MoveState moveState)
        {
            var intents = new List<Dictionary<string, object?>>();
            foreach (var intent in moveState.Intents)
            {
                var intentData = new Dictionary<string, object?>
                {
                    ["type"] = intent.IntentType.ToString()
                };
                try
                {
                    var targets = creature.CombatState?.PlayerCreatures;
                    if (targets != null)
                    {
                        string label = intent.GetIntentLabel(targets, creature).GetFormattedText();
                        intentData["label"] = StripRichTextTags(label);

                        var hoverTip = intent.GetHoverTip(targets, creature);
                        if (hoverTip.Title != null)
                            intentData["title"] = StripRichTextTags(hoverTip.Title);
                        if (hoverTip.Description != null)
                            intentData["description"] = StripRichTextTags(hoverTip.Description);
                    }
                }
                catch (Exception ex) { Warn("enemy.intent", ex); }
                intents.Add(intentData);
            }
            state["intents"] = intents;
        }

        return state;
    }

    /// <summary>
    /// What a dead-but-still-present enemy is waiting for, when it is waiting to come back.
    ///
    /// The one case in the game today is the Decimillipede: killing a segment runs
    /// ReattachPower.AfterDeath, which pushes the monster straight into its DEAD_MOVE and
    /// leaves the corpse in the fight; DEAD_MOVE's follow-up is REATTACH_MOVE, which calls
    /// ReattachPower.DoReattach and heals the segment back up by the power's Amount (25).
    /// One move is performed per enemy turn, so the segment is back two enemy turns after
    /// it fell, and the API said nothing about it.
    ///
    /// Returns null for a corpse with no heal move ahead of it (DieForYouPower and friends
    /// also keep their owner in the fight, but it stays dead) - "revive" present means
    /// "this is coming back", never merely "this is dead".
    /// </summary>
    private static Dictionary<string, object?>? BuildReviveState(Creature creature)
    {
        var power = FindPowerKeepingCorpseInCombat(creature);

        // ReattachPower.DoReattach bails out when every other segment is dead too - that
        // is the kill, and the whole Decimillipede fades out. Saying "back in 2 turns"
        // while the fight is ending would be exactly backwards, so check the same thing
        // the power checks: another living holder of the same power.
        if (power != null && !HasLivingPeerWithPower(creature, power))
            return null;

        int? turns = TurnsUntilRevive(creature);
        if (turns == null)
            return null;

        var revive = new Dictionary<string, object?>
        {
            ["in_turns"] = turns
        };

        if (power != null)
        {
            revive["power_id"] = SafeGetText(() => power.Id.Entry);
            revive["power_name"] = SafeGetText(() => power.Title);
            // ReattachPower heals by its own Amount, so its stack count is the HP the
            // segment comes back with. Reported as null rather than guessed for a power
            // whose amount is not a heal.
            int? amount = SafeGetInt(() => power.Amount);
            revive["hp"] = amount > 0 ? amount : null;
        }

        return revive;
    }

    /// <summary>
    /// Whether another living enemy carries the same power - ReattachPower's
    /// "are all my other segments dead?" test, asked without naming the power.
    /// </summary>
    private static bool HasLivingPeerWithPower(Creature creature, PowerModel power)
    {
        try
        {
            var combatState = creature.CombatState;
            if (combatState == null)
                return true;

            foreach (var other in combatState.Enemies)
            {
                if (ReferenceEquals(other, creature) || !other.IsAlive)
                    continue;
                if (other.HasPower(power.Id))
                    return true;
            }
            return false;
        }
        catch (Exception ex) { Warn("enemy.revive.peers", ex); return true; }
    }

    /// <summary>
    /// Enemy turns until this dead creature heals itself back up, read off the monster's
    /// own move state machine: the moves are performed one per enemy turn along the
    /// MoveState.FollowUpState chain, so the position of the heal move in that chain is
    /// the answer. 2 while the segment still has to sit through DEAD_MOVE, 1 once
    /// REATTACH_MOVE is the move it will perform next.
    ///
    /// Nothing exposes a countdown directly - ReattachPower's only bookkeeping is a
    /// private "isReviving" flag on its internal data, with no turn counter at all - so
    /// the chain is the reading, not a second source that could be cross-checked.
    /// Null when no heal move is reachable: the walk stops at the first state that is not
    /// a plain MoveState (a RandomBranchState has not picked its next move yet) and after
    /// a few steps, so a heal further away than that is reported as unknown rather than
    /// guessed.
    /// </summary>
    private static int? TurnsUntilRevive(Creature creature)
    {
        const int maxLookahead = 4;
        try
        {
            MonsterState? state = creature.Monster?.NextMove;
            for (int turns = 1; turns <= maxLookahead; turns++)
            {
                if (state is not MoveState move)
                    return null;
                if (move.Intents.Any(intent => intent.IntentType == IntentType.Heal))
                    return turns;
                state = move.FollowUpState;
            }
        }
        catch (Exception ex) { Warn("enemy.revive.in_turns", ex); }
        return null;
    }

    /// <summary>
    /// The power that is keeping this corpse in the fight, i.e. the one that answered no
    /// to ShouldCreatureBeRemovedFromCombatAfterDeath. Null when the creature is being
    /// held by something other than one of its own powers.
    /// </summary>
    private static PowerModel? FindPowerKeepingCorpseInCombat(Creature creature)
    {
        foreach (var power in creature.Powers)
        {
            try
            {
                if (!power.ShouldCreatureBeRemovedFromCombatAfterDeath(creature))
                    return power;
            }
            catch (Exception ex) { Warn("enemy.revive.power", ex); }
        }
        return null;
    }

    private static Dictionary<string, object?> BuildEventState(EventRoom eventRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var eventModel = eventRoom.CanonicalEvent;
        bool isAncient = eventModel is AncientEventModel;
        state["event_id"] = eventModel.Id.Entry;
        state["event_name"] = SafeGetText(() => eventModel.Title);
        state["is_ancient"] = isAncient;

        // Check dialogue state for ancients
        bool inDialogue = false;
        var uiRoom = NEventRoom.Instance;
        if (isAncient && uiRoom != null)
        {
            var ancientLayout = FindFirst<NAncientEventLayout>(uiRoom);
            if (ancientLayout != null)
            {
                var hitbox = ancientLayout.GetNodeOrNull<NClickableControl>("%DialogueHitbox");
                inDialogue = hitbox != null && hitbox.Visible && hitbox.IsEnabled;
            }
        }
        state["in_dialogue"] = inDialogue;

        // Event body text
        state["body"] = SafeGetText(() => eventModel.Description);

        // Options from UI
        var options = new List<Dictionary<string, object?>>();
        if (uiRoom != null)
        {
            var buttons = FindAll<NEventOptionButton>(uiRoom);
            int index = 0;
            foreach (var button in buttons)
            {
                var opt = button.Option;
                var optData = new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["title"] = SafeGetText(() => opt.Title),
                    ["description"] = SafeGetText(() => opt.Description),
                    ["is_locked"] = opt.IsLocked,
                    ["is_proceed"] = opt.IsProceed,
                    ["was_chosen"] = opt.WasChosen
                };
                if (opt.Relic != null)
                {
                    optData["relic_name"] = SafeGetText(() => opt.Relic.Title);
                    optData["relic_description"] = SafeGetText(() => opt.Relic.DynamicDescription);
                }
                optData["keywords"] = BuildHoverTips(opt.HoverTips);
                options.Add(optData);
                index++;
            }
        }
        state["options"] = options;

        return state;
    }

    private static Dictionary<string, object?> BuildFakeMerchantState(EventRoom eventRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();
        // LocalMutableEvent holds the per-player mutable copy with populated inventory;
        // CanonicalEvent is the shared template which may not have it.
        var fakeMerchant = (FakeMerchant)(eventRoom.LocalMutableEvent ?? eventRoom.CanonicalEvent);

        state["event_id"] = fakeMerchant.Id.Entry;
        state["event_name"] = SafeGetText(() => fakeMerchant.Title);
        state["started_fight"] = fakeMerchant.StartedFight;

        // Find the NFakeMerchant UI node
        var uiRoom = NEventRoom.Instance;
        NFakeMerchant? fakeMerchantNode = null;
        if (uiRoom != null)
            fakeMerchantNode = FindFirst<NFakeMerchant>(uiRoom);

        if (fakeMerchant.StartedFight)
        {
            // After the foul potion fight, merchant is gone - just show proceed
            state["shop"] = new Dictionary<string, object?>
            {
                ["items"] = new List<Dictionary<string, object?>>(),
                ["can_proceed"] = true
            };
            state["message"] = "The fake merchant has been defeated. Proceed to map.";
            return state;
        }

        // Auto-open the inventory if the merchant button is still available
        if (fakeMerchantNode != null)
        {
            var inventoryUI = FindFirst<NMerchantInventory>(fakeMerchantNode);
            if (inventoryUI != null && !inventoryUI.IsOpen)
            {
                // ForceClick the merchant button to go through the proper signal chain
                // (disables proceed button, wires InventoryClosed callback, etc.)
                var merchantButton = fakeMerchantNode.MerchantButton;
                if (merchantButton != null && merchantButton.Visible && merchantButton.IsEnabled)
                    merchantButton.ForceClick();
            }
        }

        // Build shop inventory from the FakeMerchant model
        var shopState = BuildFakeMerchantShopItems(fakeMerchant.Inventory);

        // Proceed button
        if (fakeMerchantNode != null)
        {
            var proceedButton = FindFirst<NProceedButton>(fakeMerchantNode);
            shopState["can_proceed"] = proceedButton?.IsEnabled ?? false;
        }
        else
        {
            shopState["can_proceed"] = false;
        }

        state["shop"] = shopState;
        return state;
    }

    private static Dictionary<string, object?> BuildFakeMerchantShopItems(MerchantInventory? inventory)
    {
        var state = new Dictionary<string, object?>();

        if (inventory == null)
        {
            state["items"] = new List<Dictionary<string, object?>>();
            state["error"] = "Fake merchant inventory is not ready yet; retry in a moment.";
            return state;
        }

        var items = new List<Dictionary<string, object?>>();
        int index = 0;

        // FakeMerchant only sells relics (no cards, potions, or card removal)
        foreach (var entry in inventory.RelicEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "relic",
                ["price"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold
            };
            if (entry.Model is { } relic)
            {
                item["relic_id"] = relic.Id.Entry;
                item["relic_name"] = SafeGetText(() => relic.Title);
                item["relic_description"] = SafeGetText(() => relic.DynamicDescription);
                item["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic);
            }
            items.Add(item);
            index++;
        }

        state["items"] = items;
        return state;
    }

    private static Dictionary<string, object?> BuildRestSiteState(RestSiteRoom restSiteRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var uiRoom = NRestSiteRoom.Instance;
        var options = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var opt in restSiteRoom.Options)
        {
            options.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = opt.OptionId,
                ["name"] = SafeGetText(() => opt.Title),
                ["description"] = SafeGetText(() => opt.Description),
                ["is_enabled"] = opt.IsEnabled,
                // A model option with no button is exactly the case where counting
                // buttons positionally would have pressed the wrong option.
                ["has_button"] = uiRoom != null && FindRestSiteButton(uiRoom, opt) != null
            });
            index++;
        }
        state["options"] = options;

        var proceedButton = uiRoom?.ProceedButton;
        state["can_proceed"] = proceedButton?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildShopState(MerchantRoom merchantRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var inventory = merchantRoom.GetLocalInventory();
        if (inventory == null)
        {
            state["items"] = new List<Dictionary<string, object?>>();
            state["can_proceed"] = NMerchantRoom.Instance?.ProceedButton?.IsEnabled ?? false;
            state["error"] =
                "Shop inventory is not ready yet (null). Often happens right after entering the merchant from the map; retry in a moment.";
            return state;
        }

        var items = new List<Dictionary<string, object?>>();
        int index = 0;

        // Cards
        foreach (var entry in inventory.CardEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "card",
                ["price"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold,
                ["on_sale"] = entry.IsOnSale
            };
            if (entry.CreationResult?.Card is { } card)
            {
                var cardInfo = BuildCardInfo(card);
                item["card_id"] = cardInfo["id"];
                item["card_name"] = cardInfo["name"];
                item["card_type"] = cardInfo["type"];
                item["card_cost"] = cardInfo["cost"];
                item["card_star_cost"] = cardInfo["star_cost"];
                item["card_rarity"] = cardInfo["rarity"];
                item["card_description"] = cardInfo["description"];
                item["keywords"] = cardInfo["keywords"];
            }
            items.Add(item);
            index++;
        }

        // Relics
        foreach (var entry in inventory.RelicEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "relic",
                ["price"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold
            };
            if (entry.Model is { } relic)
            {
                item["relic_id"] = relic.Id.Entry;
                item["relic_name"] = SafeGetText(() => relic.Title);
                item["relic_description"] = SafeGetText(() => relic.DynamicDescription);
                item["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic);
            }
            items.Add(item);
            index++;
        }

        // Potions
        foreach (var entry in inventory.PotionEntries)
        {
            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "potion",
                ["price"] = entry.Cost,
                ["is_stocked"] = entry.IsStocked,
                ["can_afford"] = entry.EnoughGold
            };
            if (entry.Model is { } potion)
            {
                item["potion_id"] = potion.Id.Entry;
                item["potion_name"] = SafeGetText(() => potion.Title);
                item["potion_description"] = SafeGetText(() => potion.DynamicDescription);
                item["keywords"] = BuildHoverTips(potion.ExtraHoverTips);
            }
            items.Add(item);
            index++;
        }

        // Card removal
        if (inventory.CardRemovalEntry is { } removal)
        {
            items.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["category"] = "card_removal",
                ["price"] = removal.Cost,
                ["is_stocked"] = removal.IsStocked,
                ["can_afford"] = removal.EnoughGold
            });
        }

        state["items"] = items;

        // Potions the merchant will buy (Foul Potion sells for gold instead of
        // exploding). There was no sign anywhere in state that this was even possible.
        var localPlayer = LocalContext.GetMe(runState);
        if (localPlayer != null)
        {
            var sellable = new List<Dictionary<string, object?>>();
            for (int slot = 0; slot < localPlayer.PotionSlots.Count; slot++)
            {
                var held = localPlayer.GetPotionAtSlotIndex(slot);
                if (held == null || !CanSellPotionToMerchant(localPlayer, held))
                    continue;
                sellable.Add(new Dictionary<string, object?>
                {
                    ["slot"] = slot,
                    ["potion_id"] = held.Id.Entry,
                    ["potion_name"] = SafeGetText(() => held.Title),
                    ["action"] = "sell_potion"
                });
            }
            if (sellable.Count > 0)
                state["sellable_potions"] = sellable;
        }

        var proceedButton = NMerchantRoom.Instance?.ProceedButton;
        state["can_proceed"] = proceedButton?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildMapState(RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var map = runState.Map;
        var visitedCoords = runState.VisitedMapCoords;
        var marks = CollectMapMarks(runState);
        if (marks.Count > 0)
        {
            state["marked_nodes"] = marks
                .OrderBy(kv => kv.Key.Item1).ThenBy(kv => kv.Key.Item2)
                .Select(kv => new Dictionary<string, object?>
                {
                    ["col"] = kv.Key.Item1,
                    ["row"] = kv.Key.Item2,
                    ["marked_by"] = kv.Value
                })
                .ToList();
        }

        // Current position
        if (visitedCoords.Count > 0)
        {
            var cur = visitedCoords[visitedCoords.Count - 1];
            state["current_position"] = new Dictionary<string, object?>
            {
                ["col"] = cur.col, ["row"] = cur.row,
                ["type"] = map.GetPoint(cur)?.PointType.ToString()
            };
        }

        // Visited path
        var visited = new List<Dictionary<string, object?>>();
        foreach (var coord in visitedCoords)
        {
            visited.Add(new Dictionary<string, object?>
            {
                ["col"] = coord.col, ["row"] = coord.row,
                ["type"] = map.GetPoint(coord)?.PointType.ToString()
            });
        }
        state["visited"] = visited;

        // Next options - read travelable state from UI nodes
        var nextOptions = new List<Dictionary<string, object?>>();
        var mapScreen = NMapScreen.Instance;
        if (mapScreen != null)
        {
            var travelable = FindAll<NMapPoint>(mapScreen)
                .Where(mp => mp.State == MapPointState.Travelable && mp.Point != null)
                .OrderBy(mp => mp.Point!.coord.col)
                .ToList();

            int index = 0;
            foreach (var nmp in travelable)
            {
                var pt = nmp.Point;
                var option = new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["col"] = pt.coord.col,
                    ["row"] = pt.coord.row,
                    ["type"] = pt.PointType.ToString()
                };
                AddMapNodeMarks(option, pt, marks);

                // 1-level lookahead
                var children = pt.Children
                    .OrderBy(c => c.coord.col)
                    .Select(c => new Dictionary<string, object?>
                    {
                        ["col"] = c.coord.col, ["row"] = c.coord.row,
                        ["type"] = c.PointType.ToString()
                    }).ToList();
                if (children.Count > 0)
                    option["leads_to"] = children;

                nextOptions.Add(option);
                index++;
            }
        }
        state["next_options"] = nextOptions;

        // Full map - all nodes organized for planning
        var nodes = new List<Dictionary<string, object?>>();

        // Starting point
        var start = map.StartingMapPoint;
        nodes.Add(BuildMapNode(start, marks));

        // Grid nodes
        foreach (var pt in map.GetAllMapPoints())
            nodes.Add(BuildMapNode(pt, marks));

        // Boss identity comes from the live act's EncounterModel — BossEncounter
        // throws if the act hasn't finished setup yet, so guard the access.
        EncounterModel? bossEncounter = null;
        try { bossEncounter = runState.Act.BossEncounter; }
        catch (Exception ex) { Warn("map.boss", ex); }
        var secondBossEncounter = runState.Act.SecondBossEncounter;

        var primaryBossId = bossEncounter?.Id?.Entry;
        var primaryBossName = SafeGetText(() => bossEncounter?.Title);
        var bossNode = BuildMapNode(map.BossMapPoint, marks);
        AddBossIdentity(bossNode, primaryBossId, primaryBossName);
        nodes.Add(bossNode);

        Dictionary<string, object?>? secondBoss = null;
        if (map.SecondBossMapPoint != null)
        {
            var secondBossId = secondBossEncounter?.Id?.Entry;
            var secondBossName = SafeGetText(() => secondBossEncounter?.Title);
            var secondBossNode = BuildMapNode(map.SecondBossMapPoint, marks);
            AddBossIdentity(secondBossNode, secondBossId, secondBossName);
            nodes.Add(secondBossNode);
            secondBoss = BuildBossInfo(map.SecondBossMapPoint, secondBossId, secondBossName);
        }

        state["nodes"] = nodes;

        var nextEncounters = BuildNextEncounters(runState);
        if (nextEncounters.Count > 0)
            state["next_encounters"] = nextEncounters;
        var primaryBoss = BuildBossInfo(map.BossMapPoint, primaryBossId, primaryBossName);
        state["boss"] = primaryBoss;
        state["bosses"] = secondBoss != null
            ? new List<Dictionary<string, object?>> { primaryBoss, secondBoss }
            : new List<Dictionary<string, object?>> { primaryBoss };

        return state;
    }

    /// <summary>
    /// The encounter each room type will serve next. Acts draw combats from a fixed
    /// ordered list, so the next monster / elite fight is known before entering the
    /// room — the game logs it at combat start, but state only ever reported enemies
    /// once the fight had already begun, which is too late to route around a bad matchup.
    ///
    /// Read-only: RoomSet's Next* getters peek at the list, they do not advance it.
    /// </summary>
    private static Dictionary<string, object?> BuildNextEncounters(RunState runState)
    {
        var result = new Dictionary<string, object?>();
        try
        {
            var rooms = GetInstanceFieldValue(runState.Act, "_rooms");
            if (rooms == null)
                return result;

            AddNextEncounter(result, "monster", GetPropertyValue(rooms, "NextNormalEncounter"));
            AddNextEncounter(result, "elite", GetPropertyValue(rooms, "NextEliteEncounter"));

            var nextEvent = GetPropertyValue(rooms, "NextEvent");
            if (nextEvent != null)
            {
                result["event"] = new Dictionary<string, object?>
                {
                    ["id"] = SafeGetText(() => ((AbstractModel)nextEvent).Id.Entry),
                    ["name"] = SafeGetText(() => GetPropertyValue(nextEvent, "Title"))
                };
            }
        }
        catch (Exception ex) { Warn("map.next_encounters", ex); }

        return result;
    }

    private static void AddNextEncounter(Dictionary<string, object?> target, string key, object? encounterObj)
    {
        if (encounterObj is not EncounterModel encounter)
            return;

        var info = new Dictionary<string, object?>
        {
            ["id"] = SafeGetText(() => encounter.Id.Entry),
            ["name"] = SafeGetText(() => encounter.Title),
            ["is_weak"] = SafeGetBool(() => encounter.IsWeak)
        };

        try
        {
            var monsters = encounter.AllPossibleMonsters?
                .Select(m => SafeGetText(() => m.Title) ?? SafeGetText(() => m.Id.Entry) ?? "?")
                .ToList();
            if (monsters != null && monsters.Count > 0)
                info["possible_monsters"] = monsters;
        }
        catch (Exception ex) { Warn($"map.next_encounters.{key}.possible_monsters", ex); }

        target[key] = info;
    }

    private static object? GetPropertyValue(object target, string propertyName)
    {
        try { return target.GetType().GetProperty(propertyName)?.GetValue(target); }
        catch (Exception ex) { Warn($"{target.GetType().Name}.{propertyName}", ex); return null; }
    }

    // The caller's name stands in for a field name here: this helper has no idea which
    // part of the state it is feeding, and "bool: NullReferenceException" alone names nothing.
    private static bool? SafeGetBool(Func<bool> getter,
        [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        try { return getter(); }
        catch (Exception ex) { Warn($"bool in {caller}", ex); return null; }
    }

    private static Dictionary<string, object?> BuildBossInfo(MapPoint pt, string? bossId, string? bossName)
    {
        var boss = new Dictionary<string, object?>
        {
            ["col"] = pt.coord.col,
            ["row"] = pt.coord.row
        };
        AddBossIdentity(boss, bossId, bossName);
        return boss;
    }

    private static void AddBossIdentity(Dictionary<string, object?> target, string? bossId, string? bossName)
    {
        if (string.IsNullOrWhiteSpace(bossId))
            return;

        target["id"] = bossId;
        if (!string.IsNullOrWhiteSpace(bossName))
            target["name"] = bossName;
    }

    private static Dictionary<string, object?> BuildMapNode(MapPoint pt, IReadOnlyDictionary<(int, int), List<string>>? marks = null)
    {
        var node = new Dictionary<string, object?>
        {
            ["col"] = pt.coord.col,
            ["row"] = pt.coord.row,
            ["type"] = pt.PointType.ToString(),
            ["children"] = pt.Children
                .OrderBy(c => c.coord.col)
                .Select(c => new List<int> { c.coord.col, c.coord.row })
                .ToList()
        };

        AddMapNodeMarks(node, pt, marks);
        return node;
    }

    /// <summary>
    /// Attaches whatever has marked this node. Relics such as Fur Coat mark rooms when
    /// picked up (marked combats spawn enemies at 1 HP), and events attach quests to
    /// nodes — neither was visible anywhere in state or in the save, so the whole point
    /// of the relic (route towards the free fights) could not be acted on.
    /// </summary>
    private static void AddMapNodeMarks(
        Dictionary<string, object?> node,
        MapPoint pt,
        IReadOnlyDictionary<(int, int), List<string>>? marks)
    {
        if (marks != null && marks.TryGetValue((pt.coord.col, pt.coord.row), out var markedBy))
            node["marked_by"] = new List<string>(markedBy);

        try
        {
            var quests = pt.Quests;
            if (quests != null && quests.Count > 0)
            {
                node["quests"] = quests
                    .Select(q => SafeGetText(() => q.Id.Entry) ?? "unknown")
                    .ToList();
            }
        }
        catch (Exception ex) { Warn("map.nodes.quests", ex); }

        if (pt.CanBeModified)
            node["can_be_modified"] = true;
    }

    /// <summary>
    /// Collects map coordinates marked by the local player's relics.
    /// Relics advertise their marks through a public GetMarkedCoords(); the lookup is
    /// duck-typed so any relic that grows one is picked up without further changes here.
    /// </summary>
    private static Dictionary<(int, int), List<string>> CollectMapMarks(RunState runState)
    {
        var marks = new Dictionary<(int, int), List<string>>();
        try
        {
            var player = LocalContext.GetMe(runState);
            if (player == null)
                return marks;

            int actIndex = runState.CurrentActIndex;
            foreach (var relic in player.Relics)
            {
                var method = relic.GetType().GetMethod("GetMarkedCoords", System.Type.EmptyTypes);
                if (method == null)
                    continue;

                // A relic that marked rooms in an earlier act must not paint this act's map.
                var actProperty = relic.GetType().GetProperty("FurCoatActIndex")
                    ?? relic.GetType().GetProperty("MarkedActIndex");
                if (actProperty?.GetValue(relic) is int markedAct && markedAct != actIndex)
                    continue;

                if (method.Invoke(relic, null) is not System.Collections.IEnumerable coords)
                    continue;

                string relicId = SafeGetText(() => relic.Id.Entry) ?? relic.GetType().Name;
                foreach (var coordObj in coords)
                {
                    if (coordObj is not MapCoord coord)
                        continue;
                    var key = (coord.col, coord.row);
                    if (!marks.TryGetValue(key, out var list))
                        marks[key] = list = new List<string>();
                    if (!list.Contains(relicId))
                        list.Add(relicId);
                }
            }
        }
        catch (Exception ex) { Warn("map.nodes.marked_by", ex); }

        return marks;
    }

    private static Dictionary<string, object?> BuildRewardsState(NRewardsScreen rewardsScreen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Reward items
        var rewardButtons = FindAll<NRewardButton>(rewardsScreen);
        var items = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var button in rewardButtons)
        {
            if (button.Reward == null || !button.IsEnabled) continue;
            var reward = button.Reward;

            var item = new Dictionary<string, object?>
            {
                ["index"] = index,
                ["type"] = GetRewardTypeName(reward),
                ["description"] = SafeGetText(() => reward.Description)
            };

            // Type-specific details
            if (reward is GoldReward goldReward)
                item["gold_amount"] = goldReward.Amount;
            else if (reward is PotionReward potionReward && potionReward.Potion != null)
            {
                item["potion_id"] = potionReward.Potion.Id.Entry;
                item["potion_name"] = SafeGetText(() => potionReward.Potion.Title);
                item["potion_description"] = SafeGetText(() => potionReward.Potion.DynamicDescription);
            }

            items.Add(item);
            index++;
        }
        state["items"] = items;

        // Proceed button
        var proceedButton = FindFirst<NProceedButton>(rewardsScreen);
        state["can_proceed"] = proceedButton?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildCardRewardState(NCardRewardSelectionScreen cardScreen)
    {
        var state = new Dictionary<string, object?>();

        var cardHolders = FindAllSortedByPosition<NCardHolder>(cardScreen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            // The upgraded form is part of what a reward card is worth, so it belongs in
            // the choice rather than being discovered later at a smith.
            AttachUpgradePreview(cardInfo, card);
            cards.Add(cardInfo);
            index++;
        }
        state["cards"] = cards;

        // Alternatives are the row of buttons beside the cards. "Skip" is only one of
        // them: relics add their own (Pael's Wing adds SACRIFICE, a reroll relic adds
        // REROLL), and skipping is NOT the same as taking one of those — Pael's Wing
        // counts sacrifices, and skip_card_reward does not count. State used to report
        // a single can_skip boolean, so the other options were invisible from the API
        // and the relic's whole function was unreachable.
        var alternatives = BuildCardRewardAlternatives(cardScreen);
        state["alternatives"] = alternatives;
        state["can_skip"] = alternatives.Any(a =>
            string.Equals(a.GetValueOrDefault("option_id")?.ToString(), "Skip", StringComparison.OrdinalIgnoreCase));

        return state;
    }

    /// <summary>
    /// The card reward's alternative options, in the order their buttons appear.
    /// Ids come from the screen's own CardRewardAlternative list where readable, so an
    /// option added by a relic this code has never heard of still shows up.
    /// </summary>
    private static List<Dictionary<string, object?>> BuildCardRewardAlternatives(NCardRewardSelectionScreen cardScreen)
    {
        var result = new List<Dictionary<string, object?>>();
        var buttons = FindAll<NCardRewardAlternativeButton>(cardScreen);

        var optionIds = new List<string>();
        try
        {
            if (GetInstanceFieldValue(cardScreen, "_extraOptions") is System.Collections.IEnumerable options)
            {
                foreach (var option in options)
                {
                    var id = GetPropertyValue(option, "OptionId")?.ToString();
                    optionIds.Add(id ?? "");
                }
            }
        }
        catch (Exception ex) { Warn("card_reward.alternatives.option_id", ex); }

        for (int i = 0; i < buttons.Count; i++)
        {
            var button = buttons[i];
            string? label = SafeGetText(() => GetInstanceFieldValue(button, "_optionName"));
            string optionId = i < optionIds.Count && !string.IsNullOrWhiteSpace(optionIds[i])
                ? optionIds[i]
                : label ?? $"option_{i}";

            result.Add(new Dictionary<string, object?>
            {
                ["index"] = i,
                ["option_id"] = optionId,
                ["title"] = label,
                ["enabled"] = button.IsEnabled
            });
        }

        return result;
    }

    private static Dictionary<string, object?> BuildCardSelectState(NCardGridSelectionScreen screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Screen type
        state["screen_type"] = screen switch
        {
            NDeckTransformSelectScreen => "transform",
            NDeckUpgradeSelectScreen => "upgrade",
            NDeckEnchantSelectScreen => "enchant",
            NDeckCardSelectScreen => "select",
            NSimpleCardSelectScreen => "simple_select",
            _ => screen.GetType().Name
        };

        // Player summary
        // Prompt text from UI label
        var bottomLabel = screen.GetNodeOrNull("%BottomLabel");
        if (bottomLabel != null)
        {
            var textVariant = bottomLabel.Get("text");
            string? prompt = textVariant.VariantType != Godot.Variant.Type.Nil ? StripRichTextTags(textVariant.AsString()) : null;
            state["prompt"] = prompt;
        }

        // Which cards the screen has selected right now. Read from the screen's own
        // `_selectedCards` set - the one OnCardClicked toggles - so `selected` cannot drift
        // from what the game will act on. Without it a caller driving a multi-card screen
        // (the enchant screens) is blind to its own selection (A-7).
        var selection = GetCardSelectSelection(screen);
        if (selection == null)
            Warn("card_select.selected", "screen has no readable _selectedCards field");

        // Cards in the grid (sorted by visual position - MoveToFront can reorder children)
        var cardHolders = FindAllSortedByPosition<NGridCardHolder>(screen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        int selectedInGrid = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            if (selection != null)
            {
                bool isSelected = selection.Contains(card);
                cardInfo["selected"] = isSelected;
                if (isSelected) selectedInGrid++;
            }
            // Only on the upgrade screen: the transform/remove screens do not upgrade
            // anything, and every card carrying a preview would bloat the payload.
            if (screen is NDeckUpgradeSelectScreen) AttachUpgradePreview(cardInfo, card);
            cards.Add(cardInfo);
            index++;
        }
        state["cards"] = cards;

        // How many are selected and how many the screen wants. `selected_count` is the
        // screen's own count (it can exceed what the visible grid window shows);
        // `required_count` is the "select N" of the prompt, null when the screen accepts a
        // range ("up to N"), where min_select/max_select give the two ends.
        if (selection != null)
            state["selected_count"] = selection.Count;
        var prefs = GetCardSelectPrefs(screen);
        if (prefs.HasValue)
        {
            int min = prefs.Value.MinSelect;
            int max = prefs.Value.MaxSelect;
            state["min_select"] = min;
            state["max_select"] = max;
            state["required_count"] = min == max ? min : (int?)null;
        }
        else
        {
            state["required_count"] = null;
        }
        if (selection != null && selection.Count != selectedInGrid)
        {
            // Only possible when the grid is scrolled: its holders are a sliding window
            // over the deck, so a selected card can be outside `cards[]`.
            state["selected_outside_grid"] = selection.Count - selectedInGrid;
        }

        // Preview container showing? (selection complete, awaiting confirm)
        // The subclasses each name theirs differently; FindCardSelectPreviewContainers
        // knows all the spellings (upgrade, enchant and the generic one).
        var previewContainers = FindCardSelectPreviewContainers(screen);
        bool previewShowing = previewContainers.Count > 0;
        state["preview_showing"] = previewShowing;
        if (previewShowing)
        {
            var previewCards = new List<Dictionary<string, object?>>();
            foreach (var container in previewContainers)
                AddPreviewCardsFromContainer(container, previewCards);
            state["preview_cards"] = previewCards;
        }

        // Button states - when a preview is open, cancel goes through the
        // preview container's Cancel / PreviewCancel button (same path as
        // the action handler), not the top-level %Close button.
        bool canCancel = false;
        foreach (var container in previewContainers)
        {
            var cancelBtn = GetCardSelectPreviewCancel(container);
            if (cancelBtn?.IsEnabled == true) { canCancel = true; break; }
        }
        if (!canCancel)
        {
            var closeButton = screen.GetNodeOrNull<NBackButton>("%Close");
            canCancel = closeButton?.IsEnabled ?? false;
        }
        state["can_cancel"] = canCancel;

        // Confirm button - the preview's own confirm while one is open, otherwise the
        // screen's. Note that below a grid, "confirm" usually opens the preview rather
        // than applying the selection; confirm_selection reports which of the two it did.
        bool canConfirm = false;
        foreach (var container in previewContainers)
        {
            var confirm = GetCardSelectPreviewConfirm(container);
            if (confirm?.IsEnabled == true) { canConfirm = true; break; }
        }
        if (!canConfirm && !previewShowing)
        {
            var mainConfirm = GetCardSelectMainConfirm(screen);
            if (mainConfirm?.IsEnabled == true) canConfirm = true;
            // Fallback: search the screen tree for any enabled confirm button, for a
            // subclass that names its confirm button something else.
            if (!canConfirm)
                canConfirm = FindAll<NConfirmButton>(screen).Any(b => b.IsEnabled && b.IsVisibleInTree());
        }
        state["can_confirm"] = canConfirm;

        return state;
    }

    private static Dictionary<string, object?> BuildChooseCardState(NChooseACardSelectionScreen screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();
        state["screen_type"] = "choose";

        state["prompt"] = "Choose a card.";

        var cardHolders = FindAllSortedByPosition<NGridCardHolder>(screen);
        var cards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in cardHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            cards.Add(cardInfo);
            index++;
        }
        state["cards"] = cards;

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        state["can_skip"] = skipButton?.IsEnabled == true && skipButton.Visible;
        state["preview_showing"] = false;
        state["can_confirm"] = false;
        state["can_cancel"] = state["can_skip"];

        return state;
    }

    private static Dictionary<string, object?> BuildBundleSelectState(NChooseABundleSelectionScreen screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();
        state["screen_type"] = "bundle";

        state["prompt"] = "Choose a bundle.";

        var bundles = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var bundle in FindAll<NCardBundle>(screen))
        {
            var cards = new List<Dictionary<string, object?>>();
            int cardIndex = 0;
            foreach (var card in bundle.Bundle)
            {
                var cardInfo = BuildCardInfo(card);
                cardInfo["index"] = cardIndex;
                cards.Add(cardInfo);
                cardIndex++;
            }

            bundles.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["card_count"] = cards.Count,
                ["cards"] = cards
            });
            index++;
        }
        state["bundles"] = bundles;

        var previewContainer = screen.GetNodeOrNull<Godot.Control>("%BundlePreviewContainer");
        bool previewShowing = previewContainer?.Visible == true;
        state["preview_showing"] = previewShowing;

        var previewCards = new List<Dictionary<string, object?>>();
        var previewCardsContainer = screen.GetNodeOrNull<Godot.Control>("%Cards");
        if (previewCardsContainer != null)
        {
            int previewIndex = 0;
            foreach (var holder in FindAll<NPreviewCardHolder>(previewCardsContainer))
            {
                var card = holder.CardModel;
                if (card == null) continue;

                var cardInfo = BuildCardInfo(card);
                cardInfo["index"] = previewIndex;
                previewCards.Add(cardInfo);
                previewIndex++;
            }
        }
        state["preview_cards"] = previewCards;

        var cancelButton = screen.GetNodeOrNull<NBackButton>("%Cancel");
        var confirmButton = screen.GetNodeOrNull<NConfirmButton>("%Confirm");
        state["can_cancel"] = cancelButton?.IsEnabled == true;
        state["can_confirm"] = confirmButton?.IsEnabled == true;

        return state;
    }

    private static Dictionary<string, object?> BuildHandSelectState(NPlayerHand hand, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        // Mode
        state["mode"] = hand.CurrentMode switch
        {
            NPlayerHand.Mode.SimpleSelect => "simple_select",
            NPlayerHand.Mode.UpgradeSelect => "upgrade_select",
            _ => hand.CurrentMode.ToString()
        };

        // Prompt text from %SelectionHeader
        var headerLabel = hand.GetNodeOrNull<Godot.Control>("%SelectionHeader");
        if (headerLabel != null)
        {
            var textVariant = headerLabel.Get("text");
            string? prompt = textVariant.VariantType != Godot.Variant.Type.Nil
                ? StripRichTextTags(textVariant.AsString())
                : null;
            state["prompt"] = prompt;
        }

        // Selectable cards (visible holders in the hand)
        var selectableCards = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in hand.ActiveHolders)
        {
            var card = holder.CardModel;
            if (card == null) continue;

            var cardInfo = BuildCardInfo(card);
            cardInfo["index"] = index;
            cardInfo["description"] = SafeGetCardDescription(card); // hand cards use default pile
            // Only when the prompt is "upgrade a card in hand"; the plain select modes
            // (discard, exhaust, ...) have nothing to decide from the upgraded text.
            if (hand.CurrentMode == NPlayerHand.Mode.UpgradeSelect) AttachUpgradePreview(cardInfo, card);
            selectableCards.Add(cardInfo);
            index++;
        }
        state["cards"] = selectableCards;

        // Already-selected cards (in the SelectedHandCardContainer)
        var selectedContainer = hand.GetNodeOrNull<Godot.Control>("%SelectedHandCardContainer");
        if (selectedContainer != null)
        {
            var selectedCards = new List<Dictionary<string, object?>>();
            var selectedHolders = FindAll<NSelectedHandCardHolder>(selectedContainer);
            int selIdx = 0;
            foreach (var holder in selectedHolders)
            {
                var card = holder.CardModel;
                if (card == null) continue;
                selectedCards.Add(new Dictionary<string, object?>
                {
                    ["index"] = selIdx,
                    ["name"] = SafeGetText(() => card.Title)
                });
                selIdx++;
            }
            if (selectedCards.Count > 0)
                state["selected_cards"] = selectedCards;
        }

        // Confirm button state
        var confirmBtn = hand.GetNodeOrNull<NConfirmButton>("%SelectModeConfirmButton");
        state["can_confirm"] = confirmBtn?.IsEnabled ?? false;

        return state;
    }

    private static Dictionary<string, object?> BuildRelicSelectState(NChooseARelicSelection screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        state["prompt"] = "Choose a relic.";

        var relicHolders = FindAll<NRelicBasicHolder>(screen);
        var relics = new List<Dictionary<string, object?>>();
        int index = 0;
        foreach (var holder in relicHolders)
        {
            var relic = holder.Relic?.Model;
            if (relic == null) continue;

            relics.Add(new Dictionary<string, object?>
            {
                ["index"] = index,
                ["id"] = relic.Id.Entry,
                ["name"] = SafeGetText(() => relic.Title),
                ["description"] = SafeGetText(() => relic.DynamicDescription),
                ["rarity"] = relic.Rarity.ToString(),
                ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
            });
            index++;
        }
        state["relics"] = relics;

        var skipButton = screen.GetNodeOrNull<NClickableControl>("SkipButton");
        state["can_skip"] = skipButton?.IsEnabled == true && skipButton.Visible;

        return state;
    }

    private static Dictionary<string, object?> BuildCrystalSphereState(NCrystalSphereScreen screen, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var instructionsTitle = screen.GetNodeOrNull<Godot.Control>("%InstructionsTitle");
        if (instructionsTitle != null)
        {
            var textVariant = instructionsTitle.Get("text");
            if (textVariant.VariantType != Godot.Variant.Type.Nil)
                state["instructions_title"] = StripRichTextTags(textVariant.AsString());
        }

        var instructionsDescription = screen.GetNodeOrNull<Godot.Control>("%InstructionsDescription");
        if (instructionsDescription != null)
        {
            var textVariant = instructionsDescription.Get("text");
            if (textVariant.VariantType != Godot.Variant.Type.Nil)
                state["instructions_description"] = StripRichTextTags(textVariant.AsString());
        }

        var cells = FindAll<NCrystalSphereCell>(screen);
        state["grid_width"] = cells.Count > 0 ? cells.Max(c => c.Entity.X) + 1 : 0;
        state["grid_height"] = cells.Count > 0 ? cells.Max(c => c.Entity.Y) + 1 : 0;

        var cellStates = new List<Dictionary<string, object?>>();
        var clickableCells = new List<Dictionary<string, object?>>();
        foreach (var cell in cells.OrderBy(c => c.Entity.Y).ThenBy(c => c.Entity.X))
        {
            var cellState = new Dictionary<string, object?>
            {
                ["x"] = cell.Entity.X,
                ["y"] = cell.Entity.Y,
                ["is_hidden"] = cell.Entity.IsHidden,
                ["is_clickable"] = cell.Entity.IsHidden && cell.Visible,
                ["is_highlighted"] = cell.Entity.IsHighlighted,
                ["is_hovered"] = cell.Entity.IsHovered
            };

            if (!cell.Entity.IsHidden && cell.Entity.Item != null)
            {
                cellState["item_type"] = cell.Entity.Item.GetType().Name;
                cellState["is_good"] = cell.Entity.Item.IsGood;
            }

            cellStates.Add(cellState);
            if (cell.Entity.IsHidden && cell.Visible)
            {
                clickableCells.Add(new Dictionary<string, object?>
                {
                    ["x"] = cell.Entity.X,
                    ["y"] = cell.Entity.Y
                });
            }
        }
        state["cells"] = cellStates;
        state["clickable_cells"] = clickableCells;

        var revealedItems = new List<Dictionary<string, object?>>();
        foreach (var item in cells
                     .Where(c => !c.Entity.IsHidden && c.Entity.Item != null)
                     .Select(c => c.Entity.Item!)
                     .Distinct())
        {
            revealedItems.Add(new Dictionary<string, object?>
            {
                ["item_type"] = item.GetType().Name,
                ["x"] = item.Position.X,
                ["y"] = item.Position.Y,
                ["width"] = item.Size.X,
                ["height"] = item.Size.Y,
                ["is_good"] = item.IsGood
            });
        }
        state["revealed_items"] = revealedItems;

        var bigButton = screen.GetNodeOrNull<Godot.Control>("%BigDivinationButton");
        var smallButton = screen.GetNodeOrNull<Godot.Control>("%SmallDivinationButton");
        bool bigVisible = bigButton?.Visible == true;
        bool smallVisible = smallButton?.Visible == true;
        bool bigActive = bigButton?.GetNodeOrNull<Godot.Control>("%Outline")?.Visible == true;
        bool smallActive = smallButton?.GetNodeOrNull<Godot.Control>("%Outline")?.Visible == true;

        state["tool"] = bigActive ? "big" : smallActive ? "small" : "none";
        state["can_use_big_tool"] = bigVisible;
        state["can_use_small_tool"] = smallVisible;

        var divinationsLeft = screen.GetNodeOrNull<Godot.Control>("%DivinationsLeft");
        if (divinationsLeft != null)
        {
            var textVariant = divinationsLeft.Get("text");
            if (textVariant.VariantType != Godot.Variant.Type.Nil)
                state["divinations_left_text"] = StripRichTextTags(textVariant.AsString());
        }

        state["can_proceed"] = FindCrystalSphereProceedButton(screen) != null;

        return state;
    }

    private static Dictionary<string, object?> BuildTreasureState(TreasureRoom treasureRoom, RunState runState)
    {
        var state = new Dictionary<string, object?>();

        var treasureUI = FindFirst<NTreasureRoom>(
            ((Godot.SceneTree)Godot.Engine.GetMainLoop()).Root);

        if (treasureUI == null)
        {
            state["message"] = "Treasure room loading...";
            return state;
        }

        // Auto-open chest if not yet opened
        var chestButton = treasureUI.GetNodeOrNull<NClickableControl>("Chest");
        if (chestButton is { IsEnabled: true })
        {
            chestButton.ForceClick();
            state["message"] = "Opening chest...";
            return state;
        }

        // Show relics available for picking
        var relicCollection = treasureUI.GetNodeOrNull<NTreasureRoomRelicCollection>("%RelicCollection");
        if (relicCollection?.Visible == true)
        {
            var holders = FindAll<NTreasureRoomRelicHolder>(relicCollection)
                .Where(h => h.IsEnabled && h.Visible)
                .ToList();

            var relics = new List<Dictionary<string, object?>>();
            int index = 0;
            foreach (var holder in holders)
            {
                var relic = holder.Relic?.Model;
                if (relic == null) continue;
                relics.Add(new Dictionary<string, object?>
                {
                    ["index"] = index,
                    ["id"] = relic.Id.Entry,
                    ["name"] = SafeGetText(() => relic.Title),
                    ["description"] = SafeGetText(() => relic.DynamicDescription),
                    ["rarity"] = relic.Rarity.ToString(),
                    ["keywords"] = BuildHoverTips(relic.HoverTipsExcludingRelic)
                });
                index++;
            }
            state["relics"] = relics;
        }

        state["can_proceed"] = treasureUI.ProceedButton?.IsEnabled ?? false;

        return state;
    }

    private static string GetRewardTypeName(Reward reward) => reward switch
    {
        GoldReward => "gold",
        PotionReward => "potion",
        RelicReward => "relic",
        CardReward => "card",
        SpecialCardReward => "special_card",
        CardRemovalReward => "card_removal",
        _ => reward.GetType().Name.ToLower()
    };

    private static List<Dictionary<string, object?>> BuildPowersState(Creature creature)
    {
        var powers = new List<Dictionary<string, object?>>();
        foreach (var power in creature.Powers)
        {
            // IsVisible is a UI concern (it hides zero-stack and internal bookkeeping
            // powers from the HUD). A power with a live stack is always reported, even
            // when the HUD would hide it, because callers compute damage from this list.
            bool isVisible;
            try { isVisible = power.IsVisible; }
            catch (Exception ex) { Warn("status.is_visible", ex); isVisible = true; }
            if (!isVisible && !HasLiveStack(power)) continue;

            // Per-power try/catch: HoverTips getter calls into game engine code
            // (LocString resolution, DynamicVars, virtual ExtraHoverTips) that can
            // throw during state transitions. Skip the power rather than fail the
            // entire state query.
            try
            {
                var allTips = power.HoverTips.ToList();
                string? resolvedDesc = null;
                var extraTips = new List<IHoverTip>();
                foreach (var tip in allTips)
                {
                    if (tip.Id == power.Id.ToString())
                    {
                        if (tip is HoverTip ht && ht.Description != null)
                            resolvedDesc = StripRichTextTags(ht.Description);
                    }
                    else
                    {
                        extraTips.Add(tip);
                    }
                }
                resolvedDesc ??= SafeGetText(() => power.SmartDescription);

                // "amount" is the stack count that is actually in effect.
                // DisplayAmount is a UI-facing override: TenderPower, for example,
                // returns "cards played so far this turn", which reads as 0 at the
                // start of the very turn the debuff is live. Reporting that as the
                // stack count made "(0)" look like "expired". Keep the UI number
                // under display_amount for anyone who needs the on-screen value.
                int amount = power.Amount;
                int displayAmount = power.DisplayAmount;
                var entry = new Dictionary<string, object?>
                {
                    ["id"] = power.Id.Entry,
                    ["name"] = SafeGetText(() => power.Title),
                    ["amount"] = amount,
                    ["type"] = power.Type.ToString(),
                    ["description"] = resolvedDesc,
                    ["keywords"] = BuildHoverTips(extraTips)
                };
                if (displayAmount != amount)
                    entry["display_amount"] = displayAmount;
                if (!isVisible)
                    entry["hidden_in_ui"] = true;
                powers.Add(entry);
            }
            catch (Exception ex)
            {
                // HoverTips / SmartDescription reach deep into engine code and can throw
                // mid-transition. Dropping the power silently used to make debuffs
                // disappear from state while they were plainly still in effect, leaving
                // "why did my damage drop?" to be reverse-engineered from card text.
                // Emit what can be read without the engine instead.
                Warn("status.description", ex);
                powers.Add(BuildMinimalPowerState(power));
            }
        }
        return powers;
    }

    /// <summary>Fallback entry for a power whose description could not be resolved.</summary>
    private static Dictionary<string, object?> BuildMinimalPowerState(PowerModel power)
    {
        var entry = new Dictionary<string, object?>
        {
            ["id"] = SafeGetText(() => power.Id.Entry) ?? "unknown",
            ["name"] = SafeGetText(() => power.Title),
            ["amount"] = SafeGetInt(() => power.Amount),
            ["type"] = SafeGetText(() => power.Type.ToString()),
            ["description"] = null,
            ["description_unavailable"] = true,
            ["keywords"] = new List<string>()
        };
        return entry;
    }

    private static bool HasLiveStack(PowerModel power)
    {
        try { return power.Amount != 0; }
        catch (Exception ex) { Warn("status.amount", ex); return false; }
    }

    // Left silent on purpose: its callers use it to probe alternatives (PreviewValue,
    // then IntValue), so a throw here is an expected step, not a missing field. The
    // caller warns when every alternative fails.
    private static int? SafeGetInt(Func<int> getter)
    {
        try { return getter(); }
        catch { return null; }
    }

    private static List<Dictionary<string, object?>> BuildPetsState(Player player)
    {
        var pets = new List<Dictionary<string, object?>>();
        var combatState = player.PlayerCombatState;
        if (combatState == null) return pets;

        // Check Osty specifically (Byrdpip/PaelsLegion are cosmetic with no real combat state)
        var osty = combatState.GetPet<Osty>();
        if (osty != null)
        {
            pets.Add(new Dictionary<string, object?>
            {
                ["id"] = osty.Monster?.Id.Entry ?? "OSTY",
                ["name"] = SafeGetText(() => osty.Monster?.Title) ?? "Otsy",
                ["alive"] = osty.IsAlive,
                ["hp"] = osty.CurrentHp,
                ["max_hp"] = osty.MaxHp,
                ["block"] = osty.Block,
                ["status"] = BuildPowersState(osty)
            });
        }

        return pets;
    }
}
