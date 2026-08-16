using System.Collections.Generic;
using Rocket.API;

namespace BanditPlugin
{
    public class BanditConfiguration : IRocketPluginConfiguration
    {
        /// <summary>
        /// Master switch for the loadout below. Off spawns bandits naked and empty-handed.
        /// </summary>
        public bool ApplyLoadout = true;

        /// <summary>
        /// The classes a bandit can be spawned as: "/bandit mg", "/bandit marksman". Each carries
        /// its own loadout and its own combat figures, resolved once at spawn, so bandits of
        /// different classes fight differently under one configuration. See BanditKit.
        ///
        /// Starts EMPTY, and must. XmlSerializer does not replace a collection it finds already
        /// populated - it calls Add for every element in the file on top of whatever the field
        /// initializer built. Seeding the defaults here therefore appended the file's four kits to
        /// the initializer's four, and because Rocket's XMLFileAsset.Load() saves immediately after
        /// deserializing, the doubled list was written straight back out: four kits became eight,
        /// then sixteen, once per server start. BanditPlugin.Load fills this in after the fact
        /// instead, which is the only point at which "the file didn't have any" can be told apart
        /// from "the file's are already loaded".
        /// </summary>
        public List<BanditKit> Kits = new List<BanditKit>();

        /// <summary>
        /// Which kit a plain "/bandit" spawns. Blank falls back to <see cref="Loadout"/> and the
        /// global figures below, which is what every bandit used before kits existed.
        /// </summary>
        public string DefaultKit = "rifleman";

        /// <summary>
        /// What a bandit spawned with no kit wears and carries - see <see cref="DefaultKit"/>.
        /// BanditLoadout has the slot list and a table of GUIDs read out of this server's own
        /// Bundles folder. A kit ignores this entirely and brings its own.
        /// </summary>
        public BanditLoadout Loadout = new BanditLoadout();

        /// <summary>
        /// Distance at which a bandit carrying a secondary swaps to it, so a sidearm gets used at
        /// room distance and the rifle everywhere else. It swaps back a few metres further out than
        /// it swaps in, so a target hovering on the boundary doesn't make the bot spend the fight
        /// changing weapons. 0 (the default) keeps the primary out at all ranges.
        /// </summary>
        public float SecondaryWeaponRange = 0f;

        /// <summary>
        /// Refill the magazine whenever it runs dry. The bot has no client to press reload, so
        /// without this it fires one magazine and then stands there. Refills to the capacity of the
        /// magazine actually attached to whichever gun is in its hands.
        /// </summary>
        public bool InfiniteAmmo = true;

        /// <summary>
        /// Spawn bandits holding fire, so a new one tracks you, takes cover and walks its route
        /// without ever pulling the trigger until /bandit shoot start gives it weapons free.
        ///
        /// Fire control is per-bandit and applied to whoever is alive when the command runs, so a
        /// bandit spawned after a /bandit shoot start begins held again - set this false to make
        /// weapons free the standing default instead. Same for CoverByDefault and PeekByDefault.
        /// </summary>
        public bool HoldFireByDefault = true;

        public float TurnSpeedDegreesPerSecond = 180f;
        public float ScanIntervalSeconds = 0.5f;
        public float FireIntervalSeconds = 0.6f;
        public float AimToleranceDegrees = 10f;
        public float FireRange = 50f;

        /// <summary>
        /// Furthest a bandit notices anyone at all, as opposed to being able to shoot them.
        ///
        /// This used to be unbounded: the target scan walked every connected player, took the
        /// nearest one it had line of sight to at any distance whatsoever, and turned the body onto
        /// them - so a bandit would stand tracking someone across a valley it could never reach.
        /// Kept comfortably above FireRange, because watching a target close the last stretch is
        /// the behaviour worth having; it is the four-hundred-metre stare that is not.
        /// </summary>
        public float TargetAcquireRange = 140f;

