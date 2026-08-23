using System.Collections.Generic;
using BanditPlugin.Navigation;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.BanditGeometry;

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

        /// <summary>
        /// How much higher than the ground it is on a vehicle may find itself standing after one
        /// step: whatever a 35 degree climb gives over the distance travelled, plus a kerb.
        /// See <see cref="IsSurfaceReachable"/>. The tangent matches the navigator's own
        /// MaxClimbDegrees, so the drive step and the test that approved it agree about what a hill
        /// is.
        /// </summary>
        private const float MaxClimbTangent = 0.7f;
        private const float MaxStepUpMetres = 1f;

        /// <summary>
        /// How far off the wanted heading a vehicle may be pointing before it steers at all.
        ///
        /// Steering is a key press, not a dial, so without a deadband a vehicle holds full lock one
        /// way then the other all the way down a straight road. Wide enough to sit still on a
        /// straight, narrow enough that the vehicle is never visibly crabbing.
        /// </summary>
        private const float SteeringDeadbandDegrees = 4f;

        /// <summary>
        /// How far ahead the steering looks at its own rate of turn, in seconds.
        ///
        /// This is the whole of the wobble fix. Steering input is three states - left, straight,
        /// right - because that is what the wheel code takes, and it holds full lock until it is
        /// told otherwise. Steering purely on "which side of the wanted heading am I on" therefore
        /// holds full lock right up to the moment it crosses, overshoots, holds full lock the other
        /// way, and the vehicle saws down the road at whatever frequency the packet rate allows.
        ///
        /// Judging the error the vehicle *will* have if it keeps turning at the rate it is already
        /// turning is what a person does without thinking about it: you unwind the wheel before the
        /// car is straight. A third of a second is enough to unwind in time at road speed without
        /// making the steering feel lazy.
        /// </summary>
        private const float SteeringDampingSeconds = 0.35f;

        /// <summary>How far over its speed limit a vehicle gets before it brakes rather than simply
        /// lifting off. Coasting down covers the ordinary case; this is for arriving at the top of a
        /// hill with gravity helping.</summary>
        private const float OverspeedBrakeMetresPerSecond = 2f;

        /// <summary>Walking pace, for coming round a corner too tight to take at speed. Enough to
        /// make the steering bite, slow enough to be a manoeuvre rather than a lunge.</summary>
        private const float CreepMetresPerSecond = 2.5f;

        /// <summary>At or below this SpeedScale the vehicle is being held deliberately, so it is not
        /// judged for making no progress. See <see cref="UpdateProgress"/>.</summary>
        private const float HeldSpeedScale = 0.05f;

        /// <summary>
        /// How hard a bandit is willing to brake, and how much clear road it insists on keeping in
        /// hand, when pacing itself against whatever is in front.
        ///
        /// Together these are the whole of "do not drive into things": the speed limit for this
        /// packet is whatever the vehicle could still stop from within the clear road the navigator
        /// measured, less the margin. It falls out of that automatically that a bandit follows a
        /// slower vehicle at a sane distance, slows for a parked car before deciding whether to go
        /// round it, and stops behind something it cannot get past - none of which needed a rule of
        /// its own. Three and a half metres per second squared is unhurried braking; the vehicle can
        /// do far better in an emergency, and asking for less than it can do is what makes it look
        /// like a driver rather than a solver.
        /// </summary>
        private const float ComfortBrakingMetresPerSecondSquared = 3.5f;
        private const float FollowingMarginMetres = 2.5f;

        /// <summary>
        /// How long a bandit will sit behind another vehicle before it decides that one is not going
        /// anywhere either and starts looking for a way past.
        ///
        /// Waiting behind traffic is not being stuck, and the stall detector cannot tell the two
        /// apart on its own - it only sees a vehicle that has stopped getting closer to where it was
        /// sent. Left to it, a follower in a column that stops for thirty seconds decides it is
        /// wedged and reverses into whoever is behind it. But the patience has to end somewhere, or
        /// a burnt-out wreck across the road stops the convoy for good.
        /// </summary>
        private const float TrafficPatienceSeconds = 12f;

        /// <summary>How far a measured ride height may differ from the one the body's colliders
        /// imply before it is treated as a vehicle that is not resting on anything. Roughly the
        /// suspension travel, which is the whole of the honest difference between the two.</summary>
        private const float RideHeightToleranceMetres = 0.35f;

        /// <summary>Heading error at which the vehicle stops moving and only turns.</summary>
        private const float StopAndTurnDegrees = 120f;

        /// <summary>
        /// The heading error at which the driver gives up arcing toward the target and does a proper
        /// three-point turn, and the smaller error at which it decides the turn is done. The gap
        /// between them is hysteresis, so it does not drop in and out of the manoeuvre on the
        /// boundary.
        /// </summary>
        private const float TurnAroundEnterDegrees = 80f;
        private const float TurnAroundExitDegrees = 50f;

        /// <summary>
        /// The fastest a vehicle may be travelling and still decide to three-point turn.
        ///
        /// Nobody turns a lorry round at thirteen metres a second, and a vehicle that tries reads as
        /// a handbrake stop in the middle of an open road. A large heading error at speed does not
        /// mean "the target is behind me" anyway - it means the vehicle is running wide of a bend,
        /// or the navigator has just deflected round something - and the answer to both is to slow
        /// down and steer, which <see cref="CorneringSpeed"/> makes it do. If the error is still
        /// there once it has slowed, the turn starts then, properly, from a crawl.
        /// </summary>
        private const float TurnAroundMaxMetresPerSecond = 3.5f;

        /// <summary>
        /// Heading error a vehicle may carry at full speed, and the speed band it is squeezed into
        /// beyond that.
        ///
        /// Steering is a key press held for a packet, so how tight an arc the vehicle actually
        /// describes is decided by how fast it is going. Nothing was tying the two together: the
        /// speed limit came from what was in front and the steering came from the error, so a
        /// vehicle could take a bend flat out, run wide of it, and only then discover the target was
        /// ninety degrees off. Slowing in proportion to how far off line the nose is turns that into
        /// what a driver does - lift for the corner, take it, get back on the throttle.
        /// </summary>
        private const float CorneringFreeDegrees = 35f;
        private const float FastCorneringMetresPerSecond = 12f;
        private const float SlowCorneringMetresPerSecond = 3f;

        /// <summary>How far ahead or behind a manoeuvre step checks for room before it commits to
        /// moving that way. One check before every movement, which is the whole discipline of a
        /// tight turn: never move in a direction you have not just proven is clear.</summary>
        private const float ManeuverProbeMetres = 3.5f;

        /// <summary>How far off the nearest road centre a manoeuvre may carry the vehicle before it
        /// counts as leaving the road. The node half-width plus this, so it uses the full
        /// carriageway and a good deal of shoulder but does not swing out into a field. Generous on
        /// purpose: a vehicle that insists on the exact crown of the road backs out of an
        /// intersection it could have driven through slightly off to one side.</summary>
        private const float OnRoadMarginMetres = 4.5f;

        /// <summary>The extra room allowed around a junction node - a crossroads or a tee, where
        /// several road pieces meet and the drivable area is far wider than any one of their
        /// half-widths. Without this the vehicle treats the open middle of an intersection as
        /// off-road and reverses to find the centre line of a single street.</summary>
        private const float JunctionRoadMarginMetres = 10f;

        /// <summary>How far to look for a road when deciding whether a point is still on one. Beyond
        /// this the vehicle is somewhere the graph does not cover, and the road restriction is lifted
        /// rather than freezing it.</summary>
        private const float OnRoadSearchMetres = 30f;

        /// <summary>How much the heading must improve to count as the turn making progress, so the
        /// stall detector stays quiet through a legitimate multi-step turn but still fires on one
        /// that is genuinely wedged.</summary>
        private const float TurnProgressDegrees = 3f;

        /// <summary>Beyond this a player is not worth turning a turret onto.</summary>
        private const float TurretTrackRangeMetres = 250f;

        /// <summary>When the patience for the vehicle in front runs out. Zero when nothing is in
        /// the way. See <see cref="TrafficPatienceSeconds"/>.</summary>
        private float _trafficHoldUntil;

        /// <summary>When the current reverse hop must end, and the earliest another may start. See
        /// <see cref="ReverseHopSeconds"/>.</summary>
        private float _reverseUntil;

        /// <summary>Three-point-turn state: whether one is in progress, whether the current leg is
        /// the reverse one, and the best (smallest) heading error the turn has reached, for judging
        /// whether it is still making progress.</summary>
        private bool _turning;
        private bool _turningReverse;
        private float _turnBestError = 180f;

        /// <summary>What each leg of the current three-point turn is refused by, for the log. The
        /// one manoeuvre where both ways out can be shut at once is the one that most needs to say
        /// which - "blocked by nothing it can name" was a real give-up message from a tank standing
        /// on clear tarmac.</summary>
        private string _turnRefusals;
        private float _reverseCooldownUntil;

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
        /// The longest a reverse hop may last, and how long the vehicle must then drive forwards
        /// before it is allowed another.
        ///
        /// Both exist because the old exit conditions could not fire. A reverse hop ended when the
        /// heading error dropped below seventy degrees or the destination got further away than
        /// twenty-seven metres - and while reversing, the vehicle deliberately points its *tail* at
        /// the destination, so the heading error sits near a hundred and eighty by construction and
        /// never drops. The distance could not save it either, because a vehicle following a route
        /// is handed a fresh target twelve metres ahead the whole way, so the remaining distance
        /// never grows. The log showed the result exactly: REV, err=-56, left=12.0m, held for the
        /// length of the trip. They drove the route backwards.
        ///
        /// A hop is now a manoeuvre with an end. Back up for a couple of seconds, then go forwards
        /// whatever the geometry says - which is also how a person gets a vehicle round: reverse
        /// once, then drive out of it. The cooldown is what stops it going straight back into
        /// reverse on the next packet and reinventing the same latch.
        /// </summary>
        private const float ReverseHopSeconds = 2.5f;
        private const float ReverseCooldownSeconds = 4f;

        /// <summary>
        /// How long the vehicle may fail to get closer to where it was sent before it counts as
        /// stuck. This is the whole definition: not "is something in front of me" - the navigator
        /// already sweeps for that and drives round it - but "I was supposed to be making progress
        /// and I am not". A vehicle grinding along a wall is moving and getting nowhere; a vehicle
        /// waiting to finish a turn is still and perfectly fine.
        /// </summary>
        /// Two and a half seconds was far too quick. A vehicle held up for one traffic light's
        /// worth of time, or crawling round a bend behind something slower, had done nothing wrong
        /// and was being put through a recovery manoeuvre for it - and the recovery is destructive
        /// enough (reverse, then refuse the route's own direction for several seconds) that firing
        /// it wrongly is much worse than firing it late. Seven seconds of gaining under a metre is
        /// eighty metres of driving at road speed: unambiguous.
        private const float NoProgressSeconds = 7f;

        /// <summary>How much closer counts as progress rather than noise.</summary>
        private const float ProgressMetres = 0.75f;

        /// <summary>
        /// A route that suddenly gets this much longer is a new route, not a failure - repathing
        /// round the far side of a building legitimately lengthens the trip - so the yardstick is
        /// re-taken instead of counting it as being stuck.
        /// </summary>
        private const float RepathJumpMetres = 15f;

        /// <summary>
        /// How long the direction that failed stays banned after a reverse.
        ///
        /// Cut from seven seconds, and it needed cutting. The ban refuses a fifty-degree arc around
        /// a world direction, and on a road the direction that "failed" is the direction of the
        /// road - so a wrongly-fired recovery did not merely pause the vehicle, it forbade it from
        /// driving down the road it was on and sent it into the field beside it at the first angle
        /// the fan would accept. Long enough to make the vehicle try somewhere else, short enough
        /// that being wrong about it costs one manoeuvre rather than the trip.
        /// </summary>
        private const float BanFailedDirectionSeconds = 3f;

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

        /// <summary>The vehicle's own rigidbody, under physics driving. Cached per vehicle for the
        /// same reason the footprint is - it does not change while the bandit is sitting in it.</summary>
        private Rigidbody _body;
        private InteractableVehicle _bodyMeasuredFor;

        /// <summary>The driving packet last handed to PlayerInput, kept so its pose can be held
        /// current until the server reads it. See <see cref="RefreshPendingPose"/>.</summary>
        private DrivingPlayerInputPacket _pendingPose;
        private InteractableVehicle _pendingPoseVehicle;

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

        /// <summary>How fast it is actually going, in metres per second. Read by whatever is feeding
        /// it destinations, so a route can be followed more tightly the slower the vehicle is.</summary>
        public float Speed => _speed;

        /// <summary>Where the bandit is driving to, if anywhere.</summary>
        public bool HasDestination => _navigator.HasDestination;

        public Vector3 Destination => _navigator.Destination;

        /// <summary>True while the vehicle is too wide for every way round the obstacle in front of
        /// it - the "it doesn't fit" case, as opposed to being merely slow.</summary>
        public bool IsBlocked => _navigator.IsBlocked;

        /// <summary>
        /// Whether the vehicle is in the water far enough that vanilla has drowned it.
        ///
        /// The same test vanilla uses for its own engine cut-out, rather than a depth of our own:
        /// whatever the map says is water and wherever the vehicle keeps its waterline, this agrees
        /// with the game about it. A drowned vehicle is finished as a vehicle - the engine will not
        /// restart while it is in there - so whoever is driving it should stop asking.
        /// </summary>
        public bool IsSubmerged
        {
            get
            {
                InteractableVehicle vehicle = Vehicle;
                return vehicle != null && vehicle.isUnderwater;
            }
        }

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
            InteractableVehicle vehicleLeft = Vehicle;

            if (!_self.movement.forceRemoveFromVehicle())
            {
                reason = "it is not in a vehicle";
                return false;
            }

            TrackNearestPlayer = false;
            _navigator.Stop();
            _speed = 0f;

            // Getting out is a seat change, so vanilla will call updatePhysics itself - but it does
            // that before this returns, and the bandit is no longer here to re-assert anything.
            // Asking again costs nothing and means the vehicle is never left in our state.
            RestoreVanillaPhysics(vehicleLeft);
            _bodyMeasuredFor = null;
            _body = null;

            reason = was;
            return true;
        }

        /// <summary>
        /// Sends the vehicle to a point. Only the driver can be given one, and only in something
        /// that drives on the ground - see <see cref="WhyCannotDriveTo"/>.
        /// </summary>
        public bool TrySetDestination(Vector3 destination, out string reason)
        {
            return TrySetDestination(destination, Mathf.Max(4f, _footprint.HalfLength + 2f), out reason);
        }

        /// <summary>
        /// Drives to a point, told explicitly how near counts as arrived.
        ///
        /// The overload exists for route following, where the point handed over is not a
        /// destination at all - it is a carrot a fixed distance up the road that moves with the
        /// vehicle. Such a carrot must use a small arrival radius: with the default one (the
        /// vehicle's own length plus a margin, seven metres for a Stryker) a carrot placed six
        /// metres ahead is already "arrived" the instant it is issued, so the navigator stops, the
        /// route re-issues the same point, and the vehicle sits on an empty road being told it has
        /// reached somewhere it is nowhere near. The route layer decides real arrival itself; the
        /// carrot only needs to steer.
        /// </summary>
        public bool TrySetDestination(Vector3 destination, float arriveRadius, out string reason)
        {
            InteractableVehicle vehicle = Vehicle;

            reason = WhyCannotDriveTo(vehicle);
            if (reason != null)
            {
                return false;
            }

            EnsureFootprint(vehicle);
            CalibrateRideHeight(vehicle);

            _navigator.SetDestination(destination, arriveRadius);

            // A fresh trip gets a fresh budget of attempts to back out of something, and a stall
            // sample taken from where it actually is rather than wherever the last trip ended.
            _unstickAttempts = 0;
            _unstickUntil = 0f;
            _reversing = false;
            _reverseUntil = 0f;
            _bestRemaining = float.MaxValue;
            _lastProgressTime = Time.time;

            reason = null;
            return true;
        }

        /// <summary>
        /// Slides the current destination along to a new point, for a route follower whose "target"
        /// is a carrot a fixed distance up the road.
        ///
        /// The carrot has to move every tick, and <see cref="TrySetDestination"/> cannot be what
        /// moves it: that starts a fresh trip, which drops the route, refills the unstick budget and
        /// re-takes the stall yardstick. Called every tick it would mean the vehicle could never be
        /// found stuck; called only when the route index changes - which is what happened before -
        /// the carrot sits still for eight metres of driving, and a vehicle that passes a stationary
        /// point three metres off its nose sees the bearing to it swing through a right angle. On a
        /// bend that reads as "the target is ninety degrees off", which is the threshold for a
        /// three-point turn, so the vehicle stops mid-corner and reverses for no reason at all.
        ///
        /// Returns false when there is no trip to move, so the caller issues a real destination.
        /// </summary>
        public bool TryMoveDestination(Vector3 destination, float arriveRadius)
        {
            if (!_navigator.HasDestination || Vehicle == null)
            {
                return false;
            }

            // The stall detector measures "am I getting closer to where I was sent", and a
            // destination that runs away up the road ahead of the vehicle breaks that measurement:
            // the remaining distance holds steady at one lookahead however well the vehicle is
            // driving, and after seven seconds of it the vehicle decides it is wedged and reverses
            // out of an empty road. So the yardstick is moved by exactly as far as the carrot moved,
            // and what is left over is the real progress again.
            //
            // Crediting the carrot's own movement rather than the change in remaining distance is
            // what makes this honest in both cases: the carrot is placed a lookahead from the
            // vehicle, so it advances precisely as much as the vehicle does, and a vehicle that is
            // wedged moves it not at all and is found stuck exactly as before.
            Vector3 moved = destination - _navigator.Destination;
            moved.y = 0f;

            _navigator.MoveDestination(destination, arriveRadius);

            if (_bestRemaining < float.MaxValue)
            {
                _bestRemaining += moved.magnitude;
            }

            return true;
        }

        public void StopDriving()
        {
            _navigator.Stop();

            // Under physics driving the body was deliberately left loose, so handing it back is part
            // of stopping - otherwise a vehicle told to stop on a slope simply rolls away.
            RestoreVanillaPhysics(Vehicle);
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

            // The backstop to the navigator refusing to drive into water. Nothing about the drive
            // step notices water on its own - it snaps the vehicle onto whatever is beneath it,
            // which under water is the seabed - so a vehicle that got in anyway, by being pushed,
            // by spawning there, or by driving in before the refusal existed, would motor along the
            // bottom indefinitely. Vanilla has already killed the engine by this point; this stops
            // the bandit pretending otherwise.
            if (IsSubmerged && _navigator.HasDestination)
            {
                Rocket.Core.Logging.Logger.Log($"[Bandit] {DescribeVehicle(vehicle)} is underwater - "
                    + "stopping rather than driving along the bottom.");
                _navigator.Stop();
            }

            if (UsePhysicsDriving(vehicle))
            {
                // The vehicle drives itself. All this does is work the controls and then report
                // where the physics put it - see StepPhysically.
                StepPhysically(vehicle, deltaTime, out forwardVelocity, out float steeringInput);

                position = vehicleTransform.position;
                rotation = vehicleTransform.rotation;

                _pendingPose = BuildDrivingPacket(vehicle, frameNumber, position, rotation,
                    forwardVelocity, steeringInput);
                _pendingPoseVehicle = vehicle;
                return _pendingPose;
            }

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

            _pendingPose = null;
            _pendingPoseVehicle = null;
            return BuildDrivingPacket(vehicle, frameNumber, position, rotation, forwardVelocity, 0f);
        }

        /// <summary>
        /// The packet itself, once somebody has decided what pose to report. Shared by both drive
        /// paths, because what goes in it does not depend on which of them produced the numbers.
        /// </summary>
        private DrivingPlayerInputPacket BuildDrivingPacket(InteractableVehicle vehicle, uint frameNumber,
            Vector3 position, Quaternion rotation, float forwardVelocity, float steeringInput)
        {
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
                //
                // ReplicatedForwardVelocity on the server is set from this, and the wheel code reads
                // it back to scale steering angle and motor torque - so under physics driving these
                // are not cosmetic any more, they are part of the loop.
                speed = Mathf.Abs(forwardVelocity),
                forwardVelocity = forwardVelocity,
                steeringInput = steeringInput,
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
            if (VehicleTerrain.TrySample(next, vehicleTransform, out Vector3 ground, out Vector3 groundNormal)
                && IsSurfaceReachable(ground, position, step.magnitude))
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
        /// <summary>
        /// Whether this vehicle is driven by the engine's own physics rather than by a pose this
        /// class works out for itself.
        ///
        /// Unturned gives a vehicle exactly two modes, and <see cref="InteractableVehicle.updatePhysics"/>
        /// picks between them. With somebody in seat 0 the server makes the body kinematic and takes
        /// the pose from the driver's packets; with the seat empty the server simulates it properly.
        /// A real player is the first case: their client runs the wheel physics locally and reports
        /// the result, and the server only checks the result is not absurd. Nothing about that is
        /// the server's own work, which is why a bandit had to invent a pose - and inventing one is
        /// what produced every complaint about how these things move. A hand-rolled kinematic step
        /// has no rigidbody, so it floats or sinks depending on a guessed ride height, it has no
        /// collision response, so a T-72 that clips a fire hydrant tips to whatever angle a single
        /// ground-normal ray reports and then buries its nose in the road, and it has no engine, so
        /// its speed is a constant somebody chose.
        ///
        /// So do what the real client does instead: hold the body physical even though it is
        /// driven, work the wheels through vanilla's own wheel code, and report the pose that comes
        /// out. Same packets, same validation, same physics - the difference is only which machine
        /// is running it.
        /// </summary>
        private bool UsePhysicsDriving(InteractableVehicle vehicle)
        {
            if (vehicle == null || vehicle.asset == null || !PhysicsDrivingEnabled)
            {
                return false;
            }

            if (vehicle.asset.engine != EEngine.CAR)
            {
                return false;
            }

            // Everything hangs on being able to run vanilla's own gearbox-and-torque pass, because
            // every vehicle on this server turns out to declare gear ratios - the Offroader and the
            // Ural included - and a geared vehicle handed no torque sits still and revs.
            return WheelPhysicsPass != null;
        }

        /// <summary>
        /// Keeps the pose in a packet that has not been read yet up to date with where the vehicle
        /// actually is.
        ///
        /// This matters only under physics driving, and it matters a lot. Packets go into
        /// PlayerInput's own queue and are read out on the server's schedule, up to a packet
        /// interval later - and the server applies the pose it finds with Rigidbody.MovePosition.
        /// Against a kinematic body, which is what a real player's vehicle is on the server, a
        /// slightly stale pose is harmless. Against a live one it is a correction backwards to
        /// where the vehicle was eighty milliseconds ago, which at speed is the better part of a
        /// metre, applied several times a second, fighting the physics that moved it.
        ///
        /// The packet is a reference we still hold, the queue has not touched it, and Unity is
        /// single threaded - so the fix is simply to keep writing the current pose into it until it
        /// is gone. Refreshing one the server has already read is harmless: nothing else refers to
        /// it. The result is that the pose the server applies is the pose the vehicle is in, and
        /// MovePosition becomes the no-op it should be.
        /// </summary>
        public void RefreshPendingPose()
        {
            if (_pendingPose == null)
            {
                return;
            }

            // Tracked alongside rather than read back off the packet, whose own vehicle field is
            // internal to the game assembly.
            InteractableVehicle vehicle = Vehicle;
            if (vehicle == null || _pendingPoseVehicle != vehicle)
            {
                _pendingPose = null;
                _pendingPoseVehicle = null;
                return;
            }

            Transform vehicleTransform = vehicle.transform;
            _pendingPose.position = vehicleTransform.position;
            _pendingPose.rotation = vehicleTransform.rotation;

            Rigidbody body = EnsureBody(vehicle);
            if (body != null)
            {
                float forward = Vector3.Dot(body.velocity, vehicleTransform.forward);
                _pendingPose.speed = Mathf.Abs(forward);
                _pendingPose.forwardVelocity = forward;
                _pendingPose.velocityInput = forward;
            }
        }

        /// <summary>Whether physics driving is switched on for this server.</summary>
        private static bool PhysicsDrivingEnabled
        {
            get
            {
                BanditPlugin plugin = BanditPlugin.Instance;
                return plugin?.Configuration?.Instance == null
                    || plugin.Configuration.Instance.VehiclePhysicsDriving;
            }
        }

        /// <summary>
        /// Vanilla's gearbox-and-torque pass, which is the one piece of driving a dedicated server
        /// genuinely does not run.
        ///
        /// <c>InteractableVehicle.isDriver</c> is compiled to a flat <c>false</c> in the server
        /// build, so the branch that calls this never fires. Everything else about driving is
        /// reachable - <see cref="InteractableVehicle.simulate"/> is public and is the client's own
        /// entry point - but this one method is private, and it is where the gears are selected, the
        /// engine RPM is integrated and the motor torque is finally handed to each wheel. Without it
        /// a geared vehicle gets no torque at all, and every vehicle on this server is geared.
        ///
        /// Bound once as an open delegate rather than invoked through MethodInfo each time, so the
        /// per-packet cost is an ordinary call. If it cannot be bound, physics driving reports
        /// itself unavailable and the older kinematic step takes over.
        /// </summary>
        private static System.Action<InteractableVehicle, float> WheelPhysicsPass
        {
            get
            {
                if (_wheelPhysicsResolved)
                {
                    return _wheelPhysicsPass;
                }

                _wheelPhysicsResolved = true;

                System.Reflection.MethodInfo method = typeof(InteractableVehicle).GetMethod(
                    "UpdateLocallyDrivenWheelPhysicsAndGears",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
                        | System.Reflection.BindingFlags.Public);

                try
                {
                    _wheelPhysicsPass = method == null
                        ? null
                        : (System.Action<InteractableVehicle, float>)System.Delegate.CreateDelegate(
                            typeof(System.Action<InteractableVehicle, float>), method);
                }
                catch (System.Exception)
                {
                    _wheelPhysicsPass = null;
                }

                if (_wheelPhysicsPass == null)
                {
                    Rocket.Core.Logging.Logger.LogWarning("[Bandit] Could not reach the game's wheel "
                        + "physics pass, so bandits fall back to the older kinematic drive step. "
                        + "This usually means the game updated and the method was renamed.");
                }

                return _wheelPhysicsPass;
            }
        }

        private static System.Action<InteractableVehicle, float> _wheelPhysicsPass;
        private static bool _wheelPhysicsResolved;

        /// <summary>
        /// One packet's worth of driving the vehicle with its own physics: work out where the
        /// navigator wants to go, hold the controls that way, and let Unity move it.
        ///
        /// Nothing here sets a position. The wheels are handed a steering and an acceleration input
        /// exactly as a driving client hands them one, vanilla's wheel code turns those into steer
        /// angle, motor torque and brake torque, and the physics step does the rest - suspension,
        /// traction, the kerb it just hit, all of it. Whatever comes out is what gets reported.
        /// </summary>
        private void StepPhysically(InteractableVehicle vehicle, float deltaTime, out float forwardVelocity,
            out float steeringInput)
        {
            EnsureFootprint(vehicle);
            EnsurePhysical(vehicle);

            Transform vehicleTransform = vehicle.transform;
            Rigidbody body = EnsureBody(vehicle);

            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            forwardVelocity = Vector3.Dot(velocity, vehicleTransform.forward);

            if (!_navigator.HasDestination)
            {
                // Parked. Still worked every packet, because a physical vehicle left alone on a hill
                // rolls down it - the handbrake is the thing standing in for the kinematic freeze
                // the engine would otherwise have applied.
                ApplyWheelInputs(vehicle, deltaTime, 0, 0, brake: true);
                steeringInput = 0f;
                _reversing = false;
                return;
            }

            _navigator.Tick(vehicle, _footprint, deltaTime, forwardVelocity);

            float yaw = vehicleTransform.eulerAngles.y;
            Vector3 heading = _navigator.DesiredDirection;

            if (heading.sqrMagnitude < 0.0001f)
            {
                // Nowhere clear to steer - the navigator could not find a heading that fits. Hold on
                // the brake; the stall detector decides when to give up.
                UpdateProgress();
                ApplyWheelInputs(vehicle, deltaTime, 0, 0, brake: true);
                steeringInput = 0f;
                _reversing = false;
                _turning = false;
                return;
            }

            float desiredYaw = Mathf.Atan2(heading.x, heading.z) * Mathf.Rad2Deg;

            // The signed error of the nose from where we want it pointing, always measured against
            // the real target heading rather than against whichever way we happen to be driving.
            // Getting this wrong - measuring it against a straight-back reverse direction, so it read
            // as zero - is why the old reverse just went straight and never turned the vehicle round.
            float headingError = Mathf.DeltaAngle(yaw, desiredYaw);
            float absError = Mathf.Abs(headingError);

            // Enter a three-point turn when the target is well off the nose, leave it once the nose
            // has come most of the way round. In open ground the "turn" never needs its reverse leg
            // and is just a hard forward arc; in a tight space it alternates, which is the manoeuvre.
            if (!_turning && absError > TurnAroundEnterDegrees
                && Mathf.Abs(forwardVelocity) < TurnAroundMaxMetresPerSecond)
            {
                _turning = true;
                _turningReverse = false;
                _turnBestError = absError;
            }
            else if (_turning && absError < TurnAroundExitDegrees)
            {
                _turning = false;
            }

            int steer;
            int throttle;
            bool brake = false;
            bool reverse;

            // A stall recovery, set by UpdateProgress when the vehicle has stopped getting closer,
            // takes precedence over both driving and turning: back straight out of whatever it is
            // wedged against, with the wheels turned toward the target so the reverse also starts
            // lining it up. This is the head-on case a three-point turn does not cover - nose into a
            // wall, target dead ahead - and without it a physics-driven vehicle had no way to
            // reverse out of anything, because the reverse used to live on a path this no longer
            // takes.
            if (Time.time < _unstickUntil && _navigator.IsTravelClear(vehicle, _footprint,
                    -(Quaternion.Euler(0f, yaw, 0f) * Vector3.forward), ignoreOverlap: true))
            {
                reverse = true;
                _turning = false;
                int toward = absError < SteeringDeadbandDegrees ? 0 : (headingError > 0f ? 1 : -1);
                steer = -toward;
                float reverseSpeed = -forwardVelocity;
                throttle = reverseSpeed > CreepMetresPerSecond ? 0 : -1;
                brake = reverseSpeed > CreepMetresPerSecond;
            }
            else if (_turning)
            {
                StepThreePointTurn(vehicle, yaw, headingError, absError, forwardVelocity,
                    out steer, out throttle, out brake, out reverse);
            }
            else
            {
                StepForwardDrive(vehicle, body, headingError, forwardVelocity,
                    out steer, out throttle, out brake, out reverse);
            }

            // The heading that stopped working is the one a stall bans, so the reroute goes a
            // different way. Only while going forward - the way out of a reverse is not a direction
            // to forbid.
            if (!reverse)
            {
                _lastTravelDirection = heading.normalized;
            }

            UpdateProgress();

            steeringInput = steer;

            ClearSquadmatesFromPath(vehicle, vehicleTransform.position,
                reverse ? -vehicleTransform.forward : vehicleTransform.forward, Mathf.Abs(forwardVelocity));

            ApplyWheelInputs(vehicle, deltaTime, steer, throttle, brake);

            float along = reverse ? -forwardVelocity : forwardVelocity;
            BanditNavLog.Trace(this,
                $"v={along:0.0}m/s scale={SpeedScale:0.00} steer={steer} thr={throttle}"
                + (brake ? " BRAKE" : string.Empty)
                + (reverse ? " REV" : string.Empty)
                + (_turning ? (_turningReverse ? " TURN-REV" : " TURN-FWD") : string.Empty)
                + (_turning && _turnRefusals != null ? $" {_turnRefusals}" : string.Empty)
                + $" err={headingError:0}deg"
                + $" clear={(_navigator.ClearAheadMetres >= float.MaxValue * 0.5f ? "inf" : _navigator.ClearAheadMetres.ToString("0.0"))}m"
                + $" left={_navigator.RemainingDistance:0.0}m"
                + (_navigator.AvoidanceDegrees != 0f ? $" avoid={_navigator.AvoidanceDegrees:0}deg" : string.Empty)
                + (_navigator.IsBlocked ? " BLOCKED" : string.Empty)
                + (_navigator.ObstacleAhead != null ? $" ahead={_navigator.ObstacleAhead}" : string.Empty)
                + (_navigator.RefusedReason != null ? $" refused={_navigator.RefusedReason}" : string.Empty)
                + (_navigator.Overlapping != null ? $" inside={_navigator.Overlapping}" : string.Empty));

            // Only so /banditv status reports the speed it is really doing rather than nothing.
            _speed = Mathf.Abs(forwardVelocity);
            _reversing = reverse;
        }

        /// <summary>
        /// The fastest a vehicle may take a heading error this big.
        ///
        /// Unbounded while the nose is roughly on line, so a straight road is driven at road speed,
        /// and falling to a crawl by the angle at which the driver would rather turn round than arc.
        /// </summary>
        private static float CorneringSpeed(float absError)
        {
            if (absError <= CorneringFreeDegrees)
            {
                return float.MaxValue;
            }

            return Mathf.Lerp(FastCorneringMetresPerSecond, SlowCorneringMetresPerSecond,
                Mathf.InverseLerp(CorneringFreeDegrees, TurnAroundEnterDegrees, absError));
        }

        /// <summary>The fastest a vehicle may go and still pull up short of something this far
        /// ahead, braking no harder than it would want to.</summary>
        private static float StoppableSpeed(float clearAhead)
        {
            if (clearAhead >= float.MaxValue * 0.5f)
            {
                return float.MaxValue;
            }

            float usable = clearAhead - FollowingMarginMetres;
            return usable <= 0f
                ? 0f
                : Mathf.Sqrt(2f * ComfortBrakingMetresPerSecondSquared * usable);
        }

        /// <summary>
        /// Hands the vehicle its controls, down the same two calls a real driving client makes.
        ///
        /// <see cref="InteractableVehicle.simulate"/> is the client's driving entry point and is
        /// public. It is worth far more than poking the wheel colliders by hand, because it is also
        /// where everything that keeps a vehicle the right way up lives: the wheel balancing force,
        /// the roll damping, and the downforce that scales with speed. Driving without it is what a
        /// T-72 clipping a fire hydrant and ending up on its side looks like. It also gates torque
        /// properly - no fuel, drowned, dead or engine off and the wheels get nothing, which is the
        /// correct behaviour rather than something to reimplement.
        ///
        /// Then the gearbox pass, which the server would otherwise skip. See
        /// <see cref="WheelPhysicsPass"/>.
        ///
        /// Steering and throttle are whole numbers because that is what the signature takes: a real
        /// player drives with keys, so the wheel code is built to smooth -1/0/1 into an angle at the
        /// asset's own steering speed. Feeding it a fraction would not steer more finely, it would
        /// just be a fraction of a key press.
        /// </summary>
        private void ApplyWheelInputs(InteractableVehicle vehicle, float deltaTime, int steering,
            int acceleration, bool brake)
        {
            vehicle.simulate(0u, _self.input.recov, steering, acceleration, 0f, 0f, brake,
                inputStamina: false, deltaTime);

            WheelPhysicsPass?.Invoke(vehicle, deltaTime);
        }

        /// <summary>
        /// Keeps the body physical while a bandit is driving it.
        ///
        /// Vanilla froze it when the bandit sat down - that is what it does to every driven vehicle,
        /// because normally the driver's own machine is the one simulating. Re-asserted every packet
        /// rather than once, since <see cref="InteractableVehicle.updatePhysics"/> runs again on any
        /// seat change and would put it back. Each assignment is guarded, so the steady state is a
        /// handful of comparisons.
        /// </summary>
        private void EnsurePhysical(InteractableVehicle vehicle)
        {
            Rigidbody body = EnsureBody(vehicle);
            if (body != null && body.isKinematic)
            {
                body.isKinematic = false;
                body.useGravity = true;
            }

            Wheel[] tires = vehicle.tires;
            if (tires == null)
            {
                return;
            }

            foreach (Wheel tire in tires)
            {
                if (tire != null && !tire.isPhysical && !tire.IsDead)
                {
                    tire.isPhysical = true;
                }
            }
        }

        /// <summary>
        /// Hands the vehicle back to the engine's own idea of what its physics should be. Called
        /// when the bandit stops driving or gets out, so a parked vehicle is frozen exactly as
        /// vanilla would have frozen it rather than left loose on a hillside.
        /// </summary>
        private void RestoreVanillaPhysics(InteractableVehicle vehicle)
        {
            if (vehicle != null)
            {
                vehicle.updatePhysics();
            }
        }

        private Rigidbody EnsureBody(InteractableVehicle vehicle)
        {
            if (vehicle == null)
            {
                return null;
            }

            if (_bodyMeasuredFor != vehicle)
            {
                _body = vehicle.GetComponent<Rigidbody>();
                _bodyMeasuredFor = vehicle;
            }

            return _body;
        }

        /// <summary>
        /// Whether the surface the ground probe found is one the wheels could actually have got up
        /// onto over this step, as opposed to one somewhere above them.
        ///
        /// The probe answers "what is the first solid thing below this point", starting well above
        /// the vehicle so that a rise it is driving into is found rather than missed. The cost of
        /// that generosity is that the roof of a house the vehicle is passing is also "below" a
        /// point above the house, so a vehicle clipping the footprint of a building had its own
        /// ground snap carry it up onto the roof, a bit at a time, within the server's vertical
        /// speed clamp the whole way. The clamp bounds the rate and never the total, which is why
        /// it did not stop it.
        ///
        /// So a rise is accepted only as far as the ground could plausibly have climbed over the
        /// distance actually travelled, plus a kerb. A roof edge is metres up in one step and fails;
        /// a hill, a ramp and a bridge approach are all continuous with what the vehicle is already
        /// standing on and pass. Falls are not limited here - that is gravity, and the server's own
        /// clamp already governs how fast it may happen.
        /// </summary>
        private bool IsSurfaceReachable(Vector3 ground, Vector3 position, float travelled)
        {
            float currentSurface = position.y - _rideHeight;
            if (ground.y <= currentSurface)
            {
                return true;
            }

            float allowedRise = travelled * MaxClimbTangent + MaxStepUpMetres;
            return ground.y - currentSurface <= allowedRise;
        }

        /// <summary>
        /// Ordinary driving: aimed at the target, arcing toward it, at a speed it can stop from
        /// within the road it can see. Used whenever the nose is roughly the right way round; a big
        /// turn is the three-point turn's job instead.
        /// </summary>
        private void StepForwardDrive(InteractableVehicle vehicle, Rigidbody body, float headingError,
            float forwardVelocity, out int steer, out int throttle, out bool brake, out bool reverse)
        {
            reverse = false;
            brake = false;

            // Steered on where the nose will be pointing shortly rather than where it is now, so it
            // unwinds the wheel before it is straight instead of sawing across the line. The rate
            // comes off the rigidbody, which is the real body under physics driving.
            float yawRate = body != null ? body.angularVelocity.y * Mathf.Rad2Deg : 0f;
            float steerError = headingError - yawRate * SteeringDampingSeconds;

            steer = Mathf.Abs(steerError) < SteeringDeadbandDegrees ? 0 : (steerError > 0f ? 1 : -1);

            float limit = CruiseSpeed(vehicle) * Mathf.Clamp01(SpeedScale);
            limit = Mathf.Min(limit, StoppableSpeed(_navigator.ClearAheadMetres));
            limit = Mathf.Min(limit, CorneringSpeed(Mathf.Abs(headingError)));

            if (limit <= 0.01f)
            {
                throttle = 0;
                brake = true;
                return;
            }

            if (forwardVelocity > limit)
            {
                throttle = 0;
                brake = forwardVelocity > limit + OverspeedBrakeMetresPerSecond;
            }
            else
            {
                throttle = 1;
            }
        }

        /// <summary>
        /// A three-point turn. The one manoeuvre a vehicle cannot fake and the old code got wrong.
        ///
        /// The rule is the one a driver uses: rotate the nose toward where you want to go, going
        /// forward while there is room ahead and backward when there is not - and crucially, when
        /// backing up, turn the wheels the *opposite* way, because a reversing car pivots its nose
        /// the other way for a given steering input. Steering the same way in both gears is what
        /// made the old reverse cancel the forward leg and leave the vehicle rocking on the spot.
        ///
        /// Every leg is checked before it is driven - both for something solid in the way and for
        /// the edge of the road - so the turn never grinds into a wall and never swings off the
        /// tarmac. When a leg is blocked the other gear is taken; when both are blocked it holds,
        /// and the stall detector ends the trip.
        /// </summary>
        private void StepThreePointTurn(InteractableVehicle vehicle, float yaw, float headingError,
            float absError, float forwardVelocity, out int steer, out int throttle, out bool brake,
            out bool reverse)
        {
            // Heading progress keeps the stall detector quiet: a turn that is still bringing the
            // nose round is working, however little ground it is covering.
            if (absError < _turnBestError - TurnProgressDegrees)
            {
                _turnBestError = absError;
                _lastProgressTime = Time.time;
            }

            Vector3 nose = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;

            // Two separate questions per leg: is it clear of obstacles, and does it keep us on the
            // road. Clearance is a hard no - we never drive into things. The road is only a
            // preference: a vehicle turning across a road has its nose and tail pointing at the two
            // shoulders, so a straight probe forward and back both read as off-road even though the
            // arc between them is fine. Treating the road as a hard rule there froze the turn with
            // clear tarmac all around it, which is the "refuses to move" in the log. So we prefer a
            // leg that is both clear and on-road, and fall back to one that is merely clear.
            BanditTravelRefusal forwardRefusal =
                _navigator.WhyTravelRefused(vehicle, _footprint, nose, ignoreOverlap: false);
            BanditTravelRefusal reverseRefusal =
                _navigator.WhyTravelRefused(vehicle, _footprint, -nose, ignoreOverlap: true);

            _turnRefusals = $"fwd={forwardRefusal} rev={reverseRefusal}";

            bool forwardClear = forwardRefusal == BanditTravelRefusal.None;
            bool reverseClear = reverseRefusal == BanditTravelRefusal.None;
            bool forwardOnRoad = forwardClear && IsOnRoad(vehicle.transform.position + nose * ManeuverProbeMetres);
            bool reverseOnRoad = reverseClear && IsOnRoad(vehicle.transform.position - nose * ManeuverProbeMetres);

            // Switch leg when the current one is no longer the better choice. Preference order:
            // a clear-and-on-road leg beats a merely-clear leg beats nothing.
            bool currentGood = _turningReverse ? reverseOnRoad : forwardOnRoad;
            bool otherGood = _turningReverse ? forwardOnRoad : reverseOnRoad;
            bool currentClear = _turningReverse ? reverseClear : forwardClear;
            bool otherClear = _turningReverse ? forwardClear : reverseClear;

            if (!currentGood && otherGood)
            {
                _turningReverse = !_turningReverse;
            }
            else if (!currentClear && otherClear)
            {
                _turningReverse = !_turningReverse;
            }

            // Neither way out. A slope refusal is the one that may be argued with: it is a
            // thirty-five degree cutoff decided by sampling the ground every two metres, and at a
            // crawl the honest way to find out whether the vehicle gets up a verge is to put it at
            // the verge. Anything else - something solid, a drop, deep water - is a real refusal and
            // is never driven into.
            //
            // Without this the turn simply stood on the brake: steer 0, throttle 0, both legs shut,
            // and nothing that happens next can reopen either of them, because what is in front of
            // and behind a stationary vehicle does not change while it is stationary. The stall
            // detector then fired three times against a vehicle that was being told not to move,
            // banned a direction that was never the problem, and the convoy unloaded a tank on an
            // open road. That is exactly the T-72 in the log.
            if (!forwardClear && !reverseClear)
            {
                if (forwardRefusal == BanditTravelRefusal.Slope)
                {
                    _turningReverse = false;
                }
                else if (reverseRefusal == BanditTravelRefusal.Slope)
                {
                    _turningReverse = true;
                }
            }

            reverse = _turningReverse;
            bool legOpen = reverse ? reverseClear : forwardClear;

            // The soft refusal above: this leg is shut only because of the climb test, and both are
            // shut, so it is driven anyway rather than sat on.
            if (!legOpen && (reverse ? reverseRefusal : forwardRefusal) == BanditTravelRefusal.Slope)
            {
                legOpen = true;
            }

            // Wheels toward the target going forward; the opposite lock going backward, which is
            // what actually swings the nose the same way in reverse.
            int toward = absError < SteeringDeadbandDegrees ? 0 : (headingError > 0f ? 1 : -1);
            steer = reverse ? -toward : toward;

            float along = reverse ? -forwardVelocity : forwardVelocity;

            if (!legOpen)
            {
                // Both ways out are refused by something that is not going to be argued with. Hold,
                // brake, and let the stall timer end the trip - it now has the two refusals to say
                // so with.
                steer = 0;
                throttle = 0;
                brake = true;
                return;
            }

            // A crawl - a turn in a confined space is not a place for speed - and braked down to it
            // if we are somehow going faster.
            if (along > CreepMetresPerSecond)
            {
                throttle = 0;
                brake = true;
            }
            else
            {
                throttle = reverse ? -1 : 1;
                brake = false;
            }
        }

        /// <summary>
        /// Whether a point is on (or close enough to) a road, by the graph's own idea of where the
        /// roads are and how wide they are.
        ///
        /// When there is no road within reach the restriction is lifted rather than enforced - off
        /// the graph's coverage, "stay on the road" has no meaning and would only freeze the
        /// vehicle. On the road, the tolerance is the carriageway half-width plus a shoulder.
        /// </summary>
        private static bool IsOnRoad(Vector3 point)
        {
            if (!BanditRoadGraph.TryGetNearest(point, OnRoadSearchMetres, out int nodeIndex, out float distance))
            {
                return true; // no road here to be off of
            }

            BanditRoadGraph.RoadNode node = BanditRoadGraph.Get(nodeIndex);
            float half = node != null ? node.HalfWidth : 4f;

            // A junction is wide open - the middle of a crossroads is drivable in every direction,
            // not just along one street's centre line - so a node where several roads meet gets a
            // much larger allowance, which is what lets a vehicle cut across an intersection off to
            // one side instead of reversing to line up on the crown.
            float margin = node != null && node.Links.Count > 2
                ? JunctionRoadMarginMetres
                : OnRoadMarginMetres;

            return distance <= half + margin;
        }

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
                // heading error crosses the threshold and never actually goes anywhere - but sticky
                // with an end to it. See ReverseHopSeconds.
                if (Time.time >= _reverseUntil
                    || forwardError < ResumeForwardDegrees
                    || remaining > ReverseHopMetres * 1.5f)
                {
                    _reversing = false;
                    _reverseCooldownUntil = Time.time + ReverseCooldownSeconds;
                    BanditNavLog.Write(this, $"out of reverse - {forwardError:0}deg off, {remaining:0.0}m out");
                }
            }
            else if (Time.time >= _reverseCooldownUntil
                && forwardError > ReverseBehindDegrees
                && remaining <= ReverseHopMetres)
            {
                _reversing = true;
                _reverseUntil = Time.time + ReverseHopSeconds;
                BanditNavLog.Write(this, $"reverse hop - target {forwardError:0}deg behind, {remaining:0.0}m out");
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
            // A vehicle that is being held on purpose is not a vehicle that is stuck. SpeedScale at
            // or near zero is the convoy holding its interval, crawling under contact, or waiting
            // for its men - and in every one of those cases the vehicle is doing exactly as it was
            // told. Letting the stall timer run through it meant the follower in a nose-to-tail
            // column decided it was wedged, banned the direction it was "failing" in, and reversed
            // into whatever was behind it. Which is the vehicle behind it.
            if (SpeedScale <= HeldSpeedScale)
            {
                _lastProgressTime = Time.time;
                _trafficHoldUntil = 0f;
                return;
            }

            // Held up by another vehicle rather than by the scenery. Give it a while to move off
            // before treating this as being stuck - see TrafficPatienceSeconds.
            if (_navigator.BlockedByVehicle)
            {
                if (_trafficHoldUntil <= 0f)
                {
                    _trafficHoldUntil = Time.time + TrafficPatienceSeconds;
                }

                if (Time.time < _trafficHoldUntil)
                {
                    _lastProgressTime = Time.time;
                    return;
                }
            }
            else
            {
                _trafficHoldUntil = 0f;
            }

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
                BanditNavLog.Write(this, $"giving up - {_unstickAttempts} unstick attempt(s) and still "
                    + $"{remaining:0.0}m out; blocked by {_navigator.ObstacleAhead ?? "nothing it can name"}"
                    + (_turning && _turnRefusals != null ? $"; mid-turn, {_turnRefusals}" : string.Empty));
                _navigator.GiveUp();
                return;
            }

            _unstickAttempts++;
            _unstickUntil = Time.time + UnstickReverseSeconds;
            _lastProgressTime = Time.time; // the reverse gets its own window before it is judged
            _bestRemaining = remaining;

            BanditNavLog.Write(this, $"stalled at {remaining:0.0}m out - backing up "
                + $"({_unstickAttempts}/{MaxUnstickAttempts}); ahead: "
                + $"{_navigator.ObstacleAhead ?? "clear"}, refused: {_navigator.RefusedReason ?? "nothing"}"
                + (_turning && _turnRefusals != null ? $", mid-turn {_turnRefusals}" : string.Empty));

            // The route's own direction is not banned for traffic. Whatever is in front is going the
            // same way and will move; forbidding a fifty-degree arc around the road for it is how a
            // vehicle that only needed to wait ends up in the field beside the road. It still backs
            // off, which is what breaks a nose-to-tail deadlock, but it comes back to the same
            // heading afterwards.
            if (!_navigator.BlockedByVehicle)
            {
                _navigator.BanDirection(_lastTravelDirection, BanFailedDirectionSeconds);
            }
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
            //
            // The multiplier is the asset's own Speed_Max, because TargetForwardVelocity is that
            // times 1.25 - so a bandit now drives an Offroader at the 12.5 m/s the Offroader is
            // meant to do, rather than the 7.8 it was doing at half. Half was left over from not
            // having worked the validation ceiling out: sqrt(sqrDelta) is a *per-packet* distance,
            // so the speed the server will accept is sqrt(sqrDelta) / PlayerInput.RATE, which is
            // TargetForwardVelocity * 1.25. Speed_Max is under two thirds of that, and the
            // per-packet clamp in MaxHorizontalStep still has the last word either way.
            float assetSpeed = vehicle.asset != null ? vehicle.asset.TargetForwardVelocity : 0f;
            return Mathf.Clamp(assetSpeed * 0.8f, MinCruiseMetresPerSecond, MaxCruiseMetresPerSecond);
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

            Vector3 origin = EyeOf(_self);

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
                : EyeOf(_self);

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
                float measured = vehicleTransform.position.y - ground.y;

                // Believed only when it agrees with the body's own geometry, which is the fix for
                // the floating. A vehicle is calibrated the moment it is given a destination, and
                // for a convoy that is the tick after it spawned - and it spawns a metre and a half
                // up so that it settles onto the ground rather than being launched out of ground it
                // was overlapping. Measured in mid-drop, "how far above the ground am I" is the
                // drop and not the ride height, and clamping that into range quietly handed back
                // the top of the range every time. So the column drove the whole route half a metre
                // in the air. Out of range now means the vehicle is not resting on anything, and
                // the measurement is discarded in favour of the geometry.
                if (Mathf.Abs(measured - fromFootprint) <= RideHeightToleranceMetres)
                {
                    _rideHeight = Mathf.Max(0f, measured);
                    return;
                }
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
