# API Quick Reference

HTTP API on `localhost:15526`. No authentication.

- `GET /` — health check; returns `{ message, status, version, commit, schema_version }`
- `GET /api/v1/singleplayer` — read game state
- `POST /api/v1/singleplayer` — perform action
- `GET /api/v1/multiplayer` — read multiplayer state
- `POST /api/v1/multiplayer` — perform multiplayer action
- `GET /api/v1/profile` — read current profile progress
- `GET /api/v1/compendium` — read Compendium-shaped profile progress
- `GET /api/v1/wiki` — fuzzy-search discovered card/relic wiki entries
- `GET /api/v1/profiles` — list profile slots
- `POST /api/v1/profiles` — switch or delete profile slots

Singleplayer and multiplayer endpoints are mutually exclusive (HTTP 409 if mismatched).

## GET — Query Parameters

| Parameter | Values                                        | Default | Description                                                     |
|-----------|-----------------------------------------------|---------|-----------------------------------------------------------------|
| `format`  | `json`, `markdown`                            | `json`  | Response format                                                 |
| `pretty`  | `1`, `true`, `yes` (or present with no value) | off     | Indent the JSON. Off by default — compact output is ~32% smaller |

`pretty` applies to POST requests as well (e.g. `POST /api/v1/singleplayer?pretty=1`) and to error
responses. It has no effect on `format=markdown`.

## GET — State Types

Every JSON response includes:
- `state_type` — which screen the game is on (see below)
- `mod_version` — mod version string, e.g. `0.4.0`
- `mod_commit` — git commit the mod was built from, e.g. `2de67cd` (`2de67cd-dirty` for a build from an edited tree), `null` when the build could not determine one
- `schema_version` — integer version of this contract. **Bumped only when an existing field or action changes meaning or is removed.** Added fields, actions, parameters and `state_type`s do not bump it, so feature-detect additions by presence and use `schema_version` only to notice a breaking change.
- `run` — `{ act, floor, ascension }` (absent for `menu`)
- `player` — full player state: character, HP, gold, relics, potions, `max_potion_slots` (belt capacity, grows with relics), and during combat: energy, hand, piles, orbs (absent for `menu`)
  - `deck_count` — number of cards in the master deck. Present on every screen, combat included (combat clones the deck into the draw pile, so the deck itself stays intact).
  - `deck` — the master deck itself, one entry per card: `index` (position in this list), `id`, `name`, `type`, `cost`, `star_cost`, `description`, `rarity`, `is_upgraded`, `keywords`. **Outside combat only** — during a fight the hand plus `draw_pile` / `discard_pile` / `exhaust_pile` already enumerate every card. Empty (and `deck_count` 0) before a run's starting deck is dealt out. Markdown renders it as `### Deck (N cards)` with identical copies collapsed into one `×N` line.

Every card object carries `is_upgraded`, `is_upgradable`, `upgrade_level` and `max_upgrade_level` (a card can have more than one upgrade level, so `is_upgraded` does not mean "no upgrade left"). On the screens where an upgrade or a pick is being decided — `card_select` with `screen_type: "upgrade"`, `hand_select` with `mode: "upgrade_select"`, and `card_reward` — a card that still has an upgrade left also carries `upgrade_preview`: `{ name, cost, star_cost, description, keywords }` of the card as it would read after one upgrade, built from the game's own upgraded clone. It is absent everywhere else (combat hand, piles, deck list) to keep the payload small.

| `state_type` | Screen | Available Actions |
|---|---|---|
| `menu` | Main menu, menu submenu (incl. multiplayer host/join/load lobby), character select, `menu_screen: settings`, the in-run pause menu (`menu_screen: pause`) and the screens it opens (`menu_screen: compendium`, …), or a blocking FTUE/tutorial/popup that can also appear mid-run | `menu_select` |
| `unknown` | Unrecognized room or null state | None |
| `monster` / `elite` / `boss` | In combat | `play_card`, `use_potion`, `end_turn` |
| `hand_select` | In-combat card selection (exhaust, discard, upgrade) | `combat_select_card`, `combat_confirm_selection` |
| `rewards` | Rewards screen (post-combat or event-triggered) | `claim_reward`, `proceed` |
| `card_reward` | Pick a card to add to deck | `select_card_reward`, `skip_card_reward`, `select_card_reward_alternative` |
| `map` | Map navigation | `choose_map_node` |
| `event` | Event or Ancient encounter | `choose_event_option`, `advance_dialogue` |
| `rest_site` | Rest site | `choose_rest_option`, `proceed` |
| `shop` | Shop (auto-opens inventory) | `shop_purchase`, `sell_potion`, `proceed` |
| `fake_merchant` | Fake Merchant event (relic-only shop) | `shop_purchase`, `sell_potion`, `proceed` |
| `treasure` | Treasure room (auto-opens chest) | `claim_treasure_relic`, `proceed` |
| `card_select` | Deck card selection overlay (transform, upgrade, remove, choose-a-card) | `select_card`, `confirm_selection`, `cancel_selection` |
| `bundle_select` | Card bundle choice overlay | `select_bundle`, `confirm_bundle_selection`, `cancel_bundle_selection` |
| `relic_select` | Relic choice overlay (boss relics) | `select_relic`, `skip_relic_selection` |
| `crystal_sphere` | Crystal Sphere minigame | `crystal_sphere_set_tool`, `crystal_sphere_click_cell`, `crystal_sphere_proceed` |
| `game_over` | Run ended | `menu_select` with `main_menu` |
| `overlay` | Unhandled overlay (catch-all, prevents soft-lock) | None (manual interaction needed) |

