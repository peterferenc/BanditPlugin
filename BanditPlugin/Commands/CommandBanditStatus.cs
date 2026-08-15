using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditstatus" - what each bandit thinks it is doing.
    ///
    /// Movement failures are silent by nature: the bot just stands there, and nothing is logged
    /// because nothing went wrong. This prints the four things that actually explain it - which
    /// state the brain is in, whether it has a destination, whether it is pathing or steering
    /// blind, and whether its feet are being given a direction at all.
    /// </summary>
    public class CommandBanditStatus : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditstatus";
        public string Help => "Reports what each bandit is doing, for diagnosing movement.";
        public string Syntax => string.Empty;
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

            for (int i = 0; i < bandits.Count; i++)
            {
                BanditBotController bandit = bandits[i];
                if (bandit.Brain == null)
                {
                    Reply(caller, $"#{i + 1}: brain never initialised.", Color.red);
                    continue;
                }

                BanditBrain brain = bandit.Brain;
                string target = bandit.CurrentTarget != null
                    ? bandit.CurrentTarget.channel.owner.playerID.characterName
                    : "none";

                string destination = brain.Navigator.HasDestination
                    ? $"{Vector3.Distance(bandit.Self.transform.position, brain.Navigator.Destination):0}m " +
                      (brain.Navigator.IsFollowingPath ? "(A*)" : "(steering)")
                    : "none";

                Reply(caller,
                    $"#{i + 1}: {brain.State}, target {target}, dest {destination}, " +
                    $"holding {bandit.EquippedWeaponName}{(bandit.IsBursting ? " (bursting)" : string.Empty)}, " +
                    $"moving {(brain.MoveDirection.sqrMagnitude > 0.0001f ? "yes" : "no")}, " +
                    $"orders [{(bandit.HoldFire ? "hold fire" : "weapons free")}, " +
                    $"cover {(brain.CoverEnabled ? "on" : "off")}, " +
                    $"peek {(brain.PeekEnabled ? "on" : "off")}]" +
                    $"{(brain.PatrolEnabled ? ", patrolling" : string.Empty)}",
                    Color.white);
            }
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
