using System;
using System.Collections;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// Paints a planned route on the ground so it can be looked at.
    ///
    /// The same reasoning as <see cref="BanditCoverDebug"/>, for the same reason: a route is a list
    /// of coordinates in a log, and "it went the wrong way" and "it went the right way badly" read
    /// identically there. Standing on the road and seeing where the line actually goes settles in
    /// one look what a hundred trace lines argue about - whether a corner is being cut, whether the
    /// arc through a junction sits on the tarmac, whether the road graph joined two roads somewhere
    /// silly.
    ///
    /// Colours carry the only distinction worth making at a glance:
    ///   green   the point the vehicle is currently driving at
    ///   blue    ordinary road points
    ///   yellow  points on a rounded corner, so the arc is visible as an arc
    ///   red     a waypoint the route was asked to visit, rather than a road node
    ///
    /// The same decal warning applies as for cover markers, and harder, because a cross-map route
    /// has hundreds of points where a cover search has forty: every marker is eight permanent decal
    /// objects on every client in range, so this caps what it draws and always clears first.
    /// </summary>
    public static class BanditRouteDebug
    {
        // Paintball impact effects, the only stock effects that come in colours.
        private const string EffectGreen = "066b5cee2aee41eba3d631e4f3710b9b";
        private const string EffectBlue = "f52928886e6848d4b810077fa537534a";
        private const string EffectRed = "563658fc7a334dbc8c0b9e322aac96b9";
        private const string EffectYellow = "d9820fabf8174ed5807dc44593800406";

        private static readonly string[] AllMarkerGuids = { EffectGreen, EffectBlue, EffectRed, EffectYellow };

        private const float MarkerRelevantDistance = 512f;

        /// <summary>The most markers a single draw will place. A route across PEI is several hundred
        /// points and each marker is eight decals that never expire on their own, so drawing all of
        /// them is how you make somebody's client unplayable to answer a question about a corner.</summary>
        public const int DefaultMaxMarkers = 120;

        private static Coroutine _clearRoutine;

        /// <summary>
        /// The most recently planned route, kept so it can be drawn after the fact.
        ///
        /// Recorded at plan time rather than asked for on demand, because by the time anybody wants
        /// to see a route the interesting thing has usually already happened - the vehicle is
        /// wedged, or it took a line nobody expected - and re-planning from where it is now would
        /// answer a different question.
        /// </summary>
        public static IReadOnlyList<Marker> LastPlan { get; private set; }

        /// <summary>Where the vehicle following that route is currently steering, updated as it
        /// goes. Drawn green.</summary>
        public static Vector3? CurrentTarget { get; set; }

        public static void SetPlan(List<Marker> plan)
        {
            LastPlan = plan;
            CurrentTarget = null;
        }

        /// <summary>One point of a route, and what kind of point it is.</summary>
        public struct Marker
        {
            public Vector3 Position;
            public MarkerKind Kind;
        }

        public enum MarkerKind
        {
            RoadPoint,
            CornerArc,
            Waypoint,
            Current,

            /// <summary>A node where roads meet, or where the graph had to bridge a gap between
            /// two of them. Drawn apart from ordinary nodes because a junction in the wrong place
            /// is the one graph fault you can actually see from the ground.</summary>
            Junction
        }

        /// <summary>
        /// Draws a route. Returns how many markers were placed, which the caller reports - a capped
        /// draw must never quietly look like a shorter route than was really planned.
        /// </summary>
        public static int Show(IReadOnlyList<Marker> markers, float seconds, int maxMarkers)
        {
            Clear();

            if (BanditPlugin.Instance == null || markers == null || markers.Count == 0 || maxMarkers <= 0)
            {
                return 0;
            }

            // Thinned by stride rather than by truncation, so a capped draw still shows the whole
            // route at lower resolution instead of the first third of it at full.
            float stride = markers.Count <= maxMarkers ? 1f : (float)markers.Count / maxMarkers;
            int drawn = 0;

            for (float cursor = 0f; cursor < markers.Count; cursor += stride)
            {
                Marker marker = markers[Mathf.Min(Mathf.FloorToInt(cursor), markers.Count - 1)];
                Spawn(EffectFor(marker.Kind), marker.Position);
                drawn++;
            }

            // Waypoints and the current target are the two things you are usually looking for, so
            // they are drawn whatever the thinning did to them.
            if (markers.Count > maxMarkers)
            {
                foreach (Marker marker in markers)
                {
                    if (marker.Kind == MarkerKind.Waypoint || marker.Kind == MarkerKind.Current)
                    {
                        Spawn(EffectFor(marker.Kind), marker.Position);
                        drawn++;
                    }
                }
            }

            if (seconds > 0f)
            {
                _clearRoutine = BanditPlugin.Instance.StartCoroutine(ClearAfter(seconds));
            }

            return drawn;
        }

        public static void Clear()
        {
            if (_clearRoutine != null && BanditPlugin.Instance != null)
            {
                BanditPlugin.Instance.StopCoroutine(_clearRoutine);
            }

            _clearRoutine = null;

            foreach (string guidText in AllMarkerGuids)
            {
                if (Guid.TryParse(guidText, out Guid guid))
                {
                    EffectManager.ClearEffectByGuid_AllPlayers(guid);
                }
            }
        }

        private static IEnumerator ClearAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            _clearRoutine = null;
            Clear();
        }

        private static string EffectFor(MarkerKind kind)
        {
            switch (kind)
            {
                case MarkerKind.Current: return EffectGreen;
                case MarkerKind.CornerArc: return EffectYellow;
                case MarkerKind.Waypoint: return EffectRed;
                case MarkerKind.Junction: return EffectRed;
                default: return EffectBlue;
            }
        }

        private static void Spawn(string guidText, Vector3 position)
        {
            if (!Guid.TryParse(guidText, out Guid guid))
            {
                return;
            }

            TriggerEffectParameters parameters = new TriggerEffectParameters(guid)
            {
                // Lifted clear of the road mesh, or the burst is buried in the tarmac it is
                // marking, which is exactly where it is least visible.
                position = position + Vector3.up * 0.2f,
                relevantDistance = MarkerRelevantDistance,
                reliable = true
            };

            parameters.SetDirection(Vector3.up);

            if (parameters.asset != null)
            {
                EffectManager.triggerEffect(parameters);
            }
        }
    }
}