        /// <summary>
        /// Fire in bursts rather than one aimed shot every FireIntervalSeconds.
        ///
        /// A burst is produced by holding the trigger down, not by clicking faster: the bot sends
        /// one attack Start, leaves it latched across the next several packets, and releases once
        /// the configured number of rounds has actually left the barrel. Rounds inside a burst
        /// therefore come out at the gun's own cadence - vanilla's UseableGun.tockShoot() paces
        /// itself against the asset's Firerate, so an Eaglefire really does burst at its 600rpm -
        /// and BurstIntervalSeconds becomes the gap between bursts rather than between rounds.
        ///
        /// Clicking faster is not an alternative: PlayerEquipment.isBusy stays set for 150ms after
        /// every shot and UseableGun.startPrimary() refuses while it is, so no amount of re-pulling
        /// beats about four rounds a second however fast the gun is.
        ///
        /// Holding the trigger only does anything on a gun set to automatic, so turning this on also
        /// changes the firemode the loadout is given - see BanditLoadoutApplier.SetFiremode, which
        /// explains why that works even for a semi-only gun like the Eaglefire. Bolt-actions and
        /// pump shotguns are the exception and stay on semi, firing one round per interval as
        /// before: their round count between rechambers is enforced where a held trigger never
        /// looks, so bursting one would turn it into a machinegun.
        /// </summary>
        public bool BurstFire = false;

        /// <summary>
        /// Rounds per burst, drawn fresh for each burst so a squad of bandits doesn't fire in
        /// lockstep. Both ends inclusive; set them equal for a fixed size, or both to 1 to keep
        /// single shots at the burst cadence.
        ///
        /// Either weapon in the loadout can override this pair for as long as it is the one in the
        /// bot's hands, via BanditWeapon - which is how a rifle gets 3-4 and a machinegun 5-6 from
        /// one config.
        /// </summary>
        public int BurstMinRounds = 3;

        /// <summary>Upper end of the burst size draw. See <see cref="BurstMinRounds"/>.</summary>
        public int BurstMaxRounds = 4;

        /// <summary>
        /// Pause between bursts, used in place of FireIntervalSeconds while BurstFire is on.
        ///
        /// Worth setting deliberately rather than matching FireIntervalSeconds: 3-4 rounds every
        /// 1.1s is roughly twice the sustained fire of one round every 0.6s, so a careless value
        /// makes bandits abruptly deadlier instead of differently deadly. Raise it, or lower
        /// AimHitChance, if bursts turn out to kill too fast.
        /// </summary>
        public float BurstIntervalSeconds = 1.1f;

        /// <summary>
        /// How much the aim error grows over a burst - a stand-in for the recoil climb a real player
        /// fights, and the reason a burst is not simply strictly better than a single shot.
        ///
        /// Round n of a burst is fired with its miss distance scaled by 1 + this * n, so at the
        /// default a 4-round burst walks from the configured AimHitChance out to roughly double the
        /// spread by the last round. 0 makes every round as accurate as the first. Note that
        /// AimMaxErrorDegrees still caps the result, so the ramp does less at point blank range,
        /// where it is already clamped.
        /// </summary>
        public float BurstErrorRampPerRound = 0.35f;

        /// <summary>
        /// Fraction of shots whose line passes within AimTargetRadius of the target's chest, i.e.
        /// roughly the bot's hit rate. The aim error needed for this is derived from the distance
        /// to the target, so the bot is no more accurate up close than it is far away in hitbox
        /// terms. 1 restores the old perfect aimbot.
        ///
        /// Either weapon in the loadout can override this for as long as it is the one in the bot's
        /// hands, via BanditWeapon.AimHitChance.
        /// </summary>
        public float AimHitChance = 0.3f;

        /// <summary>
        /// Half-width in metres of the area treated as "on target" when solving for the aim error
        /// above - roughly half a torso.
        /// </summary>
        public float AimTargetRadius = 0.35f;

        /// <summary>
        /// Half-height of that same area. Bigger than the width because a shot that drifts low off
        /// the chest still finds a stomach or a leg, where the same drift sideways finds only air.
        /// Together these two are an ellipse standing in for the player's hitboxes, so they are an
        /// approximation: if the measured hit rate comes out off, tune AimHitChance to taste.
        /// </summary>
        public float AimTargetHalfHeight = 0.8f;

        /// <summary>
        /// Hard cap on the aim error, so that at point blank range - where a torso-width miss is a
        /// huge angle - the bot doesn't visibly flail sideways.
        /// </summary>
        public float AimMaxErrorDegrees = 8f;

