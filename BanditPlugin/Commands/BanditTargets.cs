using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using SDG.Unturned;

namespace BanditPlugin.Commands
{
    public enum BanditTargetKind
    {
        Bandit,
        Squad,
        Event,
        Convoy,
        Vehicle,
        Team,
        All
    }

    /// <summary>
    /// Something an order can be given to: one bandit, or any of the groups bandits come in.
    ///
    /// Every order resolves its target to a list of bandits and acts on each one, which is what
    /// lets "shoot on" mean the same thing to a single bandit, a squad, an event or a whole team -
    /// each order is written once, and works on every kind of target.
    /// </summary>
    public sealed class BanditTarget
    {
        public BanditTargetKind Kind { get; private set; }
        public BanditBotController Bandit { get; private set; }
        public BanditSquad Squad { get; private set; }
        public BanditEvent Event { get; private set; }
        public BanditConvoy Convoy { get; private set; }
        public BanditEvent.Ride Ride { get; private set; }
        public BanditTeam Team { get; private set; }

        public static BanditTarget Of(BanditBotController bandit) =>
            new BanditTarget { Kind = BanditTargetKind.Bandit, Bandit = bandit };

        public static BanditTarget Of(BanditSquad squad) =>
            new BanditTarget { Kind = BanditTargetKind.Squad, Squad = squad };

        public static BanditTarget Of(BanditEvent banditEvent) =>
            new BanditTarget { Kind = BanditTargetKind.Event, Event = banditEvent };

        public static BanditTarget Of(BanditConvoy convoy) =>
            new BanditTarget { Kind = BanditTargetKind.Convoy, Convoy = convoy };

        public static BanditTarget Of(BanditEvent.Ride ride) =>
            new BanditTarget { Kind = BanditTargetKind.Vehicle, Ride = ride };

        public static BanditTarget Of(BanditTeam team) =>
            new BanditTarget { Kind = BanditTargetKind.Team, Team = team };

        public static readonly BanditTarget AllBandits = new BanditTarget { Kind = BanditTargetKind.All };

