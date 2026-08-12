using System.Collections.Generic;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditwp add|remove|clear|list" - records the patrol route for the current map by walking
    /// it. Waypoints are saved to Rocket/Plugins/BanditPlugin/Waypoints/&lt;map&gt;.txt as they are
    /// edited, so a route survives a restart and can also be hand-edited.
    /// </summary>
    public class CommandBanditWaypoint : IRocketCommand
    {
        /// <summary>How near you have to stand to a waypoint to remove it.</summary>
        private const float RemoveRadius = 10f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditwp";
        public string Help => "Edits this map's bandit patrol waypoints at your feet.";
        public string Syntax => "add|remove|clear|list";
        public List<string> Aliases => new List<string> { "banditwaypoint" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            Vector3 position = ((UnturnedPlayer)caller).Player.transform.position;
            string action = command.Length > 0 ? command[0].ToLowerInvariant() : "list";

            switch (action)
            {
                case "add":
                    BanditWaypointStore.Add(position);
                    UnturnedChat.Say(caller, $"Waypoint {BanditWaypointStore.Current.Count} added.", Color.green);
                    break;

                case "remove":
                case "delete":
                    bool removed = BanditWaypointStore.RemoveNearest(position, RemoveRadius);
                    UnturnedChat.Say(caller,
                        removed ? "Waypoint removed." : $"No waypoint within {RemoveRadius:0}m.",
                        removed ? Color.green : Color.red);
                    break;

                case "clear":
                    int cleared = BanditWaypointStore.Clear();
                    UnturnedChat.Say(caller, $"Cleared {cleared} waypoint(s).", Color.green);
                    break;

                default:
                    IReadOnlyList<Vector3> waypoints = BanditWaypointStore.Current;
                    if (waypoints.Count == 0)
                    {
                        bool usesLocationNodes = BanditPlugin.Instance.Configuration.Instance.PatrolUseLocationNodesWhenNoWaypoints;
                        int fallback = BanditWaypointStore.GetRoute(usesLocationNodes).Count;
                        UnturnedChat.Say(caller, fallback > 0
                            ? $"No recorded waypoints; patrol would use this map's {fallback} location nodes."
                            : "No waypoints recorded on this map.", Color.yellow);
                        break;
                    }

                    UnturnedChat.Say(caller, $"{waypoints.Count} waypoint(s) on {BanditWaypointStore.CurrentMap}:", Color.green);
                    for (int i = 0; i < waypoints.Count; i++)
                    {
                        Vector3 waypoint = waypoints[i];
                        float distance = Vector3.Distance(position, waypoint);
                        UnturnedChat.Say(caller,
                            $"  {i + 1}. ({waypoint.x:0}, {waypoint.y:0}, {waypoint.z:0}) - {distance:0}m away",
                            Color.white);
                    }
                    break;
            }
        }
    }
}
