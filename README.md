# Apartment Complex Unlock (Rust / Oxide plugin)

Customizes the **Apartment Complex** monument (Common Ground, July 2026) on `apartments_complex_1`:

1. **NPC auto turrets** — removed automatically (all `NPCAutoTurret` killed). *(main goal)*
2. **Outer safe zone** — on by default; set config `Safe zone: keep the monument's outer safe zone ON` to `false` and the plugin keeps it off permanently (re-asserted on every sweep, since the monument respawns the zone enabled on boot/wipe). Admins can also toggle it live with `/apsafezone on|off` (v0.18.0, console: `apartment.safezone`), which lasts until the plugin reloads. Off = monument exterior becomes PvP; the per-room combat zones are never touched. `/apsafezone` alone shows status. Works by shrinking the trigger's sphere collider to 1 cm (v1.0.4 — it used to disable the collider, but Unity's `Collider.ClosestPoint` on a disabled collider returns the query point, which made vanilla's satellite crash-site search treat the *entire map* as inside this safe zone: every thruster said "Unsuitable location") + `ForceUpdateTriggers()` on nearby players, so it applies instantly without re-entering.
3. **Deployables (admin)** — admins place things like **recyclers**, **car lifts**, **gambling machines**, boxes, furnaces, etc. on the roads/open areas inside the monument, with a **live preview**, bypassing the monument's build block. Placements are decay-proof and persist across restarts.
4. **Renter decorating** — players who **rent an apartment room** can place deployable items **inside their own room** by simply holding the item and clicking — plus industrial automation (pipe tool), wallpaper panels (wallpaper tool), and reskinning (spray can).
5. **In-game help** — `/aphelp` opens a scrollable window with a **Guide tab** and an **Item Names tab** (the full deployables reference).

The 62 interior apartment rooms (their `TriggerSafeZoneOverride` combat zones) are deliberately left untouched.

Plugin file: [`ApartmentComplexUnlock.cs`](ApartmentComplexUnlock.cs)