        /// <summary>
        /// How much of its aim error a bandit keeps while crouched, and while prone.
        ///
        /// Bracing. A bipod on the ground is steadier than a rifle held at the shoulder, and
        /// without something like this getting low is all cost and no benefit - a smaller target
        /// that shoots exactly as badly, which is no reason for a machinegunner to ever lie down.
        ///
        /// It multiplies the miss distance drawn in SampleAimError, so it tightens the group and
        /// quietens the visible sway in the same stroke - the wobble between shots is drawn from
        /// the same figure. 1 is no bracing bonus at all.
        ///
        /// The effect is not linear: a hit chance p becomes 1-(1-p)^(1/m^2), so at these defaults
        /// the machinegunner's 0.22 standing becomes 0.32 crouched and 0.44 prone. Worth knowing
        /// before raising them, and worth knowing that this applies to ANY bandit in the stance -
        /// including a rifleman crouched in cover, whose 0.35 becomes 0.49. Deliberately modest for
        /// that reason: a big bonus here quietly makes every squad far deadlier.
        /// </summary>
        public float CrouchedAimErrorMultiplier = 0.8f;

        /// <summary>Prone is steadier still. See <see cref="CrouchedAimErrorMultiplier"/>.</summary>
        public float ProneAimErrorMultiplier = 0.65f;

        /// <summary>How often a fresh aim error is drawn while the bot is holding aim between shots.</summary>
        public float AimWobbleIntervalSeconds = 0.35f;

        /// <summary>
        /// Time constant for drifting toward a newly drawn aim error. Larger is a slower, lazier
        /// sway; 0 makes the aim snap between samples.
        /// </summary>
        public float AimWobbleSmoothingSeconds = 0.15f;

        /// <summary>
        /// Require an unobstructed line from the bot's eye to the target's before targeting or
        /// firing, so the bot cannot see or shoot through walls, rocks or vehicles.
        /// </summary>
        public bool RequireLineOfSight = true;

        /// <summary>
        /// Master switch for the whole movement/patrol/cover layer. Off makes bandits the
        /// stationary turrets they used to be.
        /// </summary>
        public bool MovementEnabled = true;

        /// <summary>How close, horizontally, counts as having reached a destination.</summary>
        public float ArriveRadius = 2f;

        /// <summary>How often a moving bot asks A* for a fresh path.</summary>
        public float RepathIntervalSeconds = 2.5f;

        /// <summary>
        /// How far a point may be from the navmesh and still be pathed to. AstarPath's own
        /// maxNearestNodeDistance is 100m, which would snap a bot standing in a field onto the
        /// graph of the next town; beyond this distance the bot steers directly instead.
        /// </summary>
        public float NavmeshSnapDistance = 3f;

        /// <summary>Allow requesting sprint while travelling. Vanilla still gates it on stamina.</summary>
        public bool AllowSprint = true;

        /// <summary>Allow jumping over low obstacles and when wedged.</summary>
        public bool AllowJumping = true;

        /// <summary>
        /// Walk toward a target further away than PreferredEngagementRange. Off by default: a
        /// bandit that closes on whoever it sees is a chase behaviour, and it overrides whatever
        /// the bot was doing. Only applies when it has no order or patrol to follow.
        /// </summary>
        public bool AdvanceOnTarget = false;

        /// <summary>
        /// After losing contact, go and look at the last place the target was seen, or back along
        /// the bullet when shot by someone unseen. Off by default for the same reason as
        /// AdvanceOnTarget - it makes the bot leave its post.
        /// </summary>
        public bool InvestigateEnabled = false;

        /// <summary>
        /// Seconds a killed bandit lies there before being removed. A bot has no client to press
        /// respawn, so without this its corpse holds a player slot forever. Negative disables the
        /// cleanup and leaves the body until /banditclear.
        /// </summary>
        public float DespawnSecondsAfterDeath = 5f;

        /// <summary>Range the bot tries to fight at, and scores cover positions against.</summary>
        public float PreferredEngagementRange = 25f;

