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

        /// <summary>
        /// Past this the vehicle turns around rather than reversing. Backing up is for getting out
        /// of somewhere and for short repositioning; nobody reverses two hundred metres.
        /// </summary>
        private const float ReverseHopMetres = 18f;

        /// <summary>Heading error at which the destination counts as being behind the vehicle.</summary>
        private const float ReverseBehindDegrees = 110f;

        /// <summary>Heading error at which it is worth going back into forward gear.</summary>
        private const float ResumeForwardDegrees = 70f;

        private const float UnstickReverseSeconds = 1.6f;
        private const int MaxUnstickAttempts = 3;

        /// <summary>
        /// How long the vehicle may fail to get closer to where it was sent before it counts as
        /// stuck. This is the whole definition: not "is something in front of me" - the navigator
        /// already sweeps for that and drives round it - but "I was supposed to be making progress
        /// and I am not". A vehicle grinding along a wall is moving and getting nowhere; a vehicle
        /// waiting to finish a turn is still and perfectly fine.
        /// </summary>
        private const float NoProgressSeconds = 2.5f;

        /// <summary>How much closer counts as progress rather than noise.</summary>
        private const float ProgressMetres = 0.75f;

        /// <summary>
        /// A route that suddenly gets this much longer is a new route, not a failure - repathing
        /// round the far side of a building legitimately lengthens the trip - so the yardstick is
        /// re-taken instead of counting it as being stuck.
        /// </summary>
        private const float RepathJumpMetres = 15f;

        /// <summary>How long the direction that failed stays banned after a reverse.</summary>
        private const float BanFailedDirectionSeconds = 7f;

        /// <summary>How long the trigger stays down once a turret opens up, and how long it waits
        /// afterwards. A latched trigger fires at the gun's own rate; re-pulling cannot, because
        /// vanilla sets equipment.isBusy for 150ms on every shot.</summary>
        private const float TurretBurstSeconds = 1.2f;

        private const float TurretBurstPauseSeconds = 1.4f;

        /// <summary>How near an explosive round has to land to count as a hit. Deliberately
        /// conservative next to a real blast radius - close enough to be worth firing, not so
        /// generous that a rocket turret shoots at anything vaguely in front of it.</summary>
        private const float BlastCloseEnoughMetres = 2.5f;

        /// <summary>Extra width either side of the vehicle that a squadmate is cleared out of.</summary>
        private const float SquadClearanceMarginMetres = 1.4f;

        /// <summary>How long a squadmate keeps running once told to move. Short on purpose - it is
        /// re-issued every packet while they are still in the lane.</summary>
        private const float EvadeHoldSeconds = 1.2f;

        private readonly BanditBotController _controller;
        private readonly Player _self;
        private readonly BanditVehicleNavigator _navigator;

        private BanditVehicleFootprint _footprint = BanditVehicleFootprint.Default;
        private InteractableVehicle _footprintMeasuredFor;

        private float _speed;
        private Vector3 _groundNormal = Vector3.up;

        // How high this vehicle's origin sits above the ground when it is resting on it, measured
        // from the vehicle itself at the moment it is given somewhere to go. See CalibrateRideHeight.
        private float _rideHeight;

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

        // Reverse gear. _reversing is sticky between packets so the vehicle does not flip gear every
        // time the heading error crosses a threshold; _unstick is the separate, forced reverse that
        // backs it out of something it has wedged itself against.
        private bool _reversing;
        private float _unstickUntil;
        private int _unstickAttempts;

        // Progress toward the destination, which is what "stuck" is measured against.
        private float _bestRemaining = float.MaxValue;
        private float _lastProgressTime;

        // The heading that was being driven when progress stopped, so the reverse can ban it.
        private Vector3 _lastTravelDirection = Vector3.forward;

        // Turret trigger. Latched down through a burst rather than pulsed, because vanilla's
        // isBusy gate caps a re-pulled trigger at about four rounds a second whatever the gun is.
        private bool _triggerLatched;
        private float _burstEndsAt;
        private float _nextBurstTime;

        private Player _lookTarget;

        public BanditVehicleDriver(BanditBotController controller, Player self)
        {
            _controller = controller;
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

        /// <summary>
        /// Scales the speed this vehicle drives at, between crawling and its usual cruise. One is
        /// normal and is what everything that does not set it gets.
        ///
        /// It exists for the two things a convoy has to do that a single vehicle never did: hold
        /// its interval behind the vehicle in front, and keep rolling while it fights instead of
        /// either stopping dead or driving off and leaving its infantry behind. Scaling the speed
        /// is the whole of it - the heading, the clearance sweep and the validation clamps are
        /// unchanged, so a slowed vehicle drives exactly as it otherwise would, just slower.
        ///
        /// Zero is a legitimate value and means "stay put with the engine running", which is not
        /// the same as <see cref="StopDriving"/>: the destination survives, so it moves off again
        /// the moment the scale comes back up.
        /// </summary>
        public float SpeedScale { get; set; } = 1f;

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
        /// Seats the bandit in a gun seat and leaves it tracking whoever is nearest.
        ///
        /// Seat 1 is F2, seat 2 is F3, and so on - the same numbering a player's own seat keys use,
        /// which is what makes "gunner2" mean the thing you get by pressing F3. A vehicle with
        /// several turrets simply has several of these; in a seat with no turret at all the bandit
        /// rides along and watches, which is harmless and occasionally useful.
        /// </summary>
        public bool TryGun(byte seatIndex, out string reason)
        {
            if (!TryEnter(seatIndex, out reason))
            {
                return false;
            }

            TrackNearestPlayer = true;
            return true;
        }

        /// <summary>
        /// Seats the bandit in a named seat of a *named* vehicle, rather than of whichever one
        /// happens to be nearest.
        ///
        /// Needed the moment anything spawns a vehicle and wants that vehicle crewed: an event that
        /// puts down two trucks thirty metres apart and then fills them by proximity will happily
        /// put both drivers in the same one. The nearest-vehicle search is a convenience for a
        /// person typing a command; code that already knows which vehicle it means should say so.
        /// </summary>
        public bool TryEnter(InteractableVehicle vehicle, byte seatIndex, out string reason)
        {
            if (vehicle == null)
            {
                reason = "the vehicle is gone";
                return false;
            }

            return TryEnter(seatIndex, vehicle, out reason);
        }

        private bool TryEnter(byte seatIndex, out string reason) => TryEnter(seatIndex, null, out reason);

        /// <param name="target">
        /// The vehicle to get into, or null to take the nearest one with that seat free.
        /// </param>
        private bool TryEnter(byte seatIndex, InteractableVehicle target, out string reason)
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

            InteractableVehicle vehicle = target;
            if (vehicle == null)
            {
                vehicle = FindNearestWithFreeSeat(seatIndex, out reason);
                if (vehicle == null)
                {
                    return false;
                }
            }
            else
            {
                // A named vehicle goes through the same preconditions the search applies to every
                // candidate, or the caller learns its truck was wrecked by getting a bare failure
                // out of the seat RPC instead of a sentence saying so.
                string why = WhySeatUnavailable(vehicle, seatIndex);
                if (why != null)
                {
                    reason = $"{DescribeVehicle(vehicle)} {why}";
                    return false;
                }
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
            CalibrateRideHeight(vehicle);

            // A vehicle cannot put its centre on a point the way a person can stand on one. Arriving
            // is its nose being there, which is its own length away from its origin.
            _navigator.SetDestination(destination, Mathf.Max(4f, _footprint.HalfLength + 2f));

            // A fresh trip gets a fresh budget of attempts to back out of something, and a stall
            // sample taken from where it actually is rather than wherever the last trip ended.
            _unstickAttempts = 0;
            _unstickUntil = 0f;
            _reversing = false;
            _bestRemaining = float.MaxValue;
            _lastProgressTime = Time.time;

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

            // The turret's magazine and its reload, on the same per-packet footing as on foot - a
            // cannon that takes six seconds to reload has to spend them, whatever the burst timer
            // would otherwise allow.
            _controller.TickAmmo();

            TopUpConsumables(vehicle, force: false);
            UpdateSeatAim(vehicle, deltaTime);

            return IsDriver
                ? BuildDrivingPacket(vehicle, frameNumber, deltaTime)
                : (PlayerInputPacket)BuildPassengerPacket(vehicle, frameNumber);
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
            float forwardVelocity = 0f;

            if (_navigator.HasDestination)
            {
                SyncCommandedPose(vehicle, position, rotation);
                position = _commandedPosition;
                rotation = _commandedRotation;

                Step(vehicle, deltaTime, ref position, ref rotation, out forwardVelocity);

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
                _reversing = false;
            }

            return new DrivingPlayerInputPacket(vehicle)
            {
                position = position,
                rotation = rotation,

                // Replicated for the benefit of watching clients - engine note, wheel spin, the
                // speedometer. None of it feeds back into where the server puts the vehicle.
                //
                // speed is written as an *unsigned* clamped float by the packet's own serialiser, so
                // it carries the magnitude and forwardVelocity carries the sign. Reversing therefore
                // shows up as negative forward velocity, exactly as it does from a real client.
                speed = Mathf.Abs(forwardVelocity),
                forwardVelocity = forwardVelocity,
                steeringInput = 0f,
                velocityInput = forwardVelocity,

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
        private WalkingPlayerInputPacket BuildPassengerPacket(InteractableVehicle vehicle, uint frameNumber)
        {
            EAttackInputFlags primaryAttack = DecideTurretFire(vehicle, out int hitReports,
                out Vector3 muzzleOrigin, out Vector3 muzzleDirection, out float range);

            WalkingPlayerInputPacket packet = BuildPassengerPacket(frameNumber, primaryAttack);

            // Only for hitscan turrets. The server raycasts nothing itself - UseableGun.ballistics
            // applies damage from reports the owning client sends, and with no client every round is
            // discarded. Projectile turrets are the opposite: fire() spawns the rocket server-side
            // along the seat's own aim, so a report would be ignored.
            if (hitReports > 0)
            {
                _controller.AttachHitReports(packet, hitReports, muzzleOrigin, muzzleDirection, range);
            }

            return packet;
        }

        private WalkingPlayerInputPacket BuildPassengerPacket(uint frameNumber, EAttackInputFlags primaryAttack)
        {
            return new WalkingPlayerInputPacket
            {
                analog = AnalogNeutral,
                clientPosition = _self.transform.position,
                yaw = _lookYaw,
                pitch = _lookPitch,
                keys = 0,
                primaryAttack = primaryAttack,
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
        private void Step(InteractableVehicle vehicle, float deltaTime, ref Vector3 position, ref Quaternion rotation, out float forwardVelocity)
        {
            EnsureFootprint(vehicle);
            _navigator.Tick(vehicle, _footprint, deltaTime);

            Transform vehicleTransform = vehicle.transform;

            // The yaw we last reported, not the one the body has settled into, for the same reason
            // the position comes from the commanded pose. See SyncCommandedPose.
            float yaw = rotation.eulerAngles.y;

            UpdateProgress();

            Vector3 travel = ResolveTravelDirection(vehicle, yaw, out bool reverse);

            float targetSpeed = 0f;
            if (travel.sqrMagnitude > 0.0001f)
            {
                // Which way the *nose* has to point to travel that way. Backing up is the same
                // heading with the vehicle turned round, which is the whole of reverse gear here.
                float travelYaw = Mathf.Atan2(travel.x, travel.z) * Mathf.Rad2Deg;
                float noseYaw = reverse ? travelYaw + 180f : travelYaw;

                float headingError = Mathf.Abs(Mathf.DeltaAngle(yaw, noseYaw));
                yaw = Mathf.MoveTowardsAngle(yaw, noseYaw, HullTurnDegreesPerSecond * deltaTime);

                // Slow into the turn and stop altogether once the heading is behind it, so the
                // vehicle comes round onto it before committing rather than driving a long arc
                // through whatever the sweep was avoiding.
                float cruise = (reverse ? ReverseSpeed(vehicle) : CruiseSpeed(vehicle)) * Mathf.Clamp01(SpeedScale);
                targetSpeed = cruise * Mathf.Clamp01(1f - headingError / StopAndTurnDegrees);
            }

            _speed = Mathf.MoveTowards(_speed, targetSpeed, AccelerationMetresPerSecondSquared * deltaTime);
            forwardVelocity = reverse ? -_speed : _speed;

            Vector3 forward = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            Vector3 travelDirection = reverse ? -forward : forward;

            // Remembered for the ban a stall issues: the direction that stopped working is the one
            // being driven when it stopped, not the one the navigator wants next.
            if (!reverse)
            {
                _lastTravelDirection = travelDirection;
            }

            // Before the step is taken, so a squadmate in the way has the whole of it to get clear.
            ClearSquadmatesFromPath(vehicle, position, travelDirection, _speed);

            Vector3 next = position + travelDirection * (_speed * deltaTime);

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
                next.y = Mathf.Clamp(ground.y + _rideHeight, position.y - maxFall, position.y + maxRise);

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
        /// Which way to travel this packet, and whether to do it tail-first.
        ///
        /// Two reasons to reverse, and they are not the same thing. One is that the place we are
        /// going is behind us and close - swinging a lorry round to cover ten metres takes longer
        /// than backing up, and looks wrong besides. The other is that we are wedged: forward has
        /// stopped producing movement, so back out and let the avoidance fan try again from a
        /// different spot.
        ///
        /// The clearance sweep does not care which way the vehicle faces - it sweeps a world
        /// direction through a box the width of the body - so a heading the navigator already
        /// approved is as safe to reverse along as to drive along. Only the wedged case picks its
        /// own direction, and that one is swept here.
        /// </summary>
        private Vector3 ResolveTravelDirection(InteractableVehicle vehicle, float yaw, out bool reverse)
        {
            reverse = false;

            if (Time.time < _unstickUntil)
            {
                // Overlap is forgiven for this one sweep: the vehicle is very likely already
                // touching whatever it failed to get past, and refusing to reverse out of something
                // it is touching is refusing the one manoeuvre that can free it.
                Vector3 back = -(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward);
                if (_navigator.IsTravelClear(vehicle, _footprint, back, ignoreOverlap: true))
                {
                    reverse = true;
                    return back;
                }

                // Boxed in behind as well. Nothing to do but stop and let the navigator's own
                // give-up timer end the trip, which is what reports it.
                _unstickUntil = 0f;
            }

            Vector3 heading = _navigator.DesiredDirection;
            if (heading.sqrMagnitude < 0.0001f)
            {
                _reversing = false;
                return Vector3.zero;
            }

            float travelYaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;
            float forwardError = Mathf.Abs(Mathf.DeltaAngle(yaw, travelYaw));
            float remaining = _navigator.RemainingDistance;

            if (_reversing)
            {
                // Sticky, or the vehicle changes its mind about which gear it is in every time the
                // heading error crosses the threshold and never actually goes anywhere.
                if (forwardError < ResumeForwardDegrees || remaining > ReverseHopMetres * 1.5f)
                {
                    _reversing = false;
                }
            }
            else if (forwardError > ReverseBehindDegrees && remaining <= ReverseHopMetres)
            {
                _reversing = true;
            }

            reverse = _reversing;
            return heading;
        }

        /// <summary>
        /// Stuck means one thing: the vehicle was supposed to be getting closer to where it was sent
        /// and, for a couple of seconds, hasn't.
        ///
        /// Deliberately not "is there something in front of me". The navigator already sweeps for
        /// that and drives round it, and most of what it finds is not a problem at all - a wall it
        /// steers past is a fact about the route, not a failure of it. Distance to the destination
        /// is the only measure that catches every real case: grinding along a fence at full speed,
        /// circling a rock the fan keeps deflecting off, or sitting still because nothing fits. All
        /// three look identical from here, and all three want the same answer.
        ///
        /// That answer is: back out, and ban the direction that failed so the repath goes a
        /// different way rather than taking another run at the same gap. Bounded to a few attempts,
        /// because a vehicle that backs up, drives into the same rock and backs up again would do it
        /// forever - and reversing counts as movement, so nothing else would ever end the trip.
        /// </summary>
        private void UpdateProgress()
        {
            float remaining = _navigator.RemainingDistance;

            if (remaining < _bestRemaining - ProgressMetres)
            {
                _bestRemaining = remaining;
                _lastProgressTime = Time.time;
                _unstickAttempts = 0; // real progress refills the budget
                return;
            }

            if (remaining > _bestRemaining + RepathJumpMetres)
            {
                // A different, longer route - measure against that from now on rather than against
                // a best that belonged to a route we are no longer on.
                _bestRemaining = remaining;
                _lastProgressTime = Time.time;
                return;
            }

            if (Time.time - _lastProgressTime < NoProgressSeconds || Time.time < _unstickUntil)
            {
                return;
            }

            if (_unstickAttempts >= MaxUnstickAttempts)
            {
                _navigator.GiveUp();
                return;
            }

            _unstickAttempts++;
            _unstickUntil = Time.time + UnstickReverseSeconds;
            _lastProgressTime = Time.time; // the reverse gets its own window before it is judged
            _bestRemaining = remaining;

            _navigator.BanDirection(_lastTravelDirection, BanFailedDirectionSeconds);
        }

        /// <summary>
        /// Tells any squadmate standing in the lane the vehicle is about to drive through to get out
        /// of it.
        ///
        /// The vehicle does not brake and does not steer round them - a bandit is not in any of the
        /// masks the width sweep uses, and giving way to your own infantry would mean a vehicle that
        /// can never move through its own squad. The bandit moves instead, and the order it is given
        /// outranks everything it might be doing, cover included. See BanditBrain.OrderEvade.
        ///
        /// The lane is the vehicle's own width plus a margin, running from its nose to a couple of
        /// seconds' driving ahead - so the faster it goes, the earlier its squad scatters. Squadmates
        /// riding in a vehicle are skipped, being in no danger from it, and so is anyone on a
        /// different level: a bandit on a roof above the lane is not about to be run over.
        /// </summary>
        private void ClearSquadmatesFromPath(InteractableVehicle vehicle, Vector3 position, Vector3 travelDirection, float speed)
        {
            if (speed < 0.5f || travelDirection.sqrMagnitude < 0.0001f)
            {
                return; // parked, or turning on the spot
            }

            Vector3 forward = new Vector3(travelDirection.x, 0f, travelDirection.z);
            if (forward.sqrMagnitude < 0.0001f)
            {
                return;
            }
            forward.Normalize();

            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float lookahead = _footprint.HalfLength + Mathf.Max(6f, speed * 2f);
            float halfWidth = _footprint.HalfWidth + SquadClearanceMarginMetres;

            List<BanditBotController> bandits = BanditBotController.LiveBandits;
            for (int i = 0; i < bandits.Count; i++)
            {
                BanditBotController other = bandits[i];
                if (other == null || other == _controller || other.Brain == null)
                {
                    continue;
                }

                // One of ours: the same squad, or - since teams exist - anyone else on our side,
                // because a driver that swerves for its own section and flattens the section next
                // to it is not steering around friends, it is steering around five of them. Two
                // bandits spawned loose are on the same team as well as in the same null squad,
                // which is what you want when testing with /bandit rather than /squadspawn.
                //
                // An enemy team's bandit is deliberately not in this pool. It gets run over.
                if (other.Squad != _controller.Squad
                    && _controller.IsHostileTo(other.Self, otherIsBandit: true))
                {
                    continue;
                }

                if (other.Self == null || other.Self.life == null || other.Self.life.isDead)
                {
                    continue;
                }

                if (other.Driver != null && other.Driver.IsSeated)
                {
                    continue; // riding in something, including possibly this
                }

                Vector3 offset = other.Self.transform.position - position;
                if (Mathf.Abs(offset.y) > 3f)
                {
                    continue; // different floor, bridge or rooftop
                }

                offset.y = 0f;
                float along = Vector3.Dot(offset, forward);
                if (along < -_footprint.HalfLength || along > lookahead)
                {
                    continue; // behind the vehicle, or too far ahead to matter yet
                }

                float side = Vector3.Dot(offset, right);
                if (Mathf.Abs(side) > halfWidth)
                {
                    continue; // beside the lane, not in it
                }

                // Out the nearer way. Dead centre picks a side rather than dithering between two
                // equally good ones while the bumper arrives.
                float sign = side >= 0f ? 1f : -1f;
                other.Brain.OrderEvade(right * sign, EvadeHoldSeconds);
            }
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
        /// Reverse is slower than forward, as it is in anything with wheels - and it wants to be
        /// slow here for a second reason: backing up is either a short hop or an attempt to get
        /// unwedged, and neither is helped by carrying speed into whatever is behind.
        /// </summary>
        private static float ReverseSpeed(InteractableVehicle vehicle)
        {
            float assetReverse = vehicle.asset != null ? vehicle.asset.TargetReverseSpeed : 0f;
            return Mathf.Clamp(assetReverse * 0.5f, 2f, 6f);
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
        /// The angles are local, because that is what vanilla expects - but local to *what* is the
        /// whole difficulty; see <see cref="ResolveAimFrame"/>.
        /// </summary>
        private void UpdateSeatAim(InteractableVehicle vehicle, float deltaTime)
        {
            _lookTarget = TrackNearestPlayer && vehicle != null ? FindNearestEnemy() : null;

            if (_lookTarget == null)
            {
                _lookYaw = Mathf.MoveTowardsAngle(_lookYaw, 0f, TurretTraverseDegreesPerSecond * deltaTime);
                _lookPitch = Mathf.MoveTowards(_lookPitch, 90f, TurretTraverseDegreesPerSecond * deltaTime);
                return;
            }

            Player target = _lookTarget;
            Passenger seat = _self.movement.getVehicleSeat();
            Quaternion frame = ResolveAimFrame(vehicle, seat);

            Vector3 origin = _self.look != null && _self.look.aim != null
                ? _self.look.aim.position
                : _self.transform.position + Vector3.up * 1.5f;

            Vector3 toTarget = AimPointOf(target) - origin;
            if (toTarget.sqrMagnitude < 0.0001f)
            {
                return;
            }

            Vector3 local = Quaternion.Inverse(frame) * toTarget.normalized;
            float wantedYaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            float wantedPitch = 90f - Mathf.Asin(Mathf.Clamp(local.y, -1f, 1f)) * Mathf.Rad2Deg;

            _lookYaw = Mathf.MoveTowardsAngle(_lookYaw, wantedYaw, TurretTraverseDegreesPerSecond * deltaTime);
            _lookPitch = Mathf.MoveTowards(_lookPitch, wantedPitch, TurretTraverseDegreesPerSecond * deltaTime);

            // Held inside the turret's own traverse limits rather than chasing an angle it cannot
            // reach. Vanilla clamps these anyway, but doing it here keeps the angles we *think* the
            // gun is at equal to the ones it really is - which is what the shot below is aimed along.
            if (seat != null && seat.turret != null)
            {
                _lookYaw = Mathf.Clamp(_lookYaw, seat.turret.yawMin, seat.turret.yawMax);
                _lookPitch = Mathf.Clamp(_lookPitch, seat.turret.pitchMin, seat.turret.pitchMax);
            }
        }

        /// <summary>
        /// The frame the seat's yaw and pitch are measured in - which is not the seat.
        ///
        /// PlayerLook.simulate rotates the gun itself with
        ///     turretYaw.localRotation = rotationYaw * Euler(0, yaw, 0)
        /// so the barrel's zero is the yaw pivot's parent times the base rotation the pivot was
        /// built with. The player's own aim transform, meanwhile, hangs off the *seat*. Those two
        /// agree only while the seat happens to face the same way as the turret mount, which is true
        /// of a hull gun and false of most second and third turrets - and where they disagree, a
        /// bandit solving its angles in seat space pointed the barrel somewhere else entirely while
        /// its rounds still went to the target. Gun facing away, bullets landing: exactly the bug.
        ///
        /// Solving in the turret's own frame instead makes the barrel point at the target, and the
        /// shot is traced along the same frame, so what is aimed and what is fired agree.
        ///
        /// The one exception is a projectile turret without an aim camera. Vanilla spawns rockets
        /// along player.look.aim.forward, and with useAimCamera off that transform stays in seat
        /// space whatever the barrel does - so for those the seat frame is what the round will
        /// actually follow, and matching it is what makes the rocket land on the target.
        /// </summary>
        private Quaternion ResolveAimFrame(InteractableVehicle vehicle, Passenger seat)
        {
            if (seat == null)
            {
                return vehicle != null ? vehicle.transform.rotation : Quaternion.identity;
            }

            Quaternion seatFrame = seat.seat != null
                ? seat.seat.rotation
                : (vehicle != null ? vehicle.transform.rotation : Quaternion.identity);

            if (seat.turret == null || seat.turretYaw == null)
            {
                return seatFrame; // no turret to disagree with
            }

            if (!seat.turret.useAimCamera && _self.equipment != null
                && _self.equipment.asset is ItemGunAsset gun && gun.projectile != null)
            {
                return seatFrame; // the rocket follows the seat, so aim the seat
            }

            Transform pivotParent = seat.turretYaw.parent;
            Quaternion parentRotation = pivotParent != null ? pivotParent.rotation : Quaternion.identity;
            return parentRotation * seat.rotationYaw;
        }

        /// <summary>
        /// Whether to pull the turret's trigger this packet, and along what line.
        ///
        /// The line has to be worked out here rather than read off player.look.aim, for the same
        /// reason the on-foot bandit builds its own: the packet has not been simulated yet, so the
        /// aim transform still holds the previous one. These are the angles vanilla is about to be
        /// given, converted out of seat space, so the round that gets reported is the round that is
        /// really fired.
        /// </summary>
        private EAttackInputFlags DecideTurretFire(InteractableVehicle vehicle, out int hitReports,
            out Vector3 muzzleOrigin, out Vector3 muzzleDirection, out float range)
        {
            hitReports = 0;
            muzzleOrigin = Vector3.zero;
            muzzleDirection = Vector3.forward;
            range = 0f;

            ItemGunAsset gun = CanFireTurret(vehicle, out Vector3 origin, out Vector3 direction, out Vector3 aimPoint)
                ? _self.equipment.asset as ItemGunAsset
                : null;

            if (gun == null)
            {
                // Includes being told to hold fire mid-burst, which has to send the release or the
                // trigger stays down.
                if (_triggerLatched)
                {
                    _triggerLatched = false;
                    _nextBurstTime = Time.time + TurretBurstPauseSeconds;
                    return EAttackInputFlags.Stop;
                }
                return EAttackInputFlags.None;
            }

            muzzleOrigin = origin;
            muzzleDirection = direction;
            range = Mathf.Max(gun.range, 1f);

            EAttackInputFlags input;
            if (_triggerLatched)
            {
                if (Time.time >= _burstEndsAt)
                {
                    _triggerLatched = false;
                    _nextBurstTime = Time.time + TurretBurstPauseSeconds;
                    return EAttackInputFlags.Stop;
                }

                // Nothing to send: the Start that opened the burst is still latched, and vanilla
                // ignores a repeat. The hit reports below still have to come every packet.
                input = EAttackInputFlags.None;
            }
            else
            {
                if (Time.time < _nextBurstTime)
                {
                    return EAttackInputFlags.None;
                }

                _triggerLatched = true;
                _burstEndsAt = Time.time + TurretBurstSeconds;
                input = EAttackInputFlags.Start;
            }

            if (gun.projectile == null)
            {
                hitReports = PlanHitReportCount(gun);
            }

            return input;
        }

        /// <summary>
        /// Everything that has to hold before a turret may fire: an order to shoot, a turret to shoot
        /// with, something to shoot at, and a line to it that a round can actually travel.
        /// </summary>
        private bool CanFireTurret(InteractableVehicle vehicle, out Vector3 origin, out Vector3 direction, out Vector3 aimPoint)
        {
            origin = Vector3.zero;
            direction = Vector3.forward;
            aimPoint = Vector3.zero;

            // The same standing order the bandit fights under on foot, so /banditstop and
            // /banditshoot mean what they say from a seat too - and a freshly spawned bandit holds
            // its fire in a turret exactly as it does on the ground.
            if (vehicle == null || _controller == null || _controller.HoldFire || !TrackNearestPlayer)
            {
                return false;
            }

            if (vehicle.isDead || !vehicle.canUseTurret)
            {
                return false;
            }

            Passenger seat = _self.movement.getVehicleSeat();
            if (seat == null || seat.turret == null || seat.seat == null)
            {
                return false; // a passenger seat with no gun in it
            }

            // Must come before any trigger input. PlayerEquipment routes primary attacks to
            // simulate_PunchInput while there is no valid useable, so firing during the equip
            // animation makes the bandit throw punches from the gunner's seat.
            if (!_controller.IsGunReady())
            {
                return false;
            }

            // Waiting out the gun's reload. This is the one that matters most on a vehicle: a tank
            // cannon's Reload_Time dwarfs any burst timing, and without it the turret fires several
            // times faster than the gun can manage.
            if (_controller.IsReloading)
            {
                return false;
            }

            Player target = _lookTarget;
            if (target == null || target.life == null || target.life.isDead)
            {
                return false;
            }

            ItemGunAsset gun = _self.equipment.asset as ItemGunAsset;
            if (gun == null)
            {
                return false;
            }

            // The turret's own muzzle where the vehicle has one, rather than the bandit's head. On
            // a tank the head is inside the hull, and a shot traced from in there hits its own
            // armour before it has gone anywhere.
            origin = seat.turretAim != null
                ? seat.turretAim.position
                : (_self.look != null && _self.look.aim != null
                    ? _self.look.aim.position
                    : _self.transform.position + Vector3.up * 1.5f);

            // The same frame the angles were solved in, so the traced shot is the one the barrel is
            // actually pointing along. See ResolveAimFrame.
            Vector3 localDirection = Quaternion.Euler(_lookPitch - 90f, _lookYaw, 0f) * Vector3.forward;
            direction = (ResolveAimFrame(vehicle, seat) * localDirection).normalized;

            aimPoint = AimPointOf(target);
            Vector3 toTarget = aimPoint - origin;
            float distance = toTarget.magnitude;

            if (distance > Mathf.Max(gun.range, 1f))
            {
                return false;
            }

            // Cheap reject before the raycast, and nothing more than that. It cannot be the test
            // that decides whether to fire: ten degrees is three and a half metres of miss at
            // thirty, which is exactly how a turret ends up hosing rounds over someone's head.
            if (Vector3.Angle(direction, toTarget.normalized) > _controller.AimToleranceDegrees)
            {
                return false;
            }

            if (IsSquadmateInLineOfFire(origin, toTarget.normalized, distance))
            {
                return false;
            }

            return WouldRoundConnect(vehicle, gun, origin, direction, target, aimPoint);
        }

        /// <summary>
        /// Whether the round this packet would fire actually arrives, rather than merely leaving in
        /// roughly the right direction.
        ///
        /// This exists because a turret cannot point wherever it likes. Vanilla clamps a seat's aim
        /// to the turret's own pitchMin/pitchMax, so a gun that cannot depress far enough sits at
        /// its limit with the target comfortably inside any angular tolerance and every round
        /// sailing over their head. The only honest test is the one the damage uses: trace the shot
        /// and see what it meets.
        ///
        /// It is the same ray AttachHitReports will trace, so passing here also guarantees the
        /// report that follows is a hit on the target rather than on the bandit's own vehicle.
        /// </summary>
        private bool WouldRoundConnect(InteractableVehicle vehicle, ItemGunAsset gun, Vector3 origin,
            Vector3 direction, Player target, Vector3 aimPoint)
        {
            RaycastInfo info = DamageTool.raycast(new Ray(origin, direction), Mathf.Max(gun.range, 1f),
                RayMasks.DAMAGE_CLIENT, _self);

            if (info.transform == null)
            {
                return false; // into the sky
            }

            if (vehicle != null && info.transform.IsChildOf(vehicle.transform))
            {
                return false; // its own hull
            }

            // Someone behind a tree, a fence or a parked car is not safe from a turret - the turret
            // shoots the tree. Unconditional here rather than gated on the kit the way the on-foot
            // path is: a vehicle gun can always do something about breakable cover, and waiting for
            // the target to step out is the one thing a tank should never do.
            if (_controller.IsBreakableCover(info, aimPoint))
            {
                return true;
            }

            if (info.player == target)
            {
                return true;
            }

            // An explosive round does not have to hit the man. Anything it lands close enough to
            // will do, which is what lets a rocket turret shoot at someone behind a low wall.
            return gun.projectile != null
                && (info.point - aimPoint).sqrMagnitude <= BlastCloseEnoughMetres * BlastCloseEnoughMetres;
        }

        /// <summary>
        /// One report per round that could leave the barrel while this packet is simulated.
        /// UseableGun.ballistics pairs each round with one InputInfo and silently drops any it
        /// cannot pair, so a fast turret supplied with a single report does one round of damage and
        /// the rest in noise.
        /// </summary>
        private static int PlanHitReportCount(ItemGunAsset gun)
        {
            int samplesPerPacket = (int)PlayerInput.SAMPLES;

            // The equipment clock runs at 50 ticks a second and the gun may fire on one tick in
            // every Firerate + 1 of them - the same figure the asset states as 50 / (Firerate + 1)
            // rounds per second.
            int ticksPerRound = Mathf.Max(1, gun.firerate + 1);
            return Mathf.Clamp(Mathf.CeilToInt((float)samplesPerPacket / ticksPerRound), 1, samplesPerPacket);
        }

        /// <summary>
        /// Whether one of ours is close enough to the firing line to be hit by it. A turret is a good
        /// deal less forgiving than a rifle, so the clearance is wider than the on-foot one - and
        /// unlike the vehicle's own path, this stops the gun rather than moving the bandit: a
        /// squadmate walking past the muzzle is not in danger unless the trigger goes down.
        /// </summary>
        private bool IsSquadmateInLineOfFire(Vector3 origin, Vector3 direction, float targetDistance)
        {
            const float clearanceRadius = 1.5f;

            List<BanditBotController> bandits = BanditBotController.LiveBandits;
            for (int i = 0; i < bandits.Count; i++)
            {
                BanditBotController other = bandits[i];
                if (other == null || other == _controller || other.Self == null
                    || other.Self.life == null || other.Self.life.isDead)
                {
                    continue;
                }

                if (_controller.IsHostileTo(other.Self, otherIsBandit: true))
                {
                    continue; // another team's - the gunner is not holding fire for that
                }

                Vector3 centre = AimPointOf(other.Self) - origin;
                float along = Vector3.Dot(centre, direction);
                if (along <= 0.5f || along >= targetDistance)
                {
                    continue; // behind the muzzle, or beyond what is being shot at
                }

                if ((centre - direction * along).sqrMagnitude < clearanceRadius * clearanceRadius)
                {
                    return true;
                }
            }

            return false;
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

        /// <summary>
        /// Works out how high to hold the vehicle by measuring where it is sitting right now, while
        /// it is still resting on the ground of its own accord.
        ///
        /// Deriving this from the footprint alone means trusting collider geometry to agree with
        /// where the vehicle physically settles, and suspension, tracks and odd collider layouts all
        /// make that a guess. Measuring the real gap is exact for the vehicle actually being driven.
        ///
        /// Clamped against the footprint's own figure so a vehicle that is already floating - left
        /// there by an earlier trip, or dropped in by a spawn - cannot bake its float in and keep it
        /// forever.
        /// </summary>
        private void CalibrateRideHeight(InteractableVehicle vehicle)
        {
            Transform vehicleTransform = vehicle.transform;
            float fromFootprint = Mathf.Max(0f, _footprint.RideHeight);

            if (VehicleTerrain.TrySample(vehicleTransform.position, vehicleTransform, out Vector3 ground, out Vector3 _))
            {
                _rideHeight = Mathf.Clamp(vehicleTransform.position.y - ground.y, 0f, fromFootprint + 0.5f);
                return;
            }

            _rideHeight = fromFootprint;
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
                if (_controller != null && _controller.IsReloading)
                {
                    seat += $", reloading ({_controller.ReloadSecondsRemaining:0.0}s)";
                }
                else
                {
                    seat += _triggerLatched ? ", firing" : ", tracking";
                }
            }

            if (_navigator.HasDestination)
            {
                seat += _navigator.IsBlocked
                    ? $", blocked {_navigator.RemainingDistance:0}m out"
                    : $", {_navigator.RemainingDistance:0}m to go" + (_navigator.IsFollowingPath ? " (A*)" : " (steering)");

                if (Time.time < _unstickUntil)
                {
                    seat += $", backing out ({_unstickAttempts}/{MaxUnstickAttempts})";
                }
                else if (_reversing)
                {
                    seat += ", in reverse";
                }
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
        /// The nearest live player this crew is at war with, at any angle and through anything. Used
        /// only for pointing a seat; nothing shoots off the back of it.
        /// </summary>
        private Player FindNearestEnemy()
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

                // A bandit from a team this crew is fighting is worth pointing the gun at; one of
                // our own is not. Same rule the on-foot scan uses - see BanditTeams.IsHostile.
                bool candidateIsBandit = candidate.GetComponent<BanditBotController>() != null;
                if (!_controller.IsHostileTo(candidate, candidateIsBandit))
                {
                    continue;
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