        /// <summary>How a reply names it: "Bandit #7", "Squad 2 (rifle)", "Vehicle 4 (tank)".</summary>
        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case BanditTargetKind.Bandit:
                        return $"Bandit #{Bandit.Id}";
                    case BanditTargetKind.Squad:
                        return $"Squad {Squad.Id} ({Squad.TypeName})";
                    case BanditTargetKind.Event:
                        return $"Event {Event.Id}";
                    case BanditTargetKind.Convoy:
                        return $"Convoy {Convoy.Id}";
                    case BanditTargetKind.Vehicle:
                        return $"Vehicle {Ride.Id} ({Ride.TypeName})";
                    case BanditTargetKind.Team:
                        return $"Team {Team.Label}";
                    default:
                        return "All bandits";
                }
            }
        }

        /// <summary>
        /// Whether there is anything left of it. A squad that has lost every man, or an event that
        /// has been cleared, is gone even though the object still exists.
        /// </summary>
        public bool IsGone
        {
            get
            {
                switch (Kind)
                {
                    case BanditTargetKind.Bandit:
                        return Bandit == null || Bandit.Self == null;
                    case BanditTargetKind.Squad:
                        return !BanditSquad.All.Contains(Squad) || Squad.Members.Count == 0;
                    case BanditTargetKind.Event:
                        return !BanditEvent.All.Contains(Event);
                    case BanditTargetKind.Convoy:
                        return !BanditConvoy.All.Contains(Convoy);
                    case BanditTargetKind.Vehicle:
                        return BanditEvent.FindRide(Ride.Id) == null;
                    default:
                        return false;
                }
            }
        }

        /// <summary>Every bandit it covers that is still on the map, dead or alive.</summary>
        public List<BanditBotController> Members()
        {
            List<BanditBotController> members = new List<BanditBotController>();

            switch (Kind)
            {
                case BanditTargetKind.Bandit:
                    members.Add(Bandit);
                    break;

                case BanditTargetKind.Squad:
                    members.AddRange(Squad.Members);
                    break;

                case BanditTargetKind.Event:
                    AddSquads(members, Event);
                    break;

                case BanditTargetKind.Convoy:
                    AddSquads(members, Convoy.Event);
                    break;

                case BanditTargetKind.Vehicle:
                    members.AddRange(BanditEvent.CrewOf(Ride));
                    break;

                case BanditTargetKind.Team:
                    Steamworks.CSteamID group = BanditTeams.GroupIdOf(Team);
                    foreach (BanditBotController bandit in FakePlayerSpawner.GetActiveControllers())
                    {
                        if (bandit.Self.quests != null && bandit.Self.quests.groupID == group)
                        {
                            members.Add(bandit);
                        }
                    }
                    break;

                default:
                    members.AddRange(FakePlayerSpawner.GetActiveControllers());
                    break;
            }

            members.RemoveAll(bandit => bandit == null || bandit.Self == null);
            return members;
        }

        private static void AddSquads(List<BanditBotController> members, BanditEvent banditEvent)
        {
            foreach (BanditSquad squad in banditEvent.Squads)
            {
                members.AddRange(squad.Members);
            }
        }
    }

    /// <summary>
    /// Reads targets off the front of a command, and remembers the last thing spawned - which is
    /// what an order with no target is given to.
    ///
    ///   7              bandit #7
    ///   squad 2        squad 2, and the same shape for event, convoy and vehicle
    ///   team red       every bandit on a team
    ///   all            every bandit there is
    ///   last           the last thing spawned, which is also what no target at all means
    /// </summary>
    public static class BanditTargets
    {
        /// <summary>The last thing any /bandit ... spawn put down.</summary>
        public static BanditTarget Last { get; private set; }

        public static void Remember(BanditTarget target)
        {
            Last = target;
        }

        /// <summary>Forgets the last thing spawned, for when everything has been cleared.</summary>
        public static void Forget()
        {
            Last = null;
        }

        /// <summary>
        /// Reads a target starting at <paramref name="start"/>.
        /// </summary>
        /// <returns>False only when something there looks like a target but does not name one that
        /// exists - "squad 9" with no squad 9. No target at all is true with
        /// <paramref name="consumed"/> 0, so the caller can fall back on <see cref="Last"/>.</returns>
        public static bool TryParse(string[] args, int start, out BanditTarget target, out int consumed,
            out string error)
        {
            target = null;
            consumed = 0;
            error = null;

            if (args == null || start >= args.Length)
            {
                return true;
            }

            string word = args[start].ToLowerInvariant();
            string next = start + 1 < args.Length ? args[start + 1] : null;

            if (int.TryParse(word.TrimStart('#'), out int banditId))
            {
                consumed = 1;
                return TryFindBandit(banditId, out target, out error);
            }

            switch (word)
            {
                case "all":
                    consumed = 1;
                    target = BanditTarget.AllBandits;
                    return true;

                case "last":
                    consumed = 1;
                    return TryLast(out target, out error);

                case "team":
                    if (next == null)
                    {
                        return true;
                    }

                    BanditTeam team = BanditTeams.Find(BanditPlugin.Instance.Configuration.Instance, next);
                    if (team == null)
                    {
                        consumed = 2;
                        error = $"No team called '{next}'. Teams: "
                            + $"{string.Join(", ", BanditTeams.Names(BanditPlugin.Instance.Configuration.Instance).ToArray())}.";
                        return false;
                    }

                    consumed = 2;
                    target = BanditTarget.Of(team);
                    return true;

                case "squad":
                case "event":
                case "convoy":
                case "vehicle":
                    if (next == null || !int.TryParse(next, out int id))
                    {
                        consumed = 1;
                        error = $"'{word}' takes a number or 'spawn': /bandit {word} <number> <order>, "
                            + $"or /bandit {word} spawn ... - /bandit list shows the numbers.";
                        return false;
                    }

                    consumed = 2;
                    return TryFindGroup(word, id, out target, out error);

                default:
                    return true;
            }
        }

        /// <summary>
        /// Reads a target that is all that is left of the command - "clear squad 2", "debug status
        /// 7" - falling back on the last thing spawned when there is none, and failing, with a
        /// message ready to show, when there is nothing at all. Anything after the target, or a
        /// word that is not one, is refused rather than ignored: "clear sqaud 2" quietly clearing
        /// the last thing spawned instead would be the worst way for a typo to go.
        /// </summary>
        public static bool TryResolve(string[] args, int start, out BanditTarget target, out int consumed,
            out string error)
        {
            if (!TryParse(args, start, out target, out consumed, out error))
            {
                return false;
            }

            int length = args?.Length ?? 0;
            if (start + consumed < length)
            {
                target = null;
                error = $"'{args[start + consumed]}' is not a target. Targets: a bandit's number, squad <n>, "
                    + "event <n>, convoy <n>, vehicle <n>, team <name>, all, last.";
                return false;
            }

            return target != null || TryLast(out target, out error);
        }

        /// <summary>The last thing spawned, or false with a message ready to show if there is
        /// none or it is gone.</summary>
        public static bool TryLast(out BanditTarget target, out string error)
        {
            target = Last;
            error = null;

            if (target == null)
            {
                error = BanditCommand.NothingSpawned;
                return false;
            }

            if (target.IsGone)
            {
                error = $"{target.Label} - the last thing spawned - is gone. /bandit list shows what is left.";
                target = null;
                return false;
            }

            return true;
        }

        private static bool TryFindBandit(int id, out BanditTarget target, out string error)
        {
            target = null;
            error = null;

            foreach (BanditBotController bandit in FakePlayerSpawner.GetActiveControllers())
            {
                if (bandit.Id == id)
                {
                    target = BanditTarget.Of(bandit);
                    return true;
                }
            }

            error = $"No bandit #{id}. /bandit list shows who is on the map.";
            return false;
        }

        private static bool TryFindGroup(string kind, int id, out BanditTarget target, out string error)
        {
            target = null;
            error = null;

            switch (kind)
            {
                case "squad":
                    foreach (BanditSquad squad in BanditSquad.All)
                    {
                        if (squad.Id == id && squad.Members.Count > 0)
                        {
                            target = BanditTarget.Of(squad);
                        }
                    }
                    break;

                case "event":
                    foreach (BanditEvent banditEvent in BanditEvent.All)
                    {
                        if (banditEvent.Id == id)
                        {
                            target = BanditTarget.Of(banditEvent);
                        }
                    }
                    break;

                case "convoy":
                    foreach (BanditConvoy convoy in BanditConvoy.All)
                    {
                        if (convoy.Id == id)
                        {
                            target = BanditTarget.Of(convoy);
                        }
                    }
                    break;

                case "vehicle":
                    BanditEvent.Ride ride = BanditEvent.FindRide(id);
                    if (ride != null)
                    {
                        target = BanditTarget.Of(ride);
                    }
                    break;
            }

            if (target == null)
            {
                error = $"No {kind} {id}. /bandit list shows what is on the map.";
                return false;
            }

            return true;
        }

        /// <summary>The event a convoy runs on, or null - so an event can be treated as the convoy
        /// it really is.</summary>
        public static BanditConvoy ConvoyOf(BanditEvent banditEvent)
        {
            foreach (BanditConvoy convoy in BanditConvoy.All)
            {
                if (convoy.Event == banditEvent)
                {
                    return convoy;
                }
            }

            return null;
        }

        /// <summary>The ride a bandit is the driver of, in any running event, or null.</summary>
        public static BanditEvent.Ride RideDrivenBy(BanditBotController bandit)
        {
            foreach (BanditEvent banditEvent in BanditEvent.All)
            {
                foreach (BanditEvent.Ride ride in banditEvent.Rides)
                {
                    if (ride.Driver == bandit)
                    {
                        return ride;
                    }
                }
            }

            return null;
        }
    }
}
