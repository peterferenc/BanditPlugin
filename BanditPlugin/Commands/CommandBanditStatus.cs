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
            // Convoys first: a column reports as one thing doing one thing, and reading it off
            // twenty individual bandits is not possible - none of them knows the state the convoy
            // is in.
            foreach (BanditConvoy convoy in BanditConvoy.All)
            {
                Reply(caller, convoy.Describe(), Color.cyan);
            }

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

                // The stance vanilla settled on, not the one the brain asked for. They can differ -
                // PlayerStance refuses to duck in shallow water, and a crouch input beats a prone
                // one - so this is the only honest answer to "did /banditprone take?".
                string stance = bandit.Self.stance != null
                    ? bandit.Self.stance.stance.ToString()
                    : "unknown";

                string kit = bandit.Profile != null ? bandit.Profile.KitName : "default";

                // Read off the bandit's live group rather than what it was spawned with, so a team
                // changed underneath it - by /banditteam, or by the group being deleted - shows the
                // side it is actually fighting on.
                string team = BanditTeams.Describe(bandit.Self);
                // The type as well as the number: two squads on the ground are usually two different
                // types, and which one a bandit belongs to explains the figures it is fighting with.
                string squad = bandit.Squad != null
                    ? $" sq{bandit.Squad.Id} {bandit.Squad.TypeName}"
                    : string.Empty;

                Reply(caller,
                    $"#{i + 1} [{kit}{squad} team {team}]: {brain.State}, target {target}, dest {destination}, " +
                    $"holding {bandit.EquippedWeaponName}{(bandit.IsBursting ? " (bursting)" : string.Empty)}" +
                    $"{(bandit.IsSuppressingFire ? " (suppressing)" : string.Empty)}, " +
                    $"stance {stance}, " +
                    $"moving {(brain.MoveDirection.sqrMagnitude > 0.0001f ? "yes" : "no")}, " +
                    $"orders [{(bandit.HoldFire ? "hold fire" : "weapons free")}, " +
                    $"cover {(brain.CoverEnabled ? "on" : "off")}, " +
                    $"peek {(brain.PeekEnabled ? "on" : "off")}, " +
                    $"stance {brain.StanceOrder}]" +
                    $"{(brain.PatrolEnabled ? ", patrolling" : string.Empty)}" +
                    $", fire: {bandit.DescribeFireBlock()}",
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
