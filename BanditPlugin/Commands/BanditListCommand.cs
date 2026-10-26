using System.Collections.Generic;
using BanditPlugin.FakePlayer;
using Rocket.API;
using UnityEngine;
using static BanditPlugin.Commands.BanditCommand;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/bandit list" - everything on the map, by the numbers orders take.
    ///
    /// Grouped the way the things themselves nest: events and convoys, the squads inside them and
    /// the bandits inside those, and then whoever is on their own. It is the answer to "what do I
    /// type to talk to that one", so it is short on detail - /bandit debug status is the long form.
    /// </summary>
    public static class BanditListCommand
    {
        public static void Execute(IRocketPlayer caller)
        {
            List<BanditBotController> bandits = FakePlayerSpawner.GetActiveControllers();

            if (bandits.Count == 0)
            {
                Reply(caller, "Nothing on the map. /bandit spawn, or /bandit help.", Color.yellow);
                return;
            }

            Reply(caller, $"{bandits.Count} bandit(s) on the map.", Color.green);

            // Anything that has lost every man is left out: it can no longer take an order.
            foreach (BanditConvoy convoy in BanditConvoy.All)
            {
                if (convoy.Event.BanditCount == 0)
                {
                    continue;
                }

                Reply(caller, $"Convoy {convoy.Id} [{convoy.State}]: "
                    + BanditEventSpawnCommand.DescribeContents(convoy.Event), Color.cyan);
            }

            foreach (BanditEvent banditEvent in BanditEvent.All)
            {
                if (banditEvent.BanditCount > 0 && BanditTargets.ConvoyOf(banditEvent) == null)
                {
                    Reply(caller, $"Event {banditEvent.Id}: {BanditEventSpawnCommand.DescribeContents(banditEvent)}",
                        Color.cyan);
                }
            }

            foreach (BanditSquad squad in BanditSquad.All)
            {
                List<BanditBotController> members = squad.Members;
                if (members.Count > 0)
                {
                    Reply(caller, $"  Squad {squad.Id} ({squad.TypeName}): {DescribeMembers(members)}", Color.white);
                }
            }

            List<BanditBotController> alone = bandits.FindAll(bandit => bandit.Squad == null);
            if (alone.Count > 0)
            {
                Reply(caller, $"  On their own: {DescribeMembers(alone)}", Color.white);
            }

            BanditTarget last = BanditTargets.Last;
            if (last != null && !last.IsGone)
            {
                Reply(caller, $"Orders with no target go to {last.Label}, the last thing spawned.", Color.grey);
            }
        }
    }
}
