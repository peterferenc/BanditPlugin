using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit spawn [&lt;kit&gt;] [team:&lt;team&gt;]" - one bandit where you are looking.
    ///
    /// A bandit spawned this way does nothing until told: it stands, tracks whoever it can see, and
    /// waits. That makes each behaviour something you switch on and watch in isolation, which is
    /// the whole point of a test bot - see <see cref="BanditOrders"/>. It becomes the last thing
    /// spawned, so the orders that follow go to it without a number.
    /// </summary>
    public static class BanditSpawnCommand
    {
        public static void Execute(IRocketPlayer caller, string[] args)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            args = BanditTeams.ExtractTeamArgument(args, out string requestedTeam);

            BanditTeam team = BanditTeams.Find(config, requestedTeam);
            if (team == null && !string.IsNullOrEmpty(requestedTeam))
            {
                Reply(caller, $"No team called '{requestedTeam}'. Teams: "
                    + $"{string.Join(", ", BanditTeams.Names(config).ToArray())}.", Color.red);
                return;
            }

            BanditKit kit = config.FindKit(config.DefaultKit);
            if (args.Length > 0)
            {
                kit = config.FindKit(args[0]);
                if (kit == null)
                {
                    Reply(caller, $"No kit called '{args[0]}'. Known kits: "
                        + $"{string.Join(", ", config.KitNames().ToArray())}. /bandit info kits lists them.", Color.red);
                    return;
                }
            }

            UnturnedPlayer callerPlayer = RequirePlayer(caller, "Spawning a bandit");
            if (callerPlayer == null)
            {
                return;
            }

            Spawn(caller, callerPlayer.Player, kit, team, config);
        }

        private static void Spawn(IRocketPlayer caller, Player unturnedPlayer, BanditKit kit, BanditTeam team,
            BanditConfiguration config)
        {
            Vector3 origin = unturnedPlayer.look.aim.position;
            Vector3 direction = unturnedPlayer.look.aim.forward;

            Vector3 spawnPosition;
            if (Physics.Raycast(origin, direction, out RaycastHit hit, 50f, RayMasks.BLOCK_COLLISION))
            {
                spawnPosition = hit.point;
            }
            else
            {
                spawnPosition = unturnedPlayer.transform.position + unturnedPlayer.transform.forward * 3f;
            }

            // Face the bandit back toward whoever spawned it. Its target scan will override this as
            // soon as it sees someone, but it gives it a sane initial facing.
            float facingAngleDegrees = unturnedPlayer.transform.eulerAngles.y + 180f;

            // The class and the side go in the name, because that is the one label visible from
            // across a field and through a scope - which is the only practical way to tell five of
            // them apart once a squad is on the ground, or to tell two sides apart once both are.
            BanditTeam spawnTeam = team ?? BanditTeams.Default(config);
            string kitName = kit != null && !string.IsNullOrEmpty(kit.Name) ? kit.Name : null;
            string prefix = spawnTeam != null ? spawnTeam.Label : "Bandit";
            string displayName = kitName != null ? $"{prefix} {kitName}" : prefix;

            Player bandit = FakePlayerSpawner.Spawn(spawnPosition, facingAngleDegrees, displayName, kit, spawnTeam);
            BanditBotController controller = bandit != null ? FakePlayerSpawner.LastSpawnedController : null;
            if (controller == null)
            {
                Reply(caller, "Failed to spawn bandit - see server console for details.", Color.red);
                return;
            }

            BanditTargets.Remember(BanditTarget.Of(controller));

            string orders = controller.Brain != null
                ? DescribeStandingOrders(controller)
                : "no standing orders yet";

            Reply(caller, $"Spawned bandit #{controller.Id} ({displayName}) on team {BanditTeams.Describe(bandit)} "
                + $"({orders}).", Color.green);
        }

        /// <summary>
        /// What the kit already switched on, so it is obvious which of the usual orders still need
        /// giving by hand. A kit that turns nothing on behaves exactly as a bandit always did.
        /// </summary>
        private static string DescribeStandingOrders(BanditBotController bandit)
        {
            List<string> orders = new List<string>();
            if (!bandit.HoldFire)
            {
                orders.Add("weapons free");
            }
            if (bandit.Brain.CoverEnabled)
            {
                orders.Add("cover");
            }
            if (bandit.Brain.PeekEnabled)
            {
                orders.Add("peek");
            }
            if (bandit.Brain.ProneEnabled)
            {
                orders.Add("prone");
            }

            return orders.Count > 0
                ? string.Join(", ", orders.ToArray())
                : "holding fire, no orders";
        }

        /// <summary>"/bandit info kits" - the kits, and the ranges each fights at.</summary>
        public static void ReplyKits(IRocketPlayer caller, BanditConfiguration config)
        {
            List<string> names = config.KitNames();
            if (names.Count == 0)
            {
                Reply(caller, "No kits configured.", Color.red);
                return;
            }

            Reply(caller, $"Kits: {string.Join(", ", names.ToArray())}. "
                + $"'/bandit spawn' alone spawns '{config.DefaultKit}'.", Color.white);

            foreach (string name in names)
            {
                // Resolved rather than read off the kit, so a kit that leaves a figure at -1 to
                // inherit the global one reports the number it will actually fight with.
                BanditProfile profile = BanditProfile.FromKit(config, config.FindKit(name));
                Reply(caller, $"  {name}: fires to {profile.FireRange:0}m, notices at {profile.TargetAcquireRange:0}m, "
                    + $"fights at {profile.PreferredEngagementRange:0}m"
                    + (profile.BurstFire ? ", bursts" : ", single shots")
                    + (profile.AdvanceOnTarget ? ", closes in" : string.Empty), Color.grey);
            }
        }
    }
}
