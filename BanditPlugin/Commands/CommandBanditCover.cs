using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditcover" - makes the last spawned bandit take cover from *you*, right now, and says
    /// what it found.
    ///
    /// Cover normally only triggers when the bot is exposed and its search timer is up, which
    /// makes it awkward to observe and impossible to debug: a search that finds nothing looks
    /// exactly like a search that never ran. This forces one and reports the outcome - which spot,
    /// what kind, how far - or, when it finds nothing, the tally of which test rejected the
    /// candidates.
    /// </summary>
    public class CommandBanditCover : IRocketCommand
    {
        /// <summary>How long the candidate markers keep pulsing, so you can walk around them.</summary>
        private const float MarkerSeconds = 12f;

        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditcover";
        public string Help => "Makes the last spawned bandit take cover from you, and reports what it found.";
        public string Syntax => string.Empty;
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit?.Brain == null)
            {
                UnturnedChat.Say(caller, "No bandit to command - spawn one with /bandit first.", Color.red);
                return;
            }

            Player callerPlayer = ((UnturnedPlayer)caller).Player;
            Vector3 threatEye = callerPlayer.look != null && callerPlayer.look.aim != null
                ? callerPlayer.look.aim.position
                : callerPlayer.transform.position + Vector3.up * 1.5f;

            List<BanditCoverCandidateReport> reports = new List<BanditCoverCandidateReport>();
            bool found = bandit.Brain.TryTakeCoverFrom(threatEye, out BanditCoverSearchStats stats, reports);

            Vector3? chosen = found ? bandit.Brain.CurrentCover.Position : (Vector3?)null;
            BanditCoverDebug.Show(reports, chosen, MarkerSeconds);

            // Also to the server console: chat truncates, and the tally is the whole diagnostic.
            Rocket.Core.Logging.Logger.Log($"[Bandit] Cover search from {threatEye} for bandit at "
                + $"{bandit.Self.transform.position}: {stats}"
                + (found ? $" -> chose {bandit.Brain.CurrentCover.Position}" : " -> nothing"));

            UnturnedChat.Say(caller, stats.ToString(), Color.white);
            UnturnedChat.Say(caller, BanditCoverDebug.Legend, Color.grey);

            if (!found)
            {
                UnturnedChat.Say(caller, "No cover found.", Color.yellow);
                return;
            }

            BanditCoverSpot spot = bandit.Brain.CurrentCover;
            float distance = Vector3.Distance(bandit.Self.transform.position, spot.Position);

            string kind = spot.RequiresCrouch
                ? "crouch cover (safe ducked, can shoot standing)"
                : spot.CanPeek
                    ? "hard cover (hidden; steps out to shoot)"
                    : "hard cover (hidden, no firing angle)";

            UnturnedChat.Say(caller, $"Taking {kind} - {distance:0.#}m away.", Color.green);
        }
    }
}