**Note:** `use_potion` and `discard_potion` work during any state where potions are accessible (combat, map, events, etc.).

**Note:** the settings screen is reported as `state_type: menu`, `menu_screen: "settings"` wherever it is
open, with `in_run: true` when a run is paused underneath (`run` and `player` are still included there) and
`in_run: false` on the main menu. Its only option is `back`; changing settings values is not exposed.

**Note:** the in-run pause menu is reported as `state_type: menu`, `menu_screen: "pause"`, `in_run: true`
(`run` and `player` are still included), with one `{name, enabled}` option per visible button: `resume`,
`settings`, `compendium`, `give_up`, `disconnect`, `save_and_quit` (`give_up` / `save_and_quit` are hidden
for multiplayer clients, `disconnect` only appears for them). Open it with the `open_pause_menu` action.
Anything the pause menu opens on the same stack is reported the same way with a derived `menu_screen`
(e.g. `compendium`, `card_library`), `screen_class` naming the game's node type, and a single `back`
option — so a screen the API can enter is always a screen it can leave. While any of these is open,
gameplay actions are refused with an error instead of being applied to the room underneath.

## POST — Actions

All POST requests use JSON body with `"action"` field. All responses include `{ "status": "ok" | "error", "message": "..." }`.

### Menu / Game Over

| Action | Parameters | When to Use |
|---|---|---|
| `menu_select` | `option`: string, `seed`?: string, `ascension`?: int | Choose an advertised menu option. Options are case-insensitive. Submenus include `back` where visible, including `profile_select` options `profile_1`, `profile_2`, `profile_3`, and `back`. Blocking popups expose normalized button labels such as `ignore` or `back`. On `settings` (opened with `settings` from the main menu, or from the pause menu during a run) the only option is `back` (aliases `close`, `resume`), which leaves the screen and returns to whatever was underneath; settings values themselves cannot be changed through the API. On `pause` the options are `resume` (aliases `back`, `close`, `continue`) to close the menu and return to the run, `settings` and `compendium` (both left again with `back`), `give_up` (alias `abandon`) which **abandons the run permanently** — it opens the game's confirmation popup, reported next as `menu_screen: "popup"`, and is never auto-confirmed, so answer it with `yes` / `no` — `save_and_quit` (alias `main_menu`) which saves and returns to the main menu, where `continue` resumes the run, and `disconnect` in multiplayer. On the screens the pause menu opens (`compendium` and below) the only option is `back`. `game_over` supports `main_menu` only; `continue` returns an error. Supplying `seed` in unsupported contexts such as standard singleplayer character select returns an error and does not start a run. Timeline is no longer blocked when epochs are pending; use the `timeline_reveal_epochs` action to clear them. Multiplayer flow: on `multiplayer_join` use `refresh` / `back` / `join_<index>` / `join_<player_id>`. On `multiplayer_load_lobby` use `confirm` (or `embark`) to ready up, `unready` to retract, `back` to leave. On `character_select` while in MP, an additional `unready` option becomes available after readying, plus a `lobby` block in state lists ascension, all_ready, and per-player roster. `ascension` sets the ascension level on character select (applied before `option`, so it can ride along with the character pick or with `confirm`, or be sent alone with an empty `option`); character select state reports the current `ascension`, the unlocked `max_ascension`, and `ascension_selectable`. |
| `open_pause_menu` (alias `pause`) | (none) | Open the in-run pause menu (the top-bar gear / Esc), the only API route out of a run. Requires a run in progress; returns `already_open: true` if it was already up, and an error if the compendium or settings is stacked on top of it (leave that with `menu_select` `back` first) or if a card is still being played. The next state reports `menu_screen: "pause"`. ⚠️ From there `give_up` **abandons the run permanently** (it asks for confirmation first) and `save_and_quit` ends the session back at the main menu — neither is undoable from the API. |
| `timeline_reveal_epochs` | (none) | Reveal every obtained-but-unrevealed epoch. A finished run leaves epochs obtained, and the main menu hides `singleplayer` until they are revealed. Call from the main menu and repeat until the response has `done: true`; `pending_epoch_ids` lists what is left. Opens the Timeline, reveals each pending epoch through the game's own reveal path (granting its unlocks and writing progress), then returns to the main menu. Works with no run in progress. |