        /// <summary>
        /// Keep the tank full while a bandit occupies a vehicle. On for testing, and it is not only
        /// convenience: an empty tank is the one case vanilla tightens the anti-teleport check on a
        /// car from the asset's own delta down to half a metre a packet, so a bandit that runs dry
        /// mid-drive stops being able to move at anything like a sensible speed.
        /// </summary>
        public bool VehicleInfiniteFuel = true;

        /// <summary>
        /// Keep the battery charged while a bandit occupies a vehicle. Vanilla only turns the engine
        /// on when the battery has charge, so a flat one leaves the bandit sitting in a dead vehicle
        /// with its lights off. Health is deliberately not topped up - a bandit's vehicle is meant
        /// to be destructible.
        /// </summary>
        public bool VehicleInfiniteBattery = true;

        /// <summary>
        /// Spawn bandits already looking for cover when a target can see them or they are being
        /// shot. Off by default, so a fresh bandit stands where you put it until /bandit cover
        /// start. Like fire control, this is a per-bandit standing order rather than a global
        /// switch, so this only sets what a newly spawned one starts with.
        /// </summary>
        public bool CoverByDefault = false;

        /// <summary>
        /// Spawn bandits already peeking - alternating hiding with stepping out to shoot - once
        /// they are in cover. Off by default; /bandit peek start turns it on. With it off a bandit
        /// that takes cover goes down and stays down.
        /// </summary>
        public bool PeekByDefault = false;

        /// <summary>
        /// Radius searched for cover around the bot. Comfortably beyond PreferredEngagementRange,
        /// so a bandit can fall back to real cover rather than only what is already at arm's reach.
        /// Cost scales with it - see CoverRingSamples.
        /// </summary>
        public float CoverSearchRadius = 32f;

        /// <summary>Minimum gap between cover searches - each one costs a burst of raycasts.</summary>
        public float CoverSearchIntervalSeconds = 3f;

        /// <summary>
        /// Blind samples per ring when searching for cover, on top of nearby colliders. Two rings
        /// are sampled, at 45% and 90% of the search radius, so this is the angular resolution of
        /// the fallback sweep: raising the radius without raising this spreads the same number of
        /// samples over a bigger circle and quietly makes the search coarser.
        /// </summary>
        public int CoverRingSamples = 20;

        /// <summary>
        /// Most candidate markers "/banditcover" will draw. Each marker is a paintball impact
        /// effect, and each of those spawns eight decals that vanilla never expires - so this is
        /// really a budget of maxMarkers x 8 objects on your client until they are cleared. The
        /// search itself is unaffected; only the drawing is capped, and the reported tallies always
        /// cover the whole search.
        /// </summary>
        public int CoverDebugMaxMarkers = 48;

        /// <summary>
        /// How long "/banditcover" markers stay before clearing themselves. 0 leaves them until the
        /// next search or "/banditcover clear".
        /// </summary>
        public float CoverDebugSeconds = 20f;

        /// <summary>
        /// Cover closer to the threat than this is ignored, so the bot doesn't "take cover" by
        /// walking into someone's lap. Kept low: the tree between you and a shooter ten metres
        /// away is legitimate cover, and a larger value silently rejects every candidate in a
        /// close-range fight.
        /// </summary>
        public float CoverMinimumThreatDistance = 3f;

        /// <summary>
        /// Sprint to cover while this much of the route is still ahead, and walk the rest.
        ///
        /// Vanilla refuses to sprint while aiming down sights, so a sprinting bandit has its rifle
        /// down and cannot shoot. Running the long open stretch is worth that; running the last few
        /// metres is not, so it walks the remainder with its gun up and keeps firing on the way in.
        /// Measured along the path where one exists, not straight-line. 0 disables sprinting to
        /// cover entirely. Also gated by AllowSprint, and by vanilla's own stamina rules.
        ///
        /// Low, because the alternative to running is usually worse than losing a few shots: a
        /// bandit ordered prone stands up to move at all (see BanditBrain.ApplyProneOrder), and
        /// walking those metres upright in the open is how it dies on the way to cover.
        /// </summary>
        public float SprintToCoverMinPathDistance = 5f;

        /// <summary>How long the bot stays hidden between peeks.</summary>
        public float CoverHideSeconds = 2.5f;

