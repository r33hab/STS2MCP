using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Nodes.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;

namespace STS2_MCP;

/// <summary>
/// The `selection` object of the card selection screens: hand_select (kind HAND), a card_select grid (GRID) and
/// choose-a-card (CHOOSE). It tells a client what the screen asks for without reading its localized text: the
/// prompt's localization key, the bounds, whether it can be skipped, what is already picked, the pile a grid picks
/// from, and the card, potion, relic or power whose effect opened it.
///
/// Nothing in it is hidden: the prompt, the bounds and the skip are on the screen, the picks are highlighted there,
/// and the source was just played, used or triggered in view. The screens keep all of it but the source (their
/// selector prefs, their selection, a combat pile screen's pile). The source is handed to NPlayerHand.SelectCards,
/// which does not keep it, and never reaches the grid and choose-a-card screens at all; so it is captured where the
/// game hands it over (the patches below) and matched back to the screen it opened.
/// </summary>
public static partial class McpMod
{
    /// <summary>The banner NChooseACardSelectionScreen._Ready shows, a constant in the game's code.</summary>
    private const string ChooseACardPromptKey = "gameplay_ui.CHOOSE_CARD_HEADER";

    // ------------------------------------------------------------------ the object

    /// <summary>hand_select's selection: NPlayerHand keeps the prefs it was given and its picks, in toggle order.</summary>
    private static Dictionary<string, object?> BuildHandSelection(NPlayerHand hand)
    {
        var prefs = GetInstanceFieldValue(hand, "_prefs") as CardSelectorPrefs?;
        if (prefs == null)
            Warn("hand_select.selection", "hand has no readable _prefs field");
        var selected = GetInstanceFieldValue(hand, "_selectedCards") as IEnumerable<CardModel>;
        if (selected == null)
            Warn("hand_select.selection", "hand has no readable _selectedCards field");

        // NPlayerHand.RefreshSelectModeConfirmButton enables confirm from MinSelect picks, so MinSelect 0 is a skip.
        return BuildSelection("HAND", pile: null, PromptKey(prefs?.Prompt),
            FindSelectionSource(SelectionShape.Hand, prefs?.Prompt, pile: null, cards: null),
            prefs?.MinSelect, prefs?.MaxSelect, prefs is { } p ? p.MinSelect == 0 : null, selected);
    }

    /// <summary>
    /// A card_select grid's selection. The combat pile screen (CardSelectCmd.FromCombatPile) knows its pile; the other
    /// grids show a list of cards (generated cards, the deck), so their pile is null.
    /// </summary>
    private static Dictionary<string, object?> BuildGridSelection(NCardGridSelectionScreen screen)
    {
        var prefs = GetCardSelectPrefs(screen);
        if (prefs == null)
            Warn("card_select.selection", "screen has no readable _prefs field");
        CardPile? pile = null;
        if (screen is NCombatPileCardSelectScreen)
        {
            pile = GetInstanceFieldValue(screen, "_pile") as CardPile;
            if (pile == null)
                Warn("card_select.selection", "screen has no readable _pile field");
        }

        // The grids enable confirm from MinSelect picks; the deck screens also close empty-handed when Cancelable.
        bool closable = screen is NDeckCardSelectScreen or NDeckTransformSelectScreen or NDeckEnchantSelectScreen or NDeckUpgradeSelectScreen;
        return BuildSelection("GRID", pile == null ? null : SelectionPileName(pile.Type), PromptKey(prefs?.Prompt),
            FindSelectionSource(SelectionShape.Grid, prefs?.Prompt, pile, cards: null),
            prefs?.MinSelect, prefs?.MaxSelect, prefs is { } p ? p.MinSelect == 0 || (closable && p.Cancelable) : null,
            // The screen's own selection set, in its own order: the order it hands to the game (see raw-full.md).
            GetCardSelectSelection(screen));
    }

