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
        public string Syntax => "[on|off]";
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
                    + "second, and stalls, refusals and gives-up whenever they happen."
                : "Navigation logging off.", Color.green);
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
