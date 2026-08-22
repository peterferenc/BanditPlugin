using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.BanditGeometry;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// The box a vehicle actually occupies, measured in its own local space.
    ///
    /// This is the whole reason vehicle pathing is not on-foot pathing with a bigger number: a
    /// bandit is a 0.4m capsule that can sidestep and jump, and an APC is a 3x7m box that can do
    /// neither. Every clearance test below is this box, not a radius.
    ///
    /// Measured from the colliders rather than the asset, because the asset has no single size
    /// field and the colliders are what the world will actually stop against.
    /// </summary>
    public struct BanditVehicleFootprint
    {
        /// <summary>Centre of the body box, in vehicle-local space.</summary>
        public Vector3 LocalCentre;

        /// <summary>x is half the width, y half the height, z half the length.</summary>
        public Vector3 HalfExtents;

        /// <summary>Local y of the underside - where the wheels are, and what sits on the ground.</summary>
        public float LocalBottom;

        public float HalfWidth => HalfExtents.x;
        public float HalfLength => HalfExtents.z;

        /// <summary>How far the vehicle's origin sits above its underside. Ground snapping puts the
        /// origin this far above whatever it finds, so the body lands on the surface rather than
        /// half inside it.</summary>
        public float RideHeight => -LocalBottom;

        /// <summary>
        /// A body-sized box for a vehicle with no usable colliders. Deliberately car-sized: too
        /// small and a bandit drives a tank through a gate it does not fit in, which is the failure
        /// this whole struct exists to prevent.
        /// </summary>
        public static BanditVehicleFootprint Default => new BanditVehicleFootprint
        {
            LocalCentre = new Vector3(0f, 0.9f, 0f),
            HalfExtents = new Vector3(1.2f, 0.9f, 2.5f),
            LocalBottom = 0f
        };

        /// <summary>
        /// A collider this thin in two of its three dimensions is a pole, an aerial or a gun barrel,
        /// and it is left out of the box.
        ///
        /// Not cosmetic tidying: the box is what every clearance test sweeps, and a Stryker measured
        /// 4.8m wide by 10.4m long - against a real 2.7 by 7 - because its aerials and its barrel
        /// were in it. That box does not fit in a lane, so the vehicle spent its life refusing
        /// headings, and it made the arrival radius (which is derived from the length) so large that
        /// it stopped a car's length short of everywhere it was sent. Nothing that thin is going to
        /// stop a six-wheeled armoured car, and pretending otherwise made the vehicle undrivable.
        /// </summary>
        private const float ThinColliderMetres = 0.3f;

        public static BanditVehicleFootprint Measure(InteractableVehicle vehicle)
        {
            if (vehicle == null)
            {
                return Default;
            }

            Transform root = vehicle.transform;
            Collider[] colliders = vehicle.GetComponentsInChildren<Collider>(includeInactive: false);

            bool any = false;
            Bounds local = new Bounds();

            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                if (!TryGetLocalGeometry(collider, out Bounds geometry))
                {
                    continue;
                }

                if (IsThin(geometry.size))
                {
                    BanditNavLog.Write($"footprint {(vehicle.asset != null ? vehicle.asset.FriendlyName : "vehicle")}",
                        $"ignoring thin collider '{collider.name}' {geometry.size.x:0.00}x"
                        + $"{geometry.size.y:0.00}x{geometry.size.z:0.00}");
                    continue;
                }

                // The collider's own geometry, carried into vehicle space through its own transform.
                //
                // Emphatically NOT collider.bounds: that is a world-space AABB, and folding its
                // corners into vehicle space inflates the result by however much the vehicle happens
                // to be rotated. A tank sitting at 45 degrees measured about 40% too wide and, worse,
                // reported an underside well below its real one - which the drive step reads as ride
                // height, so the tank drove half a metre in the air and glided over everything. It
                // only showed on some vehicles because a vehicle parked square to the world axes
                // measures correctly.
                Vector3 min = geometry.min;
                Vector3 max = geometry.max;

                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 point = new Vector3(
                        (corner & 1) == 0 ? min.x : max.x,
                        (corner & 2) == 0 ? min.y : max.y,
                        (corner & 4) == 0 ? min.z : max.z);

                    Vector3 localPoint = root.InverseTransformPoint(collider.transform.TransformPoint(point));
                    if (!any)
                    {
                        local = new Bounds(localPoint, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        local.Encapsulate(localPoint);
                    }
                }
            }

            if (!any)
            {
                return Default;
            }

            BanditNavLog.Write($"footprint {(vehicle.asset != null ? vehicle.asset.FriendlyName : "vehicle")}",
                $"{local.size.x:0.00}m wide, {local.size.z:0.00}m long, {local.size.y:0.00}m tall; "
                + $"underside {local.min.y:0.00}, centre ({local.center.x:0.00}, {local.center.y:0.00}, "
                + $"{local.center.z:0.00})");

            return new BanditVehicleFootprint
            {
                LocalCentre = local.center,
                HalfExtents = local.extents,
                LocalBottom = local.min.y
            };
        }

        /// <summary>Whether a collider is a pole rather than part of the hull - thin in its two
        /// smallest dimensions. See <see cref="ThinColliderMetres"/>.</summary>
        private static bool IsThin(Vector3 size)
        {
            float smallest = Mathf.Min(size.x, Mathf.Min(size.y, size.z));
            float largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            float middle = size.x + size.y + size.z - smallest - largest;

            return smallest < ThinColliderMetres && middle < ThinColliderMetres;
        }

        /// <summary>
        /// A collider's shape in its own local space, which is the only form that survives being
        /// rotated into somebody else's.
        ///
        /// Handled per type because there is no general accessor for it - Collider only offers the
        /// world AABB. Anything unrecognised is skipped rather than guessed at: a vehicle has plenty
        /// of colliders, and one silently over-sized box would put the whole footprint wrong.
        /// </summary>
        private static bool TryGetLocalGeometry(Collider collider, out Bounds bounds)
        {
            switch (collider)
            {
                case BoxCollider box:
                    bounds = new Bounds(box.center, box.size);
                    return true;

                case SphereCollider sphere:
                    bounds = new Bounds(sphere.center, Vector3.one * (sphere.radius * 2f));
                    return true;

                case CapsuleCollider capsule:
                {
                    float diameter = capsule.radius * 2f;
                    Vector3 size = new Vector3(diameter, diameter, diameter);

                    // direction: 0 = x, 1 = y, 2 = z
                    if (capsule.direction == 0) size.x = Mathf.Max(capsule.height, diameter);
                    else if (capsule.direction == 1) size.y = Mathf.Max(capsule.height, diameter);
                    else size.z = Mathf.Max(capsule.height, diameter);

                    bounds = new Bounds(capsule.center, size);
                    return true;
                }

                case MeshCollider mesh when mesh.sharedMesh != null:
                    bounds = mesh.sharedMesh.bounds;
                    return true;

                case WheelCollider wheel:
                    // The tyre, at the position the suspension holds it. Its underside is what the
                    // vehicle actually rests on, so this is the collider that sets the ride height.
                    bounds = new Bounds(wheel.center, Vector3.one * (wheel.radius * 2f));
                    return true;

                default:
                    bounds = default;
                    return false;
            }
        }
    }

    /// <summary>
    /// Ground sampling shared by the navigator and the driving step.
    /// </summary>
    public static class VehicleTerrain
    {
        /// <summary>
        /// What a vehicle can rest on: terrain, roads and bridges (objects), and player structures.
        /// Not other vehicles - a bandit should drive around a parked truck, not up onto it - and
        /// not trees, which are obstacles rather than surfaces.
        /// </summary>
        public const int GroundMask = RayMasks.GROUND | RayMasks.GROUND2 | RayMasks.LARGE
            | RayMasks.MEDIUM | RayMasks.STRUCTURE | RayMasks.BARRICADE | RayMasks.ENVIRONMENT;

        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        /// <summary>
        /// Finds the surface under a point, ignoring anything belonging to <paramref name="ignoreRoot"/>.
        ///
        /// The ray starts well above the point rather than at it, because the position being tested
        /// is usually where the vehicle is *about* to be, which may be a metre into a rise.
        /// </summary>
        public static bool TrySample(Vector3 position, Transform ignoreRoot, out Vector3 point, out Vector3 normal)
        {
            point = position;
            normal = Vector3.up;

            const float startHeight = 6f;
            const float probeLength = 24f;

            Ray ray = new Ray(position + Vector3.up * startHeight, Vector3.down);
            int count = Physics.RaycastNonAlloc(ray, Hits, probeLength, GroundMask, QueryTriggerInteraction.Ignore);

            float bestDistance = float.MaxValue;
            bool found = false;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                if (hit.transform == null || (ignoreRoot != null && hit.transform.IsChildOf(ignoreRoot)))
                {
                    continue;
                }

                if (hit.distance < bestDistance)
                {
                    bestDistance = hit.distance;
                    point = hit.point;
                    normal = hit.normal;
                    found = true;
                }
            }

            return found;
        }
    }

    /// <summary>
    /// Turns "drive this vehicle to that point" into a world-space heading, and says when the way
    /// ahead is too tight to take.
    ///
    /// The routing half is the same trick the on-foot navigator uses - the server's own A*
    /// (AstarPathfindingProject, one RecastGraph per Nav volume) for the corner list, straight
    /// steering when either end is off the mesh, because the navmesh only covers the towns.
    ///
    /// The steering half is not, and cannot be. That navmesh was baked for a walking zombie: it
    /// says nothing about whether a 3m-wide truck fits between two trees on a corner it is happy to
    /// route a person through. So every heading is tested by sweeping the vehicle's actual width
    /// through it before it is used, and if none of the fan is clear the vehicle stops rather than
    /// grinding into the gap. That is the honest answer to "it might not fit": a bandit that gets
    /// as close as the vehicle can go and reports being stuck beats one that wedges itself in a
    /// doorway and keeps pushing.
    /// </summary>
    public sealed class BanditVehicleNavigator
    {
        public float RepathIntervalSeconds
        {
            get { return _path.RepathIntervalSeconds; }
            set { _path.RepathIntervalSeconds = value; }
        }

        /// <summary>
        /// How far a point may be from the navmesh and still snap onto it. Looser than the on-foot
        /// 3m: roads are frequently at the very edge of a Nav volume, or just outside one, and a
        /// vehicle that refuses to path because its start point is 4m off the mesh would steer
        /// blind through the one place pathing is most useful.
        /// </summary>
        public float NavmeshSnapDistance
        {
            get { return _path.NavmeshSnapDistance; }
            set { _path.NavmeshSnapDistance = value; }
        }

        /// <summary>World-space unit vector on the XZ plane, or zero when there is nowhere safe to go.</summary>
        public Vector3 DesiredDirection { get; private set; }

        public Vector3 Destination { get; private set; }
        public bool HasDestination { get; private set; }

        /// <summary>Latched on arrival; the driver reads and clears it.</summary>
        public bool HasArrived { get; private set; }

        /// <summary>Latched when the vehicle has been wedged long enough to stop trying.</summary>
        public bool HasGivenUp { get; private set; }

        /// <summary>True while every heading in the fan is blocked - i.e. it does not fit.</summary>
        public bool IsBlocked { get; private set; }

        /// <summary>
        /// How much clear road there is along the heading it settled on, out to
        /// <see cref="ScanAheadMetres"/>.
        ///
        /// The clearance sweep only ever answered yes or no, and at the range it answers over -
        /// seven metres - "no" arrives about half a second before the impact does. That is why
        /// vehicles read as ignoring each other: by the time anything in front registered, the only
        /// available responses were a swerve at full speed or a collision. A distance can be driven
        /// to instead: the driver slows so it could stop in it, which is following a vehicle down a
        /// road, and it keeps enough time in hand for the fan to find a way past.
        /// </summary>
        public float ClearAheadMetres { get; private set; } = float.MaxValue;

        /// <summary>
        /// Whether the nearest thing along that heading is another vehicle rather than scenery.
        ///
        /// The difference decides what patience means. Traffic clears - the vehicle in front is
        /// going the same way and will move - so sitting behind it is following, not being stuck,
        /// and the driver's stall detector must not read it as a reason to reverse into whoever is
        /// behind. A wall does not clear, and being stopped by one is exactly what that detector is
        /// for.
        /// </summary>
        public bool BlockedByVehicle { get; private set; }

        /// <summary>
        /// What the nearest thing along the chosen heading actually is, by name, and what refused
        /// the heading the route wanted.
        ///
        /// Diagnostics only, and worth the two strings: "could not get through" on an empty road is
        /// unanswerable without them. Everything this class decides is a boxcast against a mask, and
        /// the difference between a fence, a road mesh, the vehicle in front and a clip volume
        /// nobody can see is exactly the difference between a bug and correct behaviour.
        /// </summary>
        public string ObstacleAhead { get; private set; }

        /// <summary>Why the heading the route wanted was refused, or null if it was taken.</summary>
        public string RefusedReason { get; private set; }

        /// <summary>How far off the wanted heading the fan had to go to find room, in degrees.</summary>
        public float AvoidanceDegrees { get; private set; }

        public bool IsFollowingPath => _path.HasPath;

        /// <summary>How close counts as arrived. Scaled to the vehicle, because a bandit can walk
        /// onto a point and a lorry can only get its nose near it.</summary>
        public float ArriveRadius { get; private set; } = 6f;

        public float RemainingDistance
        {
            get
            {
                if (!HasDestination)
                {
                    return 0f;
                }

                return _path.RemainingDistance(_lastPosition, Destination);
            }
        }

        /// <summary>
        /// How far above the vehicle's underside the probe starts. Above kerbs, ruts and the lip of
        /// a road - roads and bridges are objects on the same layers as obstacles, so a probe that
        /// reached the ground would read the road ahead as a wall.
        /// </summary>
        private const float ProbeFloorClearance = 0.35f;

        // The body's own box, swept along the candidate heading, oriented as the vehicle is.
        //
        // It was a thin plate at the body centre, cast for HalfLength + lookahead - an approximation
        // that is only correct when the heading is the vehicle's own forward, and that has a much
        // worse problem than inaccuracy: the first HalfLength of the cast is *inside the vehicle*.
        // Anything the body is currently straddling is therefore an obstacle in front of it. The
        // log caught it exactly - "refused: 'Road_Line_0' at 0.9m" on a Stryker whose nose is five
        // metres from its centre: it was refusing to move because of the road it was parked on.
        //
        // Sweeping the real box for the real distance says what was meant all along: how far the
        // vehicle can travel this way before any part of it touches anything. Distance zero then
        // means genuinely overlapping, which is the one case worth special handling.
        private const float MinProbeHalfHeight = 0.35f;

        /// <summary>How far ahead to look for something in the way, at minimum. Longer for a long
        /// vehicle, because it needs the room to swing.</summary>
        private const float MinLookaheadMetres = 7f;

        /// <summary>
        /// How far ahead, in seconds of travel, the blocking sweep reaches at speed.
        ///
        /// A fixed seven metres is a fifth of a second at road speed, which is not a reaction, it is
        /// a report of what has already happened. Distance that scales with speed means the vehicle
        /// commits to going round something while going round it is still a steering input rather
        /// than a swerve.
        ///
        /// Kept modest, and capped, because this sweep is a straight line and a road is not: look
        /// far enough down a bend and the tree on the outside of it is "in the way", and the vehicle
        /// steers off the road to avoid something it was never going to hit. Seeing further than
        /// this is the distance probe's job, and that one only chooses a speed.
        /// </summary>
        private const float LookaheadSeconds = 0.6f;
        private const float MaxLookaheadMetres = 12f;

        /// <summary>How far the distance probe reaches. Only for slowing down, so it can afford to
        /// see much further than the blocking sweep does.</summary>
        private const float ScanAheadMetres = 30f;

        /// <summary>Steeper than this counts as a wall rather than a hill.</summary>
        private const float MaxClimbDegrees = 35f;

        /// <summary>How much water a vehicle may drive through. Deeper than this is not ground,
        /// whatever the ground sample under it says.</summary>
        private const float MaxFordDepthMetres = 1f;

        /// <summary>
        /// How far apart the ground is sampled along a step being tested.
        ///
        /// This is what separates a hill from the side of a building, and the figure is chosen from
        /// that: at two metres, anything rising more than about 1.4m in one sample is refused, which
        /// is every wall and no kerb.
        /// </summary>
        private const float GroundSampleSpacingMetres = 2f;

        /// <summary>
        /// Trees, rocks, buildings, player builds and other vehicles. Not terrain: a rise in the
        /// ground is a climb test, not an obstacle, or every hill reads as a wall.
        ///
        /// CLIP is the one that matters and the one that was missing. Unturned objects carry their
        /// collision on separate clip volumes rather than on the visible mesh, and vanilla's own
        /// BLOCK_COLLISION includes that layer - so a fence, a railing or a barrier can be perfectly
        /// solid to everything in the game and completely invisible to a sweep that only looks at
        /// LARGE and MEDIUM. That is why thin things were being driven straight through.
        ///
        /// SMALL is still left out, and stays out: it is not in BLOCK_COLLISION either, so it does
        /// not stop players or vehicles in vanilla, and a truck should drive through a bush.
        /// </summary>
        private const int ObstacleMask = RayMasks.LARGE | RayMasks.MEDIUM | RayMasks.RESOURCE
            | RayMasks.STRUCTURE | RayMasks.BARRICADE | RayMasks.VEHICLE | RayMasks.CLIP;

        /// <summary>
        /// How far from vertical a surface's normal may be and still count as something to drive
        /// onto rather than something to stop in front of.
        ///
        /// This is the difference between a road and a wall, and without it there was none. Roads in
        /// Unturned are objects, on the same layers as buildings - the road mesh is Segment_n on
        /// Environment, and the surface pieces are Road_Line_n, Road_Tee_n and friends on Large -
        /// so a sweep looking for buildings finds the road it is driving along. It only shows where
        /// the surface ahead rises into the plate: a crest, a tee, the join between two segments, or
        /// a vehicle sitting nose-down. Which is exactly the "they get off the road at a segment
        /// transition", "the stryker stopped on an empty road" and "they wobble" reports, and the
        /// log bears it out - Road_Line_0 was the single most common thing refusing a heading, 243
        /// times in one convoy.
        ///
        /// The rule that fixes it is the same division of labour the mask already implies: this
        /// sweep refuses *walls*, and whether the vehicle can get up a *surface* is the slope test's
        /// business. A surface whose normal is within the climb limit is therefore not an obstacle
        /// here, whatever layer it is on. Environment comes out of the mask entirely, since it holds
        /// roads and bridges and VehicleTerrain.GroundMask already treats it as ground - a layer
        /// cannot sensibly be a surface to the ground probe and a wall to the sweep.
        /// </summary>
        private static readonly float MaxClimbNormalY = Mathf.Cos(MaxClimbDegrees * Mathf.Deg2Rad);

        // Wider fan than the on-foot navigator's 35/65/90: a vehicle cannot strafe, so a detour it
        // can actually drive has to start as a turn it can actually make.
        private static readonly float[] AvoidanceAngles = { 20f, 40f, 60f, 80f };

        /// <summary>How wide the arc around a banned direction is. Wide enough that "go round it"
        /// means a genuinely different way, narrow enough to leave the fan somewhere to go.</summary>
        private const float BannedArcDegrees = 50f;

        private static readonly RaycastHit[] Hits = new RaycastHit[32];

        private readonly Player _self;
        private readonly BanditPathFollower _path;

        private Vector3 _lastPosition;

        private int _avoidSign;
        private float _avoidSignExpiry;

        /// <summary>How far the blocking sweep reaches this tick. Set from the speed the driver
        /// reports, so a stopped vehicle looks a body length ahead and a moving one looks as far as
        /// it will travel in <see cref="LookaheadSeconds"/>.</summary>
        private float _lookaheadMetres = MinLookaheadMetres;

        /// <summary>The last thing a sweep refused a heading for, for the diagnostics.</summary>
        private string _lastBlocker;

        private Vector3 _bannedDirection;
        private float _bannedUntil;

        public BanditVehicleNavigator(Player self)
        {
            _self = self;

            // Four metres rather than the on-foot 1.5: a lorry that has to touch each corner
            // weaves along a route a bandit walks straight down.
            _path = new BanditPathFollower(self) { NavmeshSnapDistance = 8f, CornerArriveRadius = 4f };
            _lastPosition = self != null ? self.transform.position : Vector3.zero;
        }

        public void SetDestination(Vector3 destination, float arriveRadius)
        {
            Destination = destination;
            ArriveRadius = Mathf.Max(2f, arriveRadius);
            HasDestination = true;
            HasArrived = false;
            HasGivenUp = false;
            IsBlocked = false;
            _path.Restart();
            _bannedUntil = 0f;
        }

        /// <summary>
        /// Ends the trip, reporting it as abandoned rather than arrived.
        ///
        /// The decision belongs to the driver now, not here: "stuck" means the vehicle stopped
        /// getting closer to where it was sent, which is a fact about the route, and this class only
        /// ever sees one packet's heading. Whether the vehicle moved at all is beside the point - it
        /// can grind along a wall all day without ever arriving.
        /// </summary>
        public void GiveUp()
        {
            HasGivenUp = true;
            Stop();
        }

        /// <summary>
        /// Refuses to steer anywhere near this world direction for a while, and throws away the
        /// current route.
        ///
        /// Called by the driver when a trip has stopped making progress: whatever is out that way
        /// has already been proven not to work, so the point of backing up is to try a *different*
        /// way, not to take another run at the same one. The ban is on a world direction rather than
        /// on a place, which is what makes it survive the vehicle turning round to reverse.
        /// </summary>
        public void BanDirection(Vector3 direction, float seconds)
        {
            direction.y = 0f;
            if (direction.sqrMagnitude < 0.0001f)
            {
                return;
            }

            _bannedDirection = direction.normalized;
            _bannedUntil = Time.time + seconds;
            ForceRepath();
        }

        /// <summary>Drops the current route and asks for a fresh one on the next tick.</summary>
        public void ForceRepath()
        {
            _path.DropRoute();
        }

        private bool IsBanned(Vector3 heading)
        {
            return Time.time < _bannedUntil
                && Vector3.Angle(heading, _bannedDirection) < BannedArcDegrees;
        }

        public void Stop()
        {
            HasDestination = false;
            IsBlocked = false;
            ClearAheadMetres = float.MaxValue;
            BlockedByVehicle = false;
            _path.Abandon();
            DesiredDirection = Vector3.zero;
        }

        public bool ConsumeArrived()
        {
            bool arrived = HasArrived;
            HasArrived = false;
            return arrived;
        }

        public bool ConsumeGaveUp()
        {
            bool gaveUp = HasGivenUp;
            HasGivenUp = false;
            return gaveUp;
        }

        /// <param name="speed">
        /// How fast the vehicle is actually travelling, which is what sets how far ahead this looks.
        /// Zero is a safe default and means "look a body length ahead", which is what a stopped
        /// vehicle needs.
        /// </param>
        public void Tick(InteractableVehicle vehicle, BanditVehicleFootprint footprint, float deltaTime,
            float speed = 0f)
        {
            DesiredDirection = Vector3.zero;

            if (vehicle == null || !HasDestination)
            {
                ClearAheadMetres = float.MaxValue;
                return;
            }

            _lookaheadMetres = Mathf.Max(MinLookaheadMetres,
                Mathf.Min(MaxLookaheadMetres, Mathf.Abs(speed) * LookaheadSeconds));

            Vector3 position = vehicle.transform.position;
            _lastPosition = position;

            if (FlatDistance(position, Destination) <= ArriveRadius)
            {
                HasArrived = true;
                Stop();
                return;
            }

            _path.Refresh(position, Destination);

            Vector3 steerTarget = _path.SteerTarget(position, Destination);

            Vector3 desired = Flatten(steerTarget - position);
            if (desired.sqrMagnitude < 0.0001f)
            {
                desired = Flatten(Destination - position);
            }
            if (desired.sqrMagnitude < 0.0001f)
            {
                return;
            }
            desired.Normalize();

            DesiredDirection = ChooseClearHeading(vehicle, footprint, desired);

            // Measured along the heading it settled on rather than along the one it wanted: a
            // vehicle that has already committed to going round something should be pacing itself
            // against the way it is actually going.
            if (DesiredDirection.sqrMagnitude > 0.0001f)
            {
                ClearAheadMetres = MeasureClearAhead(vehicle, footprint, DesiredDirection);
            }
            else
            {
                ClearAheadMetres = 0f;
                BlockedByVehicle = false;
                ObstacleAhead = null;
            }
        }

        /// <summary>
        /// How far the body could travel along a heading before it touched something.
        ///
        /// The same plate the blocking sweep uses, cast much further and read for its distance
        /// instead of its yes-or-no. Only ever used to choose a speed, so seeing a long way is
        /// cheap: an obstacle thirty metres off sets a limit far above anything the vehicle would
        /// have driven at anyway, and the limit only bites once it is close.
        /// </summary>
        private float MeasureClearAhead(InteractableVehicle vehicle, BanditVehicleFootprint footprint,
            Vector3 heading)
        {
            Transform root = vehicle.transform;
            int count = SweepBody(vehicle, footprint, heading, ScanAheadMetres);

            float nearest = ScanAheadMetres;
            Transform nearestHit = null;

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                if (hit.transform == null || hit.transform.IsChildOf(root))
                {
                    continue;
                }

                // Distances are measured from the leading face of the plate, which already sits at
                // the body's centre - so the nose is HalfLength ahead of it and that has to come off
                // before this is a length of clear road.
                if (IsDrivableSurface(hit))
                {
                    continue;
                }

                float clear = Mathf.Max(0f, hit.distance);
                if (clear < nearest)
                {
                    nearest = clear;
                    nearestHit = hit.transform;
                }
            }

            // Asked once, of the nearest hit only: what a vehicle wants to know is what is stopping
            // it, not an inventory of everything down the road.
            InteractableVehicle blocker = nearestHit != null
                ? nearestHit.GetComponentInParent<InteractableVehicle>()
                : null;

            BlockedByVehicle = blocker != null;
            ObstacleAhead = nearestHit == null
                ? null
                : $"{Describe(nearestHit)} at {nearest:0.0}m";

            return nearest;
        }

        /// <summary>
        /// Whether this contact is a surface the wheels would ride up rather than a wall they would
        /// stop against. See <see cref="MaxClimbNormalY"/>.
        ///
        /// A zero-distance hit is excluded because a sweep that starts inside a collider reports a
        /// meaningless normal, and something the body is already touching is not something to drive
        /// onto in any case.
        /// </summary>
        private static bool IsDrivableSurface(RaycastHit hit)
        {
            return hit.distance > 0.01f && hit.normal.y >= MaxClimbNormalY;
        }

        /// <summary>A collider named usefully: the vehicle it belongs to if it is one, otherwise its
        /// own name and the layer it is on, since a clip volume's name rarely says what it is.</summary>
        private static string Describe(Transform hit)
        {
            InteractableVehicle vehicle = hit.GetComponentInParent<InteractableVehicle>();
            if (vehicle != null)
            {
                return $"vehicle '{(vehicle.asset != null ? vehicle.asset.FriendlyName : hit.name)}'";
            }

            return $"'{hit.name}' (layer {LayerMask.LayerToName(hit.gameObject.layer)})";
        }

        /// <summary>
        /// The wanted heading if the vehicle fits through it, otherwise the nearest one in the fan
        /// that it does. Zero when nothing does - which stops the vehicle rather than shoving it
        /// into the gap, and is what /banditvgoto reports as "it doesn't fit".
        /// </summary>
        private Vector3 ChooseClearHeading(InteractableVehicle vehicle, BanditVehicleFootprint footprint, Vector3 desired)
        {
            bool banned = IsBanned(desired);

            if (!banned && IsHeadingClear(vehicle, footprint, desired, ignoreOverlap: false))
            {
                if (Time.time > _avoidSignExpiry)
                {
                    _avoidSign = 0;
                }

                IsBlocked = false;
                RefusedReason = null;
                AvoidanceDegrees = 0f;
                return desired;
            }

            RefusedReason = banned
                ? $"banned direction ({(_bannedUntil - Time.time):0.0}s left)"
                : _lastBlocker ?? "no ground / too steep";

            // Keep turning the same way around an obstacle for a moment, or a vehicle sitting in
            // front of a wall picks left and right on alternate packets and drives straight into it.
            int firstSign = _avoidSign != 0 && Time.time <= _avoidSignExpiry ? _avoidSign : 1;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                int sign = attempt == 0 ? firstSign : -firstSign;
                for (int i = 0; i < AvoidanceAngles.Length; i++)
                {
                    Vector3 candidate = Quaternion.Euler(0f, AvoidanceAngles[i] * sign, 0f) * desired;
                    if (!IsBanned(candidate) && IsHeadingClear(vehicle, footprint, candidate, ignoreOverlap: false))
                    {
                        _avoidSign = sign;
                        _avoidSignExpiry = Time.time + 2f;
                        IsBlocked = false;
                        AvoidanceDegrees = AvoidanceAngles[i] * sign;
                        return candidate;
                    }
                }
            }

            IsBlocked = true;
            AvoidanceDegrees = 0f;
            return Vector3.zero;
        }

        /// <summary>
        /// Sweeps a plate the width of the vehicle along a heading, and checks the ground it would
        /// climb on the way.
        ///
        /// Public because reversing needs it too: which way the vehicle is pointing has nothing to
        /// do with this test - it sweeps a world direction - so backing out of somewhere asks the
        /// same question about the space behind as driving forward asks about the space ahead.
        /// </summary>
        /// <param name="ignoreOverlap">
        /// Whether to forgive something the body is already touching. Backing out of a wall the
        /// vehicle has driven into has to be allowed, or the one manoeuvre that could free it is the
        /// one thing it refuses to do; choosing a heading to drive *into* must not be.
        /// </param>
        public bool IsTravelClear(InteractableVehicle vehicle, BanditVehicleFootprint footprint, Vector3 heading, bool ignoreOverlap = false)
        {
            return vehicle != null
                && heading.sqrMagnitude > 0.0001f
                && IsHeadingClear(vehicle, footprint, heading.normalized, ignoreOverlap);
        }

        private bool IsHeadingClear(InteractableVehicle vehicle, BanditVehicleFootprint footprint, Vector3 heading, bool ignoreOverlap)
        {
            Transform root = vehicle.transform;
            float lookahead = Mathf.Max(_lookaheadMetres, footprint.HalfLength * 2f);

            int count = SweepBody(vehicle, footprint, heading, lookahead);

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = Hits[i];
                if (hit.transform == null || hit.transform.IsChildOf(root))
                {
                    continue; // our own body, clip colliders and wheels
                }

                // A zero-distance hit is a collider the sweep started inside. That used to be
                // forgiven, which meant a vehicle pressed against something reported every direction
                // as clear and kept pushing. It is a wall, and the way out of it is reverse.
                if (ignoreOverlap && hit.distance <= 0.01f)
                {
                    continue;
                }

                if (IsDrivableSurface(hit))
                {
                    continue;
                }

                _lastBlocker = $"{Describe(hit.transform)} at {hit.distance:0.0}m";
                return false;
            }

            _lastBlocker = null;
            return IsGroundClimbable(vehicle, footprint, heading, lookahead, ignoreOverlap);
        }

        /// <summary>
        /// Sweeps the vehicle's own box along a heading and returns however many contacts it found
        /// in <see cref="Hits"/>. Hit distances are therefore metres of travel, not distances from
        /// some point inside the vehicle.
        ///
        /// Oriented with the vehicle rather than with the heading, because the body does not
        /// instantly rotate to face a heading it has only just chosen - the question being asked is
        /// "if this vehicle, as it is currently sitting, slid that way, what would it touch".
        /// </summary>
        private static int SweepBody(InteractableVehicle vehicle, BanditVehicleFootprint footprint,
            Vector3 heading, float distance)
        {
            Transform root = vehicle.transform;

            // The band the body really occupies, minus the bottom few centimetres so the road under
            // it is not mistaken for the wall in front of it.
            float bottom = footprint.LocalBottom + ProbeFloorClearance;
            float top = Mathf.Max(bottom + MinProbeHalfHeight * 2f,
                footprint.LocalCentre.y + footprint.HalfExtents.y);
            float halfHeight = (top - bottom) * 0.5f;

            Vector3 centre = root.TransformPoint(new Vector3(
                footprint.LocalCentre.x,
                bottom + halfHeight,
                footprint.LocalCentre.z));

            Vector3 halfExtents = new Vector3(footprint.HalfWidth, halfHeight, footprint.HalfLength);

            return Physics.BoxCastNonAlloc(centre, halfExtents, heading, Hits, root.rotation, distance,
                ObstacleMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Whether the ground a step ahead is a hill or a cliff. Terrain is deliberately kept out of
        /// the obstacle sweep - at bumper height every upslope would read as a wall - so this is the
        /// only thing stopping a bandit from driving up the side of a mountain.
        /// </summary>
        private static bool IsGroundClimbable(InteractableVehicle vehicle, BanditVehicleFootprint footprint,
            Vector3 heading, float lookahead, bool wedged = false)
        {
            Transform root = vehicle.transform;
            Vector3 here = root.position;
            float distance = Mathf.Min(lookahead, footprint.HalfLength + 4f);

            if (!VehicleTerrain.TrySample(here, root, out Vector3 previous, out Vector3 _))
            {
                return false;
            }

            // Walked in short steps rather than measured end to end, and that is the whole of it.
            // Comparing the ground here with the ground six metres ahead averages everything in
            // between, so the wall of a house - four metres of vertical brick - came out as a
            // thirty-four degree ramp and passed the climb test. Then the drive step's ground snap
            // did exactly what it was told and put the vehicle on the roof, which is the "they drive
            // up on houses" report. At two-metre steps the same wall is sixty-three degrees and
            // there is nothing to average it away with, while a kerb or a verge still reads as the
            // small step up it is.
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / GroundSampleSpacingMetres));

            for (int step = 1; step <= steps; step++)
            {
                Vector3 point = here + heading * (distance * step / steps);

                if (!VehicleTerrain.TrySample(point, root, out Vector3 ground, out Vector3 _))
                {
                    // No ground under this part of the step: a bridge edge, a cliff lip, or deep
                    // water. Not somewhere to drive.
                    return false;
                }

                // Water is a surface the ground sample says nothing about. Every step below snaps
                // the vehicle onto whatever is underneath it, and under water that is the seabed -
                // so without this test a column drives into the sea and keeps going along the
                // bottom of it, which is what it did. A ford is allowed, because a stream crossing
                // is somewhere vehicles do drive; anything deeper is not ground.
                if (WaterUtility.isPointUnderwater(ground + Vector3.up * MaxFordDepthMetres))
                {
                    return false;
                }

                // The slope test is dropped for a vehicle that is trying to get itself unwedged.
                // It is asking whether it may back out of something it is already touching, and the
                // ground right behind a vehicle that has beached itself on a fence post or a ditch
                // lip is exactly the ground this test calls a wall. Refusing there is how a vehicle
                // that could trivially reverse out ends up reporting itself boxed in and stopping.
                // The other two refusals stand: no ground at all is still a cliff edge, and deep
                // water is still deep water, and reversing into either is not a recovery.
                float run = FlatDistance(previous, ground);
                if (!wedged && run >= 0.01f
                    && Mathf.Atan2(ground.y - previous.y, run) * Mathf.Rad2Deg > MaxClimbDegrees)
                {
                    return false;
                }

                previous = ground;
            }

            return true;
        }
    }
}
