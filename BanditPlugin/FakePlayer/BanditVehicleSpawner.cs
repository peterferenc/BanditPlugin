using System;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Puts a vehicle on the ground.
    ///
    /// Nothing in this plugin could do that before: every vehicle path went looking through
    /// VehicleManager.vehicles for something a mapper had already placed, which is fine for trying
    /// a bot in the nearest truck and useless for an event that has to bring its own.
    ///
    /// The spawn itself is vanilla's own VehicleManager.spawnVehicleV2, so the vehicle is a real
    /// networked one in every respect - it is saved, it is respawned, players can drive it, and it
    /// is destroyed the same way any other is. What is worth care here is not the spawn call but
    /// the two things either side of it: working out which asset was meant, and dropping it
    /// somewhere it will settle rather than somewhere it will explode.
    /// </summary>
    public static class BanditVehicleSpawner
    {
        /// <summary>
        /// How far above the ground a vehicle is released. It has to be clear of the terrain or
        /// vanilla's own collision resolution fires it into the sky, and it should not be so high
        /// that it lands hard enough to hurt the crew that is about to get in.
        /// </summary>
        private const float DropHeight = 1.5f;

        /// <summary>
        /// Every vehicle this plugin has put on the ground, so "/banditclear" can take them away
        /// again.
        ///
        /// A spawned vehicle is a real one and outlives its crew completely: kicking every bot on
        /// the server leaves the trucks sitting exactly where they were. Bots can be found again by
        /// walking Provider.clients, but a vehicle carries nothing that says who spawned it, so this
        /// list is the only record that it was ours - and without it, an evening of testing events
        /// leaves a scrapyard behind.
        /// </summary>
        private static readonly System.Collections.Generic.List<InteractableVehicle> Spawned =
            new System.Collections.Generic.List<InteractableVehicle>();

        /// <summary>
        /// Finds the asset a configured <see cref="BanditVehicleType.Vehicle"/> string means.
        ///
        /// Both forms vanilla uses are accepted, and both are needed. Current vanilla content
        /// carries only a GUID - the Offroader has no legacy ID at all - while older assets and a
        /// good deal of workshop content are still addressed by number. Requiring one form would
        /// simply make half the game unreachable from the configuration file.
        ///
        /// Dashes and braces in a GUID are tolerated, since that is how the same value gets pasted
        /// out of different tools.
        /// </summary>
        public static VehicleAsset Resolve(string vehicle, out string error)
        {
            if (string.IsNullOrEmpty(vehicle))
            {
                error = "no vehicle ID or GUID given";
                return null;
            }

            string trimmed = vehicle.Trim();

            // A legacy ID first, because it is the cheaper test and the two forms cannot collide -
            // a GUID is 32 hex digits and never parses as a ushort.
            if (ushort.TryParse(trimmed, out ushort legacyId))
            {
                VehicleAsset byId = Assets.find(EAssetType.VEHICLE, legacyId) as VehicleAsset;
                if (byId == null)
                {
                    // Worth being specific, because the usual cause is not a typo. Vanilla has been
                    // moving its own content off legacy IDs, and the ones it has moved have no ID
                    // line in the .dat at all - the Ural, the Offroader and the Tank among them - so
                    // a number that was correct for years now finds nothing. Workshop content, which
                    // still numbers itself, keeps working, which is what makes it look selective.
                    error = $"no vehicle with legacy ID {legacyId} is loaded on this map "
                        + "(current vanilla content is addressed by GUID or name, not by number)";
                    return null;
                }

                error = null;
                return byId;
            }

            string cleaned = trimmed.Replace("-", string.Empty).Replace("{", string.Empty).Replace("}", string.Empty);
            if (!Guid.TryParse(cleaned, out Guid guid))
            {
                // Not a number and not a GUID, so try it as an asset name - the same fallback the
                // game's own /v takes, and the only form of the three that a person can read.
                VehicleAsset byName = FindByName(trimmed);
                if (byName != null)
                {
                    error = null;
                    return byName;
                }

                error = $"'{vehicle}' is not a legacy vehicle ID, a GUID, or the name of a loaded vehicle";
                return null;
            }

            VehicleAsset byGuid = Assets.find<VehicleAsset>(guid);
            if (byGuid == null)
            {
                error = $"no vehicle with GUID {cleaned} is loaded on this map";
                return null;
            }

            error = null;
            return byGuid;
        }

        /// <summary>
        /// A vehicle by its asset name - "Ural", "Off_Roader" - matched the way vanilla's own
        /// vehicle command matches it, on the asset name rather than the localised friendly one, so
        /// the same string works whatever language the server runs in.
        /// </summary>
        private static VehicleAsset FindByName(string name)
        {
            System.Collections.Generic.List<VehicleAsset> all =
                new System.Collections.Generic.List<VehicleAsset>();
            Assets.find(all);

            foreach (VehicleAsset asset in all)
            {
                if (asset != null && string.Equals(name, asset.name, StringComparison.InvariantCultureIgnoreCase))
                {
                    return asset;
                }
            }

            return null;
        }

        /// <summary>
        /// Spawns a vehicle at a point, facing a heading.
        ///
        /// The position is ground-snapped and then lifted, rather than used as given: a vehicle
        /// spawned with its origin on the terrain has half its hull inside it, and vanilla's physics
        /// resolves that overlap by launching the thing. Snapping and dropping means it falls the
        /// last metre and settles, which also lets it sit correctly on a slope without anyone here
        /// having to work out the surface normal.
        /// </summary>
        public static InteractableVehicle Spawn(VehicleAsset asset, Vector3 position, float facing, out string error)
        {
            if (asset == null)
            {
                error = "no vehicle asset";
                return null;
            }

            Vector3 ground = BanditPlacement.SnapToGround(position);
            Vector3 spawnPoint = ground + Vector3.up * DropHeight;

            // The asset overload, not the one taking a legacy ID. Current vanilla content carries
            // only a GUID - the Offroader's .dat has no ID line at all - and an asset addressed that
            // way has an id of 0, which the ID overload would then fail to find. Handing over the
            // asset we already resolved skips the lookup entirely and works for both forms.
            InteractableVehicle vehicle = VehicleManager.spawnVehicleV2(
                asset, spawnPoint, Quaternion.Euler(0f, facing, 0f));

            if (vehicle == null)
            {
                // spawnVehicleInternal returns null for an asset that is not really a vehicle - a
                // redirector pointing at nothing, most likely - which is worth saying plainly
                // because the ID resolved perfectly well a moment ago.
                error = $"the server refused to spawn {asset.FriendlyName}";
                return null;
            }

            Spawned.Add(vehicle);

            error = null;
            return vehicle;
        }

        /// <summary>
        /// Destroys every vehicle this plugin spawned, whether an event put it there or "/banditv"
        /// did.
        ///
        /// Vanilla's askVehicleDestroy is used rather than anything of ours: it removes the
        /// occupants first, so a bandit still sitting in one is not left parented to a vehicle that
        /// no longer exists. Vehicles that a player has since destroyed are simply skipped.
        /// </summary>
        public static int DestroyAll()
        {
            int destroyed = 0;

            foreach (InteractableVehicle vehicle in Spawned)
            {
                if (vehicle == null || vehicle.isExploded)
                {
                    continue;
                }

                VehicleManager.askVehicleDestroy(vehicle);
                destroyed++;
            }

            Spawned.Clear();
            return destroyed;
        }

        /// <summary>
        /// Spawns by whatever the caller typed or configured, resolving the asset on the way.
        /// </summary>
        public static InteractableVehicle Spawn(string vehicle, Vector3 position, float facing, out string error)
        {
            VehicleAsset asset = Resolve(vehicle, out error);
            return asset == null ? null : Spawn(asset, position, facing, out error);
        }

        /// <summary>
        /// Which seats of this asset hold a turret, as a readable list, or null if none do.
        ///
        /// Only used to tell people things: a crew configured into seat 2 of something whose turret
        /// is on seat 1 produces a passenger who sits quietly through the whole fight, and there is
        /// no way to work that out from in game except by trying every seat yourself. The asset
        /// knows, so "/banditevent check" and the vehicle spawn reply say so.
        /// </summary>
        public static string DescribeTurretSeats(VehicleAsset asset)
        {
            if (asset == null || asset.turrets == null || asset.turrets.Length == 0)
            {
                return null;
            }

            string[] seats = new string[asset.turrets.Length];
            for (int i = 0; i < asset.turrets.Length; i++)
            {
                seats[i] = asset.turrets[i].seatIndex.ToString();
            }

            return string.Join(", ", seats);
        }
    }
}
