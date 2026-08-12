using System.Collections;
using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.Navigation
{
    /// <summary>
    /// Draws a cover search in the world, because a failed search is otherwise completely opaque -
    /// the bot stands still, nothing is logged, and "no cover found" looks the same whether the
    /// search rejected forty candidates or never generated one.
    ///
    /// Markers are vanilla paintball impact effects, which happen to be the only stock effects
    /// that come in colours. They are one-shot particle bursts, so they are re-sent on a timer for
    /// a few seconds - long enough to walk around and look at the pattern.
    ///
    /// Colour tells you which test rejected a spot, which is the whole point: a field of red says
    /// the candidates were being thrown away, a field of nothing says none were generated.
    /// </summary>
    public static class BanditCoverDebug
    {
        // GUIDs from Bundles/Effects/Impacts/Paintball_*_Dynamic on the server install.
        private const string EffectGreen = "066b5cee2aee41eba3d631e4f3710b9b";  // chosen
        private const string EffectBlue = "f52928886e6848d4b810077fa537534a";   // viable, not chosen
        private const string EffectRed = "563658fc7a334dbc8c0b9e322aac96b9";    // still visible - not cover
        private const string EffectYellow = "d9820fabf8174ed5807dc44593800406"; // no standing room
        private const string EffectOrange = "d24723f15bfe4544bc9ee689c0d8d611"; // no ground under it
        private const string EffectPurple = "0bbb4d81380148a88aef453b3c5158bd"; // too close to the threat

        /// <summary>How far away clients are told about the markers.</summary>
        private const float MarkerRelevantDistance = 256f;

        private const float PulseIntervalSeconds = 0.75f;

        public static void Show(List<BanditCoverCandidateReport> reports, Vector3? chosen, float seconds)
        {
            if (BanditPlugin.Instance == null || reports == null || reports.Count == 0)
            {
                return;
            }

            BanditPlugin.Instance.StartCoroutine(Pulse(new List<BanditCoverCandidateReport>(reports), chosen, seconds));
        }

        private static IEnumerator Pulse(List<BanditCoverCandidateReport> reports, Vector3? chosen, float seconds)
        {
            float endTime = Time.time + seconds;

            while (Time.time < endTime)
            {
                foreach (BanditCoverCandidateReport report in reports)
                {
                    bool isChosen = chosen.HasValue && (report.Position - chosen.Value).sqrMagnitude < 0.05f;
                    Spawn(isChosen ? EffectGreen : EffectFor(report.Outcome), report.Position);
                }

                yield return new WaitForSeconds(PulseIntervalSeconds);
            }
        }

        private static void Spawn(string guidText, Vector3 position)
        {
            if (!System.Guid.TryParse(guidText, out System.Guid guid))
            {
                return;
            }

            TriggerEffectParameters parameters = new TriggerEffectParameters(guid)
            {
                // Lifted slightly so the burst isn't buried in the ground mesh.
                position = position + Vector3.up * 0.15f,
                relevantDistance = MarkerRelevantDistance,
                reliable = true
            };
            parameters.SetDirection(Vector3.up);

            if (parameters.asset != null)
            {
                EffectManager.triggerEffect(parameters);
            }
        }

        private static string EffectFor(BanditCoverOutcome outcome)
        {
            switch (outcome)
            {
                case BanditCoverOutcome.Chosen: return EffectGreen;
                case BanditCoverOutcome.Viable: return EffectBlue;
                case BanditCoverOutcome.StillVisible: return EffectRed;
                case BanditCoverOutcome.NoStandingRoom: return EffectYellow;
                case BanditCoverOutcome.NoGround: return EffectOrange;
                case BanditCoverOutcome.TooCloseToThreat: return EffectPurple;
                default: return EffectRed;
            }
        }

        /// <summary>Legend to print alongside, so the colours mean something without the source.</summary>
        public const string Legend =
            "green = chosen, blue = viable, red = still visible, yellow = no room, " +
            "orange = no ground, purple = too close to you";
    }
}
