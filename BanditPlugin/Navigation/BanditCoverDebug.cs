using System;
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
    /// Markers are vanilla paintball impact effects, which happen to be the only stock effects that
    /// come in colours. Colour tells you which test rejected a spot, which is the whole point: a
    /// field of red says candidates were being thrown away, a field of nothing says none were
    /// generated.
    ///
    /// The markers are expensive, and knowing why is what keeps this from melting the client again.
    /// Each Paintball_*_Dynamic asset declares "Splatters 8" and no Splatter_Lifetime, which parses
    /// to zero - and EffectManager only schedules a cleanup "if (asset.splatterLifetime >
    /// float.Epsilon)". So one triggerEffect leaves EIGHT decal GameObjects that never expire on
    /// their own. An earlier version re-sent every marker on a 0.75s timer for twelve seconds,
    /// which meant a forty-candidate search left roughly five thousand permanent objects on every
    /// client in range. Hence the rules here:
    ///   - fire each marker ONCE; the decals persist by themselves, so pulsing bought nothing
    ///   - cap how many are drawn, because search radius drives candidate count
    ///   - always clear the previous search before drawing, and clear again on a timer
    /// EffectManager.ClearEffectByGuid_AllPlayers is what actually removes them: its ClearEffect
    /// walks asset.splatters and calls pool.DestroyAllMatchingPrefab on each.
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

        /// <summary>Every marker asset, so a clear can sweep all of them in one go.</summary>
        private static readonly string[] AllMarkerGuids =
        {
            EffectGreen, EffectBlue, EffectRed, EffectYellow, EffectOrange, EffectPurple
        };

        /// <summary>How far away clients are told about the markers.</summary>
        private const float MarkerRelevantDistance = 256f;

        /// <summary>Decals per marker, straight out of the asset's "Splatters" line. Only used to
        /// report the real object count, which is the number that actually matters for framerate.</summary>
        private const int SplattersPerMarker = 8;

        private static Coroutine _clearRoutine;

        /// <summary>
        /// Draws one search. Returns how many markers were actually placed, which can be fewer than
        /// the number of candidates - the caller reports that, so a capped draw never silently
        /// looks like a smaller search than really ran.
        /// </summary>
        public static int Show(List<BanditCoverCandidateReport> reports, Vector3? chosen, float seconds, int maxMarkers)
        {
            // Wipe the last search first. Without this every invocation stacks another few hundred
            // permanent decals on top of the ones already there.
            Clear();

            if (BanditPlugin.Instance == null || reports == null || reports.Count == 0 || maxMarkers <= 0)
            {
                return 0;
            }

            List<BanditCoverCandidateReport> markers = SelectMarkers(reports, maxMarkers);

            foreach (BanditCoverCandidateReport report in markers)
            {
                bool isChosen = chosen.HasValue && (report.Position - chosen.Value).sqrMagnitude < 0.05f;
                Spawn(isChosen ? EffectGreen : EffectFor(report.Outcome), report.Position);
            }

            if (seconds > 0f)
            {
                _clearRoutine = BanditPlugin.Instance.StartCoroutine(ClearAfter(seconds));
            }

            return markers.Count;
        }

        /// <summary>
        /// Removes every marker from every client. Safe to call when nothing is drawn, and used
        /// both by the auto-clear timer and by "/banditcover clear".
        /// </summary>
        public static void Clear()
        {
            if (_clearRoutine != null && BanditPlugin.Instance != null)
            {
                BanditPlugin.Instance.StopCoroutine(_clearRoutine);
            }
            _clearRoutine = null;

            foreach (string guidText in AllMarkerGuids)
            {
                if (Guid.TryParse(guidText, out Guid guid))
                {
                    EffectManager.ClearEffectByGuid_AllPlayers(guid);
                }
            }
        }

        private static IEnumerator ClearAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            _clearRoutine = null;
            Clear();
        }

        /// <summary>
        /// Trims a search down to the marker budget.
        ///
        /// Viable spots are never dropped - they are the answer to "where could it have gone". The
        /// remaining budget is spread across the rejects with an even stride rather than taking the
        /// first N, so what you see keeps the shape of the search instead of being whichever corner
        /// the candidate generator happened to emit first.
        /// </summary>
        private static List<BanditCoverCandidateReport> SelectMarkers(
            List<BanditCoverCandidateReport> reports, int maxMarkers)
        {
            if (reports.Count <= maxMarkers)
            {
                return reports;
            }

            List<BanditCoverCandidateReport> picked = new List<BanditCoverCandidateReport>(maxMarkers);
            List<BanditCoverCandidateReport> rejects = new List<BanditCoverCandidateReport>();

            foreach (BanditCoverCandidateReport report in reports)
            {
                bool interesting = report.Outcome == BanditCoverOutcome.Chosen
                    || report.Outcome == BanditCoverOutcome.Viable;

                if (interesting && picked.Count < maxMarkers)
                {
                    picked.Add(report);
                }
                else if (!interesting)
                {
                    rejects.Add(report);
                }
            }

            int budget = maxMarkers - picked.Count;
            if (budget <= 0 || rejects.Count == 0)
            {
                return picked;
            }

            if (rejects.Count <= budget)
            {
                picked.AddRange(rejects);
                return picked;
            }

            float stride = (float)rejects.Count / budget;
            for (int i = 0; i < budget; i++)
            {
                int index = Mathf.Min(Mathf.FloorToInt(i * stride), rejects.Count - 1);
                picked.Add(rejects[index]);
            }

            return picked;
        }

        private static void Spawn(string guidText, Vector3 position)
        {
            if (!Guid.TryParse(guidText, out Guid guid))
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

        /// <summary>Decal count for a given number of markers, for the command to report.</summary>
        public static int SplatterCount(int markers)
        {
            return markers * SplattersPerMarker;
        }

        /// <summary>Legend to print alongside, so the colours mean something without the source.</summary>
        public const string Legend =
            "green = chosen, blue = viable, red = still visible, yellow = no room, " +
            "orange = no ground, purple = too close to you";
    }
}
