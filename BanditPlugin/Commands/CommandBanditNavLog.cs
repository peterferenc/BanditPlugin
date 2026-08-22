using System;
using System.Collections.Generic;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditnavlog on|off" - the running commentary from every driving bandit.
    ///
    /// A development switch rather than a setting, and deliberately not in the configuration file:
    /// the moment you want it is the moment a vehicle is doing something inexplicable in front of
    /// you, and by the time the server has restarted to pick up a config change the convoy is gone.
    ///
    /// What it costs is a line per driving vehicle per half second in Rocket's log, plus the event
    /// lines - a stall, a recovery, a refusal, a give-up - which are written whenever it is on and
    /// are the ones actually worth reading. See <see cref="BanditNavLog"/>.
    /// </summary>
    public class CommandBanditNavLog : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditnavlog";
        public string Help => "Turns the vehicle navigation commentary in the server log on or off.";
        public string Syntax => "[on|off|route [seconds]|clear]";
        public List<string> Aliases => new List<string> { "bnavlog" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (command.Length == 0)
            {
                Reply(caller, $"Navigation logging is {(BanditNavLog.Enabled ? "on" : "off")}. "
                    + "/banditnavlog on|off.", Color.yellow);
                return;
            }

            string word = command[0];

            if (word.Equals("route", StringComparison.OrdinalIgnoreCase)
                || word.Equals("show", StringComparison.OrdinalIgnoreCase))
            {
                ShowRoute(caller, command);
                return;
            }

            if (word.Equals("clear", StringComparison.OrdinalIgnoreCase))
            {
                BanditRouteDebug.Clear();
                Reply(caller, "Route markers cleared.", Color.green);
                return;
            }

            bool on;

            if (word.Equals("on", StringComparison.OrdinalIgnoreCase)
                || word.Equals("true", StringComparison.OrdinalIgnoreCase) || word == "1")
            {
                on = true;
            }
            else if (word.Equals("off", StringComparison.OrdinalIgnoreCase)
                || word.Equals("false", StringComparison.OrdinalIgnoreCase) || word == "0")
            {
                on = false;
            }
            else
            {
                Reply(caller, "Usage: /banditnavlog on|off.", Color.red);
                return;
            }

            BanditNavLog.Enabled = on;

            if (!on)
            {
                BanditNavLog.Reset();
            }

            Reply(caller, on
                ? "Navigation logging on - every driving bandit reports to the server log twice a "
                    + "second, and stalls, refusals and gives-up whenever they happen. "
                    + "/banditnavlog route draws the planned route on the ground."
                : "Navigation logging off.", Color.green);
        }

        /// <summary>
        /// Paints the last planned route on the ground.
        ///
        /// The route the *planner* produced, not one re-planned now: by the time anybody wants to
        /// look at a route, the interesting thing has already happened, and re-planning from where
        /// the vehicle currently is would answer a different question than the one being asked.
        /// </summary>
        private static void ShowRoute(IRocketPlayer caller, string[] command)
        {
            IReadOnlyList<BanditRouteDebug.Marker> plan = BanditRouteDebug.LastPlan;

            if (plan == null || plan.Count == 0)
            {
                Reply(caller, "No route has been planned yet. Send a vehicle with /banditvgoto marker, "
                    + "/banditvgoto wp or /banditevent convoy first.", Color.yellow);
                return;
            }

            float seconds = 60f;
            if (command.Length > 1 && float.TryParse(command[1], out float requested) && requested > 0f)
            {
                seconds = Mathf.Min(requested, 600f);
            }

            List<BanditRouteDebug.Marker> markers = new List<BanditRouteDebug.Marker>(plan);

            if (BanditRouteDebug.CurrentTarget.HasValue)
            {
                markers.Add(new BanditRouteDebug.Marker
                {
                    Position = BanditRouteDebug.CurrentTarget.Value,
                    Kind = BanditRouteDebug.MarkerKind.Current
                });
            }

            int drawn = BanditRouteDebug.Show(markers, seconds, BanditRouteDebug.DefaultMaxMarkers);

            Reply(caller, $"Route: {plan.Count} planned point(s), {drawn} marker(s) drawn for "
                + $"{seconds:0}s.", Color.green);

            Reply(caller, "  blue road point, yellow corner arc, red waypoint, green where it is "
                + "steering now."
                + (drawn < plan.Count ? " Thinned to fit the marker budget - every waypoint is still "
                    + "drawn." : string.Empty), Color.grey);
        }

        private static void Reply(IRocketPlayer caller, string message, Color colour)
        {
            if (caller is ConsolePlayer)
            {
                Rocket.Core.Logging.Logger.Log(message);
                return;
            }

            UnturnedChat.Say(caller, message, colour);
        }
    }
}