    /// <summary>
    /// Choose-a-card's selection: a pick is one card and completes the screen at once, so nothing is ever selected;
    /// a skippable screen can also end with none.
    /// </summary>
    private static Dictionary<string, object?> BuildChooseSelection(NChooseACardSelectionScreen screen)
    {
        var cards = GetInstanceFieldValue(screen, "_cards");
        var canSkip = GetInstanceFieldValue(screen, "_canSkip") as bool?;
        if (canSkip == null)
            Warn("card_select.selection", "screen has no readable _canSkip field");
        return BuildSelection("CHOOSE", pile: null, ChooseACardPromptKey,
            FindSelectionSource(SelectionShape.Choose, prompt: null, pile: null, cards),
            canSkip == null ? null : canSkip.Value ? 0 : 1, 1, canSkip, Array.Empty<CardModel>());
    }

    private static Dictionary<string, object?> BuildSelection(string kind, string? pile, string? promptKey, AbstractModel? source,
        int? min, int? max, bool? canSkip, IEnumerable<CardModel>? selected)
    {
        return new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["pile"] = pile,
            ["prompt_key"] = promptKey,
            ["source"] = BuildSelectionSource(source),
            ["min"] = min,
            ["max"] = max,
            ["can_skip"] = canSkip,
            ["selected"] = selected?.Select(card => new Dictionary<string, object?>
            {
                ["uid"] = SafeGetStableCardUid(card),
                ["id"] = SafeGetCardId(card)
            }).ToList()
        };
    }

    /// <summary>
    /// `{type, id, uid}` of the model whose effect opened the selection: type card, potion, relic or power, and
    /// unknown for anything else (an orb, an event, a monster) or when there is none; uid only for a card.
    /// </summary>
    private static Dictionary<string, object?> BuildSelectionSource(AbstractModel? model)
    {
        string? id = null;
        if (model != null)
        {
            try { id = model.Id.Entry; }
            catch (Exception ex) { Warn("selection.source", ex); }
        }
        return new Dictionary<string, object?>
        {
            ["type"] = model switch
            {
                CardModel => "card",
                PotionModel => "potion",
                RelicModel => "relic",
                PowerModel => "power",
                _ => "unknown"
            },
            ["id"] = id,
            ["uid"] = model is CardModel card ? SafeGetStableCardUid(card) : null
        };
    }

    /// <summary>The prompt's localization table and key, joined by a dot ("card_selection.TO_EXHAUST"); null if none.</summary>
    private static string? PromptKey(LocString? prompt)
        => prompt == null || prompt.IsEmpty ? null : $"{prompt.LocTable}.{prompt.LocEntryKey}";

    private static string? SelectionPileName(PileType type) => type switch
    {
        PileType.Draw => "DRAW",
        PileType.Discard => "DISCARD",
        PileType.Exhaust => "EXHAUST",
        _ => null
    };

    private static string? SafeGetCardId(CardModel card)
    {
        try { return card.Id.Entry; }
        catch (Exception ex) { Warn("card.id", ex); return null; }
    }

    // ------------------------------------------------------------------ the source

    private enum SelectionShape { Hand, Grid, Choose }

    /// <summary>
    /// A selection the game has started, with what opened it and what identifies its screen: the prompt of its prefs
    /// (the screen keeps a copy of the prefs, holding the same LocString), the combat pile, or the card list
    /// choose-a-card was given.
    /// </summary>
    private sealed class SelectionSourceCapture
    {
        public SelectionShape Shape;
        public AbstractModel? Source;
        public LocString? Prompt;
        public CardPile? Pile;
        public object? Cards;
        /// <summary>The entry point's own task; the capture is live until it completes.</summary>
        public Task? Selection;
    }

    /// <summary>Oldest first. Guarded by its own lock, though the game and the state builder both use it on the main thread.</summary>
    private static readonly List<SelectionSourceCapture> _selectionSources = new();

    /// <summary>A bound on the list, for selections whose task never completes (a combat torn down mid-choice).</summary>
    private const int MaxSelectionSources = 32;

    private static SelectionSourceCapture RecordSelectionSource(
        SelectionShape shape, AbstractModel? source, LocString? prompt, CardPile? pile, object? cards)
    {
        var capture = new SelectionSourceCapture { Shape = shape, Source = source, Prompt = prompt, Pile = pile, Cards = cards };
        lock (_selectionSources)
        {
            _selectionSources.RemoveAll(c => c.Selection is { IsCompleted: true });
            if (_selectionSources.Count >= MaxSelectionSources)
                _selectionSources.RemoveAt(0);
            _selectionSources.Add(capture);
        }
        return capture;
    }

    /// <summary>
    /// The source of the live selection that opened this screen, or null. Several can be pending at once (hooks
    /// queue their choices while an earlier one is on screen), so a capture is matched to the screen by identity, not
    /// by recency: the same prompt object (and, for a combat pile screen, the same pile), or for choose-a-card the
    /// same card list. Should two live captures ever match, the newest wins.
    /// </summary>
    private static AbstractModel? FindSelectionSource(SelectionShape shape, LocString? prompt, CardPile? pile, object? cards)
    {
        lock (_selectionSources)
        {
            for (int i = _selectionSources.Count - 1; i >= 0; i--)
            {
                var capture = _selectionSources[i];
                if (capture.Shape != shape || capture.Selection is not { IsCompleted: false })
                    continue;
                bool sameScreen = shape == SelectionShape.Choose
                    ? cards != null && ReferenceEquals(capture.Cards, cards)
                    : prompt != null && ReferenceEquals(capture.Prompt, prompt) && (capture.Pile == null || ReferenceEquals(capture.Pile, pile));
                if (sameScreen)
                    return capture.Source;
            }
        }
        return null;
    }

    /// <summary>
    /// The model the game itself names as involved in a choice: PlayerChoiceContext.LastInvolvedModel, the top of the
    /// context's model stack. CardModel.OnPlayWrapper pushes the card being played, PotionModel.OnUseWrapper the
    /// potion, and the hook runner each relic, power or card it calls; the game shows remote players this model as the
    /// one they are waiting on (NMultiplayerPlayerIntentHandler).
    /// </summary>
    private static AbstractModel? InvolvedModel(PlayerChoiceContext? context)
    {
        try { return context?.LastInvolvedModel; }
        catch (Exception) { return null; }
    }

    private static void EndSelectionCapture(object? state, Task task)
    {
        if (state is SelectionSourceCapture capture)
            capture.Selection = task;
    }

    /// <summary>
    /// A game method a source patch binds to: found by name and exact parameter types, returning a Task, and only
    /// while its parameters still carry the names the prefix reads (Harmony binds arguments by name, and a renamed one
    /// throws inside PatchAll, which would take the mod's other patches with it). Null when the game has changed it.
    /// </summary>
    private static MethodInfo? FindSelectionPatchTarget(Type type, string name, Type[] parameterTypes, string[] parameterNames)
    {
        try
        {
            var method = AccessTools.Method(type, name, parameterTypes);
            if (method == null || !typeof(Task).IsAssignableFrom(method.ReturnType))
                return null;
            var names = method.GetParameters().Select(p => p.Name).ToHashSet();
            return parameterNames.All(names.Contains) ? method : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Prepare for a source patch. TargetMethods' yield break alone is not enough: the game's Harmony (2.4) throws on
    /// a patch class with no target, inside PatchAll. Returning false makes Harmony skip the class instead.
    /// </summary>
    private static bool PrepareSelectionPatch(MethodInfo? target, string label)
    {
        if (target != null)
            return true;
        GD.Print($"[STS2 MCP] {label} not found: selection.source reads unknown on the screens it opens");
        return false;
    }

    private static IEnumerable<MethodBase> SelectionPatchTargets(MethodInfo? target)
    {
        if (target == null)
            yield break;
        yield return target;
    }

    /// <summary>Every hand selection (FromHand, FromHandForDiscard, FromHandForUpgrade) is handed its source here.</summary>
    [HarmonyPatch]
    private static class HandSelectionSourcePatch
    {
        private static MethodInfo? Target() => FindSelectionPatchTarget(typeof(NPlayerHand), "SelectCards",
            new[] { typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>), typeof(AbstractModel), typeof(NPlayerHand.Mode) },
            new[] { "prefs", "source" });

        private static bool Prepare() => PrepareSelectionPatch(Target(), "NPlayerHand.SelectCards");
        private static IEnumerable<MethodBase> TargetMethods() => SelectionPatchTargets(Target());

        private static void Prefix(CardSelectorPrefs prefs, AbstractModel? source, out object? __state)
            => __state = RecordSelectionSource(SelectionShape.Hand, source, prefs.Prompt, pile: null, cards: null);

        private static void Postfix(Task __result, object? __state) => EndSelectionCapture(__state, __result);
    }

    /// <summary>A grid over a combat pile (Headbutt, Seeker Strike, Secret Technique, Liquid Memories...).</summary>
    [HarmonyPatch]
    private static class CombatPileSelectionSourcePatch
    {
        private static MethodInfo? Target() => FindSelectionPatchTarget(typeof(CardSelectCmd), "FromCombatPile",
            new[] { typeof(PlayerChoiceContext), typeof(CardPile), typeof(Player), typeof(CardSelectorPrefs), typeof(Func<CardModel, bool>) },
            new[] { "context", "pile", "prefs" });

        private static bool Prepare() => PrepareSelectionPatch(Target(), "CardSelectCmd.FromCombatPile");
        private static IEnumerable<MethodBase> TargetMethods() => SelectionPatchTargets(Target());

        private static void Prefix(PlayerChoiceContext context, CardPile pile, CardSelectorPrefs prefs, out object? __state)
            => __state = RecordSelectionSource(SelectionShape.Grid, InvolvedModel(context), prefs.Prompt, pile, cards: null);

        private static void Postfix(Task __result, object? __state) => EndSelectionCapture(__state, __result);
    }

    /// <summary>A grid over a list of cards (Choices Paradox).</summary>
    [HarmonyPatch]
    private static class SimpleGridSelectionSourcePatch
    {
        private static MethodInfo? Target() => FindSelectionPatchTarget(typeof(CardSelectCmd), "FromSimpleGrid",
            new[] { typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(CardSelectorPrefs) },
            new[] { "context", "prefs" });

        private static bool Prepare() => PrepareSelectionPatch(Target(), "CardSelectCmd.FromSimpleGrid");
        private static IEnumerable<MethodBase> TargetMethods() => SelectionPatchTargets(Target());

        private static void Prefix(PlayerChoiceContext context, CardSelectorPrefs prefs, out object? __state)
            => __state = RecordSelectionSource(SelectionShape.Grid, InvolvedModel(context), prefs.Prompt, pile: null, cards: null);

        private static void Postfix(Task __result, object? __state) => EndSelectionCapture(__state, __result);
    }

    /// <summary>A grid over cards made for the deck (Sea Glass, and events).</summary>
    [HarmonyPatch]
    private static class RewardGridSelectionSourcePatch
    {
        private static MethodInfo? Target() => FindSelectionPatchTarget(typeof(CardSelectCmd), "FromSimpleGridForRewards",
            new[] { typeof(PlayerChoiceContext), typeof(List<CardCreationResult>), typeof(Player), typeof(CardSelectorPrefs) },
            new[] { "context", "prefs" });

        private static bool Prepare() => PrepareSelectionPatch(Target(), "CardSelectCmd.FromSimpleGridForRewards");
        private static IEnumerable<MethodBase> TargetMethods() => SelectionPatchTargets(Target());

        private static void Prefix(PlayerChoiceContext context, CardSelectorPrefs prefs, out object? __state)
            => __state = RecordSelectionSource(SelectionShape.Grid, InvolvedModel(context), prefs.Prompt, pile: null, cards: null);

        private static void Postfix(Task __result, object? __state) => EndSelectionCapture(__state, __result);
    }

    /// <summary>Choose-a-card (Attack / Skill / Power / Colorless Potion, Discovery, Toolbox...).</summary>
    [HarmonyPatch]
    private static class ChooseACardSelectionSourcePatch
    {
        private static MethodInfo? Target() => FindSelectionPatchTarget(typeof(CardSelectCmd), "FromChooseACardScreen",
            new[] { typeof(PlayerChoiceContext), typeof(IReadOnlyList<CardModel>), typeof(Player), typeof(bool) },
            new[] { "context", "cards" });

        private static bool Prepare() => PrepareSelectionPatch(Target(), "CardSelectCmd.FromChooseACardScreen");
        private static IEnumerable<MethodBase> TargetMethods() => SelectionPatchTargets(Target());

        private static void Prefix(PlayerChoiceContext context, IReadOnlyList<CardModel> cards, out object? __state)
            => __state = RecordSelectionSource(SelectionShape.Choose, InvolvedModel(context), prompt: null, pile: null, cards);

        private static void Postfix(Task __result, object? __state) => EndSelectionCapture(__state, __result);
    }
}
