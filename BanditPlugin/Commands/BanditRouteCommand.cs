using System.Collections.Generic;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit route patrol|convoy add [marker]|remove [&lt;n&gt;]|list|clear" - this map's two
    /// routes.
    ///
    /// They are not the same list: a patrol route is walked by bandits told "patrol on", a convoy
    /// route is driven by "/bandit convoy spawn", which starts at its first point and drives the
    /// rest. Both take the same words. A point goes where you stand or at your map marker, which
    /// are the two places anyone actually knows the coordinates of; "remove" with a number takes
    /// the one the list printed, and without one takes the nearest within 10m.
    /// </summary>
    public static class BanditRouteCommand
    {
        /// <summary>How close you must be to a point for a bare "remove" to take it.</summary>
        private const float RemoveRadius = 10f;

        public static void Execute(IRocketPlayer caller, string[] args)
        {
            if (args.Length == 0)
            {
                List(caller, patrol: true);
                List(caller, patrol: false);
                return;
            }

            bool patrol;
            if (Is(args[0], "patrol"))
            {
                patrol = true;
            }
            else if (Is(args[0], "convoy"))
            {
                patrol = false;
            }
            else
            {
                ReplyUsage(caller);
                return;
            }

            string action = args.Length > 1 ? args[1].ToLowerInvariant() : "list";
            switch (action)
            {
                case "add":
                    Add(caller, patrol, Rest(args, 2));
                    break;

                case "remove":
                    Remove(caller, patrol, Rest(args, 2));
                    break;

                case "clear":
                    int cleared = patrol ? BanditWaypointStore.Clear() : BanditConvoyRoute.Clear();
                    Reply(caller, $"Cleared {cleared} {Name(patrol)} waypoint(s).", Color.green);
                    break;

                case "list":
                    List(caller, patrol);
                    break;

                default:
                    ReplyUsage(caller);
                    break;
            }
        }

        private static string Name(bool patrol) => patrol ? "patrol" : "convoy";

        private static IReadOnlyList<Vector3> Points(bool patrol) =>
            patrol ? BanditWaypointStore.Current : BanditConvoyRoute.Current;

        private static void Add(IRocketPlayer caller, bool patrol, string[] args)
        {
            UnturnedPlayer player = RequirePlayer(caller, "Adding a waypoint");
            if (player == null)
            {
                return;
            }

            Vector3 point = player.Player.transform.position;
            if (args.Length > 0 && BanditPlacement.IsMarkerRequest(args[0])
                && !BanditOrders.TryMarker(caller, player.Player, out point))
            {
                return;
            }

            int count;
            if (patrol)
            {
                BanditWaypointStore.Add(point);
                count = BanditWaypointStore.Current.Count;
            }
            else
            {
                count = BanditConvoyRoute.Add(point);
            }

            Reply(caller, $"{(patrol ? "Patrol" : "Convoy")} waypoint {count} set at ({point.x:0}, {point.z:0}).",
                Color.green);

            if (!patrol && count == 1)
            {
                Reply(caller, "One more at least - a convoy spawns at the first waypoint and drives to the last.",
                    Color.grey);
            }
        }

        private static void Remove(IRocketPlayer caller, bool patrol, string[] args)
        {
            int number;
            if (args.Length > 0)
            {
                if (!int.TryParse(args[0], out number))
                {
                    Reply(caller, $"Usage: /bandit route {Name(patrol)} remove [<number from the list>]", Color.yellow);
                    return;
                }
            }
            else
            {
                UnturnedPlayer player = RequirePlayer(caller, "Removing the nearest waypoint");
                if (player == null)
                {
                    return;
                }

                number = Nearest(Points(patrol), player.Player.transform.position);
                if (number < 0)
                {
                    Reply(caller, $"No {Name(patrol)} waypoint within {RemoveRadius:0}m - give its number "
                        + "from the list instead.", Color.red);
                    return;
                }
            }

            bool removed = patrol ? BanditWaypointStore.RemoveAt(number) : BanditConvoyRoute.RemoveAt(number);
            Reply(caller, removed
                ? $"Removed {Name(patrol)} waypoint {number}."
                : $"There is no {Name(patrol)} waypoint {number}.", removed ? Color.green : Color.red);
        }

        /// <summary>The one-based number of the point nearest <paramref name="position"/> within
        /// <see cref="RemoveRadius"/>, or -1.</summary>
        private static int Nearest(IReadOnlyList<Vector3> points, Vector3 position)
        {
            int nearest = -1;
            float nearestSquared = RemoveRadius * RemoveRadius;

            for (int i = 0; i < points.Count; i++)
            {
                float distanceSquared = (points[i] - position).sqrMagnitude;
                if (distanceSquared <= nearestSquared)
                {
                    nearestSquared = distanceSquared;
                    nearest = i + 1;
                }
            }

            return nearest;
        }

        private static void List(IRocketPlayer caller, bool patrol)
        {
            IReadOnlyList<Vector3> points = Points(patrol);
            Vector3? from = caller is UnturnedPlayer player ? player.Player.transform.position : (Vector3?)null;

            if (points.Count == 0)
            {
                string fallback = string.Empty;
                if (patrol)
                {
                    bool usesLocationNodes = BanditPlugin.Instance.Configuration.Instance.PatrolUseLocationNodesWhenNoWaypoints;
                    int nodes = BanditWaypointStore.GetRoute(usesLocationNodes).Count;
                    fallback = nodes > 0 ? $" Patrols would use this map's {nodes} location nodes." : string.Empty;
                }

                Reply(caller, $"No {Name(patrol)} route on this map.{fallback} "
                    + $"/bandit route {Name(patrol)} add records one where you stand.", Color.yellow);
                return;
            }

            string map = patrol ? BanditWaypointStore.CurrentMap : BanditConvoyRoute.CurrentMap;
            Reply(caller, $"{points.Count} {Name(patrol)} waypoint(s) on {map}"
                + (!patrol && points.Count < 2 ? " - one more is needed to run a convoy:" : ":"), Color.green);

            for (int i = 0; i < points.Count; i++)
            {
                Vector3 point = points[i];
                Reply(caller, $"  {i + 1}. ({point.x:0}, {point.y:0}, {point.z:0})"
                    + (from.HasValue ? $" - {Vector3.Distance(from.Value, point):0}m away" : string.Empty),
                    Color.white);
            }
        }

        private static void ReplyUsage(IRocketPlayer caller)
        {
            Reply(caller, "Usage: /bandit route patrol|convoy add [marker]  |  remove [<n>]  |  list  |  clear",
                Color.yellow);
        }
    }
}
