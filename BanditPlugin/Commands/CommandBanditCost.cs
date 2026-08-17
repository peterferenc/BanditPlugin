using System;
using System.Collections.Generic;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace BanditPlugin.Commands
{
    /// <summary>
    /// "/banditcost" - works out what everything ought to cost, from the guns' own numbers.
    ///
    ///   /banditcost           what the model suggests, beside what the configuration says
    ///   /banditcost apply     write the suggestions into the configuration
    ///
    /// A suggester rather than an automatic pricer, and the distinction is the whole design. Prices
    /// computed live would be worse in three separate ways: "/banditevent 400 seed:9" would stop
    /// reproducing the moment a workshop item updated underneath it, there would be no single number
    /// left to nudge when an event felt wrong, and the model would silently overrule your judgement
    /// on the things it cannot see. Suggesting, then writing an ordinary editable number, keeps all
    /// three. See <see cref="BanditCostModel"/> for what it can and cannot measure.
    ///
    /// Expect to disagree with it about the machinegunner. Priced on damage alone a Nykorev at 11
    /// rounds and a 0.22 hit chance comes out below a Maplestrike, which is arithmetically right and
    /// tactically wrong - the gun's job is to pin people down, and suppression appears in no asset
    /// field anywhere. That is a good reason to read the column, not to write it back unexamined.
    /// </summary>
    public class CommandBanditCost : IRocketCommand
    {
        public AllowedCaller AllowedCaller => AllowedCaller.Both;
        public string Name => "banditcost";
        public string Help => "Suggests point costs for every kit and vehicle from the game's own asset data.";
        public string Syntax => "[apply]";
        public List<string> Aliases => new List<string> { "bcost", "costs" };
        public List<string> Permissions => new List<string> { "bandit.spawn" };

        /// <summary>
        /// How far a suggestion has to be from the current price before it is worth pointing at.
        /// Below this the two agree closely enough that flagging it is noise.
        /// </summary>
        private const float NotableRatio = 1.5f;

        public void Execute(IRocketPlayer caller, string[] command)
        {
            BanditConfiguration config = BanditPlugin.Instance.Configuration.Instance;

            List<BanditCostEstimate> kits = BanditCostModel.EstimateKits(config);
            List<BanditCostEstimate> vehicles = BanditCostModel.EstimateVehicles(config);

            bool apply = command.Length > 0
                && command[0].Equals("apply", StringComparison.OrdinalIgnoreCase);

            string anchor = !string.IsNullOrEmpty(config.CostModelAnchorKit)
                ? config.CostModelAnchorKit
                : config.DefaultKit;

            Reply(caller, $"Costs scaled so '{anchor}' = {config.CostModelAnchorPoints:0.#} pts, "
                + $"reach baseline {config.CostModelReachBaseline:0}m. "
                + "'now' is your configuration, 'model' is the estimate.", Color.white);

            ReportKits(caller, kits);
            ReportVehicles(caller, vehicles);

            if (!apply)
            {
                Reply(caller, "Nothing written. /banditcost apply writes the model column into the "
                    + "configuration - read it first, especially any suppression class.", Color.grey);
                return;
            }

            Apply(caller, config, kits, vehicles);
        }

        private void ReportKits(IRocketPlayer caller, List<BanditCostEstimate> kits)
        {
            Reply(caller, "Kits:", Color.white);

            foreach (BanditCostEstimate estimate in kits)
            {
                if (!estimate.IsPriced)
                {
                    Reply(caller, $"  {estimate.Name}: cannot price - {estimate.Problem}.", Color.yellow);
                    continue;
                }

                Reply(caller, $"  {estimate.Name}: now {estimate.Current:0.#}, model {estimate.Suggested:0.#}"
                    + Disagreement(estimate) + $"  [{estimate.Working}]", Color.grey);
            }
        }

        private void ReportVehicles(IRocketPlayer caller, List<BanditCostEstimate> vehicles)
        {
            Reply(caller, "Vehicles (platform only - crew is priced as kits on top):", Color.white);

            foreach (BanditCostEstimate estimate in vehicles)
            {
                if (!estimate.IsPriced)
                {
                    Reply(caller, $"  {estimate.Name}: cannot price - {estimate.Problem}.", Color.yellow);
                    continue;
                }

                Reply(caller, $"  {estimate.Name}: now {estimate.Current:0.#}, model {estimate.Suggested:0.#}"
                    + Disagreement(estimate) + $"  [{estimate.Working}]", Color.grey);
            }
        }

        /// <summary>
        /// Points at a suggestion that disagrees sharply with the configured price. Those are the
        /// only rows worth arguing about - and the row where the model is wrong rather than the
        /// configuration is usually one of these.
        /// </summary>
        private static string Disagreement(BanditCostEstimate estimate)
        {
            float ratio = estimate.Ratio;
            if (ratio <= 0f)
            {
                return string.Empty;
            }

            if (ratio >= NotableRatio)
            {
                return $" (model says {ratio:0.#}x dearer)";
            }

            if (ratio <= 1f / NotableRatio)
            {
                return $" (model says {1f / ratio:0.#}x cheaper)";
            }

            return string.Empty;
        }

        /// <summary>
        /// Writes the suggestions in and saves. Only things that could actually be priced are
        /// touched - a kit whose gun did not resolve keeps whatever it had, since overwriting it
        /// with a zero would quietly remove it from every event.
        /// </summary>
        private void Apply(IRocketPlayer caller, BanditConfiguration config,
            List<BanditCostEstimate> kits, List<BanditCostEstimate> vehicles)
        {
            int changed = 0;

            foreach (BanditCostEstimate estimate in kits)
            {
                if (!estimate.IsPriced || estimate.Suggested <= 0f)
                {
                    continue;
                }

                BanditKit kit = config.FindKit(estimate.Name);
                if (kit != null)
                {
                    kit.Cost = Round(estimate.Suggested);
                    changed++;
                }
            }

            foreach (BanditCostEstimate estimate in vehicles)
            {
                if (!estimate.IsPriced || estimate.Suggested <= 0f)
                {
                    continue;
                }

                BanditVehicleType vehicle = config.FindVehicle(estimate.Name);
                if (vehicle != null)
                {
                    vehicle.Cost = Round(estimate.Suggested);
                    changed++;
                }
            }

            BanditPlugin.Instance.Configuration.Save();

            Reply(caller, $"Wrote {changed} price(s) into the configuration. Squad costs follow their "
                + "members automatically, so they have moved too - /banditevent check shows the new "
                + "table, and any MinEventCost floors are unchanged and may now need revisiting.", Color.green);
        }

        /// <summary>
        /// Prices are rounded to something a person would have typed. A cost of 23.7418 is not more
        /// accurate than 24 - the model is an estimate either way - and a configuration full of
        /// six-decimal numbers is one nobody will hand-edit afterwards, which defeats the point of
        /// suggesting rather than computing.
        /// </summary>
        private static float Round(float value)
        {
            return value >= 10f ? Mathf.Round(value) : Mathf.Round(value * 10f) / 10f;
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
