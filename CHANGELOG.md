# Changelog

All notable changes to the Apartment Complex Unlock plugin are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/),
and this project adheres to [Semantic Versioning](https://semver.org/).

## [1.0.6] - 2026-09-12

First public release on GitHub — no gameplay changes.

### Added
- One-line load message naming the author and tip jar (`ApartmentComplexUnlock v1.0.6 loaded - by LowPopLabs - ko-fi.com/lowpoplabs`).
- MIT license file and a README Support section with the Ko-fi button; support posture is as-is, issues welcome.

### Changed
- The plugin file moved from `oxide/plugins/` to the repo root; the README's server-specific deploy notes became a generic Install section.

## [1.0.5] - 2026-09-03

### Fixed
- Compiles again on the September 3, 2026 Rust update (build 2633.288): the game removed `BaseEntity.SetFlag`, so entity flags are now set through the new flags-update scope with a network update, exactly what the old call did. No behaviour change. Affects the door lock placed on unlocked rooms.

## [1.0.4] - 2026-08-23

### Fixed
- Turning the outer safe zone off now shrinks the zone's trigger collider to 1 cm instead of disabling it. A disabled collider broke the vanilla satellite crash-site location search, making every thruster report "Unsuitable location" — the game treated the entire map as inside this safe zone.

## [1.0.3]

### Added
- A saved layout (default name `default`) is re-applied automatically ~15 seconds after every server boot, so outdoor decor survives wipes hands-free. Configurable via `Layout: auto-apply a saved layout on boot` and `Layout: which saved layout to auto-apply on boot`.

### Changed
- Data files moved into their own folder: `oxide/data/ApartmentComplexUnlock/spawned.json` and `oxide/data/ApartmentComplexUnlock/layouts.json`. The old top-level files are copied into the folder automatically on first load and left behind as a backup you can delete.

## [1.0.2]

### Fixed
- The `slots` alias now spawns the bandit-camp slot machine (`slotmachine.prefab`), which players can actually mount and play. It previously spawned the caboose's counter-embedded decor variant, which was unplayable. Existing configs are migrated automatically (including removal of the spawn offset only the old prefab needed) — kill and re-deploy any already-placed machines.

## [1.0.0]

### Added
- Initial release for the Apartment Complex monument (Common Ground update):
  - NPC auto turrets removed automatically.
  - Outer safe zone control — keep it on (default) or off via config, plus a live `/apsafezone on|off` admin toggle. Per-room combat zones are never touched.
  - Admin deployables anywhere in the monument with a live preview — including statics like recyclers, car lifts, and gambling machines. Placements are decay-proof and persist across restarts.
  - Renter decorating: renters hold a deployable item in their room and click to place (R rotates), with `/apundeploy` refunds, per-room limits, and full integration with the vanilla rental (persistence, apartment locks, checkout cleanup).
  - Industrial automation via the pipe tool, wallpaper panels via the wallpaper tool, and reskinning via the spray can.
  - Electric appliances auto-power — fridges, lights, heaters, conveyors, and crafters just work, no wiring needed.
  - Admin layouts: save, list, apply, and delete named snapshots of outdoor placements, stored relative to the monument so they survive map changes.
  - In-game help window (`/aphelp`) with a guide tab and a full item-names reference.
