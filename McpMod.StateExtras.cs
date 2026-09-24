using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Saves.Runs;

namespace STS2_MCP;

/// <summary>
/// The state a simulator needs to rebuild a combat position exactly (schema 4): per-card
/// enchantment, affliction and cost overrides; per-enemy move ids, AI history, AI fields, slot
/// and numeric intent damage; per-relic status and counters; and the turn's and combat's
/// card-play counts. StateBuilder calls in here; the shapes are documented in docs/raw-full.md.
///
/// Everything reported is something the player can see or derive from what they have seen.
/// The monster AI's randomness is never read: no RunRng.MonsterAi, no seeds or RNG counters,
/// and of the move graph only the moves already rolled (state_log, move_id) and the edge out
/// of the current move (move_follow_up_id, which names a branch state without resolving it).
///
/// Every read is guarded on its own and lands in `warnings` when it fails, so a game update
/// that renames one member costs that one field, never the payload.
/// </summary>
public static partial class McpMod
{
    // ------------------------------------------------------------------ cards

    /// <summary>
    /// Adds `enchantment`, `affliction` and `fields` to a card object. Called for every card the
    /// state reports: the hand, the draw/discard/exhaust piles, the run deck and the card screens
    /// that share BuildCardInfo.
    /// </summary>
    private static void AddCardExtras(Dictionary<string, object?> cardInfo, CardModel card)
    {
        cardInfo["enchantment"] = BuildEnchantmentState(card);
        cardInfo["affliction"] = GetAfflictionId(card);
        cardInfo["fields"] = BuildCardFields(card);
    }

    private static Dictionary<string, object?>? BuildEnchantmentState(CardModel card)
    {
        try
        {
            var enchantment = card.Enchantment;
            if (enchantment == null)
                return null;
            return new Dictionary<string, object?>
            {
                ["id"] = enchantment.Id.Entry,
                ["amount"] = enchantment.Amount
            };
        }
        catch (Exception ex) { Warn("card.enchantment", ex); return null; }
    }

    private static string? GetAfflictionId(CardModel card)
    {
        try { return card.Affliction?.Id.Entry; }
        catch (Exception ex) { Warn("card.affliction", ex); return null; }
    }

