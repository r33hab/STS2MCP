# Full API Reference

HTTP API served by the STS2_MCP mod on `localhost:15526`. Local use only. Every request needs the API token
from `STS2_MCP.conf`, and web pages are refused outright — see [Authentication and configuration](#authentication-and-configuration).

**Endpoints:**
- `GET  /` — health check: `{ "message", "status", "version", "commit", "schema_version" }`
- `GET  /api/v1/singleplayer` — read current game state
- `POST /api/v1/singleplayer` — perform a game action
- `GET  /api/v1/multiplayer` — read multiplayer game state
- `POST /api/v1/multiplayer` — perform a multiplayer action
- `GET  /api/v1/profile` — read current profile progress
- `GET  /api/v1/compendium` — read Compendium-shaped profile progress
- `GET  /api/v1/wiki` — fuzzy-search discovered card/relic wiki entries
- `GET  /api/v1/profiles` — list profile slots
- `POST /api/v1/profiles` — switch or delete profile slots
- `GET  /api/v1/actions?since=<seq>` — the player's moves (card plays, potions, end turns), each tagged human or API; see [Action log](#action-log)

The singleplayer and multiplayer endpoints are mutually exclusive: calling singleplayer during a multiplayer run (or vice versa) returns HTTP 409.

---

## Authentication and configuration

Every request, to every endpoint including `GET /`, must carry the API token in one of these headers:

```
X-STS2-Token: <token>
Authorization: Bearer <token>
```

If `X-STS2-Token` is present it is the one checked; a wrong `X-STS2-Token` is not rescued by a correct
`Authorization` header. The comparison runs in constant time.

These checks run before any routing, in this order:

| Check | Failure |
|---|---|
| The request carries an `Origin` header, i.e. it came from a web page | HTTP 403 `{"status": "error", "error": "Browser origins are not permitted"}` |
| The token is missing or wrong | HTTP 401 `{"status": "error", "error": "Missing or invalid token"}` |
| A `POST` body is not `Content-Type: application/json` (parameters such as `; charset=utf-8` are fine) | HTTP 415 `{"status": "error", "error": "POST bodies must be application/json"}` |

No CORS headers are sent, and a CORS preflight (`OPTIONS` with `Origin`) gets the same 403: no web page can
call this API or read its replies.

### `STS2_MCP.conf`

A JSON file next to the mod DLL (`mods/STS2_MCP/STS2_MCP.conf`). The mod reads it once, when the game starts:
restart the game after editing it.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `port` | int | `15526` | Port to listen on. The server binds `localhost` and `127.0.0.1` only. |
| `token` | string | generated | The API token. When it is absent or blank, the mod generates 64 random hex characters and writes them back into the file, keeping every other key. |
| `allow_hidden_state` | bool | `false` | Allow `?hidden=1` reads of [hidden information](#hidden-information). |
| `allow_debug_actions` | bool | `false` | Allow the [debug and staging actions](#debug-and-staging-actions). |

- The environment variable `STS2_MCP_TOKEN`, when set to a non-blank value, takes precedence over `token`; no
  token is generated or written then.
- The two gates are on only for a literal JSON `true`. `"true"` or `1` leave them off.
- On macOS and Linux the file is readable by its owner only (`0600`). When the mod creates the file or writes a
  generated token into it, it writes a new file that is `0600` from the start and moves it into place, so the
  token is never on disk with wider permissions. When it loads a file that grants more (a hand-made file at
  `0644`, say), it narrows it to `0600`; a tighter one (`0400`) is left as it is.
- A file that is not a valid JSON object is reported in the game log and its contents are left untouched (its
  mode is still narrowed). The mod then runs with the default port, both gates off, and a random token it does
  not publish, so every request is refused until the file is fixed.

```jsonc
{
  "port": 15526,
  "token": "…64 hex characters…",
  "allow_hidden_state": false,
  "allow_debug_actions": false
}
```

```bash
# The mod's folder; this is the default Steam install on macOS. On Windows and Linux it is
# <game_install>/mods/STS2_MCP (see the README's install section).
MODS_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/MacOS/mods/STS2_MCP"
TOKEN=$(python3 -c 'import json, sys; print(json.load(open(sys.argv[1]))["token"])' "$MODS_DIR/STS2_MCP.conf")
curl -s -H "X-STS2-Token: $TOKEN" http://localhost:15526/
```

### Hidden information

A few fields reveal what the game never shows the player. They are left out of every response unless **both**
`allow_hidden_state` is `true` in `STS2_MCP.conf` **and** the `GET` request has the query parameter `hidden=1`
(exactly `1`). They exist for tests and capture tools; a coach or autopilot must not read them.

| Field | Endpoint | What it reveals |
|---|---|---|
| `player.draw_pile_ordered` | `GET /api/v1/singleplayer`, in combat | The draw pile's true order. The game's own draw-pile screen sorts it, exactly as `draw_pile` does. |
| `battle.enemies[].move_follow_up_id` (and `battle.allies[]`) | `GET /api/v1/singleplayer`, in combat | The state wired after the enemy's next move. When that is a move rather than a branch, it is the move the enemy performs after the one its intents show, which the game never shows. See [Enemy moves and AI](#monster--elite--boss--combat). |
| `current_run.seed` | `GET /api/v1/compendium` | The seed of the run in progress, which drives every RNG stream of the run. |

They never appear in `POST` responses (including the `state` a `wait: true` action returns), on the multiplayer
endpoint, or in markdown output. `hand_ordered`, `discard_pile_ordered` and `exhaust_pile_ordered` are always
reported in combat, because the game shows those piles in their true order. Card `uid`s are numbered so that they
never encode the draw order either (see [Card uids](#card-object-in-hand)).

---

## GET — Read Game State

### Query Parameters

| Parameter | Values                                        | Default | Description                                                     |
|-----------|-----------------------------------------------|---------|-----------------------------------------------------------------|
| `format`  | `json`, `markdown`                            | `json`  | Response format                                                 |
| `pretty`  | `1`, `true`, `yes` (or present with no value) | off     | Indent the JSON. Off by default — compact output is ~32% smaller |
| `hidden`  | `1`                                           | off     | Add the [hidden-information](#hidden-information) fields. Ignored unless `allow_hidden_state` is `true` in `STS2_MCP.conf` |

JSON responses are compact by default; `pretty` opts into indentation for that one request. It works on
every JSON-returning endpoint, on POST as well as GET (e.g. `POST /api/v1/singleplayer?pretty=1`), and on
error responses. It has no effect on `format=markdown`.

### Common Top-Level Fields

Every response — `menu` and error-ish states included — carries the build stamp:

```jsonc
{
  "state_type": "...",      // Screen identifier (see sections below)
  "mod_version": "0.4.0",   // Mod version
  "mod_commit": "2de67cd",  // Git commit built from ("<sha>-dirty" from an edited tree); null if unknown
  "schema_version": 4,      // Contract version — see below
  "action_log_seq": 57,     // seq of the newest action-log entry, 0 before the first — see "Action log"
  "time_scale": 1           // The game's speed (Godot Engine.TimeScale; 1 = normal) — see set_time_scale
}
```

`action_log_seq` starts at 0 each time the game starts and then only grows. When it moves,
`GET /api/v1/actions?since=<the seq you last read>` returns the new moves. `time_scale` is `null` (with a
`warnings` entry) if it could not be read. While the game's hit-stop runs (a fraction of a second on some heavy
hits, see [`set_time_scale`](#set_time_scale)) it reads lower than the scale that was set.

`schema_version` is bumped **only** when an existing field or action changes meaning or is removed.
Purely additive changes (new fields, actions, parameters, `state_type`s) do not bump it, so
feature-detect additions by presence and use `schema_version` only to notice a breaking change.

- **4** — `player.draw_pile_ordered` is gone from the default payload: the draw pile's true order
  is [hidden information](#hidden-information), sent only to an opted-in `?hidden=1` read, and
  every request needs the API token. The same version adds what a simulator needs to rebuild a
  combat position — enemy `move_id`, `is_stunned`, `state_log`, `slot`, `ai_fields` and
  attack-intent `damage` / `base_damage` / `hits` (and, on a hidden read only,
  `move_follow_up_id`); card `enchantment`, `affliction` and `fields`; pile card `id` /
  `is_upgraded` / `upgrade_level`; `uid` on the ordered piles; relic `status`, `is_used_up`,
  `stack_count`, `saved` and `fields`; and `battle.history` — so a client that relies on those
  can require `schema_version >= 4`.
- **3** — `map.next_encounters` is gone. It read which monster / elite / event the act would
  serve next out of the pre-rolled room list, which the game does not show until the room is
  entered. `map.encounters_seen` reports what the act has already served instead.
- **2** — `battle.enemies` is no longer "the living enemies": a corpse the game keeps in the
  fight (a Decimillipede segment waiting to reattach) is listed with `alive: false`. Filter on
  the new `alive` field instead of assuming every entry is a valid target.
- **1** — initial contract.

Every response except `menu` additionally includes these top-level fields alongside the
state-specific data:

```jsonc
{
  "state_type": "...",      // Screen identifier (see sections below)
  "run": {
    "act": 1,               // Current act (1-indexed)
    "floor": 3,             // Total floors visited
    "ascension": 0,         // Ascension level
    "map_coord": { "col": 3, "row": 2 }  // The map node you are on; null before the act's first node
  },
  "player": { ... },        // Full player state (see Player Object below)
  // ... state-specific fields
}
```

`run.map_coord` is the node the player last entered this act, in the coordinates of `map.nodes[].col/row` (the
game's `RunState.CurrentMapCoord`). It is `null` before the act's first node: on the map at the start of acts 2
and 3, and of an act 1 begun without Neow, until the starting node is chosen (the game's `RunManager.EnterAct`
enters the starting node itself only for an act 1 that starts with Neow). At Neow it is already the starting node
(row 0). It is on every payload that has a `run` block, combat included.

**The map outside combat.** The decision screens outside combat — `rewards`, `card_reward`, `event` (Neow and
the other Ancients included), `fake_merchant`, `rest_site`, `shop`, `treasure`, `card_select`, `bundle_select`,
`relic_select` and `crystal_sphere` — also carry the [`map`](#map--map-navigation) block, in the same shape as
on the map screen, so a client can weigh a choice against the route ahead. The player can open the map at any
moment with the map button, so nothing hidden is added. It is left out in combat (including a `card_select` a card
opens mid-fight) and on the other screens. Markdown output is unchanged: it renders the map on the map screen only.
A map that cannot be built costs only this block: the screen's payload is still sent, without `map` and with a
`map` entry in `warnings`.

### Player Object

Always present at the top level (except `menu`). Contains everything about the local player.

```jsonc
{
  "character": "The Ironclad",   // The character's title, in the game's current language
  "character_id": "IRONCLAD",    // The character's id: the same in every language
  "hp": 72,
  "max_hp": 80,
  "block": 0,
  "gold": 99,

  // --- Combat-only fields (present only when CombatManager.IsInProgress) ---
  "energy": 3,
  "max_energy": 3,
  "stars": 2,               // Regent only; omitted if 0 and character doesn't always show stars
  "hand": [ /* Card Objects */ ],
  "draw_pile_count": 15,
  "discard_pile_count": 3,
  "exhaust_pile_count": 1,
  "draw_pile": [ /* Pile Card Objects (sorted by rarity, matching in-game display) */ ],
  "discard_pile": [ /* Pile Card Objects */ ],
  "exhaust_pile": [ /* Pile Card Objects */ ],
  // The same cards in the game's own order: the hand as in `hand`, and for the piles index 0 is the top.
  // `uid` is the card's uid from `hand` / the Pile Card Objects, so each entry can be matched to its full entry.
  "hand_ordered": [ { "id": "STRIKE_IRONCLAD", "is_upgraded": false, "uid": "STRIKE_IRONCLAD#0" } ],
  "discard_pile_ordered": [ /* same shape */ ],
  "exhaust_pile_ordered": [ /* same shape */ ],
  "draw_pile_ordered": [ /* same shape; index 0 is drawn next. Hidden information: only with
                           allow_hidden_state and ?hidden=1, see "Hidden information" */ ],
  "orbs": [ /* Orb Objects */ ],   // Defect only; omitted if orb capacity is 0
  "orb_slots": 3,
  "orb_empty_slots": 1,
  "pets": [                        // Necrobinder only; omitted if no pets
    {
      "id": "OSTY",
      "name": "Otsy",
      "alive": true,
      "hp": 12,
      "max_hp": 12,
      "block": 0,
      "status": [ /* Power Objects */ ]
    }
  ],

  // --- Always present ---
  "status": [ /* Power Objects */ ],
  "relics": [
    {
      "id": "PEN_NIB",
      "name": "Pen Nib",
      "description": "Every 10th Attack you play deals double damage.",
      "counter": 3,          // Number if relic shows a counter, null otherwise (the UI number; see below)
      "keywords": [ /* Keyword Objects */ ],
      "status": "Normal",    // RelicStatus: "Normal", "Active" or "Disabled"
      "is_used_up": false,   // A relic with limited uses that has spent them all
      "stack_count": 1,      // Copies of a stackable relic held as this one entry
      "saved": { "AttacksPlayed": 13 },  // Its [SavedProperty] values; null if it saves none
      "fields": { "_attacksPlayed": 13 } // Its class's own int/bool fields; {} if all are 0/false
    }
  ],
  "potions": [
    {
      "id": "SWIFT_POTION",
      "name": "Swift Potion",
      "description": "Draw 3 cards.",
      "slot": 0,             // Potion slot index (use this for use_potion action)
      "can_use_in_combat": true,
      "target_type": "None", // None, Self, AnyEnemy, AnyAlly, AnyPlayer, etc.
      "keywords": [ /* Keyword Objects */ ]
    }
  ],
  "max_potion_slots": 3,     // Belt capacity. Default 3, grows with belt-expanding relics (e.g. Potion Belt: +2). Use to detect a full belt: len(potions) >= max_potion_slots.
  "deck_count": 12,          // Cards in the master deck
  "deck": [ /* Deck Card Objects, see below */ ]
}
```

**The character.** `character` is the character's title as the game shows it, so it changes with the game's
language ("The Ironclad" in English). `character_id` is the character model's id (`IRONCLAD`, `SILENT`,
`DEFECT`, `NECROBINDER`, `REGENT`), the same in every language; match on that one. It is `null` only when it
could not be read, and `warnings` then names `player.character_id`.

**The run deck.** `deck_count` and `deck` are present on every screen, combat included, as soon as the
run has a deck (before the starting deck is dealt out, `deck` is empty and `deck_count` is 0). In a fight
this is still the master deck, not the combat piles: combat works on copies (the draw pile is built from
them), so cards generated during the fight appear in `hand` and the piles but not here. Each entry is
a Card Object without the hand-only fields (`uid`, `target_type`, `can_play`, `unplayable_reason`,
`vs_targets`), plus `index`, its position in this list. Markdown prints the deck outside combat only.

**Relic counters.** `counter` is the number the relic shows, and the game often transforms it: Pen Nib
shows `AttacksPlayed % 10`, Kunai only shows its count during combat. The raw state is reported beside it:

- `saved` — the relic's `[SavedProperty]` values by property name, i.e. what the game writes to the save
  file for it (Pen Nib's `AttacksPlayed`, Happy Flower's `TurnsSeen`, Lizard Tail's `WasUsed`). Ints,
  bools, strings, int arrays, and model ids as their id string; a property that holds whole cards is left
  out. `null` when the relic saves nothing.
- `fields` — the int and bool instance fields declared by the relic's own class (and its bases below
  `RelicModel`), by field name: the per-turn and per-combat counters a save does not keep, such as Kunai's
  `_attacksPlayedThisTurn` or Centennial Puzzle's `_usedThisCombat`, next to the saved ones. An
  auto-property's value is keyed by the property name. Fields at `0` / `false` are left out, so a missing
  key reads as `0` / `false`.

`status`, `is_used_up`, `stack_count`, `saved` and `fields` are `null` when they could not be read; the
reason is in `warnings`.

### Card Object (in hand)

```jsonc
{
  "index": 0,
  "uid": "STRIKE_IRONCLAD#0", // This card instance, for the whole combat (play_card accepts it as card_uid)
  "id": "STRIKE_IRONCLAD",
  "name": "Strike",
  "type": "Attack",          // Attack, Skill, Power, Status, Curse
  "cost": "1",               // Energy cost as string ("X" for X-cost): what it costs to play now
  "star_cost": null,         // Regent star cost as string, null if N/A
  "description": "Deal 6 damage.",
  "rarity": "Basic",
  "target_type": "AnyEnemy", // None, Self, AnyEnemy, AllEnemies, etc.
  "can_play": true,
  "unplayable_reason": null, // e.g. "NotEnoughEnergy", "Unplayable", null if playable
  "is_upgraded": false,
  "is_upgradable": true,
  "upgrade_level": 0,
  "max_upgrade_level": 1,
  "keywords": [ /* Keyword Objects */ ],
  "enchantment": null,       // { "id": "SHARP", "amount": 3 } when enchanted — see "Card state"
  "affliction": null,        // Affliction id such as "BOUND" when afflicted
  "fields": {},              // Runtime cost overrides — see "Card state"
  "unplayable": false        // The Unplayable keyword — see "Card state"
}
```

**Card uids.** `uid` names one card instance for the whole combat, as `<id>#<n>`, and `play_card` accepts it.
Its number never encodes where a card lies in the draw pile. When the mod first reads a fight (a state read, an
action-log entry or a `play_card`), it numbers the cards dealt from the master deck in master-deck order, so the
k-th Strike of `player.deck` is `STRIKE_IRONCLAD#k` wherever the shuffle put it (the game's
`Player.PopulateCombatState` links each dealt card to its deck card before shuffling). A card made during the
fight takes the next number of its id where it first appears: in pile order if that is the hand, discard, exhaust
or play pile, which the player sees in order; if it is first met in the draw pile (a Wound shuffled in), by what the
player can see of it (upgrade, enchantment, affliction) and, between cards alike in all of that, in random order.
Fresh cards on a choose-a-card screen are numbered in the order the screen shows them. Outside combat, the cards
of a `card_select` screen (deck cards) carry uids too; those last until the next fight starts. (The multiplayer
endpoint, left as it was, does not number a fight's cards before its state reads list them, so there a card can
take its number on first sight.)

### Card state (every card object)

Every card object — in `hand`, the Pile Card Objects, `deck`, and the card screens (`hand_select`,
`card_select`, `card_reward`, `shop`...) — carries the card's own state beside its text:

- `enchantment` — `{ "id": "SHARP", "amount": 3 }`: the enchantment's id and its amount, or `null`.
- `affliction` — the affliction's id (e.g. `"BOUND"`), or `null`.
- `unplayable` — `true` when the card has the Unplayable keyword now: a curse or status such as Wound, Injury or
  Dazed, or a card another model makes unplayable (the game's `CardModel.Keywords`, the check `CardModel.CanPlay`
  makes for its `Unplayable` reason). It is printed on the card, so it reveals nothing. Pile cards carry no
  `can_play`, so this is what tells a curse in the draw pile from a card that can be played.
- `fields` — the card's runtime **energy-cost overrides** (Bullet Time, Enlightenment, Eidolon, Kingly
  Kick...), as absolute costs, each present only when it changes the cost. `{}` when there are none, which
  is always the case for an X-cost card and a card that cannot be played (the game ignores overrides on
  those):

  | Key | Cost it reports | Present when |
  |---|---|---|
  | `cost_this_turn` | What the card costs right now, every override applied | it differs from `cost_until_played` or from `cost_for_combat` (an absent `cost_until_played` means the same as `cost_for_combat`, an absent `cost_for_combat` the base cost) |
  | `cost_until_played` | What it will cost after this turn if it is still unplayed — the "until played" overrides still apply | it differs from `cost_for_combat` |
  | `cost_for_combat` | What it costs once every temporary override has worn off (the "this combat" overrides) | it differs from the card's base cost |

  So `cost_this_turn ?? cost_until_played ?? cost_for_combat ?? base` is always the current cost, and a
  reader that only looks at `cost_this_turn` and `cost_for_combat` still gets the current cost right. The
  base cost is the card's printed cost with its upgrades — an upgrade is not an override, so an upgraded
  card with no overrides has `fields: {}`. These are the card's own costs, before the global modifiers
  that other cards, powers and relics apply (Corruption, Enthralled...), which the game adds on top and only
  then clamps at 0; a relative override that overshoots (a Kingly Kick drawn often enough) is reported
  below 0, as the game holds it. `cost` is the resulting number the card shows.

`enchantment`, `affliction`, `fields` and `unplayable` are `null` when they could not be read; the reason
is in `warnings`.

### Pile Card Object (draw/discard/exhaust piles)

```jsonc
{
  "uid": "STRIKE_IRONCLAD#1", // The same uid the card has in hand; stable for the whole combat
  "name": "Strike",
  "cost": "1",               // Energy cost as string ("X" for X-cost)
  "star_cost": null,         // Regent star cost as string, null if N/A
  "description": "Deal 6 damage.",
  "id": "STRIKE_IRONCLAD",
  "is_upgraded": false,
  "upgrade_level": 0,
  "enchantment": null,       // See "Card state"
  "affliction": null,
  "fields": {},
  "unplayable": false        // true for a curse or status (Wound, Injury...): see "Card state"
}
```

### Orb Object

```jsonc
{
  "id": "LIGHTNING",
  "name": "Lightning",
  "description": "Passive: Deal 3 damage to a random enemy. Evoke: Deal 8 damage.",
  "passive_val": 3,
  "evoke_val": 8,
  "keywords": [ /* Keyword Objects */ ]
}
```

### Power Object (status effects)

Used for both player `status` and enemy `status`.

```jsonc
{
  "id": "STRENGTH",
  "name": "Strength",
  "amount": 3,
  "type": "Buff",            // "Buff" or "Debuff"
  "description": "Increases attack damage by 3.",
  "keywords": [ /* Keyword Objects */ ]
}
```

### Keyword Object

```jsonc
{
  "name": "Vulnerable",
  "description": "Takes 50% more damage from attacks."
}
```

---

## State Types

### `menu`

No run in progress.

```jsonc
{
  "state_type": "menu",
  "message": "No run in progress. Player is in the main menu.",
  "menu_screen": "main",
  "options": ["continue", "singleplayer", "multiplayer", "compendium", "timeline", "settings", "quit"]
}
```

Use `menu_select` with one of the advertised options. Options are accepted case-insensitively.
If a visible option is intentionally withheld from automation, the state may also include `blocked_options` with a reason. For example, Timeline can be blocked while obtained epochs still need manual reveal because opening that game state through automation can trigger invalid unlock-state errors.

Menu sub-screens expose their own options:

- `singleplayer`: `standard`, `daily`, `custom`, `back`
- `multiplayer`: `host`, `join`, `load`, `abandon`, `back`
- `multiplayer_host`: `standard`, `daily`, `custom`, `back`
- `multiplayer_join`: `refresh`, `back`, `join_<index>`, `join_<player_id>`
- `multiplayer_load_lobby`: `confirm` / `embark`, `unready`, `back`
- `profile_select`: `profile_1`, `profile_2`, `profile_3`, `back`
- `character_select`: character IDs/names, `back`, `confirm` / `embark`, `unready` (MP, after readying)
- `tutorial_prompt`: `no`, `yes`
- `popup`: advertised popup button labels, normalized to lowercase words such as `ignore` or `back`
- `timeline`: `advance`, `back`

The `multiplayer` submenu is gated on whether a multiplayer save exists: with no save, `host` is shown and `load` / `abandon` are hidden; with a save, those are shown and `host` is hidden. `abandon` opens a vertical popup — the next state turn surfaces the popup's `confirm` / `cancel` options, not a transition.

#### `multiplayer_join` — Friend list / FastMP

```jsonc
{
  "state_type": "menu",
  "menu_screen": "multiplayer_join",
  "message": "Pick a friend to join, or 'refresh' to update the list.",
  "fast_mp": false,             // true when --fastmp / Steam not initialized; auto-joins 127.0.0.1:33771
  "loading": false,             // true while refreshing or while a join handshake is in flight
  "no_friends": false,          // true when refresh returned an empty list
  "friends": [
    { "index": 0, "name": "Bob", "player_id": "76561198000000000", "enabled": true }
  ],
  "options": [
    { "name": "join_0",  "enabled": true },
    { "name": "refresh", "enabled": true },
    { "name": "back",    "enabled": true }
  ]
}
```

`menu_select` accepts either `join_<index>` (e.g. `join_0`) or `join_<player_id>` (e.g. `join_76561198000000000`) — both click the same button. With `fast_mp: true`, the screen auto-joins localhost on open; you typically won't need to issue a join action in that mode.

#### `multiplayer_load_lobby` — Resume saved MP run

Reached via the `multiplayer` submenu's `load` option (host) or by joining a host that already loaded a save (client). Mirrors the standard MP character-select ready/unready flow but on a fixed (saved) party.

```jsonc
{
  "state_type": "menu",
  "menu_screen": "multiplayer_load_lobby",
  "message": "Multiplayer load lobby. Confirm to ready up; ...",
  "lobby": {
    "type": "host",                // or "client"
    "game_mode": "standard",       // or "daily" / "custom"
    "ascension": 0,
    "act": 2,
    "floor": 13,
    "character_id": "REGENT",      // local player's saved character
    "current_hp": 64,
    "max_hp": 80,
    "gold": 240,
    "expected_player_count": 3,
    "connected_player_count": 2,
    "players": [
      {
        "id": "76561198000000000",
        "is_local": true,
        "character_id": "REGENT",
        "is_connected": true,
        "is_ready": false,
        "platform_name": "Alice"
      }
    ]
  },
  "options": [
    { "name": "confirm", "enabled": true },
    { "name": "embark",  "enabled": true },   // alias of confirm
    { "name": "back",    "enabled": true },
    { "name": "unready", "enabled": false }   // becomes enabled after confirm/embark
  ]
}
```

#### `character_select` — extended for MP

The same screen drives SP, MP host, and MP client. In MP, an additional `lobby` block appears, and the `unready` option becomes available after the local player has hit `confirm`/`embark`:

```jsonc
{
  "state_type": "menu",
  "menu_screen": "character_select",
  "message": "Select a character.",
  "characters": [ /* same shape as SP */ ],
  "selected_character": "REGENT",  // The selected character, the one embark starts; absent until one is selected
  "selection_busy": false,         // true while the unlock animation may still change the selection
  "lobby": {                       // present only in MP
    "type": "host",                // "host" | "client" | "singleplayer"
    "game_mode": "standard",       // "standard" | "daily" | "custom"
    "max_players": 4,
    "ascension": 0,
    "max_ascension": 5,            // min across all lobby members' MaxMultiplayerAscension
    "all_ready": false,
    "is_about_to_begin": false,    // mirrors StartRunLobby.IsAboutToBeginGame()
    "is_local_ready": false,
    "local_player_id": "76561198000000000",
    "player_count": 2,
    "seed": "...",                 // optional, present only for daily/custom
    "players": [
      {
        "id": "76561198000000000",
        "slot_id": 0,
        "is_local": true,
        "is_host": true,           // only positively true for self when hosting; otherwise false
        "character": "The Regent",
        "character_id": "REGENT",
        "is_ready": false,
        "platform_name": "Alice"
      }
    ]
  },
  "options": [
    { "name": "REGENT",  "enabled": true },
    { "name": "IRONCLAD","enabled": true },
    /* ... other characters and lockable RANDOM ... */
    { "name": "confirm", "enabled": true },
    { "name": "embark",  "enabled": true },
    { "name": "back",    "enabled": true },
    { "name": "unready", "enabled": false }
  ]
}
```

In SP the `lobby` field is omitted, and `unready` does not appear (the unready button is only enabled after MP ready).

`selected_character` and `selection_busy` are reported in SP and MP. When the screen opens right after a character
was unlocked, the game plays an unlock animation that selects the new character about a second later,
overwriting any earlier pick. While that animation is pending, `selection_busy` is `true` and `menu_select` with
a character refuses with a retryable error ("Character selection is busy (unlock animation in progress); retry
in a moment."). Check that `selected_character` names your pick before you send `confirm`. If the game's
progress cannot be read, `selection_busy` is `false` and `warnings` has a `menu.character_select.selection_busy`
entry; a pick is then not refused.

### `unknown`

Run state or room type not recognized.

```jsonc
{
  "state_type": "unknown",
  "room_type": "SomeUnknownRoom"  // May be present
}
```

### `monster` / `elite` / `boss` — Combat

```jsonc
{
  "state_type": "monster",  // or "elite", "boss"
  "battle": {
    "round": 1,
    "turn": "player",       // "player" or "enemy"
    "is_play_phase": true,
    "enemies": [
      {
        "entity_id": "TOADPOLE_0",    // Synthesized ID for targeting
        "combat_id": 42,               // Internal combat ID
        "name": "Toadpole",
        "alive": true,                 // Always present. false = dead but still in the fight
        "hp": 18,
        "max_hp": 23,
        "block": 0,
        "status": [ /* Power Objects */ ],
        "intents": [
          {
            "type": "Attack",          // Attack, Defend, Buff, Debuff, Sleep, etc.
            "label": "10",             // Damage number or short label
            "title": "Attack",         // Hover tip title
            "description": "Deals 10 damage.", // Hover tip description
            "damage": 10,              // Attack intents only: per hit, as the intent shows it (7 × 1.5, the player is Vulnerable)
            "base_damage": 7,          // Attack intents only: per hit, before any modifier
            "hits": 1                  // Attack intents only: number of hits
          }
        ],
        "move_id": "WHIRL_MOVE",       // The move it will perform next (the game's move state id)
        "move_follow_up_id": "SPIKEN_MOVE", // Hidden read only: the state wired after that move (a move id or a branch state's id)
        "is_stunned": false,
        "state_log": ["SPIKEN_MOVE", "SPIKE_SPIT_MOVE", "WHIRL_MOVE"], // Moves its AI has rolled, oldest first
        "slot": 0,                     // Position among the enemy slots
        "ai_fields": {                 // The monster class's own state, by C# member name
          "_isFront": true, "IsFront": true,
          "SpikeSpitDamage": 3, "SpikeSpitRepeat": 3, "WhirlDamage": 7, "SpikenAmount": 2
        }
      },
      {
        // A dead enemy the game keeps in the fight (a Decimillipede segment waiting to
        // reattach). Listed so the revive is visible; it cannot be targeted while dead.
        "entity_id": "DECIMILLIPEDE_SEGMENT_1",
        "combat_id": 44,
        "name": "Decimillipede Segment",
        "alive": false,
        "hp": 0,
        "max_hp": 46,
        "block": 0,
        "status": [ /* still carries REATTACH_POWER */ ],
        "revive": {                    // Present only when it is actually coming back
          "in_turns": 2,               // Enemy turns until it heals back up; null if unknown
          "power_id": "REATTACH_POWER",
          "power_name": "Reattach",
          "hp": 25                     // HP it returns with; null when not readable
        },
        "intents": [],                 // Becomes a Heal intent on the turn before the revive
        "move_id": "DEAD_MOVE",
        "move_follow_up_id": "REATTACH_MOVE" // hidden read only
        // ...and is_stunned, state_log, slot, ai_fields as above
      }
    ],
    "allies": [ /* Osty and other pets, in the same shape; their slot is null */ ],
    "history": {                       // The local player's card plays (see below)
      "cards_played_this_turn": 2,
      "attacks_played_this_turn": 1,
      "skills_played_this_turn": 1,
      "powers_played_this_turn": 0,
      "cards_played_this_combat": 9
    }
  },
  "run": { ... },
  "player": { ... }  // Includes hand, energy, piles, orbs during combat
}
```

**Enemy moves and AI.** These let a simulator carry each enemy's AI on from the current position. By default
they report what the player has seen or can work out from it; the AI's randomness (its RNG, seeds and
counters) is never read, and of the move graph only the moves already rolled appear, plus, on a hidden read
only, the edge out of the current move:

- `move_id` — the id of the move the enemy will perform next (`MoveState.StateId`, e.g. `"WHIRL_MOVE"`),
  whose intents are `intents`. `"STUNNED"` while stunned; `"UNSET_MOVE"`, the game's placeholder, before
  its first move is rolled.
- `move_follow_up_id` — **[hidden information](#hidden-information)**, present only on a `?hidden=1` read
  with `allow_hidden_state` on. The state the game has wired to follow that move: another move's id, or the
  id of a branch state (a random or conditional choice), which is only resolved when the move after this one
  is rolled. When it is a move, it is the move the enemy performs after the one its intents show, which the
  game never shows the player. For `"STUNNED"` it is the move the enemy resumes with. `null` when there is
  none.
- `is_stunned` — `true` while the enemy's move is the stun (the game's `Creature.IsStunned`).
- `state_log` — the move ids its AI has rolled this combat, oldest first, repeats included: the game's
  `MonsterMoveStateMachine.StateLog`, which its repeat and cooldown rules read. The current move is
  normally the last entry. Branch states never appear in it, and neither does a move put in place without a
  roll (the stun's `"STUNNED"`, a Decimillipede segment's `"DEAD_MOVE"`). `null` when the enemy has no move
  machine.
- `slot` — its position among the enemy slots. An encounter that declares named slots (Exoskeletons,
  Two-Tailed Rats, Living Fog...) puts each enemy in a fixed slot and summons into the free ones; `slot` is
  that slot's index in the encounter's list, and it does not change when a neighbour dies. Other encounters
  lay their enemies out left to right in the order they act; there `slot` is the index in that order, and
  it drops by one when an enemy before it leaves the fight. `null` for allies.
- `ai_fields` — the monster class's own state, by C# member name: the int, bool, enum (by name) and string
  instance fields and properties that its class (and any base class below `MonsterModel`) declares, such as
  a Toadpole's `IsFront` or a Two-Tailed Rat's `_turnsUntilSummonable`. A property that only wraps a field
  appears under both names, and derived numbers such as a move's damage at the current ascension appear
  too. Values at their type's default (`false`, `0`, an enum's zero value, `null`) are left out, so a
  missing key reads as that default; a nullable number is left out only when it is `null`.

**Attack intents.** An intent that is an attack — `type` `"Attack"`, or `"DeathBlow"` — carries its numbers,
which `label` only has as text: `damage` is the per-hit damage the intent shows the player (the move's
damage after the game's damage modifiers against you: the enemy's Strength and Weak, your Vulnerable, and
so on), `base_damage` the move's own per-hit damage before any of them, and `hits` the number of hits. Both
damage numbers are floored at 0, as the game floors the intent's number. Other intents have none of the three.

Each of these fields is `null` when it could not be read; the reason is in `warnings`.

**`battle.history`.** The local player's card plays this combat, counted the way the game's own cards count
them (Finisher, Stomp, Pinpoint): a play counts once it has finished resolving, and an auto-play or a
Replay repeat is a play too. `*_this_turn` covers the current turn only, so it is 0 during the enemy
turn. `null` when the history could not be read.

**Dead enemies.** `enemies[]` normally holds only living creatures, because the game drops a
killed enemy from the fight. A few powers refuse that — `ReattachPower` on a Decimillipede
segment is the one that matters — and their owner stays in the fight as a corpse. Those
corpses are listed with `alive: false` and `hp: 0`, keeping the `entity_id` they already had,
so a caller that never meets one sees no change. Filter on `alive` (not on `hp > 0`) to get
the targetable enemies; `play_card` and `use_potion` already refuse a dead target.

`revive` is present only when the corpse is actually scheduled to come back: `in_turns` is
read off the monster's move chain (2 the turn it dies, 1 the turn before it stands up, and
its `intents` then show `Heal`), and `hp` is the amount it heals for. A corpse held in the
fight by something that never revives it (`DieForYouPower`, `IllusionPower`) has `alive: false`
and no `revive`, and neither does the last segment of a Decimillipede — with no living peer
left to reattach to, the fight is over rather than about to restart.

### `hand_select` — In-Combat Card Selection

Appears when a card effect prompts "Select a card to exhaust/discard/upgrade". **Not** an overlay — happens within the combat hand.
It always happens in a fight, so it always carries `in_combat: true` and the fight's `battle` block (see
[In-combat selection screens](#in-combat-selection-screens)).

```jsonc
{
  "state_type": "hand_select",
  "hand_select": {
    "mode": "simple_select",     // "simple_select" or "upgrade_select"
    "prompt": "Select a card to Exhaust.",
    "cards": [
      {
        "index": 0,
        "uid": "STRIKE_IRONCLAD#1", // The card's uid, as in player.hand
        "id": "STRIKE_IRONCLAD",
        "name": "Strike",
        "type": "Attack",
        "cost": "1",
        "star_cost": null,       // Regent star cost as string, null if N/A
        "description": "Deal 6 damage.",
        "is_upgraded": false,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "selected_cards": [          // Only present if cards have been selected
      { "index": 0, "name": "Defend" }
    ],
    "can_confirm": false,
    "selection": {               // See "The selection object"
      "kind": "HAND",
      "pile": null,
      "prompt_key": "card_selection.TO_EXHAUST",
      "source": { "type": "card", "id": "BURNING_PACT", "uid": "BURNING_PACT#0" },
      "min": 1, "max": 1,
      "can_skip": false,
      "selected": [ { "uid": "DEFEND_IRONCLAD#3", "id": "DEFEND_IRONCLAD" } ]
    }
  },
  "in_combat": true,  // Always true here
  "battle": { ... },  // The fight's battle block, as the combat state carries it
  "run": { ... },
  "player": { ... }   // Includes hand, energy and piles, as in combat
}
```

### `rewards` — Rewards Screen

Appears after combat ends or when triggered by events (e.g. TheFutureOfPotions, Draft).

```jsonc
{
  "state_type": "rewards",
  "rewards": {
    "items": [
      {
        "index": 0,
        "type": "gold",              // gold, potion, relic, card, special_card, card_removal
        "description": "Obtain 25 gold.",
        "gold_amount": 25            // Only for gold rewards
      },
      {
        "index": 1,
        "type": "potion",
        "description": "Obtain a potion.",
        "potion_id": "SWIFT_POTION", // Only for potion rewards
        "potion_name": "Swift Potion"
      },
      {
        "index": 2,
        "type": "card",
        "description": "Add a card to your deck."
        // Card rewards open the card_reward screen when claimed
      }
    ],
    "can_proceed": true
  },
  "run": { ... },
  "player": { ... }
}
```

### `card_reward` — Card Reward Selection

Pick one card to add to your deck. Appears after claiming a card reward, or directly during events (e.g. Draft modifier at Neow).

```jsonc
{
  "state_type": "card_reward",
  "card_reward": {
    "cards": [
      {
        "index": 0,
        "id": "UPPERCUT",
        "name": "Uppercut",
        "type": "Attack",
        "cost": "2",
        "star_cost": null,
        "description": "Deal 13 damage. Apply 1 Weak. Apply 1 Vulnerable.",
        "rarity": "Uncommon",
        "is_upgraded": false,
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_skip": true
  },
  "run": { ... },
  "player": { ... }
}
```

### `map` — Map Navigation

```jsonc
{
  "state_type": "map",
  "map": {
    "current_position": {
      "col": 3, "row": 2, "type": "Monster"
    },
    "visited": [
      { "col": 3, "row": 0, "type": "Ancient" },
      { "col": 3, "row": 1, "type": "Monster" },
      { "col": 3, "row": 2, "type": "Monster" }
    ],
    "next_options": [
      {
        "index": 0,
        "col": 2, "row": 3,
        "type": "RestSite",
        "leads_to": [            // 1-level lookahead (children)
          { "col": 1, "row": 4, "type": "Elite" },
          { "col": 3, "row": 4, "type": "Shop" }
        ]
      }
    ],
    "nodes": [                   // Full map DAG
      {
        "col": 3, "row": 0,
        "type": "Ancient",     // the starting node, the act's Ancient (Monster in an act 1 begun without Neow)
        "children": [ [2, 1], [3, 1], [4, 1] ]  // [col, row] pairs
      }
    ],
    "boss": {
      "col": 3,
      "row": 15,
      "id": "ENCOUNTER.VANTOM_BOSS",
      "name": "Vantom"
    },
    "bosses": [
      { "col": 3, "row": 15, "id": "ENCOUNTER.VANTOM_BOSS", "name": "Vantom" }
    ],
    // What this act has ALREADY served. There is no "next encounter" field: the game does
    // not show that before the room is entered. Omitted entirely before the first room.
    "encounters_seen": {
      "act": 1,
      "monster": [
        { "floor": 2, "id": "NIBBITS_WEAK", "name": "Nibbits", "monsters": ["Nibbit", "Nibbit"] }
      ],
      "elite": [
        { "floor": 8, "id": "BYRDONIS_ELITE", "name": "Byrdonis", "monsters": ["Byrdonis"] }
      ],
      "event": [
        { "floor": 1, "id": "NEOW", "name": "Neow" }
      ]
      // "elite_cycle": 1   // only once the act's elite pool has wrapped at least once
    }
  },
  "run": { ... },
  "player": { ... }
}
```

The same block rides along on the decision screens outside combat (see [Common Top-Level Fields](#common-top-level-fields)).
There, `next_options` lists what the map screen will offer once this room is done, computed from the run the way
the game's map screen computes it when it opens: the current node's children (every node of the next row with free
travel, as Winged Boots give), the boss after the last row, the second boss after the first one in an act with two
bosses, the starting node before the act's first node. It is
ordered the same way, so an `index` there is the index `choose_map_node` takes once the map is open, but
`choose_map_node` itself only works on the map screen. On the map screen, `next_options` is read from the screen's
own nodes, as before.

### `event` — Event / Ancient Encounter

```jsonc
{
  "state_type": "event",
  "event": {
    "event_id": "NEOW",
    "event_name": "Neow",
    "is_ancient": true,
    "in_dialogue": false,        // true = click to advance dialogue first
    "body": "Event description text...",
    "options": [
      {
        "index": 0,
        "title": "Draft",
        "description": "Choose 10 card rewards to replace your starting deck.",
        "is_locked": false,
        "is_proceed": false,
        "was_chosen": false,
        "relic_name": "Relic Name",         // Only if option has a relic
        "relic_description": "Relic desc.",  // Only if option has a relic
        "keywords": [ /* Keyword Objects */ ]
      }
    ]
  },
  "run": { ... },
  "player": { ... }
}
```

### `rest_site` — Rest Site

```jsonc
{
  "state_type": "rest_site",
  "rest_site": {
    "options": [
      {
        "index": 0,
        "id": "HEAL",
        "name": "Rest",
        "description": "Heal 30% of max HP.",
        "is_enabled": true,
        "has_button": true   // false = the option exists in the run model but no
                             // button is drawn; choose_rest_option refuses it
      }
    ],
    "can_proceed": false
  },
  "run": { ... },
  "player": { ... }
}
```

### `shop` — Shop

Shop inventory is auto-opened when state is queried.

```jsonc
{
  "state_type": "shop",
  "shop": {
    "items": [
      // Card entry
      {
        "index": 0,
        "category": "card",
        "price": 75,               // Gold price in the shop
        "is_stocked": true,
        "can_afford": true,
        "on_sale": false,
        "card_id": "OFFERING",
        "card_name": "Offering",
        "card_type": "Skill",
        "card_cost": "1",          // Energy cost as string ("X" for X-cost)
        "card_star_cost": null,    // Regent star cost as string, null if N/A
        "card_rarity": "Rare",
        "card_description": "Lose 6 HP. Gain 2 Energy. Draw 3 cards.",
        "keywords": [ /* Keyword Objects */ ]
      },
      // Relic entry
      {
        "index": 5,
        "category": "relic",
        "price": 150,
        "is_stocked": true,
        "can_afford": false,
        "relic_id": "VAJRA",
        "relic_name": "Vajra",
        "relic_description": "At the start of each combat, gain 1 Strength.",
        "keywords": [ /* Keyword Objects */ ]
      },
      // Potion entry
      {
        "index": 8,
        "category": "potion",
        "price": 50,
        "is_stocked": true,
        "can_afford": true,
        "potion_id": "FIRE_POTION",
        "potion_name": "Fire Potion",
        "potion_description": "Deal 20 damage to target enemy.",
        "keywords": [ /* Keyword Objects */ ]
      },
      // Card removal entry
      {
        "index": 10,
        "category": "card_removal",
        "price": 75,
        "is_stocked": true,
        "can_afford": true
      }
    ],
    "can_proceed": true,
    "error": "..."             // Only present if inventory isn't ready; retry in a moment
  },
  "run": { ... },
  "player": { ... }
}
```

### `fake_merchant` — Fake Merchant Event

A relic-only shop disguised as an event. Uses `shop_purchase` and `proceed` actions (same as regular shop). If the player triggers a fight, the merchant disappears.

```jsonc
{
  "state_type": "fake_merchant",
  "fake_merchant": {
    "event_id": "FAKE_MERCHANT",
    "event_name": "Fake Merchant",
    "started_fight": false,         // true after triggering the foul potion fight
    "shop": {
      "items": [
        {
          "index": 0,
          "category": "relic",
          "cost": 150,
          "is_stocked": true,
          "can_afford": true,
          "relic_id": "VAJRA",
          "relic_name": "Vajra",
          "relic_description": "At the start of each combat, gain 1 Strength.",
          "keywords": [ /* Keyword Objects */ ]
        }
      ],
      "can_proceed": true
    },
    // After fight:
    // "started_fight": true,
    // "shop": { "items": [], "can_proceed": true },
    // "message": "The fake merchant has been defeated. Proceed to map."
  },
  "run": { ... },
  "player": { ... }
}
```

**Note:** The fake merchant only sells relics — no cards, potions, or card removal. The `shop_purchase` action works the same as for a regular shop.

### `treasure` — Treasure Room

Chest is auto-opened on first state query.

```jsonc
{
  "state_type": "treasure",
  "treasure": {
    "message": "Opening chest...",  // Transitional state; query again
    // OR, once opened:
    "relics": [
      {
        "index": 0,
        "id": "LANTERN",
        "name": "Lantern",
        "description": "Gain 1 Energy on the first turn of each combat.",
        "rarity": "Uncommon",
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_proceed": true
  },
  "run": { ... },
  "player": { ... }
}
```

### `card_select` — Card Selection Overlay

Covers deck transforms, upgrades, enchants, removals, and choose-a-card effects. Appears on top of any room, and
mid-fight when a card, potion, relic or power asks you to pick from a pile (Headbutt, Seeker Strike, Secret
Technique, Liquid Memories) or from freshly made cards (Attack / Skill / Power / Colorless Potion, Discovery,
Toolbox). A fight's `card_select` carries `in_combat: true` and the `battle` block; see
[In-combat selection screens](#in-combat-selection-screens).

```jsonc
{
  "state_type": "card_select",
  "card_select": {
    "screen_type": "transform",  // transform, upgrade, enchant, select, simple_select, choose
    "prompt": "Choose 2 cards to Transform.",
    "cards": [
      {
        "index": 0,
        "uid": "STRIKE_IRONCLAD#1", // The card's uid (in a fight, the same as in the piles)
        "id": "STRIKE_IRONCLAD",
        "name": "Strike",
        "type": "Attack",
        "cost": "1",
        "star_cost": null,       // Regent star cost as string, null if N/A
        "description": "Deal 6 damage.",
        "rarity": "Common",
        "is_upgraded": false,
        "selected": false,       // in the screen's own selection set (grid screens only)
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "selected_count": 0,         // how many cards the screen currently holds selected
    "required_count": 2,         // the N of a "choose N" prompt; null when a range is accepted
    "min_select": 2,             // the two ends of that range (equal when required_count is set)
    "max_select": 2,
    "preview_showing": false,    // true when the preview panel is up (step two, see below)
    "can_confirm": false,        // true when confirm button is available
    "can_cancel": true           // true when close/cancel button is available

    // "selected_outside_grid": only present when the grid is scrolled and a selected card
    // falls outside the window listed in "cards".

    // For "choose" type: picking is immediate (no confirm needed).
    // can_skip indicates if a skip button exists.

    "selection": { /* See "The selection object" */ }
  },
  "in_combat": false,  // true when a fight opened it; then "battle" follows, as in combat
  "run": { ... },
  "player": { ... }
}
```

**Grid screens have two steps.** Cards are picked first; then a preview panel opens — by itself once
`max_select` is reached, or when `confirm_selection` presses the screen's own confirm — and only the
preview's own confirm applies the selection. `selected` comes from the screen's selection set, not from the
grid highlight, so it stays `true` during the preview (the game drops the highlight there but keeps the card).

### In-combat selection screens

`hand_select`, a `card_select` grid and a choose-a-card `card_select` can open in the middle of a fight. There they
carry the fight with them, so a client can tell the decision is part of the combat and analyze it:

| Field | In a fight | Outside a fight |
|---|---|---|
| `in_combat` | `true` | `false` |
| `battle` | The same block the `monster` / `elite` / `boss` state carries (enemies, intents, history...), built under the same [hidden-information](#hidden-information) rules: `move_follow_up_id` only on a `?hidden=1` read with `allow_hidden_state` on | absent |
| `player` | As in combat: `energy`, `hand`, the piles (`draw_pile` sorted as the game shows it), orbs | as on any other screen |

"In a fight" means the current room is a combat room and its combat has not ended (the game's
`CombatManager.IsInProgress`). `hand_select` only exists in a fight, so it is always `in_combat: true`. `state_type`
stays `hand_select` / `card_select`, so a client that only reads the screen block is unaffected. In a fight they
carry no map block (outside one, `card_select` does, like the other decision screens). Markdown renders the battle
above the selection, as it always has for `hand_select`.

### The selection object

`hand_select` and `card_select` (every grid screen and choose-a-card, in a fight or not) carry `selection`: what the
screen asks for, in the game's own terms instead of its localized text, and what opened it. Its cards (`source`,
`selected`) carry the same `uid` as the screen's `cards` and, in a fight, the piles.

```jsonc
"selection": {
  "kind": "GRID",               // HAND (hand_select) | GRID (a card_select grid) | CHOOSE (choose-a-card)
  "pile": "DISCARD",            // a grid over a combat pile: DRAW | DISCARD | EXHAUST; otherwise null
  "prompt_key": "cards.HEADBUTT.selectionScreenPrompt",   // the prompt's localization table.key
  "source": { "type": "card", "id": "HEADBUTT", "uid": "HEADBUTT#0" },   // what opened the screen
  "min": 1, "max": 1,           // how many cards a confirmed pick holds
  "can_skip": false,            // the screen can end with no card
  "selected": [ { "uid": "STRIKE_IRONCLAD#2", "id": "STRIKE_IRONCLAD" } ]   // already picked
}
```

| Field | Value | `null` when |
|---|---|---|
| `kind` | The screen's shape: `HAND` picks from the hand, `GRID` from a grid of cards, `CHOOSE` one of up to three freshly made cards. It is not a game enum; the game has none. | never |
| `pile` | A grid over a combat pile (the game's `CardSelectCmd.FromCombatPile`: Headbutt, Seeker Strike, Secret Technique, Liquid Memories, Droplet of Precognition): `DRAW`, `DISCARD` or `EXHAUST`. | HAND and CHOOSE; a grid over a list of cards instead of a pile (the deck screens, generated cards) |
| `prompt_key` | The prompt's localization table and key, joined by a dot, the same in every language: `card_selection.TO_EXHAUST`, `cards.HEADBUTT.selectionScreenPrompt`, `gameplay_ui.CHOOSE_CARD_UPGRADE_HEADER` (Armaments). CHOOSE is always `gameplay_ui.CHOOSE_CARD_HEADER`, its banner. | the prompt cannot be read (with a warning) |
| `source.type` | `card`, `potion`, `relic` or `power`: what kind of model opened the screen. `unknown` for any other model (an orb, a monster) and when the game names none (below). | never |
| `source.id` | That model's id (`HEADBUTT`, `ATTACK_POTION`, `GAMBLING_CHIP`, `ENTROPY_POWER`); an `unknown` model the game does name keeps its id. | no model |
| `source.uid` | The source card's uid. | not a card |
| `min` / `max` | The selector's bounds, the game's `CardSelectorPrefs.MinSelect` / `MaxSelect`. A "choose any number" prompt (Gambling Chip, Gambler's Brew) has the game's own no-limit `max`, 999999999. CHOOSE: `max` 1, and `min` 0 when it can be skipped, 1 when it cannot. | the screen's prefs cannot be read (with a warning); for CHOOSE, `min` when its skip flag cannot be read |
| `can_skip` | The screen can end with no card chosen: a hand or grid whose `min` is 0 (its confirm button works with nothing picked), a deck screen that has a close button (the game's `Cancelable`: a shop removal, a smith), a choose-a-card screen with a skip button. So `min` is 0 exactly when `can_skip` is true, except on a closable deck screen. | as `min` |
| `selected` | The cards already picked, `{uid, id}` each. On the hand, in the order they were picked. On a grid, in the order of the screen's own selection set, which is the order the game acts on them in: the pick order, except that a card picked after a deselect takes the freed place. CHOOSE: always `[]`, as a pick completes the screen. | unreadable (with a warning) |

**The source.** The game never tells the screens what opened them. It hands a hand selection's source to
`NPlayerHand.SelectCards`, which does not keep it, and it names the model involved in any choice on the choice's
context (`PlayerChoiceContext.LastInvolvedModel`: the card being played, the potion being used, the relic, power or
card whose hook is running; the game shows other players that model while they wait). The mod records it as the
selection starts, with Harmony prefixes on `NPlayerHand.SelectCards` and on `CardSelectCmd.FromCombatPile`,
`FromSimpleGrid`, `FromSimpleGridForRewards` and `FromChooseACardScreen`, and forgets it when that selection
completes. It matches each record to the screen it opened (the screen's prompt and pile, or choose-a-card's card
list), so two selections queued at once, such as two relics at the start of a turn, each report their own.
`source.type` is `unknown` when:
- the game names no model: the deck screens (a smith, an event, a relic picked up), which run without a choice
  context, and the selections the game starts with an empty one (the Massive Scroll, Hefty Tablet and Lead
  Paperweight picks, an event's card grid, the Knowledge Demon's choice);
- the model is not a card, potion, relic or power (`source.id` names it);
- the patches could not bind to the game (after a game update; the game log says which, and every screen then
  reads `unknown`).

**Nothing hidden.** `selection` reveals nothing the screen does not show. The prompt, the bounds and whether the
screen can be skipped are on it, the picks are highlighted there, and the source card was just played in view (or
the potion used, the relic or power triggered on screen). A grid over the draw pile (Seeker Strike, Secret
Technique) lists its `cards` in the order the screen shows them: the game sorts that grid by rarity and then name,
not in draw order, and the mod reads the order off the screen, adding nothing to it. Their uids do not encode the
draw order either (see "Card uids").

### `bundle_select` — Card Bundle Selection Overlay

Choose between bundles of cards (e.g. from Scroll Boxes relic).

```jsonc
{
  "state_type": "bundle_select",
  "bundle_select": {
    "screen_type": "bundle",
    "prompt": "Choose a bundle.",
    "bundles": [
      {
        "index": 0,
        "card_count": 3,
        "cards": [
          {
            "index": 0,
            "id": "UPPERCUT",
            "name": "Uppercut",
            "type": "Attack",
            "cost": "2",
            "star_cost": null,   // Regent star cost as string, null if N/A
            "description": "Deal 13 damage. Apply 1 Weak. Apply 1 Vulnerable.",
            "rarity": "Uncommon",
            "is_upgraded": false,
            "keywords": [ /* Keyword Objects */ ]
          }
        ]
      }
    ],
    "preview_showing": false,
    "preview_cards": [ /* Card Objects — shown when a bundle is selected */ ],
    "can_cancel": false,
    "can_confirm": false
  },
  "run": { ... },
  "player": { ... }
}
```

### `relic_select` — Relic Choice Overlay

Boss relic selection. Pick is immediate.

```jsonc
{
  "state_type": "relic_select",
  "relic_select": {
    "prompt": "Choose a relic.",
    "relics": [
      {
        "index": 0,
        "id": "BLACK_STAR",
        "name": "Black Star",
        "description": "Elites drop an additional relic.",
        "rarity": "Rare",
        "keywords": [ /* Keyword Objects */ ]
      }
    ],
    "can_skip": true
  },
  "run": { ... },
  "player": { ... }
}
```

### `crystal_sphere` — Crystal Sphere Minigame

```jsonc
{
  "state_type": "crystal_sphere",
  "crystal_sphere": {
    "instructions_title": "Crystal Sphere",
    "instructions_description": "Use divination tools to reveal cells...",
    "grid_width": 9,
    "grid_height": 11,
    "cells": [
      {
        "x": 0, "y": 0,
        "is_hidden": true,       // true = unrevealed
        "is_clickable": true,    // true = can be clicked to reveal
        "is_highlighted": false,
        "is_hovered": false,
        "item_type": "CrystalSphereGold",  // Only on revealed cells
        "is_good": true                     // Only on revealed cells
      }
    ],
    "clickable_cells": [
      { "x": 4, "y": 7 }       // Convenience list of clickable cells
    ],
    "revealed_items": [
      {
        "item_type": "CrystalSphereGold",
        "x": 2, "y": 3,
        "width": 1, "height": 1,
        "is_good": true
      }
    ],
    "tool": "big",               // "big", "small", or "none"
    "can_use_big_tool": true,
    "can_use_small_tool": true,
    "divinations_left_text": "3 divinations remaining",
    "can_proceed": false
  },
  "run": { ... },
  "player": { ... }
}
```

### `game_over`

Run has ended.

```jsonc
{
  "state_type": "game_over",
  "game_over": {
    "message": "Run ended.",
    "options": ["main_menu"]
  },
  "run": { ... },
  "player": { ... }
}
```

Use `menu_select` with `main_menu` to return to the main menu. `continue` is not advertised because it is not an actionable game-over option.

### `overlay` — Unhandled Overlay (catch-all)

Prevents soft-locks when an unrecognized overlay is active.

```jsonc
{
  "state_type": "overlay",
  "overlay": {
    "screen_type": "NSomeUnknownScreen",
    "message": "An overlay (NSomeUnknownScreen) is active. It may require manual interaction in-game."
  },
  "run": { ... },
  "player": { ... }
}
```

---

## Profiles

Profile endpoints are independent of the singleplayer and multiplayer run endpoints.

### `GET /api/v1/profile`

Returns the active profile's persistent progress summary, including character stats, card stats, encounter stats, discovered content, achievements, epochs, and global totals.

### `GET /api/v1/compendium`

Returns the active profile's progress grouped by the in-game Compendium cards:

- `current_run`: present while a run is active. Includes a derived `run_id` in `{save_scope}:profile{profile_id}:{start_time}` format, plus `start_time`, `save_time`, `run_time`, and run metadata read from `current_run.save`. Its `seed` is [hidden information](#hidden-information): it drives every RNG stream of the run in progress, so it is reported only when `allow_hidden_state` is `true` and the request has `?hidden=1`.
- `card_library`: discovered card IDs and card pick/skip/win/loss stats. This endpoint intentionally reports profile-level progress; card rules text remains available through game state when cards are visible in a run.
- `relic_collection`: discovered relic IDs. The profile save exposes discovery, but not a typed per-relic description or obtained-count catalog.
- `potion_lab`: discovered potion IDs. The profile save exposes discovery, but not a typed per-potion rules text or lab metadata catalog.
- `bestiary`: profile encounter/enemy fight stats. The in-game Bestiary card is currently marked future/locked, so this section is stats-only and does not expose a full enemy metadata catalog.
- `character_stats`: per-character and global profile totals.
- `run_history`: summaries of the active profile's saved `saves/history/*.run` files. If more than 20 files exist, the response includes the 20 most recent entries and the local `history_path`.

The response is built from `SaveManager.Progress`, the active profile's `progress.save` path, and local run-history files under the same active Steam account. It does not scan every Steam account under the save root. Run-history parsing is bounded to the 20 most recent `.run` files in the response so profile pages with many saved runs do not return unbounded payloads.

`current_run.run_id` is derived from save scope, profile id, and run `start_time`. Use it to identify one concrete run attempt. To group runs by generated content, use the `seed` of finished runs in `run_history` (not gated: a finished run's seed reveals nothing about the one in progress), or `current_run.seed` on an opted-in `?hidden=1` read; multiple attempts can share the same seed.

```jsonc
{
  "profile_id": 1,
  "current_run": {
    "is_in_progress": true,
    "profile_id": 1,
    "save_scope": "modded",
    "run_id": "modded:profile1:1778295706",
    "start_time": 1778295706,
    "seed": "2450ZAR9EF",   // only with allow_hidden_state and ?hidden=1
    "run_time": 137
  },
  "sections": {
    "card_library": { "status": "exposed", "discovered_ids": ["BASH"], "stats": [] },
    "relic_collection": { "status": "partially_exposed", "discovered_ids": ["BURNING_BLOOD"] },
    "potion_lab": { "status": "partially_exposed", "discovered_ids": [] },
    "bestiary": { "status": "locked_in_ui", "encounter_stats": [], "enemy_stats": [] },
    "character_stats": { "status": "exposed", "characters": [], "global": {} },
    "run_history": {
      "status": "exposed",
      "entry_count": 10,
      "entries": [
        {
          "id": "1774869148",
          "run_id": "modded:profile1:1774869148",
          "players": [{ "id": 1, "character": "CHARACTER.IRONCLAD" }],
          "ascension": 0,
          "win": false,
          "run_time": 6541
        }
      ]
    }
  }
}
```

### `GET /api/v1/wiki`

Searches wiki-style card and relic entries available to the active profile. The endpoint requires a query, filters to the profile's discovered card and relic IDs, fuzzy-ranks the matches, and returns a bounded result set. It does not expose the full game catalog.

Query parameters:

| Parameter | Values | Default | Description |
|---|---|---|---|
| `query` or `q` | text | required | Fuzzy search text, such as `ironclad perfect strike` or `silver spoon`. |
| `item_type` or `type` | `all`, `card`, `relic` | `all` | Restricts the search to one wiki kind. |
| `limit` | integer | `10` | Maximum returned entries. The mod clamps values below 1 and above its internal maximum. |

Card results include both `base` and `upgraded` variants when the card can be upgraded. The upgraded variant is built from a cloned preview, so the catalog model is not mutated.

```http
GET /api/v1/wiki?query=ironclad%20perfect%20strike&item_type=card&limit=3
```

```jsonc
{
  "status": "ok",
  "profile_id": 1,
  "query": "ironclad perfect strike",
  "item_type": "card",
  "limit": 3,
  "scope": "active_profile_discovered_cards_and_relics",
  "selection_policy": "Searches only cards and relics discovered by the active profile, then returns the best fuzzy matches instead of exposing the full catalog.",
  "counts": {
    "discovered_cards": 42,
    "discovered_relics": 18,
    "searched": 42,
    "returned": 1
  },
  "results": [
    {
      "item_type": "card",
      "id": "PERFECTED_STRIKE",
      "name": "Perfected Strike",
      "score": 775.123,
      "rarity": "Common",
      "type": "Attack",
      "is_upgradable": true,
      "base": {
        "variant": "base",
        "cost": "2",
        "description": "Deal ... damage.",
        "is_upgraded": false,
        "keywords": []
      },
      "upgraded": {
        "variant": "upgraded",
        "cost": "2",
        "description": "Deal ... damage.",
        "is_upgraded": true,
        "keywords": []
      }
    }
  ]
}
```

Relic searches use the same profile filter and ranking:

```http
GET /api/v1/wiki?query=silver%20spoon&item_type=relic
```

Relic results include `id`, `name`, `rarity`, `description`, and `keywords`.

### `GET /api/v1/profiles`

Lists the three profile slots and identifies the active slot.

```json
{
  "current_profile_id": 1,
  "profiles": [
    { "id": 1, "is_current": true, "has_data": true },
    { "id": 2, "is_current": false, "has_data": false },
    { "id": 3, "is_current": false, "has_data": true }
  ]
}
```

### `POST /api/v1/profiles`

Switch to a profile slot through the game's profile UI:

```json
{ "action": "switch", "profile_id": 2 }
```

Delete an inactive profile slot:

```json
{ "action": "delete", "profile_id": 2 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `action` | string | Yes | `switch` or `delete` |
| `profile_id` | int | Yes | Profile slot, from 1 to 3 |

Switching is rejected during a run. Deleting the active profile is rejected; switch away first if you need to remove a slot.

---

## Action log

`GET /api/v1/actions?since=<seq>` returns the player's moves: every card play, potion use and end turn, each
tagged with who made it. A coach uses it to know exactly which move was made, so it can grade that move.

```jsonc
{
  "status": "ok",
  "latest_seq": 57,                    // seq of the newest entry (0 before the first); = action_log_seq in the state
  "entries": [                         // every entry with seq > since, oldest first
    {
      "seq": 55,
      "kind": "play_card",             // "play_card" | "use_potion" | "end_turn"
      "source": "human",               // "human": a click in the game; "api": this mod, for a client's request
      "utc": "2026-09-24T18:02:11.482Z",
      "act": 1,                        // run.act, run.floor and battle.round when the move was made
      "floor": 3,
      "round": 2,                      // null outside combat
      "card_id": "STRIKE_IRONCLAD",
      "card_uid": "STRIKE_IRONCLAD#1", // the card's uid, as in the state's hand; null if it could not be found
      "target": "NIBBIT_0",            // the target's entity_id; null for an untargeted card
      "target_combat_id": 12           // the target's combat_id; null for an untargeted card
    },
    {
      "seq": 56, "kind": "use_potion", "source": "api",
      "utc": "2026-09-24T18:02:13.090Z", "act": 1, "floor": 3, "round": 2,
      "slot": 0,                       // belt slot, as in player.potions[].slot
      "target": null,                  // entity_id of a monster target; null for no target or yourself
      "target_combat_id": 7            // the target's combat_id (your own creature for a potion used on
                                       // yourself in combat); null for no target
    },
    { "seq": 57, "kind": "end_turn", "source": "human", "utc": "2026-09-24T18:02:15.771Z", "act": 1, "floor": 3, "round": 2 }
  ]
}
```

| Parameter | Meaning |
|---|---|
| `since` | Return the entries with a `seq` greater than this. Default `0`: every entry still held. It must be a whole number of 0 or more; anything else is HTTP 400. |

- **Fields by kind.** Every entry has `seq`, `kind`, `source`, `utc` (UTC, millisecond precision), `act`, `floor`
  and `round`. `play_card` adds `card_id`, `card_uid`, `target` and `target_combat_id`; `use_potion` adds `slot`,
  `target` and `target_combat_id`; `end_turn` adds nothing. A kind's keys are always present; a value that does
  not apply, or could not be read, is `null`.
- **Polling.** Keep the highest `seq` you have seen and pass it as `since`. Every state carries
  `action_log_seq`, the newest seq, so a client that polls the state can tell when there is something new.
  Seqs are consecutive. They start again at 1 each time the game starts: the log is kept in memory, not saved.
- **Capacity.** The log keeps the newest 2000 entries. If the first entry you get has a `seq` above `since + 1`,
  the entries in between were dropped.
- **What counts as a move.** An entry is written when the game enqueues the request, not when the move resolves.
  That is normally the moment you click (or the API acts). The exception is a card or potion used while the game is
  holding play-phase moves (during the enemy turn and the start of your turn): the game parks it and enqueues it when
  your play phase opens, and it is logged then, with the `utc`, `act`, `floor` and `round` of that moment. The game
  can still cancel a queued request, for example a card queued behind the play that ends the combat; the log does not
  revise the entry, and the next state shows what happened. Only the End Turn button and the API's `end_turn`
  produce `end_turn` entries: a turn the game ends by itself (Void Form, a player dying on their own turn) is not a
  move. Cards the game plays by itself (auto-plays) are not logged either. Potions are logged in and out of combat;
  selling a potion to the merchant is a `use_potion`.
- **Who made it.** `api` is exactly the move the mod itself enqueued for a `POST /api/v1/singleplayer` request: the
  card play `play_card` built, the potion use `use_potion` or `sell_potion` started, and the API's `end_turn`. It is
  tagged at that enqueue call, so a move the game releases or queues while the mod is acting stays `human`, and so
  does everything else. `play_card` and an in-combat `use_potion` refuse while the game would hold the move for the
  play phase (see [`play_card`](#play_card)), so an API move is never parked and released later. No `menu_select`
  produces a move: on the tutorial popup over the End Turn button, `advance` only dismisses the popup (see
  [`menu_select`](#menu_select)).
- **Singleplayer only**, and only the local player's moves. Nothing is logged during a multiplayer run.
- Like every endpoint it needs the API token. It is answered from the log itself rather than on the game's main
  thread, so it never returns 503, even while the game is loading. `?pretty=1` works as elsewhere.

---

## POST — Perform Actions

All POST requests use a JSON body with an `"action"` field and action-specific parameters.

### Success Response

```jsonc
{ "status": "ok", "message": "Playing 'Strike' targeting Jaw Worm" }
```

### Error Response

Action failures — the action was understood but could not be performed — are returned with
**HTTP 200**:

```jsonc
{ "status": "error", "error": "Card requires a target. Provide 'target' with an entity_id." }
```

Transport failures — a browser `Origin` (403), a missing or wrong token (401), a body that is not
`application/json` (415), a disabled debug action (403), invalid JSON, a missing `action` field, an unknown
route, a wrong HTTP method, the wrong run mode for the endpoint, or an unhandled exception — use an
**HTTP 4xx/5xx** code but the same body shape:

```jsonc
// HTTP 400
{ "status": "error", "error": "Missing 'action' field" }
```

So every POST response body carries `status` (`"ok"` or `"error"`), and `error` is present if and
only if `status` is `"error"`. Branching on `status` alone is enough; the HTTP code only distinguishes
*where* the failure happened.

### Unavailable Response (HTTP 503)

```jsonc
{ "error": "timed out after 10000 ms waiting for the game's main thread", "status": "error", "retry": true }
```

Every request — GET included — is executed on the game's main thread, which is pumped once per
rendered frame. The exceptions are `GET /` (the health check) and `GET /api/v1/actions` (the
[action log](#action-log)): they are answered without the main thread and never return 503.
A `503` with `"retry": true` means the request **was not executed**: the main thread
did not answer within 10 s, or more than 32 requests are already queued, which only happens when the
game is not processing frames (scene load, hang, minimized or paused app). Nothing was applied to the
run, so the same request may be sent again once the game is responsive. Contrast with `500`, where
the action ran and threw.

### Debug and staging actions

The `debug_*` actions rewrite the run instead of playing it: `debug_start_encounter`, `debug_start_event`,
`debug_start_shop`, `debug_force_play_phase`, `debug_add_card`, `debug_set_energy`, `debug_set_stars`,
`debug_add_power`, `debug_enchant_card`, `debug_enter_next_act`, `debug_gain_max_hp` and `debug_upgrade_deck`
(the fork's README describes them). Unless `STS2_MCP.conf` sets `allow_debug_actions` to `true`, every one of
them is refused before it reaches the game:

```jsonc
// HTTP 403
{ "status": "error", "error": "debug actions disabled" }
```

`return_to_main_menu` is not a debug action — it is the pause menu's own Save and Quit — and is always allowed.

### Waiting for the Game to Settle

A POST returns as soon as the action has been dispatched on the main thread. Whether the animation,
card resolution and redraw it started have finished is *not* in the response, so a client that acts
on the reply is reading pre-resolution numbers — enemy HP before a multi-hit landed, a hand that has
not been redrawn. The usual workaround is a GET poll loop on `is_resolving`; `"wait": true` moves
that loop into the server, so one round trip both performs the action and returns the settled state.

```json
{ "action": "play_card", "card_uid": "STRIKE_IRONCLAD#1", "target": "NIBBIT_0", "wait": true }
```

| Parameter | Type | Default | Meaning |
|---|---|---|---|
| `wait` | bool | `false` | Wait for the game to settle before answering. Truthy forms: `true`, `1`, `"1"`, `"true"`, `"yes"`, `"on"`. Also readable from the query string (`?wait=1`, or a bare `?wait`). |
| `wait_timeout_ms` | int | `10000` | Upper bound on the wait, clamped to `500`–`30000`. |
| `include_state` | bool | `true` when `wait` is set | Whether to embed the settled state. |

Response, with the wait's own fields appended after the action's:

```jsonc
{
  "status": "ok",
  "message": "Playing 'Strike' targeting Nibbit",
  "waited_ms": 431,
  "polls": 6,
  "settled": true,
  "changed": true,
  "state": { "state_type": "monster", "...": "..." }
}
```

| Field | Meaning |
|---|---|
| `waited_ms` | Wall-clock duration of the wait. |
| `polls` | Number of state reads performed. |
| `settled` | `true` if the game came to rest; `false` means the timeout was reached and the state may still be mid-resolution. |
| `changed` | Whether anything observable moved compared with the state read just before the action. `false` means the action was a no-op. |
| `state` | The final state, identical in shape to `GET /api/v1/singleplayer`. Omitted when `include_state` is `false`, and when the wait could not read a state at all. |
| `wait_error` | Present only when the wait itself failed (the main thread stopped answering, or the state could not be built). The action already ran, so this is not a `503`. |

**The settle rule.** Every ~60 ms the server re-reads the state on the main thread and calls it
settled when `is_resolving` is false and, for `monster` / `elite` / `boss`, `battle.is_play_phase`
is true. It must look settled for **3 consecutive polls** (~180 ms) before the wait returns: a click
only takes effect on the following frame, and between two queued actions the game looks idle for a
single frame — one poll would routinely return a mid-resolution state. This mirrors the
empirically tuned loop every operator client ends up writing.

**The no-change rule.** A cheap fingerprint (state type, menu screen, floor, round, player HP /
block / energy, the uids in hand, each enemy's HP and block, the number of options on screen) is
taken just before the action and compared on every poll. If it has not moved within the first
**3000 ms**, the wait stops and returns `settled: true, changed: false` rather than burning the
whole timeout on an action the game ignored.

The wait never runs when the action returned `"status": "error"`, and a state that cannot be built
(it throws while a scene is swapping) counts as "not settled" and is simply polled again.

This is a **singleplayer-only** feature: `POST /api/v1/multiplayer` ignores `wait`,
`wait_timeout_ms` and `include_state`.

---

### `menu_select`

Select an option from the main menu, a menu submenu, profile select, character select, tutorial prompt, blocking popup, timeline screen, or game-over screen.

```json
{ "action": "menu_select", "option": "singleplayer" }
```

```json
{ "action": "menu_select", "option": "main_menu" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `option` | string | Yes | One of the current state's advertised menu options. Matching is case-insensitive. |
| `seed` | string | No | Only supported in menu contexts that expose a real seeded flow. Standard singleplayer character select currently returns an error without starting a run when `seed` is supplied. |

`game_over` advertises only `main_menu`. `continue` is not actionable on that screen and returns an error.
On a tutorial popup (`menu_screen: "tutorial"`), `advance` / `proceed` press the popup's own button. Two tutorials
point at a control underneath them and lay a stand-in over it: the "cannot play this card" tutorial over the End
Turn button and the merchant tutorial over the merchant. Clicking the stand-in in the game also ends the turn or
opens the shop. The API never presses it: while only the stand-in can be pressed, `advance` is advertised with
`enabled: false` and returns a retryable error, so dismissing a tutorial never ends your turn or opens the shop.
If `timeline` is blocked by pending obtained epochs, `menu_select` returns an error with `manual_action_required: true` and `pending_epoch_ids` instead of opening Timeline.
On `character_select`, a character pick is refused with a retryable error while `selection_busy` is `true` (the unlock
animation would overwrite it); after a pick, check `selected_character` before sending `confirm`.

### `set_time_scale`

Change how fast the game runs: the engine's time scale (Godot `Engine.TimeScale`), which speeds up or slows down
every animation and timed wait, and with them how quickly actions resolve. It works with or without a run, and
while the pause menu is open.

```json
{ "action": "set_time_scale", "scale": 3 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `scale` | number | Yes | `1` is normal speed. Clamped to `0.5`–`10`. A numeric string such as `"3"` is accepted. |

```jsonc
{ "status": "ok", "message": "Time scale set to 3x", "time_scale": 3 }   // time_scale: the value applied, after clamping
```

The setting lasts until it is changed again or the game restarts, and every state reports it as `time_scale`.
`wait` is ignored for this action.

**Hit-stop.** Some heavy hits (moves of Vantom, Mecha Knight and Ceremonial Beast) trigger the game's hit-stop:
it drops the engine's time scale to 0.1, ramps it back up over a fraction of a second, and ends by setting it to
exactly 1. The mod remembers the scale you set and puts it back as soon as the hit-stop has ended (it checks every
frame: scale back at 1, a scale other than 1 set through this action, no hit-stop running). A hit-stop that starts
while another still runs does not stop the first one, which runs on to its own end, so the mod waits until every
running hit-stop has ended. So the speed survives
those fights, and a state read during a hit-stop reports the lower `time_scale` of that moment. The mod only ever
changes a scale that reads exactly 1; any other value, whoever set it, is left alone.

**Errors:** missing, non-numeric or non-finite `scale`.

---

### `play_card`

Play a card from hand during combat.

```json
{ "action": "play_card", "card_index": 0, "target": "JAW_WORM_0" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `card_index` | int | Yes | 0-based index in hand |
| `target` | string | For `AnyEnemy` cards | `entity_id` of the target enemy |

**Errors:** Not in combat, not play phase, card unplayable, invalid index, missing target. Also "The play phase has
not opened yet" for a moment at the start of each turn: the player's phase already reads Play (`is_play_phase` is
true) but the game has not yet opened the play phase, and would hold the card until it does. Retry; nothing was
queued. The mod refuses rather than letting the game hold the card, so the play is queued inside the request and
the [action log](#action-log) tags it `api`. Singleplayer only: `POST /api/v1/multiplayer` does not refuse, and the
game holds the card until the play phase opens, as it always has.

**Warning:** Playing a card removes it from hand — all higher indices shift left. Play from highest index first, or re-query state between plays.

### `use_potion`

Use a potion from the potion belt.

```json
{ "action": "use_potion", "slot": 0, "target": "JAW_WORM_0" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `slot` | int | Yes | Potion slot index |
| `target` | string | For `AnyEnemy` potions | `entity_id` of the target enemy |

**Errors:** Empty slot, combat-only potion used outside combat, automatic potion, already queued, player dead. In
combat, also "The play phase has not opened yet" (the same retryable refusal as `play_card`, singleplayer only):
during the turn start (the draw), and for a moment after `is_play_phase` turns true, the game would hold the
potion until the play phase opens.

### `discard_potion`

Discard a potion to free up the slot. Use when potion slots are full and you need room for incoming potions (rewards, Cauldron, etc.).

```json
{ "action": "discard_potion", "slot": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `slot` | int | Yes | Potion slot index |

**Errors:** Empty slot, out-of-range slot.

### `end_turn`

End the player's combat turn.

```json
{ "action": "end_turn" }
```

**Errors:** Not in combat, not play phase, card mid-play, hand in selection mode.

### `combat_select_card`

Select a card during in-combat hand selection (exhaust, discard, upgrade prompts).

```json
{ "action": "combat_select_card", "card_index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `card_index` | int | Yes | Index in the selectable hand cards |

### `combat_confirm_selection`

Confirm the in-combat hand card selection.

```json
{ "action": "combat_confirm_selection" }
```

**Errors:** No selection active, confirm button not enabled (select more cards first).

### `claim_reward`

Claim a reward from the rewards screen.

```json
{ "action": "claim_reward", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based reward index |

Gold, potion, and relic rewards are claimed immediately. Card rewards open the `card_reward` screen.

**Note:** Claiming a reward may change indices of remaining rewards.

### `select_card_reward`

Pick a card from the card reward selection screen.

```json
{ "action": "select_card_reward", "card_index": 1 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `card_index` | int | Yes | 0-based card index |

### `skip_card_reward`

Skip the card reward (if a skip/bowl option is available).

```json
{ "action": "skip_card_reward" }
```

### `proceed`

Leave the current screen and open the map. Works from: rewards, rest site, shop, treasure room.

```json
{ "action": "proceed" }
```

**Note:** Does NOT work for events — use `choose_event_option` with the Proceed option's index instead.

### `choose_event_option`

Choose an event option.

```json
{ "action": "choose_event_option", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based index matching the option's `index` from state (locked options return an error) |

Works for both regular events and Ancients (after dialogue ends).

### `advance_dialogue`

Click through Ancient dialogue.

```json
{ "action": "advance_dialogue" }
```

Call repeatedly until `in_dialogue` becomes `false` and event options appear.

### `choose_rest_option`

Choose a rest site option (rest, smith, etc.).

```json
{ "action": "choose_rest_option", "option_id": "SMITH" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `option_id` | string | One of the two | Matches the option's `id` from state, case-insensitively (`HEAL`, `SMITH`, `DIG`, …). Aliases: `id`, `option`. **Preferred** |
| `index` | int | One of the two | 0-based index matching the option's `index` from state. Alias: `option_index` |

The option is resolved in `RestSiteRoom.Options` (the same list the state numbers) and the
button is then looked up from that option, so the on-screen order of the buttons cannot make
this press a different option than the one asked for. Disabled options, unknown ids, and an
`option_id` that disagrees with a supplied `index` all return an error listing every option as
`[index] id (enabled/disabled)`.

Success adds the `option_id` and `index` of what was clicked:

```json
{ "status": "ok", "message": "Selecting rest site option: Smith", "option_id": "SMITH", "index": 1 }
```

### `shop_purchase`

Purchase a shop item.

```json
{ "action": "shop_purchase", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based index in the flat items list |

**Errors:** Not in shop, item sold out, not enough gold, inventory not ready.

### `choose_map_node`

Travel to a map node.

```json
{ "action": "choose_map_node", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based index from `next_options` |

### `select_card`

Select a card in a card selection overlay.

```json
{ "action": "select_card", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based card index in the grid |

**Behavior varies by screen type:**
- Grid screens (transform, upgrade, enchant, select): toggles selection through the game's own
  `OnCardClicked`, so a card that is already selected comes back out. Once `max_select` cards are selected,
  the screen opens its preview by itself.
- Choose-a-card screens (potions, effects): picks the card immediately.

On grid screens the result carries the outcome read back from the screen: `selected` (true when the card is
now in the selection), `selected_count`, `min_select` / `max_select`, and `preview_showing` when the press
opened the preview.

Returns `status: error` when the press would not land: while a preview is open (use `confirm_selection` or
`cancel_selection` — pressing through it corrupts the preview row), and when the screen refuses the press
because its selection is already full.

### `confirm_selection`

Confirm card selection (grid screens only).

```json
{ "action": "confirm_selection" }
```

With a preview open, presses the preview's own confirm, which applies the selection. Otherwise presses the
screen's confirm, which on most of these screens **opens the preview** rather than applying anything — the
message says which of the two happened, so a two-step screen needs two calls. Not needed for choose-a-card
screens.

Returns `status: error` rather than an `ok` that does nothing when fewer than `min_select` or more than
`max_select` cards are selected, when no confirm button is enabled, or when the press was swallowed (the
screen is still open and no preview appeared).

### `cancel_selection`

Cancel or close the card selection overlay.

```json
{ "action": "cancel_selection" }
```

**Behavior:**
- If a preview is showing: cancels back to the selection grid. This also **clears the selection** — the game's
  own cancel empties the set, so the grid comes back with nothing selected.
- For choose-a-card screens: clicks the skip button (if available).
- Otherwise: closes the selection screen (if cancellation is allowed).

### `select_bundle`

Open a bundle preview in the bundle selection screen.

```json
{ "action": "select_bundle", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based bundle index |

### `confirm_bundle_selection`

Confirm the currently previewed bundle.

```json
{ "action": "confirm_bundle_selection" }
```

### `cancel_bundle_selection`

Cancel the bundle preview.

```json
{ "action": "cancel_bundle_selection" }
```

### `select_relic`

Pick a relic from the relic choice screen. Selection is immediate.

```json
{ "action": "select_relic", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based relic index |

### `skip_relic_selection`

Skip the relic choice.

```json
{ "action": "skip_relic_selection" }
```

### `claim_treasure_relic`

Claim a relic from an opened treasure chest.

```json
{ "action": "claim_treasure_relic", "index": 0 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `index` | int | Yes | 0-based relic index |

### `crystal_sphere_set_tool`

Switch divination tool in the Crystal Sphere minigame.

```json
{ "action": "crystal_sphere_set_tool", "tool": "big" }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `tool` | string | Yes | `"big"` or `"small"` |

### `crystal_sphere_click_cell`

Reveal a cell in the Crystal Sphere.

```json
{ "action": "crystal_sphere_click_cell", "x": 4, "y": 7 }
```

| Parameter | Type | Required | Description |
|---|---|---|---|
| `x` | int | Yes | Cell x-coordinate |
| `y` | int | Yes | Cell y-coordinate |

### `crystal_sphere_proceed`

Finish the Crystal Sphere minigame.

```json
{ "action": "crystal_sphere_proceed" }
```

---

## Multiplayer Additions

### GET — Additional Top-Level Fields

```jsonc
{
  "game_mode": "multiplayer",
  "net_type": "SteamMultiplayer",
  "player_count": 2,
  "local_player_slot": 0,      // Index of local player in players array
  "players": [                  // Summary of all players
    {
      "character": "The Ironclad",
      "character_id": "IRONCLAD",   // As in the Player Object: the same in every language
      "hp": 72, "max_hp": 80,
      "gold": 99,
      "is_alive": true,
      "is_local": true
    }
  ]
}
```

### Battle State Additions

```jsonc
{
  "battle": {
    "all_players_ready": false
    // Same shape as singleplayer (round, turn, is_play_phase, enemies).
    // Player state lives in top-level "player" (local) and "players" (all).
  },
  "players": [
    {
      "character": "The Ironclad",
      "character_id": "IRONCLAD",
      "hp": 72, "max_hp": 80,
      "gold": 99,
      "is_alive": true,
      "is_local": true,
      "is_ready_to_end_turn": false   // Only present during combat
    },
    {
      "character": "The Necrobinder",
      "character_id": "NECROBINDER",
      "hp": 60, "max_hp": 66,
      "gold": 80,
      "is_alive": true,
      "is_local": false,
      "is_ready_to_end_turn": false,
      "pets": [                       // Omitted for local player (pets are under top-level "player")
        {
          "id": "OSTY",
          "name": "Otsy",
          "alive": true,
          "hp": 12, "max_hp": 12,
          "block": 0,
          "status": [ /* Power Objects */ ]
        }
      ]
    }
  ]
}
```

### Map State Additions

```jsonc
{
  "map": {
    "votes": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "voted": true,
        "vote_col": 2, "vote_row": 3
      }
    ],
    "all_voted": false
  }
}
```

### Event State Additions

```jsonc
{
  "event": {
    "is_shared": true,
    "votes": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "voted": true,
        "vote_option": 0
      }
    ],
    "all_voted": false
  }
}
```

### Treasure State Additions

```jsonc
{
  "treasure": {
    "is_bidding_phase": true,
    "bids": [
      {
        "player": "The Ironclad",
        "is_local": true,
        "voted": true,
        "vote_relic_index": 0
      }
    ],
    "all_bid": false
  }
}
```

### Multiplayer-Only Actions

**End turn (vote):**
```json
{ "action": "end_turn" }
```
In multiplayer, this is a vote. The turn ends only when all players submit.

**Undo end turn:**
```json
{ "action": "undo_end_turn" }
```
Retract the end-turn vote before all players have committed.

All other actions work identically to singleplayer.