        /// <summary>How long the bot exposes itself to shoot before ducking back.</summary>
        public float CoverPeekSeconds = 2f;

        /// <summary>
        /// Which classes "/squadspawn" puts on the ground, in the order they are laid out from
        /// left to right. Any kit name is valid, and repeats are how you get two riflemen.
        ///
        /// Empty for the same reason <see cref="Kits"/> is - a seeded list gets the file's entries
        /// appended to it rather than replacing it, which is what turned a five-man squad into ten.
        /// </summary>
        public List<string> SquadComposition = new List<string>();

        /// <summary>The squad a fresh configuration starts with. See <see cref="SquadComposition"/>.</summary>
        public static List<string> DefaultSquadComposition()
        {
            return new List<string> { "rifleman", "rifleman", "mg", "marksman", "breacher" };
        }

        /// <summary>
        /// Metres between squad members as they are placed. Also the depth of the wedge, so the
        /// flanks sit back from the centre rather than the whole squad standing on one line.
        /// </summary>
        public float SquadSpacing = 5f;

        /// <summary>
        /// How far down your sightline "/squadspawn" puts the squad.
        ///
        /// Deliberately well past every kit's TargetAcquireRange, so a squad spawns unaware and you
        /// get to walk in on it rather than arriving mid-firefight. Spawning one on top of yourself
        /// skips the only part of the behaviour worth watching - the moment they notice.
        /// </summary>
        public float SquadSpawnDistance = 200f;

        /// <summary>
        /// Spawn a squad weapons free and already under its classes' standing orders, unlike a
        /// lone "/bandit" which stands inert until told. A squad exists to be watched fighting, and
        /// one that has to be switched on a command at a time is not a squad.
        /// </summary>
        public bool SquadWeaponsFree = true;

        /// <summary>
        /// How long a squad keeps acting on a sighting after the last member loses sight of it -
        /// cover held, machinegun still firing at the spot, nobody standing up. Longer than one
        /// bandit's own target memory on purpose: a squad that loses eyes on someone for a moment
        /// has not stopped being in contact with them.
        /// </summary>
        public float SquadContactMemorySeconds = 12f;

        /// <summary>
        /// Closest two squad members will deliberately take cover to each other.
        ///
        /// Without a separation the squad piles onto one spot, and not by chance: the cover finder
        /// is deterministic and scores candidates from the searcher's position and the threat
        /// alone, so bandits standing together facing the same way all pick the same coordinate.
        /// Worth keeping at least a couple of metres wide - close enough that they are still one
        /// squad behind one wall, far enough that one grenade is not all of them.
        /// </summary>
        public float SquadCoverSeparation = 4f;

        /// <summary>
        /// How long a squad member sits in contact without a shot before it stops waiting and does
        /// something about it: giving up its cover and searching again against where the enemy is
        /// now, and failing that, moving toward them until an angle opens up.
        ///
        /// This is what stops a squad going inert. Cover is chosen against the threat as it was at
        /// the time, and it is only given up when it stops *hiding* the bandit - so once the enemy
        /// shifts to a flank, everyone who cannot see the new angle stays tucked behind a rock
        /// facing the wrong way, perfectly safe and perfectly useless, while whoever happens to
        /// have the angle fights alone.
        ///
        /// Only applies to bandits in a squad. A lone one keeps holding the position it was given,
        /// which is what makes it useful for testing one behaviour at a time. 0 disables it.
        /// </summary>
        public float RepositionAfterNoShotSeconds = 5f;

        /// <summary>
        /// How long a suppressing bandit keeps firing at a position after the whole squad has lost
        /// sight of the enemy. While anyone can still see them this does not apply - the gunner
        /// keeps firing for as long as the contact is being reported.
        /// </summary>
        public float SuppressionSeconds = 6f;

        /// <summary>
        /// How wide a berth a bandit gives a squadmate standing in its line of fire. Measured
        /// perpendicular to the shot, so this is roughly "how close to a mate's shoulder a round
        /// may pass". Bandits do not target each other, but bullets are raycast and hit whatever is
        /// in the way, so without this a prone machinegunner cheerfully empties a belt into the
        /// backs of the two riflemen in front of it.
        /// </summary>
        public float FriendlyFireClearanceRadius = 0.9f;

