using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditpatrol [on|off]" - starts or stops every live bandit walking the current map's
    /// waypoint route. With no argument it toggles.
    ///
    /// Applies to all bandits rather than just the last one, because a patrol is a standing order
    /// for the map: you record a route once with /banditwp and then set everyone walking it.
    /// </summary>
    public class CommandBanditPatrol : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditpatrol";
        public string Help => "Starts or stops all bandits patrolling this map's waypoints.";
        public string Syntax => "[on|off]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            List<BanditBotController> bandits = FakePlayerSpawner.GetActiveControllers();
            if (bandits.Count == 0)
            {
                Reply(caller, "No bandits spawned.", Color.red);
                return;
            }

            bool enable;
            if (command.Length > 0 && command[0].Equals("on", System.StringComparison.OrdinalIgnoreCase))
            {
                enable = true;
            }
            else if (command.Length > 0 && command[0].Equals("off", System.StringComparison.OrdinalIgnoreCase))
            {
                enable = false;
            }
            else
            {
                // Toggle off the first bandit's current state, so the command is idempotent-ish
                // when they're all in the same state (which they normally are).
                enable = bandits[0].Brain == null || !bandits[0].Brain.PatrolEnabled;
            }

            int waypointCount = BanditWaypointStore.GetRoute(
                BanditPlugin.Instance.Configuration.Instance.PatrolUseLocationNodesWhenNoWaypoints).Count;

            if (enable && waypointCount == 0)
            {
                Reply(caller, "No waypoints on this map - record some with /banditwp add.", Color.red);
                return;
            }

            int affected = 0;
            foreach (BanditBotController bandit in bandits)
            {
                if (bandit.Brain == null)
                {
                    continue;
                }
                bandit.Brain.SetPatrol(enable);
                affected++;
            }

            Reply(caller, enable
                ? $"{affected} bandit(s) patrolling {waypointCount} waypoint(s)."
                : $"{affected} bandit(s) holding position.", Color.green);
        }
    }
}