    /// <summary>CardEnergyCost keeps its overrides in this private list and exposes no reader for it.</summary>
    private static readonly FieldInfo? _localCostModifiersField = typeof(CardEnergyCost)
        .GetField("_localModifiers", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// The card's runtime energy-cost overrides (see ComputeCostOverrides). Empty for a card
    /// without any; null when they could not be read.
    ///
    /// A fresh copy of a card carries no overrides. An upgrade is not one: it changes the card's
    /// base cost (CardEnergyCost's base, which GetWithModifiers(None) returns), and the overrides
    /// are measured against that base, so "differs from a fresh copy" is exactly "an override
    /// changes the cost".
    /// </summary>
    private static Dictionary<string, object?>? BuildCardFields(CardModel card)
    {
        try
        {
            var fields = new Dictionary<string, object?>();
            if (card.IsCanonical)
                return fields;

            var energyCost = card.EnergyCost;
            // GetWithModifiers skips every override on an X-cost card and on one whose base cost
            // is negative (unplayable), so on those no override changes what the card costs.
            if (energyCost.CostsX || !energyCost.HasLocalModifiers)
                return fields;
            int baseCost = energyCost.GetWithModifiers(CostModifiers.None);
            if (baseCost < 0)
                return fields;

            if (_localCostModifiersField?.GetValue(energyCost) is not IReadOnlyList<LocalCostModifier> modifiers)
            {
                Warn("card.fields", "CardEnergyCost._localModifiers is missing");
                return null;
            }

            foreach (var (key, value) in ComputeCostOverrides(baseCost, modifiers))
                fields[key] = value;
            return fields;
        }
        catch (Exception ex) { Warn("card.fields", ex); return null; }
    }

    /// <summary>
    /// A card's cost overrides, split by how long they last and reported as absolute costs.
    ///
    /// CardEnergyCost folds its modifiers over the base cost in the order they were added (an
    /// absolute one resets the running cost, a relative one offsets it, a reduce-only one never
    /// raises it) and drops each one when it expires, keeping the rest in order. The cost at a
    /// given horizon is therefore the same fold over the modifiers still alive then:
    /// <list type="bullet">
    /// <item>cost_for_combat: only the whole-combat modifiers, i.e. what the card costs once every
    /// temporary override has worn off. Present when that differs from the base cost.</item>
    /// <item>cost_until_played: the whole-combat and the until-played ones, i.e. what it costs
    /// after this turn if it is still unplayed. Present when that differs from cost_for_combat.</item>
    /// <item>cost_this_turn: every modifier, i.e. what it costs right now. Present when that
    /// differs from either of the other two, so `cost_this_turn ?? cost_until_played ??
    /// cost_for_combat ?? base` is always the current cost, and a reader that only knows
    /// cost_this_turn and cost_for_combat still gets the current cost right.</item>
    /// </list>
    /// These are the card's own costs, before the global modifiers (Corruption, Enthralled...)
    /// that the game adds on top and only then clamps at 0. A relative override that overshoots
    /// (a Kingly Kick drawn often enough) is therefore reported below 0, as the game holds it.
    /// </summary>
    private static Dictionary<string, object?> ComputeCostOverrides(int baseCost, IReadOnlyList<LocalCostModifier> modifiers)
    {
        int forCombat = FoldCostModifiers(baseCost, modifiers,
            m => m.Expiration == LocalCostModifierExpiration.EndOfCombat);
        int untilPlayed = FoldCostModifiers(baseCost, modifiers,
            m => !m.Expiration.HasFlag(LocalCostModifierExpiration.EndOfTurn));
        int now = FoldCostModifiers(baseCost, modifiers, _ => true);

        var overrides = new Dictionary<string, object?>();
        if (now != forCombat || now != untilPlayed)
            overrides["cost_this_turn"] = now;
        if (untilPlayed != forCombat)
            overrides["cost_until_played"] = untilPlayed;
        if (forCombat != baseCost)
            overrides["cost_for_combat"] = forCombat;
        return overrides;
    }

    private static int FoldCostModifiers(
        int baseCost, IReadOnlyList<LocalCostModifier> modifiers, Func<LocalCostModifier, bool> alive)
    {
        int cost = baseCost;
        foreach (var modifier in modifiers)
        {
            if (alive(modifier))
                cost = modifier.Modify(cost);
        }
        return cost;
    }

    /// <summary>`id`, `is_upgraded` and `upgrade_level` on a draw/discard/exhaust pile entry.</summary>
    private static void AddPileCardIdentity(Dictionary<string, object?> entry, CardModel card)
    {
        try
        {
            entry["id"] = card.Id.Entry;
            entry["is_upgraded"] = card.IsUpgraded;
            entry["upgrade_level"] = card.CurrentUpgradeLevel;
        }
        catch (Exception ex)
        {
            Warn("card.pile_identity", ex);
            entry.TryAdd("id", null);
            entry.TryAdd("is_upgraded", null);
            entry.TryAdd("upgrade_level", null);
        }
    }

    private static string? SafeGetStableCardUid(CardModel card)
    {
        try { return GetStableCardUid(card); }
        catch (Exception ex) { Warn("card.uid", ex); return null; }
    }

    // ------------------------------------------------------------------ enemies

    /// <summary>
    /// `move_id`, `move_follow_up_id`, `is_stunned`, `state_log`, `slot` and `ai_fields`: what a
    /// simulator needs to carry this creature's AI on from the current position.
    /// </summary>
    private static void AddEnemyAiState(Dictionary<string, object?> state, Creature creature)
    {
        var monster = creature.Monster;

        string? moveId = null, followUpId = null;
        try
        {
            var move = monster?.NextMove;
            moveId = move?.StateId;
            // FollowUpState is the wired successor; FollowUpStateId covers a move that only names
            // its successor, like the STUNNED move a stun installs (it names the move to resume
            // with). A branch state is reported by its own id: which way it goes is rolled only
            // when the move after this one is picked, and is never read here.
            followUpId = move?.FollowUpState?.Id ?? move?.FollowUpStateId;
        }
        catch (Exception ex) { Warn("enemy.move_id", ex); }
        state["move_id"] = moveId;
        state["move_follow_up_id"] = followUpId;

        bool? stunned = null;
        try { stunned = creature.IsStunned; }
        catch (Exception ex) { Warn("enemy.is_stunned", ex); }
        state["is_stunned"] = stunned;

        state["state_log"] = BuildStateLog(monster);
        state["slot"] = GetEnemySlot(creature);
        state["ai_fields"] = BuildAiFields(monster);
    }

    /// <summary>
    /// MonsterMoveStateMachine.StateLog as state ids, oldest first: the move the machine landed
    /// on each time it rolled (repeats included), which is what its repeat and cooldown rules
    /// read. Branch states never appear in it (their ShouldAppearInLogs is false), and neither
    /// does a move installed without a roll (a stun's STUNNED move, SetMoveImmediate).
    /// </summary>
    private static List<string>? BuildStateLog(MonsterModel? monster)
    {
        try
        {
            var machine = monster?.MoveStateMachine;
            if (machine == null)
                return null;
            return machine.StateLog.Select(logged => logged.Id).ToList();
        }
        catch (Exception ex) { Warn("enemy.state_log", ex); return null; }
    }

    /// <summary>
    /// The creature's position among the enemy slots.
    ///
    /// An encounter that declares named slots (EncounterModel.Slots: Exoskeletons, Two-Tailed
    /// Rats, Living Fog...) places each enemy at a fixed slot, and summons fill the free ones;
    /// the slot's index in that list is the answer, and it does not move when a neighbour dies.
    /// Every other encounter has no fixed slots: the room lays its enemies out left to right in
    /// ICombatState.Enemies order, which is also their turn order, so the index there is used.
    /// That one drops by one when an enemy before it leaves the fight.
    ///
    /// Null for a creature on the player's side (an ally such as Osty) and for a slot name the
    /// encounter does not declare.
    /// </summary>
    private static int? GetEnemySlot(Creature creature)
    {
        try
        {
            var combatState = creature.CombatState;
            if (combatState == null || !creature.IsEnemy)
                return null;

            var slots = combatState.Encounter?.Slots;
            if (slots != null && slots.Count > 0)
            {
                for (int i = 0; i < slots.Count; i++)
                {
                    if (string.Equals(slots[i], creature.SlotName, StringComparison.Ordinal))
                        return i;
                }
                return null;
            }

            int index = 0;
            foreach (var enemy in combatState.Enemies)
            {
                if (ReferenceEquals(enemy, creature))
                    return index;
                index++;
            }
            return null;
        }
        catch (Exception ex) { Warn("enemy.slot", ex); return null; }
    }

    private static readonly ConcurrentDictionary<Type, PrimitiveMember[]> _aiFieldMembers = new();

    /// <summary>
    /// The concrete monster class's own state, by C# member name: the int, bool, enum and string
    /// instance fields and properties declared by the monster's class and its bases below
    /// MonsterModel (Toadpole._isFront, TwoTailedRat._turnsUntilSummonable...). What MonsterModel
    /// declares is left out, and so are the class's overrides of it (MinInitialHp, the sfx paths),
    /// which are fixed per class rather than state. An Rng-typed member (the monster's Rng, its
    /// RunRng set) is never one of those types, so the AI's randomness cannot leak through here.
    /// Entries at their type's default are left out (see ReadPrimitiveMembers).
    /// </summary>
    private static Dictionary<string, object?>? BuildAiFields(MonsterModel? monster)
    {
        if (monster == null)
            return null;
        try
        {
            var members = _aiFieldMembers.GetOrAdd(monster.GetType(), type =>
                CollectDeclaredPrimitiveMembers(type, typeof(MonsterModel), includeProperties: true, IsAiFieldType));
            return ReadPrimitiveMembers(monster, members, "enemy.ai_fields");
        }
        catch (Exception ex) { Warn("enemy.ai_fields", ex); return null; }
    }

    private static bool IsAiFieldType(Type type)
    {
        if (type == typeof(string))
            return true;
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying == typeof(int) || underlying == typeof(bool) || underlying.IsEnum;
    }

    /// <summary>
    /// `damage`, `base_damage` and `hits` on an attack intent (an AttackIntent: IntentType Attack
    /// or DeathBlow). `damage` is AttackIntent.GetSingleDamage, the per-hit number the intent
    /// shows the player: the move's damage run through Hook.ModifyDamage against the local
    /// player (Strength, Weak, Vulnerable and the rest). `base_damage` is the move's own per-hit
    /// damage before any of that, AttackIntent.DamageCalc. `hits` is AttackIntent.Repeats.
    /// </summary>
    private static void AddAttackIntentDamage(Dictionary<string, object?> intentData, AttackIntent intent, Creature owner)
    {
        int? damage = null, baseDamage = null, hits = null;
        try
        {
            IEnumerable<Creature> targets = owner.CombatState?.PlayerCreatures ?? (IEnumerable<Creature>)Array.Empty<Creature>();
            damage = intent.GetSingleDamage(targets, owner);
        }
        catch (Exception ex) { Warn("enemy.intent.damage", ex); }
        try
        {
            var damageCalc = intent.DamageCalc;
            if (damageCalc != null)
                baseDamage = (int)damageCalc();
        }
        catch (Exception ex) { Warn("enemy.intent.base_damage", ex); }
        try { hits = intent.Repeats; }
        catch (Exception ex) { Warn("enemy.intent.hits", ex); }

        intentData["damage"] = damage;
        intentData["base_damage"] = baseDamage;
        intentData["hits"] = hits;
    }

    // ------------------------------------------------------------------ relics

    /// <summary>`status`, `is_used_up`, `stack_count`, `saved` and `fields` on a relic object.</summary>
    private static void AddRelicExtras(Dictionary<string, object?> relicState, RelicModel relic)
    {
        string? status = null;
        bool? usedUp = null;
        int? stackCount = null;
        try { status = relic.Status.ToString(); }
        catch (Exception ex) { Warn("relic.status", ex); }
        try { usedUp = relic.IsUsedUp; }
        catch (Exception ex) { Warn("relic.is_used_up", ex); }
        try { stackCount = relic.StackCount; }
        catch (Exception ex) { Warn("relic.stack_count", ex); }

        relicState["status"] = status;
        relicState["is_used_up"] = usedUp;
        relicState["stack_count"] = stackCount;
        relicState["saved"] = BuildSavedProps(relic);
        relicState["fields"] = BuildRelicFields(relic);
    }

    /// <summary>
    /// The relic's [SavedProperty] values, by property name: what the game writes to the save
    /// file for it (PenNib.AttacksPlayed, HappyFlower.TurnsSeen, LizardTail.WasUsed...).
    /// SavedProperties.From is the call RelicModel.ToSerializable makes. Null when the relic
    /// saves nothing or the read failed.
    /// </summary>
    private static Dictionary<string, object?>? BuildSavedProps(RelicModel relic)
    {
        try { return FlattenSavedProperties(SavedProperties.From(relic)); }
        catch (Exception ex) { Warn("relic.saved", ex); return null; }
    }

    /// <summary>
    /// SavedProperties' typed lists as one name → value map. Model ids are reported by Entry;
    /// the card-valued lists (a relic that stores whole cards) are left out.
    /// </summary>
    private static Dictionary<string, object?>? FlattenSavedProperties(SavedProperties? props)
    {
        if (props == null)
            return null;
        var values = new Dictionary<string, object?>();
        foreach (var p in props.ints ?? new()) values[p.name] = p.value;
        foreach (var p in props.bools ?? new()) values[p.name] = p.value;
        foreach (var p in props.strings ?? new()) values[p.name] = p.value;
        foreach (var p in props.intArrays ?? new()) values[p.name] = p.value;
        foreach (var p in props.modelIds ?? new()) values[p.name] = p.value?.Entry;
        return values.Count > 0 ? values : null;
    }

    private static readonly ConcurrentDictionary<Type, PrimitiveMember[]> _relicFieldMembers = new();

    /// <summary>
    /// The relic class's own int and bool instance fields, declared by its class and its bases
    /// below RelicModel: the per-turn and per-combat counters a save does not keep
    /// (Kunai._attacksPlayedThisTurn, CentennialPuzzle._usedThisCombat...) next to the saved
    /// ones. Entries at false / 0 are left out (see ReadPrimitiveMembers).
    /// </summary>
    private static Dictionary<string, object?>? BuildRelicFields(RelicModel relic)
    {
        try
        {
            var members = _relicFieldMembers.GetOrAdd(relic.GetType(), type =>
                CollectDeclaredPrimitiveMembers(type, typeof(RelicModel), includeProperties: false,
                    fieldType => fieldType == typeof(int) || fieldType == typeof(bool)));
            return ReadPrimitiveMembers(relic, members, "relic.fields");
        }
        catch (Exception ex) { Warn("relic.fields", ex); return null; }
    }

    // ------------------------------------------------------------------ history

    /// <summary>
    /// The local player's card-play counts from CombatManager.Instance.History, counted the way
    /// the game's own cards count them (Stomp, Pinpoint, Finisher, FTL): CardPlaysFinished
    /// entries for cards the local player owns, HappenedThisTurn for the per-turn ones. An
    /// auto-play and each Replay repeat is a play there too; a card still resolving is not yet
    /// one. The per-turn counts are 0 during the enemy turn.
    /// </summary>
    private static Dictionary<string, object?>? BuildCombatHistory(ICombatState combatState)
    {
        try
        {
            int thisTurn = 0, attacks = 0, skills = 0, powers = 0, thisCombat = 0;
            foreach (var entry in CombatManager.Instance.History.CardPlaysFinished)
            {
                var card = entry.CardPlay.Card;
                if (!LocalContext.IsMine(card))
                    continue;
                thisCombat++;
                if (!entry.HappenedThisTurn(combatState))
                    continue;
                thisTurn++;
                switch (card.Type)
                {
                    case CardType.Attack: attacks++; break;
                    case CardType.Skill: skills++; break;
                    case CardType.Power: powers++; break;
                }
            }

            return new Dictionary<string, object?>
            {
                ["cards_played_this_turn"] = thisTurn,
                ["attacks_played_this_turn"] = attacks,
                ["skills_played_this_turn"] = skills,
                ["powers_played_this_turn"] = powers,
                ["cards_played_this_combat"] = thisCombat
            };
        }
        catch (Exception ex) { Warn("battle.history", ex); return null; }
    }

    // ------------------------------------------------------------------ reflection

    /// <summary>A primitive instance member read off a model, and the key it is reported under.</summary>
    private sealed record PrimitiveMember(string Name, MemberInfo Member, bool IsNullable);

    private const string BackingFieldSuffix = ">k__BackingField";

    /// <summary>
    /// The instance fields (and, if asked, properties) declared by <paramref name="type"/> and its
    /// bases up to but excluding <paramref name="stopAt"/>, whose type <paramref name="isWanted"/>
    /// accepts. The most derived declaration of a name wins. An auto-property's compiler-generated
    /// backing field is keyed by the property's name; other compiler-generated fields are skipped.
    /// A property that overrides a member declared at or above <paramref name="stopAt"/> belongs
    /// to that base class and is skipped, as are indexers and properties without a getter.
    /// </summary>
    private static PrimitiveMember[] CollectDeclaredPrimitiveMembers(
        Type type, Type stopAt, bool includeProperties, Func<Type, bool> isWanted)
    {
        const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public |
                                      BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var members = new List<PrimitiveMember>();
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var current = type; current != null && current != stopAt && current != typeof(object); current = current.BaseType)
        {
            foreach (var field in current.GetFields(Declared))
            {
                string? name = field.Name;
                if (name.StartsWith('<'))
                {
                    name = name.EndsWith(BackingFieldSuffix, StringComparison.Ordinal)
                        ? name[1..^BackingFieldSuffix.Length]
                        : null;
                }
                if (name != null && isWanted(field.FieldType) && names.Add(name))
                    members.Add(new PrimitiveMember(name, field, Nullable.GetUnderlyingType(field.FieldType) != null));
            }

            if (!includeProperties)
                continue;

            foreach (var property in current.GetProperties(Declared))
            {
                var getter = property.GetMethod;
                if (getter == null || property.GetIndexParameters().Length > 0 || !isWanted(property.PropertyType))
                    continue;
                var declaredBy = getter.GetBaseDefinition().DeclaringType;
                if (declaredBy != null && declaredBy.IsAssignableFrom(stopAt))
                    continue;
                if (names.Add(property.Name))
                    members.Add(new PrimitiveMember(property.Name, property, Nullable.GetUnderlyingType(property.PropertyType) != null));
            }
        }

        return members.ToArray();
    }

