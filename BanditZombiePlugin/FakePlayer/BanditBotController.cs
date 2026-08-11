using System.Collections.Generic;
using System.Reflection;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditZombiePlugin.FakePlayer
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

        public float TurnSpeedDegreesPerSecond = 180f;
        public float ScanIntervalSeconds = 0.5f;
        public float FireIntervalSeconds = 0.6f;
        public float AimToleranceDegrees = 10f;
        public float FireRange = 50f;
        public bool InfiniteAmmo = true;

        // input_x/input_y are decoded as ((analog >> 4) & 0xF) - 1 and (analog & 0xF) - 1, so the
        // neutral "no movement" value is 0x11, NOT 0. Sending 0 would make the bot walk backwards.
        private const byte AnalogNeutral = 0x11;

        private const byte AmmoStateIndex = 10;   // PlayerEquipment.state[10] == rounds in magazine
        private const byte FiremodeStateIndex = 11; // PlayerEquipment.state[11] == EFiremode

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

        private void Start()
        {
            _currentYaw = transform.eulerAngles.y;

            if (ServersidePacketsField == null)
            {
                Logger.LogError("[BanditZombie] Could not reflect PlayerInput.serversidePackets - the bot cannot be driven. Game version may have changed.");
                enabled = false;
                return;
            }

            _serversidePackets = ServersidePacketsField.GetValue(Self.input) as Queue<PlayerInputPacket>;
            if (_serversidePackets == null)
            {
                Logger.LogError("[BanditZombie] PlayerInput.serversidePackets was not a Queue<PlayerInputPacket>; the bot cannot be driven.");
                enabled = false;
            }
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

            EnsureGunEquipped();
            AimAtTarget(elapsed);
            // Order matters: DecideAttackInput() consumes the trigger state for this packet.
            EAttackInputFlags secondary = DecideAimInput();
            EAttackInputFlags primary = DecideAttackInput();
            EnqueueInputPacket(primary, secondary);
        }

        private void AimAtTarget(float elapsed)
        {
            if (_target == null)
            {
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

            TopUpAmmoIfNeeded();

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
            bool wantsToAim = IsGunReady() && _target != null && !_target.life.isDead && IsTargetInRange();

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

            state[AmmoStateIndex] = BanditZombiePlugin.Instance.Configuration.Instance.MagazineCapacity;

            // The equipped UseableGun works off its own cached copy, so the state byte alone is not
            // enough - it would keep believing the magazine is empty.
            if (UseableGunAmmoField != null && Self.equipment.useable is UseableGun gun)
            {
                UseableGunAmmoField.SetValue(gun, BanditZombiePlugin.Instance.Configuration.Instance.MagazineCapacity);
            }
        }

        /// <summary>
        /// PlayerEquipment.ServerEquip() silently does nothing if the player is momentarily not in
        /// an equippable state (life.isDead, !canEquip, isBusy). Right after spawn that is a race,
        /// which is why some bots ended up standing around holding nothing: the rifle went into
        /// their inventory but the equip call was dropped. Retry until it actually takes.
        /// </summary>
        private void EnsureGunEquipped()
        {
            if (Self.equipment == null || Self.equipment.HasValidUseable || Self.life == null || Self.life.isDead)
            {
                return;
            }

            if (Time.time < _nextEquipAttemptTime)
            {
                return;
            }
            _nextEquipAttemptTime = Time.time + 0.5f;

            // forceAddItem(..., auto: true) routes primary weapons into page 0 slot (0,0).
            if (Self.inventory != null && Self.inventory.getItemCount(0) > 0)
            {
                Self.equipment.ServerEquip(0, 0, 0);
            }
        }

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
            Vector3 origin = Self.look.aim.position;
            Vector3 direction = Quaternion.Euler(_currentPitch - 90f, _currentYaw, 0f) * Vector3.forward;

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
            WalkingPlayerInputPacket packet = new WalkingPlayerInputPacket
            {
                analog = AnalogNeutral,
                clientPosition = transform.position,
                yaw = _currentYaw,
                pitch = _currentPitch,
                keys = 0,
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
                if (distanceSq < nearestDistanceSq)
                {
                    nearest = candidate;
                    nearestDistanceSq = distanceSq;
                }
            }

            return nearest;
        }
    }
}
