using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.UI;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Game.Rust.Cui;

namespace Oxide.Plugins
{
    [Info("Apartment Complex Unlock", "LowPopLabs", "1.0.7")]
    [Description("Apartment Complex: removes NPC turrets, admin deployable placement, and lets renters furnish their own apartment room (server-side placement with live preview). The outer safe zone stays on unless you turn it off in the config.")]
    public class ApartmentComplexUnlock : RustPlugin
    {
        // Rust 2633.288 (2026-09-03) removed BaseEntity.SetFlag; flags now change inside a
        // FlagsUpdateScope. SendNetworkUpdate mode matches what the old call did.
        private static void SetFlagNet(BaseEntity entity, BaseEntity.Flags flag, bool value)
        {
            using (var scope = entity.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                scope.Set(flag, value);
            }
        }
        // ---------------------------------------------------------------------
        // Confirmed live on Rust 70 (Common Ground), monument "apartments_complex_1":
        //   * Turrets (27x NPCAutoTurret)  -> Kill(), auto on sweep. Works. (main goal)
        //   * Outer safe zone -> ON unless the config says otherwise ("Safe zone:
        //     keep the monument's outer safe zone ON" = false) or an admin runs
        //     /apsafezone off. Off is re-asserted on every sweep, because the
        //     monument respawns its zone enabled on boot/wipe.
        //   * Building block -> the monument enforces this on the CLIENT
        //     ("Cannot build in this area"); server-side volume removal does NOT
        //     enable normal building (proven via decompile: CanBuild()==true,
        //     client refuses to send the request). So instead admins place
        //     DEPLOYABLES server-side at the aim point (apdeploy <item>) — ANY
        //     deployable item by shortname — which bypasses the client check.
        //   * Renters (ApartmentRoom.owners) get the same flow inside their own
        //     room: item consumed from inventory, entity registered into the
        //     game's own ApartmentRoom.Furniture list so it persists across
        //     restarts, is taxed like vanilla furniture, and is auto-destroyed
        //     on checkout/eviction. Lockable items get an apartment_lock that
        //     auto-authorizes the room's renters.
        //   * Vanilla-feel hold placement: confirmed the client blocks native
        //     building INSIDE rooms too (red ghost + "Cannot build in this
        //     area" even with server volumes removed), so instead: hold a
        //     deployable item while standing in your rented room -> plugin
        //     ghost outline appears; LEFT-CLICK places, R rotates. The click
        //     never reaches the game's build system, but buttons stream to the
        //     server in every input tick (OnPlayerInput), which is how we see it.
        // Detection is radius-based (DetectionRadius, 150m) because
        // MonumentInfo.Bounds is unreliable here. Diagnostics: apartment.scan,
        // apartment.probe <name>, apartment.whyblocked, apartment.layers.
        // ---------------------------------------------------------------------

        #region Localization

