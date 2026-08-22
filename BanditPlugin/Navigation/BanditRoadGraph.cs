using System.Collections.Generic;
using SDG.Framework.Water;
using SDG.Unturned;
using UnityEngine;
using Logger = Rocket.Core.Logging.Logger;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// The map's roads, turned into something a vehicle can be routed along.
    ///
    /// This exists because the two pathfinders the plugin already has are both the wrong tool for
    /// driving any distance. The server's A* only covers the Nav volumes - the towns where zombies
    /// spawn - so a vehicle crossing the map between them is off-mesh for most of the trip and
    /// falls back to whisker steering, which is fine for the last hundred metres and useless for
    /// the first three kilometres. Steering straight at a destination, meanwhile, drives through
    /// forests and up hillsides because the shortest line between two towns is never the road.
    ///
    /// Roads are real level data and the server loads all of it. <see cref="LevelRoads.load"/> is
    /// called unconditionally from Level.init and reads Environment/Paths.dat into a list of
    /// <see cref="Road"/>, each one a cubic Bezier spline over its joints. None of the evaluation
    /// needs a mesh or a renderer, so it all works headless:
    ///
    ///     road.getPosition(index, t)   a point on the spline
    ///     road.getVelocity(index, t)   the tangent there, i.e. which way the road runs
    ///     road.getLengthEstimate(i)    how long that segment is
    ///
    /// What the game does *not* provide is any notion of a road network. Roads are independent
    /// splines with no connectivity, no junctions and no lane data - where two of them cross is
    /// implicit geometry and nothing more. So junctions are recovered here, by linking samples from
    /// different roads that are close enough to be the same piece of tarmac - and then the gaps
    /// between what that leaves are bridged, because on a real map it leaves a dozen disconnected
    /// islands of road and A* between two of them simply fails. See <see cref="BridgeGaps"/>.
    ///
    /// Nothing in here steers. It answers "which way round the road network" and hands back a list
    /// of points; <see cref="BanditVehicleNavigator"/> still does the driving, including every
    /// clearance sweep, because a road being on the map says nothing about the tree that fell
    /// across it.
    /// </summary>
    public static class BanditRoadGraph
    {
        /// <summary>
        /// Distance between samples along a road, in metres.
        ///
        /// This is the resolution of every corner the convoy will drive, so it is a compromise
        /// between a route that cuts bends and a graph with tens of thousands of nodes in it.
        /// Vanilla samples its own road meshes every 5m (Road.updateSamples); 8m is coarser than
        /// the mesh needs to look right and fine enough that a vehicle aimed at the next node is
        /// never aimed off the road.
        /// </summary>
        private const float SampleSpacingMetres = 8f;

        /// <summary>
        /// How near two samples on *different* roads have to be to count as the same junction.
        ///
        /// Derived per pair from the two roads' half-widths, since a motorway junction is
        /// physically wider than a dirt track crossing, and clamped so that neither a very narrow
        /// path nor an unusually wide modded road produces a nonsense figure.
        /// </summary>
        private const float MinJunctionRadiusMetres = 3f;
        private const float MaxJunctionRadiusMetres = 14f;

        /// <summary>How far apart in height two nodes may be and still be one junction. A road and
        /// the bridge crossing over it are closer than this in plan and much further in height, and
        /// linking them would route a convoy off the overpass.</summary>
        private const float MaxJunctionHeightMetres = 4f;

        /// <summary>
        /// How long a stretch of missing road may be before it is treated as two separate networks
        /// rather than one with a hole in it. See <see cref="BridgeGaps"/>.
        ///
        /// Two hundred and fifty metres sounds enormous for a "gap" and is not: PEI's two halves are
        /// one hundred and sixty-seven metres apart at the closest point their splines come to each
        /// other, and every convoy crossing the island depends on that crossing. The figure that
        /// keeps this honest is not the distance, it is the ground test each crossing has to pass.
        /// </summary>
        private const float MaxGapMetres = 250f;

        /// <summary>How often the ground under a candidate gap is checked, and what counts as
        /// crossable ground when it is.</summary>
        private const float GapSampleSpacingMetres = 8f;
        private const float MaxFordDepthMetres = 1f;
        private const float MaxGapClimbDegrees = 30f;

        /// <summary>How far above the terrain to look for road decking when testing a gap. Tall
        /// enough to find a bridge over a cutting, short enough not to find the next hill.</summary>
        private const float BridgeProbeHeightMetres = 30f;

        /// <summary>
        /// Cell size of the spatial hash used for nearest-node queries and junction detection.
        /// Comfortably larger than <see cref="MaxJunctionRadiusMetres"/> so a junction search only
        /// ever has to look at the nine cells around a node.
        /// </summary>
        private const float GridCellMetres = 16f;

        /// <summary>
        /// The name prefixes of the placed objects that make up a town's roads.
        ///
        /// This is the whole point of the object pass. Unturned has two unrelated road systems and
        /// the graph used to read only one of them: the spline network in LevelRoads, which is the
        /// rural highways, and nothing else. Town streets are built the other way - by hand-placing
        /// prefab tiles from Bundles/Objects/Large/Roads (Road_Line, Road_Turn, Road_Tee and the
        /// rest) - and those live in LevelObjects, not LevelRoads. On PEI that is 73 tiles the graph
        /// could not see; on Russia 149; and it is exactly why a convoy drove the countryside and
        /// then lost the road the moment it reached a town.
        ///
        /// Bridges and tunnels carry the road over and under things and are part of it. Sewers and
        /// docks are not roads a vehicle drives, and are left out - Germany alone has sixty-odd
        /// sewer tiles that would otherwise wire a phantom road network under the streets.
        /// </summary>
        private static readonly string[] RoadObjectPrefixes = { "Road_", "Bridge_", "Tunnel_", "Dam_" };

        /// <summary>Height prefixes that keep the object's own Y instead of being dropped onto the
        /// terrain - a bridge deck and a tunnel mouth are deliberately not at ground level.</summary>
        private static readonly string[] ElevatedObjectPrefixes = { "Bridge_", "Tunnel_" };

        /// <summary>
        /// Every tile node is tagged with this as its RoadIndex, so <see cref="LinkJunctions"/>
        /// leaves tile-to-tile pairs alone - their topology is built by <see cref="LinkTileNodes"/>
        /// from actual adjacency - while still stitching tiles to the spline roads they meet, which
        /// is a different-index pair and exactly what junction linking is for.
        /// </summary>
        private const int RoadObjectRoadIndex = -2;

        /// <summary>Extra slack added to two tiles' half-sizes when deciding whether they are
        /// neighbours. Enough to bridge the seam between two tiles laid edge to edge, small enough
        /// that diagonally-touching tiles at a crossroads are not wired to each other.</summary>
        private const float TileLinkMarginMetres = 2.5f;

        /// <summary>The most a single road tile is assumed to span, for the neighbour search radius.
        /// A backstop against a modded tile with an enormous stray collider dragging the search
        /// radius out to nothing useful.</summary>
        private const float MaxTileHalfSizeMetres = 14f;

        /// <summary>Default half-width for a town street node, when nothing better is measured. Four
        /// metres of half-width is an eight-metre carriageway, which is a two-lane street.</summary>
        private const float TileHalfWidthMetres = 4f;

        /// <summary>
        /// What a metre of each kind of road costs to drive, relative to a metre of motorway.
        ///
        /// This is what makes a convoy prefer the highway over the farm track running parallel to
        /// it, without any hand-tagging: <see cref="Road.GetChartMode"/> already classifies every
        /// road by width and surface for the map chart, and that classification is exactly the
        /// distinction wanted here. The penalties are deliberately mild - a dirt road that saves a
        /// kilometre is still the right answer.
        /// </summary>
        private static float ChartCostMultiplier(EObjectChart chart)
        {
            switch (chart)
            {
                case EObjectChart.HIGHWAY: return 1f;
                case EObjectChart.ROAD: return 1.1f;
                case EObjectChart.STREET: return 1.25f;
                case EObjectChart.PATH: return 1.6f;
                default: return 1.3f;
            }
        }

        /// <summary>One sample along one road, and everything the router needs about it.</summary>
        public sealed class RoadNode
        {
            /// <summary>Where it is. Terrain height, or the spline's own height on a bridge.</summary>
            public Vector3 Position;

            /// <summary>
            /// Which way the road runs here, pointing along increasing t. Roads have no direction
            /// of travel - this is the axis of the road, and the convoy uses whichever sign it is
            /// travelling in.
            /// </summary>
            public Vector3 Direction;

            /// <summary>Half the flat drivable width. See <see cref="MeasureHalfWidth"/>.</summary>
            public float HalfWidth;

            /// <summary>For a town-road tile node, half its footprint, used to decide which tiles
            /// are neighbours. Zero for a spline sample, which is not linked by footprint.</summary>
            public float TileHalfSize;

            /// <summary>The road this came from, and how it is classified on the chart.</summary>
            public int RoadIndex;
            public EObjectChart Chart;

            /// <summary>
            /// Neighbours, by node index. Undirected: consecutive samples along a road, plus every
            /// junction link. Small enough (2 on open road, 3-6 at a junction) that a list beats
            /// anything cleverer.
            /// </summary>
            public readonly List<int> Links = new List<int>();
        }

        /// <summary>One crossing of a gap in the road network, kept so /banditroads can report what
        /// the router had to invent to make the map connected.</summary>
        public struct GapCandidate
        {
            public int From;
            public int To;
            public float Distance;
        }

        private static readonly List<RoadNode> Nodes = new List<RoadNode>();
        private static readonly Dictionary<long, List<int>> Grid = new Dictionary<long, List<int>>();
        private static readonly List<GapCandidate> Gaps = new List<GapCandidate>();

        /// <summary>The gaps between roads this graph had to bridge to be routable.</summary>
        public static IReadOnlyList<GapCandidate> BridgedGaps
        {
            get
            {
                EnsureBuilt();
                return Gaps;
            }
        }

        /// <summary>Whether these two nodes are joined by a bridged gap rather than by road. What
        /// /banditroads uses to say how much of a route is not actually on tarmac.</summary>
        public static bool IsBridgedGap(int a, int b)
        {
            foreach (GapCandidate gap in Gaps)
            {
                if ((gap.From == a && gap.To == b) || (gap.From == b && gap.To == a))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The map the graph was built for, so it rebuilds itself when the level changes.</summary>
        private static string _builtMap;

        /// <summary>
        /// When an empty result may be tried again. A command can ask for the graph before
        /// LevelRoads.load has run, and caching "this map has no roads" from that moment would be
        /// permanent - but a map that genuinely has none must not rebuild on every query either.
        /// </summary>
        private static float _retryAfter;
        private const float EmptyGraphRetrySeconds = 30f;

        /// <summary>Scratch for A*, sized to the node count and reused between queries.</summary>
        private static float[] _gScore;
        private static int[] _cameFrom;
        private static int[] _stamp;
        private static int _queryStamp;

        public static bool IsAvailable
        {
            get
            {
                EnsureBuilt();
                return Nodes.Count > 1;
            }
        }

        public static int NodeCount
        {
            get
            {
                EnsureBuilt();
                return Nodes.Count;
            }
        }

        public static RoadNode Get(int nodeIndex)
        {
            return nodeIndex >= 0 && nodeIndex < Nodes.Count ? Nodes[nodeIndex] : null;
        }

        /// <summary>
        /// Builds the graph if it has not been built for this map yet. Cheap to call every tick -
        /// it is a string comparison once the graph is up.
        /// </summary>
        public static void EnsureBuilt()
        {
            string map = Level.info != null ? Level.info.name : null;
            if (map == null)
            {
                return;
            }

            if (_builtMap == map && (Nodes.Count > 1 || Time.realtimeSinceStartup < _retryAfter))
            {
                return;
            }

            _builtMap = map;
            _retryAfter = Time.realtimeSinceStartup + EmptyGraphRetrySeconds;
            Build();
        }

        /// <summary>Throws the graph away, so the next query rebuilds it. For a road editor or a test.</summary>
        public static void Invalidate()
        {
            _builtMap = null;
            _retryAfter = 0f;
        }

        private static void Build()
        {
            Nodes.Clear();
            Grid.Clear();

            float startedAt = Time.realtimeSinceStartup;
            int roadCount = 0;

            // LevelRoads keeps its list private and offers no count, but getRoad() returns null
            // past the end, which is the enumeration vanilla's own callers use.
            for (int roadIndex = 0; ; roadIndex++)
            {
                Road road = LevelRoads.getRoad(roadIndex);
                if (road == null)
                {
                    break;
                }

                if (road.joints == null || road.joints.Count < 2)
                {
                    continue;
                }

                if (IsTrainTrack(roadIndex))
                {
                    // Trains run on roads in this engine - the track is a Road with an entry in the
                    // level's Trains config, and its samples are the same shape as a highway's.
                    // Routing a lorry down a railway would look exactly as wrong as it sounds.
                    continue;
                }

                AppendRoad(road, roadIndex);
                roadCount++;
            }

            int splineNodeCount = Nodes.Count;
            int tileNodeStart = Nodes.Count;
            int tiles = AppendRoadObjects();

            BuildGrid();
            LinkTileNodes(tileNodeStart);
            LinkJunctions();
            BridgeGaps();

            _gScore = new float[Nodes.Count];
            _cameFrom = new int[Nodes.Count];
            _stamp = new int[Nodes.Count];
            _queryStamp = 0;

            if (Nodes.Count < 2)
            {
                Logger.Log($"[Bandit] No usable roads on {_builtMap} - convoys will drive straight lines.");
                return;
            }

            float elapsedMs = (Time.realtimeSinceStartup - startedAt) * 1000f;
            Logger.Log($"[Bandit] Road graph for {_builtMap}: {Nodes.Count} node(s) across "
                + $"{roadCount} spline road(s) and {tiles} town-road tile(s) "
                + $"({splineNodeCount} spline node(s), {Nodes.Count - splineNodeCount} tile node(s)) "
                + $"in {elapsedMs:0}ms.");
        }

        /// <summary>
        /// Whether this road is one of the level's train tracks. The association lives in the
        /// level's own config rather than on the road, which is also how Road.buildMesh decides
        /// whether to bother sampling track positions for it.
        /// </summary>
        private static bool IsTrainTrack(int roadIndex)
        {
            if (Level.info == null || Level.info.configData == null || Level.info.configData.Trains == null)
            {
                return false;
            }

            foreach (LevelTrainAssociation train in Level.info.configData.Trains)
            {
                if (train != null && train.RoadIndex == roadIndex)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Samples one road at a fixed spacing and chains the samples together.
        ///
        /// Sampling is per segment rather than over the whole spline, because the road's t is not
        /// arc length: getPosition(t) divides t evenly between joints, so a long straight and a
        /// tight bend get the same share of it. Walking segment by segment with each one's own
        /// length estimate is what keeps the spacing even, and is what vanilla does in
        /// Road.updateSamples.
        /// </summary>
        private static void AppendRoad(Road road, int roadIndex)
        {
            Classify(road, out float halfWidth, out EObjectChart chart);

            int segments = road.joints.Count - 1 + (road.isLoop ? 1 : 0);
            int firstNodeOfRoad = Nodes.Count;
            int previous = -1;

            for (int segment = 0; segment < segments; segment++)
            {
                float length = road.getLengthEstimate(segment);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / SampleSpacingMetres));

                // The last step of a segment is the first of the next one, so it is left to that
                // segment - except on the final segment of a road that does not loop, where the
                // end point is added below.
                for (int step = 0; step < steps; step++)
                {
                    float t = (float)step / steps;
                    previous = AppendSample(road, roadIndex, segment, t, halfWidth, chart, previous);
                }
            }

            if (road.isLoop)
            {
                // Close the ring back onto the first sample rather than adding a duplicate on top
                // of it.
                if (previous >= 0 && previous != firstNodeOfRoad)
                {
                    Link(previous, firstNodeOfRoad);
                }
            }
            else
            {
                AppendSample(road, roadIndex, segments - 1, 1f, halfWidth, chart, previous);
            }
        }

        private static int AppendSample(Road road, int roadIndex, int segment, float t,
            float halfWidth, EObjectChart chart, int previous)
        {
            Vector3 position = road.getPosition(segment, t);
            Vector3 direction = road.getVelocity(segment, t);

            // Mirrors Road.buildMesh: a joint flagged ignoreTerrain is a bridge or an overpass and
            // keeps the spline's own height; everything else is laid onto the terrain. The joint
            // offset is a hand-tuned lift the map maker applied, and is part of where the surface
            // actually is. Exact height matters less here than it looks - the driver re-samples the
            // surface under the vehicle every step, and its ground mask includes ENVIRONMENT, which
            // is the layer road meshes are on - but a node buried in a hillside under a bridge
            // would be picked as "nearest" from the wrong deck.
            RoadJoint joint = road.joints[Mathf.Clamp(segment, 0, road.joints.Count - 1)];
            if (!joint.ignoreTerrain)
            {
                position.y = LevelGround.getHeight(position);
            }

            RoadJoint next = segment < road.joints.Count - 1
                ? road.joints[segment + 1]
                : (road.isLoop ? road.joints[0] : joint);
            position.y += Mathf.Lerp(joint.offset, next.offset, t);

            RoadNode node = new RoadNode
            {
                Position = position,
                Direction = direction.sqrMagnitude > 0.0001f ? direction.normalized : Vector3.forward,
                HalfWidth = halfWidth,
                RoadIndex = roadIndex,
                Chart = chart
            };

            int index = Nodes.Count;
            Nodes.Add(node);

            if (previous >= 0)
            {
                Link(previous, index);
            }

            return index;
        }

        /// <summary>
        /// How wide a road is, and what kind of road it is. Both answers come from the same place,
        /// so they are worked out together.
        ///
        /// The width is the trap. A road carries one of two configurations depending on the map's
        /// age, and they disagree about what "width" means: a modern RoadAsset's Width is the
        /// *full* flat width - Road.buildMesh lays its vertices at `Width * 0.5f` either side of
        /// the spline - while the legacy RoadMaterial.width is already a half-width, which is why
        /// the game added a HalfWidth property to say so. Reading the legacy figure as a full width
        /// would put a convoy's right-hand lane in the ditch.
        ///
        /// Outside that flat top the mesh tapers into the terrain over a further `Depth` metres.
        /// That skirt is shoulder rather than road and is deliberately not counted.
        ///
        /// The classification mirrors Road.GetChartMode - the same thresholds the game itself uses
        /// to decide what to draw on the map chart - rather than calling it, so this reads the
        /// underlying fields directly and keeps working on a server whose Road class predates that
        /// method. Both thresholds are the same road: 16m of flat width is a highway.
        /// </summary>
        /// <summary>
        /// Adds a node for every placed road tile in the level, so town streets become part of the
        /// graph. Returns how many tiles were taken.
        ///
        /// One node per tile, at the tile's centre - which is the middle of the road, exactly where
        /// a vehicle should drive. No attempt is made to read each prefab's connection geometry:
        /// there are forty-odd road piece variants and no reliable per-prefab way to know where a
        /// Tee's third arm points, so instead the topology is recovered afterwards from adjacency
        /// (see <see cref="LinkTileNodes"/>) - tiles laid edge to edge are neighbours, and a
        /// crossroads tile is simply a tile with three or four neighbours. That needs nothing but
        /// positions and sizes, and it is robust to every piece the game or a mod defines.
        ///
        /// Each tile's half-size is measured from its colliders so the neighbour test scales to the
        /// piece - a long bridge span and a short kerb tile are both handled by the same rule.
        /// </summary>
        private static int AppendRoadObjects()
        {
            if (LevelObjects.objects == null)
            {
                return 0;
            }

            int tiles = 0;
            List<LevelObject>[,] regions = LevelObjects.objects;

            for (int x = 0; x < regions.GetLength(0); x++)
            {
                for (int y = 0; y < regions.GetLength(1); y++)
                {
                    List<LevelObject> cell = regions[x, y];
                    if (cell == null)
                    {
                        continue;
                    }

                    foreach (LevelObject obj in cell)
                    {
                        if (obj == null || obj.transform == null || obj.asset == null)
                        {
                            continue;
                        }

                        string name = obj.asset.name;
                        if (!IsRoadObject(name, out bool elevated))
                        {
                            continue;
                        }

                        AppendRoadObject(obj, elevated);
                        tiles++;
                    }
                }
            }

            return tiles;
        }

        private static void AppendRoadObject(LevelObject obj, bool elevated)
        {
            Transform transform = obj.transform;
            Vector3 position = transform.position;

            // A road tile sits on the terrain and its origin is at road level; a bridge deck or a
            // tunnel mouth keeps the height the mapper placed it at, the same distinction the spline
            // pass makes with RoadJoint.ignoreTerrain.
            if (!elevated)
            {
                position.y = LevelGround.getHeight(position);
            }

            // The tile's forward is its local +Z carried into the world - only used to give the
            // node a direction for lane offsetting and steering; the actual road shape comes from
            // how the tiles link up.
            Vector3 forward = transform.forward;
            forward.y = 0f;

            Nodes.Add(new RoadNode
            {
                Position = position,
                Direction = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward,
                HalfWidth = TileHalfWidthMetres,
                RoadIndex = RoadObjectRoadIndex,
                Chart = EObjectChart.STREET,
                TileHalfSize = MeasureTileHalfSize(obj)
            });
        }

        /// <summary>Half the tile's largest horizontal footprint, from its colliders, for the
        /// neighbour search. Falls back to a street-sized default when a tile has no measurable
        /// collider.</summary>
        private static float MeasureTileHalfSize(LevelObject obj)
        {
            Collider[] colliders = obj.transform.GetComponentsInChildren<Collider>(includeInactive: false);
            float half = 0f;

            foreach (Collider collider in colliders)
            {
                if (collider == null || collider.isTrigger)
                {
                    continue;
                }

                Bounds bounds = collider.bounds; // world AABB is fine - only a search radius
                half = Mathf.Max(half, bounds.extents.x, bounds.extents.z);
            }

            if (half < 0.5f)
            {
                half = TileHalfWidthMetres;
            }

            return Mathf.Min(half, MaxTileHalfSizeMetres);
        }

        /// <summary>Whether a placed object is a piece of drivable town road, and whether it is one
        /// that keeps its own height rather than being dropped onto the terrain.</summary>
        private static bool IsRoadObject(string name, out bool elevated)
        {
            elevated = false;
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }

            bool isRoad = false;
            for (int i = 0; i < RoadObjectPrefixes.Length; i++)
            {
                if (name.StartsWith(RoadObjectPrefixes[i], System.StringComparison.OrdinalIgnoreCase))
                {
                    isRoad = true;
                    break;
                }
            }

            if (!isRoad)
            {
                return false;
            }

            for (int i = 0; i < ElevatedObjectPrefixes.Length; i++)
            {
                if (name.StartsWith(ElevatedObjectPrefixes[i], System.StringComparison.OrdinalIgnoreCase))
                {
                    elevated = true;
                    break;
                }
            }

            return true;
        }

        /// <summary>
        /// Wires the town-road tiles into a network by adjacency: two tiles whose footprints touch
        /// are the same stretch of road and get linked.
        ///
        /// This is where the topology comes from, and it is the reason the tile pass needs no
        /// per-prefab geometry. Tiles are laid edge to edge on a grid, so "their footprints touch"
        /// is the whole of it - a straight run links into a chain, a Tee tile ends up with three
        /// neighbours and a crossroads four, and the router treats those exactly as it treats a
        /// spline junction. The height check keeps a bridge deck from linking to the road passing
        /// under it, the same way <see cref="LinkJunctions"/> does.
        /// </summary>
        private static void LinkTileNodes(int tileNodeStart)
        {
            List<int> nearby = new List<int>();

            for (int i = tileNodeStart; i < Nodes.Count; i++)
            {
                RoadNode node = Nodes[i];
                nearby.Clear();
                GatherCells(node.Position, nearby, node.TileHalfSize + MaxTileHalfSizeMetres);

                foreach (int other in nearby)
                {
                    if (other <= i || other < tileNodeStart)
                    {
                        continue; // each tile pair once, and only tile-to-tile here
                    }

                    RoadNode candidate = Nodes[other];

                    // Footprints touch, plus a small margin for the seam between them.
                    float reach = node.TileHalfSize + candidate.TileHalfSize + TileLinkMarginMetres;

                    Vector3 delta = candidate.Position - node.Position;
                    float flat = delta.x * delta.x + delta.z * delta.z;
                    if (flat > reach * reach)
                    {
                        continue;
                    }

                    // Two decks stacked at an overpass are close in plan and far in height; that is
                    // not a link a vehicle can drive.
                    if (Mathf.Abs(delta.y) > MaxJunctionHeightMetres)
                    {
                        continue;
                    }

                    Link(i, other);
                }
            }
        }

        private static void Classify(Road road, out float halfWidth, out EObjectChart chart)
        {
            RoadAsset asset = road.GetRoadAsset();
            if (asset != null && asset.Width > 0f)
            {
                halfWidth = asset.Width * 0.5f;
                chart = asset.ChartOverride != EObjectChart.NONE
                    ? asset.ChartOverride
                    : (asset.Width > 16f ? EObjectChart.HIGHWAY : EObjectChart.ROAD);
                return;
            }

            RoadMaterial legacy = LevelRoads.materials != null && road.material < LevelRoads.materials.Length
                ? LevelRoads.materials[road.material]
                : null;

            if (legacy == null || legacy.width <= 0f)
            {
                halfWidth = 4f; // the legacy default, and a sane road either way
                chart = EObjectChart.ROAD;
                return;
            }

            halfWidth = legacy.width;
            chart = !legacy.isConcrete
                ? EObjectChart.PATH
                : (legacy.width > 8f ? EObjectChart.HIGHWAY : EObjectChart.ROAD);
        }

        private static void Link(int a, int b)
        {
            if (a == b || a < 0 || b < 0)
            {
                return;
            }

            if (!Nodes[a].Links.Contains(b))
            {
                Nodes[a].Links.Add(b);
            }

            if (!Nodes[b].Links.Contains(a))
            {
                Nodes[b].Links.Add(a);
            }
        }

        private static long CellKey(Vector3 position)
        {
            int x = Mathf.FloorToInt(position.x / GridCellMetres);
            int z = Mathf.FloorToInt(position.z / GridCellMetres);
            return ((long)x << 32) ^ (uint)z;
        }

        private static void BuildGrid()
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                long key = CellKey(Nodes[i].Position);
                if (!Grid.TryGetValue(key, out List<int> cell))
                {
                    cell = new List<int>();
                    Grid[key] = cell;
                }

                cell.Add(i);
            }
        }

        /// <summary>
        /// Recovers the junctions. Two samples belonging to different roads that are within a
        /// road's width of each other are the same piece of ground as far as a vehicle is
        /// concerned, so they get linked and the router can turn off one road onto the other.
        ///
        /// Height is checked as well as plan distance, and that check is the whole reason this is
        /// not a flat 2D proximity test: a bridge crosses directly over the road beneath it, and
        /// linking those two would let a convoy route itself off the top of an overpass.
        /// </summary>
        private static void LinkJunctions()
        {
            List<int> nearby = new List<int>();
            int links = 0;

            for (int i = 0; i < Nodes.Count; i++)
            {
                RoadNode node = Nodes[i];
                nearby.Clear();
                GatherCells(node.Position, nearby);

                foreach (int other in nearby)
                {
                    if (other <= i)
                    {
                        continue; // each pair once
                    }

                    RoadNode candidate = Nodes[other];
                    if (candidate.RoadIndex == node.RoadIndex)
                    {
                        continue; // consecutive samples are already linked, and a road does not
                                  // junction with itself in any way worth driving
                    }

                    float radius = Mathf.Clamp(node.HalfWidth + candidate.HalfWidth,
                        MinJunctionRadiusMetres, MaxJunctionRadiusMetres);

                    Vector3 delta = candidate.Position - node.Position;
                    if (Mathf.Abs(delta.y) > MaxJunctionHeightMetres)
                    {
                        continue; // an overpass, not a junction
                    }

                    delta.y = 0f;
                    if (delta.sqrMagnitude > radius * radius)
                    {
                        continue;
                    }

                    Link(i, other);
                    links++;
                }
            }

            Logger.Log($"[Bandit] Road graph junctions: {links} link(s) between roads.");
        }

        /// <summary>
        /// Joins the road network back together across the gaps the map maker left in it.
        ///
        /// This is not a refinement, it is the difference between routing and not routing. A map's
        /// roads are drawn as separate splines and there is nothing in the editor that makes one
        /// end *at* another: PEI's twenty-three roads touch closely enough to be linked as junctions
        /// in only twenty places, which leaves fourteen disconnected islands of road. A* between two
        /// of them cannot fail gracefully - there is no route - so every convoy whose two ends
        /// happened to sit on different islands fell back to driving the straight line, which is
        /// exactly the symptom this was reported as.
        ///
        /// The gaps are real ground, not missing data: the tarmac genuinely stops and the next
        /// stretch starts a hundred metres later, with drivable land in between. So they are linked
        /// - but only after the ground between them has been checked, because the other thing on the
        /// far side of a gap in a coastal map's road network is a bay, and a convoy cheerfully
        /// routed across one would drive into the sea.
        ///
        /// Kruskal rather than "link everything close enough": one crossing per pair of islands is
        /// what a missing stretch of road is, and the shortest is the right one. Anything more just
        /// puts the router's own shortcuts into a network that is supposed to describe roads.
        /// </summary>
        private static void BridgeGaps()
        {
            Gaps.Clear();

            if (Nodes.Count < 2)
            {
                return;
            }

            int[] parent = new int[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
            {
                parent[i] = i;
            }

            // Seed the components from the links already made - along each road, and at every
            // junction found above.
            for (int i = 0; i < Nodes.Count; i++)
            {
                foreach (int link in Nodes[i].Links)
                {
                    Union(parent, i, link);
                }
            }

            List<GapCandidate> candidates = new List<GapCandidate>();
            List<int> nearby = new List<int>();

            for (int i = 0; i < Nodes.Count; i++)
            {
                // Loose ends only. A gap in the network is where a spline stops, and starting from
                // every node instead would offer the router a shortcut off the side of every road
                // it passes - which is not a road, and not what this is for.
                if (Nodes[i].Links.Count > 1)
                {
                    continue;
                }

                GatherCandidates(i, parent, nearby, candidates);
            }

            candidates.Sort((a, b) => a.Distance.CompareTo(b.Distance));

            int bridged = 0;

            // Both ends of a gap are loose ends, so each one is usually proposed twice - once from
            // either side. Remembering the refusals keeps the ground test, which raycasts, from
            // being run twice on the same stretch and reported twice in the log.
            HashSet<long> refused = new HashSet<long>();

            foreach (GapCandidate candidate in candidates)
            {
                if (Find(parent, candidate.From) == Find(parent, candidate.To))
                {
                    continue; // these two are already joined, by road or by a shorter gap
                }

                long pair = candidate.From < candidate.To
                    ? ((long)candidate.From << 32) | (uint)candidate.To
                    : ((long)candidate.To << 32) | (uint)candidate.From;

                if (refused.Contains(pair))
                {
                    continue;
                }

                if (!IsGapDrivable(Nodes[candidate.From].Position, Nodes[candidate.To].Position, out string reason))
                {
                    refused.Add(pair);
                    Logger.Log($"[Bandit] Road gap of {candidate.Distance:0}m at "
                        + $"({Nodes[candidate.From].Position.x:0}, {Nodes[candidate.From].Position.z:0}) "
                        + $"left open: {reason}.");
                    continue;
                }

                Link(candidate.From, candidate.To);
                Union(parent, candidate.From, candidate.To);
                Gaps.Add(candidate);
                bridged++;
            }

            int components = 0;
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Find(parent, i) == i)
                {
                    components++;
                }
            }

            Logger.Log($"[Bandit] Road graph gaps: {bridged} bridged, {refused.Count} left open, "
                + $"{components} unconnected piece(s) of network remaining.");
        }

        /// <summary>
        /// Every node worth considering as the far side of a gap from this loose end: the nearest
        /// node of each other road within reach that is not already connected to it.
        ///
        /// One per road rather than one overall, because the nearest road is not always the one
        /// that reconnects the network - a lay-by three metres away is closer than the highway the
        /// route actually needs, and Kruskal can only choose between candidates it was given.
        /// </summary>
        private static void GatherCandidates(int from, int[] parent, List<int> nearby, List<GapCandidate> into)
        {
            nearby.Clear();
            GatherCells(Nodes[from].Position, nearby, MaxGapMetres);

            RoadNode node = Nodes[from];
            int component = Find(parent, from);
            Dictionary<int, GapCandidate> bestPerRoad = new Dictionary<int, GapCandidate>();

            foreach (int other in nearby)
            {
                if (other == from || Nodes[other].RoadIndex == node.RoadIndex)
                {
                    continue;
                }

                if (Find(parent, other) == component)
                {
                    continue; // already reachable, so this would be a shortcut and not a repair
                }

                float distance = Vector3.Distance(node.Position, Nodes[other].Position);
                if (distance > MaxGapMetres)
                {
                    continue;
                }

                if (!bestPerRoad.TryGetValue(Nodes[other].RoadIndex, out GapCandidate best)
                    || distance < best.Distance)
                {
                    bestPerRoad[Nodes[other].RoadIndex] = new GapCandidate
                    {
                        From = from,
                        To = other,
                        Distance = distance
                    };
                }
            }

            foreach (KeyValuePair<int, GapCandidate> entry in bestPerRoad)
            {
                into.Add(entry.Value);
            }
        }

        /// <summary>
        /// Whether a vehicle could actually cross the ground between two road ends.
        ///
        /// Two things disqualify it, and the first is the one that matters: water. A gap in a
        /// coastal map's road network is as likely to be a bay as a missing stretch of tarmac, and
        /// there is no way to tell them apart from the spline data alone - both are simply two road
        /// ends with nothing between them. A shallow ford is allowed, because a stream crossing is
        /// a place vehicles do drive.
        ///
        /// The surface is the terrain, or road decking above it where there is any: a bridge in this
        /// engine is a road whose joints are flagged ignoreTerrain, so its deck is a road mesh on
        /// the ENVIRONMENT layer. Objects are deliberately not probed - a gap that happens to pass
        /// over a warehouse roof is not a road.
        /// </summary>
        private static bool IsGapDrivable(Vector3 from, Vector3 to, out string reason)
        {
            float distance = Vector3.Distance(from, to);
            int steps = Mathf.Max(1, Mathf.CeilToInt(distance / GapSampleSpacingMetres));
            float run = distance / steps;

            float previous = GapSurfaceHeight(from);

            for (int step = 1; step <= steps; step++)
            {
                Vector3 point = Vector3.Lerp(from, to, (float)step / steps);
                float height = GapSurfaceHeight(point);

                // A metre of water is a ford; more than that is somewhere a wheeled vehicle sinks.
                if (WaterUtility.isPointUnderwater(new Vector3(point.x, height + MaxFordDepthMetres, point.z)))
                {
                    reason = $"{MaxFordDepthMetres:0.0}m of water {step * run:0}m along it";
                    return false;
                }

                if (run > 0.01f && Mathf.Abs(Mathf.Atan2(height - previous, run) * Mathf.Rad2Deg) > MaxGapClimbDegrees)
                {
                    reason = $"ground steeper than {MaxGapClimbDegrees:0} degrees {step * run:0}m along it";
                    return false;
                }

                previous = height;
            }

            reason = null;
            return true;
        }

        /// <summary>The drivable surface at a point: the terrain, or the road deck over it.</summary>
        private static float GapSurfaceHeight(Vector3 point)
        {
            float terrain = LevelGround.getHeight(point);

            // Road meshes are the only thing in the whole of Assembly-CSharp on the ENVIRONMENT
            // layer, so this ray answers "is there a bridge here?" and nothing else.
            Ray ray = new Ray(new Vector3(point.x, terrain + BridgeProbeHeightMetres, point.z), Vector3.down);
            if (Physics.Raycast(ray, out RaycastHit hit, BridgeProbeHeightMetres * 2f,
                    RayMasks.ENVIRONMENT, QueryTriggerInteraction.Ignore)
                && hit.point.y > terrain)
            {
                return hit.point.y;
            }

            return terrain;
        }

        private static int Find(int[] parent, int node)
        {
            while (parent[node] != node)
            {
                parent[node] = parent[parent[node]]; // halve the path on the way up
                node = parent[node];
            }

            return node;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);

            if (rootA != rootB)
            {
                parent[rootB] = rootA;
            }
        }

        private static void GatherCells(Vector3 position, List<int> into)
        {
            GatherCells(position, into, GridCellMetres);
        }

        private static void GatherCells(Vector3 position, List<int> into, float radius)
        {
            int cx = Mathf.FloorToInt(position.x / GridCellMetres);
            int cz = Mathf.FloorToInt(position.z / GridCellMetres);
            int rings = Mathf.Max(1, Mathf.CeilToInt(radius / GridCellMetres));

            for (int dx = -rings; dx <= rings; dx++)
            {
                for (int dz = -rings; dz <= rings; dz++)
                {
                    long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                    if (Grid.TryGetValue(key, out List<int> cell))
                    {
                        into.AddRange(cell);
                    }
                }
            }
        }

        /// <summary>
        /// The road node nearest a point, within a radius. This is how a convoy gets on and off the
        /// network: the spawn point and each waypoint are snapped to the nearest road, and whatever
        /// is left over at either end is driven directly.
        /// </summary>
        public static bool TryGetNearest(Vector3 position, float maxDistance, out int nodeIndex, out float distance)
        {
            EnsureBuilt();

            nodeIndex = -1;
            distance = float.MaxValue;

            if (Nodes.Count == 0)
            {
                return false;
            }

            // Widen the search a ring at a time rather than scanning every node: a map's road
            // network is tens of thousands of samples and this runs per leg, per convoy.
            int rings = Mathf.Max(1, Mathf.CeilToInt(maxDistance / GridCellMetres));
            int cx = Mathf.FloorToInt(position.x / GridCellMetres);
            int cz = Mathf.FloorToInt(position.z / GridCellMetres);
            float bestSquared = maxDistance * maxDistance;

            for (int ring = 0; ring <= rings; ring++)
            {
                for (int dx = -ring; dx <= ring; dx++)
                {
                    for (int dz = -ring; dz <= ring; dz++)
                    {
                        // Only the perimeter of each ring is new.
                        if (ring > 0 && Mathf.Abs(dx) != ring && Mathf.Abs(dz) != ring)
                        {
                            continue;
                        }

                        long key = ((long)(cx + dx) << 32) ^ (uint)(cz + dz);
                        if (!Grid.TryGetValue(key, out List<int> cell))
                        {
                            continue;
                        }

                        foreach (int candidate in cell)
                        {
                            float candidateSquared = (Nodes[candidate].Position - position).sqrMagnitude;
                            if (candidateSquared < bestSquared)
                            {
                                bestSquared = candidateSquared;
                                nodeIndex = candidate;
                            }
                        }
                    }
                }

                // Stopping at the first ring with a hit in it would be wrong: the query point sits
                // somewhere inside its own cell, so a node one ring further out can still be nearer
                // than one found here. Everything in ring r+1 is at least r cells away, which is
                // the bar the best hit so far has to beat.
                if (nodeIndex >= 0 && Mathf.Sqrt(bestSquared) <= ring * GridCellMetres)
                {
                    break;
                }
            }

            if (nodeIndex < 0)
            {
                return false;
            }

            distance = Mathf.Sqrt(bestSquared);
            return true;
        }

        /// <summary>
        /// Routes between two points over the road network, appending the road nodes to
        /// <paramref name="into"/>. The caller's own start and end points are not included - a
        /// convoy drives to the first node with the navigator it already has, and off the last one
        /// the same way.
        ///
        /// Returns false when either end is further than <paramref name="snapDistance"/> from any
        /// road, or when no route exists between them - two towns on separate islands, most often.
        /// Both are ordinary answers rather than failures, and the caller drives direct instead.
        /// </summary>
        public static bool TryRoute(Vector3 from, Vector3 to, float snapDistance, List<int> into, out string reason)
        {
            EnsureBuilt();
            into.Clear();

            if (Nodes.Count < 2)
            {
                reason = "this map has no roads";
                return false;
            }

            if (!TryGetNearest(from, snapDistance, out int start, out float startDistance))
            {
                reason = $"no road within {snapDistance:0}m of the start";
                return false;
            }

            if (!TryGetNearest(to, snapDistance, out int goal, out float goalDistance))
            {
                reason = $"no road within {snapDistance:0}m of the destination";
                return false;
            }

            if (start == goal)
            {
                into.Add(start);
                reason = null;
                return true;
            }

            if (!Search(start, goal, into))
            {
                reason = "no road route between those points";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>
        /// Plain A* over the node graph, with distance for the heuristic and distance times the
        /// road's chart penalty for the cost, so a route prefers a highway to a track of the same
        /// length without ever refusing the track.
        /// </summary>
        private static bool Search(int start, int goal, List<int> into)
        {
            _queryStamp++;

            MinHeap open = new MinHeap(Mathf.Min(Nodes.Count, 1024));
            _gScore[start] = 0f;
            _cameFrom[start] = -1;
            _stamp[start] = _queryStamp;
            open.Push(start, Heuristic(start, goal));

            Vector3 goalPosition = Nodes[goal].Position;

            while (open.Count > 0)
            {
                int current = open.Pop();
                if (current == goal)
                {
                    Reconstruct(goal, into);
                    return true;
                }

                RoadNode node = Nodes[current];
                float currentScore = _gScore[current];

                foreach (int neighbour in node.Links)
                {
                    RoadNode other = Nodes[neighbour];
                    float step = Vector3.Distance(node.Position, other.Position)
                        * ChartCostMultiplier(other.Chart);
                    float tentative = currentScore + step;

                    if (_stamp[neighbour] == _queryStamp && tentative >= _gScore[neighbour])
                    {
                        continue;
                    }

                    _stamp[neighbour] = _queryStamp;
                    _gScore[neighbour] = tentative;
                    _cameFrom[neighbour] = current;
                    open.Push(neighbour, tentative + Vector3.Distance(other.Position, goalPosition));
                }
            }

            return false;
        }

        private static float Heuristic(int node, int goal)
        {
            return Vector3.Distance(Nodes[node].Position, Nodes[goal].Position);
        }

        private static void Reconstruct(int goal, List<int> into)
        {
            int cursor = goal;
            while (cursor >= 0)
            {
                into.Add(cursor);
                cursor = _cameFrom[cursor];
            }

            into.Reverse();
        }

        /// <summary>
        /// Where on the road a vehicle of this width should actually drive, given which way it is
        /// going: over to the right if it fits in half the road, down the middle if it does not.
        ///
        /// The margin is the gap left between the vehicle's flank and the edge of the flat surface.
        /// Below that the lane is not worth having - a truck riding the shoulder of a narrow road
        /// is worse than one straddling the crown of it.
        /// </summary>
        public static Vector3 GetLanePosition(int nodeIndex, Vector3 travelDirection, float vehicleHalfWidth)
        {
            RoadNode node = Get(nodeIndex);
            if (node == null)
            {
                return Vector3.zero;
            }

            const float LaneMarginMetres = 0.5f;

            // Half a carriageway has to hold the vehicle plus a margin on each side of it.
            float laneHalfWidth = node.HalfWidth * 0.5f;
            if (vehicleHalfWidth + LaneMarginMetres > laneHalfWidth)
            {
                return node.Position;
            }

            travelDirection.y = 0f;
            if (travelDirection.sqrMagnitude < 0.0001f)
            {
                return node.Position;
            }

            // Cross(up, forward) is the right-hand side; Cross(forward, up) - which is the order
            // vanilla's own mesh builder uses - points left, and would put the convoy into
            // oncoming traffic.
            Vector3 right = Vector3.Cross(Vector3.up, travelDirection.normalized);
            return node.Position + right * laneHalfWidth;
        }

        /// <summary>
        /// A binary heap keyed on f-score. The alternative - scanning the open list for the best
        /// node - is O(n) per pop, which on a graph this size turns a route into a visible hitch.
        /// </summary>
        private sealed class MinHeap
        {
            private int[] _items;
            private float[] _priorities;

            public int Count { get; private set; }

            public MinHeap(int capacity)
            {
                _items = new int[Mathf.Max(4, capacity)];
                _priorities = new float[_items.Length];
            }

            public void Push(int item, float priority)
            {
                if (Count == _items.Length)
                {
                    System.Array.Resize(ref _items, Count * 2);
                    System.Array.Resize(ref _priorities, Count * 2);
                }

                int child = Count++;
                _items[child] = item;
                _priorities[child] = priority;

                while (child > 0)
                {
                    int parent = (child - 1) / 2;
                    if (_priorities[parent] <= _priorities[child])
                    {
                        break;
                    }

                    Swap(parent, child);
                    child = parent;
                }
            }

            public int Pop()
            {
                int result = _items[0];
                Count--;
                _items[0] = _items[Count];
                _priorities[0] = _priorities[Count];

                int parent = 0;
                while (true)
                {
                    int left = parent * 2 + 1;
                    int right = left + 1;
                    int smallest = parent;

                    if (left < Count && _priorities[left] < _priorities[smallest])
                    {
                        smallest = left;
                    }

                    if (right < Count && _priorities[right] < _priorities[smallest])
                    {
                        smallest = right;
                    }

                    if (smallest == parent)
                    {
                        break;
                    }

                    Swap(smallest, parent);
                    parent = smallest;
                }

                return result;
            }

            private void Swap(int a, int b)
            {
                int item = _items[a];
                _items[a] = _items[b];
                _items[b] = item;

                float priority = _priorities[a];
                _priorities[a] = _priorities[b];
                _priorities[b] = priority;
            }
        }
    }
}