### Profiles

`GET /api/v1/profile` returns persistent progress for the active profile, including character stats, discoveries, achievements, epochs, and global run totals.

`GET /api/v1/compendium` returns the active profile grouped like the in-game Compendium:

When a run is active, the response includes `current_run.run_id` in `{save_scope}:profile{profile_id}:{start_time}` format. This identifies the specific run attempt, while `seed` identifies the generated run content.

| Section | Status |
|---|---|
| `card_library` | Discovered cards plus pick/skip/win/loss stats. Card rules text is supplied by game state when cards are visible in a run. |
| `relic_collection` | Discovered relic IDs. Profile data does not expose a typed per-relic description or obtained-count catalog. |
| `potion_lab` | Discovered potion IDs. Profile data does not expose a typed per-potion rules text or lab metadata catalog. |
| `bestiary` | Encounter/enemy profile fight stats. The game UI marks Bestiary as future/locked, so this is stats-only rather than a full enemy catalog. |
| `character_stats` | Per-character and global totals. |
| `run_history` | Summaries of the active profile's saved `saves/history/*.run` files, capped to the 20 most recent entries. |

Run history is resolved from the active Steam account's profile save root. `current_run.run_id` identifies one concrete attempt; `seed` identifies generated run content and can repeat across attempts.

`GET /api/v1/wiki?query=<text>&item_type=<all|card|relic>&limit=<n>&scope=<discovered|all>` returns a bounded fuzzy-search result over cards and relics. `scope` defaults to `discovered` (the active profile's discovered entries only); `scope=all` searches the full catalog, which is what you need when an offered card or relic generates something this profile has never held. Every result carries a `discovered` flag. `query` is required because this endpoint is intentionally selective and does not dump the full catalog. `item_type` defaults to `all`, and `limit` defaults to 10 with an internal maximum. Card results include both `base` and `upgraded` objects when the card can be upgraded.

Example searches:

- `/api/v1/wiki?query=ironclad%20perfect%20strike&item_type=card`
- `/api/v1/wiki?query=silver%20spoon&item_type=relic&limit=5`

`GET /api/v1/profiles` returns the three profile slots:

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

`POST /api/v1/profiles` supports:

| Action | Parameters | When to Use |
|---|---|---|
| `switch` | `profile_id`: 1-3 | Switch through the game profile UI. Empty slots can be used for fresh-profile testing. Cannot be used during a run. |
| `delete` | `profile_id`: 1-3 | Delete an inactive profile slot. The active profile is rejected. |

### Combat

| Action | Parameters | When to Use |
|---|---|---|
| `play_card` | `card_uid`?: string, `card_index`?: int, `target`?: string | Play a card from hand. Identify the card by `card_uid` (from state, e.g. `"STRIKE#2"`) — it names the card instance and stays valid for the whole combat — or by `card_index`, which shifts whenever a card leaves the hand. `target` is an `entity_id` (`"JAW_WORM_0"`) or a `combat_id` (`3`), required for single-target cards unless exactly one enemy is alive, in which case it is chosen automatically. |
| `use_potion` | `slot`: int (aliases `potion_index`, `index`), `target`?: string | Use a potion. `slot` is the belt slot from state `player.potions[].slot`, not a position in the list. `target` (an `entity_id` or `combat_id`) is required for enemy-targeting potions unless exactly one enemy is alive, in which case it is chosen automatically; a targeted potion is never consumed against nothing. Works outside combat for non-combat-only potions. |
| `sell_potion` | `slot`: int (aliases `potion_index`, `index`) | Sell a potion to the merchant by using it on them (Foul Potion's 100-gold mode). Only valid in a shop or fake merchant, and only for potions with a merchant interaction; shop state lists those under `sellable_potions`. |
| `discard_potion` | `slot`: int | Discard a potion to free up the slot. Use when slots are full and you need room for incoming potions. |
| `end_turn` | _(none)_ | End the player's turn. |

### In-Combat Hand Selection (`hand_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `combat_select_card` | `card_index`: int | Select/deselect a card during "choose a card to exhaust/discard" prompts. |
| `combat_confirm_selection` | _(none)_ | Confirm the hand card selection. |

When `mode` is `upgrade_select`, each selectable card with an upgrade left carries `upgrade_preview` (its upgraded form).

### Rewards (`rewards`)

| Action | Parameters | When to Use |
|---|---|---|
| `claim_reward` | `index`: int | Claim a reward. Card rewards open the `card_reward` screen. |
| `proceed` | _(none)_ | Leave the rewards screen. |

### Card Reward (`card_reward`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_card_reward` | `card_index`: int | Pick a card to add to deck. |
| `skip_card_reward` | _(none)_ | Skip the card reward (if allowed). Picks the `Skip` option by id, not whichever button is first — a relic can add its own alternative, and skipping does not take it. |
| `select_card_reward_alternative` | `option_id`: string (or `index`: int) | Take one of the card reward's alternative options, listed in state under `card_reward.alternatives`. `Skip` is one; relics add others (Pael's Wing adds `SACRIFICE`, which turns the reward into progress towards a relic — skipping does **not** count as sacrificing). |

Each offered card with an upgrade left carries `upgrade_preview`, so the upgraded form can weigh into the pick.

### Map (`map`)

`map.boss` includes the upcoming boss coordinate plus stable `id` and readable `name` fields when the run map exposes boss identity. `map.bosses` contains all boss entries for maps with more than one boss.

| Action | Parameters | When to Use |
|---|---|---|
| `choose_map_node` | `index`: int | Travel to a node from `next_options`. |

### Event (`event`)

| Action | Parameters | When to Use |
|---|---|---|
| `choose_event_option` | `index`: int | Choose an event option by index from state. Locked options return an error. Also used for "Proceed" options. |
| `advance_dialogue` | _(none)_ | Click through Ancient dialogue until `in_dialogue` is false. |

### Rest Site (`rest_site`)

| Action | Parameters | When to Use |
|---|---|---|
| `choose_rest_option` | `option_id`: str (aliases `id`, `option`) — **preferred**; or `index`: int (alias `option_index`) | Choose rest, smith, or another option. `option_id` matches `rest_site.options[].id` case-insensitively (`HEAL`, `SMITH`, `DIG`, …). `index` is `rest_site.options[].index`, i.e. the position in the game's option list — *not* the on-screen button order. Both are resolved to the same model option and must agree if both are sent. Disabled options are refused; errors list every option as `[index] id (enabled/disabled)`. Replies with `option_id` and `index` of what was clicked. |
| `proceed` | _(none)_ | Leave the rest site. |

`rest_site.options[]` carries `index`, `id`, `name`, `description`, `is_enabled` and `has_button`
(false when the option exists in the run model but no button is drawn yet — markdown appends
`(no button on screen)` in that case, and `choose_rest_option` refuses it instead of pressing a neighbour).

### Shop (`shop`)

| Action | Parameters | When to Use |
|---|---|---|
| `shop_purchase` | `index`: int | Buy an item by its index. Must be stocked and affordable. |
| `proceed` | _(none)_ | Leave the shop. |

### Treasure (`treasure`)

| Action | Parameters | When to Use |
|---|---|---|
| `claim_treasure_relic` | `index`: int | Claim a relic from the opened chest. |
| `proceed` | _(none)_ | Leave the treasure room. |

### Card Selection Overlay (`card_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_card` | `index`: int | Grid screens: toggle card selection. Choose-a-card: pick immediately. |
| `confirm_selection` | _(none)_ | Confirm (for grid screens with preview). Not needed for choose-a-card. |
| `cancel_selection` | _(none)_ | Cancel preview, skip (choose-a-card), or close screen. |

On the upgrade screen (`screen_type: "upgrade"`), each card with an upgrade left carries `upgrade_preview` (its upgraded form). The transform/remove screens do not.

### Bundle Selection Overlay (`bundle_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_bundle` | `index`: int | Open a bundle preview. |
| `confirm_bundle_selection` | _(none)_ | Confirm the previewed bundle. |
| `cancel_bundle_selection` | _(none)_ | Cancel the bundle preview. |

### Relic Selection Overlay (`relic_select`)

| Action | Parameters | When to Use |
|---|---|---|
| `select_relic` | `index`: int | Pick a relic (immediate). |
| `skip_relic_selection` | _(none)_ | Skip the relic choice. |

### Crystal Sphere (`crystal_sphere`)

| Action | Parameters | When to Use |
|---|---|---|
| `crystal_sphere_set_tool` | `tool`: `"big"` or `"small"` | Switch divination tool. |
| `crystal_sphere_click_cell` | `x`: int, `y`: int | Reveal a cell. |
| `crystal_sphere_proceed` | _(none)_ | Finish the minigame. |

## Multiplayer Additions

`POST /api/v1/multiplayer` supports all singleplayer actions plus:

| Action | Parameters | When to Use |
|---|---|---|
| `end_turn` | _(none)_ | Vote to end turn. Turn ends when all players vote. |
| `undo_end_turn` | _(none)_ | Retract end-turn vote (before all players committed). |