    /// <summary>
    /// Reads each member off <paramref name="source"/>. Values at their type's default are left
    /// out (false, 0, an enum's zero value, a null string), so a missing key reads as that
    /// default; a nullable member is left out only when null. Enums are reported by name. A
    /// member whose read throws is skipped and named in `warnings`.
    /// </summary>
    private static Dictionary<string, object?> ReadPrimitiveMembers(object source, PrimitiveMember[] members, string context)
    {
        var values = new Dictionary<string, object?>();
        foreach (var member in members)
        {
            object? value;
            try
            {
                value = member.Member is FieldInfo field
                    ? field.GetValue(source)
                    : ((PropertyInfo)member.Member).GetValue(source);
            }
            catch (Exception ex)
            {
                Warn($"{context}.{member.Name}", ex is TargetInvocationException { InnerException: { } inner } ? inner : ex);
                continue;
            }

            if (value == null || (!member.IsNullable && IsTypeDefault(value)))
                continue;
            values[member.Name] = value is Enum enumValue ? enumValue.ToString() : value;
        }
        return values;
    }

    private static bool IsTypeDefault(object value) => value switch
    {
        bool flag => !flag,
        int number => number == 0,
        Enum enumValue => enumValue.Equals(Enum.ToObject(enumValue.GetType(), 0)),
        _ => false
    };
}
