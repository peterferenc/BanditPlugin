using System.Collections.Generic;
using BanditPlugin.Navigation;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Everything a bandit does from a vehicle seat: getting in, staying put, driving somewhere, and
    /// tracking a target from a gunner's seat.
    ///
    /// The bot drives for the same reason it walks: vanilla lets whoever is in seat 0 say where the
    /// vehicle is. PlayerInput branches on the packet type, and a DrivingPlayerInputPacket carries a
    /// position and a rotation where the walking one carries an analog byte.
    /// InteractableVehicle.simulate() ends in rootRigidbody.MovePosition(point)/MoveRotation(angle),
    /// and updatePhysics() makes the rigidbody kinematic the moment seat 0 is occupied. The driver
    /// *is* the physics - there is no engine to model, only a pose to report.
    ///
    /// Which packet to send is decided by the seat, not by preference. A driver's stance is DRIVING
    /// and only the driving packet reaches InteractableVehicle.simulate; every other seat's stance
    /// is SITTING, and it is the *walking* packet's SITTING branch that calls
    /// PlayerMovement.ServerUpdateTurretAim() - the one thing that replicates where a gunner is
    /// pointing. Send a driving packet from a turret seat and the turret never moves for anyone.
    ///
    /// Two gates decide whether the server believes a driving packet (off LAN, ForceTrustClient
    /// unset): the horizontal step must be within asset.sqrDelta, and the vertical speed within
    /// asset.validSpeedUp/validSpeedDown. Failing either starts a recovery - the vehicle snaps back
    /// and further packets are ignored for a few seconds - so every step below is clamped to both.
    /// </summary>
    public class BanditVehicleDriver
    {
        /// <summary>How far from the bandit to look for something to get into.</summary>
        public const float SearchRadiusMetres = 50f;

        /// <summary>Seat 0 is the driver in every vehicle: it is what InteractableVehicle.isDriven
        /// tests, and the seat vanilla hands out first.</summary>
        public const byte DriverSeat = 0;

        /// <summary>Seat 1 - the seat a player takes with F2, and the gunner's seat in anything
        /// with a turret behind the driver.</summary>
        public const byte GunnerSeat = 1;

        /// <summary>Vanilla's full battery, from InteractableVehicle's own charge arithmetic.</summary>
        private const ushort FullBatteryCharge = 10000;

        // input_x/input_y are decoded as ((analog >> 4) & 0xF) - 1 and (analog & 0xF) - 1, so the
        // neutral "no movement" value is 0x11 rather than 0. A seated bandit never wants anything
        // else: vanilla ignores movement input from a seat, but a 0 here would still be decoded as
        // "walk backwards" if it ever climbed out mid-packet.
        private const byte AnalogNeutral = 0x11;

        // How fast a bandit swings a turret onto its target. Slow enough to read as a turret being
        // traversed rather than snapping, fast enough to keep up with someone walking past.
        private const float TurretTraverseDegreesPerSecond = 70f;

        // How fast the hull comes round onto a new heading. A vehicle held kinematic can spin on the
        // spot, so this is the only thing making it look like it has a turning circle.
        private const float HullTurnDegreesPerSecond = 55f;

        private const float AccelerationMetresPerSecondSquared = 6f;

        /// <summary>Fastest a bandit will drive, whatever the vehicle could do. Fast enough to cover
        /// ground, slow enough that the width sweep gets to see an obstacle before the bumper does.</summary>
        private const float MaxCruiseMetresPerSecond = 14f;

        private const float MinCruiseMetresPerSecond = 3f;

        /// <summary>Fraction of each validation limit actually used, so a slow server frame cannot
        /// push a step over the line and start a recovery.</summary>
        private const float ValidationSafetyFactor = 0.85f;

        /// <summary>Heading error at which the vehicle stops moving and only turns.</summary>
        private const float StopAndTurnDegrees = 120f;

        /// <summary>Beyond this a player is not worth turning a turret onto.</summary>
        private const float TurretTrackRangeMetres = 250f;

        private const float ConsumableCheckIntervalSeconds = 1f;

        private readonly Player _self;
        private readonly BanditVehicleNavigator _navigator;

        private BanditVehicleFootprint _footprint = BanditVehicleFootprint.Default;
        private InteractableVehicle _footprintMeasuredFor;

        private float _speed;
        private Vector3 _groundNormal = Vector3.up;

        // The pose this bandit last told the server the vehicle was in.
        //
        // Steps are taken from here rather than from vehicle.transform, because the transform can be
        // a packet behind: MovePosition on a kinematic body lands at the next physics step, while
        // the server has already accepted our reported point into InteractableVehicle.real and is
        // measuring the *next* packet against it. Stepping from a stale transform would therefore
        // hand it a delta of up to two steps and trip the anti-teleport check the step is carefully
        // sized to stay inside.
        private Vector3 _commandedPosition;
        private Quaternion _commandedRotation = Quaternion.identity;
        private bool _hasCommandedPose;

        private float _lookYaw;
        private float _lookPitch = 90f;

        private float _nextConsumableCheck;

        public BanditVehicleDriver(Player self)
        {
            _self = self;
            _navigator = new BanditVehicleNavigator(self);
        }

        /// <summary>The vehicle this bandit is sitting in, or null.</summary>
        public InteractableVehicle Vehicle =>
            _self != null && _self.movement != null ? _self.movement.getVehicle() : null;

        /// <summary>True while the bandit occupies any seat - which is when the controller has to
        /// send vehicle packets instead of on-foot ones.</summary>
        public bool IsSeated => Vehicle != null;

        /// <summary>True only in seat 0, i.e. when this bandit is the one holding the vehicle's
        /// pose. A passenger sends packets too, but vanilla ignores their position.</summary>
        public bool IsDriver => IsSeated && _self.movement.getSeat() == DriverSeat;

        /// <summary>
        /// Keep the seat pointed at the nearest player. Set by /banditv gunner, and cleared when the
        /// bandit gets out. Harmless in a seat with no turret - the bandit just turns its head.
        /// </summary>
        public bool TrackNearestPlayer { get; set; }

        /// <summary>Where the bandit is driving to, if anywhere.</summary>
        public bool HasDestination => _navigator.HasDestination;

        public Vector3 Destination => _navigator.Destination;

        /// <summary>True while the vehicle is too wide for every way round the obstacle in front of
        /// it - the "it doesn't fit" case, as opposed to being merely slow.</summary>
        public bool IsBlocked => _navigator.IsBlocked;

        /// <summary>Latched when the drive was abandoned because the vehicle wedged. Read and
        /// cleared by whoever reports it.</summary>
        public bool ConsumeGaveUp() => _navigator.ConsumeGaveUp();

        public bool ConsumeArrived() => _navigator.ConsumeArrived();

        public float RemainingDriveDistance => _navigator.RemainingDistance;

        /// <summary>
        /// Seats the bandit in the driver seat of the nearest vehicle that has one free.
        ///
        /// The seat is named rather than taken: vanilla's ServerForcePassengerIntoVehicle picks the
        /// first free one, so ordering a bandit into the driver's seat of a vehicle whose driver
        /// seat is full would quietly put it in the back. See VehicleSeatTool.
        /// </summary>
        public bool TryDrive(out string reason) => TryEnter(DriverSeat, out reason);

        /// <summary>
        /// Seats the bandit in the F2 seat and leaves it tracking whoever is nearest. That seat is
        /// the gunner's in anything with a turret behind the driver; in something without one the
        /// bandit simply rides along and watches.
        /// </summary>
        public bool TryGun(out string reason)
        {
            if (!TryEnter(GunnerSeat, out reason))
            {
                return false;
            }

            TrackNearestPlayer = true;
            return true;
        }

        private bool TryEnter(byte seatIndex, out string reason)
        {
            if (_self == null || _self.life == null || _self.life.isDead)
            {
                reason = "the bandit is dead";
                return false;
            }

            if (IsSeated)
            {
                reason = $"it is already in {DescribeVehicle(Vehicle)}";
                return false;
            }

            InteractableVehicle vehicle = FindNearestWithFreeSeat(seatIndex, out reason);
            if (vehicle == null)
            {
                return false;
            }

            // Before seating, not after: vanilla decides whether the engine turns on at the moment a
            // driver sits down, and it only turns on with charge in the battery.
            TopUpConsumables(vehicle, force: true);

            if (!VehicleSeatTool.TrySeat(_self, vehicle, seatIndex, out string error))
            {
                reason = $"{DescribeVehicle(vehicle)} - {error}";
                return false;
            }

            _speed = 0f;
            _lookYaw = 0f;
            _lookPitch = 90f;
            _groundNormal = Vector3.up;
            _footprintMeasuredFor = null;
            _navigator.Stop();

            reason = DescribeVehicle(vehicle);
            return true;
        }

        /// <summary>
        /// Gets the bandit out wherever vanilla can find room for it. forceRemoveFromVehicle is the
        /// server-side exit that ignores whether a safe exit point exists, and it also covers a seat
        /// change ordered this same tick and not yet applied - which a plain "am I in a vehicle"
        /// test would miss.
        /// </summary>
        public bool TryExit(out string reason)
        {
            if (_self == null || _self.movement == null)
            {
                reason = "the bandit is gone";
                return false;
            }

            string was = IsSeated ? DescribeVehicle(Vehicle) : "a vehicle";

            if (!_self.movement.forceRemoveFromVehicle())
            {
                reason = "it is not in a vehicle";
                return false;
            }

            TrackNearestPlayer = false;
            _navigator.Stop();
            _speed = 0f;

            reason = was;
            return true;
        }

        /// <summary>
        /// Sends the vehicle to a point. Only the driver can be given one, and only in something
        /// that drives on the ground - see <see cref="WhyCannotDriveTo"/>.
        /// </summary>
        public bool TrySetDestination(Vector3 destination, out string reason)
        {
            InteractableVehicle vehicle = Vehicle;

            reason = WhyCannotDriveTo(vehicle);
            if (reason != null)
            {
                return false;
            }

            EnsureFootprint(vehicle);

            // A vehicle cannot put its centre on a point the way a person can stand on one. Arriving
            // is its nose being there, which is its own length away from its origin.
            _navigator.SetDestination(destination, Mathf.Max(4f, _footprint.HalfLength + 2f));
            reason = null;
            return true;
        }

        public void StopDriving()
        {
            _navigator.Stop();
        }

        /// <summary>
        /// Why this bandit cannot be given a destination, or null if it can.
        ///
        /// Boats and aircraft are refused rather than half-supported. Every step below snaps the
        /// vehicle onto the ground under it, which for a boat means the seabed and for a helicopter
        /// means the ground it is meant to be above. Holding station already works for all of them;
        /// it is only going somewhere that is wheels-only for now.
        /// </summary>
        private string WhyCannotDriveTo(InteractableVehicle vehicle)
        {
            if (vehicle == null)
            {
                return "the bandit is not in a vehicle";
            }

            if (!IsDriver)
            {
                return $"the bandit is a passenger in seat {_self.movement.getSeat()}, not the driver";
            }

            if (vehicle.asset == null)
            {
                return "the vehicle has no asset";
            }

            switch (vehicle.asset.engine)
            {
                case EEngine.CAR:
                    return null;
                case EEngine.BOAT:
                    return "boats are not supported yet - the drive step follows the ground, which under a boat is the seabed";
                case EEngine.TRAIN:
                    return "trains replicate a packed road position this doesn't build";
                default:
                    return $"{vehicle.asset.engine} is not supported yet - the drive step follows the ground";
            }
        }

        /// <summary>
        /// One packet's worth of being in a vehicle, whichever seat it is in.
        ///
        /// Returns the base type on purpose: which subclass this is depends on the seat, and the
        /// controller does not need to care - it enqueues whatever comes back.
        /// </summary>
        public PlayerInputPacket BuildPacket(uint frameNumber, float deltaTime)
        {
            InteractableVehicle vehicle = Vehicle;

            TopUpConsumables(vehicle, force: false);
            UpdateSeatAim(vehicle, deltaTime);

            return IsDriver
                ? BuildDrivingPacket(vehicle, frameNumber, deltaTime)
                : (PlayerInputPacket)BuildPassengerPacket(frameNumber);
        }

        /// <summary>
        /// The driver's packet: where the vehicle now is, and how fast it got there.
        ///
        /// With no destination this is pure hold-station - the vehicle's own transform echoed
        /// straight back with every motion value zeroed, which is a delta of nothing and cannot trip
        /// either validation gate. With one, the pose is stepped toward it first.
        /// </summary>
        private DrivingPlayerInputPacket BuildDrivingPacket(InteractableVehicle vehicle, uint frameNumber, float deltaTime)
        {
            Transform vehicleTransform = vehicle.transform;
            Vector3 position = vehicleTransform.position;
            Quaternion rotation = vehicleTransform.rotation;
            float speed = 0f;

            if (_navigator.HasDestination)
            {
                SyncCommandedPose(vehicle, position, rotation);
                position = _commandedPosition;
                rotation = _commandedRotation;

                Step(vehicle, deltaTime, ref position, ref rotation, out speed);

                _commandedPosition = position;
                _commandedRotation = rotation;
            }
            else
            {
                // Holding station reads the vehicle's own transform every packet instead, which is
                // self-correcting: whatever moved it, the reported pose is the one it is really in
                // and the delta stays nothing.
                _hasCommandedPose = false;
                _speed = 0f;
            }

            return new DrivingPlayerInputPacket(vehicle)
            {
                position = position,
                rotation = rotation,

                // Replicated for the benefit of watching clients - engine note, wheel spin, the
                // speedometer. None of it feeds back into where the server puts the vehicle.
                speed = speed,
                forwardVelocity = speed,
                steeringInput = 0f,
                velocityInput = speed,

                // A driver's look is seat-local and clamped to +/-160 degrees; zero and level is the
                // bandit facing out of the windscreen. Only a turret seat has anything to aim.
                yaw = 0f,
                pitch = 90f,

                keys = 0,
                primaryAttack = EAttackInputFlags.None,
                secondaryAttack = EAttackInputFlags.None,

                // Mirrors the bandit's own counter, which is what the recovery check compares
                // against - a packet carrying a stale one is discarded while a recovery runs.
                recov = _self.input.recov,
                clientSimulationFrameNumber = frameNumber
            };
        }

        /// <summary>
        /// A passenger's packet. Walking, not driving: it is the SITTING branch of the walking
        /// simulate that calls ServerUpdateTurretAim, and that is the only thing that tells every
        /// other client where this seat is pointing.
        /// </summary>
        private WalkingPlayerInputPacket BuildPassengerPacket(uint frameNumber)
        {
            return new WalkingPlayerInputPacket
            {
                analog = AnalogNeutral,
                clientPosition = _self.transform.position,
                yaw = _lookYaw,
                pitch = _lookPitch,
                keys = 0,
                primaryAttack = EAttackInputFlags.None,
                secondaryAttack = EAttackInputFlags.None,
                recov = _self.input.recov,
                clientSimulationFrameNumber = frameNumber
            };
        }

        /// <summary>
        /// Adopts the vehicle's real transform as the pose to step from whenever we have no
        /// remembered one, or the two have drifted further apart than a step can explain.
        ///
        /// That drift is not hypothetical: it is exactly what a rejected packet looks like. When the
        /// server starts a recovery it snaps the vehicle back to its last accepted position, and a
        /// bandit that kept stepping from where it *wished* the vehicle was would keep sending
        /// rejected packets from further and further away. Resyncing means one bad packet costs a
        /// stumble rather than the rest of the trip.
        /// </summary>
        private void SyncCommandedPose(InteractableVehicle vehicle, Vector3 actualPosition, Quaternion actualRotation)
        {
            float maxStep = MaxHorizontalStep(vehicle);
            float tolerance = Mathf.Max(1f, maxStep * 3f);

            if (!_hasCommandedPose || (actualPosition - _commandedPosition).sqrMagnitude > tolerance * tolerance)
            {
                _commandedPosition = actualPosition;
                _commandedRotation = actualRotation;
                _hasCommandedPose = true;
            }
        }

        /// <summary>
        /// The furthest the server will let a vehicle move between two packets, with the safety
        /// margin already taken off. asset.sqrDelta is that distance squared.
        /// </summary>
        private static float MaxHorizontalStep(InteractableVehicle vehicle)
        {
            float sqrDelta = vehicle.asset != null ? vehicle.asset.sqrDelta : 0.25f;
            return Mathf.Sqrt(Mathf.Max(sqrDelta, 0.01f)) * ValidationSafetyFactor;
        }

        /// <summary>
        /// Moves the pose one packet toward the destination: turn onto the heading the navigator
        /// picked, roll forward, sit down on the ground, and clamp both against what the server will
        /// accept.
        /// </summary>
        private void Step(InteractableVehicle vehicle, float deltaTime, ref Vector3 position, ref Quaternion rotation, out float speed)
        {
            EnsureFootprint(vehicle);
            _navigator.Tick(vehicle, _footprint, deltaTime);

            Transform vehicleTransform = vehicle.transform;

            // The yaw we last reported, not the one the body has settled into, for the same reason
            // the position comes from the commanded pose. See SyncCommandedPose.
            float yaw = rotation.eulerAngles.y;
            Vector3 heading = _navigator.DesiredDirection;

            float targetSpeed = 0f;
            if (heading.sqrMagnitude > 0.0001f)
            {
                float desiredYaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
                float headingError = Mathf.Abs(Mathf.DeltaAngle(yaw, desiredYaw));
                yaw = Mathf.MoveTowardsAngle(yaw, desiredYaw, HullTurnDegreesPerSecond * deltaTime);

                // Slow into the turn and stop altogether once the destination is behind it, so the
                // vehicle comes round onto the heading before it commits to it rather than driving a
                // long arc through whatever the sweep was avoiding.
                targetSpeed = CruiseSpeed(vehicle) * Mathf.Clamp01(1f - headingError / StopAndTurnDegrees);
            }

            _speed = Mathf.MoveTowards(_speed, targetSpeed, AccelerationMetresPerSecondSquared * deltaTime);
            speed = _speed;

            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 next = position + forward * (_speed * deltaTime);

            // Horizontal gate: asset.sqrDelta is the square of the furthest the server will accept a
            // vehicle moving between two packets.
            float maxStep = MaxHorizontalStep(vehicle);
            Vector3 step = next - position;
            step.y = 0f;
            if (step.magnitude > maxStep)
            {
                step = step.normalized * maxStep;
            }
            next = new Vector3(position.x + step.x, position.y, position.z + step.z);

            // Sit on whatever is under the new position, within the vertical speed the server allows.
            if (VehicleTerrain.TrySample(next, vehicleTransform, out Vector3 ground, out Vector3 groundNormal))
            {
                float maxRise = vehicle.asset.validSpeedUp * deltaTime * ValidationSafetyFactor;
                float maxFall = vehicle.asset.validSpeedDown * deltaTime * ValidationSafetyFactor;
                next.y = Mathf.Clamp(ground.y + _footprint.RideHeight, position.y - maxFall, position.y + maxRise);

                // Smoothed, because a wheel crossing a kerb flips the raw normal for one frame and
                // the vehicle would visibly jolt.
                float blend = 1f - Mathf.Exp(-deltaTime / 0.2f);
                _groundNormal = Vector3.Slerp(_groundNormal, groundNormal, blend).normalized;
            }
            else
            {
                next.y = position.y;
                _groundNormal = Vector3.Slerp(_groundNormal, Vector3.up, 1f - Mathf.Exp(-deltaTime / 0.2f)).normalized;
            }

            Vector3 levelForward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 alignedForward = Vector3.ProjectOnPlane(levelForward, _groundNormal);
            rotation = alignedForward.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(alignedForward.normalized, _groundNormal)
                : Quaternion.Euler(0f, yaw, 0f);

            position = next;
        }

        /// <summary>
        /// How fast to drive this particular vehicle. Half the asset's own top speed, bounded at both
        /// ends: fast enough to be worth watching, and comfortably inside the per-packet horizontal
        /// gate, which allows about 1.25 times the asset's top speed.
        /// </summary>
        private static float CruiseSpeed(InteractableVehicle vehicle)
        {
            // TargetForwardVelocity is the same number the sqrDelta gate is derived from
            // (sqrDelta == (TargetForwardVelocity * 0.1)^2 for a car), which is what ties the cruise
            // speed below to the limit above rather than to a guess.
            float assetSpeed = vehicle.asset != null ? vehicle.asset.TargetForwardVelocity : 0f;
            return Mathf.Clamp(assetSpeed * 0.5f, MinCruiseMetresPerSecond, MaxCruiseMetresPerSecond);
        }

        /// <summary>
        /// Points the seat at the nearest player, or lets it settle facing forward when there is
        /// nobody to watch.
        ///
        /// Deliberately no line-of-sight test, unlike target acquisition on foot. From inside a
        /// vehicle the first thing a ray hits is usually the vehicle, so a gunner that insisted on
        /// seeing its target would drop it the moment the hull came between them and snap back to
        /// centre - which reads as the tracking being broken rather than as the gunner being careful.
        ///
        /// The angles are seat-local, because that is what vanilla expects: PlayerLook assigns them
        /// to the seat's own local rotation and clamps them to the turret's yawMin/yawMax and
        /// pitchMin/pitchMax. A world-space aim would be folded onto a limit and sit there.
        /// </summary>
        private void UpdateSeatAim(InteractableVehicle vehicle, float deltaTime)
        {
            if (!TrackNearestPlayer || vehicle == null)
            {
                _lookYaw = Mathf.MoveTowardsAngle(_lookYaw, 0f, TurretTraverseDegreesPerSecond * deltaTime);
                _lookPitch = Mathf.MoveTowards(_lookPitch, 90f, TurretTraverseDegreesPerSecond * deltaTime);
                return;
            }

            Player target = FindNearestRealPlayer();
            if (target == null)
            {
                _lookYaw = Mathf.MoveTowardsAngle(_lookYaw, 0f, TurretTraverseDegreesPerSecond * deltaTime);
                _lookPitch = Mathf.MoveTowards(_lookPitch, 90f, TurretTraverseDegreesPerSecond * deltaTime);
                return;
            }

            Passenger seat = _self.movement.getVehicleSeat();
            Transform frame = seat != null && seat.seat != null ? seat.seat : vehicle.transform;

            Vector3 origin = _self.look != null && _self.look.aim != null
                ? _self.look.aim.position
                : _self.transform.position + Vector3.up * 1.5f;

            Vector3 toTarget = AimPointOf(target) - origin;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector3 local = frame.InverseTransformDirection(toTarget.normalized);
            float wantedYaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float wantedPitch = 90f - Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;

            _lookYaw = Mathf.MoveTowardsAngle(_lookYaw, wantedYaw, TurretTraverseDegreesPerSecond * deltaTime);
            _lookPitch = Mathf.MoveTowards(_lookPitch, wantedPitch, TurretTraverseDegreesPerSecond * deltaTime);
        }

        /// <summary>
        /// Keeps the vehicle fuelled and charged while a bandit is in it, if the configuration says
        /// so. Health is deliberately untouched: the vehicle is meant to be destructible.
        ///
        /// Both are replicated rather than only set, or the client keeps showing the gauge it last
        /// heard about - and the fuel one matters beyond the gauge, because vanilla tightens a car's
        /// anti-teleport delta to half a metre a packet once the tank is empty.
        /// </summary>
        private void TopUpConsumables(InteractableVehicle vehicle, bool force)
        {
            if (vehicle == null)
            {
                return;
            }

            if (!force && Time.time < _nextConsumableCheck)
            {
                return;
            }
            _nextConsumableCheck = Time.time + ConsumableCheckIntervalSeconds;

            BanditConfiguration config = BanditPlugin.Instance != null ? BanditPlugin.Instance.Configuration.Instance : null;
            if (config == null)
            {
                return;
            }

            if (config.VehicleInfiniteFuel && vehicle.usesFuel && vehicle.asset != null && vehicle.fuel < vehicle.asset.fuel)
            {
                vehicle.fuel = vehicle.asset.fuel;
                VehicleManager.sendVehicleFuel(vehicle, vehicle.fuel);
            }

            if (config.VehicleInfiniteBattery && vehicle.usesBattery && vehicle.batteryCharge < FullBatteryCharge)
            {
                vehicle.batteryCharge = FullBatteryCharge;
                VehicleManager.sendVehicleBatteryCharge(vehicle, vehicle.batteryCharge);
            }
        }

        private void EnsureFootprint(InteractableVehicle vehicle)
        {
            if (vehicle == null || _footprintMeasuredFor == vehicle)
            {
                return;
            }

            _footprint = BanditVehicleFootprint.Measure(vehicle);
            _footprintMeasuredFor = vehicle;
        }

        /// <summary>Where the bandit is sitting and what it is doing there, for /banditstatus.</summary>
        public string Describe()
        {
            InteractableVehicle vehicle = Vehicle;
            if (vehicle == null)
            {
                return "on foot";
            }

            string seat = IsDriver ? $"driving {DescribeVehicle(vehicle)}" : $"seat {_self.movement.getSeat()} of {DescribeVehicle(vehicle)}";

            if (TrackNearestPlayer)
            {
                seat += ", tracking";
            }

            if (_navigator.HasDestination)
            {
                seat += _navigator.IsBlocked
                    ? $", blocked {_navigator.RemainingDistance:0}m out"
                    : $", {_navigator.RemainingDistance:0}m to go" + (_navigator.IsFollowingPath ? " (A*)" : " (steering)");
            }

            return seat;
        }

        /// <summary>
        /// The size of the vehicle the bandit is in, for the command that reports whether it will
        /// fit. Measured from its colliders; see BanditVehicleFootprint.
        /// </summary>
        public BanditVehicleFootprint Footprint
        {
            get
            {
                EnsureFootprint(Vehicle);
                return _footprint;
            }
        }

        private InteractableVehicle FindNearestWithFreeSeat(byte seatIndex, out string reason)
        {
            Vector3 origin = _self.transform.position;
            float radiusSquared = SearchRadiusMetres * SearchRadiusMetres;

            InteractableVehicle nearest = null;
            float nearestDistanceSquared = radiusSquared;

            // The nearest one we had to turn down, kept only so the failure message can name it.
            // "No vehicle within 50m" when there is a truck ten metres away whose driver seat is
            // taken is the kind of answer that sends you looking in the wrong place.
            InteractableVehicle nearestRejected = null;
            float nearestRejectedDistanceSquared = radiusSquared;
            string rejection = null;

            List<InteractableVehicle> vehicles = VehicleManager.vehicles;
            for (int i = 0; vehicles != null && i < vehicles.Count; i++)
            {
                InteractableVehicle vehicle = vehicles[i];
                if (vehicle == null || vehicle.asset == null)
                {
                    continue;
                }

                float distanceSquared = (vehicle.transform.position - origin).sqrMagnitude;
                if (distanceSquared > radiusSquared)
                {
                    continue;
                }

                string why = WhySeatUnavailable(vehicle, seatIndex);
                if (why != null)
                {
                    if (distanceSquared < nearestRejectedDistanceSquared)
                    {
                        nearestRejectedDistanceSquared = distanceSquared;
                        nearestRejected = vehicle;
                        rejection = why;
                    }
                    continue;
                }

                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearest = vehicle;
                }
            }

            if (nearest != null)
            {
                reason = null;
                return nearest;
            }

            reason = nearestRejected != null
                ? $"nearest vehicle ({DescribeVehicle(nearestRejected)}) {rejection}"
                : $"no vehicle within {SearchRadiusMetres:0}m of the bandit";
            return null;
        }

        /// <summary>
        /// Why this vehicle's wanted seat cannot be used, or null if it can. The first three are
        /// vanilla's own preconditions from InteractableVehicle.tryAddPlayer, restated so the command
        /// can name the one that bit instead of reporting a flat failure.
        /// </summary>
        private static string WhySeatUnavailable(InteractableVehicle vehicle, byte seatIndex)
        {
            if (vehicle.isDead || vehicle.isExploded)
            {
                return "is wrecked";
            }

            if (vehicle.isDrowned)
            {
                return "is underwater";
            }

            if (!vehicle.isExitable)
            {
                // Checked when entering, not when leaving: vanilla refuses to seat anyone in a
                // vehicle it cannot find a safe exit point beside.
                return "has no safe exit point beside it";
            }

            Passenger[] seats = vehicle.passengers;
            if (seats == null || seatIndex >= seats.Length || seats[seatIndex] == null)
            {
                int count = seats == null ? 0 : seats.Length;
                return $"has no seat {seatIndex} (it has {count})";
            }

            if (seats[seatIndex].player != null)
            {
                return $"already has someone in seat {seatIndex}";
            }

            return null;
        }

        /// <summary>
        /// The nearest live player who is not one of ours, at any angle and through anything. Used
        /// only for pointing a seat; nothing shoots off the back of it.
        /// </summary>
        private Player FindNearestRealPlayer()
        {
            Player nearest = null;
            float nearestDistanceSquared = TurretTrackRangeMetres * TurretTrackRangeMetres;
            Vector3 origin = _self.transform.position;

            foreach (SteamPlayer steamPlayer in Provider.clients)
            {
                Player candidate = steamPlayer.player;
                if (candidate == null || candidate == _self || candidate.life == null || candidate.life.isDead)
                {
                    continue;
                }

                if (candidate.GetComponent<BanditBotController>() != null)
                {
                    continue; // bandits don't watch each other
                }

                float distanceSquared = (candidate.transform.position - origin).sqrMagnitude;
                if (distanceSquared < nearestDistanceSquared)
                {
                    nearestDistanceSquared = distanceSquared;
                    nearest = candidate;
                }
            }

            return nearest;
        }

        /// <summary>The chest, taken as a fraction of the way from a player's feet to their eyes, so
        /// it follows their stance the same way the on-foot aim point does.</summary>
        private static Vector3 AimPointOf(Player player)
        {
            Vector3 eye = player.look != null && player.look.aim != null
                ? player.look.aim.position
                : player.transform.position + Vector3.up * 1.75f;

            return Vector3.Lerp(player.transform.position, eye, 0.7f);
        }

        private static string DescribeVehicle(InteractableVehicle vehicle)
        {
            if (vehicle == null)
            {
                return "a vehicle";
            }

            return vehicle.asset != null ? vehicle.asset.FriendlyName : "an unnamed vehicle";
        }
    }
}
