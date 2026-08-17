using System;
using System.Collections.Generic;

namespace BanditPlugin
{
    /// <summary>
    /// What an event decided to buy, before anything is put on the ground.
    ///
    /// Kept separate from the spawning on purpose: the draw is the part with the interesting
    /// decisions in it and none of the side effects, so it can be reasoned about - and re-run from
    /// the same seed - without a single bandit existing.
    /// </summary>
    public sealed class BanditEventPlan
    {
        /// <summary>The squad types drawn, in the order they were bought.</summary>
        public readonly List<BanditSquadType> Squads = new List<BanditSquadType>();

        /// <summary>The vehicles drawn, each of which arrives with the crew its type names.</summary>
        public readonly List<BanditVehicleType> Vehicles = new List<BanditVehicleType>();

        /// <summary>
        /// The budget left over after no whole squad or vehicle was affordable, spent on individual
        /// bandits. These are attached to the last squad drawn - see <see cref="BanditEventDraw"/>.
        /// </summary>
        public readonly List<BanditKit> Loose = new List<BanditKit>();

        /// <summary>What was typed, what was spent, and what could not be.</summary>
        public float Budget;
        public float Spent;
        public float Unspent => Budget - Spent;

        /// <summary>The seed this draw ran from, so the same event can be run again.</summary>
        public int Seed;

        /// <summary>Total bandits, across squads, vehicle crews and the remainder.</summary>
        public int BanditCount;

        /// <summary>
        /// True when the draw stopped because it ran out of room for bandits rather than out of
        /// money. Worth saying out loud in the reply: an event that spent 300 of its 900 points
        /// because the server has eleven free slots is not a badly tuned config.
        /// </summary>
        public bool LimitedByBanditCap;
    }

    /// <summary>
    /// Spends an event's budget on squads, vehicles and men.
    ///
    /// Three rules do all the work here, and they are worth stating plainly because between them
    /// they are what makes "/banditevent 200" and "/banditevent 900" different in kind rather than
    /// only in size:
    ///
    ///   Cost says what is affordable. Every kit carries one, a squad's is the sum of its members'
    ///   and a vehicle's is its platform plus its crew, so nothing has a price that can drift away
    ///   from the thing it describes.
    ///
    ///   MinEventCost says what belongs. This is the rule that stops a large budget from simply
    ///   buying a great many riflemen: marksmen, machineguns and armour are held back until the
    ///   event is big enough that they are one element of a fight rather than the whole of it. It is
    ///   checked against the budget that was typed, not against what is left, so a type either
    ///   belongs in this event or it does not - it cannot become eligible by being drawn late.
    ///
    ///   Weight says how often. Ordinary things are drawn several times as often as specialists, so
    ///   most of what walks at you is ordinary.
    ///
    /// Squads are drawn whole rather than assembled a man at a time. A squad is not a container: it
    /// carries its own contact memory, cover separation and patience, and those are what make five
    /// men behave like a section instead of five men standing near each other. A bag of individually
    /// drawn kits would have no coherent answer for any of them. The leftover budget is then spent
    /// on individual bandits attached to the last squad, which is where the variation in squad size
    /// comes from: six points over after a rifle section buys a fifth and sixth rifleman.
    /// </summary>
    public static class BanditEventDraw
    {
        /// <summary>
        /// A backstop on the number of things one event may buy, whatever the numbers say.
        ///
        /// The loop below already terminates on its own - every purchase reduces the remaining
        /// budget, and nothing costing zero is ever eligible - but both of those are guarantees made
        /// by validation elsewhere, and a runaway spawn loop on a live server is a far worse outcome
        /// than an event that stops early.
        /// </summary>
        private const int MaxDraws = 512;

