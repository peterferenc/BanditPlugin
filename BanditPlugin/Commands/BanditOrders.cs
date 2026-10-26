using System;
using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit [target] &lt;order&gt;" - the orders, given to a bandit or to any group of them.
    ///
    ///   shoot on|off          weapons free, or hold fire
    ///   cover on|off          look for cover and move to it, or stay where they are
    ///   peek on|off           once in cover, step out to shoot - or stay down
    ///   stance stand|crouch|prone|free
    ///   patrol on|off         walk this map's patrol route
    ///   goto [marker]         walk to where you are looking, or your map marker
    ///   wave                  holster, wave at you, re-arm
    ///   seat driver|gunner[n]|exit, drive ...   see <see cref="BanditVehicleOrders"/>
    ///
    /// With no target an order goes to the last thing spawned, so the usual testing loop - spawn
    /// something, then tell it what to do - never needs a number typed. A bandit spawned with
    /// "/bandit spawn" does none of these until told, which is what makes each behaviour something
    /// you can switch on and watch by itself.
    /// </summary>
    public static class BanditOrders
    {
        /// <summary>How far away a "goto" point can be picked by looking at it.</summary>
        private const float MaxTargetDistance = 512f;

        /// <summary>Every order word, so the dispatcher can tell an order from an unknown word.</summary>
        public static readonly string[] Words =
        {
            "shoot", "cover", "peek", "stance", "patrol", "goto", "wave", "seat", "drive"
        };

        public static bool IsOrder(string word)
        {
            return Array.IndexOf(Words, word?.ToLowerInvariant()) >= 0;
        }

        public static void Execute(IRocketPlayer caller, BanditTarget target, string order, string[] args)
        {
            switch (order.ToLowerInvariant())
            {
                case "shoot":
                    Switch(caller, target, order, args, (bandit, on) => bandit.HoldFire = !on,
                        "weapons free", "holding fire");
                    break;

                case "cover":
                    Switch(caller, target, order, args, (bandit, on) => bandit.Brain.SetCoverEnabled(on),
                        "looking for cover", "holding position");
                    break;

                case "peek":
                    Switch(caller, target, order, args, (bandit, on) => bandit.Brain.SetPeekEnabled(on),
                        "peeking from cover", "staying down in cover");
                    break;

                case "stance":
                    Stance(caller, target, args);
                    break;

                case "patrol":
                    Patrol(caller, target, args);
                    break;

                case "goto":
                    GoTo(caller, target, args);
                    break;

                case "wave":
                    Wave(caller, target);
                    break;

                case "seat":
                    BanditVehicleOrders.Seat(caller, target, args);
                    break;

                case "drive":
                    BanditVehicleOrders.Drive(caller, target, args);
                    break;
            }
        }

        /// <summary>
        /// The bandits of a target that can take an order now: alive, and with a brain. Says why
        /// and returns an empty list when there are none, so every order can simply return.
        /// </summary>
        public static List<BanditBotController> Ready(IRocketPlayer caller, BanditTarget target)
        {
            List<BanditBotController> ready = new List<BanditBotController>();
            List<BanditBotController> members = target.Members();

            foreach (BanditBotController bandit in members)
            {
                if (bandit.Brain != null && !IsDead(bandit))
                {
                    ready.Add(bandit);
                }
            }

            if (members.Count == 0)
            {
                Reply(caller, $"{target.Label} has nobody left on the map.", Color.red);
            }
            else if (ready.Count == 0)
            {
                Reply(caller, target.Kind == BanditTargetKind.Bandit && IsDead(members[0])
                    ? BanditIsDead
                    : $"Nobody in {target.Label} is ready for orders yet - try again in a moment.", Color.red);
            }

            return ready;
        }

        /// <summary>The shape shoot, cover and peek share: on or off, for everyone in the target.</summary>
        private static void Switch(IRocketPlayer caller, BanditTarget target, string order, string[] args,
            Action<BanditBotController, bool> apply, string onDescription, string offDescription)
        {
            if (args.Length == 0 || !TryParseOnOff(args[0], out bool on))
            {
                Reply(caller, $"Usage: /bandit [target] {order.ToLowerInvariant()} on|off", Color.yellow);
                return;
            }

            List<BanditBotController> bandits = Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            foreach (BanditBotController bandit in bandits)
            {
                apply(bandit, on);
            }

            Reply(caller, $"{target.Label}: {bandits.Count} bandit(s) {(on ? onDescription : offDescription)}.",
                Color.green);
        }

        /// <summary>
        /// "free" is the way back to letting each kit decide for itself - without it, ordering a
        /// squad to stand would permanently disable the machinegunner's own prone-on-contact.
        /// </summary>
        private static void Stance(IRocketPlayer caller, BanditTarget target, string[] args)
        {
            if (args.Length == 0 || !TryParseStance(args[0], out BanditStance stance))
            {
                Reply(caller, "Usage: /bandit [target] stance stand|crouch|prone|free", Color.yellow);
                return;
            }

            List<BanditBotController> bandits = Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            int swimming = 0;
            foreach (BanditBotController bandit in bandits)
            {
                bandit.Brain.SetStanceOrder(stance);
                if (bandit.Self.stance != null && bandit.Self.stance.stance == EPlayerStance.SWIM)
                {
                    swimming++;
                }
            }

            // The stance itself changes on the next input packet, so reading it back here would
            // still show the old one. Report the order, and the one case vanilla refuses outright.
            string note = swimming > 0
                ? $" {swimming} in water, though - vanilla forces a swimming player upright."
                : string.Empty;

            Reply(caller, $"{target.Label}: {bandits.Count} bandit(s) "
                + (stance == BanditStance.Free ? "choosing their own stance again." : $"holding {args[0].ToLowerInvariant()}.")
                + note, Color.green);
        }

        private static bool TryParseStance(string argument, out BanditStance stance)
        {
            switch (argument.ToLowerInvariant())
            {
                case "stand":
                case "standing":
                    stance = BanditStance.Stand;
                    return true;
                case "crouch":
                case "crouched":
                    stance = BanditStance.Crouch;
                    return true;
                case "prone":
                    stance = BanditStance.Prone;
                    return true;
                case "free":
                case "auto":
                    stance = BanditStance.Free;
                    return true;
                default:
                    stance = BanditStance.Free;
                    return false;
            }
        }

        private static void Patrol(IRocketPlayer caller, BanditTarget target, string[] args)
        {
            if (args.Length == 0 || !TryParseOnOff(args[0], out bool on))
            {
                Reply(caller, "Usage: /bandit [target] patrol on|off", Color.yellow);
                return;
            }

            int waypointCount = BanditWaypointStore.GetRoute(
                BanditPlugin.Instance.Configuration.Instance.PatrolUseLocationNodesWhenNoWaypoints).Count;

            if (on && waypointCount == 0)
            {
                Reply(caller, "No patrol route on this map - record one with /bandit route patrol add.", Color.red);
                return;
            }

            List<BanditBotController> bandits = Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            foreach (BanditBotController bandit in bandits)
            {
                bandit.Brain.SetPatrol(on);
            }

            Reply(caller, on
                ? $"{target.Label}: {bandits.Count} bandit(s) patrolling {waypointCount} waypoint(s)."
                : $"{target.Label}: {bandits.Count} bandit(s) holding position.", Color.green);
        }

        /// <summary>
        /// Walks the target to where you are looking, or to your map marker.
        ///
        /// One bandit goes to the point itself. More than one go to a formation laid out across it,
        /// facing the way you were looking - the same line a squad spawns in - because five men
        /// sent to one point arrive as a crowd standing on each other.
        /// </summary>
        private static void GoTo(IRocketPlayer caller, BanditTarget target, string[] args)
        {
            UnturnedPlayer player = RequirePlayer(caller, "goto");
            if (player == null || !TryPickPoint(caller, player.Player, args, out Vector3 point))
            {
                return;
            }

            List<BanditBotController> bandits = Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            // A seated bandit has no feet to steer, so the order would be accepted and then do
            // nothing at all. Counted and named in the reply rather than silently skipped.
            List<BanditBotController> walkers = new List<BanditBotController>();
            foreach (BanditBotController bandit in bandits)
            {
                if (bandit.Driver == null || !bandit.Driver.IsSeated)
                {
                    walkers.Add(bandit);
                }
            }

            int seated = bandits.Count - walkers.Count;
            if (walkers.Count == 0)
            {
                Reply(caller, $"{target.Label} is in a vehicle - use drive, or seat exit first.", Color.red);
                return;
            }

            Vector3 forward = point - player.Player.transform.position;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            float spacing = BanditPlugin.Instance.Configuration.Instance.SquadSpacing;

            for (int i = 0; i < walkers.Count; i++)
            {
                Vector3 slot = walkers.Count == 1
                    ? point
                    : BanditPlacement.FormationSlot(point, right, forward, i, walkers.Count, spacing);

                walkers[i].Brain.GoTo(slot);
            }

            float distance = Vector3.Distance(walkers[0].Self.transform.position, point);
            Reply(caller, $"{target.Label}: {walkers.Count} bandit(s) moving out"
                + (walkers.Count > 1 ? " in formation" : string.Empty) + $" - {distance:0}m."
                + (seated > 0 ? $" {seated} in vehicles stay put - use drive for those." : string.Empty),
                Color.green);
        }

        /// <summary>
        /// The point a goto or drive is aimed at: the caller's map marker if they said "marker",
        /// otherwise whatever they are looking at within 512m.
        /// </summary>
        public static bool TryPickPoint(IRocketPlayer caller, Player player, string[] args, out Vector3 point)
        {
            point = Vector3.zero;

            if (args.Length > 0 && BanditPlacement.IsMarkerRequest(args[0]))
            {
                return TryMarker(caller, player, out point);
            }

            if (!Physics.Raycast(player.look.aim.position, player.look.aim.forward, out RaycastHit hit,
                    MaxTargetDistance, RayMasks.BLOCK_COLLISION))
            {
                Reply(caller, "Look at a point on the ground within 512m, or add 'marker'.", Color.red);
                return false;
            }

            point = hit.point;
            return true;
        }

        /// <summary>
        /// The caller's map marker, put on the ground. A marker is a click on the map, so the height
        /// it carries is whatever the client sent rather than the ground under it - and everything
        /// downstream compares heights.
        /// </summary>
        public static bool TryMarker(IRocketPlayer caller, Player player, out Vector3 point)
        {
            point = Vector3.zero;

            if (player.quests == null || !player.quests.isMarkerPlaced)
            {
                Reply(caller, "No map marker placed.", Color.red);
                return false;
            }

            point = player.quests.markerPosition;
            point.y = LevelGround.getHeight(point);
            return true;
        }

        private static void Wave(IRocketPlayer caller, BanditTarget target)
        {
            UnturnedPlayer player = RequirePlayer(caller, "wave");
            if (player == null)
            {
                return;
            }

            List<BanditBotController> bandits = Ready(caller, target);
            if (bandits.Count == 0)
            {
                return;
            }

            int waving = 0;
            foreach (BanditBotController bandit in bandits)
            {
                waving += bandit.TryPlayGesture(EPlayerGesture.WAVE, player.Player) ? 1 : 0;
            }

            Reply(caller, waving > 0
                ? $"{target.Label}: {waving} bandit(s) waving."
                : $"{target.Label} is already mid-gesture.", waving > 0 ? Color.green : Color.yellow);
        }
    }
}
