using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditv" - puts the last spawned bandit into a vehicle, or takes it out again.
    ///
    ///   /banditv drive    climb into the driver seat of the nearest vehicle and hold it there
    ///   /banditv gunner   climb into the F2 seat and keep it pointed at the nearest player
    ///   /banditv exit     get out
    ///
    /// Acts on the last spawned bandit rather than all of them, like /banditprone and /banditcover:
    /// this is something you try on one bot and watch.
    ///
    /// Driving is deliberately nothing more than sitting still for now. A seated bandit stops
    /// walking, stops shooting and holds the vehicle exactly where it found it - which is the only
    /// way to see whether the server accepts a bot's driving packets at all before anything depends
    /// on it moving. /banditstatus reports the seat it ended up in.
    /// </summary>
    public class CommandBanditVehicle : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditv";
        public string Help => "Puts the last spawned bandit in the nearest driver or gunner seat, or takes it out.";
        public string Syntax => "<drive|gunner|exit>";
        public List<string> Aliases => new List<string> { "bv" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit?.Driver == null)
            {
                Reply(caller, "No bandit to command - spawn one with /bandit first.", Color.red);
                return;
            }

            if (command.Length == 0)
            {
                Reply(caller, "Usage: /banditv <drive|gunner|exit>", Color.yellow);
                return;
            }

            switch (command[0].ToLowerInvariant())
            {
                case "drive":
                    Drive(caller, bandit);
                    return;

                case "gunner":
                case "gun":
                    Gun(caller, bandit);
                    return;

                case "exit":
                    Exit(caller, bandit);
                    return;

                default:
                    Reply(caller, "Usage: /banditv <drive|gunner|exit>", Color.yellow);
                    return;
            }
        }

        private static void Drive(IRocketPlayer caller, BanditBotController bandit)
        {
            string reason;
            if (!bandit.Driver.TryDrive(out reason))
            {
                Reply(caller, $"Bandit stayed on foot: {reason}.", Color.red);
                return;
            }

            // The seat change is applied by vanilla on the bandit's next input packet, up to
            // PlayerInput.RATE away, so reading movement.getVehicle() back here would still show
            // nothing. Report the order given; /banditstatus prints the seat it really ended up in.
            Reply(caller, $"Bandit taking the wheel of {reason}, and holding it there. "
                + "/banditvgoto sends it somewhere.", Color.green);
        }

        private static void Gun(IRocketPlayer caller, BanditBotController bandit)
        {
            string reason;
            if (!bandit.Driver.TryGun(out reason))
            {
                Reply(caller, $"Bandit stayed on foot: {reason}.", Color.red);
                return;
            }

            // Seat 1 is whatever the vehicle's second seat happens to be. In anything with a turret
            // behind the driver that is the gun; in a plain car it is the passenger seat, and the
            // bandit rides along watching instead. Which one it turned out to be is only knowable
            // once vanilla has applied the seat change, so /banditstatus is the honest answer.
            Reply(caller, $"Bandit into the F2 seat of {reason}, tracking the nearest player.", Color.green);
        }

        private static void Exit(IRocketPlayer caller, BanditBotController bandit)
        {
            string reason;
            if (!bandit.Driver.TryExit(out reason))
            {
                Reply(caller, $"Bandit stayed put: {reason}.", Color.red);
                return;
            }

            Reply(caller, $"Bandit out of {reason}.", Color.green);
        }

        private static void Reply(IRocketPlayer caller, string message, Color color)
        {
            if (caller is Rocket.Unturned.Player.UnturnedPlayer)
            {
                UnturnedChat.Say(caller, message, color);
            }
            else
            {
                Rocket.Core.Logging.Logger.Log($"[Bandit] {message}");
            }
        }
    }
}
