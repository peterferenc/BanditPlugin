using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using BanditPlugin.Navigation;
using Rocket.API;
using Rocket.Unturned.Chat;
using Rocket.Unturned.Player;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

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
        public AllowedCaller AllowedCaller => AllowedCaller.Player;
        public string Name => "banditcover";
        public string Help => "Makes the last spawned bandit take cover from you, and reports what it found. 'clear' removes the markers.";
        public string Syntax => "[clear]";
        public List<string> Aliases => new List<string>();
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        public void Execute(IRocketPlayer caller, string[] command)
        {
            if (command.Length > 0 && command[0].Equals("clear", System.StringComparison.OrdinalIgnoreCase))
            {
                BanditCoverDebug.Clear();
                UnturnedChat.Say(caller, "Cover markers cleared.", Color.green);
                return;
            }

            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;
            BanditBotController bandit = FakePlayerSpawner.LastSpawnedController;
            if (bandit?.Brain == null)
            {
                UnturnedChat.Say(caller, NoBandit, Color.red);
                return;
            }

            Player callerPlayer = ((UnturnedPlayer)caller).Player;
            Vector3 threatEye = BanditGeometry.EyeOf(callerPlayer);

            List<BanditCoverCandidateReport> reports = new List<BanditCoverCandidateReport>();
            bool found = bandit.Brain.TryTakeCoverFrom(threatEye, out BanditCoverSearchStats stats, reports);

            Vector3? chosen = found ? bandit.Brain.CurrentCover.Position : (Vector3?)null;
            int drawn = BanditCoverDebug.Show(reports, chosen, config.CoverDebugSeconds, config.CoverDebugMaxMarkers);

            // Also to the server console: chat truncates, and the tally is the whole diagnostic.
            Rocket.Core.Logging.Logger.Log($"[Bandit] Cover search from {threatEye} for bandit at "
                + $"{bandit.Self.transform.position}: {stats}"
                + (found ? $" -> chose {bandit.Brain.CurrentCover.Position}" : " -> nothing")
                + $" (drew {drawn} of {reports.Count} markers)");

            UnturnedChat.Say(caller, stats.ToString(), Color.white);

            // Say so when the drawing is a sample. The tallies above always cover the whole search,
            // so without this a capped draw reads as a smaller search than actually ran.
            if (drawn < reports.Count)
            {
                UnturnedChat.Say(caller,
                    $"Drawing {drawn} of {reports.Count} candidates ({BanditCoverDebug.SplatterCount(drawn)} decals) - "
                    + "all viable spots plus an even sample of the rejects. Raise CoverDebugMaxMarkers to see more.",
                    Color.grey);
            }

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