        /// <summary>
        /// Runs the draw.
        /// </summary>
        /// <param name="banditCap">
        /// The most bandits this event may spawn - the configured cap, or the server's free player
        /// slots, whichever is smaller. Bandits are real clients occupying real slots, so this is a
        /// hard limit rather than a preference.
        /// </param>
        public static BanditEventPlan Draw(BanditConfiguration config, float budget, int seed, int banditCap)
        {
            BanditEventPlan plan = new BanditEventPlan { Budget = budget, Seed = seed };
            Random random = new Random(seed);

            float remaining = budget;
            int vehicleCap = Math.Max(0, config.EventVehicleCap);

            // Squads and vehicles first, drawn against each other out of one pool so a big budget
            // can put its money into either. The cap on vehicles is what stops it putting all of it
            // into them: a budget alone has no opinion about the shape of what it buys.
            for (int draw = 0; draw < MaxDraws; draw++)
            {
                List<Candidate> candidates = new List<Candidate>();

                foreach (BanditSquadType squad in Enumerate(config.Squads))
                {
                    float cost = config.SquadCost(squad);
                    int size = CountMembers(config, squad);
                    if (IsEligible(squad.Weight, cost, squad.MinEventCost, budget, remaining)
                        && size > 0 && plan.BanditCount + size <= banditCap)
                    {
                        candidates.Add(new Candidate { Squad = squad, Cost = cost, Size = size, Weight = squad.Weight });
                    }
                }

                if (plan.Vehicles.Count < vehicleCap)
                {
                    foreach (BanditVehicleType vehicle in Enumerate(config.Vehicles))
                    {
                        float cost = config.VehicleCost(vehicle);
                        int size = CountCrew(config, vehicle);
                        if (IsEligible(vehicle.Weight, cost, vehicle.MinEventCost, budget, remaining)
                            && plan.BanditCount + size <= banditCap)
                        {
                            candidates.Add(new Candidate { Vehicle = vehicle, Cost = cost, Size = size, Weight = vehicle.Weight });
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    // Distinguishing "out of money" from "out of slots" is the difference between a
                    // reply that reads as a tuning problem and one that reads as a full server.
                    plan.LimitedByBanditCap = plan.BanditCount >= banditCap;
                    break;
                }

                Candidate picked = PickWeighted(candidates, random);
                remaining -= picked.Cost;
                plan.Spent += picked.Cost;
                plan.BanditCount += picked.Size;

                if (picked.Squad != null)
                {
                    plan.Squads.Add(picked.Squad);
                }
                else
                {
                    plan.Vehicles.Add(picked.Vehicle);
                }
            }

            // Then the remainder, one man at a time, which is what gives squads their odd sizes.
            for (int draw = 0; draw < MaxDraws; draw++)
            {
                List<Candidate> candidates = new List<Candidate>();

                foreach (BanditKit kit in Enumerate(config.Kits))
                {
                    float cost = BanditConfiguration.CostOf(kit);
                    if (IsEligible(kit.Weight, cost, kit.MinEventCost, budget, remaining)
                        && plan.BanditCount + 1 <= banditCap)
                    {
                        candidates.Add(new Candidate { Kit = kit, Cost = cost, Size = 1, Weight = kit.Weight });
                    }
                }

                if (candidates.Count == 0)
                {
                    plan.LimitedByBanditCap |= plan.BanditCount >= banditCap;
                    break;
                }

                Candidate picked = PickWeighted(candidates, random);
                remaining -= picked.Cost;
                plan.Spent += picked.Cost;
                plan.BanditCount += 1;
                plan.Loose.Add(picked.Kit);
            }

            EnsureNotEmpty(config, plan, banditCap);
            return plan;
        }

        /// <summary>
        /// An event that drew nothing at all still puts one bandit down.
        ///
        /// "/banditevent 5" against a rifleman costing 10 buys nothing, and a command that
        /// cheerfully reports having spawned an empty event is a command that looks broken. One man
        /// is both the honest minimum and the obvious reading of a very small number.
        ///
        /// The cheapest kit is taken rather than a random one, and its unlock floor is ignored -
        /// nothing here is affordable by definition, so weighing the choice would be pretending at a
        /// decision that has already been made.
        /// </summary>
        private static void EnsureNotEmpty(BanditConfiguration config, BanditEventPlan plan, int banditCap)
        {
            if (plan.BanditCount > 0 || banditCap < 1)
            {
                return;
            }

            BanditKit cheapest = null;
            float cheapestCost = float.MaxValue;

            foreach (BanditKit kit in Enumerate(config.Kits))
            {
                float cost = BanditConfiguration.CostOf(kit);
                if (kit.Weight > 0f && cost > 0f && cost < cheapestCost)
                {
                    cheapest = kit;
                    cheapestCost = cost;
                }
            }

            if (cheapest == null)
            {
                return;
            }

            plan.Loose.Add(cheapest);
            plan.Spent += cheapestCost;
            plan.BanditCount = 1;
        }

        /// <summary>
        /// Whether a thing may be drawn at all: it must cost something, be affordable out of what is
        /// left, belong in an event this size, and not have been weighted out.
        ///
        /// The cost test is the important one and it is why every price goes through here. Something
        /// costing nothing would be affordable no matter how much had already been spent, so the
        /// loop that buys it would never make progress and never end - and an unset Cost on a config
        /// written before events existed is exactly zero.
        /// </summary>
        private static bool IsEligible(float weight, float cost, float minEventCost, float budget, float remaining)
        {
            return weight > 0f
                && cost > 0f
                && cost <= remaining
                && minEventCost <= budget;
        }

        private static Candidate PickWeighted(List<Candidate> candidates, Random random)
        {
            float total = 0f;
            foreach (Candidate candidate in candidates)
            {
                total += candidate.Weight;
            }

            double roll = random.NextDouble() * total;
            foreach (Candidate candidate in candidates)
            {
                roll -= candidate.Weight;
                if (roll <= 0d)
                {
                    return candidate;
                }
            }

            // Only reachable on floating-point crumbs at the very end of the walk.
            return candidates[candidates.Count - 1];
        }

        /// <summary>
        /// How many men a squad type will really produce - members naming a kit that does not exist
        /// are skipped by the spawn, so counting them here would reserve slots for men who never
        /// arrive.
        /// </summary>
        private static int CountMembers(BanditConfiguration config, BanditSquadType type)
        {
            if (type?.Members == null)
            {
                return 0;
            }

            int count = 0;
            foreach (string member in type.Members)
            {
                if (config.FindKit(member) != null)
                {
                    count++;
                }
            }

            return count;
        }

        private static int CountCrew(BanditConfiguration config, BanditVehicleType type)
        {
            if (type?.Crew == null)
            {
                return 0;
            }

            int count = 0;
            foreach (BanditVehicleSeat seat in type.Crew)
            {
                if (seat != null && config.FindKit(seat.Kit) != null)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>Walks a configured list, skipping nulls and unnamed entries.</summary>
        private static IEnumerable<BanditKit> Enumerate(List<BanditKit> kits)
        {
            if (kits == null) yield break;
            foreach (BanditKit kit in kits)
            {
                if (kit != null && !string.IsNullOrEmpty(kit.Name)) yield return kit;
            }
        }

        private static IEnumerable<BanditSquadType> Enumerate(List<BanditSquadType> squads)
        {
            if (squads == null) yield break;
            foreach (BanditSquadType squad in squads)
            {
                if (squad != null && !string.IsNullOrEmpty(squad.Name)) yield return squad;
            }
        }

        private static IEnumerable<BanditVehicleType> Enumerate(List<BanditVehicleType> vehicles)
        {
            if (vehicles == null) yield break;
            foreach (BanditVehicleType vehicle in vehicles)
            {
                if (vehicle != null && !string.IsNullOrEmpty(vehicle.Name)) yield return vehicle;
            }
        }

        /// <summary>One thing the draw could buy this round, and what it would cost to buy it.</summary>
        private struct Candidate
        {
            public BanditSquadType Squad;
            public BanditVehicleType Vehicle;
            public BanditKit Kit;
            public float Cost;
            public int Size;
            public float Weight;
        }
    }
}
