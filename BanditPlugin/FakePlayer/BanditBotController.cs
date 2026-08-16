using System.Collections.Generic;
using System.Reflection;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Drives the bot by feeding it fabricated input packets, exactly like a real client would.
    ///
    /// Rather than calling look/movement/equipment simulate by hand and hand-rolling the simulation
    /// and clock counters, we enqueue one WalkingPlayerInputPacket per tick onto the player's own
    /// PlayerInput.serversidePackets queue. Vanilla PlayerInput then does everything for us, in the
    /// right order and at the right cadence (verified by decompiling the real Assembly-CSharp.dll):
    ///     life.simulate(...)
    ///     look.simulate(packet.yaw, packet.pitch, RATE)          &lt;- rotation, replicated
    ///     stance.simulate(...)
    ///     movement.simulate(...)                                  &lt;- position, replicated
    ///     equipment.simulate(sim, packet.primaryAttack, ...)      &lt;- pulls the trigger
    ///     animator.simulate(...)
    /// and afterwards advances _simulation and calls equipment.tock(clock) SAMPLES times, which is
    /// what actually paces firing (UseableGun.tockShoot gates on "clock - lastFire"). Driving tock
    /// ourselves would mean reproducing that clock cadence exactly or getting the firerate wrong.
    ///
    /// This is the same approach the MIT-licensed EvolutionPlugins/Dummy project uses. We skip the
    /// hardest part of their implementation - client-side movement prediction - because this bot
    /// never moves, so clientPosition is simply wherever it already is.
    /// </summary>
    public class BanditBotController : MonoBehaviour
    {
        public Player Self { get; set; }
        public SteamPlayer SteamPlayerToKeepAlive { get; set; }

        /// <summary>
        /// This bandit's resolved class settings. Set by the spawner before Start() runs, so the
        /// brain can read it as it is constructed. Never null on a spawned bandit.
        /// </summary>
        public BanditProfile Profile { get; set; }

        /// <summary>
        /// The squad this bandit belongs to, or null for one spawned on its own. Set by
        /// BanditSquad.Add. A lone bandit behaves exactly as it did before squads existed - every
        /// squad-aware branch in here and in the brain is written to fall through when this is null.
        /// </summary>
        public BanditSquad Squad { get; set; }

        /// <summary>This bandit's class, for the squad's contact reports and /banditstatus.</summary>
        public string KitName => Profile != null ? Profile.KitName : "default";

        /// <summary>
        /// Every live bandit, so one about to fire can check whether another is in the way without
        /// walking Provider.clients or allocating a list on the way to every trigger pull.
        /// </summary>
        private static readonly List<BanditBotController> Live = new List<BanditBotController>();

        /// <summary>
        /// Every live bandit, for the code that has to look at the others - the friendly-fire check
        /// and, from a vehicle, working out which squadmates are about to be run over. Exposed as
        /// the list itself rather than a copy because both callers run per packet.
        /// </summary>
        internal static List<BanditBotController> LiveBandits => Live;

        /// <summary>Decides where the bot wants to walk. Created in Start, once Self is set.</summary>
        public BanditBrain Brain { get; private set; }

        /// <summary>
        /// Getting into and out of vehicles, and holding one still once seated. Created in Start
        /// alongside the brain, and never null afterwards. A bandit that has never been ordered into
        /// a vehicle simply reports IsSeated false and nothing here runs.
        /// </summary>
        public BanditVehicleDriver Driver { get; private set; }

        /// <summary>Whoever the bot is currently shooting at, or null.</summary>
        public Player CurrentTarget => _target;

        /// <summary>
        /// Weapons tight: the bot still acquires and tracks targets, and still takes cover from
        /// them, but never pulls the trigger or shoulders the rifle. Set at spawn from
        /// BanditConfiguration.HoldFireByDefault - on by default, so a fresh bandit is harmless
        /// until told otherwise - and toggled afterwards by /bandit shoot start|stop.
        /// Dropping aim-down-sights matters as well as the trigger - vanilla
        /// PlayerStance refuses to sprint while aiming, so a bot told to hold fire can actually
        /// run somewhere.
        /// </summary>
        public bool HoldFire { get; set; }

        /// <summary>
        /// The brain asking for the rifle to come down this tick, currently only while sprinting to
        /// cover. Separate from HoldFire because that is a standing order you give a bandit, and
        /// this is a momentary consequence of what it is doing - clearing itself the moment it stops
        /// running - so folding the two together would make /bandit shoot start look like it had
        /// been silently undone.
        /// </summary>
        private bool WeaponDown => Brain != null && Brain.WantsWeaponDown;

        /// <summary>
        /// True from the moment a gesture is ordered until the bot has finished playing it, i.e.
        /// while it is putting its weapon away, waving, and before MaintainEquippedWeapon is allowed
        /// to pull the gun back out. Used to hold the bot still and keep it from shooting mid-wave.
        /// </summary>
        public bool IsGesturing => _gesturePhase != GesturePhase.None;

        /// <summary>True while the bot is holding the trigger down through a burst, for
        /// /banditstatus - the only way to see from in-game that burst fire is really engaging.</summary>
        public bool IsBursting => _burstTarget > 0;

        public float TurnSpeedDegreesPerSecond = 180f;
        public float ScanIntervalSeconds = 0.5f;
        public float FireIntervalSeconds = 0.6f;
        public float AimToleranceDegrees = 10f;
        public float FireRange = 50f;
        public float TargetAcquireRange = 140f;
        public bool SuppressiveFire;
        public float SuppressionSeconds = 6f;
        public float FriendlyFireClearanceRadius = 0.9f;
        public bool InfiniteAmmo = true;
        public bool HasPrimaryWeapon = true;
        public bool HasSecondaryWeapon;
        public float SecondaryWeaponRange;
        public float PrimaryAimHitChance = 0.3f;
        public float SecondaryAimHitChance = 0.3f;
        public bool BurstFire;
        public int PrimaryBurstMinRounds = 3;
        public int PrimaryBurstMaxRounds = 4;
        public int SecondaryBurstMinRounds = 3;
        public int SecondaryBurstMaxRounds = 4;
        public float BurstIntervalSeconds = 1.1f;
        public float BurstErrorRampPerRound = 0.35f;
        public float AimTargetRadius = 0.35f;
        public float AimTargetHalfHeight = 0.8f;
        public float AimMaxErrorDegrees = 8f;
        public float CrouchedAimErrorMultiplier = 0.8f;
        public float ProneAimErrorMultiplier = 0.65f;
        public float AimWobbleIntervalSeconds = 0.35f;
        public float AimWobbleSmoothingSeconds = 0.15f;
        public bool RequireLineOfSight = true;

        // input_x/input_y are decoded as ((analog >> 4) & 0xF) - 1 and (analog & 0xF) - 1, so the
        // neutral "no movement" value is 0x11, NOT 0. Sending 0 would make the bot walk backwards.
        private const byte AnalogNeutral = 0x11;

        // PlayerInput reconstructs its key array as (packet.keys & (1 << index)) != 0.
        private const ushort KeyJump = 1 << 0;
        private const ushort KeyCrouch = 1 << 3;
        private const ushort KeyProne = 1 << 4;
        private const ushort KeySprint = 1 << 5;
        private const ushort KeyLeanLeft = 1 << 6;
        private const ushort KeyLeanRight = 1 << 7;

        // sin(22.5 degrees). input_x/input_y are only ever -1, 0 or 1, so a desired direction has
        // to be quantised onto the eight compass points; this is the sector boundary between them.
        private const float OctantThreshold = 0.3827f;

        private const byte AmmoStateIndex = 10;   // PlayerEquipment.state[10] == rounds in magazine
        private const byte FiremodeStateIndex = 11; // PlayerEquipment.state[11] == EFiremode

        // PlayerInput runs equipment.tock() SAMPLES times per packet at one packet every RATE
        // seconds, so the clock UseableGun paces its firing against advances this many times a
        // second. ItemGunAsset states the same figure the other way round - its rounds per second
        // is 50 / (Firerate + 1) - which is what makes this a constant rather than a derivation.
        private const float TocksPerSecond = 50f;

        // Pull the line-of-sight rays up slightly short of the endpoints, so a ray that starts or
        // ends flush against a surface doesn't report that surface as cover. Same 0.025 vanilla
        // InteractableSentry.ScanForTargets uses.
        private const float LineOfSightSkinWidth = 0.025f;

        // What counts as blocking the shot. Vanilla's sentry masks are the starting point, plus the
        // SMALL layer on both rays.
        //
        // That addition is not cosmetic. A bullet is raycast against DAMAGE_CLIENT, which includes
        // SMALL - bushes, low fences, debris, clutter - while BLOCK_SENTRY and DAMAGE_SERVER both
        // leave it out. Testing visibility without it meant the bot looked down a line the ray
        // treated as clear, fired, and had the round stop dead on a bush: the hit report came back
        // against a non-player and nothing took damage. It fires, and nothing happens.
        //
        // It shows up mostly when the gun is low. A standing bandit's eye at 1.75m is above most
        // clutter; a prone one's at 0.35m is inside it, which is why lying down looked like it
        // broke shooting outright. The two masks now agree, so "I can see you" means "my round can
        // reach you", and where it cannot the bandit holds fire or moves instead of feeding rounds
        // into a shrub.
        //
        // ENEMY and ENTITY are still in the bullet's mask and not in these: those are zombies and
        // world entities, which do eat a round, but they move off on their own and are not worth
        // making a bandit stand down for.
        private static readonly int LineOfSightForwardMask = RayMasks.BLOCK_SENTRY | RayMasks.SMALL;
        private static readonly int LineOfSightReturnMask = RayMasks.DAMAGE_SERVER | RayMasks.SMALL;

        // The bot swaps to its sidearm at SecondaryWeaponRange and back to the rifle this much
        // further out, so a target loitering on the boundary doesn't make it swap every time it
        // takes a step. Any hysteresis band wider than a stride does the job; four metres is one.
        private const float WeaponSwitchHysteresisMetres = 4f;

        // How far from the feet toward the eyes a player's chest sits. Applied to that player's own
        // aim height, so it tracks their stance rather than assuming they are standing.
        private const float ChestHeightFraction = 0.7f;

        // ServerEquip is a request, not a command - it no-ops while the player is busy, dead or
        // mid-equip-animation - so equipping is retried on this interval rather than assumed.
        private const float EquipRetryIntervalSeconds = 0.5f;

        // ServerEquip(255, ...) is vanilla's "put whatever is in hand away" request - the same one
        // pressing the dequip key sends - and it goes through the same isBusy/canEquip gate as any
        // other equip, so it is retried like one.
        private const byte DequipPage = byte.MaxValue;

        // Give up trying to holster after this long and play the gesture anyway. Something has to
        // bound it: a bot that is permanently isBusy would otherwise sit in the holstering phase
        // forever, with its weapon suppressed and its feet nailed down, and never wave.
        private const float GestureHolsterTimeoutSeconds = 2f;

        // How long the bot stands empty-handed before re-arming. The clip length only exists inside
        // the client's animation bundle - the server has no CharacterAnimator to measure
        // Gesture_Wave with - so this is a fixed span comfortably longer than the animation.
        private const float GestureHoldSeconds = 2.5f;

        private static readonly FieldInfo ServersidePacketsField =
            typeof(PlayerInput).GetField("serversidePackets", BindingFlags.NonPublic | BindingFlags.Instance);

        // UseableGun caches the magazine count in a private field when it equips, and writes it back
        // into equipment.state[10] as it fires - so topping the bot up means setting both.
        private static readonly FieldInfo UseableGunAmmoField =
            typeof(UseableGun).GetField("ammo", BindingFlags.NonPublic | BindingFlags.Instance);

        // The server does NOT raycast bullets itself. UseableGun.ballistics() applies damage from
        // hit reports the owning client sends (PlayerInput.sendRaycast(info, ERaycastInfoUsage.Gun));
        // with no client to send them, "if (!player.input.hasInputs()) break" discards every bullet,
        // which is why an un-reported shot damages nothing at all - not players, not even trees.
        // We therefore raycast ourselves and inject the result into the packet's own input queue.
        private static readonly FieldInfo PlayerInputInputsField =
            typeof(PlayerInput).GetField("inputs", BindingFlags.NonPublic | BindingFlags.Instance);

        private Queue<PlayerInputPacket> _serversidePackets;
        private Player _target;
        private float _nextScanTime;
        private float _packetAccumulator;
        private uint _clientSimulationFrameNumber;
        private float _currentYaw;
        private float _currentPitch = 90f; // 0..180, 90 == level
        private float _nextFireTime;
        private bool _triggerHeld;
        private bool _aimingActive;
        private float _nextEquipAttemptTime;
        private float _diedAtTime;

        // Rounds wanted from the burst in progress, 0 when none is - so this doubles as "the
        // trigger is currently latched down". _burstRoundsFired counts what has actually left the
        // barrel, not what we asked for, and _burstDeadline bounds a burst whose rounds never come.
        private int _burstTarget;
        private int _burstRoundsFired;
        private float _burstDeadline;

        // equipment.state[10] as of the previous packet, which is how rounds fired are counted.
        // -1 means there is no baseline yet, so the next reading must not be treated as a delta.
        private int _lastObservedAmmo = -1;

        // Hit reports the packet being built needs. Decided by the trigger logic, consumed by
        // EnqueueInputPacket; see AttachHitReports() for why the count is not always one.
        private int _hitReportsForThisPacket;

        // Whether the bandit was in a seat as of the last packet, so getting in and getting out are
        // each noticed once rather than tested for everywhere. See TickSeated().
        private bool _wasSeated;

        /// <summary>Where a one-off gesture has got to. See TickGesture().</summary>
        private enum GesturePhase
        {
            None,
            Holstering,
            Playing
        }

        // Where this bandit keeps firing once it can no longer see anyone, and when that place was
        // last actually seen by somebody. See UpdateSuppression().
        private Vector3 _suppressionPoint;
        private float _lastSightingTime = float.MinValue;
        private bool _hasSuppressionPoint;

        // Measured from the last live sighting rather than from a deadline set when contact broke,
        // so the window cannot be restarted by a stale memory - it runs down once, and only a
        // fresh pair of eyes on the enemy resets it.
        private bool IsSuppressing =>
            SuppressiveFire && _hasSuppressionPoint && Time.time - _lastSightingTime < SuppressionSeconds;

        /// <summary>True while firing at a place rather than a person, for /banditstatus.</summary>
        public bool IsSuppressingFire => IsSuppressing && _aimIntent == AimIntent.Suppression;

        private GesturePhase _gesturePhase;
        private EPlayerGesture _pendingGesture;
        private Player _gestureLookAt;
        private float _gesturePhaseDeadline;

        // Aim error, in degrees, added on top of the tracked aim when the packet is built. Kept
        // separate from _currentYaw/_currentPitch on purpose: those keep tracking the target dead
        // on, so IsAimedAtTarget() still answers "have I finished turning onto him" rather than
        // "did this particular shot happen to be wobbled back on target".
        private float _aimErrorYaw;
        private float _aimErrorPitch;
        private float _aimErrorYawTarget;
        private float _aimErrorPitchTarget;
        private float _nextWobbleSampleTime;

        private void Start()
        {
            _currentYaw = transform.eulerAngles.y;

            if (ServersidePacketsField == null)
            {
                Logger.LogError("[Bandit] Could not reflect PlayerInput.serversidePackets - the bot cannot be driven. Game version may have changed.");
                enabled = false;
                return;
            }

            _serversidePackets = ServersidePacketsField.GetValue(Self.input) as Queue<PlayerInputPacket>;
            if (_serversidePackets == null)
            {
                Logger.LogError("[Bandit] PlayerInput.serversidePackets was not a Queue<PlayerInputPacket>; the bot cannot be driven.");
                enabled = false;
                return;
            }

            Brain = new BanditBrain(this, Self);
            Driver = new BanditVehicleDriver(this, Self);
            Live.Add(this);
        }

        private void OnDestroy()
        {
            Live.Remove(this);
            Squad?.ReleaseCover(this);
        }

        /// <summary>
        /// Routed here from the DamageTool hook in BanditPlugin, so a bot that gets shot by
        /// someone it never saw still knows roughly where the shot came from.
        /// </summary>
        public void NotifyDamaged(Vector3 shotDirection)
        {
            Brain?.NotifyDamaged(shotDirection);
        }

        private void Update()
        {
            if (Self == null || _serversidePackets == null)
            {
                return;
            }

            // Provider.KickClientsWithBadConnection() drops any client whose last packet is older
            // than Timeout_Game_Seconds. A bot never actually receives packets, so refresh this or
            // it gets kicked after ~30s.
            if (SteamPlayerToKeepAlive != null)
            {
                SteamPlayerToKeepAlive.timeLastPacketWasReceivedFromClient = Time.realtimeSinceStartup;
            }

            if (HandleDeath())
            {
                return;
            }

            if (Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanIntervalSeconds;
                _target = FindNearestRealPlayer();

                // Everything this bandit can see goes to the squad, every scan. This is the only
                // place contact enters the shared picture, and it is why a bandit behind a wall
                // reacts at all.
                if (_target != null)
                {
                    // The target's real eye, not the chest this bandit aims at. The squad uses
                    // this as the threat's viewpoint for cover searches, and a viewpoint reported
                    // half a metre too low sees less over a wall than the shooter really does -
                    // which would hand out cover that is not cover.
                    Squad?.ReportContact(this, _target, EyeOf(_target));
                }
            }

            UpdateSuppression();

            // Feed packets at the same rate a real client sends them. PlayerInput self-regulates if
            // we run slightly fast, but there's no reason to let the queue grow.
            _packetAccumulator += Time.deltaTime;
            if (_packetAccumulator < PlayerInput.RATE)
            {
                return;
            }
            float elapsed = _packetAccumulator;
            _packetAccumulator = 0f;

            // A seated bandit is driven by a different packet entirely, and none of the on-foot work
            // below applies to it: the brain's steering has no feet to steer, vanilla will not let a
            // driver equip anything, and a gesture cannot play from a seat.
            if (TickSeated(elapsed))
            {
                return;
            }

            // Before MaintainEquippedWeapon, which is the thing being suppressed while a gesture
            // runs - so the tick that ends the gesture is also the tick the rifle comes back out.
            TickGesture();
            MaintainEquippedWeapon();

            // Before aiming, because with no target to lock onto the bot faces wherever the brain
            // is walking - and because the analog byte built below is relative to that facing.
            Brain?.Tick(elapsed, _target);

            ResolveAimPoint();
            RecordShotOpportunity();
            AimAtTarget(elapsed);
            UpdateAimWobble(elapsed);
            // Order matters: DecideAttackInput() consumes the trigger state for this packet.
            EAttackInputFlags secondary = DecideAimInput();
            EAttackInputFlags primary = DecideAttackInput();
            EnqueueInputPacket(primary, secondary);
        }

        /// <summary>
        /// Sends one "hold this vehicle where it is" packet if the bandit is in a seat, and returns
        /// whether it did - i.e. whether the rest of the on-foot tick should be skipped.
        ///
        /// Also the one place the two modes hand over to each other. Climbing into a seat takes the
        /// gun out of the bandit's hands, so a latched trigger, a shouldered rifle or a half-played
        /// gesture all belong to a weapon it is no longer holding. Left set, the burst that was
        /// running when it got in would pick up mid-magazine the moment it climbed back out.
        /// </summary>
        private bool TickSeated(float elapsed)
        {
            bool seated = Driver.IsSeated;

            if (seated != _wasSeated)
            {
                _wasSeated = seated;
                _aimingActive = false;
                _triggerHeld = false;
                CancelBurst();
                CancelGesture();

                // So the bandit re-arms on the first tick back on its feet rather than waiting out
                // a retry interval that started before it ever got in.
                _nextEquipAttemptTime = 0f;

                if (!seated)
                {
                    // Thrown out by a death, a wreck or someone else's /banditv exit, rather than by
                    // the order that put it there. Standing orders that only mean anything from a
                    // seat go with the seat.
                    Driver.TrackNearestPlayer = false;
                    Driver.StopDriving();
                }
            }

            if (!seated)
            {
                return false;
            }

            _serversidePackets.Enqueue(Driver.BuildPacket(_clientSimulationFrameNumber++, elapsed));
            return true;
        }

        /// <summary>
        /// When this bandit last had somewhere to put a round: something to aim at, in range, with
        /// a clear line to it.
        ///
        /// Deliberately not "when it last fired" - a bandit between bursts, or one that has been
        /// told to hold fire, has a shot and simply is not taking it, and treating that as a dry
        /// spell would send it wandering off looking for an angle it already has. This measures
        /// opportunity, which is what the brain wants to know before deciding its position is
        /// useless.
        /// </summary>
        public float LastShotOpportunityTime { get; private set; } = float.MinValue;

        private void RecordShotOpportunity()
        {
            if (_aimIntent != AimIntent.Target && _aimIntent != AimIntent.Suppression)
            {
                return;
            }

            if (!IsTargetInRange())
            {
                return;
            }

            // A squadmate in the way counts as having no shot, and has to. This is a standing
            // condition, not a passing one: a rifleman that takes cover directly in front of a
            // prone machinegunner sits there for the rest of the fight, and treating that as "the
            // gunner has a shot, it is simply choosing not to take it" would leave it lying behind
            // its own squad in silence forever. Counted as a dry spell instead, so the brain moves
            // it somewhere it can shoot from.
            if (IsFriendlyInLineOfFire())
            {
                return;
            }

            bool visible = HasLineOfSightToPoint(_aimPoint);

            if (visible)
            {
                LastShotOpportunityTime = Time.time;
            }
        }

        /// <summary>
        /// Keeps a machinegunner firing at a place after it has stopped being able to see a person.
        ///
        /// Two windows feed this, and the difference between them is the whole behaviour. While
        /// anyone in the squad still has eyes on the contact, the point is refreshed every tick and
        /// the deadline keeps being pushed out - so the gunner hoses a position a rifleman fifteen
        /// metres away is looking at, from behind something it cannot see past itself. Once nobody
        /// can see them any more, the last reported position is fired at for SuppressionSeconds and
        /// then dropped.
        ///
        /// A visible target always wins: there is no point suppressing a place when there is a
        /// person to shoot at, and reacquiring cancels the suppression outright rather than letting
        /// it run down.
        /// </summary>
        private void UpdateSuppression()
        {
            if (!SuppressiveFire)
            {
                return;
            }

            // Anything this bandit can see itself is the best possible report, and recording it
            // here is what starts the clock the moment it loses sight of them.
            if (_target != null && _target.life != null && !_target.life.isDead)
            {
                _suppressionPoint = AimPointOf(_target);
                _lastSightingTime = Time.time;
                _hasSuppressionPoint = true;
                return;
            }

            // Otherwise take a squadmate's word for it, but only while one of them can genuinely
            // see the contact rather than merely remember it. That distinction is the behaviour:
            // refreshed on a live sighting the gunner keeps firing indefinitely at a position
            // somebody else is watching, and the moment the last pair of eyes loses them the
            // window below starts running down.
            BanditSquad squad = Squad;
            if (squad != null && squad.AnyoneSeesContact)
            {
                _suppressionPoint = squad.ContactAimPoint;
                _lastSightingTime = Time.time;
                _hasSuppressionPoint = true;
            }
        }

        /// <summary>
        /// Removes the bot a few seconds after it is killed, and stops driving it in the meantime.
        ///
        /// A real player's corpse goes away when they press respawn. A bot has no client to press
        /// anything, so a killed bandit lies there indefinitely, still holding a player slot and
        /// still counted by /banditclear - which is why killing one never cleared it. Returns true
        /// while dead, so the caller stops building input packets for a corpse.
        /// </summary>
        private bool HandleDeath()
        {
            if (Self.life == null || !Self.life.isDead)
            {
                _diedAtTime = 0f;
                return false;
            }

            // A corpse is done gesturing. Left set, this would suppress the weapon and the feet of
            // whatever the bot does next, since TickGesture never runs while dead.
            CancelGesture();

            // And done shooting. PlayerInput stops simulating a dead player, so the trigger is
            // already released as far as vanilla is concerned.
            CancelBurst();

            float despawnDelay = BanditPlugin.Instance.Configuration.Instance.DespawnSecondsAfterDeath;
            if (despawnDelay < 0f)
            {
                return true; // configured to leave the body for /banditclear
            }

            if (_diedAtTime <= 0f)
            {
                _diedAtTime = Time.time;
                return true;
            }

            if (Time.time - _diedAtTime >= despawnDelay)
            {
                // Disable first: Provider.kick tears the player down, and this component must not
                // run another Update against a half-removed player.
                enabled = false;
                FakePlayerSpawner.DespawnBot(SteamPlayerToKeepAlive);
            }

            return true;
        }

        /// <summary>
        /// Orders a one-off gesture: the bot puts its weapon away, plays the animation, then arms
        /// itself again. Returns false if it is dead or already mid-gesture.
        ///
        /// Weapon first because vanilla will not let a player gesture with something in their hands
        /// (PlayerAnimator.ReceiveGestureRequest drops any request while HasValidUseable), so a bot
        /// that waved with its rifle out would be doing something no player can do. It is also the
        /// only way the wave reads as friendly rather than as a bandit pointing a gun at you.
        /// </summary>
        /// <param name="lookAt">Who to turn and face while gesturing, or null to keep the current facing.</param>
        public bool TryPlayGesture(EPlayerGesture gesture, Player lookAt)
        {
            if (Self == null || Self.life == null || Self.life.isDead || IsGesturing)
            {
                return false;
            }

            _pendingGesture = gesture;
            _gestureLookAt = lookAt;
            _gesturePhase = GesturePhase.Holstering;
            _gesturePhaseDeadline = Time.time + GestureHolsterTimeoutSeconds;

            // The weapon is about to leave the bot's hands, taking aim-down-sights and any
            // half-pulled trigger with it - same reset the weapon swap in MaintainEquippedWeapon
            // does, and for the same reason.
            _aimingActive = false;
            _triggerHeld = false;
            return true;
        }

        private void CancelGesture()
        {
            _gesturePhase = GesturePhase.None;
            _gestureLookAt = null;
        }

        /// <summary>
        /// Walks an ordered gesture through its two phases: get the weapon put away, then play the
        /// animation and stand empty-handed long enough for it to finish.
        ///
        /// Re-arming is not a phase of its own - clearing IsGesturing is enough, because
        /// MaintainEquippedWeapon runs immediately afterwards and its whole job is to put the
        /// wanted weapon back in the bot's hands, retries included.
        /// </summary>
        private void TickGesture()
        {
            switch (_gesturePhase)
            {
                case GesturePhase.None:
                    return;

                case GesturePhase.Holstering:
                    if (Self.equipment != null && Self.equipment.HasValidUseable
                        && Time.time < _gesturePhaseDeadline)
                    {
                        RequestHolster();
                        return;
                    }

                    // sendGesture rather than the RPC a client would send: the server-side branch
                    // broadcasts the animation to everyone (loopback included) without re-checking
                    // the equipment and stance conditions a real client is held to, which is what
                    // lets the gesture still play in the timeout case above.
                    Self.animator?.sendGesture(_pendingGesture, true);
                    _gesturePhase = GesturePhase.Playing;
                    _gesturePhaseDeadline = Time.time + GestureHoldSeconds;
                    return;

                case GesturePhase.Playing:
                    if (Time.time >= _gesturePhaseDeadline)
                    {
                        CancelGesture();
                    }
                    return;
            }
        }

        private void RequestHolster()
        {
            if (Time.time < _nextEquipAttemptTime)
            {
                return;
            }

            if (Self.equipment.isBusy || !Self.equipment.canEquip || !Self.equipment.IsEquipAnimationFinished)
            {
                return; // vanilla would drop the request; don't burn a retry interval on it
            }

            _nextEquipAttemptTime = Time.time + EquipRetryIntervalSeconds;
            Self.equipment.ServerEquip(DequipPage, 0, 0);
        }

        /// <summary>What the bot is currently pointing its gun at, and why.</summary>
        private enum AimIntent
        {
            /// <summary>Nothing to aim at; the body turns to follow the feet.</summary>
            None,

            /// <summary>Turned to face whoever is being waved at.</summary>
            Gesture,

            /// <summary>A player this bandit can see, and may shoot at.</summary>
            Target,

            /// <summary>A position, not a person - see <see cref="UpdateSuppression"/>.</summary>
            Suppression
        }

        private AimIntent _aimIntent;
        private Vector3 _aimPoint;

        /// <summary>
        /// Works out what to point at this tick, in priority order, and leaves it in
        /// <see cref="_aimPoint"/> for the firing decisions further down to share.
        ///
        /// One point for everything is what keeps the aim, the "am I on target" test, the
        /// line-of-sight check and the hit raycast all talking about the same place. Suppression
        /// only exists because that point does not have to be a player.
        /// </summary>
        private void ResolveAimPoint()
        {
            // A gesture aimed at somebody outranks the combat target: the whole point of /banditwave
            // is that the bandit turns and waves at *you*, even if someone else is closer.
            if (IsGesturing && _gestureLookAt != null && _gestureLookAt.life != null && !_gestureLookAt.life.isDead)
            {
                _aimIntent = AimIntent.Gesture;
                _aimPoint = AimPointOf(_gestureLookAt);
                return;
            }

            if (_target != null && _target.life != null && !_target.life.isDead)
            {
                _aimIntent = AimIntent.Target;
                _aimPoint = AimPointOf(_target);
                return;
            }

            if (IsSuppressing)
            {
                _aimIntent = AimIntent.Suppression;
                _aimPoint = _suppressionPoint;
                return;
            }

            _aimIntent = AimIntent.None;
        }

        private void AimAtTarget(float elapsed)
        {
            if (_aimIntent == AimIntent.None)
            {
                TurnTowardsTravelDirection(elapsed);
                return;
            }

            // Both ends have to be the real, stance-dependent points rather than a nominal eye
            // height, or the bot aims at where a standing body would be from where a standing eye
            // would be. Prone is where that stops being a rounding error: PlayerLook puts the aim
            // transform at HEIGHT_LOOK_PRONE (0.35m) against 1.75m standing, and the bullet leaves
            // from that transform - so a bot solving its pitch as if its eye were 1.15m higher than
            // it is fires into the ground in front of itself.
            Vector3 eye = EyePosition;
            Vector3 toTarget = _aimPoint - eye;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector3 direction = toTarget.normalized;
            float desiredYaw = Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
            // Unturned pitch runs 0..180 with 90 level, decreasing as you look up
            // (PlayerLook.look() does "_pitch -= y"), hence 90 - elevation.
            float desiredPitch = Mathf.Clamp(90f - (Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg), 0f, 180f);

            _currentYaw = Mathf.MoveTowardsAngle(_currentYaw, desiredYaw, TurnSpeedDegreesPerSecond * elapsed);
            _currentPitch = Mathf.MoveTowards(_currentPitch, ClampPitchToStance(desiredPitch), TurnSpeedDegreesPerSecond * elapsed);
        }

        /// <summary>
        /// Holds a wanted pitch to what the bot's current stance actually allows.
        ///
        /// PlayerLook.clampPitch does this to every player and the limits are tight lying down -
        /// prone is held to 60-120, a mere 30 degrees either side of level, against a standing
        /// player's full 0-180. Without matching it here the bot would believe it had aimed
        /// somewhere vanilla will not let it point: IsAimedAtTarget would pass, and the hit raycast
        /// - which is the thing that actually does the damage - would be fired down a line the gun
        /// is not on, landing hits the replicated aim visibly contradicts.
        ///
        /// Matching it instead means a prone bandit simply cannot engage something steeply above or
        /// below it, and holds fire rather than cheating, which is what a player in that stance
        /// would have to do too.
        /// </summary>
        private float ClampPitchToStance(float pitch)
        {
            if (Self == null || Self.stance == null)
            {
                return pitch;
            }

            switch (Self.stance.stance)
            {
                case EPlayerStance.PRONE:
                    return Mathf.Clamp(pitch, 60f, 120f);
                case EPlayerStance.CROUCH:
                    return Mathf.Clamp(pitch, 20f, 160f);
                default:
                    return pitch;
            }
        }

        /// <summary>
        /// With nobody to shoot at, the body turns to face wherever the brain is heading.
        ///
        /// This is not just cosmetic. Movement is body-relative - PlayerMovement does
        /// "transform.rotation * move.normalized * speed", and the body yaw is the packet's yaw -
        /// so a bot that never turns can only ever strafe along the eight compass points around
        /// its spawn facing. Turning onto the travel direction is what lets it hold a line down a
        /// road, and the pitch is levelled off so it isn't walking around staring at the sky.
        /// </summary>
        private void TurnTowardsTravelDirection(float elapsed)
        {
            if (Brain == null || !Brain.DesiredFacing.HasValue)
            {
                return;
            }

            _currentYaw = Mathf.MoveTowardsAngle(_currentYaw, Brain.DesiredFacing.Value, TurnSpeedDegreesPerSecond * elapsed);
            _currentPitch = Mathf.MoveTowards(_currentPitch, 90f, TurnSpeedDegreesPerSecond * elapsed);
        }

        /// <summary>
        /// Turns the brain's desired world direction into the packet's analog byte.
        ///
        /// The direction has to be expressed in the *body's* frame, and the body yaw is whatever
        /// this packet says it is - PlayerInput calls look.simulate (which assigns
        /// transform.localRotation from the yaw) immediately before movement.simulate reads it.
        /// So the same yaw that is about to be written into the packet is the one to un-rotate by,
        /// aim error included: that error is small next to a 45-degree movement sector, but using
        /// a different angle here than the packet carries would make the bot drift off its line.
        ///
        /// The result is that an engaged bandit strafes and backpedals with its gun still on the
        /// target, and only turns its body when it has nobody to shoot at.
        /// </summary>
        private byte BuildAnalog(float packetYaw)
        {
            // Stand still to gesture. The brain keeps its destination, so a bandit waved at
            // mid-patrol carries on walking the route the moment the wave is over.
            if (Brain == null || IsGesturing)
            {
                return AnalogNeutral;
            }

            Vector3 direction = Brain.MoveDirection;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return AnalogNeutral;
            }

            Vector3 local = Quaternion.Euler(0f, -packetYaw, 0f) * direction.normalized;
            int inputX = Mathf.Abs(local.x) > OctantThreshold ? (local.x > 0f ? 1 : -1) : 0;
            int inputY = Mathf.Abs(local.z) > OctantThreshold ? (local.z > 0f ? 1 : -1) : 0;

            if (inputX == 0 && inputY == 0)
            {
                return AnalogNeutral;
            }

            return (byte)(((inputX + 1) << 4) | (inputY + 1));
        }

        private ushort BuildKeys()
        {
            // No sprinting, ducking or leaning through a gesture - and standing upright matters,
            // because vanilla refuses the request outright from a prone player.
            if (Brain == null || IsGesturing)
            {
                return 0;
            }

            ushort keys = 0;
            if (Brain.WantsJump)
            {
                keys |= KeyJump;
            }
            // Mutually exclusive, and in this order: PlayerStance.simulate reads its crouch input
            // first and only considers prone when that one is clear, so a packet carrying both is
            // a crouch. Sending exactly one key is what makes the stance the brain asked for the
            // stance vanilla actually applies. The brain already keeps these two apart; the else
            // is here so that stays true however the flags are set in future.
            if (Brain.WantsCrouch)
            {
                keys |= KeyCrouch;
            }
            else if (Brain.WantsProne)
            {
                keys |= KeyProne;
            }
            // Vanilla PlayerStance refuses to sprint while aiming down sights, out of stamina or
            // standing still, so this is a request rather than a command - which is what we want.
            if (Brain.WantsSprint)
            {
                keys |= KeySprint;
            }
            // PlayerAnimator treats both-at-once as neutral, so these must stay mutually exclusive.
            if (Brain.WantsLeanLeft && !Brain.WantsLeanRight)
            {
                keys |= KeyLeanLeft;
            }
            else if (Brain.WantsLeanRight && !Brain.WantsLeanLeft)
            {
                keys |= KeyLeanRight;
            }
            return keys;
        }

        /// <summary>
        /// Keeps the aim gently sliding around the target between shots instead of sitting welded
        /// to its centre. This is cosmetic - the sample that decides whether a shot lands is drawn
        /// fresh in DecideAttackInput() at the moment the trigger is pulled - but without it the
        /// bot would only ever twitch once per shot.
        /// </summary>
        private void UpdateAimWobble(float elapsed)
        {
            // Nothing to wobble around while gesturing: the aim is pointed at whoever is being
            // waved at, not at a target being shot, and shooting error has no business moving it.
            if (_aimIntent != AimIntent.Target && _aimIntent != AimIntent.Suppression)
            {
                _aimErrorYaw = _aimErrorYawTarget = 0f;
                _aimErrorPitch = _aimErrorPitchTarget = 0f;
                return;
            }

            if (Time.time >= _nextWobbleSampleTime)
            {
                _nextWobbleSampleTime = Time.time + Mathf.Max(AimWobbleIntervalSeconds, 0.01f);
                SampleAimError(out _aimErrorYawTarget, out _aimErrorPitchTarget);
            }

            // Exponential approach, so the sway is the same shape whatever the packet rate is.
            float t = AimWobbleSmoothingSeconds > 0.0001f
                ? 1f - Mathf.Exp(-elapsed / AimWobbleSmoothingSeconds)
                : 1f;
            _aimErrorYaw = Mathf.Lerp(_aimErrorYaw, _aimErrorYawTarget, t);
            _aimErrorPitch = Mathf.Lerp(_aimErrorPitch, _aimErrorPitchTarget, t);
        }

        /// <summary>
        /// Draws how far off the target's centre this shot should go.
        ///
        /// The miss is picked in metres at the target's range - a lateral and a vertical offset,
        /// each normally distributed - and only then converted to an angle. That way the hit rate
        /// doesn't change with distance: a 0.5m miss is a miss whether it happens at 5m or at 50m,
        /// whereas a fixed angular spread would make the bot lethal up close and useless far away.
        ///
        /// Scaling each axis' standard deviation by that axis' half-extent puts the miss, measured
        /// in target-widths, on a circular unit Gaussian - so P(inside the target ellipse) is
        /// 1 - exp(-1 / 2s^2), and the configured hit chance p is hit at s = 1 / sqrt(-2 ln(1-p)).
        /// </summary>
        private void SampleAimError(out float yawError, out float pitchError)
        {
            SampleAimError(out yawError, out pitchError, 1f);
        }

        /// <param name="errorScale">
        /// Multiplier on the drawn miss distance, used to walk a burst's accuracy off as it climbs.
        /// 1 is the configured hit chance.
        /// </param>
        private void SampleAimError(out float yawError, out float pitchError, float errorScale)
        {
            yawError = 0f;
            pitchError = 0f;

            float hitChance = Mathf.Clamp(ActiveAimHitChance, 0f, 0.999f);
            if (hitChance >= 0.999f || AimTargetRadius <= 0f || _aimIntent == AimIntent.None)
            {
                return; // configured back into a perfect aimbot
            }

            float scale = StanceAimErrorMultiplier / Mathf.Sqrt(-2f * Mathf.Log(1f - hitChance));

            // Clamped because at arm's length the angle subtended by a torso-width miss explodes;
            // AimMaxErrorDegrees catches the same case from the other side.
            float distance = Mathf.Max((_aimPoint - EyePosition).magnitude, 1f);

            yawError = ErrorDegrees(NextGaussian() * AimTargetRadius * scale * errorScale, distance);
            pitchError = ErrorDegrees(NextGaussian() * AimTargetHalfHeight * scale * errorScale, distance);
        }

        /// <summary>
        /// How much of its aim error this bandit keeps in the stance it is actually in.
        ///
        /// Read from the live vanilla stance rather than from what the brain asked for, so a
        /// bandit that has been refused a crouch - no headroom, shallow water - does not get to
        /// shoot as though it were braced.
        /// </summary>
        private float StanceAimErrorMultiplier
        {
            get
            {
                if (Self == null || Self.stance == null)
                {
                    return 1f;
                }

                switch (Self.stance.stance)
                {
                    case EPlayerStance.PRONE:
                        return Mathf.Max(0f, ProneAimErrorMultiplier);
                    case EPlayerStance.CROUCH:
                        return Mathf.Max(0f, CrouchedAimErrorMultiplier);
                    default:
                        return 1f;
                }
            }
        }

        private float ErrorDegrees(float offsetMetres, float distance)
        {
            float degrees = Mathf.Atan2(offsetMetres, distance) * Mathf.Rad2Deg;
            return Mathf.Clamp(degrees, -AimMaxErrorDegrees, AimMaxErrorDegrees);
        }

        /// <summary>Box-Muller: turns two uniforms into a standard normal sample.</summary>
        private static float NextGaussian()
        {
            // Random.value is inclusive of 0 and Log(0) is -Infinity, hence the floor.
            float u1 = Mathf.Max(UnityEngine.Random.value, 1e-6f);
            float u2 = UnityEngine.Random.value;
            return Mathf.Sqrt(-2f * Mathf.Log(u1)) * Mathf.Cos(2f * Mathf.PI * u2);
        }

        /// <summary>
        /// Returns the trigger input for this packet.
        ///
        /// Two cadences live behind this. Without BurstFire we alternate Start/Stop, so each fire
        /// interval is one discrete trigger pull - matching the SEMI firemode the bot is given, and
        /// behaving sanely if it's switched to AUTO. With BurstFire we instead latch the trigger
        /// down and leave it down for as many packets as the burst takes, because holding is the
        /// only way to get rounds out at the gun's own rate: UseableGun sets equipment.isBusy on
        /// every shot and clears it 150ms later, and startPrimary() refuses while it is set, so
        /// re-pulling can never beat about four rounds a second however fast the gun is. A latched
        /// trigger skips startPrimary() entirely from the second round on and goes straight to
        /// tockShoot(), which paces itself against the asset's own Firerate.
        ///
        /// PlayerEquipment ignores a Start while already started and a Stop while already stopped,
        /// so both sequences are safe.
        /// </summary>
        private EAttackInputFlags DecideAttackInput()
        {
            // Before anything reads it, and in both modes, so the baseline never goes stale.
            _burstRoundsFired += ObserveRoundsFired();

            return BurstFire ? DecideBurstAttackInput() : DecideSingleShotAttackInput();
        }

        /// <summary>One trigger pull per FireIntervalSeconds - the original cadence.</summary>
        private EAttackInputFlags DecideSingleShotAttackInput()
        {
            if (_triggerHeld)
            {
                _triggerHeld = false;
                return EAttackInputFlags.Stop;
            }

            // Interval first: CanShootThisPacket() ends in a line-of-sight raycast, and there is no
            // reason to pay for one on the seven packets out of eight that are only waiting.
            if (Time.time < _nextFireTime || !CanShootThisPacket())
            {
                return EAttackInputFlags.None;
            }

            TopUpAmmoIfNeeded();
            SnapAimErrorForRound(0);

            _nextFireTime = Time.time + FireIntervalSeconds;
            _triggerHeld = true;
            _hitReportsForThisPacket = 1;
            return EAttackInputFlags.Start;
        }

        /// <summary>
        /// Holds the trigger down until the configured number of rounds has actually left the
        /// barrel, then releases for BurstIntervalSeconds.
        ///
        /// Rounds are counted rather than predicted, so the burst comes out the right length
        /// whatever the gun's rate turns out to be and whatever the server's frame time is doing.
        /// </summary>
        private EAttackInputFlags DecideBurstAttackInput()
        {
            if (_burstTarget > 0)
            {
                // Finished, cut short by an order or a target ducking away, or wedged. The deadline
                // is the one that matters least often and matters most: a burst whose rounds never
                // arrive - the gun jammed busy, the magazine dry with InfiniteAmmo off - would
                // otherwise leave the trigger latched down for good.
                //
                // CanShootThisPacket() is tested last of the three and only while a burst is
                // actually running, because it ends in a line-of-sight raycast.
                if (_burstRoundsFired >= _burstTarget || Time.time >= _burstDeadline || !CanShootThisPacket())
                {
                    return ReleaseBurst();
                }

                // A fresh draw every packet, so each round of the burst is its own hit-or-miss
                // rather than the whole burst inheriting the first round's luck, and each one is
                // fired with a little more error than the last.
                TopUpAmmoIfNeeded();
                SnapAimErrorForRound(_burstRoundsFired);
                _hitReportsForThisPacket = PlanHitReportCount();

                // No input at all: the Start that opened the burst is still latched, and repeating
                // it would be ignored anyway.
                return EAttackInputFlags.None;
            }

            if (Time.time < _nextFireTime || !CanShootThisPacket())
            {
                return EAttackInputFlags.None;
            }

            _burstTarget = DrawBurstSize();
            _burstRoundsFired = 0;
            _burstDeadline = Time.time + BurstTimeoutSeconds(_burstTarget);

            TopUpAmmoIfNeeded();
            SnapAimErrorForRound(0);
            _hitReportsForThisPacket = PlanHitReportCount();
            return EAttackInputFlags.Start;
        }

        /// <summary>
        /// Lets go of the trigger and starts the pause before the next burst. No hit report is
        /// attached to this packet: stopPrimary() runs before the packet's tocks do, and tock()
        /// skips tockShoot() entirely once isShooting is clear, so nothing fires on the way out.
        /// </summary>
        private EAttackInputFlags ReleaseBurst()
        {
            _burstTarget = 0;
            _burstRoundsFired = 0;
            _nextFireTime = Time.time + BurstIntervalSeconds;
            return EAttackInputFlags.Stop;
        }

        /// <summary>
        /// Forgets a burst in progress without sending a release, for the cases where vanilla has
        /// already dropped the trigger for us: equipping a useable resets
        /// PlayerEquipment.wasUsablePrimaryStarted, and a dead bot is not simulated at all. Sending
        /// a Stop in those cases would be harmless but meaningless.
        /// </summary>
        private void CancelBurst()
        {
            _burstTarget = 0;
            _burstRoundsFired = 0;
            _lastObservedAmmo = -1;
            _hitReportsForThisPacket = 0;
        }

        /// <summary>
        /// Everything that has to hold before the bot may put a round downrange this packet.
        ///
        /// Shared by both cadences, and re-tested every packet of a burst rather than only at the
        /// pull, so an order to hold fire, a dash to cover or a target stepping behind a wall cuts
        /// the burst short instead of being noticed a third of a second later.
        /// </summary>
        private bool CanShootThisPacket()
        {
            if (HoldFire || WeaponDown || IsGesturing)
            {
                return false;
            }

            // Nothing to shoot at, or aimed at somebody being waved at rather than shot at.
            if (_aimIntent != AimIntent.Target && _aimIntent != AimIntent.Suppression)
            {
                return false;
            }

            // Must come before anything else that can pull the trigger. PlayerEquipment.simulate
            // routes primary attacks to simulate_PunchInput whenever there is no valid useable, so
            // firing during the equip animation makes the bot throw punches instead of shooting.
            // simulate_UseableInput also ignores input until IsEquipAnimationFinished.
            if (!IsGunReady())
            {
                return false;
            }

            if (!IsAimedAtTarget())
            {
                return false;
            }

            // A squadmate standing in the line. Bandits never target each other, but bullets are
            // raycast and hit whatever is in the way, so without this a prone machinegunner puts
            // its belt through the backs of the riflemen in front of it. Checked before the
            // line-of-sight rays because it costs no raycasts at all.
            if (IsFriendlyInLineOfFire())
            {
                return false;
            }

            // Re-checked here and not just at scan time: the target can step behind cover in the
            // half second between scans, and this is the last moment before the round goes out.
            // Tested along the line the round will actually travel - to the aim point - and not
            // to the target's eyes.
            //
            // Those are not the same line, and the gap between them scales with how low the
            // shooter is. A standing bandit's eye and the chest it aims at are close enough to
            // parallel that the distinction never showed. Crouched, the ray to a standing target's
            // head climbs over a low wall while the round flies flat into it; prone it climbs
            // steeply over everything while the round eats dirt. The bot saw a clear shot, fired,
            // and hit the obstacle every single time - which is why crouch and prone landed
            // nothing at all while standing hit at its configured rate.
            //
            // It also covers suppression for free, which was already aiming at a place.
            return HasLineOfSightToPoint(_aimPoint);
        }

        /// <summary>
        /// Whether another bandit is close enough to this bandit's line of fire to be hit by it.
        ///
        /// Geometric rather than a raycast, because it runs on every trigger pull: each candidate
        /// is projected onto the firing line and rejected if it sits within
        /// FriendlyFireClearanceRadius of it, in front of the muzzle and nearer than whatever is
        /// being shot at. Squadmates behind the shooter or beyond the target cannot be hit by the
        /// round and are ignored.
        /// </summary>
        private bool IsFriendlyInLineOfFire()
        {
            if (FriendlyFireClearanceRadius <= 0f)
            {
                return false;
            }

            Vector3 origin = EyePosition;
            Vector3 toAim = _aimPoint - origin;
            float aimDistance = toAim.magnitude;
            if (aimDistance < 0.01f)
            {
                return false;
            }

            Vector3 direction = toAim / aimDistance;
            float clearanceSq = FriendlyFireClearanceRadius * FriendlyFireClearanceRadius;

            for (int i = 0; i < Live.Count; i++)
            {
                BanditBotController other = Live[i];
                if (other == null || other == this || other.Self == null
                    || other.Self.life == null || other.Self.life.isDead)
                {
                    continue;
                }

                // Their chest, taken from their own stance rather than assumed to be standing.
                // That distinction is the whole point here: the case this check exists for is a
                // prone machinegunner behind riflemen who are crouched in cover, and treating
                // those riflemen as 1.8m tall would block the gunner from firing all fight.
                Vector3 centre = AimPointOf(other.Self) - origin;

                float along = Vector3.Dot(centre, direction);
                if (along <= 0.5f || along >= aimDistance)
                {
                    continue; // behind the muzzle, or further away than what is being shot at
                }

                if ((centre - direction * along).sqrMagnitude < clearanceSq)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Why this bandit is not shooting, in the same order the trigger logic asks the questions.
        ///
        /// A bandit that holds its fire is completely silent about it - nothing is logged, because
        /// nothing went wrong, and every cause looks identical from the outside: it just lies there.
        /// This runs the same gates CanShootThisPacket() does and names the first one that fails,
        /// which is the difference between "the machinegun is broken" and "the machinegun is prone
        /// behind a rise and cannot see you".
        ///
        /// Read-only - it must not consume the trigger state or draw an aim sample.
        /// </summary>
        public string DescribeFireBlock()
        {
            if (Self == null || Self.life == null || Self.life.isDead)
            {
                return "dead";
            }

            // Before every standing order, because it outranks all of them: a seated bandit is not
            // being driven by the on-foot tick at all, so none of the reasons below are the reason.
            if (Driver != null && Driver.IsSeated)
            {
                return $"in a vehicle ({Driver.Describe()})";
            }

            if (HoldFire)
            {
                return "holding fire";
            }

            if (WeaponDown)
            {
                return "weapon down (sprinting)";
            }

            if (IsGesturing)
            {
                return "gesturing";
            }

            if (_aimIntent == AimIntent.None)
            {
                return "nothing to shoot at";
            }

            if (_aimIntent == AimIntent.Gesture)
            {
                return "facing a gesture target";
            }

            if (!IsGunReady())
            {
                return "gun not ready";
            }

            float range = (_aimPoint - EyePosition).magnitude;
            if (range > FireRange)
            {
                return $"out of range ({range:0}m > {FireRange:0}m)";
            }

            if (!IsAimedAtTarget())
            {
                Vector3 aimDirection = Quaternion.Euler(_currentPitch - 90f, _currentYaw, 0f) * Vector3.forward;
                float off = Vector3.Angle(aimDirection, (_aimPoint - EyePosition).normalized);

                // Worth calling out separately: prone is held to 60-120 degrees of pitch, so a
                // gunner lying down simply cannot point at something steep, and "not aimed" alone
                // would look like it was merely slow to turn.
                string stanceNote = Self.stance != null && Self.stance.stance == EPlayerStance.PRONE
                    && (_currentPitch <= 60.01f || _currentPitch >= 119.99f)
                    ? " - prone pitch limit reached"
                    : string.Empty;

                return $"not aimed (off by {off:0.#}deg){stanceNote}";
            }

            if (IsFriendlyInLineOfFire())
            {
                return "squadmate in the line of fire";
            }

            bool visible = HasLineOfSightToPoint(_aimPoint);
            if (!visible)
            {
                string stanceNote = Self.stance != null && Self.stance.stance == EPlayerStance.PRONE
                    ? " (prone - eyes at 0.35m)"
                    : string.Empty;
                return $"no line of sight{stanceNote}";
            }

            if (Time.time < _nextFireTime && _burstTarget <= 0)
            {
                return "between shots";
            }

            return "clear to fire";
        }

        private int DrawBurstSize()
        {
            int min = Mathf.Max(1, ActiveBurstMinRounds);
            int max = Mathf.Max(min, ActiveBurstMaxRounds);
            return UnityEngine.Random.Range(min, max + 1);
        }

        /// <summary>
        /// How long to leave the trigger down before abandoning the rest of a burst.
        ///
        /// Deliberately generous - twice as long as the rounds should take - because this is a
        /// stuck-state backstop rather than part of the cadence. A tight bound would quietly
        /// shorten bursts whenever the server had a slow frame, which is exactly the kind of thing
        /// that reads as "the burst size setting doesn't work".
        /// </summary>
        private float BurstTimeoutSeconds(int rounds)
        {
            return Mathf.Max(0.5f, rounds * SecondsPerRound() * 2f + 0.4f);
        }

        /// <summary>
        /// Shortest gap the gun in hand allows between rounds. UseableGun.tockShoot() will not fire
        /// again until more than Firerate ticks of the equipment clock have passed, and that clock
        /// runs at TocksPerSecond. Attachments shave a little off Firerate, which is not accounted
        /// for here - both callers only want an order of magnitude and are lenient in the safe
        /// direction.
        /// </summary>
        private float SecondsPerRound()
        {
            ItemGunAsset gun = Self.equipment != null ? Self.equipment.asset as ItemGunAsset : null;
            int firerate = gun != null ? gun.firerate : 0;
            return (firerate + 1) / TocksPerSecond;
        }

        /// <summary>
        /// How many hit reports the packet being built needs: one for every round that could leave
        /// the barrel while it is simulated.
        ///
        /// The server never raycasts bullets itself. UseableGun.ballistics() pairs each round it
        /// fired with one InputInfo taken from the packet's queue and silently drops any round it
        /// cannot pair, so a burst that supplies one report does one round of damage and N-1 rounds
        /// of noise and muzzle flash. PlayerInput runs SAMPLES tocks per packet and the gun can fire
        /// on one tock in every Firerate+1 of them, which bounds the count - it is 1 for anything
        /// from an Eaglefire upwards and only climbs for the handful of guns with a Firerate of 3
        /// or less.
        ///
        /// Supplying too many is harmless: PlayerInput replaces the whole queue with the next
        /// packet's, so leftovers are discarded rather than banked. Supplying too few silently
        /// loses damage. The count is still capped at what the burst has left to fire, so a gun
        /// quick enough to get two rounds off in one packet cannot overrun its configured size.
        /// </summary>
        private int PlanHitReportCount()
        {
            int samplesPerPacket = (int)PlayerInput.SAMPLES;
            int ticksPerRound = Mathf.Max(1, Mathf.RoundToInt(SecondsPerRound() * TocksPerSecond));
            int count = Mathf.Clamp(Mathf.CeilToInt((float)samplesPerPacket / ticksPerRound), 1, samplesPerPacket);

            if (_burstTarget > 0)
            {
                count = Mathf.Min(count, Mathf.Max(1, _burstTarget - _burstRoundsFired));
            }

            return count;
        }

        /// <summary>
        /// Rounds that actually left the barrel since the last packet, read off the magazine.
        ///
        /// UseableGun.fire() takes the asset's Ammo_Per_Shot off its cached count and writes the
        /// result straight into equipment.state[10], so the magazine is the one place the server
        /// records shots the bot never explicitly asked for - which is precisely what a latched
        /// trigger produces. A reading that went up is a reload or an InfiniteAmmo top-up rather
        /// than negative shots, so it only re-baselines.
        /// </summary>
        private int ObserveRoundsFired()
        {
            byte[] state = Self.equipment != null ? Self.equipment.state : null;
            if (state == null || state.Length <= AmmoStateIndex)
            {
                _lastObservedAmmo = -1;
                return 0;
            }

            int ammo = state[AmmoStateIndex];
            int previous = _lastObservedAmmo;
            _lastObservedAmmo = ammo;

            if (previous < 0 || ammo >= previous)
            {
                return 0;
            }

            ItemGunAsset gun = Self.equipment.asset as ItemGunAsset;
            int perShot = Mathf.Max(1, gun != null ? gun.ammoPerShot : 1);

            // Rounded up, because a magazine too short to pay a full Ammo_Per_Shot still fires the
            // shot that empties it.
            return (previous - ammo + perShot - 1) / perShot;
        }

        /// <summary>
        /// Draws this round's miss and snaps the sway onto it, rather than reusing whatever the
        /// sway happens to be sitting on - so every round is an independent trial and the measured
        /// hit rate comes out at AimHitChance. Snapping the current error too keeps the packet we
        /// are about to build, its hit raycast and the replicated aim all pointing the same way.
        ///
        /// roundIndex is how far into a burst this round is, which widens the draw: see
        /// BanditConfiguration.BurstErrorRampPerRound for why a burst should not simply be a
        /// strictly better single shot.
        /// </summary>
        private void SnapAimErrorForRound(int roundIndex)
        {
            float errorScale = 1f + Mathf.Max(0f, BurstErrorRampPerRound) * Mathf.Max(0, roundIndex);

            SampleAimError(out _aimErrorYawTarget, out _aimErrorPitchTarget, errorScale);
            _aimErrorYaw = _aimErrorYawTarget;
            _aimErrorPitch = _aimErrorPitchTarget;
            _nextWobbleSampleTime = Time.time + Mathf.Max(AimWobbleIntervalSeconds, 0.01f);
        }

        private int ActiveBurstMinRounds => IsHoldingSecondary ? SecondaryBurstMinRounds : PrimaryBurstMinRounds;

        private int ActiveBurstMaxRounds => IsHoldingSecondary ? SecondaryBurstMaxRounds : PrimaryBurstMaxRounds;

        /// <summary>
        /// Holds aim-down-sights while a target is in range. Hip-firing an Eaglefire carries a lot
        /// of spread, so an un-aimed bot sprays around its target rather than hitting it.
        /// UseableGun.startSecondary() sets isAiming and replicates the aim pose to other clients,
        /// so this also makes the bot visibly shoulder the rifle. Start latches it on, Stop
        /// releases; vanilla ignores a redundant Start/Stop, so this sequencing is safe.
        /// </summary>
        private EAttackInputFlags DecideAimInput()
        {
            bool wantsToAim = !HoldFire && !WeaponDown && !IsGesturing && IsGunReady()
                && (_aimIntent == AimIntent.Target || _aimIntent == AimIntent.Suppression)
                && IsTargetInRange();

            if (wantsToAim && !_aimingActive)
            {
                _aimingActive = true;
                return EAttackInputFlags.Start;
            }

            if (!wantsToAim && _aimingActive)
            {
                _aimingActive = false;
                return EAttackInputFlags.Stop;
            }

            return EAttackInputFlags.None;
        }

        /// <summary>
        /// Where the bot's shots actually leave from - PlayerLook.aim is the same transform
        /// UseableGun fires along, so line-of-sight tests agree with where the bullet can reach.
        /// Falls back to a nominal eye height if look isn't wired up yet.
        /// </summary>
        private Vector3 EyePosition
        {
            get
            {
                return Self != null && Self.look != null && Self.look.aim != null
                    ? Self.look.aim.position
                    : transform.position + Vector3.up * 1.5f;
            }
        }

        private Vector3 TargetAimPoint
        {
            get { return _target == null ? EyePosition : AimPointOf(_target); }
        }

        /// <summary>
        /// Where on a player the bot points: the chest, wherever that has ended up.
        ///
        /// Taken as a fraction of the way from the feet to that player's own aim transform, so it
        /// follows their stance for free - roughly 1.2m on someone standing, 0.85m crouched, 0.25m
        /// prone. A fixed offset off the ground cannot do that, and the one this replaced (a flat
        /// 1.5m) sailed a clear metre over anyone lying down.
        ///
        /// The chest rather than the eye because the aim error model in SampleAimError() draws a
        /// miss around this point with a half-height of AimTargetHalfHeight; centred on the eyes,
        /// half of that ellipse is over open air above the head and the measured hit rate comes out
        /// under the configured one.
        /// </summary>
        private static Vector3 AimPointOf(Player player)
        {
            return Vector3.Lerp(player.transform.position, EyeOf(player), ChestHeightFraction);
        }

        /// <summary>
        /// A player's own aim transform - their eye, and where their shots come from. Follows their
        /// stance, so this is 1.75m standing and 0.35m prone.
        /// </summary>
        private static Vector3 EyeOf(Player player)
        {
            return player.look != null && player.look.aim != null
                ? player.look.aim.position
                : player.transform.position + Vector3.up * 1.75f;
        }

        /// <summary>
        /// True when nothing solid sits between the bot's eye and the target's.
        ///
        /// This mirrors what vanilla InteractableSentry.ScanForTargets() does, including the second
        /// backwards ray: a single outbound ray misses geometry whose colliders only face the other
        /// way, so a sentry - and now the bot - fires the return trip from just short of the target
        /// as well. BLOCK_SENTRY covers terrain, objects, barricades, structures and vehicles;
        /// neither mask contains the player layers, so bodies never count as cover for each other.
        /// </summary>
        /// <summary>Exposed for the brain, so "am I exposed" means the same as "can I be shot".</summary>
        public bool HasLineOfSightTo(Player candidate)
        {
            return HasLineOfSight(candidate);
        }

        private bool HasLineOfSight(Player candidate)
        {
            if (!RequireLineOfSight)
            {
                return true;
            }

            if (candidate == null)
            {
                return false;
            }

            return HasLineOfSightToPoint(candidate.look != null && candidate.look.aim != null
                ? candidate.look.aim.position
                : candidate.transform.position + Vector3.up * 1.5f);
        }

        /// <summary>
        /// Height PlayerLook puts the aim transform at when prone (HEIGHT_LOOK_PRONE), which is
        /// where a prone bandit's eyes and its bullets both are.
        /// </summary>
        public const float ProneEyeHeight = 0.35f;

        /// <summary>The same, crouched (HEIGHT_LOOK_CROUCH).</summary>
        public const float CrouchEyeHeight = 1.2f;

        /// <summary>
        /// Whether this bandit would still be able to see a point from a given eye height, i.e.
        /// after dropping into a lower stance.
        ///
        /// Prone drops the aim transform from 1.75m to 0.35m, and that is where both the
        /// line-of-sight test and the bullet come from - so a gunner that goes flat on level ground
        /// is looking through every rise, kerb and tuft between it and the target. It ends up lying
        /// there holding its fire, which reads exactly like a machinegunner that cannot shoot.
        ///
        /// Rather than let it blind itself, the stance is tested before it is taken.
        /// </summary>
        public bool WouldKeepLineOfSightFromHeight(float eyeHeight, Vector3 point)
        {
            if (!RequireLineOfSight)
            {
                return true;
            }

            return HasLineOfSightFrom(transform.position + Vector3.up * eyeHeight, point);
        }

        /// <summary>
        /// The same visibility test against a bare position, for suppressive fire - which is aimed
        /// at a place that may well have nobody standing in it.
        /// </summary>
        private bool HasLineOfSightToPoint(Vector3 aimPoint)
        {
            return !RequireLineOfSight || HasLineOfSightFrom(EyePosition, aimPoint);
        }

        private bool HasLineOfSightFrom(Vector3 origin, Vector3 aimPoint)
        {
            Vector3 toTarget = aimPoint - origin;
            float distance = toTarget.magnitude;
            if (distance <= LineOfSightSkinWidth)
            {
                return true; // practically inside each other; nothing can fit in between
            }

            Vector3 direction = toTarget / distance;
            float rayLength = distance - LineOfSightSkinWidth;

            RaycastHit hit;
            if (Physics.Raycast(new Ray(origin, direction), out hit, rayLength, LineOfSightForwardMask)
                && hit.transform != null && hit.transform != transform)
            {
                return false;
            }

            if (Physics.Raycast(new Ray(origin + direction * rayLength, -direction), out hit, rayLength, LineOfSightReturnMask)
                && hit.transform != null && hit.transform != transform)
            {
                return false;
            }

            return true;
        }

        private bool IsTargetInRange()
        {
            if (_aimIntent == AimIntent.None)
            {
                return false;
            }

            return (_aimPoint - EyePosition).magnitude <= FireRange;
        }

        /// <summary>
        /// True once the gun is actually in hand and its equip animation has played out. Until
        /// then the bot must not send any attack input at all - it would punch rather than shoot.
        /// Deliberately does NOT check equipment.isBusy: that flag is set while a shot is in
        /// flight, and vanilla's own startPrimary() already guards on it.
        /// </summary>
        internal bool IsGunReady()
        {
            PlayerEquipment equipment = Self.equipment;
            if (equipment == null)
            {
                return false;
            }

            return equipment.HasValidUseable
                && equipment.IsEquipAnimationFinished
                && equipment.useable is UseableGun;
        }

        private bool IsAimedAtTarget()
        {
            if (Self.life.isDead || _aimIntent == AimIntent.None)
            {
                return false;
            }

            Vector3 toTarget = _aimPoint - EyePosition;
            if (toTarget.magnitude > FireRange || toTarget.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            // Pitch counts as well as yaw. This used to flatten both vectors and compare headings
            // only, which meant the bot opened fire the moment it had turned onto the target no
            // matter where the barrel was pointing vertically - so every time the required pitch
            // moved sharply, most obviously on dropping prone, the first rounds went into the
            // ground. The direction is built exactly as AttachHitReports() builds the one it
            // raycasts along, so "aimed" means the same thing to both.
            //
            // Compared against where the bot is actually looking rather than where it wants to
            // look, so it still only shoots once it has finished turning onto the target, and
            // deliberately without the aim error, which is the shot's business and not the turn's.
            Vector3 aimDirection = Quaternion.Euler(_currentPitch - 90f, _currentYaw, 0f) * Vector3.forward;

            return Vector3.Angle(aimDirection, toTarget.normalized) <= AimToleranceDegrees;
        }

        internal void TopUpAmmoIfNeeded()
        {
            if (!InfiniteAmmo)
            {
                return;
            }

            byte[] state = Self.equipment.state;
            if (state == null || state.Length <= AmmoStateIndex)
            {
                return;
            }

            if (state[AmmoStateIndex] > 0)
            {
                return;
            }

            // Taken from the gun in hand rather than one configured number, because the bot can be
            // holding a rifle one moment and a sidearm the next.
            byte capacity = BanditLoadoutApplier.ResolveMagazineCapacity(
                Self.equipment.asset as ItemGunAsset, state);
            if (capacity == 0)
            {
                return;
            }

            state[AmmoStateIndex] = capacity;

            // Re-baseline the round counter in the same breath. ObserveRoundsFired() reads the
            // magazine before this runs, so without it the rounds fired between the refill and the
            // next packet would be attributed to a magazine that no longer exists and lost.
            _lastObservedAmmo = capacity;

            // The equipped UseableGun works off its own cached copy, so the state byte alone is not
            // enough - it would keep believing the magazine is empty.
            if (UseableGunAmmoField != null && Self.equipment.useable is UseableGun gun)
            {
                UseableGunAmmoField.SetValue(gun, capacity);
            }
        }

        /// <summary>
        /// Keeps the weapon the bot wants in its hands actually in its hands.
        ///
        /// This covers two jobs. The first is the original one: PlayerEquipment.ServerEquip()
        /// silently does nothing if the player is momentarily not in an equippable state
        /// (life.isDead, !canEquip, isBusy, mid-equip-animation). Right after spawn that is a race,
        /// which is why some bots ended up standing around holding nothing - the rifle went into
        /// their inventory but the equip call was dropped - so it is retried until it takes.
        ///
        /// The second is swapping between the primary and secondary slots as the range to the
        /// target changes. Both are the same operation, hence one method: ask for the slot we want,
        /// and ask again shortly if the request didn't stick.
        /// </summary>
        private void MaintainEquippedWeapon()
        {
            if (Self.equipment == null || Self.inventory == null || Self.life == null || Self.life.isDead)
            {
                return;
            }

            // A gesture is holding the bot's hands empty on purpose; this method exists to fill
            // them, so it has to stand down until the gesture is over or it would re-equip the
            // rifle on the very next tick and the wave would never play.
            if (IsGesturing)
            {
                return;
            }

            byte desiredPage = ChooseWeaponPage();

            // Careful: ServerEquip(page, x, y) with the page already equipped is vanilla's *dequip*
            // request, so this must only fire when the bot is holding nothing or holding the wrong
            // weapon - otherwise it would put the gun away every time it was called.
            bool holdingSomething = Self.equipment.HasValidUseable;
            if (holdingSomething && Self.equipment.equippedPage == desiredPage)
            {
                return;
            }

            if (Time.time < _nextEquipAttemptTime)
            {
                return;
            }

            // The preconditions ServerEquip checks before doing anything. Tested here as well so a
            // request vanilla was always going to drop - mid-shot, mid-equip - neither burns a
            // retry interval nor resets the aim state below on a swap that never happened.
            if (Self.equipment.isBusy
                || !Self.equipment.canEquip
                || (holdingSomething && !Self.equipment.IsEquipAnimationFinished))
            {
                return;
            }

            if (Self.inventory.getItemCount(desiredPage) == 0)
            {
                return;
            }

            _nextEquipAttemptTime = Time.time + EquipRetryIntervalSeconds;

            // A swap dequips whatever was in hand, which drops aim-down-sights and any half-pulled
            // trigger with it. Without clearing these the bot would still believe it was shouldered
            // and would never send the Start that shoulders the new weapon. The burst goes too: its
            // round count belongs to a magazine that is no longer in the bot's hands.
            _aimingActive = false;
            _triggerHeld = false;
            CancelBurst();

            Self.equipment.ServerEquip(desiredPage, 0, 0);
        }

        /// <summary>
        /// Which equipment slot the bot wants out. The rifle, unless it has a sidearm and the
        /// target has closed to within SecondaryWeaponRange - measured with a hysteresis band so
        /// the bot doesn't spend a fight at that exact distance swapping weapons instead of
        /// shooting.
        /// </summary>
        private byte ChooseWeaponPage()
        {
            if (!HasSecondaryWeapon)
            {
                return BanditLoadoutApplier.PrimarySlotPage;
            }

            if (!HasPrimaryWeapon)
            {
                return BanditLoadoutApplier.SecondarySlotPage;
            }

            if (SecondaryWeaponRange <= 0f || _target == null || _target.life.isDead)
            {
                return BanditLoadoutApplier.PrimarySlotPage;
            }

            float threshold = IsHoldingSecondary
                ? SecondaryWeaponRange + WeaponSwitchHysteresisMetres
                : SecondaryWeaponRange;

            return (TargetAimPoint - EyePosition).magnitude <= threshold
                ? BanditLoadoutApplier.SecondarySlotPage
                : BanditLoadoutApplier.PrimarySlotPage;
        }

        /// <summary>What is actually in the bot's hands right now, for /banditstatus - the only way
        /// to see from in-game whether a loadout applied and whether the sidearm swap is firing.</summary>
        public string EquippedWeaponName
        {
            get
            {
                if (Self == null || Self.equipment == null || !Self.equipment.HasValidUseable || Self.equipment.asset == null)
                {
                    return "nothing";
                }

                return Self.equipment.asset.FriendlyName;
            }
        }

        private bool IsHoldingSecondary
        {
            get
            {
                return Self.equipment != null
                    && Self.equipment.HasValidUseable
                    && Self.equipment.equippedPage == BanditLoadoutApplier.SecondarySlotPage;
            }
        }

        /// <summary>
        /// Hit chance of whatever is currently in the bot's hands, so a sidearm can be made
        /// scrappier than the rifle without touching the rest of the aim model.
        /// </summary>
        private float ActiveAimHitChance => IsHoldingSecondary ? SecondaryAimHitChance : PrimaryAimHitChance;

        /// <summary>
        /// Raycasts along the direction this packet is about to aim in, and injects the hit into
        /// the packet's own serversideInputs queue - which is exactly where PlayerInput assigns
        /// player.input.inputs from when it processes the packet, so ballistics() finds it.
        /// We briefly point PlayerInput.inputs at that queue and let vanilla's own sendRaycast do
        /// the RaycastInfo -> InputInfo conversion (limb, material, entity type, ...) rather than
        /// reimplementing that mapping and getting a field wrong.
        ///
        /// One raycast covers all <paramref name="count"/> reports, because they all stand for
        /// rounds fired during this packet along the one aim it replicates. That means a gun quick
        /// enough to fire twice inside 80ms has both those rounds hit or both miss together - only
        /// true for guns with a Firerate of 3 or less, since everything above that fires at most
        /// once per packet and gets its own draw. Reporting them along separate angles would buy
        /// independence at the cost of the invariant below, which is not a good trade.
        /// </summary>
        private void AttachHitReports(WalkingPlayerInputPacket packet, int count)
        {
            // Server-created bullets (fire() for a non-local player) get no spread - they travel
            // straight along the aim direction - so this raycast matches the bullet exactly.
            // It must use the packet's own yaw/pitch, aim error and all: those are the angles
            // PlayerLook will be simulated to, so a wobbled shot reports the hit it really made
            // (often nothing, which is the point) instead of the one it was aiming for.
            AttachHitReports(packet, count, Self.look.aim.position,
                Quaternion.Euler(packet.pitch - 90f, packet.yaw, 0f) * Vector3.forward, FireRange);
        }

        /// <summary>
        /// The same, along a muzzle line the caller worked out for itself.
        ///
        /// A turret's angles are seat-local, so the packet's yaw and pitch mean nothing in world
        /// space and the overload above cannot be used from a vehicle. The gunner converts them
        /// through the seat transform and passes the result here, which keeps one implementation of
        /// the awkward part - borrowing PlayerInput.inputs so vanilla's own sendRaycast does the
        /// RaycastInfo to InputInfo conversion.
        /// </summary>
        internal void AttachHitReports(WalkingPlayerInputPacket packet, int count, Vector3 origin, Vector3 direction, float range)
        {
            if (PlayerInputInputsField == null)
            {
                return;
            }

            RaycastInfo raycastInfo = DamageTool.raycast(new Ray(origin, direction), range, RayMasks.DAMAGE_CLIENT, Self);
            if (Self.input.isRaycastInvalid(raycastInfo))
            {
                return; // genuine miss - hit nothing at all
            }

            packet.serversideInputs = new Queue<InputInfo>();

            object previousInputs = PlayerInputInputsField.GetValue(Self.input);
            try
            {
                PlayerInputInputsField.SetValue(Self.input, packet.serversideInputs);
                for (int i = 0; i < count; i++)
                {
                    Self.input.sendRaycast(raycastInfo, ERaycastInfoUsage.Gun);
                }
            }
            finally
            {
                PlayerInputInputsField.SetValue(Self.input, previousInputs);
            }
        }

        private void EnqueueInputPacket(EAttackInputFlags primaryAttack, EAttackInputFlags secondaryAttack)
        {
            float packetYaw = _currentYaw + _aimErrorYaw;

            WalkingPlayerInputPacket packet = new WalkingPlayerInputPacket
            {
                analog = BuildAnalog(packetYaw),

                // Only used to decide whether the server sends the owner a mispredict correction
                // or a good-input ack. A moving bot will mismatch by one tick's worth of travel
                // every packet, so it takes the mispredict branch - which costs one unreliable
                // RPC into a FakeTransportConnection that throws it away.
                clientPosition = transform.position,
                yaw = packetYaw,
                pitch = Mathf.Clamp(_currentPitch + _aimErrorPitch, 0f, 180f),
                keys = BuildKeys(),
                primaryAttack = primaryAttack,
                secondaryAttack = secondaryAttack,
                recov = Self.input.recov,
                clientSimulationFrameNumber = _clientSimulationFrameNumber++
            };

            // Only a packet that can put rounds downrange needs hit reports, and during a burst that
            // is every packet the trigger stays latched for - not just the one carrying the Start.
            // The trigger logic works out how many; see AttachHitReports().
            if (_hitReportsForThisPacket > 0)
            {
                AttachHitReports(packet, _hitReportsForThisPacket);
            }
            _hitReportsForThisPacket = 0;

            _serversidePackets.Enqueue(packet);
        }

        private Player FindNearestRealPlayer()
        {
            Player nearest = null;

            // Doubles as the acquisition cap, so anyone beyond it can never become the nearest.
            // Without one the scan took the nearest visible player at any distance at all and
            // turned the body onto them, which is how a bandit ended up tracking someone across a
            // valley it had no hope of reaching. Per class, so a marksman really does see further.
            float nearestDistanceSq = TargetAcquireRange > 0f
                ? TargetAcquireRange * TargetAcquireRange
                : float.MaxValue;

            foreach (SteamPlayer steamPlayer in Provider.clients)
            {
                Player candidate = steamPlayer.player;
                if (candidate == null || candidate == Self || candidate.life.isDead)
                {
                    continue;
                }

                // Don't let bots target each other.
                if (candidate.gameObject.GetComponent<BanditBotController>() != null)
                {
                    continue;
                }

                float distanceSq = (candidate.transform.position - transform.position).sqrMagnitude;
                if (distanceSq >= nearestDistanceSq)
                {
                    continue;
                }

                // Last, because it is the only test here that costs raycasts: a player the bot
                // cannot see is not a target, so it locks onto the nearest *visible* player rather
                // than tracking the nearest one through a wall and waiting for them to step out.
                // To the point it would aim at, for the same reason the firing check is: a
                // target whose head is visible over a wall its chest is behind is one this bandit
                // cannot hit, and locking onto it only stops it looking for one it can.
                if (!HasLineOfSightToPoint(AimPointOf(candidate)))
                {
                    continue;
                }

                nearest = candidate;
                nearestDistanceSq = distanceSq;
            }

            return nearest;
        }
    }
}