<!-- lpl:links -->
**[Download v1.0.7](https://github.com/lowpoplabs/ApartmentComplexUnlock/releases/latest)** · **Flyer:** [web](https://lowpoplabs.github.io/flyers/RustApartmentUnlock.html) / [PDF](ApartmentComplexUnlock-Flyer.pdf) · **[Changelog](CHANGELOG.md)** · **[Ko-fi](https://ko-fi.com/lowpoplabs)**
<!-- /lpl:links -->

## Why deployables instead of "remove building block"

The apartment complex enforces its no-build on the **client**, not the server. We proved this by decompiling the server assembly: after removing every server-side prevent-building volume, `BasePlayer.CanBuild()` returns **true** and the server logs no placement error — but the client still shows red "Cannot build in this area" and never sends the build request. A server-only plugin can't remove client-side monument data, so **normal foundation/wall building can't be enabled plugin-only** (that needs a map edit).

Deployables sidestep this: the plugin **spawns the entity server-side** at your aim point, so the client's placement check never runs.

## Commands

Deploy (admin-only — enforced by `Deploy: restrict to admins` = true). Preview → place flow:
1. `/apdeploy <item>` — starts a **preview**: a green box (footprint) + cyan arrow (facing) follows your crosshair. Accepts **any deployable item shortname** (`box.wooden`, `furnace`, `workbench1`, `sleepingbag`, …), a config alias, or an `assets/...` prefab path.
   - Built-in aliases: `carlift` → static car lift, `recycler` → static recycler, `trough` → horse trough. (The static car lift and recycler aren't normal items, so the aliases give an easy name; the trough alias is just a convenience — `hitchtrough` also works.)
   - New built-in aliases are merged into an existing config automatically on reload (your custom entries are kept).
2. `/aprotate [degrees]` — spin the previewed object in place. No number = one step (config `RotateStepDegrees`, default 45°); a number rotates by that much (e.g. `/aprotate 90`, `/aprotate -15`).
3. `/applace` — spawn the previewed deployable at your current aim point (within 20m), at the previewed rotation.
4. `/apcancel` — cancel the preview without placing.
- `/apundeploy` — remove the deployable you're looking at (only removes entities this plugin placed **this session**; it refuses anything else).

The preview outline follows your crosshair for position; its **rotation is fixed** (starts facing the way you were looking) so it stays steady while you aim — use `/aprotate` to turn it. Markers use in-game debug draw, which renders for **admins** — since deploy is admin-only, that's who sees them.

Console equivalents: `apartment.deploy <item>`, `apartment.rotate [deg]`, `apartment.place`, `apartment.cancel`, `apartment.undeploy`, plus `apartment.apply` (run the turret sweep now).

## Renter decorating

Players who currently **rent a room** (they're in the room's `owners` list — the vanilla Common Ground rental) can furnish it. **No commands needed** (v0.16.0 hold placement):

1. **Hold a deployable item** (box, furnace, rug, …) while **standing inside your rented room**.
2. A ghost outline follows your crosshair — **green** = OK, **red** = outside your room. *(The game's own ghost stays red with "Cannot build in this area" — ignore it; that's the client-side monument block, which is exactly why the plugin places server-side.)*
3. **Left-click** places it (consumes the item). **R** rotates in 45° steps (config `RotateStepDegrees`).
4. `/apundeploy` while looking at your object takes it back (item refunded).

We verified native placement can never work here: even with every server-side prevent-building volume removed, the client still refuses inside rooms (red ghost, no build request sent). The hold flow works because button presses stream to the server in every input tick (`OnPlayerInput`), regardless of what the client thinks of the build. Toggle: `Renter deploy: vanilla-style hold placement`.

The `/apdeploy` preview→place commands also still work for renters, with these shared rules:

- **Items only** — any deployable **item shortname** (`box.wooden`, `furnace`, `rug`, `table`, …). No aliases or raw prefab paths (those cover non-item statics and stay admin-only).
- **Their room only** — placement is allowed only where the game's own `ApartmentRoom` bounds contain the aim point **and** the player is a renter of that room. The preview outline turns **red** outside the room, **green** inside.
- **Costs the item — hardcoded (v0.31.2)** — renters must have the real item (crafted or found); it's taken from inventory on placement (skin preserved) and **refunded** on `/apundeploy`. There is deliberately no config to disable this, so renters can never conjure items from nothing.
- **Limit** — `Renter deploy: max placed objects per room` (default 10). Optional blocklist: `Renter deploy: blocked item shortnames`.
- **Integrates with the vanilla rental** — placed entities are registered into the game's own `ApartmentRoom.Furniture` list, so they:
  - **persist across server restarts** (saved via the room's `furnitureIds`),
  - are made **static** exactly like vanilla apartment furniture (immortal, no ground-check, no decay),
  - get an **apartment lock** if lockable (boxes, etc.) that auto-authorizes the room's renters,
  - count toward the room's **upkeep tax** if the server enables `apartment.rentscaling`,
  - are **destroyed automatically on checkout/eviction**, exactly like vanilla furniture (items are *not* refunded then — renters should `/apundeploy` before checking out).
- Renters see the preview via a brief admin-flag flick per draw tick (ddraw only renders for admin clients); this is the standard plugin technique and lasts milliseconds.
- Optional gating: set `Renter deploy: also require Oxide permission` and grant `apartmentcomplexunlock.renter`.
- Sleeping bags placed by a renter are assigned to them automatically.
- **Electrical items auto-power** (v0.17.0, `Renter deploy: auto-power placed electrical items`). Apartments have no grid and the wire tool can never work there (`CanPlayerUseWires` fails inside "Prevent Building" volumes — client-side too, same wall as building). So the plugin mirrors the game's own trick for the vanilla apartment fridge/telephone: electrical **consumers** (fridge, ceiling light, heater, boom box, …) get the IOEntity power flag (`Flags.Reserved8`) forced on at placement and re-asserted every sweep (covers restarts). Pure wiring infrastructure — batteries, switches, timers, solar panels — stays decorative, since wires can't exist there. If you don't want free powered autoturrets in rooms, add `autoturret` to the renter blocklist.

Admins keep full free placement everywhere; anything an admin places **inside a rented room** is also registered as room furniture (persists, cleaned up on checkout). Admin `/apundeploy` can remove any plugin-placed entity (`OwnerID != 0`) but never vanilla furniture.

## Industrial automation (v0.19.0)

Pipe **connections** are pure server-side data (only the wire *tool* is client-blocked), so full industrial automation works in rented rooms:

1. **Storage adaptor** — hold one, aim at any container in your room (your boxes *or* the room's built-in furniture), click: it snap-mounts to the container's top (its function comes entirely from being parented to the container). One per container, like vanilla.
2. **Industrial crafter** — same, aimed at a placed workbench (mounts on the front; crafting tier = that workbench's level).
3. **Industrial conveyor** — free-places like any deployable; auto-power runs it (it still has its vanilla on/off toggle and filter UI).
4. **Pipe tool click flow** (v0.20.0) — hold the vanilla **pipe tool** near the monument:
   - **Left-click** a component → selects it as source (green highlight = clickable, cyan line follows your aim); **left-click** the target → real pipe connected (first free industrial output → first free industrial input).
   - **Left-click empty space or press R** → cancel the selection.
   - **Right-click** a component → disconnect all its pipes (renters: only components they placed).
   - Outside the monument the pipe tool keeps its vanilla behavior — the plugin only intercepts clicks within the detection radius (where vanilla wiring is client-blocked anyway).
5. **`/appipe`** — command fallback for the same thing: run once aiming at the source, again at the target. `/appipe clear` = disconnect aimed component, `/appipe cancel` = abort. Max 20m per pipe; renters are limited to components in their own room (and can only clear their own).

Removing a component (`/apundeploy`) or checkout kills it; the game cleans up its connections. Also fixed in v0.19.0: auto-power now feeds each device's **main power slot** (the crafter takes power on input 1, not 0) and no longer risks pulsing toggle inputs.

## Wallpaper panels (v0.21.0)

Wallpaper can't go on the room's real walls (it's per-side state on a `BuildingBlock`; monument walls are static geometry with no entity). Instead, hold the vanilla **wallpaper tool** in your room:

1. **Right-click** to pick a design in the vanilla tool's own menu (works fine in the monument — its selection RPC isn't build-gated). The plugin reads the selection via the public `GetSkinIDForCategory` (v0.31.1 — no reflection, uMod-compliant) and re-verifies design ownership server-side before applying.
2. Aim at a wall — a ghost shows a **wall panel** that will sit flush against it (bottom on the floor).
3. **Click** — the plugin spawns a real stone building wall there (`grounded`, immortal, registered as furniture) with your design applied to **both sides**. Costs the vanilla cloth price for renters.
4. **Click an existing panel** to re-paper it with the currently selected design. `/apundeploy` removes a panel.

Notes: panels are 3m building walls — treat them as accent walls (they won't perfectly tile odd wall sizes, and they sit slightly proud of the real wall). `grounded` isn't saved by the game, so the plugin re-asserts it at boot and every sweep — that's what keeps panels from collapsing after a restart. Config toggle: `Renter deploy: wallpaper panels`.

## Reskin with the spray can (v0.26)

Vanilla spray-can reskinning is hard-blocked at monuments (`ValidateReskin` → "CannotReskinInMonument", before any hook, client-side too). Instead: hold the **spray can** and **click an object you placed** — a skin-picker window opens (item icons, 18 per page, Default always first). Click a skin to apply it instantly; the window stays open to browse live.

**Skin ownership is hardcoded (v0.28.1) — no config, no admin bypass.** The picker only shows, and the server only applies, skins the player actually owns, verified with the same `CheckSkinOwnership` call the vanilla spray can uses. Nobody — admins included — can apply a skin they don't own through this plugin, by design (Facepunch compliance).

## Gambling (v0.25)

Verified prefab aliases (admin): `slots` (bandit-camp casino slot machine — mountable, scrap gambling), `blackjack` (static blackjack machine — seats and pot storage built in), `caboose` (the whole gambling wagon — a TrainCar spawned off-rails: the plugin cancels the game's off-track instakill, drops its train logic, and freezes it in place; **experimental across restarts**). Renters can place `cardtable` (poker) in their room.

**v1.0.2:** `slots` now spawns `assets/prefabs/misc/casino/slotmachine/slotmachine.prefab` (the bandit-camp machine). It previously used `slotmachine.caboose_static.prefab`, which turned out to be the caboose's counter-embedded *decor* variant — players could not mount or play it. Old configs are migrated automatically on load (including removal of the `-0.75` spawn offset that only the old prefab needed); kill and re-deploy any already-placed machines.

Testing tip (server console): `apartment.rentroom <number>` rents a room to you, `apartment.checkoutroom` checks out (destroys furniture!), `apartment.rentallrooms` / `apartment.checkoutallrooms` for bulk.

Diagnostics (admin/server console): `apartment.scan`, `apartment.probe <name>`, `apartment.whyblocked`, `apartment.layers`.

**`/aphelp`** — in-game help window (v0.24.0: scrollable CUI popup with ✕ close; v0.28.0: tabbed — **Guide** for everyone plus admin sections for admins, and **Item Names** with the full deployable-name reference baked in). Console text version: `apartment.help`.

**`/apnudge [meters]`** (v0.27.0) — move the plugin-placed object you're aiming at up/down (e.g. `/apnudge -0.75`; default `-0.1`, clamp ±5m). For prefabs whose *visuals* are offset from their server origin (neither bounds nor colliders reveal it — the old caboose_static slot machine floated ~0.75m), set a permanent per-alias fix in config `Deploy: spawn height offset` (ships empty; none of the shipped aliases need one).

## Layouts — save & re-apply your monument decor (v0.29)

`/aplayout save [name]` snapshots every plugin-placed object **outside the rooms** (your road/courtyard decor: recyclers, gambling machines, troughs, wall panels…) — prefab, skin, wallpaper, grade — with positions stored **relative to the monument**. That means:

- `/aplayout apply [name]` re-creates the whole setup with one command — **even after a forced wipe**, when the monument spawns at completely new world coordinates.
- Apply is **idempotent**: anything of the same prefab already within 0.75m is skipped, so re-running it never duplicates.
- `/aplayout list` / `/aplayout delete [name]`; console: `apartment.layout …`. Default layout name: `default`.
- Hands-free by default (v1.0.3): the layout named in config `Layout: which saved layout to auto-apply on boot` (default `default`) is re-applied ~15s after every boot, safe due to idempotency. Save it once with `/aplayout save` and every wipe re-decorates itself. Objects applied from console/boot get `OwnerID = 1`. Turn it off with `Layout: auto-apply a saved layout on boot` = `false`; before any layout is saved it simply does nothing.

## Data files (v1.0.3)

Both data files live in their own folder: `oxide/data/ApartmentComplexUnlock/spawned.json` (net IDs of plugin placements) and `oxide/data/ApartmentComplexUnlock/layouts.json`. On the first load of v1.0.3 the old top-level `ApartmentComplexUnlock.json` / `ApartmentComplexUnlock_layouts.json` are copied into the folder automatically; the originals are left behind as a backup (a plugin can't delete files) and can be removed by hand.

Renter room furniture is deliberately excluded — that belongs to the renters.

## Install

Drop `ApartmentComplexUnlock.cs` into `oxide/plugins/` and Oxide compiles it on load. The config is generated at `oxide/config/ApartmentComplexUnlock.json`. Confirm the loaded version with `oxide.plugins` and reload with `oxide.reload ApartmentComplexUnlock`.

## Config notes

- `Detection radius in meters around monument center` (150) — the monument's own bounds are unreliable, so detection is radius-based.
- `Periodic re-sweep interval in seconds` (60) — keeps turrets/safe-zone gone (e.g. after a respawn) when their toggles are on.
- `Remove building block` defaults to **false** — it does not enable normal building here (client-side block; re-confirmed in-game July 2026: red ghost inside rooms even with all server volumes destroyed). Leave it false.
- ~~Deployables placed outside a tool cupboard may decay~~ **fixed in v0.22.0**: admin free-placements outside rooms are now made decay-proof/immortal on placement (this is what previously ate hitch troughs on the roads — no cupboard = decay). Plugin placements are also persisted by net ID to the plugin data file, so they stay protected and `/apundeploy`-removable across restarts; protection is re-asserted at boot and every sweep because the game doesn't save it.

## Caveats

- Re-verify after monthly forced wipes — Facepunch renames things and monuments respawn (the plugin re-sweeps on `OnServerInitialized`).
- Removing the safe zone makes the whole monument exterior PvP-enabled.

## Compatibility

- Built against the September 3, 2026 Rust update (build 2633.288). Versions from 1.0.5 onward need that build or newer; the previous version is the last one that compiles on August builds.

## Support

Provided as-is. Bug reports welcome via GitHub Issues. No Discord, no custom work, no promises on turnaround. If it saved you time or you and your players enjoy it:

[![Ko-fi](https://ko-fi.com/img/githubbutton_sm.svg)](https://ko-fi.com/lowpoplabs)

## License

[MIT](LICENSE).