        // All player-facing chat/console messages go through the Lang API.
        // The long-form help window (guide/item reference) and diagnostic dumps
        // are deliberately not localized: they are technical reference text
        // containing game item identifiers.
        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["AdminOnly"] = "You must be an admin to use this.",
                ["AdminOnlyConsole"] = "You must be an admin to use this command.",
                ["AdminOrRenter"] = "You must be an admin, or currently rent an apartment room, to use this.",
                ["InGame"] = "Run this in-game.",
                ["InGameAim"] = "Run this in-game (needs your aim point).",
                ["InGameWhyBlocked"] = "Run this in-game while standing where you can't build.",
                ["NoMonument"] = "No monument matched \"{0}\".",
                ["NoMonumentsOnMap"] = "No monuments on this map yet.",
                ["SweepNoMatch"] = "No monuments matched filter \"{0}\". Run `apartment.scan`.",
                ["SweepSummary"] = "Apartment Complex sweep across {0} monument(s): {1} turret(s), {2} building-block volume(s) removed.",
                ["SafeZoneNone"] = "No outer TriggerSafeZone found at the monument (already destroyed? a restart respawns it).",
                ["SafeZoneOn"] = "Safe zone ON ({0} trigger(s)) — monument exterior is safe again.",
                ["SafeZoneOff"] = "Safe zone OFF ({0} trigger(s)) — monument exterior is now PvP. Kept off until the plugin reloads; set \"Safe zone: keep the monument's outer safe zone ON\" to false in the config to make it permanent.",
                ["SafeZoneStatusLine"] = "{0}: {1} outer safe-zone trigger(s), {2} active.",
                ["SafeZoneUsage"] = "Usage: /apsafezone on|off (admin). A plugin reload restores the config setting.",
                ["NudgeUsage"] = "Usage: /apnudge [meters] — e.g. /apnudge -0.75 lowers the aimed object 0.75m. Default -0.1.",
                ["NudgeNoTarget"] = "Aim at an object you placed (within 10m).",
                ["NudgeUp"] = "up",
                ["NudgeDown"] = "down",
                ["NudgeMoved"] = "Moved {0} {1} {2:0.00}m (y = {3:0.00}). Tip: once it looks right, put the total offset in config \"Deploy: spawn height offset\" so new ones spawn correctly.",
                ["LayoutCooldown"] = "Layout apply is on cooldown — try again in a few seconds.",
                ["LayoutNotFound"] = "No layout named \"{0}\".",
                ["LayoutNotFoundList"] = "No layout named \"{0}\". /aplayout list",
                ["LayoutDeleted"] = "Deleted layout \"{0}\".",
                ["LayoutNone"] = "No saved layouts. /aplayout save [name] snapshots your current placements.",
                ["LayoutList"] = "Layouts: {0}",
                ["LayoutUsage"] = "Usage: /aplayout save|apply|list|delete [name]. Saves your outdoor placements relative to the monument (survives wipes); apply re-spawns them (skips ones already present).",
                ["LayoutNothing"] = "Nothing to save — no plugin-placed entities outside rooms.",
                ["LayoutSaved"] = "Saved layout \"{0}\": {1} object(s), positioned relative to the monument. /aplayout apply {0} re-creates them (works after wipes too).",
                ["LayoutApplied"] = "Layout \"{0}\": {1} placed, {2} already present, {3} failed.",
                ["DeployUsageRenter"] = "Usage: /apdeploy <item shortname> — e.g. box.wooden, furnace, rug. The item is taken from your inventory when you /applace, and refunded on /apundeploy.",
                ["DeployUsageAdmin"] = "Usage: apdeploy <item shortname | alias | prefab path>. e.g. recycler, carlift, box.wooden. Aliases: {0}",
                ["NotDeployableItem"] = "\"{0}\" is not a deployable item. Use an item shortname like box.wooden, furnace, rug.",
                ["ItemBlocked"] = "\"{0}\" is not allowed in apartments on this server.",
                ["NeedItem"] = "You need a {0} in your inventory first.",
                ["PreviewRenter"] = "Previewing \"{0}\": green outline = OK, red = outside your room. Aim inside your room, /aprotate to spin, /applace to place — or /apcancel.",
                ["CantResolve"] = "Couldn't resolve \"{0}\". Use a deployable item shortname (box.wooden, furnace...), a config alias (recycler, carlift), or an assets/... prefab path.",
                ["PreviewAdmin"] = "Previewing \"{0}\": green box = footprint, cyan arrow = facing. Aim to position, /aprotate to spin it, then /applace to place — or /apcancel.",
                ["NothingToPlace"] = "Nothing to place — start with /apdeploy <item> first.",
                ["AimSurface20"] = "Aim at a surface (within 20m) and try /applace again.",
                ["OwnRoomOnlyPlace"] = "You can only place inside your own apartment room — aim at your room's floor/walls.",
                ["RoomLimit"] = "Room limit reached ({0}/{1}). Remove something with /apundeploy first.",
                ["ItemGone"] = "You no longer have a \"{0}\" in your inventory.",
                ["PrefabFailed"] = "Failed to create prefab: {0}",
                ["PlacedRoomCmd"] = "Placed \"{0}\" in room {1}. It stays until you remove it (/apundeploy) or the room is checked out.",
                ["PlacedOutdoorCmd"] = "Placed \"{0}\" ({1}) — decay-proof. /apdeploy again for another, or /apundeploy while looking at it.",
                ["NothingToRotate"] = "Nothing to rotate — start with /apdeploy <item> first.",
                ["Rotated"] = "Rotated to {0:0}°. /aprotate again or /applace to place.",
                ["PreviewCancelled"] = "Preview cancelled.",
                ["NoActivePreview"] = "No active preview.",
                ["UndeployAim"] = "Aim at the deployable you want to remove.",
                ["NoEntity"] = "No entity where you're aiming.",
                ["UndeployOwnRoom"] = "You can only remove objects inside your own apartment room.",
                ["UndeployOwnOnly"] = "You can only remove objects you placed — not the room's built-in furniture.",
                ["RemovedRefunded"] = "Removed {0} and refunded the item.",
                ["Removed"] = "Removed {0}.",
                ["RemovedRoomAdmin"] = "Removed {0} from room {1} (no item refund in admin mode).",
                ["NotPluginPlaced"] = "\"{0}\" wasn't placed by this plugin — aborted for safety.",
                ["AimContainer"] = "Aim at a storage container (box, fridge, ...) to attach the adaptor.",
                ["HasAdaptor"] = "That container already has a storage adaptor.",
                ["AimWorkbench"] = "Aim at a workbench to attach the industrial crafter.",
                ["HasCrafter"] = "That workbench already has a crafter.",
                ["NotMountable"] = "Not a mountable item.",
                ["MountOwnRoom"] = "You can only attach that inside your own apartment room.",
                ["MountNotMonument"] = "That host isn't inside the monument.",
                ["MountAttached"] = "Attached {0} to {1}. Use the pipe tool or /appipe to connect it.",
                ["PipeAim"] = "Aim at an industrial component (adaptor, conveyor, crafter). Usage: /appipe | /appipe clear | /appipe cancel",
                ["PipeClearAim"] = "Aim at the component whose pipes you want to remove.",
                ["PipeClearOwn"] = "You can only clear pipes on components you placed.",
                ["PipeCleared"] = "Disconnected everything from {0}.",
                ["PipeNotInRoom"] = "That component isn't inside your apartment room.",
                ["PipeNoOutput"] = "{0} has no free industrial output.",
                ["PipeSourceSet"] = "Pipe source: {0}. Now click / run /appipe on the target (R or /appipe cancel to abort).",
                ["PipeSame"] = "Source and target are the same component — aim at the other end (R to cancel).",
                ["PipeSourceLost"] = "{0} no longer has a free industrial output — selection reset.",
                ["PipeNoInput"] = "{0} has no free industrial input.",
                ["PipeTooFar"] = "Too far apart for a pipe (max 20m).",
                ["PipeConnected"] = "Piped {0} -> {1}. Click another component for the next pipe.",
                ["PipeCancelled"] = "Pipe selection cancelled.",
                ["PipeCancelledEmpty"] = "Pipe selection cancelled (clicked empty space).",
                ["NoPipeSelection"] = "No pipe selection active.",
                ["WpPickDesign"] = "Pick a design in the wallpaper tool's menu first (right-click).",
                ["WpNotOwned"] = "You don't own that wallpaper design.",
                ["WpRepapered"] = "Re-papered the panel.",
                ["WpAimWall"] = "Aim at a wall (within 10m).",
                ["WpVertical"] = "Aim at a vertical wall surface.",
                ["WpOwnRoom"] = "You can only wallpaper inside your own apartment room.",
                ["WpNeedCloth"] = "You need {0} {1} to place wallpaper.",
                ["WpPanelFailed"] = "Failed to create the wall panel.",
                ["WpPlaced"] = "Wallpaper panel placed — click it again to change the design, /apundeploy to remove.",
                ["NoSkins"] = "That object has no item skins.",
                ["HoldContext"] = "Hold the deployable item while standing in your room (admins: anywhere in the monument), then left-click.",
                ["AimSurface"] = "Aim at a surface within 20m.",
                ["OwnRoomOnly"] = "You can only place inside your own apartment room.",
                ["AimMonument"] = "Aim somewhere inside the monument.",
                ["PlacedRoomHold"] = "Placed {0} ({1}/{2}) in room {3}. /apundeploy while looking at it to take it back.",
                ["PlacedOutdoorHold"] = "Placed {0} ({1}) — decay-proof. /apundeploy while looking at it to remove.",
                ["SkinTitle"] = "{0} — CHOOSE A SKIN   ({1} available)",
                ["PageXY"] = "Page {0} / {1}",
                ["HelpTitle"] = "APARTMENT COMPLEX — HELP",
                ["TabGuide"] = "GUIDE",
                ["TabItems"] = "ITEM NAMES",
                ["HintPlace"] = "click to place, R to rotate",
                ["HintAttach"] = "click to attach",
                ["HintAimContainer"] = "aim at a container",
                ["HintAimWorkbench"] = "aim at a workbench",
                ["HintPipeSource"] = "click: select source",
                ["HintNoOutput"] = "no free output",
                ["HintPipeIsSource"] = "source",
                ["HintPipeConnect"] = "click: connect",
                ["HintNoInput"] = "no free input",
                ["HintPipeCancel"] = "pipe source — R to cancel",
                ["HintRepaper"] = "click: re-paper",
                ["HintPickDesign"] = "right-click: pick a design in the tool",
                ["HintPlacePanel"] = "click: place wallpaper panel"
            }, this);
        }

        private string L(string key, BasePlayer player, params object[] args)
        {
            var msg = lang.GetMessage(key, this, player != null ? player.UserIDString : null);
            return args.Length > 0 ? string.Format(msg, args) : msg;
        }

        #endregion

        #region Configuration

        private Configuration _config;

        private class Configuration
        {
            [JsonProperty("Monument name filter (substring, case-insensitive)")]
            public string MonumentNameFilter = "apartments_complex";

            [JsonProperty("Detection radius in meters around monument center (0 = use monument bounds)")]
            public float DetectionRadius = 150f;

            [JsonProperty("Remove auto turrets")]
            public bool RemoveAutoTurrets = true;

            // Off by default: the apartment complex enforces its build block on the
            // CLIENT, so removing server-side prevent-building volumes does not let
            // players build. Use deployables (apdeploy) to place things instead.
            [JsonProperty("Remove building block (does NOT enable normal building here — client-side block)")]
            public bool RemoveBuildingBlock = false;

            [JsonProperty("Building-block physics layer name")]
            public string BuildingBlockLayerName = "Prevent Building";

            [JsonProperty("Bounds padding in meters (only used when Detection radius = 0)")]
            public float BoundsPadding = 5f;

            [JsonProperty("Sweep delay after server init (seconds)")]
            public float SweepDelay = 10f;

            [JsonProperty("Periodic re-sweep interval in seconds (0 = disabled) — keeps the safe zone off when toggled off")]
            public float ResweepInterval = 60f;

            // The monument respawns its safe zone on every boot/wipe, so "off"
            // has to be re-asserted; the sweep does that (see EnforceSafeZone).
            [JsonProperty("Safe zone: keep the monument's outer safe zone ON (false = monument exterior is PvP)")]
            public bool SafeZoneEnabled = true;

            [JsonProperty("Deploy: restrict deploy/undeploy commands to admins")]
            public bool DeployAdminOnly = true;

            [JsonProperty("Deploy: rotate step in degrees for /aprotate")]
            public float RotateStepDegrees = 45f;

            [JsonProperty("Renter deploy: let renters place deployables inside their own apartment room")]
            public bool RenterDeployEnabled = true;

            // NOTE: renters ALWAYS need the real item in their inventory
            // (crafted/found) — consumed on place, refunded on remove. This is
            // deliberately NOT configurable, so renters can never conjure items
            // from nothing.

            [JsonProperty("Renter deploy: max placed objects per room")]
            public int RenterMaxPerRoom = 10;

            [JsonProperty("Renter deploy: blocked item shortnames")]
            public List<string> RenterBlockedItems = new List<string>();

            [JsonProperty("Renter deploy: also require Oxide permission apartmentcomplexunlock.renter")]
            public bool RenterRequirePermission = false;

            [JsonProperty("Renter deploy: vanilla-style hold placement (hold a deployable item in your room; left-click places, R rotates)")]
            public bool RenterHoldPlacement = true;

            // Apartments have no power grid and the wire tool is blocked there
            // (CanPlayerUseWires fails inside "Prevent Building" volumes, client-
            // side too), so electrical consumers get force-powered instead —
            // the same trick vanilla uses for the apartment fridge/telephone.
            [JsonProperty("Renter deploy: auto-power placed electrical items (apartments have no grid)")]
            public bool RenterAutoPower = true;

            [JsonProperty("Renter deploy: wallpaper panels (hold the wallpaper tool, click a wall)")]
            public bool RenterWallpaper = true;

            // Vanilla spray-can reskin is hard-blocked at the monument
            // (IsBuildBlockedByMonument -> "CannotReskinInMonument", client-side
            // too), so the plugin opens its own skin picker instead.
            // NOTE: skin OWNERSHIP is deliberately NOT configurable — everyone,
            // admins included, can only apply skins they own (verified with the
            // same PlayerBlueprints.CheckSkinOwnership call the vanilla spray
            // can uses). Hardcoded to stay Facepunch-compliant.
            [JsonProperty("Reskin: hold the spray can and click your placed object to open a skin picker")]
            public bool ReskinEnabled = true;

            // You can deploy ANY deployable item by its shortname (e.g. box.wooden,
            // furnace, workbench1). These entries are just optional shortcuts /
            // overrides — handy for prefabs that aren't a normal item, like the
            // static (no-power, no-decay) car lift.
            [JsonProperty("Deployable aliases (optional: name -> prefab path OR item shortname). Any item shortname also works directly.")]
            public Dictionary<string, string> Deployables = new Dictionary<string, string>
            {
                ["carlift"] = "assets/bundled/prefabs/static/modularcarlift.static.prefab",
                ["recycler"] = "assets/bundled/prefabs/static/recycler_static.prefab",
                ["trough"] = "assets/prefabs/deployable/hitch & trough/hitchtrough.deployed.prefab",
                ["coop"] = "chickencoop",
                ["coup"] = "chickencoop",
                ["planter"] = "planter.large",
                // Gambling: the static machines are self-contained (no power/rails).
                // The caboose is a TrainCar — spawned off-rails the plugin cancels
                // its instakill and freezes it in place (see StabilizeIfTrainCar).
                // Slots: use the bandit-camp casino prefab, NOT the caboose_static
                // one — that variant is the counter-embedded caboose decor and is
                // not mountable standalone; this one players mount and play.
                ["caboose"] = "assets/content/vehicles/trains/caboose/traincaboose.entity.prefab",
                ["slots"] = "assets/prefabs/misc/casino/slotmachine/slotmachine.prefab",
                ["blackjack"] = "assets/content/vehicles/trains/caboose/blackjackmachine/blackjackmachine.static.prefab"
            };

            // On by default: `/aplayout save` with no name writes "default", so a
            // server that has saved a layout once gets re-decorated after every
            // boot/wipe with no further action. Harmless before the first save —
            // there is simply nothing to apply.
            [JsonProperty("Layout: auto-apply a saved layout on boot")]
            public bool AutoApplyLayoutEnabled = true;

            [JsonProperty("Layout: which saved layout to auto-apply on boot")]
            public string AutoApplyLayout = DefaultLayoutName;

            // Asset paths can change with game updates — configurable per the
            // uMod guidelines.
            [JsonProperty("Prefab path: wallpaper wall panel")]
            public string WallPanelPrefab = "assets/prefabs/building core/wall/wall.prefab";

            [JsonProperty("Prefab path: apartment lock")]
            public string ApartmentLockPrefab = "assets/prefabs/apartment/apartment_lock.prefab";

            // Some prefabs' client-side VISUALS are offset from their server
            // origin (e.g. the caboose static slot machine floats ~0.75m) and
            // neither bounds nor colliders reveal it server-side. Manual per-name
            // spawn offsets fix those permanently; find the right value live with
            // /apnudge, then put it here. (None needed for the shipped aliases —
            // the casino slot machine's origin sits at its base.)
            [JsonProperty("Deploy: spawn height offset by alias or prefab path (negative = lower)")]
            public Dictionary<string, float> SpawnOffsets = new Dictionary<string, float>();

            public string ToJson() => JsonConvert.SerializeObject(this);
        }

        protected override void LoadDefaultConfig() => _config = new Configuration();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception("config is null");
            }
            catch (Exception ex)
            {
                PrintError($"Config is invalid, using defaults. Error: {ex.Message}");
                LoadDefaultConfig();
            }

            // Merge in any built-in deployable aliases / spawn offsets the saved
            // config is missing (so new defaults appear on update without wiping
            // the user's custom ones).
            if (_config.Deployables == null) _config.Deployables = new Dictionary<string, string>();
            foreach (var kv in new Configuration().Deployables)
                if (!_config.Deployables.ContainsKey(kv.Key)) _config.Deployables[kv.Key] = kv.Value;
            if (_config.SpawnOffsets == null) _config.SpawnOffsets = new Dictionary<string, float>();
            foreach (var kv in new Configuration().SpawnOffsets)
                if (!_config.SpawnOffsets.ContainsKey(kv.Key)) _config.SpawnOffsets[kv.Key] = kv.Value;

            // v1.0.3 migration: the auto-apply layout used to be one string where
            // empty meant "off"; it is now a bool + a name, on by default. Keep a
            // hand-picked layout name; the old empty default becomes "auto-apply
            // the layout named default" (the new behaviour). The old key just
            // disappears — WriteObject rewrites the file from the config class.
            var oldAutoApply = Config["Layout: auto-apply this saved layout on boot (empty = off)"] as string;
            if (!string.IsNullOrEmpty(oldAutoApply)) _config.AutoApplyLayout = oldAutoApply;

            // v1.0.2 migration: "slots" used to point at the caboose_static slot
            // machine, which turned out to be the caboose's counter-embedded decor
            // variant — players can't mount it standalone. Swap saved configs to
            // the bandit-camp casino prefab (mountable) and drop the -0.75 visual
            // offset that only that old prefab needed. Custom values are left alone.
            const string oldSlots = "assets/content/vehicles/trains/caboose/slotmachine.caboose_static.prefab";
            if (_config.Deployables.TryGetValue("slots", out var slotsPrefab) &&
                string.Equals(slotsPrefab, oldSlots, StringComparison.OrdinalIgnoreCase))
            {
                _config.Deployables["slots"] = "assets/prefabs/misc/casino/slotmachine/slotmachine.prefab";
                if (_config.SpawnOffsets.TryGetValue("slots", out var off) && Mathf.Approximately(off, -0.75f))
                    _config.SpawnOffsets.Remove("slots");
            }

            Config.WriteObject(_config, true);
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Lifecycle

        private const string PermRenter = "apartmentcomplexunlock.renter";
        private const string PermAdmin = "apartmentcomplexunlock.admin";

        private void Init()
        {
            permission.RegisterPermission(PermRenter, this);
            permission.RegisterPermission(PermAdmin, this);
            _safeZoneDesired = _config.SafeZoneEnabled;
            MigrateDataFiles();
            Puts($"ApartmentComplexUnlock v{Version} loaded - by LowPopLabs - ko-fi.com/lowpoplabs");
        }

        // Admin-tier access: real admins, or the plugin's admin permission (so
        // owners can grant moderators these powers without auth level 2).
        private bool IsFullDeployer(BasePlayer player) =>
            player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, PermAdmin));

        private void OnServerInitialized(bool initial)
        {
            // Re-adopt outdoor placements from the data file, then immediately
            // re-harden everything (protection/grounded aren't saved by the game)
            // BEFORE decay or a stability update can eat them.
            LoadSpawnedData();
            MaintainFurniture();

            // Fires on every boot (including post-wipe), so a delayed sweep here
            // covers reloads, boots, and wipes alike.
            timer.Once(_config.SweepDelay, () => RunSweep(null));

            // After the sweep settles: re-apply the saved decor layout (idempotent
            // — skips anything already present, no-ops when nothing is saved yet).
            timer.Once(_config.SweepDelay + 5f, TryAutoApplyLayout);

            // Optional safety net: re-run periodically to re-clear respawned turrets.
            if (_config.ResweepInterval > 0f)
                timer.Every(_config.ResweepInterval, () => RunSweep(null));

            // Interactive features need the per-tick input hook and the preview
            // timer; skip both entirely when everything is disabled in config
            // (uMod: subscribe to frequent hooks only while needed).
            bool interactive = (_config.RenterDeployEnabled && (_config.RenterHoldPlacement || _config.RenterWallpaper)) || _config.ReskinEnabled;
            if (!interactive) Unsubscribe(nameof(OnPlayerInput));

            // Drives the placement/tool previews (draws only while someone is aiming).
            timer.Every(0.2f, DrawPreviews);
        }

        #endregion

        #region Sweep

        private void RunSweep(IPlayer initiator)
        {
            var monuments = FindMonuments();
            if (monuments.Count == 0)
            {
                var msg = L("SweepNoMatch", null, _config.MonumentNameFilter);
                Reply(initiator, msg);
                PrintWarning(msg);
                return;
            }

            int turrets = 0, blockVolumes = 0;
            foreach (var monument in monuments)
            {
                if (_config.RemoveAutoTurrets) turrets += RemoveTurrets(monument);
                if (_config.RemoveBuildingBlock) blockVolumes += RemoveBuildingBlockVolumes(monument);
            }

            MaintainFurniture();
            EnforceSafeZone(monuments);

            var summary = L("SweepSummary", null, monuments.Count, turrets, blockVolumes);
            Reply(initiator, summary);
            // Only log when something actually changed, so the periodic re-sweep is quiet.
            if (initiator == null && turrets + blockVolumes == 0) return;
            Puts(summary);
        }

        private int RemoveTurrets(MonumentInfo monument)
        {
            var toKill = new List<NPCAutoTurret>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var turret = ent as NPCAutoTurret;
                if (turret == null || turret.IsDestroyed) continue;
                if (!InRange(monument, turret.transform.position)) continue;
                toKill.Add(turret);
            }
            foreach (var turret in toKill) turret.Kill();
            return toKill.Count;
        }

        private int RemoveBuildingBlockVolumes(MonumentInfo monument)
        {
            int layer = LayerMask.NameToLayer(_config.BuildingBlockLayerName);
            if (layer < 0)
            {
                PrintWarning($"Building-block layer \"{_config.BuildingBlockLayerName}\" does not exist. Run `apartment.layers`.");
                return 0;
            }

            var hits = OverlapMonument(monument, 1 << layer);
            int count = 0;
            foreach (var col in hits)
            {
                if (col == null) continue;
                // Destroy the collider outright — merely disabling it gets re-enabled
                // by the game (observed 42->38->28 across sweeps). A monument respawn
                // on wipe recreates it, and OnServerInitialized re-runs the sweep.
                UnityEngine.Object.Destroy(col);
                count++;
            }
            return count;
        }

        #endregion

        #region Safe zone toggle (config default + admin command)

        // Toggles the monument's OUTER safe-zone bubble (the single TriggerSafeZone
        // on the "SafeZone" GameObject — NOT the per-room TriggerSafeZoneOverride
        // combat zones, which are a sibling type and never touched). The working
        // lever is the trigger COLLIDER: InSafeZone() reads players' trigger
        // membership, so we disable/enable the collider and then ForceUpdateTriggers
        // on nearby players so it applies without walking out and back in.
        //
        // The wanted state starts at the config's "Safe zone: keep ... ON" flag and
        // is overridden at runtime by /apsafezone; every sweep re-asserts it, which
        // is what makes an "off" setting survive the monument respawning its zone
        // on boot/wipe. A plugin reload goes back to the config value.

        [ChatCommand("apsafezone")]
        private void ChatSafeZone(BasePlayer player, string command, string[] args)
        {
            if (!IsFullDeployer(player)) { player.ChatMessage(L("AdminOnly", player)); return; }
            player.ChatMessage(SafeZoneCommand(args.Length > 0 ? args[0] : null));
        }

        [ConsoleCommand("apartment.safezone")]
        private void ConsoleSafeZone(ConsoleSystem.Arg arg)
        {
            if (!HasConsolePermission(arg)) return;
            arg.ReplyWith(SafeZoneCommand(arg.GetString(0, null)));
        }

        private string SafeZoneCommand(string action)
        {
            if (string.Equals(action, "on", StringComparison.OrdinalIgnoreCase)) return SetSafeZone(true);
            if (string.Equals(action, "off", StringComparison.OrdinalIgnoreCase)) return SetSafeZone(false);
            return SafeZoneStatus();
        }

        // Set when the plugin turned the safe zone off, so Unload can revert
        // (uMod: revert world modifications on unload when possible).
        private bool _safeZoneDisabledByPlugin;

        // The state we want the zone to be in: config on load, /apsafezone after.
        private bool _safeZoneDesired = true;

        // A zone switched "off" is SHRUNK to a 1 cm sphere, not disabled. Unity's
        // Collider.ClosestPoint on a disabled collider returns the query point itself,
        // and vanilla's satellite crash-site search (CrashSpotSearch.IsInSafeZone)
        // calls exactly that on every TriggerSafeZone without checking enabled — so a
        // disabled zone read as "within clearance" everywhere on the map and every
        // thruster press failed with "Unsuitable location". An enabled 1 cm sphere
        // keeps players out, stays discoverable for the re-assert sweep, and gives
        // vanilla a real distance. Original radii are kept so "on" restores them;
        // Unload restores before a reload, and a server boot respawns the prefab zone.
        private const float OffRadius = 0.01f;
        private readonly Dictionary<TriggerSafeZone, float> _zoneRadius = new Dictionary<TriggerSafeZone, float>();

        private static bool ZoneIsOn(Collider col)
        {
            if (col == null || !col.enabled) return false;
            var sphere = col as SphereCollider;
            return sphere == null || sphere.radius > OffRadius * 10f;
        }

        private void ApplyZoneState(TriggerSafeZone zone, Collider col, bool enable)
        {
            var sphere = col as SphereCollider;
            if (sphere == null) { col.enabled = enable; return; }   // not a sphere: old behaviour
            col.enabled = true;
            if (enable)
            {
                float orig;
                if (_zoneRadius.TryGetValue(zone, out orig))
                {
                    sphere.radius = orig;
                    _zoneRadius.Remove(zone);
                }
            }
            else
            {
                if (!_zoneRadius.ContainsKey(zone) && sphere.radius > OffRadius * 10f)
                    _zoneRadius[zone] = sphere.radius;
                sphere.radius = OffRadius;
            }
        }

        // Re-assert the wanted state on every sweep. Only acts on a real mismatch,
        // so the periodic sweep doesn't churn colliders or refresh players for
        // nothing; a monument that respawned its zone gets switched back off here.
        private void EnforceSafeZone(List<MonumentInfo> monuments)
        {
            int zones = 0, active = 0;
            foreach (var monument in monuments) CountSafeZones(monument, ref zones, ref active);
            if (zones == 0) return;
            if (_safeZoneDesired ? active == zones : active == 0) return;

            Puts(SetSafeZone(_safeZoneDesired));
        }

        private void CountSafeZones(MonumentInfo monument, ref int zones, ref int active)
        {
            float r = ScanRadius(monument);
            foreach (var zone in TriggerSafeZone.allSafeZones)
            {
                if (zone == null || zone.GetType() != typeof(TriggerSafeZone)) continue;
                if (Vector3.Distance(monument.transform.position, zone.transform.position) > r) continue;
                zones++;
                var col = zone.triggerCollider != null ? zone.triggerCollider : zone.GetComponent<Collider>();
                if (ZoneIsOn(col)) active++;
            }
        }

        private string SetSafeZone(bool enable)
        {
            _safeZoneDisabledByPlugin = !enable;
            _safeZoneDesired = enable;
            var monuments = FindMonuments();
            if (monuments.Count == 0) return L("NoMonument", null, _config.MonumentNameFilter);

            int touched = 0;
            foreach (var monument in monuments)
            {
                float r = ScanRadius(monument);

                foreach (var zone in TriggerSafeZone.allSafeZones.ToList())
                {
                    if (zone == null || zone.GetType() != typeof(TriggerSafeZone)) continue;
                    if (Vector3.Distance(monument.transform.position, zone.transform.position) > r) continue;
                    var col = zone.triggerCollider != null ? zone.triggerCollider : zone.GetComponent<Collider>();
                    if (col == null) continue;
                    ApplyZoneState(zone, col, enable);
                    touched++;
                }

                // Legacy fallback: an old plugin version Destroy()'d the component
                // itself (collider survives, enabled). Recreate it on the "SafeZone"
                // GameObject. Awake() only ORs in 0x200, so make sure the player
                // layer is in the interest mask.
                if (enable && touched == 0)
                {
                    foreach (var col in Physics.OverlapSphere(monument.transform.position, r, ~0, QueryTriggerInteraction.Collide))
                    {
                        if (col == null || col.gameObject.name != "SafeZone") continue;
                        if (col.GetComponent<TriggerSafeZone>() != null) continue;
                        var zone = col.gameObject.AddComponent<TriggerSafeZone>();
                        zone.InterestLayers = (int)zone.InterestLayers | (1 << LayerMask.NameToLayer("Player (Server)"));
                        col.enabled = true;
                        touched++;
                    }
                }

                ForceRefreshPlayers(monument);
            }

            if (touched == 0) return L("SafeZoneNone", null);
            return L(enable ? "SafeZoneOn" : "SafeZoneOff", null, touched);
        }

        private string SafeZoneStatus()
        {
            var monuments = FindMonuments();
            if (monuments.Count == 0) return L("NoMonument", null, _config.MonumentNameFilter);

            var sb = new StringBuilder();
            foreach (var monument in monuments)
            {
                int zones = 0, active = 0;
                CountSafeZones(monument, ref zones, ref active);
                sb.AppendLine(L("SafeZoneStatusLine", null, monument.name, zones, active));
            }
            sb.Append(L("SafeZoneUsage", null));
            return sb.ToString();
        }

        // A just-toggled zone doesn't apply to players already standing inside it
        // (no OnTriggerEnter/Exit fires) — force their trigger memberships fresh.
        private void ForceRefreshPlayers(MonumentInfo monument)
        {
            float r = ScanRadius(monument) + 20f;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (Vector3.Distance(monument.transform.position, player.transform.position) > r) continue;
                player.ForceUpdateTriggers();
            }
        }

        #endregion

        #region Detection helpers

        private List<MonumentInfo> FindMonuments()
        {
            var result = new List<MonumentInfo>();
            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null) return result;

            foreach (var monument in monuments)
            {
                if (monument == null || string.IsNullOrEmpty(monument.name)) continue;
                if (monument.name.IndexOf(_config.MonumentNameFilter, StringComparison.OrdinalIgnoreCase) >= 0)
                    result.Add(monument);
            }
            return result;
        }

        private bool InRange(MonumentInfo monument, Vector3 worldPos)
        {
            if (_config.DetectionRadius > 0f)
                return Vector3.Distance(monument.transform.position, worldPos) <= _config.DetectionRadius;

            Vector3 local = monument.transform.InverseTransformPoint(worldPos);
            Bounds b = monument.Bounds;
            b.Expand(_config.BoundsPadding * 2f);
            return b.Contains(local);
        }

        private Collider[] OverlapMonument(MonumentInfo monument, int mask)
        {
            if (_config.DetectionRadius > 0f)
                return Physics.OverlapSphere(monument.transform.position, _config.DetectionRadius, mask, QueryTriggerInteraction.Collide);

            Vector3 worldCenter = monument.transform.TransformPoint(monument.Bounds.center);
            Vector3 halfExtents = monument.Bounds.extents + Vector3.one * _config.BoundsPadding;
            return Physics.OverlapBox(worldCenter, halfExtents, monument.transform.rotation, mask, QueryTriggerInteraction.Collide);
        }

        private float ScanRadius(MonumentInfo monument) =>
            _config.DetectionRadius > 0f
                ? _config.DetectionRadius
                : Mathf.Max(monument.Bounds.size.x, monument.Bounds.size.z) + _config.BoundsPadding;

        // Cached for per-tick checks (monuments are fixed for the map's lifetime).
        private List<MonumentInfo> _monumentsCache;

        private bool NearApartmentMonument(Vector3 pos)
        {
            if (_monumentsCache == null) _monumentsCache = FindMonuments();
            foreach (var m in _monumentsCache)
                if (m != null && Vector3.Distance(m.transform.position, pos) <= ScanRadius(m)) return true;
            return false;
        }

        #endregion

        #region Diagnostic commands

        [ConsoleCommand("apartment.apply")]
        private void CmdApply(ConsoleSystem.Arg arg)
        {
            if (!HasConsolePermission(arg)) return;
            RunSweep(arg.Player()?.IPlayer);
        }

        [ConsoleCommand("apartment.scan")]
        private void CmdScan(ConsoleSystem.Arg arg)
        {
            if (!HasConsolePermission(arg)) return;
            var sb = new StringBuilder();

            var monuments = TerrainMeta.Path?.Monuments;
            if (monuments == null || monuments.Count == 0)
            {
                arg.ReplyWith(L("NoMonumentsOnMap", arg.Player()));
                return;
            }

            var matched = monuments.Where(m => m != null && !string.IsNullOrEmpty(m.name) &&
                m.name.IndexOf(_config.MonumentNameFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            sb.AppendLine($"--- filter=\"{_config.MonumentNameFilter}\" matched {matched.Count} of {monuments.Count} monuments ---");
            foreach (var m in matched)
            {
                sb.AppendLine($"[MATCH] {m.name}");
                AppendMonumentContents(sb, m);
            }
            if (matched.Count == 0)
                sb.AppendLine("No matches — check the filter against the monument names from the previous scan.");

            arg.ReplyWith(sb.ToString());
        }

        private void AppendMonumentContents(StringBuilder sb, MonumentInfo monument)
        {
            Vector3 pos = monument.transform.position;
            float r = ScanRadius(monument);
            sb.AppendLine($"    pos={pos}  boundsSize={monument.Bounds.size}  scanRadius={r:0}m");

            // Entity-type histogram within radius (reveals the real turret class, etc.)
            var hist = new Dictionary<string, int>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                if (Vector3.Distance(pos, be.transform.position) > r) continue;
                var t = be.GetType().Name;
                hist[t] = hist.TryGetValue(t, out var c) ? c + 1 : 1;
            }
            sb.AppendLine($"    entities within {r:0}m: {hist.Values.Sum()} total across {hist.Count} types (top 40):");
            foreach (var kv in hist.OrderByDescending(k => k.Value).Take(40))
                sb.AppendLine($"      {kv.Value,4}  {kv.Key}");

            // Safe zones within radius (allSafeZones static registry — never
            // FindObjectsOfType, per the uMod performance guidelines)
            var zones = new List<TriggerSafeZone>();
            foreach (var z in TriggerSafeZone.allSafeZones)
                if (z != null && Vector3.Distance(pos, z.transform.position) <= r) zones.Add(z);
            sb.AppendLine($"    TriggerSafeZone within {r:0}m: {zones.Count}");
            foreach (var z in zones.Take(10))
                sb.AppendLine($"      dist={Vector3.Distance(pos, z.transform.position):0}m  name={z.gameObject.name}");

            // Trigger colliders grouped by physics layer (reveals the building-block layer)
            var byLayer = new Dictionary<int, int>();
            var sample = new Dictionary<int, string>();
            foreach (var col in Physics.OverlapSphere(pos, r, ~0, QueryTriggerInteraction.Collide))
            {
                if (col == null || !col.isTrigger) continue;
                int colLayer = col.gameObject.layer;
                byLayer[colLayer] = byLayer.TryGetValue(colLayer, out var c) ? c + 1 : 1;
                if (!sample.ContainsKey(colLayer)) sample[colLayer] = col.gameObject.name;
            }
            sb.AppendLine($"    trigger colliders within {r:0}m by layer:");
            foreach (var kv in byLayer.OrderByDescending(k => k.Value))
            {
                string lname = LayerMask.LayerToName(kv.Key);
                sb.AppendLine($"      [{kv.Key,2}] {(string.IsNullOrEmpty(lname) ? "(unnamed)" : lname)}: {kv.Value}  e.g. \"{sample[kv.Key]}\"");
            }

            int bbLayer = LayerMask.NameToLayer(_config.BuildingBlockLayerName);
            if (bbLayer >= 0)
            {
                int n = Physics.OverlapSphere(pos, r, 1 << bbLayer, QueryTriggerInteraction.Collide).Count(c => c != null && c.isTrigger);
                sb.AppendLine($"    colliders on \"{_config.BuildingBlockLayerName}\" (layer {bbLayer}) within {r:0}m: {n}");
            }
            else
            {
                sb.AppendLine($"    configured building-block layer \"{_config.BuildingBlockLayerName}\" does not exist");
            }
        }

        [ConsoleCommand("apartment.probe")]
        private void CmdProbe(ConsoleSystem.Arg arg)
        {
            if (!HasConsolePermission(arg)) return;
            string filter = arg.GetString(0, "safe");

            var monuments = FindMonuments();
            if (monuments.Count == 0) { arg.ReplyWith(L("NoMonument", arg.Player(), _config.MonumentNameFilter)); return; }

            var sb = new StringBuilder($"--- probe name~\"{filter}\" ---\n");
            foreach (var m in monuments)
            {
                Vector3 pos = m.transform.position;
                float r = ScanRadius(m);

                // Group matching objects by name so we can see how MANY of each there are.
                var groups = new Dictionary<string, int>();
                var rep = new Dictionary<string, Collider>();
                foreach (var col in Physics.OverlapSphere(pos, r, ~0, QueryTriggerInteraction.Collide))
                {
                    if (col == null) continue;
                    var name = col.gameObject.name;
                    if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    groups[name] = groups.TryGetValue(name, out var c) ? c + 1 : 1;
                    if (!rep.ContainsKey(name)) rep[name] = col;
                }

                if (groups.Count == 0)
                {
                    sb.AppendLine($"  (nothing named ~\"{filter}\" within {r:0}m of {m.name})");
                    continue;
                }

                foreach (var kv in groups.OrderByDescending(k => k.Value).Take(20))
                {
                    var col = rep[kv.Key];
                    var go = col.gameObject;
                    var comps = go.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
                    sb.AppendLine($"  \"{kv.Key}\" x{kv.Value}  layer={LayerMask.LayerToName(go.layer)}({go.layer})  trigger={col.isTrigger}  " +
                                  $"dist={Vector3.Distance(pos, go.transform.position):0}m  boundsSize={col.bounds.size}");
                    sb.AppendLine($"      components: {string.Join(", ", comps)}");
                    var parent = go.transform.parent;
                    if (parent != null)
                    {
                        var pcomps = parent.GetComponents<Component>().Where(c => c != null).Select(c => c.GetType().Name);
                        sb.AppendLine($"      parent \"{parent.name}\": {string.Join(", ", pcomps)}");
                    }
                }
            }
            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("apartment.whyblocked")]
        private void CmdWhyBlocked(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGameWhyBlocked", null)); return; }

            Vector3 pos = player.transform.position;
            var sb = new StringBuilder($"--- why-blocked at {pos} ---\n");

            // 1) Prevent-building colliders right here (did they respawn after apply?)
            int bbLayer = LayerMask.NameToLayer(_config.BuildingBlockLayerName);
            if (bbLayer >= 0)
            {
                var pb = Physics.OverlapSphere(pos, 10f, 1 << bbLayer, QueryTriggerInteraction.Collide);
                sb.AppendLine($"colliders on \"{_config.BuildingBlockLayerName}\" within 10m: {pb.Length}");
                foreach (var c in pb.Take(8))
                    sb.AppendLine($"   \"{c.gameObject.name}\"  dist={Vector3.Distance(pos, c.transform.position):0.0}m");
            }
            else sb.AppendLine($"layer \"{_config.BuildingBlockLayerName}\" not found");

            // 2) Every TriggerBase overlapping this spot, by type (reveals a PreventBuilding trigger)
            var trigTypes = new Dictionary<string, int>();
            foreach (var c in Physics.OverlapSphere(pos, 3f, ~0, QueryTriggerInteraction.Collide))
            {
                var t = c.GetComponentInParent<TriggerBase>();
                if (t == null) continue;
                var n = t.GetType().Name;
                trigTypes[n] = trigTypes.TryGetValue(n, out var x) ? x + 1 : 1;
            }
            sb.AppendLine($"TriggerBase overlapping within 3m: {(trigTypes.Count == 0 ? "none" : string.Join(", ", trigTypes.Select(k => $"{k.Key}={k.Value}")))}");

            // 3) Terrain topology bits under this spot (monuments paint no-build topology)
            try
            {
                int topo = TerrainMeta.TopologyMap != null ? TerrainMeta.TopologyMap.GetTopology(pos) : 0;
                var bits = new List<string>();
                foreach (var name in System.Enum.GetNames(typeof(TerrainTopology.Enum)))
                {
                    int val = (int)System.Enum.Parse(typeof(TerrainTopology.Enum), name);
                    if (val != 0 && (topo & val) == val) bits.Add(name);
                }
                sb.AppendLine($"topology bits: {(bits.Count == 0 ? "none" : string.Join(", ", bits))}");
            }
            catch (Exception e) { sb.AppendLine($"topology: n/a ({e.Message})"); }

            // 4) The actual placement gate: BasePlayer.CanBuild() (privilege/entity based)
            sb.AppendLine($"player.CanBuild() = {player.CanBuild()}");

            // 5) Any building-privilege / apartment / blocking entities near the player
            var blockers = new List<string>();
            foreach (var ent in BaseNetworkable.serverEntities)
            {
                var be = ent as BaseEntity;
                if (be == null || be.IsDestroyed) continue;
                if (Vector3.Distance(pos, be.transform.position) > 40f) continue;
                var n = be.GetType().Name;
                if (n.IndexOf("Priv", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    n.IndexOf("Apartment", StringComparison.OrdinalIgnoreCase) >= 0)
                    blockers.Add($"{n}@{Vector3.Distance(pos, be.transform.position):0}m(owner={be.OwnerID})");
            }
            sb.AppendLine($"privilege/apartment entities within 40m: {(blockers.Count == 0 ? "none" : string.Join(", ", blockers.Take(10)))}");

            // 6) The game's own reason for the LAST failed placement. Attempt to place
            //    a foundation (and fail) immediately before running this to populate it.
            try
            {
                sb.AppendLine($"last placement error: token='{Construction.lastPlacementError.token}' " +
                              $"en='{Construction.lastPlacementError.english}' debug='{Construction.lastPlacementErrorDebug}'");
            }
            catch (Exception e) { sb.AppendLine($"last placement error: n/a ({e.Message})"); }

            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("apartment.layers")]
        private void CmdLayers(ConsoleSystem.Arg arg)
        {
            if (!HasConsolePermission(arg)) return;
            var sb = new StringBuilder("--- Named Unity layers ---\n");
            for (int i = 0; i < 32; i++)
            {
                var name = LayerMask.LayerToName(i);
                if (!string.IsNullOrEmpty(name))
                    sb.AppendLine($"  {i,2}: {name}");
            }
            arg.ReplyWith(sb.ToString());
        }

        private bool HasConsolePermission(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return true;   // server console / RCON
            if (IsFullDeployer(player)) return true;
            arg.ReplyWith(L("AdminOnlyConsole", arg.Player()));
            return false;
        }

        private void Reply(IPlayer player, string message)
        {
            if (player != null) player.Reply(message);
        }

        #endregion

        #region Deploy (server-side placement — bypasses the client build block)

        [ChatCommand("aphelp")]
        private void ChatHelp(BasePlayer player, string command, string[] args) => ShowHelpUi(player, 0);

        [ConsoleCommand("apartment.helpui")]
        private void ConsoleHelpUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) ShowHelpUi(player, arg.GetInt(0, 0));
        }

        private const string HelpUiName = "ApartmentHelpUI";

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null) continue;
                CuiHelper.DestroyUi(p, HelpUiName);
                CuiHelper.DestroyUi(p, SkinUiName);
            }
            // Revert the one live world modification we can cleanly revert.
            if (_safeZoneDisabledByPlugin) SetSafeZone(true);
        }

        // Scrollable help window: dark centered panel, ✕ to close (client-side,
        // no server round-trip), mouse-wheel scroll via the CUI ScrollView.
        // Tabs: 0 = Guide, 1 = Item names.
        private void ShowHelpUi(BasePlayer player, int tab)
        {
            CuiHelper.DestroyUi(player, HelpUiName);
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.09 0.10 0.11 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-330 -250", OffsetMax = "330 250" },
                CursorEnabled = true
            }, "Overlay", HelpUiName);

            ui.Add(new CuiLabel
            {
                Text = { Text = L("HelpTitle", player), FontSize = 17, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "16 -40", OffsetMax = "-48 -6" }
            }, HelpUiName);

            ui.Add(new CuiButton
            {
                Button = { Color = "0.75 0.22 0.17 1", Close = HelpUiName },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-38 -38", OffsetMax = "-8 -8" }
            }, HelpUiName);

            // Tab bar
            AddHelpTab(ui, L("TabGuide", player), 0, tab == 0, 16, 136);
            AddHelpTab(ui, L("TabItems", player), 1, tab == 1, 142, 282);

            var sections = tab == 1 ? ItemSections() : HelpSections(IsFullDeployer(player));
            float contentHeight = 8f;
            foreach (var s in sections) contentHeight += s.Height + 10f;

            ui.Add(new CuiElement
            {
                Name = HelpUiName + ".Scroll",
                Parent = HelpUiName,
                Components =
                {
                    new CuiScrollViewComponent
                    {
                        Vertical = true,
                        Horizontal = false,
                        MovementType = ScrollRect.MovementType.Clamped,
                        ScrollSensitivity = 25f,
                        ContentTransform = new CuiRectTransform
                        {
                            AnchorMin = "0 1", AnchorMax = "1 1",
                            OffsetMin = $"0 -{contentHeight:0}", OffsetMax = "0 0"
                        },
                        VerticalScrollbar = new CuiScrollbar { Size = 8f, AutoHide = false }
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 10", OffsetMax = "-10 -84" }
                }
            });

            float y = 8f;
            int i = 0;
            foreach (var s in sections)
            {
                ui.Add(new CuiElement
                {
                    Name = $"{HelpUiName}.S{i++}",
                    Parent = HelpUiName + ".Scroll",
                    Components =
                    {
                        new CuiTextComponent { Text = s.Text, FontSize = 13, Align = TextAnchor.UpperLeft, Color = "0.92 0.92 0.92 1" },
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = $"14 -{y + s.Height:0}", OffsetMax = $"-26 -{y:0}" }
                    }
                });
                y += s.Height + 10f;
            }

            CuiHelper.AddUi(player, ui);
        }

        private void AddHelpTab(CuiElementContainer ui, string label, int tab, bool active, float x0, float x1)
        {
            ui.Add(new CuiButton
            {
                Button = { Color = active ? "0.30 0.42 0.22 1" : "0.20 0.21 0.22 1", Command = active ? "" : $"apartment.helpui {tab}" },
                Text = { Text = label, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = active ? "1 1 1 1" : "0.75 0.75 0.75 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{x0} -76", OffsetMax = $"{x1} -48" }
            }, HelpUiName);
        }

        private class HelpSection { public string Text; public float Height; }

        private List<HelpSection> HelpSections(bool admin)
        {
            var list = new List<HelpSection>
            {
                new HelpSection { Height = 30, Text = "<b><color=#a3e635><size=15>RENTER GUIDE</size></color></b>  — you must currently rent a room; everything works only inside YOUR room." },
                new HelpSection { Height = 78, Text = "<b><color=#a3e635>Decorate — hold to place</color></b>\nHold any deployable item (box, rug, furnace, chicken coop...) and a green outline follows your crosshair.\n•  LEFT-CLICK places it (uses the item)      •  R rotates 45°\nA red outline means you're outside your room or the spot is invalid." },
                new HelpSection { Height = 60, Text = "<b><color=#a3e635>Remove</color></b>\n/apundeploy while looking at your object — the item is refunded.\n<color=#fca5a5>Warning:</color> checking out (or eviction) destroys placed objects with NO refund — undeploy first!" },
                new HelpSection { Height = 96, Text = "<b><color=#a3e635>Industrial automation</color></b>\nHold the PIPE TOOL:  click a source component, then click the target — a real pipe connects them.\nRIGHT-CLICK a component = clear its pipes.  R = cancel selection.\nStorage adaptor: hold it + click a container.   Crafter: hold it + click a workbench.\nConveyor: place free-standing — it has its own filter and on/off switch in its UI." },
                new HelpSection { Height = 64, Text = "<b><color=#a3e635>Wallpaper</color></b>\nHold the WALLPAPER TOOL and pick a design in its own menu (right-click).\nThen click a wall — a papered panel appears flush against it. Click a panel again to re-paper.\n/apundeploy removes the panel." },
                new HelpSection { Height = 58, Text = "<b><color=#a3e635>Electricity</color></b>\nAutomatic — fridges, lights, heaters, conveyors, crafters just work; no wiring needed (or possible).\nBatteries, switches, timers and solar panels are decoration only here." },
                new HelpSection { Height = 48, Text = "<b><color=#a3e635>Reskin</color></b>\nHold the SPRAY CAN and click one of your placed objects — a skin picker opens.\nClick a skin to apply it instantly (you must own the skin)." },
                new HelpSection { Height = 40, Text = $"<b><color=#a3e635>Limits</color></b>\nMax {_config.RenterMaxPerRoom} placed objects per room. Objects persist across restarts." },
                new HelpSection { Height = 76, Text = "<b><color=#a3e635>Command alternatives</color></b>\n/apdeploy <item>  →  /aprotate [degrees]  →  /applace   (or /apcancel)\n/appipe on source, then /appipe on target   (/appipe clear, /appipe cancel)\n/aphelp — this window." }
            };

            list.Add(new HelpSection { Height = 44, Text = "<b><color=#a3e635>Fine-tune position</color></b>\n/apnudge [meters] while aiming at your object — moves it up/down (e.g. /apnudge -0.75). Fixes anything that spawns floating." });

            if (admin)
            {
                list.Add(new HelpSection { Height = 30, Text = "<b><color=#fb923c><size=15>ADMIN</size></color></b>" });
                list.Add(new HelpSection { Height = 110, Text = "<b><color=#fb923c>Deploy anywhere</color></b>\nHOLD any deployable item anywhere in the monument — same click/R flow as renters (item consumed).\nMounting works outside rooms too (adaptor on a road box, crafter on a courtyard workbench).\n/apdeploy also accepts aliases and raw assets/ prefab paths — statics are admin-only:\nrecycler, carlift, slots (slot machine), blackjack, caboose (whole gambling wagon, frozen off-rails).\nOutside-room placements are decay-proof, persist, and are tracked in the plugin data file.\nConfig \"Deploy: spawn height offset\" fixes prefabs whose visuals spawn floating (none needed by default)." });
                list.Add(new HelpSection { Height = 92, Text = "<b><color=#fb923c>Layouts — save & re-apply your decor</color></b>\n/aplayout save [name] — snapshot all outdoor placements (positions stored relative to the monument).\n/aplayout apply [name] — re-create them, even after a wipe when the monument moves. Skips existing.\n/aplayout list | delete [name].\nWith no name the layout is saved as \"default\", and that one is re-applied automatically on every boot." });
                list.Add(new HelpSection { Height = 48, Text = "<b><color=#fb923c>Remove</color></b>\n/apundeploy removes ANY plugin-placed entity — vanilla furniture and player bases are never touched. No refunds in admin mode." });
                list.Add(new HelpSection { Height = 58, Text = "<b><color=#fb923c>Safe zone</color></b>\n/apsafezone on | off | status — toggles the outer bubble (per-room combat zones untouched).\nFor a permanent setting use the config: \"Safe zone: keep the monument's outer safe zone ON\" = false. A reload restores the config value." });
                list.Add(new HelpSection { Height = 80, Text = "<b><color=#fb923c>Console</color></b>\napartment.apply (sweep now), apartment.scan, apartment.probe <name>, apartment.whyblocked, apartment.layers, apartment.help\nTesting: apartment.rentroom <num>, apartment.checkoutroom, apartment.rentallrooms, apartment.checkoutallrooms" });
            }
            return list;
        }

        // "ITEM NAMES" tab — the DEPLOYABLES.txt reference, baked in.
        private List<HelpSection> ItemSections()
        {
            var list = new List<HelpSection>();
            void Cat(string title, params string[] lines)
            {
                list.Add(new HelpSection
                {
                    Height = 26 + lines.Length * 17,
                    Text = $"<b><color=#7dd3fc>{title}</color></b>\n" + string.Join("\n", lines)
                });
            }

            Cat("HOW TO USE",
                "Hold the item in your room and left-click, or /apdeploy <name>. All names below are exact.");
            Cat("ALIASES",
                "coop / coup — chicken coop        planter — large planter        trough — horse trough",
                "Admin-only: recycler, carlift, slots, blackjack, caboose (gambling wagon)");
            Cat("STORAGE",
                "box.wooden   box.wooden.large   coffin.storage   locker   shelves",
                "fridge   \"mini fridge\" (name really has a space)   dropbox   mailbox",
                "stash.small   vending.machine   storage_barrel_a / _b / _c   storage.monitor");
            Cat("FURNITURE / COMFORT",
                "bed   sleepingbag   table   cardtable (poker!)   chair   sofa   sofa.pattern",
                "beachchair   beachtable   rockingchair   secretlabchair   rug   rug.bear",
                "pookie.bear   skull.trophy   huntingtrophysmall / large   fishtrophy");
            Cat("WORK / CRAFTING",
                "workbench1 / 2 / 3   box.repair.bench   research.table",
                "mixingtable   cookingworkbench   composter");
            Cat("COOKING / HEAT",
                "furnace   electric.furnace   bbq   campfire   fireplace.stone",
                "hobobarrel   cursedcauldron   small.oil.refinery");
            Cat("LIGHTING (electric = auto-powered)",
                "ceilinglight   electric.simplelight   electric.fluorescentlight (.ceiling)",
                "electric.tablelight   electric.flasherlight   electric.sirenlight",
                "electric.spotlight (.tripod)   searchlight   lantern   tunalight",
                "chineselantern / chineselanternwhite   jackolantern.happy / .angry",
                "smallcandles   largecandles   strobelight   soundlight   laserlight",
                "electric.fairylights   electric.bulbstringlights");
            Cat("ELECTRICAL APPLIANCES (auto-powered)",
                "electric.heater   telephone   boombox   connected.speaker   spookyspeaker");
            Cat("INDUSTRIAL (connect with the pipe tool)",
                "storageadaptor — hold + click a container",
                "industrial.crafter — hold + click a workbench",
                "industrial.conveyor (filter + on/off in its UI)   industrial.splitter   industrial.combiner",
                "industrial.wall.light (.red / .green / .blue)");
            Cat("FARMING",
                "planter.large / .small / .triangle   bathtub.planter   minecart.planter   rail.road.planter",
                "chickencoop   beehive   hitchtroughcombo   composter",
                "water.barrel   water.catcher.small / .large   water.purifier   powered.water.purifier",
                "(electric.sprinkler needs water pipes — decorative only here)");
            Cat("SIGNS / PICTURE FRAMES (paintable)",
                "sign.wooden.small / .medium / .large / .huge",
                "sign.pictureframe.portrait / .landscape / .tall / .xl / .xxl",
                "photoframe.portrait / .landscape / .large   sign.artistcanvas.xs ... .xxl",
                "sign.neon.125x125 / .125x215 / .xl (+ .animated)   sign.hanging (.ornate)",
                "woodframe / goldframe / scrapframe (.small ... .xxl, .standing)",
                "window.paintable   paintabletarget.reactive");
            Cat("INSTRUMENTS / FUN",
                "piano   drumkit   xylophone   arcade.machine.chippy   snowman   sled.xmas");
            Cat("SEASONAL / DECOR",
                "xmas.tree   xmas.lightstring (.advanced)   xmas.present.small / .medium / .large",
                "skylantern (.green / .orange / .purple / .red)",
                "skullspikes   skulldoorknocker   skull_fire_pit");
            Cat("NOT USEFUL HERE",
                "Batteries / switches / timers / solar — wiring can't exist here (decoration only).",
                "wall.frame.* (garage door, netting, shopfront) — need real building frames.");

            return list;
        }

        [ConsoleCommand("apartment.help")]
        private void ConsoleHelp(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            bool admin = player == null || IsFullDeployer(player);
            arg.ReplyWith(RenterHelp() + (admin ? "\n\n" + AdminHelp() : ""));
        }

        private string RenterHelp() =>
            "<size=16>Apartment Complex — renter commands</size>\n" +
            "(You must currently RENT a room. Everything works only inside YOUR room.)\n" +
            "<color=#9f6>Decorate:</color> HOLD a deployable item (box, rug, furnace...) — green ghost appears. LEFT-CLICK places (uses the item), R rotates. Red ghost = outside your room.\n" +
            "<color=#9f6>Remove:</color> /apundeploy while looking at your object — item refunded. (Checkout/eviction destroys placed items WITHOUT refund!)\n" +
            "<color=#9f6>Industrial:</color> hold the PIPE TOOL: click source, then click target = pipe. Right-click a component = clear its pipes. R = cancel. Adaptors: hold one + click a container. Crafter: hold + click a workbench.\n" +
            "<color=#9f6>Wallpaper:</color> hold the WALLPAPER TOOL, pick a design in its menu, click a wall = wallpaper panel. Click the panel again = change design.\n" +
            "<color=#9f6>Power:</color> automatic — fridges, lights, heaters, conveyors just work (no wires needed or possible).\n" +
            $"<color=#9f6>Limit:</color> {_config.RenterMaxPerRoom} placed objects per room.\n" +
            "<color=#9f6>Command flow (optional):</color> /apdeploy <item> -> /aprotate [deg] -> /applace | /apcancel. Pipes: /appipe, /appipe clear, /appipe cancel.";

        private string AdminHelp() =>
            "<size=16>Apartment Complex — admin commands</size>\n" +
            "<color=#f96>Deploy anywhere:</color> same commands + hold flow, anywhere in the monument. Aliases (coop, planter, trough...) and assets/ prefab paths work — statics: recycler, carlift. Outside-room placements are decay-proof and persist.\n" +
            "<color=#f96>Remove:</color> /apundeploy removes any plugin-placed entity (OwnerID != 0), never vanilla furniture. No refunds in admin mode.\n" +
            "<color=#f96>Safe zone:</color> /apsafezone on|off|status — toggles the outer bubble (rooms unaffected). Permanent setting: config \"Safe zone: keep the monument's outer safe zone ON\".\n" +
            "<color=#f96>Layouts:</color> /aplayout save|apply|list|delete [name] — the \"default\" layout (saved by /aplayout save) is re-applied on every boot.\n" +
            "<color=#f96>Sweep:</color> apartment.apply (console) — run the turret sweep now. Auto: on boot + every re-sweep interval.\n" +
            "<color=#f96>Diagnostics (console):</color> apartment.scan, apartment.probe <name>, apartment.whyblocked, apartment.layers.\n" +
            "<color=#f96>Testing (game convars):</color> apartment.rentroom <num>, apartment.checkoutroom, apartment.rentallrooms, apartment.checkoutallrooms.\n" +
            "<color=#f96>Reference:</color> DEPLOYABLES.txt in the plugin repo lists item names; /aphelp shows this.";

        [ChatCommand("apdeploy")]
        private void ChatDeploy(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out var renter, out var err)) { player.ChatMessage(err); return; }
            player.ChatMessage(StartPreview(player, args.Length > 0 ? args[0] : null, renter));
        }

        [ChatCommand("applace")]
        private void ChatPlace(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out _, out var err)) { player.ChatMessage(err); return; }
            player.ChatMessage(DoPlace(player));
        }

        [ChatCommand("aprotate")]
        private void ChatRotate(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out _, out var err)) { player.ChatMessage(err); return; }
            player.ChatMessage(DoRotate(player, args.Length > 0 ? args[0] : null));
        }

        [ChatCommand("apcancel")]
        private void ChatCancel(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out _, out var err)) { player.ChatMessage(err); return; }
            player.ChatMessage(CancelPreview(player));
        }

        [ChatCommand("apnudge")]
        private void ChatNudge(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out var renter, out var err)) { player.ChatMessage(err); return; }
            float amount = -0.1f;
            if (args.Length > 0 && !float.TryParse(args[0], out amount))
            {
                player.ChatMessage(L("NudgeUsage", player));
                return;
            }
            amount = Mathf.Clamp(amount, -5f, 5f);

            var ent = AimedOwnedEntity(player, renter);
            if (ent == null) { player.ChatMessage(L("NudgeNoTarget", player)); return; }

            ent.transform.position += new Vector3(0f, amount, 0f);
            ent.SendNetworkUpdateImmediate();
            player.ChatMessage(L("NudgeMoved", player, ent.ShortPrefabName, L(amount >= 0f ? "NudgeUp" : "NudgeDown", player), Mathf.Abs(amount), ent.transform.position.y));
        }

        [ChatCommand("apundeploy")]
        private void ChatUndeploy(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out var renter, out var err)) { player.ChatMessage(err); return; }
            player.ChatMessage(DoUndeploy(player, renter));
        }

        [ConsoleCommand("apartment.deploy")]
        private void ConsoleDeploy(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGameAim", null)); return; }
            if (!TryGetDeployMode(player, out var renter, out var err)) { arg.ReplyWith(err); return; }
            arg.ReplyWith(StartPreview(player, arg.GetString(0, null), renter));
        }

        [ConsoleCommand("apartment.place")]
        private void ConsolePlace(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGame", null)); return; }
            if (!TryGetDeployMode(player, out _, out var err)) { arg.ReplyWith(err); return; }
            arg.ReplyWith(DoPlace(player));
        }

        [ConsoleCommand("apartment.rotate")]
        private void ConsoleRotate(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGame", null)); return; }
            if (!TryGetDeployMode(player, out _, out var err)) { arg.ReplyWith(err); return; }
            arg.ReplyWith(DoRotate(player, arg.GetString(0, null)));
        }

        [ConsoleCommand("apartment.cancel")]
        private void ConsoleCancel(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGame", null)); return; }
            if (!TryGetDeployMode(player, out _, out var err)) { arg.ReplyWith(err); return; }
            arg.ReplyWith(CancelPreview(player));
        }

        [ConsoleCommand("apartment.undeploy")]
        private void ConsoleUndeploy(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGame", null)); return; }
            if (!TryGetDeployMode(player, out var renter, out var err)) { arg.ReplyWith(err); return; }
            arg.ReplyWith(DoUndeploy(player, renter));
        }

        // Full mode (admins, or everyone when DeployAdminOnly=false): place anything
        // anywhere. Renter mode: place deployable ITEMS inside their OWN rented room.
        private bool TryGetDeployMode(BasePlayer player, out bool renter, out string error)
        {
            renter = false;
            error = null;
            if (player == null) { error = L("InGame", null); return false; }
            if (!_config.DeployAdminOnly || IsFullDeployer(player)) return true;
            if (_config.RenterDeployEnabled &&
                (!_config.RenterRequirePermission || permission.UserHasPermission(player.UserIDString, PermRenter)) &&
                FindAuthedRoom(player) != null)
            {
                renter = true;
                return true;
            }
            error = L(_config.RenterDeployEnabled ? "AdminOrRenter" : "AdminOnly", player);
            return false;
        }

        #endregion

        #region Apartment room integration (vanilla rental system)

        // Rooms are map entities (fixed for the map's lifetime; a wipe/restart
        // reloads the plugin), so scan serverEntities once and cache — the
        // hold-placement preview queries this several times per second. The
        // lookups below are deliberately plain loops (no LINQ): they run in
        // per-tick paths.
        private List<ApartmentRoom> _roomsCache;

        private List<ApartmentRoom> Rooms()
        {
            if (_roomsCache == null)
            {
                _roomsCache = new List<ApartmentRoom>();
                foreach (var ent in BaseNetworkable.serverEntities)
                {
                    var room = ent as ApartmentRoom;
                    if (room != null && !room.IsDestroyed) _roomsCache.Add(room);
                }
            }
            return _roomsCache;
        }

        // A rented room this player is an owner (renter) of.
        private ApartmentRoom FindAuthedRoom(BasePlayer player)
        {
            var rooms = Rooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (r != null && !r.IsDestroyed && r.IsCurrentlyRented() && r.IsAuthed(player.userID)) return r;
            }
            return null;
        }

        // The rented room whose bounds contain this world position (same check the
        // game itself uses in ApartmentRoom.IsInsideRoom).
        private ApartmentRoom FindRentedRoomAt(Vector3 pos)
        {
            var rooms = Rooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (r != null && !r.IsDestroyed && r.IsCurrentlyRented() && r.WorldSpaceBounds().Contains(pos)) return r;
            }
            return null;
        }

        private ApartmentRoom FindRoomWithFurniture(BaseEntity ent)
        {
            var rooms = Rooms();
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                if (r != null && !r.IsDestroyed && r.Furniture.Contains(ent)) return r;
            }
            return null;
        }

        // Furniture we added (OwnerID != 0); vanilla furniture spawns with OwnerID 0.
        private int CountPluginFurniture(ApartmentRoom room)
        {
            int n = 0;
            var furniture = room.Furniture;
            for (int i = 0; i < furniture.Count; i++)
            {
                var e = furniture[i];
                if (e != null && !e.IsDestroyed && e.OwnerID != 0) n++;
            }
            return n;
        }

        // Register a placed entity into the game's own furniture list: it then
        // persists in the save (furnitureIds), counts toward upkeep tax, gets
        // re-static-ified on load, and is auto-destroyed on checkout/eviction.
        private void RegisterRoomFurniture(ApartmentRoom room, BaseEntity ent, ulong forUser)
        {
            MakeStaticLikeFurniture(ent);
            AddApartmentLock(room, ent);
            (ent as SleepingBag)?.AssignToUser(forUser);
            if (_config.RenterAutoPower) AutoPower(ent);
            if (ent.enableSaving) room.Furniture.Add(ent);
            else _spawned.Add(ent); // non-saving statics would dangle in furnitureIds
        }

        // Force-power an electrical consumer, mirroring what the game does for the
        // vanilla apartment fridge/telephone (Flags.Reserved8 = IOEntity power
        // flag). Some deployables (electric furnace) aren't IOEntities themselves —
        // they spawn a child IO entity and proxy IsPowered() through it (e.g.
        // ElectricOven.CanRunWithNoFuel -> spawnedIo.IsPowered()) — so power the
        // children too.
        private void AutoPower(BaseEntity ent)
        {
            AutoPowerIO(ent as IOEntity);
            if (ent.children == null) return;
            foreach (var child in ent.children) AutoPowerIO(child as IOEntity);
        }

        // UpdateHasPower sets the flag; IOStateChanged makes the device react as
        // if power just arrived (e.g. CeilingLight flips Flags.On). Feed the
        // MAIN power slot, not slot 0 — e.g. IndustrialCrafter takes power on
        // input 1, and other electric inputs can be toggle lines we must NOT
        // pulse (they'd force devices on and fight the player's UI switch).
        private void AutoPowerIO(IOEntity io)
        {
            if (io == null || io.IsDestroyed || io.inputs == null) return;

            int slot = -1;
            for (int i = 0; i < io.inputs.Length; i++)
            {
                var s = io.inputs[i];
                if (s == null || s.type != IOEntity.IOType.Electric) continue;
                if (slot < 0) slot = i;               // fallback: first electric input
                if (s.mainPowerSlot) { slot = i; break; }
            }
            if (slot < 0) return;

            int amount = Mathf.Max(io.ConsumptionAmount(), 1);
            io.UpdateHasPower(amount, slot);
            io.IOStateChanged(amount, slot);
            io.SendNetworkUpdate();
        }

        // Re-assert state on plugin-placed furniture (vanilla furniture is the
        // game's job). Runs at boot and from the sweep: covers server restarts
        // (room furniture reloads from furnitureIds) and anything that
        // re-evaluates state mid-session. Power flag: can be recalculated by
        // the game. grounded: NOT saved by StabilityEntity, so wallpaper wall
        // panels would collapse on the first stability update after a restart.
        private void MaintainFurniture()
        {
            foreach (var room in Rooms())
            {
                if (room == null || room.IsDestroyed || !room.IsCurrentlyRented()) continue;
                foreach (var ent in room.Furniture)
                {
                    if (ent == null || ent.IsDestroyed || ent.OwnerID == 0) continue;
                    if (_config.RenterAutoPower) AutoPower(ent);
                    var block = ent as BuildingBlock;
                    if (block != null && !block.grounded) block.grounded = true;
                }
            }

            // Outdoor placements: protection/grounded aren't saved with the
            // entity, so re-harden every pass.
            foreach (var ent in _spawned)
            {
                if (ent == null || ent.IsDestroyed) continue;
                HardenOutdoorPlacement(ent);
            }
        }

        // Mirror of the game's private ApartmentRoom.MakeEntityStatic. Outdoor
        // placements keep their normal network range (Small would make monument
        // decor pop in late at road distances).
        private void MakeStaticLikeFurniture(BaseEntity ent, bool shrinkNetworkRange = true)
        {
            foreach (var c in ent.GetComponentsInChildren<DestroyOnGroundMissing>()) UnityEngine.Object.Destroy(c);
            foreach (var c in ent.GetComponentsInChildren<GroundWatch>()) UnityEngine.Object.Destroy(c);
            var combat = ent as BaseCombatEntity;
            if (combat != null)
            {
                combat.baseProtection = ProtectionProperties.immortalProtection;
                combat.markAttackerHostile = false;
                combat.ShowHealthInfo = false;
            }
            if (shrinkNetworkRange) ent.networkRange = EntityNetworkRange.Small;
        }

        // Free placements outside rooms (admin decor on the roads) are plain
        // entities otherwise — outside any cupboard they DECAY away in hours
        // (this is what ate the hitch troughs). Protection isn't saved with the
        // entity, so this is re-applied from MaintainFurniture on every boot.
        private void HardenOutdoorPlacement(BaseEntity ent)
        {
            MakeStaticLikeFurniture(ent, shrinkNetworkRange: false);
            var block = ent as BuildingBlock;
            if (block != null) block.grounded = true;
            StabilizeIfTrainCar(ent);
            if (_config.RenterAutoPower) AutoPower(ent);
        }

        // Train cars (the gambling caboose) normally live on rails: off-track
        // spawns are instakilled (TrainCar.killofftracktrains, queued as a
        // 0-second Invoke in Spawn — cancellable in the same frame) and their
        // physics runs spline-based movement. Cancel the kill, drop the
        // CompleteTrain (VehicleFixedUpdate no-ops when it's null), and freeze
        // the rigidbody so it sits as static decor. Re-applied every sweep
        // because a restart re-runs Spawn's off-track check.
        private void StabilizeIfTrainCar(BaseEntity ent)
        {
            var train = ent as TrainCar;
            if (train == null || train.IsDestroyed) return;
            train.CancelInvoke(train.KillMessage);
            train.completeTrain = null;
            if (train.rigidBody != null) train.rigidBody.isKinematic = true;
        }

        // Same lock the game puts on vanilla apartment furniture: auto-authorizes
        // the room's current renters, no code needed.
        private void AddApartmentLock(ApartmentRoom room, BaseEntity ent)
        {
            if (!ent.HasSlot(BaseEntity.Slot.Lock) || ent.GetSlot(BaseEntity.Slot.Lock) != null) return;
            var apLock = GameManager.server.CreateEntity(_config.ApartmentLockPrefab) as ApartmentLock;
            if (apLock == null) return;
            apLock.SetParent(ent, ent.GetSlotAnchorName(BaseEntity.Slot.Lock));
            apLock.Spawn();
            SetFlagNet(apLock, BaseEntity.Flags.Locked, true);
            apLock.Room = room;
            ent.SetSlot(BaseEntity.Slot.Lock, apLock);
        }

        private bool HasItem(BasePlayer player, int itemid)
        {
            var all = new List<Item>();
            player.inventory.GetAllItems(all);
            return all.Any(i => i != null && i.info != null && i.info.itemid == itemid && i.amount >= 1);
        }

        private bool TakeOneItem(BasePlayer player, int itemid, out ulong skin)
        {
            skin = 0;
            var all = new List<Item>();
            player.inventory.GetAllItems(all);
            var found = all.FirstOrDefault(i => i != null && i.info != null && i.info.itemid == itemid && i.amount >= 1);
            if (found == null) return false;
            skin = found.skin;
            found.UseItem(1);
            return true;
        }

        private void RefundItem(BasePlayer player, int itemid, ulong skin)
        {
            var item = ItemManager.CreateByItemID(itemid, 1, skin);
            if (item != null) player.GiveItem(item);
        }

        // entity prefab path -> item definition (refunds, skin picker).
        private Dictionary<string, ItemDefinition> _prefabToItem;

        private ItemDefinition ItemDefForEntity(BaseEntity ent)
        {
            if (_prefabToItem == null)
            {
                _prefabToItem = new Dictionary<string, ItemDefinition>(StringComparer.OrdinalIgnoreCase);
                foreach (var def in ItemManager.itemList)
                {
                    var mod = def.GetComponent<ItemModDeployable>();
                    if (mod == null || mod.entityPrefab == null) continue;
                    var path = mod.entityPrefab.resourcePath;
                    if (!string.IsNullOrEmpty(path)) _prefabToItem[path] = def;
                }
            }
            return _prefabToItem.TryGetValue(ent.PrefabName, out var d) ? d : null;
        }

        // Returns true when the entity mapped to a real item and it was given
        // back (wall panels and statics have no item — nothing to refund).
        private bool RefundEntityItem(BasePlayer player, BaseEntity ent)
        {
            var d = ItemDefForEntity(ent);
            if (d == null) return false;
            RefundItem(player, d.itemid, ent.skinID);
            return true;
        }

        #endregion

        #region Layouts (save / re-apply the admin's monument decor)

        // Snapshots the admin's outdoor placements (_spawned) as monument-LOCAL
        // positions/yaws, so a saved layout re-applies correctly after a wipe
        // even though the monument lands at new world coordinates. Room
        // furniture is per-renter and deliberately excluded.

        private class LayoutEntry
        {
            public string Prefab;
            public float X, Y, Z;      // monument-local position
            public float Yaw;          // yaw relative to the monument
            public ulong Skin;
            public int Grade = -1;     // BuildingBlock (wallpaper panels) only
            public ulong Wallpaper;
            public ulong Wallpaper2;
        }

        private float _lastLayoutApply = -999f;

        private Dictionary<string, List<LayoutEntry>> LoadLayouts()
        {
            try
            {
                return Interface.Oxide.DataFileSystem.ReadObject<Dictionary<string, List<LayoutEntry>>>(LayoutsFile)
                       ?? new Dictionary<string, List<LayoutEntry>>();
            }
            catch { return new Dictionary<string, List<LayoutEntry>>(); }
        }

        private void SaveLayouts(Dictionary<string, List<LayoutEntry>> layouts) =>
            Interface.Oxide.DataFileSystem.WriteObject(LayoutsFile, layouts);

        [ChatCommand("aplayout")]
        private void ChatLayout(BasePlayer player, string command, string[] args)
        {
            if (!IsFullDeployer(player)) { player.ChatMessage(L("AdminOnly", player)); return; }
            player.ChatMessage(LayoutCommand(player, args));
        }

        [ConsoleCommand("apartment.layout")]
        private void ConsoleLayout(ConsoleSystem.Arg arg)
        {
            if (!HasConsolePermission(arg)) return;
            var args = new List<string>();
            for (int i = 0; i < 2; i++)
            {
                var s = arg.GetString(i, null);
                if (!string.IsNullOrEmpty(s)) args.Add(s);
            }
            arg.ReplyWith(LayoutCommand(arg.Player(), args.ToArray()));
        }

        private string LayoutCommand(BasePlayer player, string[] args)
        {
            string action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string name = args.Length > 1 ? args[1] : DefaultLayoutName;
            var layouts = LoadLayouts();

            switch (action)
            {
                case "save": return LayoutSave(layouts, name);
                case "apply":
                    // Cooldown: applying spawns many entities at once (uMod:
                    // admin features need restrictions against accidental spam).
                    if (UnityEngine.Time.realtimeSinceStartup - _lastLayoutApply < 10f)
                        return L("LayoutCooldown", player);
                    _lastLayoutApply = UnityEngine.Time.realtimeSinceStartup;
                    return LayoutApply(player, layouts, name);
                case "delete":
                    if (!layouts.Remove(name)) return L("LayoutNotFound", player, name);
                    SaveLayouts(layouts);
                    return L("LayoutDeleted", player, name);
                case "list":
                    return layouts.Count == 0
                        ? L("LayoutNone", player)
                        : L("LayoutList", player, string.Join(", ", layouts.Select(kv => $"{kv.Key} ({kv.Value.Count})")));
                default:
                    return L("LayoutUsage", player);
            }
        }

        private string LayoutSave(Dictionary<string, List<LayoutEntry>> layouts, string name)
        {
            var monument = FindMonuments().FirstOrDefault();
            if (monument == null) return L("NoMonument", null, _config.MonumentNameFilter);

            var entries = new List<LayoutEntry>();
            foreach (var ent in _spawned)
            {
                if (ent == null || ent.IsDestroyed || ent.net == null || !ent.enableSaving) continue;
                Vector3 local = monument.transform.InverseTransformPoint(ent.transform.position);
                var entry = new LayoutEntry
                {
                    Prefab = ent.PrefabName,
                    X = local.x, Y = local.y, Z = local.z,
                    Yaw = ent.transform.rotation.eulerAngles.y - monument.transform.rotation.eulerAngles.y,
                    Skin = ent.skinID
                };
                var block = ent as BuildingBlock;
                if (block != null)
                {
                    entry.Grade = (int)block.grade;
                    entry.Wallpaper = block.wallpaperID;
                    entry.Wallpaper2 = block.wallpaperID2;
                }
                entries.Add(entry);
            }
            if (entries.Count == 0) return L("LayoutNothing", null);

            layouts[name] = entries;
            SaveLayouts(layouts);
            return L("LayoutSaved", null, name, entries.Count);
        }

        private string LayoutApply(BasePlayer player, Dictionary<string, List<LayoutEntry>> layouts, string name)
        {
            if (!layouts.TryGetValue(name, out var entries)) return L("LayoutNotFoundList", player, name);
            var monument = FindMonuments().FirstOrDefault();
            if (monument == null) return L("NoMonument", null, _config.MonumentNameFilter);

            int applied = 0, skipped = 0, failed = 0;
            foreach (var entry in entries)
            {
                Vector3 pos = monument.transform.TransformPoint(new Vector3(entry.X, entry.Y, entry.Z));
                Quaternion rot = Quaternion.Euler(0f, monument.transform.rotation.eulerAngles.y + entry.Yaw, 0f);

                // Idempotent: skip anything of the same prefab already sitting there.
                if (_spawned.Any(e => e != null && !e.IsDestroyed && e.PrefabName == entry.Prefab &&
                                      Vector3.Distance(e.transform.position, pos) < 0.75f))
                { skipped++; continue; }

                var ent = GameManager.server.CreateEntity(entry.Prefab, pos, rot);
                if (ent == null) { failed++; continue; }

                ent.skinID = entry.Skin;
                ent.OwnerID = player != null ? (ulong)player.userID : 1UL; // 1 = applied from console
                var preSpawnBlock = ent as BuildingBlock;
                if (preSpawnBlock != null) preSpawnBlock.grounded = true;
                ent.Spawn();

                var block = ent as BuildingBlock;
                if (block != null)
                {
                    if (entry.Grade >= 0) block.ChangeGradeAndSkin((BuildingGrade.Enum)entry.Grade, 0UL);
                    block.SetHealthToMax();
                    if (entry.Wallpaper != 0) block.SetWallpaper(entry.Wallpaper, 0);
                    if (entry.Wallpaper2 != 0) block.SetWallpaper(entry.Wallpaper2, 1);
                    block.SendNetworkUpdate();
                }

                _spawned.Add(ent);
                HardenOutdoorPlacement(ent);
                applied++;
            }
            SaveSpawnedData();
            return L("LayoutApplied", player, name, applied, skipped, failed);
        }

        // Re-decorate automatically after every wipe/boot (on by default).
        // Idempotent, so running it on every boot is safe.
        private void TryAutoApplyLayout()
        {
            if (!_config.AutoApplyLayoutEnabled) return;
            var name = string.IsNullOrEmpty(_config.AutoApplyLayout) ? DefaultLayoutName : _config.AutoApplyLayout;

            var layouts = LoadLayouts();
            if (!layouts.ContainsKey(name))
            {
                // Nothing saved yet is the normal state on a fresh install —
                // only complain when the admin has layouts but not this one.
                if (layouts.Count > 0)
                    PrintWarning($"Auto-apply layout \"{name}\" does not exist. Saved layouts: {string.Join(", ", layouts.Keys)}");
                return;
            }
            Puts("Auto-apply layout: " + LayoutApply(null, layouts, name));
        }

        #endregion

        #region Data files (oxide/data/ApartmentComplexUnlock/)

        // Both data files live in their own folder rather than loose in
        // oxide/data. v1.0.2 and earlier used "ApartmentComplexUnlock.json" and
        // "ApartmentComplexUnlock_layouts.json" at the top level.
        private string SpawnedFile => Name + "/spawned";
        private string LayoutsFile => Name + "/layouts";

        private const string DefaultLayoutName = "default";

        // Copy the old top-level files into the folder once, on first load of
        // this version. The old files are left in place as a backup (a plugin
        // can't delete them — Oxide sandboxes System.IO), so re-migration is
        // guarded on the new file not existing yet.
        private void MigrateDataFiles()
        {
            var fs = Interface.Oxide.DataFileSystem;
            bool moved = false;
            try
            {
                if (fs.ExistsDatafile(Name) && !fs.ExistsDatafile(SpawnedFile))
                {
                    fs.WriteObject(SpawnedFile, fs.ReadObject<List<ulong>>(Name) ?? new List<ulong>());
                    moved = true;
                }
                if (fs.ExistsDatafile(Name + "_layouts") && !fs.ExistsDatafile(LayoutsFile))
                {
                    fs.WriteObject(LayoutsFile, fs.ReadObject<Dictionary<string, List<LayoutEntry>>>(Name + "_layouts")
                                                ?? new Dictionary<string, List<LayoutEntry>>());
                    moved = true;
                }
            }
            catch (Exception ex) { PrintError($"Could not move the old data files into oxide/data/{Name}/: {ex.Message}"); return; }

            if (moved)
                Puts($"Data files moved to oxide/data/{Name}/. The old {Name}.json / {Name}_layouts.json are no longer used and can be deleted.");
        }

        #endregion

        #region Deploy core (preview / place / undeploy)

        // Entities this plugin placed, so undeploy never touches anything else.
        // Persisted as net IDs (stable across saves) in the plugin data file so
        // outdoor placements stay protected and removable after restarts.
        private readonly HashSet<BaseEntity> _spawned = new HashSet<BaseEntity>();

        private void SaveSpawnedData()
        {
            var ids = new List<ulong>();
            foreach (var e in _spawned)
                if (e != null && !e.IsDestroyed && e.net != null && e.enableSaving) ids.Add(e.net.ID.Value);
            Interface.Oxide.DataFileSystem.WriteObject(SpawnedFile, ids);
        }

        // A wipe starts a new save whose net IDs are unrelated to the old ones,
        // so a saved ID would resolve to some other entity on the new map. Oxide
        // calls this while the world is created, before OnServerInitialized
        // reads the file.
        private void OnNewSave(string filename)
        {
            _spawned.Clear();
            Interface.Oxide.DataFileSystem.WriteObject(SpawnedFile, new List<ulong>());
        }

        private void LoadSpawnedData()
        {
            List<ulong> ids;
            try { ids = Interface.Oxide.DataFileSystem.ReadObject<List<ulong>>(SpawnedFile) ?? new List<ulong>(); }
            catch { ids = new List<ulong>(); }

            int restored = 0, ignored = 0;
            foreach (var id in ids)
            {
                var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(id)) as BaseEntity;
                if (ent == null || ent.IsDestroyed) continue;
                // Everything this plugin places has an owner; map entities don't.
                // Also clears IDs left in the file by a wipe before v1.0.7.
                if (ent.OwnerID == 0) { ignored++; continue; }
                _spawned.Add(ent);
                restored++;
            }
            if (restored > 0) Puts($"Restored {restored} plugin-placed entities from the data file.");
            if (ignored > 0) Puts($"Ignored {ignored} saved ID(s) that now belong to map entities.");
            SaveSpawnedData(); // prune entries that no longer resolve
        }

        // Per-player pending placement being previewed.
        private readonly Dictionary<ulong, PreviewState> _previews = new Dictionary<ulong, PreviewState>();
        private class PreviewState { public string Key; public string Prefab; public float Yaw; public bool Renter; public int ItemId; }

        // Cached real footprint per prefab (measured once by spawning briefly underground).
        private readonly Dictionary<string, Bounds> _boundsCache = new Dictionary<string, Bounds>();

        // Resolve a user token into a spawnable prefab path:
        //   1) a configured alias (whose value may be a prefab path OR an item
        //      shortname), 2) a literal assets/... path, 3) any deployable item
        //      shortname.
        private string ResolvePrefab(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            if (_config.Deployables.TryGetValue(key, out var alias) && !string.IsNullOrEmpty(alias)) key = alias;
            if (key.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) return key;

            var def = ItemManager.FindItemDefinition(key);
            var mod = def?.GetComponent<ItemModDeployable>();
            if (mod != null && mod.entityPrefab != null && !string.IsNullOrEmpty(mod.entityPrefab.resourcePath))
                return mod.entityPrefab.resourcePath;
            return null;
        }

        // Start previewing a deployable: the outline + arrow follows the player's
        // aim (drawn by DrawPreviews) until they /applace or /apcancel.
        private string StartPreview(BasePlayer player, string key, bool renter)
        {
            if (string.IsNullOrEmpty(key))
                return renter
                    ? L("DeployUsageRenter", player)
                    : L("DeployUsageAdmin", player, string.Join(", ", _config.Deployables.Keys));

            // +180: deployable prefabs' "front" faces the deployer in vanilla, so
            // flip the spawn yaw to match what players expect.
            var pv = new PreviewState { Key = key, Yaw = (player.eyes.rotation.eulerAngles.y + 180f) % 360f, Renter = renter };

            if (renter)
            {
                // Renters may only use real deployable items — but aliases whose
                // value is an item shortname (e.g. coop -> chickencoop) are fine;
                // only raw prefab paths (non-item statics) stay admin-only.
                if (_config.Deployables.TryGetValue(key, out var aliasTarget) && !string.IsNullOrEmpty(aliasTarget) &&
                    !aliasTarget.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
                    key = aliasTarget;

                var def = ItemManager.FindItemDefinition(key);
                var mod = def != null ? def.GetComponent<ItemModDeployable>() : null;
                string path = mod != null && mod.entityPrefab != null ? mod.entityPrefab.resourcePath : null;
                if (string.IsNullOrEmpty(path))
                    return L("NotDeployableItem", player, key);
                if (_config.RenterBlockedItems != null && _config.RenterBlockedItems.Contains(def.shortname, StringComparer.OrdinalIgnoreCase))
                    return L("ItemBlocked", player, def.shortname);
                if (!HasItem(player, def.itemid))
                    return L("NeedItem", player, def.displayName.english);

                pv.Prefab = path;
                pv.ItemId = def.itemid;
                _previews[player.userID] = pv;
                return L("PreviewRenter", player, key);
            }

            string prefab = ResolvePrefab(key);
            if (prefab == null)
                return L("CantResolve", player, key);

            pv.Prefab = prefab;
            _previews[player.userID] = pv;
            return L("PreviewAdmin", player, key);
        }

        private string DoPlace(BasePlayer player)
        {
            if (!_previews.TryGetValue(player.userID, out var pv))
                return L("NothingToPlace", player);
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore))
                return L("AimSurface20", player);

            var room = FindRentedRoomAt(hit.point);
            if (pv.Renter)
            {
                if (room == null || !room.IsAuthed(player.userID))
                    return L("OwnRoomOnlyPlace", player);
                int placed = CountPluginFurniture(room);
                if (placed >= _config.RenterMaxPerRoom)
                    return L("RoomLimit", player, placed, _config.RenterMaxPerRoom);
            }

            ulong skin = 0;
            bool consumedItem = pv.Renter; // renters always pay the real item
            if (consumedItem && !TakeOneItem(player, pv.ItemId, out skin))
                return L("ItemGone", player, pv.Key);

            Quaternion rot = Quaternion.Euler(0f, pv.Yaw, 0f);
            var ent = GameManager.server.CreateEntity(pv.Prefab, GroundedSpawnPos(pv.Prefab, hit.point), rot);
            if (ent == null)
            {
                if (consumedItem) RefundItem(player, pv.ItemId, skin);
                _previews.Remove(player.userID);
                return L("PrefabFailed", player, pv.Prefab);
            }

            ent.skinID = skin;
            ent.OwnerID = player.userID;
            ent.Spawn();
            _previews.Remove(player.userID);

            // Anything placed inside a rented room becomes real apartment furniture:
            // persists in the save, auto-removed when the room is checked out.
            if (room != null)
            {
                RegisterRoomFurniture(room, ent, player.userID);
                return L("PlacedRoomCmd", player, pv.Key, room.RoomNumber);
            }

            _spawned.Add(ent);
            HardenOutdoorPlacement(ent);
            SaveSpawnedData();
            return L("PlacedOutdoorCmd", player, pv.Key, ent.ShortPrefabName);
        }

        private string CancelPreview(BasePlayer player) =>
            _previews.Remove(player.userID) ? L("PreviewCancelled", player) : L("NoActivePreview", player);

        // Spin the previewed object in place. No arg = one step (config RotateStepDegrees);
        // a number rotates by that many degrees (negative = the other way).
        private string DoRotate(BasePlayer player, string arg)
        {
            if (!_previews.TryGetValue(player.userID, out var pv))
                return L("NothingToRotate", player);
            float step = _config.RotateStepDegrees;
            if (!string.IsNullOrEmpty(arg) && float.TryParse(arg, out var custom)) step = custom;
            pv.Yaw = (pv.Yaw + step) % 360f;
            return L("Rotated", player, pv.Yaw);
        }

        // Some prefabs' models are offset upward from their origin (e.g. the
        // caboose static slot machine, which sits on counters in the caboose) —
        // spawned at a ground hit-point they float. Drop the spawn position so
        // the prefab's measured bottom lands on the surface. The bottom comes
        // from the COLLIDER union, not entity.bounds — bounds is hand-authored
        // in the prefab and misses the offset (the slot machine's does).
        private readonly Dictionary<string, float> _bottomCache = new Dictionary<string, float>();

        private Vector3 GroundedSpawnPos(string prefab, Vector3 hitPoint)
        {
            // Manual override wins (config key = alias or full prefab path).
            float manual = ManualSpawnOffset(prefab);
            if (manual != 0f)
            {
                hitPoint.y += manual;
                return hitPoint;
            }

            if (!_bottomCache.ContainsKey(prefab)) MeasurePrefab(prefab);
            float bottom = _bottomCache[prefab];
            if (bottom > 0.05f) hitPoint.y -= bottom;
            return hitPoint;
        }

        private float ManualSpawnOffset(string prefab)
        {
            if (_config.SpawnOffsets == null) return 0f;
            foreach (var kv in _config.SpawnOffsets)
            {
                if (string.Equals(kv.Key, prefab, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                if (_config.Deployables.TryGetValue(kv.Key, out var aliasPath) &&
                    string.Equals(aliasPath, prefab, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            }
            return 0f;
        }

        // Measure a prefab once (spawn far underground, read bounds + collider
        // bottom, kill).
        private void MeasurePrefab(string prefab)
        {
            var b = new Bounds(Vector3.zero, new Vector3(1.5f, 1.5f, 1.5f)); // fallback
            float bottom = 0f;
            var probe = GameManager.server.CreateEntity(prefab, new Vector3(0f, -500f, 0f), Quaternion.identity);
            if (probe != null)
            {
                probe.Spawn();
                if (probe.bounds.size.sqrMagnitude > 0.01f) b = probe.bounds;

                float min = float.MaxValue;
                foreach (var col in probe.GetComponentsInChildren<Collider>())
                {
                    if (col == null || col.isTrigger || !col.enabled) continue;
                    min = Mathf.Min(min, col.bounds.min.y - probe.transform.position.y);
                }
                bottom = min != float.MaxValue ? min : b.center.y - b.extents.y;

                probe.Kill();
            }
            _boundsCache[prefab] = b;
            _bottomCache[prefab] = bottom;
        }

        private Bounds GetPrefabBounds(string prefab)
        {
            if (!_boundsCache.TryGetValue(prefab, out var cached)) { MeasurePrefab(prefab); cached = _boundsCache[prefab]; }
            return cached;
        }

        // Draw a rotated wireframe box (oriented bounding box) via ddraw.line edges.
        private void DrawOrientedBox(BasePlayer viewer, Vector3 center, Vector3 halfExtents, Quaternion rot, Color color, float dur)
        {
            Vector3 rx = rot * new Vector3(halfExtents.x, 0f, 0f);
            Vector3 ry = rot * new Vector3(0f, halfExtents.y, 0f);
            Vector3 rz = rot * new Vector3(0f, 0f, halfExtents.z);
            Vector3 c0 = center - rx - ry - rz, c1 = center + rx - ry - rz, c2 = center + rx - ry + rz, c3 = center - rx - ry + rz;
            Vector3 c4 = center - rx + ry - rz, c5 = center + rx + ry - rz, c6 = center + rx + ry + rz, c7 = center - rx + ry + rz;
            void Line(Vector3 a, Vector3 b) => viewer.SendConsoleCommand("ddraw.line", dur, color, a, b);
            Line(c0, c1); Line(c1, c2); Line(c2, c3); Line(c3, c0);   // bottom
            Line(c4, c5); Line(c5, c6); Line(c6, c7); Line(c7, c4);   // top
            Line(c0, c4); Line(c1, c5); Line(c2, c6); Line(c3, c7);   // verticals
        }

        // Runs ~5x/sec via timer: command previews + hold-item + tool previews.
        private void DrawPreviews()
        {
            DrawCommandPreviews();
            UpdateHoldPreviews();
            UpdatePipeToolPreviews();
            UpdateWallpaperPreviews();
        }

        // Draws each /apdeploy preview at the player's aim point, sized to the
        // object's real footprint and rotated as it will place.
        private void DrawCommandPreviews()
        {
            if (_previews.Count == 0) return;
            List<ulong> stale = null;
            foreach (var kv in _previews)
            {
                var player = BasePlayer.FindByID(kv.Key);
                if (player == null || !player.IsConnected) { (stale ?? (stale = new List<ulong>())).Add(kv.Key); continue; }
                if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore)) continue;

                // Renters: green only while aiming inside their own room.
                Color boxColor = Color.green;
                if (kv.Value.Renter)
                {
                    var room = FindRentedRoomAt(hit.point);
                    if (room == null || !room.IsAuthed(player.userID)) boxColor = Color.red;
                }
                DrawGhost(player, kv.Value.Key, kv.Value.Prefab, kv.Value.Yaw, GroundedSpawnPos(kv.Value.Prefab, hit.point), boxColor);
            }
            if (stale != null) foreach (var id in stale) _previews.Remove(id);
        }

        // Ghost outline (oriented footprint box + facing arrow + label) at a hit
        // point. ddraw only renders on admin clients; for non-admins, flick the
        // admin flag around the draw batch (the window is a few milliseconds and
        // the messages arrive in order on the same connection).
        private void DrawGhost(BasePlayer player, string label, string prefab, float yaw, Vector3 hitPoint, Color boxColor)
        {
            Bounds b = GetPrefabBounds(prefab);
            Quaternion rot = Quaternion.Euler(0f, yaw, 0f);
            Vector3 center = hitPoint + rot * b.center;
            Vector3 fwd = rot * Vector3.forward;

            bool flick = !player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);
            if (flick) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true); player.SendNetworkUpdateImmediate(); }
            DrawOrientedBox(player, center, b.extents, rot, boxColor, 0.25f);
            player.SendConsoleCommand("ddraw.arrow", 0.25f, Color.cyan, hitPoint, hitPoint + fwd * (b.extents.z + 1f), 0.3f);
            player.SendConsoleCommand("ddraw.text", 0.25f, Color.white, center + Vector3.up * (b.extents.y + 0.3f), $"<size=20>{label}</size>");
            if (flick) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false); player.SendNetworkUpdateImmediate(); }
        }

        private string DoUndeploy(BasePlayer player, bool renter)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore))
                return L("UndeployAim", player);
            var ent = hit.collider ? hit.collider.GetComponentInParent<BaseEntity>() : null;
            if (ent == null || ent.IsDestroyed) return L("NoEntity", player);

            var room = FindRoomWithFurniture(ent);

            if (renter)
            {
                if (room == null || !room.IsAuthed(player.userID))
                    return L("UndeployOwnRoom", player);
                if (ent.OwnerID != player.userID)
                    return L("UndeployOwnOnly", player);
                room.Furniture.Remove(ent);
                bool gotItem = RefundEntityItem(player, ent);
                ent.Kill();
                return L(gotItem ? "RemovedRefunded" : "Removed", player, ent.ShortPrefabName);
            }

            // Full mode: this session's placements, or plugin-placed room furniture
            // (OwnerID != 0 — vanilla furniture spawns with OwnerID 0, never touched).
            // True admins get the item back too — hold-placement consumes real
            // items for everyone, so an admin furnishing their own rented room
            // paid for the object just like a renter did.
            if (_spawned.Contains(ent))
            {
                _spawned.Remove(ent);
                bool gotItem = IsFullDeployer(player) && RefundEntityItem(player, ent);
                ent.Kill();
                SaveSpawnedData();
                return L(gotItem ? "RemovedRefunded" : "Removed", player, ent.ShortPrefabName);
            }
            if (room != null && ent.OwnerID != 0)
            {
                room.Furniture.Remove(ent);
                bool gotItem = IsFullDeployer(player) && RefundEntityItem(player, ent);
                ent.Kill();
                return L(gotItem ? "RemovedRefunded" : "Removed", player, ent.ShortPrefabName);
            }
            return L("NotPluginPlaced", player, ent.ShortPrefabName);
        }

        #endregion

        #region Industrial (adaptor/crafter mounting + /appipe connections)

        // Industrial automation works in the apartments because pipe CONNECTIONS
        // are pure server-side data (IOSlot.connectedTo + line points, networked
        // to clients for rendering) — only the wire tool ITSELF is client-blocked.
        // So: storage adaptors / crafters snap-mount onto their host entity via
        // the hold flow (their function comes solely from GetParentEntity()),
        // and /appipe creates the connections the wire tool would have made.

        private enum MountKind { None, StorageAdaptor, Crafter }
        private readonly Dictionary<string, MountKind> _mountKinds = new Dictionary<string, MountKind>();

        private MountKind GetMountKind(string prefabPath)
        {
            if (_mountKinds.TryGetValue(prefabPath, out var cached)) return cached;
            var kind = MountKind.None;
            var go = GameManager.server.FindPrefab(prefabPath);
            if (go != null)
            {
                if (go.GetComponent<IndustrialStorageAdaptor>() != null) kind = MountKind.StorageAdaptor;
                else if (go.GetComponent<IndustrialCrafter>() != null) kind = MountKind.Crafter;
            }
            _mountKinds[prefabPath] = kind;
            return kind;
        }

        // What the held adaptor/crafter would mount onto at the aim point; null +
        // error when invalid. Renters: the host must sit inside a room they rent.
        // Full mode (admins): any host inside the monument.
        private BaseEntity FindMountTarget(MountKind kind, RaycastHit hit, BasePlayer player, bool renter, out string error)
        {
            error = null;
            var target = hit.collider ? hit.collider.GetComponentInParent<BaseEntity>() : null;

            if (kind == MountKind.StorageAdaptor)
            {
                if (target == null || (!(target is StorageContainer) && !(target is ContainerIOEntity)))
                { error = L("AimContainer", player); return null; }
                if (HasMountedChild<IndustrialStorageAdaptor>(target))
                { error = L("HasAdaptor", player); return null; }
            }
            else if (kind == MountKind.Crafter)
            {
                if (!(target is Workbench))
                { error = L("AimWorkbench", player); return null; }
                if (HasMountedChild<IndustrialCrafter>(target))
                { error = L("HasCrafter", player); return null; }
            }
            else { error = L("NotMountable", player); return null; }

            if (renter)
            {
                var room = FindRentedRoomAt(target.transform.position);
                if (room == null || !room.IsAuthed(player.userID))
                { error = L("MountOwnRoom", player); return null; }
            }
            else if (!NearApartmentMonument(target.transform.position))
            { error = L("MountNotMonument", player); return null; }
            return target;
        }

        private bool HasMountedChild<T>(BaseEntity parent) where T : BaseEntity
        {
            if (parent.children == null) return false;
            foreach (var c in parent.children)
                if (c is T && !c.IsDestroyed) return true;
            return false;
        }

        // Adaptors sit on the host's top face, crafters on the workbench front.
        private Vector3 MountPosition(MountKind kind, BaseEntity target)
        {
            var b = target.bounds;
            Vector3 local = kind == MountKind.StorageAdaptor
                ? b.center + Vector3.up * b.extents.y
                : b.center + Vector3.forward * b.extents.z;
            return target.transform.TransformPoint(local);
        }

        private string DoMountPlace(BasePlayer player, HoldState hs, Item item, RaycastHit hit, MountKind kind, bool renter)
        {
            var target = FindMountTarget(kind, hit, player, renter, out var err);
            if (target == null) return err;

            var room = FindRentedRoomAt(target.transform.position); // renter: authed, per FindMountTarget
            if (renter)
            {
                int placed = CountPluginFurniture(room);
                if (placed >= _config.RenterMaxPerRoom)
                    return L("RoomLimit", player, placed, _config.RenterMaxPerRoom);
            }

            var ent = GameManager.server.CreateEntity(hs.Prefab, MountPosition(kind, target), target.transform.rotation);
            if (ent == null) return L("PrefabFailed", player, hs.Prefab);

            ulong skin = item.skin;
            item.UseItem(1); // held-item flows always consume the item
            ent.skinID = skin;
            ent.OwnerID = player.userID;
            ent.SetParent(target, worldPositionStays: true);
            ent.Spawn();

            if (room != null)
            {
                RegisterRoomFurniture(room, ent, player.userID);
            }
            else
            {
                MakeStaticLikeFurniture(ent, shrinkNetworkRange: false);
                _spawned.Add(ent);
                SaveSpawnedData();
            }
            return L("MountAttached", player, hs.Key, target.ShortPrefabName);
        }

        // ---- /appipe: look at source, run; look at target, run again ----------

        private readonly Dictionary<ulong, IOEntity> _pipeSource = new Dictionary<ulong, IOEntity>();

        [ChatCommand("appipe")]
        private void ChatPipe(BasePlayer player, string command, string[] args)
        {
            if (!TryGetDeployMode(player, out var renter, out var err)) { player.ChatMessage(err); return; }
            player.ChatMessage(PipeCommand(player, args.Length > 0 ? args[0] : null, renter));
        }

        [ConsoleCommand("apartment.pipe")]
        private void ConsolePipe(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) { arg.ReplyWith(L("InGame", null)); return; }
            if (!TryGetDeployMode(player, out var renter, out var err)) { arg.ReplyWith(err); return; }
            arg.ReplyWith(PipeCommand(player, arg.GetString(0, null), renter));
        }

        private string PipeCommand(BasePlayer player, string action, bool renter)
        {
            if (string.Equals(action, "cancel", StringComparison.OrdinalIgnoreCase))
                return _pipeSource.Remove(player.userID) ? L("PipeCancelled", player) : L("NoPipeSelection", player);

            var io = AimedIOEntity(player);
            if (string.Equals(action, "clear", StringComparison.OrdinalIgnoreCase))
                return ClearPipes(player, io, renter);
            if (io == null)
                return L("PipeAim", player);
            return SelectOrConnect(player, io, renter);
        }

        private IOEntity AimedIOEntity(BasePlayer player)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore)) return null;
            var io = hit.collider ? hit.collider.GetComponentInParent<IOEntity>() : null;
            return io == null || io.IsDestroyed ? null : io;
        }

        private string ClearPipes(BasePlayer player, IOEntity io, bool renter)
        {
            if (io == null) return L("PipeClearAim", player);
            if (renter && io.OwnerID != player.userID)
                return L("PipeClearOwn", player);
            io.DisconnectAll();
            _pipeSource.Remove(player.userID);
            return L("PipeCleared", player, io.ShortPrefabName);
        }

        // Shared by /appipe and the pipe-tool click flow: first use selects the
        // source (free industrial output), second use connects to the target's
        // free industrial input.
        private string SelectOrConnect(BasePlayer player, IOEntity io, bool renter)
        {
            if (renter)
            {
                var room = FindRentedRoomAt(io.transform.position);
                if (room == null || !room.IsAuthed(player.userID))
                    return L("PipeNotInRoom", player);
            }

            if (!_pipeSource.TryGetValue(player.userID, out var src) || src == null || src.IsDestroyed)
            {
                int oi = FirstFreeIndustrialSlot(io, input: false);
                if (oi < 0) return L("PipeNoOutput", player, io.ShortPrefabName);
                _pipeSource[player.userID] = io;
                return L("PipeSourceSet", player, io.ShortPrefabName);
            }

            if (src == io) return L("PipeSame", player);

            int outIdx = FirstFreeIndustrialSlot(src, input: false);
            int inIdx = FirstFreeIndustrialSlot(io, input: true);
            if (outIdx < 0) { _pipeSource.Remove(player.userID); return L("PipeSourceLost", player, src.ShortPrefabName); }
            if (inIdx < 0) return L("PipeNoInput", player, io.ShortPrefabName);
            if (Vector3.Distance(src.transform.position, io.transform.position) > 20f)
                return L("PipeTooFar", player);

            // Line points live on the OUTPUT slot, in the OUTPUT entity's local
            // space, ordered [input end, ..., output end] (see the game's own
            // SnapLinesToHandlePositions).
            var points = new List<Vector3>
            {
                src.transform.InverseTransformPoint(io.transform.TransformPoint(io.inputs[inIdx].handlePosition)),
                src.outputs[outIdx].handlePosition
            };
            var slack = new List<float> { 0f, 0f };
            src.ConnectTo(io, outIdx, inIdx, points, slack, new IOEntity.LineAnchor[0]);
            if (src.ioType == IOEntity.IOType.Industrial) src.NotifyIndustrialNetworkChanged();
            if (io.ioType == IOEntity.IOType.Industrial) io.NotifyIndustrialNetworkChanged();
            src.NotifyClientsLineChanged();
            _pipeSource.Remove(player.userID);
            return L("PipeConnected", player, src.ShortPrefabName, io.ShortPrefabName);
        }

        private int FirstFreeIndustrialSlot(IOEntity io, bool input)
        {
            var slots = input ? io.inputs : io.outputs;
            if (slots == null) return -1;
            for (int i = 0; i < slots.Length; i++)
            {
                var s = slots[i];
                if (s != null && s.type == IOEntity.IOType.Industrial && s.connectedTo.Get() == null) return i;
            }
            return -1;
        }

        // ---- pipe tool click flow (no typing) ---------------------------------
        // Hold the vanilla industrial PIPE TOOL: LMB selects the aimed source then
        // connects the aimed target; LMB on empty space or R cancels; RMB clears
        // the aimed component's pipes. Active ONLY near the apartment monument —
        // everywhere else the tool keeps its vanilla behavior (wiring works
        // normally outside the monument, and reacting there would double up).

        private bool TryGetPipeToolContext(BasePlayer player, out bool renter)
        {
            renter = false;
            if (!_config.RenterDeployEnabled || !_config.RenterHoldPlacement) return false;
            var tool = player.GetHeldEntity() as WireTool;
            if (tool == null || tool.wireType != IOEntity.IOType.Industrial) return false;
            if (!NearApartmentMonument(player.transform.position)) return false;
            return TryGetDeployMode(player, out renter, out _);
        }

        private void HandlePipeToolInput(BasePlayer player, bool fire, bool alt, bool reload)
        {
            if (!TryGetPipeToolContext(player, out var renter)) return;

            if (reload)
            {
                if (_pipeSource.Remove(player.userID)) player.ChatMessage(L("PipeCancelled", player));
                return;
            }
            if (alt)
            {
                player.ChatMessage(ClearPipes(player, AimedIOEntity(player), renter));
                return;
            }
            if (!fire) return;

            var io = AimedIOEntity(player);
            if (io == null)
            {
                if (_pipeSource.Remove(player.userID)) player.ChatMessage(L("PipeCancelledEmpty", player));
                return;
            }
            player.ChatMessage(SelectOrConnect(player, io, renter));
        }

        // Runs from the preview timer: highlights the aimed component (green =
        // clickable) and draws the running line from a selected source.
        private void UpdatePipeToolPreviews()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (!TryGetPipeToolContext(player, out _)) continue;

                bool hasSrc = _pipeSource.TryGetValue(player.userID, out var src) && src != null && !src.IsDestroyed;
                var aimed = AimedIOEntity(player);
                bool aimedOk = aimed != null && (hasSrc
                    ? aimed != src && FirstFreeIndustrialSlot(aimed, input: true) >= 0
                    : FirstFreeIndustrialSlot(aimed, input: false) >= 0);

                Vector3 aimPoint;
                if (aimed != null) aimPoint = aimed.transform.position;
                else if (Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore)) aimPoint = hit.point;
                else aimPoint = player.eyes.position + player.eyes.HeadRay().direction * 5f;

                DrawPipePreview(player, aimed, aimedOk, hasSrc ? src : null, aimPoint);
            }
        }

        private void DrawPipePreview(BasePlayer player, IOEntity aimed, bool aimedOk, IOEntity src, Vector3 aimPoint)
        {
            if (aimed == null && src == null) return;
            bool flick = !player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);
            if (flick) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true); player.SendNetworkUpdateImmediate(); }

            if (aimed != null)
            {
                var b = aimed.bounds;
                DrawOrientedBox(player, aimed.transform.TransformPoint(b.center), b.extents, aimed.transform.rotation,
                    aimedOk ? Color.green : Color.red, 0.25f);
                string label = src == null
                    ? L(aimedOk ? "HintPipeSource" : "HintNoOutput", player)
                    : (aimed == src ? L("HintPipeIsSource", player) : L(aimedOk ? "HintPipeConnect" : "HintNoInput", player));
                DdrawText(player, aimed.transform.TransformPoint(b.center) + Vector3.up * (b.extents.y + 0.25f), label);
            }

            if (src != null)
            {
                int oi = FirstFreeIndustrialSlot(src, input: false);
                Vector3 from = oi >= 0 ? src.transform.TransformPoint(src.outputs[oi].handlePosition) : src.transform.position;
                Vector3 to = aimed != null && aimedOk && aimed != src
                    ? aimed.transform.TransformPoint(aimed.inputs[FirstFreeIndustrialSlot(aimed, input: true)].handlePosition)
                    : aimPoint;
                player.SendConsoleCommand("ddraw.line", 0.25f, Color.cyan, from, to);
                DdrawText(player, from + Vector3.up * 0.3f, L("HintPipeCancel", player));
            }

            if (flick) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false); player.SendNetworkUpdateImmediate(); }
        }

        private void DdrawText(BasePlayer player, Vector3 pos, string text) =>
            player.SendConsoleCommand("ddraw.text", 0.25f, Color.white, pos, $"<size=18>{text}</size>");

        #endregion

        #region Wallpaper panels (hold the wallpaper tool, click a wall)

        // The apartment's real walls are static monument geometry, and wallpaper
        // is per-side state ON a BuildingBlock (SetWallpaper) — there's nothing
        // to apply it to. So: pick a design in the vanilla tool's own menu
        // (right-click; its selection RPC works fine in the monument), then
        // click a wall — the plugin spawns a REAL building wall panel flush
        // against it (grounded=true so stability can't collapse it) with that
        // design on both sides. The selection is read via the PUBLIC
        // WallpaperPlanner.GetSkinIDForCategory — no reflection. Clicking one
        // of our existing panels re-papers it with the current design.

        private string WallPrefab => _config.WallPanelPrefab;
        private const string WallpaperDesignItem = "wallpaper.wall";

        private bool TryGetWallpaperContext(BasePlayer player, out WallpaperPlanner tool, out bool renter)
        {
            renter = false;
            tool = player.GetHeldEntity() as WallpaperPlanner;
            if (tool == null) return false;
            if (!_config.RenterDeployEnabled || !_config.RenterWallpaper) return false;
            if (!NearApartmentMonument(player.transform.position)) return false;
            return TryGetDeployMode(player, out renter, out _);
        }

        // Defense in depth: the vanilla menu only offers owned designs, but the
        // plugin re-verifies before applying (hardcoded, like reskin).
        private bool OwnsWallpaperDesign(BasePlayer player, ulong skinId)
        {
            if (skinId == 0) return true;
            var def = ItemManager.FindItemDefinition(WallpaperDesignItem);
            if (def == null) return false;
            var entry = GetSkinsFor(def).FirstOrDefault(s => s.SkinId == skinId);
            return entry != null && player.blueprints.CheckSkinOwnership(entry.DefId, player);
        }

        // Flush pose against the aimed near-vertical surface: thin axis along the
        // surface normal, bounds bottom sitting on the floor below the aim point.
        private bool TryGetPanelPose(RaycastHit hit, out Vector3 pos, out Quaternion rot)
        {
            pos = default(Vector3);
            rot = default(Quaternion);
            Vector3 n = hit.normal; n.y = 0f;
            if (n.sqrMagnitude < 0.25f) return false; // floor/ceiling — walls only
            n.Normalize();

            Bounds b = GetPrefabBounds(WallPrefab);
            rot = Quaternion.LookRotation(n, Vector3.up);
            if (b.size.x < b.size.z) rot *= Quaternion.Euler(0f, 90f, 0f); // thin axis -> normal
            float thinHalf = Mathf.Min(b.extents.x, b.extents.z);

            float floorY = hit.point.y;
            if (Physics.Raycast(hit.point + n * 0.5f, Vector3.down, out var fh, 8f, ~0, QueryTriggerInteraction.Ignore))
                floorY = fh.point.y;

            float bottomOffset = b.center.y - b.extents.y; // pivot-agnostic
            pos = new Vector3(hit.point.x, floorY - bottomOffset, hit.point.z) + n * (thinHalf + 0.02f);
            return true;
        }

        private BuildingBlock AimedOwnPanel(BasePlayer player, bool renter)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)) return null;
            var block = hit.collider ? hit.collider.GetComponentInParent<BuildingBlock>() : null;
            if (block == null || block.IsDestroyed || block.OwnerID == 0) return null; // never touch real bases
            if (renter && block.OwnerID != player.userID) return null;
            return block;
        }

        private void HandleWallpaperInput(BasePlayer player, bool fire)
        {
            if (!fire) return;
            if (!TryGetWallpaperContext(player, out var tool, out var renter)) return;
            var msg = DoWallpaperClick(player, tool, renter);
            if (!string.IsNullOrEmpty(msg)) player.ChatMessage(msg);
        }

        private string DoWallpaperClick(BasePlayer player, WallpaperPlanner tool, bool renter)
        {
            ulong skin = tool.GetSkinIDForCategory(WallpaperSettings.Category.Wall);
            if (skin == 0) return L("WpPickDesign", player);
            if (!OwnsWallpaperDesign(player, skin)) return L("WpNotOwned", player);

            var panel = AimedOwnPanel(player, renter);
            if (panel != null)
            {
                panel.SetWallpaper(skin, 0);
                panel.SetWallpaper(skin, 1);
                return L("WpRepapered", player);
            }

            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 10f, ~0, QueryTriggerInteraction.Ignore))
                return L("WpAimWall", player);
            if (!TryGetPanelPose(hit, out var pos, out var rot))
                return L("WpVertical", player);

            var room = FindRentedRoomAt(pos);
            if (renter)
            {
                if (room == null || !room.IsAuthed(player.userID))
                    return L("WpOwnRoom", player);
                int placed = CountPluginFurniture(room);
                if (placed >= _config.RenterMaxPerRoom)
                    return L("RoomLimit", player, placed, _config.RenterMaxPerRoom);
            }

            // Vanilla wallpaper cost (cloth), renters only.
            var price = tool.placementPrice;
            if (renter && price != null && price.itemDef != null && price.amount > 0f)
            {
                int need = Mathf.CeilToInt(price.amount);
                if (CountItems(player, price.itemDef.itemid) < need)
                    return L("WpNeedCloth", player, need, price.itemDef.displayName.english);
                player.inventory.Take(null, price.itemDef.itemid, need);
            }

            var block = GameManager.server.CreateEntity(WallPrefab, pos, rot) as BuildingBlock;
            if (block == null) return L("WpPanelFailed", player);
            block.grounded = true;                 // no foundations here — skip stability
            block.OwnerID = player.userID;
            block.Spawn();
            block.ChangeGradeAndSkin(BuildingGrade.Enum.Stone, 0UL);
            block.SetHealthToMax();
            block.SetWallpaper(skin, 0);
            block.SetWallpaper(skin, 1);
            block.SendNetworkUpdate();

            if (room != null) RegisterRoomFurniture(room, block, player.userID);
            else _spawned.Add(block);
            return L("WpPlaced", player);
        }

        private int CountItems(BasePlayer player, int itemid)
        {
            var all = new List<Item>();
            player.inventory.GetAllItems(all);
            int n = 0;
            foreach (var i in all)
                if (i != null && i.info != null && i.info.itemid == itemid) n += i.amount;
            return n;
        }

        private void UpdateWallpaperPreviews()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (!TryGetWallpaperContext(player, out var tool, out var renter)) continue;

                bool hasDesign = tool.GetSkinIDForCategory(WallpaperSettings.Category.Wall) != 0;
                var panel = AimedOwnPanel(player, renter);
                if (panel != null)
                {
                    bool flick = !player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin);
                    if (flick) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true); player.SendNetworkUpdateImmediate(); }
                    var b = panel.bounds;
                    DrawOrientedBox(player, panel.transform.TransformPoint(b.center), b.extents, panel.transform.rotation,
                        hasDesign ? Color.green : Color.red, 0.25f);
                    DdrawText(player, panel.transform.TransformPoint(b.center) + Vector3.up * (b.extents.y + 0.25f),
                        hasDesign ? L("HintRepaper", player) : L("HintPickDesign", player));
                    if (flick) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false); player.SendNetworkUpdateImmediate(); }
                    continue;
                }

                if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)) continue;
                if (!TryGetPanelPose(hit, out var pos, out var rot)) continue;

                bool ok = hasDesign;
                if (renter)
                {
                    var room = FindRentedRoomAt(pos);
                    ok = ok && room != null && room.IsAuthed(player.userID);
                }
                DrawGhost(player, hasDesign ? L("HintPlacePanel", player) : L("HintPickDesign", player),
                    WallPrefab, rot.eulerAngles.y, pos, ok ? Color.green : Color.red);
            }
        }

        #endregion

        #region Spray-can reskin (hold spray can, click your object -> skin picker)

        // Vanilla reskin is dead here: SprayCan.ValidateReskin fails on
        // IsBuildBlockedByMonument BEFORE any hook runs, client-side too. So the
        // plugin does it directly: entity.skinID + SendNetworkUpdate, chosen from
        // a CUI grid (icons rendered client-side from ItemId+SkinId).

        private const string SkinUiName = "ApartmentSkinUI";
        private const int SkinsPerPage = 18; // 6 x 3

        private class SkinEntry { public int DefId; public ulong SkinId; public string Name; }
        private readonly Dictionary<int, List<SkinEntry>> _skinCache = new Dictionary<int, List<SkinEntry>>();

        private List<SkinEntry> GetSkinsFor(ItemDefinition def)
        {
            if (_skinCache.TryGetValue(def.itemid, out var cached)) return cached;
            var list = new List<SkinEntry>();
            var seen = new HashSet<ulong>();
            if (def.skins != null)
                foreach (var s in def.skins)
                {
                    ulong sid = ItemDefinition.FindSkin(def.itemid, s.id);
                    if (sid == 0 || !seen.Add(sid)) continue;
                    list.Add(new SkinEntry { DefId = s.id, SkinId = sid, Name = s.name });
                }
            if (def.skins2 != null)
                foreach (var s in def.skins2)
                {
                    if (s == null || s.WorkshopId == 0 || !seen.Add(s.WorkshopId)) continue;
                    list.Add(new SkinEntry { DefId = s.DefinitionId, SkinId = s.WorkshopId, Name = s.Name });
                }
            _skinCache[def.itemid] = list;
            return list;
        }

        private bool TryGetSprayContext(BasePlayer player, out bool renter)
        {
            renter = false;
            if (!_config.ReskinEnabled) return false;
            if (!(player.GetHeldEntity() is SprayCan)) return false;
            if (!NearApartmentMonument(player.transform.position)) return false;
            return TryGetDeployMode(player, out renter, out _);
        }

        private BaseEntity AimedOwnedEntity(BasePlayer player, bool renter)
        {
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 10f, ~0, QueryTriggerInteraction.Ignore)) return null;
            var ent = hit.collider ? hit.collider.GetComponentInParent<BaseEntity>() : null;
            if (ent == null || ent.IsDestroyed || ent.OwnerID == 0) return null; // plugin-placed only
            if (renter && ent.OwnerID != player.userID) return null;
            return ent;
        }

        private void HandleSprayInput(BasePlayer player, bool fire)
        {
            if (!fire) return;
            if (!TryGetSprayContext(player, out var renter)) return;
            var ent = AimedOwnedEntity(player, renter);
            if (ent == null) return; // let normal graffiti spraying be
            OpenSkinUi(player, ent, 0);
        }

        // Ownership is HARDCODED — no config, no admin bypass. Everyone can only
        // see and apply skins they own (same check as the vanilla spray can).
        private List<SkinEntry> SkinChoicesFor(BasePlayer player, ItemDefinition def)
        {
            var skins = GetSkinsFor(def)
                .Where(s => player.blueprints.CheckSkinOwnership(s.DefId, player)).ToList();
            var all = new List<SkinEntry> { new SkinEntry { DefId = 0, SkinId = 0, Name = "Default" } };
            all.AddRange(skins);
            return all;
        }

        private void OpenSkinUi(BasePlayer player, BaseEntity ent, int page)
        {
            var def = ItemDefForEntity(ent);
            if (def == null) { player.ChatMessage(L("NoSkins", player)); return; }

            var all = SkinChoicesFor(player, def);
            int pages = Mathf.Max(1, Mathf.CeilToInt(all.Count / (float)SkinsPerPage));
            page = Mathf.Clamp(page, 0, pages - 1);
            var slice = all.Skip(page * SkinsPerPage).Take(SkinsPerPage).ToList();
            ulong entId = ent.net.ID.Value;

            CuiHelper.DestroyUi(player, SkinUiName);
            var ui = new CuiElementContainer();

            ui.Add(new CuiPanel
            {
                Image = { Color = "0.09 0.10 0.11 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-370 -260", OffsetMax = "370 260" },
                CursorEnabled = true
            }, "Overlay", SkinUiName);

            ui.Add(new CuiLabel
            {
                Text = { Text = L("SkinTitle", player, def.displayName.english.ToUpper(), all.Count - 1), FontSize = 16, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "16 -40", OffsetMax = "-48 -6" }
            }, SkinUiName);

            ui.Add(new CuiButton
            {
                Button = { Color = "0.75 0.22 0.17 1", Close = SkinUiName },
                Text = { Text = "✕", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-38 -38", OffsetMax = "-8 -8" }
            }, SkinUiName);

            for (int i = 0; i < slice.Count; i++)
            {
                var s = slice[i];
                int col = i % 6, row = i / 6;
                float x0 = 18 + col * 118, yTop = 52 + row * 142;
                string cell = $"{SkinUiName}.C{i}";

                ui.Add(new CuiPanel
                {
                    Image = { Color = "0.16 0.17 0.18 1" },
                    RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{x0} -{yTop + 132}", OffsetMax = $"{x0 + 112} -{yTop}" }
                }, SkinUiName, cell);

                if (s.SkinId != 0 || def != null)
                {
                    ui.Add(new CuiElement
                    {
                        Name = cell + ".Icon",
                        Parent = cell,
                        Components =
                        {
                            new CuiImageComponent { ItemId = def.itemid, SkinId = s.SkinId },
                            new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-46 -100", OffsetMax = "46 -8" }
                        }
                    });
                }

                ui.Add(new CuiLabel
                {
                    Text = { Text = s.Name, FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "0.85 0.85 0.85 1" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "2 2", OffsetMax = "-2 28" }
                }, cell);

                // Transparent click-catcher LAST so the icon doesn't eat the click.
                ui.Add(new CuiButton
                {
                    Button = { Color = "0 0 0 0", Command = $"apartment.skinpick {entId} {s.SkinId} {page}" },
                    Text = { Text = "" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" }
                }, cell);
            }

            // Pagination
            ui.Add(new CuiLabel
            {
                Text = { Text = L("PageXY", player, page + 1, pages), FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "0.8 0.8 0.8 1" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "-60 8", OffsetMax = "60 34" }
            }, SkinUiName);
            if (page > 0)
                ui.Add(new CuiButton
                {
                    Button = { Color = "0.25 0.27 0.30 1", Command = $"apartment.skinui {entId} {page - 1}" },
                    Text = { Text = "<", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "-110 8", OffsetMax = "-70 34" }
                }, SkinUiName);
            if (page < pages - 1)
                ui.Add(new CuiButton
                {
                    Button = { Color = "0.25 0.27 0.30 1", Command = $"apartment.skinui {entId} {page + 1}" },
                    Text = { Text = ">", FontSize = 14, Align = TextAnchor.MiddleCenter, Color = "1 1 1 1" },
                    RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "70 8", OffsetMax = "110 34" }
                }, SkinUiName);

            CuiHelper.AddUi(player, ui);
        }

        // Resolve + authorize the UI-command target (UI commands are forgeable —
        // never trust the args).
        private BaseEntity ResolveSkinTarget(BasePlayer player, ulong entId, out bool renter)
        {
            renter = false;
            if (player == null || !_config.ReskinEnabled) return null;
            if (!TryGetDeployMode(player, out renter, out _)) return null;
            var ent = BaseNetworkable.serverEntities.Find(new NetworkableId(entId)) as BaseEntity;
            if (ent == null || ent.IsDestroyed || ent.OwnerID == 0) return null;
            if (renter && ent.OwnerID != player.userID) return null;
            if (!NearApartmentMonument(ent.transform.position)) return null;
            return ent;
        }

        [ConsoleCommand("apartment.skinui")]
        private void ConsoleSkinUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var ent = ResolveSkinTarget(player, arg.GetUInt64(0, 0), out _);
            if (ent != null) OpenSkinUi(player, ent, arg.GetInt(1, 0));
        }

        [ConsoleCommand("apartment.skinpick")]
        private void ConsoleSkinPick(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var ent = ResolveSkinTarget(player, arg.GetUInt64(0, 0), out _);
            if (ent == null) return;
            ulong skinId = arg.GetUInt64(1, 0);

            var def = ItemDefForEntity(ent);
            if (def == null) return;
            if (skinId != 0)
            {
                var entry = GetSkinsFor(def).FirstOrDefault(s => s.SkinId == skinId);
                if (entry == null) return;
                // Hardcoded: no one applies a skin they don't own.
                if (!player.blueprints.CheckSkinOwnership(entry.DefId, player)) return;
            }

            ent.skinID = skinId;
            ent.SendNetworkUpdate();
            // Keep the picker open on the same page so skins can be tried live.
            OpenSkinUi(player, ent, arg.GetInt(2, 0));
        }

        #endregion

        #region Hold-item placement (vanilla-feel, no commands)

        // A renter simply HOLDS a deployable item while standing in their rented
        // room: a ghost outline follows their crosshair (green = OK, red = outside
        // the room), LEFT-CLICK places it, R rotates it. The game's own ghost
        // stays red (client-side monument block) and the client never sends a
        // build request — but button presses stream to the server every input
        // tick, so OnPlayerInput sees the click and we place server-side.

        private class HoldState { public int ItemId; public string Key; public string Prefab; public float Yaw; }
        private readonly Dictionary<ulong, HoldState> _holds = new Dictionary<ulong, HoldState>();

        private void UpdateHoldPreviews()
        {
            if (!_config.RenterDeployEnabled || !_config.RenterHoldPlacement)
            {
                if (_holds.Count > 0) _holds.Clear();
                return;
            }

            // Drop entries for players who logged off (activePlayerList loop below
            // only clears entries for players still online).
            if (_holds.Count > 0)
            {
                List<ulong> gone = null;
                foreach (var id in _holds.Keys)
                {
                    var p = BasePlayer.FindByID(id);
                    if (p == null || !p.IsConnected) (gone ?? (gone = new List<ulong>())).Add(id);
                }
                if (gone != null) foreach (var id in gone) _holds.Remove(id);
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || !player.IsConnected) continue;
                if (!TryGetHoldContext(player, out var item, out var mod, out var room, out var renter)) { _holds.Remove(player.userID); continue; }

                if (!_holds.TryGetValue(player.userID, out var hs) || hs.ItemId != item.info.itemid)
                    _holds[player.userID] = hs = new HoldState
                    {
                        ItemId = item.info.itemid,
                        Key = item.info.shortname,
                        Prefab = mod.entityPrefab.resourcePath,
                        // +180: deployables' "front" faces the deployer in vanilla.
                        Yaw = (player.eyes.rotation.eulerAngles.y + 180f) % 360f
                    };

                if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore)) continue;
                string countStr = room != null ? $" ({CountPluginFurniture(room)}/{_config.RenterMaxPerRoom})" : "";

                // Mountables (storage adaptor, industrial crafter) preview on
                // their host entity instead of the floor.
                var kind = GetMountKind(hs.Prefab);
                if (kind != MountKind.None)
                {
                    var target = FindMountTarget(kind, hit, player, renter, out _);
                    bool mountOk = target != null;
                    Vector3 pos = mountOk ? MountPosition(kind, target) : hit.point;
                    float yaw = mountOk ? target.transform.rotation.eulerAngles.y : hs.Yaw;
                    string hint = L(kind == MountKind.StorageAdaptor ? "HintAimContainer" : "HintAimWorkbench", player);
                    DrawGhost(player, $"{hs.Key}{countStr} — {(mountOk ? L("HintAttach", player) : hint)}",
                        hs.Prefab, yaw, pos, mountOk ? Color.green : Color.red);
                    continue;
                }

                bool ok;
                if (renter)
                {
                    var aimRoom = FindRentedRoomAt(hit.point);
                    ok = aimRoom != null && aimRoom.IsAuthed(player.userID);
                }
                else ok = NearApartmentMonument(hit.point);

                DrawGhost(player, $"{hs.Key}{countStr} — {L("HintPlace", player)}",
                    hs.Prefab, hs.Yaw, GroundedSpawnPos(hs.Prefab, hit.point), ok ? Color.green : Color.red);
            }
        }

        // Hold-placement applies when: no command preview running, the active item
        // is a deployable, and either (renter) the player is STANDING inside a
        // room they rent, or (full mode / admins) they're anywhere in the
        // monument. Blocklist/permission gates apply to renters only.
        private bool TryGetHoldContext(BasePlayer player, out Item item, out ItemModDeployable mod, out ApartmentRoom room, out bool renter)
        {
            item = null; mod = null; room = null; renter = false;
            if (_previews.ContainsKey(player.userID)) return false;
            item = player.GetActiveItem();
            mod = item != null && item.info != null ? item.info.GetComponent<ItemModDeployable>() : null;
            if (mod == null || mod.entityPrefab == null || string.IsNullOrEmpty(mod.entityPrefab.resourcePath)) return false;
            if (!TryGetDeployMode(player, out renter, out _)) return false;

            if (renter)
            {
                if (_config.RenterBlockedItems != null && _config.RenterBlockedItems.Contains(item.info.shortname, StringComparer.OrdinalIgnoreCase)) return false;
                if (_config.RenterRequirePermission && !permission.UserHasPermission(player.UserIDString, PermRenter)) return false;
                room = FindRentedRoomAt(player.transform.position);
                return room != null && room.IsAuthed(player.userID);
            }
            return NearApartmentMonument(player.transform.position);
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null) return;
            bool fire = input.WasJustPressed(BUTTON.FIRE_PRIMARY);
            bool alt = input.WasJustPressed(BUTTON.FIRE_SECONDARY);
            bool reload = input.WasJustPressed(BUTTON.RELOAD);
            if (!fire && !alt && !reload) return;

            if (_holds.TryGetValue(player.userID, out var hs))
            {
                if (reload) hs.Yaw = (hs.Yaw + _config.RotateStepDegrees) % 360f;
                else if (fire) player.ChatMessage(DoHoldPlace(player, hs));
                return;
            }

            HandlePipeToolInput(player, fire, alt, reload);
            HandleWallpaperInput(player, fire);
            HandleSprayInput(player, fire);
        }

        private string DoHoldPlace(BasePlayer player, HoldState hs)
        {
            // Re-validate everything at click time (the preview tick may be 200ms old).
            if (!TryGetHoldContext(player, out var item, out _, out _, out var renter) || item.info.itemid != hs.ItemId)
                return L("HoldContext", player);
            if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, 20f, ~0, QueryTriggerInteraction.Ignore))
                return L("AimSurface", player);

            // Storage adaptors / industrial crafters attach to a host entity
            // instead of free-placing.
            var mountKind = GetMountKind(hs.Prefab);
            if (mountKind != MountKind.None)
                return DoMountPlace(player, hs, item, hit, mountKind, renter);

            var room = FindRentedRoomAt(hit.point);
            int placed = room != null ? CountPluginFurniture(room) : 0;
            if (renter)
            {
                if (room == null || !room.IsAuthed(player.userID))
                    return L("OwnRoomOnly", player);
                if (placed >= _config.RenterMaxPerRoom)
                    return L("RoomLimit", player, placed, _config.RenterMaxPerRoom);
            }
            else if (!NearApartmentMonument(hit.point))
                return L("AimMonument", player);

            ulong skin = item.skin;
            var ent = GameManager.server.CreateEntity(hs.Prefab, GroundedSpawnPos(hs.Prefab, hit.point), Quaternion.Euler(0f, hs.Yaw, 0f));
            if (ent == null) return L("PrefabFailed", player, hs.Prefab);
            item.UseItem(1); // held-item flows always consume the item

            ent.skinID = skin;
            ent.OwnerID = player.userID;
            ent.Spawn();

            if (room != null)
            {
                RegisterRoomFurniture(room, ent, player.userID);
                return L("PlacedRoomHold", player, hs.Key, placed + 1, _config.RenterMaxPerRoom, room.RoomNumber);
            }

            _spawned.Add(ent);
            HardenOutdoorPlacement(ent);
            SaveSpawnedData();
            return L("PlacedOutdoorHold", player, hs.Key, ent.ShortPrefabName);
        }

        #endregion

    }
}
