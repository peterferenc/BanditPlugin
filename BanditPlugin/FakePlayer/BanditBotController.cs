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

        /// <summary>Decides where the bot wants to walk. Created in Start, once Self is set.</summary>
        public BanditBrain Brain { get; private set; }

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

        public float TurnSpeedDegreesPerSecond = 180f;
        public float ScanIntervalSeconds = 0.5f;
        public float FireIntervalSeconds = 0.6f;
        public float AimToleranceDegrees = 10f;
        public float FireRange = 50f;
        public bool InfiniteAmmo = true;
        public bool HasPrimaryWeapon = true;
        public bool HasSecondaryWeapon;
        public float SecondaryWeaponRange;
        public float PrimaryAimHitChance = 0.3f;
        public float SecondaryAimHitChance = 0.3f;
        public float AimTargetRadius = 0.35f;
        public float AimTargetHalfHeight = 0.8f;
        public float AimMaxErrorDegrees = 8f;
        public float AimWobbleIntervalSeconds = 0.35f;
        public float AimWobbleSmoothingSeconds = 0.15f;
        public bool RequireLineOfSight = true;

        // input_x/input_y are decoded as ((analog >> 4) & 0xF) - 1 and (analog & 0xF) - 1, so the
        // neutral "no movement" value is 0x11, NOT 0. Sending 0 would make the bot walk backwards.
        private const byte AnalogNeutral = 0x11;

        // PlayerInput reconstructs its key array as (packet.keys & (1 << index)) != 0.
        private const ushort KeyJump = 1 << 0;
        private const ushort KeyCrouch = 1 << 3;
        private const ushort KeySprint = 1 << 5;
        private const ushort KeyLeanLeft = 1 << 6;
        private const ushort KeyLeanRight = 1 << 7;

        // sin(22.5 degrees). input_x/input_y are only ever -1, 0 or 1, so a desired direction has
        // to be quantised onto the eight compass points; this is the sector boundary between them.
        private const float OctantThreshold = 0.3827f;

        private const byte AmmoStateIndex = 10;   // PlayerEquipment.state[10] == rounds in magazine
        private const byte FiremodeStateIndex = 11; // PlayerEquipment.state[11] == EFiremode

        // Pull the line-of-sight rays up slightly short of the endpoints, so a ray that starts or
        // ends flush against a surface doesn't report that surface as cover. Same 0.025 vanilla
        // InteractableSentry.ScanForTargets uses.
        private const float LineOfSightSkinWidth = 0.025f;

        // The bot swaps to its sidearm at SecondaryWeaponRange and back to the rifle this much
        // further out, so a target loitering on the boundary doesn't make it swap every time it
        // takes a step. Any hysteresis band wider than a stride does the job; four metres is one.
        private const float WeaponSwitchHysteresisMetres = 4f;

        // ServerEquip is a request, not a command - it no-ops while the player is busy, dead or
        // mid-equip-animation - so equipping is retried on this interval rather than assumed.
        private const float EquipRetryIntervalSeconds = 0.5f;

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
            }

            // Feed packets at the same rate a real client sends them. PlayerInput self-regulates if
            // we run slightly fast, but there's no reason to let the queue grow.
            _packetAccumulator += Time.deltaTime;
            if (_packetAccumulator < PlayerInput.RATE)
            {
                return;
            }
            float elapsed = _packetAccumulator;
            _packetAccumulator = 0f;

            MaintainEquippedWeapon();

            // Before aiming, because with no target to lock onto the bot faces wherever the brain
            // is walking - and because the analog byte built below is relative to that facing.
            Brain?.Tick(elapsed, _target);

            AimAtTarget(elapsed);
            UpdateAimWobble(elapsed);
            // Order matters: DecideAttackInput() consumes the trigger state for this packet.
            EAttackInputFlags secondary = DecideAimInput();
            EAttackInputFlags primary = DecideAttackInput();
            EnqueueInputPacket(primary, secondary);
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

        private void AimAtTarget(float elapsed)
        {
            if (_target == null)
            {
                TurnTowardsTravelDirection(elapsed);
                return;
            }

            Vector3 eye = transform.position + Vector3.up * 1.5f;
            Vector3 toTarget = (_target.transform.position + Vector3.up * 1.5f) - eye;
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
            _currentPitch = Mathf.MoveTowards(_currentPitch, desiredPitch, TurnSpeedDegreesPerSecond * elapsed);
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
            if (Brain == null)
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
            if (Brain == null)
            {
                return 0;
            }

            ushort keys = 0;
            if (Brain.WantsJump)
            {
                keys |= KeyJump;
            }
            if (Brain.WantsCrouch)
            {
                keys |= KeyCrouch;
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
            if (_target == null)
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
            yawError = 0f;
            pitchError = 0f;

            float hitChance = Mathf.Clamp(ActiveAimHitChance, 0f, 0.999f);
            if (hitChance >= 0.999f || AimTargetRadius <= 0f || _target == null)
            {
                return; // configured back into a perfect aimbot
            }

            float scale = 1f / Mathf.Sqrt(-2f * Mathf.Log(1f - hitChance));

            // Clamped because at arm's length the angle subtended by a torso-width miss explodes;
            // AimMaxErrorDegrees catches the same case from the other side.
            float distance = Mathf.Max((TargetAimPoint - EyePosition).magnitude, 1f);

            yawError = ErrorDegrees(NextGaussian() * AimTargetRadius * scale, distance);
            pitchError = ErrorDegrees(NextGaussian() * AimTargetHalfHeight * scale, distance);
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
        /// Returns the trigger input for this packet. We alternate Start/Stop rather than holding
        /// the trigger down, so each fire interval is one discrete trigger pull - matching the SEMI
        /// firemode the bot is given, and behaving sanely if it's switched to AUTO.
        /// PlayerEquipment ignores a Start while already started and a Stop while already stopped,
        /// so this sequencing is safe.
        /// </summary>
        private EAttackInputFlags DecideAttackInput()
        {
            if (_triggerHeld)
            {
                _triggerHeld = false;
                return EAttackInputFlags.Stop;
            }

            if (HoldFire)
            {
                return EAttackInputFlags.None;
            }

            // Must come before anything else that can pull the trigger. PlayerEquipment.simulate
            // routes primary attacks to simulate_PunchInput whenever there is no valid useable, so
            // firing during the equip animation makes the bot throw punches instead of shooting.
            // simulate_UseableInput also ignores input until IsEquipAnimationFinished.
            if (!IsGunReady())
            {
                return EAttackInputFlags.None;
            }

            if (!IsAimedAtTarget())
            {
                return EAttackInputFlags.None;
            }

            if (Time.time < _nextFireTime)
            {
                return EAttackInputFlags.None;
            }

            // Re-checked here and not just at scan time: the target can step behind cover in the
            // half second between scans, and this is the last moment before the round goes out.
            if (!HasLineOfSight(_target))
            {
                return EAttackInputFlags.None;
            }

            TopUpAmmoIfNeeded();

            // Draw this shot's miss now rather than reusing whatever the sway happens to be sitting
            // on, so every shot is an independent draw and the hit rate actually comes out at
            // AimHitChance. Snapping the current error too keeps the packet we are about to build,
            // its hit raycast and the replicated aim all pointing the same way.
            SampleAimError(out _aimErrorYawTarget, out _aimErrorPitchTarget);
            _aimErrorYaw = _aimErrorYawTarget;
            _aimErrorPitch = _aimErrorPitchTarget;
            _nextWobbleSampleTime = Time.time + Mathf.Max(AimWobbleIntervalSeconds, 0.01f);

            _nextFireTime = Time.time + FireIntervalSeconds;
            _triggerHeld = true;
            return EAttackInputFlags.Start;
        }

        /// <summary>
        /// Holds aim-down-sights while a target is in range. Hip-firing an Eaglefire carries a lot
        /// of spread, so an un-aimed bot sprays around its target rather than hitting it.
        /// UseableGun.startSecondary() sets isAiming and replicates the aim pose to other clients,
        /// so this also makes the bot visibly shoulder the rifle. Start latches it on, Stop
        /// releases; vanilla ignores a redundant Start/Stop, so this sequencing is safe.
        /// </summary>
        private EAttackInputFlags DecideAimInput()
        {
            bool wantsToAim = !HoldFire && IsGunReady() && _target != null && !_target.life.isDead && IsTargetInRange();

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
            get
            {
                if (_target == null)
                {
                    return EyePosition;
                }

                return _target.look != null && _target.look.aim != null
                    ? _target.look.aim.position
                    : _target.transform.position + Vector3.up * 1.5f;
            }
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

            Vector3 origin = EyePosition;
            Vector3 aimPoint = candidate.look != null && candidate.look.aim != null
                ? candidate.look.aim.position
                : candidate.transform.position + Vector3.up * 1.5f;

            Vector3 toTarget = aimPoint - origin;
            float distance = toTarget.magnitude;
            if (distance <= LineOfSightSkinWidth)
            {
                return true; // practically inside each other; nothing can fit in between
            }

            Vector3 direction = toTarget / distance;
            float rayLength = distance - LineOfSightSkinWidth;

            RaycastHit hit;
            if (Physics.Raycast(new Ray(origin, direction), out hit, rayLength, RayMasks.BLOCK_SENTRY)
                && hit.transform != null && hit.transform != transform)
            {
                return false;
            }

            if (Physics.Raycast(new Ray(origin + direction * rayLength, -direction), out hit, rayLength, RayMasks.DAMAGE_SERVER)
                && hit.transform != null && hit.transform != transform)
            {
                return false;
            }

            return true;
        }

        private bool IsTargetInRange()
        {
            if (_target == null)
            {
                return false;
            }

            Vector3 eye = transform.position + Vector3.up * 1.5f;
            return ((_target.transform.position + Vector3.up * 1.5f) - eye).magnitude <= FireRange;
        }

        /// <summary>
        /// True once the gun is actually in hand and its equip animation has played out. Until
        /// then the bot must not send any attack input at all - it would punch rather than shoot.
        /// Deliberately does NOT check equipment.isBusy: that flag is set while a shot is in
        /// flight, and vanilla's own startPrimary() already guards on it.
        /// </summary>
        private bool IsGunReady()
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
            if (_target == null || _target.life.isDead || Self.life.isDead)
            {
                return false;
            }

            Vector3 eye = transform.position + Vector3.up * 1.5f;
            Vector3 toTarget = (_target.transform.position + Vector3.up * 1.5f) - eye;
            if (toTarget.magnitude > FireRange)
            {
                return false;
            }

            // Compare against where the bot is actually looking, not where it wants to look, so it
            // only shoots once it has finished turning onto the target.
            Vector3 aimDirection = Quaternion.Euler(0f, _currentYaw, 0f) * Vector3.forward;
            Vector3 flatToTarget = new Vector3(toTarget.x, 0f, toTarget.z);
            if (flatToTarget.sqrMagnitude < 0.0001f)
            {
                return false;
            }

            return Vector3.Angle(aimDirection, flatToTarget.normalized) <= AimToleranceDegrees;
        }

        private void TopUpAmmoIfNeeded()
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
            // and would never send the Start that shoulders the new weapon.
            _aimingActive = false;
            _triggerHeld = false;

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
        /// </summary>
        private void AttachHitReport(WalkingPlayerInputPacket packet)
        {
            if (PlayerInputInputsField == null)
            {
                return;
            }

            // Server-created bullets (fire() for a non-local player) get no spread - they travel
            // straight along the aim direction - so this raycast matches the bullet exactly.
            // It must use the packet's own yaw/pitch, aim error and all: those are the angles
            // PlayerLook will be simulated to, so a wobbled shot reports the hit it really made
            // (often nothing, which is the point) instead of the one it was aiming for.
            Vector3 origin = Self.look.aim.position;
            Vector3 direction = Quaternion.Euler(packet.pitch - 90f, packet.yaw, 0f) * Vector3.forward;

            RaycastInfo raycastInfo = DamageTool.raycast(new Ray(origin, direction), FireRange, RayMasks.DAMAGE_CLIENT, Self);
            if (Self.input.isRaycastInvalid(raycastInfo))
            {
                return; // genuine miss - hit nothing at all
            }

            packet.serversideInputs = new Queue<InputInfo>();

            object previousInputs = PlayerInputInputsField.GetValue(Self.input);
            try
            {
                PlayerInputInputsField.SetValue(Self.input, packet.serversideInputs);
                Self.input.sendRaycast(raycastInfo, ERaycastInfoUsage.Gun);
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

            // Only a packet that actually pulls the trigger needs a hit report attached.
            if (primaryAttack.HasFlag(EAttackInputFlags.Start))
            {
                AttachHitReport(packet);
            }

            _serversidePackets.Enqueue(packet);
        }

        private Player FindNearestRealPlayer()
        {
            Player nearest = null;
            float nearestDistanceSq = float.MaxValue;

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
                if (!HasLineOfSight(candidate))
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
