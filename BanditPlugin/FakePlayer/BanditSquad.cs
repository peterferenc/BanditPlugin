using System.Collections.Generic;
using SDG.Unturned;
using UnityEngine;
using static BanditPlugin.BanditGeometry;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// What a handful of bandits know and agree on together: where the enemy is, and who has
    /// already taken which piece of cover.
    ///
    /// The point of this class is that a bandit's own eyes are no longer the limit of what it
    /// reacts to. Every member's target scan feeds one shared contact here, so the rifleman with a
    /// wall in front of it takes cover the moment the machinegunner spots someone, and the
    /// machinegunner keeps firing at a position the man who can actually see it is reporting. Both
    /// of those are impossible for a bandit that only knows what it personally has line of sight
    /// to, which is what every one of them was before this existed.
    ///
    /// It holds no update loop of its own. Members drive it from their own tick and read it back
    /// on the same tick, and anything that has died or been despawned is dropped the next time the
    /// list is walked - so a squad needs no cleanup and cannot leave a stale reference behind.
    /// </summary>
    public sealed class BanditSquad
    {
        /// <summary>Every squad currently in the field, newest last.</summary>
        public static readonly List<BanditSquad> All = new List<BanditSquad>();

        private static int _nextId = 1;

        /// <summary>Shown by /banditstatus, so two squads on the ground can be told apart.</summary>
        public int Id { get; }

        /// <summary>Which squad type put this one down - "sniper", "rifle" - for /banditstatus.</summary>
        public string TypeName { get; }

        /// <summary>
        /// This squad's own group figures, resolved from its type at spawn rather than read out of
        /// the configuration on every tick. That is what lets a sniper pair hold a contact for
        /// twenty-five seconds while a rifle section a hundred metres away forgets one in twelve,
        /// under the same configuration - and it means editing a type does nothing to a squad
        /// already in the field. See <see cref="BanditSquadProfile"/>.
        /// </summary>
        public float ContactMemorySeconds { get; }
        public float CoverSeparation { get; }
        public float RepositionAfterNoShotSeconds { get; }

        private readonly List<BanditBotController> _members = new List<BanditBotController>();

        private readonly Dictionary<BanditBotController, Vector3> _coverClaims =
            new Dictionary<BanditBotController, Vector3>();

        // Reused rather than allocated per query: this is read inside a cover search, which already
        // runs on a timer for a reason. Only ever handed to one caller at a time, on one thread.
        private readonly List<Vector3> _claimScratch = new List<Vector3>();

        /// <summary>Whoever a member last had eyes on, or null once nobody has seen anyone.</summary>
        public Player ContactTarget { get; private set; }

        /// <summary>
        /// Where that was, and where their eye was, when it was last reported. The eye is what
        /// cover searches want - it is the viewpoint they test visibility against - so it is
        /// reported as the target's real aim height rather than the chest anyone aims at.
        /// </summary>
        public Vector3 ContactPosition { get; private set; }
        public Vector3 ContactEye { get; private set; }

        /// <summary>
        /// Where to put rounds to suppress that contact: chest height at the last reported
        /// position. Derived from the two above by the same fraction the controller uses to pick a
        /// point on a body, so suppressing fire lands where aimed fire would have.
        /// </summary>
        public Vector3 ContactAimPoint =>
            Vector3.Lerp(ContactPosition, ContactEye, ChestHeightFraction);

        public float LastContactTime { get; private set; } = float.MinValue;

        /// <summary>Which member reported it, purely so /banditstatus can say who is spotting.</summary>
        public string ContactSpotter { get; private set; } = string.Empty;

        private BanditSquad(BanditSquadProfile profile)
        {
            Id = _nextId++;
            TypeName = profile.TypeName;
            ContactMemorySeconds = profile.ContactMemorySeconds;
            CoverSeparation = profile.CoverSeparation;
            RepositionAfterNoShotSeconds = profile.RepositionAfterNoShotSeconds;
        }

        /// <summary>
        /// Opens a squad running the figures in <paramref name="profile"/>. Null falls back to the
        /// global squad settings, which is what a squad spawned without a type gets.
        /// </summary>
        public static BanditSquad Create(BanditSquadProfile profile)
        {
            BanditSquad squad = new BanditSquad(
                profile ?? BanditSquadProfile.FromConfiguration(BanditPlugin.Instance.Configuration.Instance));
            All.Add(squad);
            return squad;
        }

        /// <summary>
        /// Forgets every squad. Called by /banditclear, which kicks the bots themselves - without
        /// this the squad objects would linger holding references to players that no longer exist.
        /// </summary>
        public static void ClearAll()
        {
            All.Clear();
        }

        public void Add(BanditBotController member)
        {
            if (member != null && !_members.Contains(member))
            {
                _members.Add(member);
                member.Squad = this;
            }
        }

        /// <summary>Live members, with anything dead or despawned dropped on the way past.</summary>
        public List<BanditBotController> Members
        {
            get
            {
                Prune();
                return _members;
            }
        }

        /// <summary>
        /// Whether the squad has seen anyone recently enough to still be acting on it.
        ///
        /// The window is what turns a sighting into a firefight rather than a flicker: cover is
        /// held, the machinegunner keeps suppressing, and nobody stands up the instant the target
        /// steps behind a tree. It is deliberately longer than one bandit's own target memory,
        /// because a squad that loses sight of someone has not stopped being in contact - and it is
        /// per squad, because how long a type keeps watching a spot is part of what that type is.
        /// </summary>
        public bool HasFreshContact
        {
            get
            {
                return ContactTarget != null
                    && Time.time - LastContactTime <= ContactMemorySeconds;
            }
        }

        /// <summary>
        /// True while at least one member can actually see the contact right now, as opposed to
        /// remembering it. This is what tells a suppressing machinegunner it is still worth firing
        /// at a spot nobody is currently looking at: someone else is.
        /// </summary>
        public bool AnyoneSeesContact
        {
            get
            {
                if (!HasFreshContact)
                {
                    return false;
                }

                foreach (BanditBotController member in Members)
                {
                    if (member.CurrentTarget != null && member.CurrentTarget == ContactTarget)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        /// <summary>
        /// Called by any member that currently has eyes on someone. The most recent report wins
        /// outright rather than being averaged or scored: the freshest sighting is the best one,
        /// and a squad arguing about which of two reports to believe would be a lot of machinery
        /// for a distinction nobody could see in play.
        /// </summary>
        public void ReportContact(BanditBotController spotter, Player target, Vector3 targetEye)
        {
            if (target == null || target.life == null || target.life.isDead)
            {
                return;
            }

            ContactTarget = target;
            ContactPosition = target.transform.position;
            ContactEye = targetEye;
            LastContactTime = Time.time;
            ContactSpotter = spotter != null ? spotter.KitName : string.Empty;
        }

        /// <summary>
        /// Records that a member is taking a particular piece of cover, so the rest of the squad
        /// stops considering it. Claims are per member and overwrite, because a bandit only ever
        /// occupies one spot at a time and moving to a new one abandons the old.
        /// </summary>
        public void ClaimCover(BanditBotController member, Vector3 spot)
        {
            if (member != null)
            {
                _coverClaims[member] = spot;
            }
        }

        public void ReleaseCover(BanditBotController member)
        {
            if (member != null)
            {
                _coverClaims.Remove(member);
            }
        }

        /// <summary>
        /// Where everyone else is already going, for a cover search to steer around.
        ///
        /// Without this the squad piles into one spot, and not by coincidence: the cover finder is
        /// deterministic and scores candidates purely on the searcher's position and the threat, so
        /// five bandits standing near each other and facing the same way all compute the same best
        /// spot and walk to precisely the same coordinate.
        ///
        /// A member's own claim is excluded, or it would reject the cover it is already standing in
        /// and shuffle out of it on the next search.
        /// </summary>
        public List<Vector3> OtherCoverClaims(BanditBotController member)
        {
            _claimScratch.Clear();
            Prune();

            foreach (KeyValuePair<BanditBotController, Vector3> claim in _coverClaims)
            {
                if (claim.Key != member)
                {
                    _claimScratch.Add(claim.Value);
                }
            }

            return _claimScratch;
        }

        /// <summary>
        /// Drops members that have died or been despawned, and retires the squad once none are
        /// left. Cheap enough to run on every access - a squad is five entries - and doing it here
        /// rather than on a timer means there is no window where a member is gone but still counted.
        /// </summary>
        private void Prune()
        {
            for (int i = _members.Count - 1; i >= 0; i--)
            {
                BanditBotController member = _members[i];
                if (member != null && member.Self != null && member.Self.life != null && !member.Self.life.isDead)
                {
                    continue;
                }

                _members.RemoveAt(i);
                if (member != null)
                {
                    _coverClaims.Remove(member);
                }
            }

            if (_members.Count == 0)
            {
                All.Remove(this);
            }
        }
    }
}