        /// <summary>Start newly spawned bandits patrolling immediately.</summary>
        public bool PatrolByDefault = false;

        /// <summary>How long a bandit loiters at a waypoint before moving on.</summary>
        public float PatrolWaypointDwellSeconds = 3f;

        /// <summary>Return to the first waypoint after the last, rather than stopping.</summary>
        public bool PatrolLoop = true;

        /// <summary>
        /// When a map has no recorded waypoints, patrol between its LocationNodes - the named
        /// places official maps mark in the level editor. Those are town centres rather than
        /// verified walkable points, so a route recorded with /banditwp is always better.
        /// </summary>
        public bool PatrolUseLocationNodesWhenNoWaypoints = true;

        /// <summary>
        /// The kit of that name, or null. Case-insensitive, because these are typed into chat.
        /// </summary>
        public BanditKit FindKit(string name)
        {
            if (Kits == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            foreach (BanditKit kit in Kits)
            {
                if (kit != null && string.Equals(kit.Name, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    return kit;
                }
            }

            return null;
        }

        /// <summary>Every kit name, for the usage line and "/bandit kits".</summary>
        public List<string> KitNames()
        {
            List<string> names = new List<string>();
            if (Kits == null)
            {
                return names;
            }

            foreach (BanditKit kit in Kits)
            {
                if (kit != null && !string.IsNullOrEmpty(kit.Name))
                {
                    names.Add(kit.Name);
                }
            }

            return names;
        }

        public void LoadDefaults()
        {
            ApplyLoadout = true;
            Kits = BanditKit.BuildDefaults();
            DefaultKit = "rifleman";
            Loadout = new BanditLoadout();
            SecondaryWeaponRange = 0f;
            InfiniteAmmo = true;
            HoldFireByDefault = true;
            TurnSpeedDegreesPerSecond = 180f;
            ScanIntervalSeconds = 0.5f;
            FireIntervalSeconds = 0.6f;
            AimToleranceDegrees = 10f;
            FireRange = 50f;
            TargetAcquireRange = 140f;
            BurstFire = false;
            BurstMinRounds = 3;
            BurstMaxRounds = 4;
            BurstIntervalSeconds = 1.1f;
            BurstErrorRampPerRound = 0.35f;
            AimHitChance = 0.3f;
            AimTargetRadius = 0.35f;
            AimTargetHalfHeight = 0.8f;
            AimMaxErrorDegrees = 8f;
            CrouchedAimErrorMultiplier = 0.8f;
            ProneAimErrorMultiplier = 0.65f;
            AimWobbleIntervalSeconds = 0.35f;
            AimWobbleSmoothingSeconds = 0.15f;
            RequireLineOfSight = true;

            MovementEnabled = true;
            ArriveRadius = 2f;
            RepathIntervalSeconds = 2.5f;
            NavmeshSnapDistance = 3f;
            AllowSprint = true;
            AllowJumping = true;

            AdvanceOnTarget = false;
            InvestigateEnabled = false;
            DespawnSecondsAfterDeath = 5f;
            PreferredEngagementRange = 25f;

            CoverByDefault = false;
            PeekByDefault = false;
            CoverSearchRadius = 32f;
            CoverSearchIntervalSeconds = 3f;
            CoverRingSamples = 20;
            CoverDebugMaxMarkers = 48;
            CoverDebugSeconds = 20f;
            CoverMinimumThreatDistance = 3f;
            SprintToCoverMinPathDistance = 5f;
            CoverHideSeconds = 2.5f;
            CoverPeekSeconds = 2f;

            SquadComposition = DefaultSquadComposition();
            SquadSpacing = 5f;
            SquadSpawnDistance = 200f;
            SquadWeaponsFree = true;
            SquadContactMemorySeconds = 12f;
            SquadCoverSeparation = 4f;
            RepositionAfterNoShotSeconds = 5f;
            SuppressionSeconds = 6f;
            FriendlyFireClearanceRadius = 0.9f;

            PatrolByDefault = false;
            PatrolWaypointDwellSeconds = 3f;
            PatrolLoop = true;
            PatrolUseLocationNodesWhenNoWaypoints = true;
        }
    }
}
