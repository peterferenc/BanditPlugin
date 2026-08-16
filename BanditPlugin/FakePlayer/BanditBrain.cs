using System.Collections.Generic;
using BanditPlugin.Navigation;
using SDG.Unturned;
using UnityEngine;

namespace BanditPlugin.FakePlayer
{
    /// <summary>
    /// Decides where the bot wants to be. It never touches packets or aim - it publishes a desired
    /// world-space direction and a few stance flags, and BanditBotController turns those into the
    /// analog/keys bytes of the input packet it was already sending.
    ///
    /// The split matters because movement in Unturned is body-relative and the body yaw *is* the
    /// aim yaw (PlayerLook.simulate assigns transform.localRotation from the packet's yaw, and
    /// PlayerMovement then does "transform.rotation * move.normalized * speed"). So the brain can
    /// only ever ask for a world direction; converting that into forward/strafe has to happen at
    /// the point where the aim for that same packet is already known.
    /// </summary>
    public sealed class BanditBrain
    {
        public enum BanditState
        {
            Idle,
            Travel,
            Investigate,
            Engage,
            TakeCover,

            /// <summary>Getting out from in front of a friendly vehicle. See OrderEvade.</summary>
            Evade
        }

        public BanditState State { get; private set; } = BanditState.Idle;

        /// <summary>World-space unit vector on the XZ plane, or zero to stand still.</summary>
        public Vector3 MoveDirection { get; private set; }

        public bool WantsSprint { get; private set; }
        public bool WantsCrouch { get; private set; }
        public bool WantsJump { get; private set; }

        /// <summary>
        /// Lie down. Never set in the same tick as <see cref="WantsCrouch"/>: PlayerStance.simulate
        /// tests its crouch input first and only looks at the prone one when that is clear, so a
        /// packet carrying both is simply a crouch. See ApplyProneOrder.
        /// </summary>
        public bool WantsProne { get; private set; }

        /// <summary>
        /// Lower the rifle and hold fire this tick. Set while sprinting somewhere that matters more
        /// than the shot: vanilla PlayerStance refuses to sprint while aiming down sights, so
        /// without the controller acting on this the bot would shoulder its gun the instant it saw
        /// someone and quietly walk to cover instead of running.
        /// </summary>
        public bool WantsWeaponDown { get; private set; }

        /// <summary>
        /// Lean out from behind cover, the same Q/E a player uses. Not cosmetic: PlayerLook's
        /// updateAim rolls aim.parent by the lean angle server-side, and because the aim transform
        /// sits about 1.6m up, that roll swings the eye roughly half a metre sideways. aim.position
        /// is what the bot's line-of-sight tests and its bullet origin both use, so leaning really
        /// does buy a firing angle around a trunk the bot's body is still behind.
        /// </summary>
        public bool WantsLeanLeft { get; private set; }
        public bool WantsLeanRight { get; private set; }

        /// <summary>
        /// Yaw the body should turn to when there is no combat target to aim at. Null means
        /// "no opinion" - the controller leaves the current facing alone.
        /// </summary>
        public float? DesiredFacing { get; private set; }

        public bool PatrolEnabled { get; private set; }

        /// <summary>
        /// Whether this bandit looks for cover at all. Off at spawn by default, so a fresh bandit
        /// stands where you put it; /bandit cover start turns it on. Per-bandit rather than a
        /// config-wide switch because it is a standing order you give in the field.
        /// </summary>
        public bool CoverEnabled { get; private set; }

        /// <summary>
        /// Whether the bandit alternates hiding with exposing itself to shoot once it is in cover.
        /// Off means it goes to cover and stays down. Also off at spawn by default; /bandit peek
        /// start turns it on.
        /// </summary>
        public bool PeekEnabled { get; private set; }

        /// <summary>
        /// The stance this bandit has been ordered to hold, from "/bandit stance ...".
        ///
        /// A standing order rather than a one-off action, because a stance only lasts as long as
        /// the key is held: PlayerStance.simulate stands a player back up on the first packet that
        /// carries neither crouch nor prone, so whatever is wanted has to be re-asserted every tick.
        /// </summary>
        public BanditStance StanceOrder { get; private set; } = BanditStance.Free;

        /// <summary>Whether the bandit is lying down on orders. Kept for /banditprone and status.</summary>
        public bool ProneEnabled => StanceOrder == BanditStance.Prone;

        public BanditNavigator Navigator { get; }

        /// <summary>Health below which the bot hides properly instead of looking for a firing angle.</summary>
        private const byte HurtHealthThreshold = 45;

        /// <summary>Damage is "recent" for this long - drives investigating and hiding.</summary>
        private const float DamageMemorySeconds = 6f;

        /// <summary>How long the bot pokes around the last place it saw someone.</summary>
        private const float InvestigateSeconds = 12f;

        /// <summary>How long a lost target is still worth walking toward.</summary>
        private const float TargetMemorySeconds = 8f;

        private const float ScanIntervalSeconds = 2f;

        /// <summary>How long after the feet stop before a prone bandit drops back down.</summary>
        private const float ProneSettleSeconds = 0.75f;

        /// <summary>How long a bandit that has given up on its position spends moving to a new one.</summary>
        private const float RepositionMoveSeconds = 6f;

        /// <summary>
        /// How close a repositioning bandit will walk toward the enemy before it stops regardless.
        /// Repositioning is about finding an angle, not about closing - that is the breacher's job -
        /// so this keeps a marksman from wandering into the fight looking for one.
        /// </summary>
        private const float MinimumRepositionApproach = 12f;

        private readonly BanditBotController _controller;
        private readonly Player _self;
        private readonly BanditConfiguration _config;

        /// <summary>
        /// This bandit's class settings. Everything the kit has a say in is read from here rather
        /// than from <see cref="_config"/>, which is what lets a machinegunner and a marksman run
        /// different engagement ranges and standing orders side by side.
        /// </summary>
        private readonly BanditProfile _profile;

        private readonly List<Vector3> _patrolRoute = new List<Vector3>();
        private int _patrolIndex;
        private float _patrolDwellUntil;

        private Vector3? _commandedDestination;

        private Vector3? _investigatePoint;
        private float _investigateUntil;
        private bool _investigateArrived;

        private Vector3 _lastKnownTargetPosition;
        private float _lastSawTargetTime = float.MinValue;

        private float _lastDamagedTime = float.MinValue;
        private Vector3? _lastThreatPoint;

        /// <summary>Minimum gap between "I'm being shot, pull in further" adjustments.</summary>
        private const float CoverAdjustIntervalSeconds = 1.5f;

        private BanditCoverSpot _coverSpot;
        private bool _hasCover;
        private bool _coverBreached;
        private float _nextCoverAdjustTime;
        private bool _peeking;
        private float _coverPhaseUntil;
        private float _nextCoverSearchTime;
        private float _nextCoverValidationTime;

        private float _proneSettleTime;

        /// <summary>
        /// Prone because the squad is in contact, as opposed to because someone ordered it. Kept
        /// apart from ProneEnabled so that contact ending stands the bandit up without wiping out
        /// a /banditprone a person gave it.
        /// </summary>
        private BanditStance _contactStance;

        private float _nextProneCheckTime;
        private bool _stanceKeepsLineOfSight;

        private float _repositionUntil;
        private float _nextRepositionTime;

        private float _nextAdvanceRepathTime;
        private float _nextScanTime;
        private float _scanYaw;

        public BanditBrain(BanditBotController controller, Player self)
        {
            _controller = controller;
            _self = self;
            _config = BanditPlugin.Instance.Configuration.Instance;

            // The spawner sets Profile immediately after AddComponent and Start() runs a frame
            // later, so it is there by now. The fallback is for a controller added by hand.
            _profile = controller.Profile ?? BanditProfile.FromConfiguration(_config);

            Navigator = new BanditNavigator(self)
            {
                ArriveRadius = _config.ArriveRadius,
                RepathIntervalSeconds = _config.RepathIntervalSeconds,
                NavmeshSnapDistance = _config.NavmeshSnapDistance,
                AllowJumping = _config.AllowJumping
            };

            CoverEnabled = _profile.Cover;
            PeekEnabled = _profile.Peek;

            // A kit that spawns its class lying down is giving it a standing order; anything else
            // leaves the stance free for cover and contact to decide.
            StanceOrder = _profile.Prone ? BanditStance.Prone : BanditStance.Free;

            PatrolEnabled = _config.PatrolByDefault;
            if (PatrolEnabled)
            {
                RefreshPatrolRoute();
            }
        }

        /// <summary>
        /// "/bandit cover start|stop".
        ///
        /// Starting clears the search cooldown so the bot looks on the very next tick instead of
        /// waiting out CoverSearchIntervalSeconds - the command should visibly do something.
        ///
        /// Stopping means "stop where you are and stay there", so it drops the cover spot *and*
        /// every other standing order. Dropping only the cover spot would leave a bandit that had
        /// been walking to cover carrying on to whatever patrol or /banditgoto it had before, which
        /// is the opposite of holding position.
        /// </summary>
        public void SetCoverEnabled(bool enabled)
        {
            CoverEnabled = enabled;

            if (enabled)
            {
                _nextCoverSearchTime = 0f;
                return;
            }

            DropCover();
            _peeking = false;
            _coverBreached = false;
            _coverPhaseUntil = 0f;
            StopMoving();
        }

        /// <summary>
        /// "/bandit peek start|stop". Only has any visible effect while the bandit is in cover -
        /// peeking is a thing you do from behind something.
        /// </summary>
        public void SetPeekEnabled(bool enabled)
        {
            PeekEnabled = enabled;
        }

        /// <summary>
        /// "/banditprone". Purely a stance: it deliberately leaves patrol, cover and any /banditgoto
        /// order alone, so a bandit told to lie down crawls its route rather than stopping. What it
        /// cannot do is beat vanilla's own rules - a bot in shallow water is forced upright by
        /// PlayerStance regardless of what we send.
        /// </summary>
        public void SetProneEnabled(bool enabled)
        {
            SetStanceOrder(enabled ? BanditStance.Prone : BanditStance.Free);
        }

        /// <summary>
        /// "/bandit stance stand|crouch|prone". An explicit order outranks everything the bandit
        /// would choose for itself - a machinegunner told to stand stays standing through contact,
        /// and one told to crouch does not duck lower for cover.
        /// </summary>
        public void SetStanceOrder(BanditStance order)
        {
            StanceOrder = order;
        }

        /// <summary>Sends the bot to a point. Overrides patrol until it arrives or gives up.</summary>
        public void GoTo(Vector3 destination)
        {
            _commandedDestination = destination;
            _investigatePoint = null;
            _patrolDwellUntil = 0f;
            Navigator.SetDestination(destination);
        }

        public void SetPatrol(bool enabled)
        {
            PatrolEnabled = enabled;
            if (enabled)
            {
                RefreshPatrolRoute();
            }
            else if (!_commandedDestination.HasValue)
            {
                Navigator.Stop();
            }
        }

        public void StopMoving()
        {
            _commandedDestination = null;
            _investigatePoint = null;
            PatrolEnabled = false;
            Navigator.Stop();
        }

        /// <summary>
        /// Called from the DamageTool hook. <paramref name="shotDirection"/> is the direction the
        /// damage was travelling in, so the shooter is back along it - which is the only clue a bot
        /// gets when it is shot by someone it never saw.
        /// </summary>
        public void NotifyDamaged(Vector3 shotDirection)
        {
            _lastDamagedTime = Time.time;

            Vector3 flat = shotDirection;
            flat.y = 0f;
            if (flat.sqrMagnitude > 0.0001f)
            {
                _lastThreatPoint = _self.transform.position - flat.normalized * 20f;
            }

            // Being shot is reason enough to stop looking for a *better* spot and take any spot.
            _nextCoverSearchTime = Mathf.Min(_nextCoverSearchTime, Time.time + 0.25f);

            // Taking rounds while supposedly hidden means this spot isn't as good as the body test
            // believed - pull further in. Hits taken mid-peek don't count: exposing yourself to
            // shoot is the whole point of a peek, and panicking every time one lands would stop the
            // bot ever firing.
            // Navigator.HasDestination means it is still running to the spot - getting shot on the
            // way says nothing about whether the spot is any good.
            if (_hasCover && !_peeking && !Navigator.HasDestination)
            {
                _coverBreached = true;
            }
        }

        /// <summary>
        /// Get out of the way, now, in this world direction.
        ///
        /// Ordered by a squadmate driving a vehicle whose path runs over this bandit. It outranks
        /// everything - cover, patrol, the fight it is in, a /banditgoto, even MovementEnabled being
        /// off - because everything else it could be doing is survivable and being run over by its
        /// own side is not. A bandit lying prone in cover stands up and sprints out of the lane, and
        /// goes back to what it was doing a second later.
        ///
        /// Re-ordered every packet while the bandit is still in the lane, so the hold is short: the
        /// order expires on its own the moment the vehicle stops threatening it.
        /// </summary>
        public void OrderEvade(Vector3 worldDirection, float seconds)
        {
            worldDirection.y = 0f;
            if (worldDirection.sqrMagnitude < 0.0001f)
            {
                return;
            }

            _evadeDirection = worldDirection.normalized;
            _evadeUntil = Time.time + seconds;
        }

        public bool IsEvading => Time.time < _evadeUntil;

        private Vector3 _evadeDirection;
        private float _evadeUntil;

        public void Tick(float deltaTime, Player target)
        {
            MoveDirection = Vector3.zero;
            WantsSprint = false;
            WantsCrouch = false;
            WantsProne = false;
            WantsJump = false;
            WantsWeaponDown = false;
            WantsLeanLeft = false;
            WantsLeanRight = false;
            DesiredFacing = null;

            if (_self == null || _self.life == null || _self.life.isDead)
            {
                Navigator.Stop();
                State = BanditState.Idle;
                return;
            }

            // Before MovementEnabled and before every state below, deliberately. The stance flags
            // were just cleared, so a prone bandit stands up on this same tick simply by not being
            // told to stay down, and the weapon comes down so vanilla will let it sprint -
            // PlayerStance refuses to sprint while aiming.
            if (IsEvading)
            {
                State = BanditState.Evade;
                MoveDirection = _evadeDirection;
                WantsSprint = true;
                WantsWeaponDown = true;
                return;
            }

            if (!_config.MovementEnabled)
            {
                // Still honour the stance. MovementEnabled off means "don't walk anywhere", and a
                // bandit lying still is exactly as stationary as one standing still - so refusing
                // /banditprone here would read as the command being broken.
                State = BanditState.Idle;
                ApplyProneOrder();
                return;
            }

            if (target != null && !target.life.isDead)
            {
                _lastKnownTargetPosition = target.transform.position;
                _lastSawTargetTime = Time.time;
            }

            // Where the threat is, from whatever source knows - this bandit's own eyes, its squad's
            // shared contact, or the direction the last bullet came from. Working it out once here
            // is what lets a bandit who can see nothing at all still take cover from something.
            bool hasThreat = TryResolveThreatEye(target, out Vector3 threatEye);

            // Before the cover check, so a bandit that has just given up its spot searches for a
            // new one on this same tick rather than standing in the open for a few seconds first.
            MaybeReposition(hasThreat);

            if (hasThreat)
            {
                MaybeTakeCover(threatEye, target);
            }
            else if (_hasCover && Time.time - _lastSawTargetTime > TargetMemorySeconds)
            {
                // Cover is held for a few seconds after the target goes invisible, because
                // crouching behind cover is what *causes* it to go invisible - the controller only
                // acquires players it has line of sight to, and ducking breaks that line by
                // design. Releasing immediately would make the bot stand up and walk off
                // mid-firefight.
                DropCover();
            }

            // The machinegunner's posture, and the reason its kit turns cover off: it answers
            // contact by lying down where it is rather than going to look for a rock. Held as a
            // separate flag from the /banditprone standing order so that dropping out of contact
            // stands it back up without countermanding an order a person gave it.
            //
            // Only when it can still see out from down there. Prone drops the aim transform to
            // 0.35m, and that is where its line-of-sight test and its bullets both start, so on
            // open ground a gunner that goes flat is looking through every rise between it and the
            // target and holds its fire - which reads as a machinegun that does not work. Tested
            // once a second rather than every tick because it costs a pair of raycasts.
            _contactStance = ResolveContactStance(hasThreat, threatEye);

            TickMovement(deltaTime, target);

            WantsJump |= Navigator.WantsJump;

            // Last, so it overrides whatever stance the movement code asked for.
            ApplyProneOrder();
        }

        /// <summary>
        /// Folds the standing prone order into this tick's stance flags.
        ///
        /// Crouch has to be cleared rather than left set alongside: PlayerStance.simulate tests its
        /// crouch input first and only falls through to the prone one when that is clear, so a
        /// packet carrying both is just a crouch - the bandit would stay up on one knee and the
        /// order would look ignored. That case is real, not theoretical: a bot in crouch cover asks
        /// for crouch every tick it is hidden.
        ///
        /// Sprint goes with it. Vanilla would refuse it anyway - it only ever promotes to SPRINT
        /// from STAND - but the sprint request drags WantsWeaponDown along in ApplySprintToCover,
        /// and that flag is ours: left set, a crawling bandit would hold its fire waiting on a
        /// sprint that can never start.
        /// </summary>
        private void ApplyProneOrder()
        {
            // An explicit order beats the stance the bandit would choose for itself, in both
            // directions: "stand" holds a machinegunner on its feet through contact, and "crouch"
            // stops it dropping any lower. Only with no order at all does contact decide.
            BanditStance order = StanceOrder;
            if (order == BanditStance.Free)
            {
                order = _contactStance;
            }

            if (order == BanditStance.Free)
            {
                return;
            }

            if (order == BanditStance.Stand)
            {
                WantsCrouch = false;
                WantsProne = false;
                return;
            }

            if (order == BanditStance.Crouch)
            {
                WantsCrouch = true;
                WantsProne = false;

                // Same reasoning as prone below: vanilla only promotes to SPRINT from STAND, so the
                // sprint would be refused while the WantsWeaponDown it drags along would not - and
                // a crouching bandit would hold its fire waiting on a run that cannot start.
                WantsSprint = false;
                WantsWeaponDown = false;
                return;
            }

            // Get up to go anywhere. Vanilla crawls at PlayerMovement.SPEED_PRONE, 1.5 m/s against
            // 7 sprinting, so a bandit that lies down and then walks to cover spends the whole
            // firefight in the open getting there - which is the opposite of what the stance is
            // for. Prone is a firing position, so it is something the bot settles into once it has
            // stopped, and the standing order survives the trip rather than being cancelled by it.
            if (MoveDirection.sqrMagnitude > 0.0001f)
            {
                _proneSettleTime = Time.time + ProneSettleSeconds;
                return;
            }

            // Not the instant the feet stop, either. The cover shuffle steps under a metre between
            // its hidden and peek positions, and dropping flat between each of those would have the
            // bot flickering up and down instead of moving.
            if (Time.time < _proneSettleTime)
            {
                return;
            }

            WantsProne = true;
            WantsCrouch = false;
            WantsSprint = false;
            WantsWeaponDown = false;
        }

        /// <summary>
        /// Combat's only say in where the feet go. Everything else the bot does while fighting -
        /// aiming, firing, tracking - happens in the controller and leaves movement alone, so a
        /// patrol or a /banditgoto keeps running through a firefight rather than being suspended
        /// by it.
        /// </summary>
        private void MaybeTakeCover(Vector3 threatEye, Player visibleTarget)
        {
            if (!CoverEnabled)
            {
                return;
            }

            ReleaseCoverIfStale(threatEye);

            if (_hasCover || Time.time < _nextCoverSearchTime)
            {
                return;
            }

            // A squad in contact is reason enough on its own. Exposure is a question about this
            // bandit - can the target see me, am I being hit - and answering it with "no" is
            // exactly the state a rifleman is in when the machinegunner has just been shot at from
            // a direction the rifleman has a wall in front of. Waiting to be shot at personally
            // before moving is how a squad gets taken apart one at a time.
            if (!IsExposedTo(visibleTarget) && !SquadInContact)
            {
                return;
            }

            _nextCoverSearchTime = Time.time + _config.CoverSearchIntervalSeconds;

            TryTakeCoverFrom(threatEye, out _);
        }

        /// <summary>
        /// Where the threat is, in priority order: someone this bandit can see, then whatever its
        /// squad last reported, then back along the last bullet that hit it.
        ///
        /// The squad entry is the one that changes behaviour. Everything below it was already
        /// reachable by a lone bandit; the shared contact is what lets one that has seen nothing at
        /// all take cover from a threat its squadmates are looking at.
        /// </summary>
        private bool TryResolveThreatEye(Player target, out Vector3 threatEye)
        {
            if (target != null && target.life != null && !target.life.isDead)
            {
                threatEye = EyeOf(target);
                return true;
            }

            BanditSquad squad = _controller.Squad;
            if (squad != null && squad.HasFreshContact)
            {
                threatEye = squad.ContactEye;
                return true;
            }

            if (Time.time - _lastSawTargetTime < TargetMemorySeconds)
            {
                threatEye = _lastKnownTargetPosition + Vector3.up * 1.65f;
                return true;
            }

            if (_lastThreatPoint.HasValue && Time.time - _lastDamagedTime < DamageMemorySeconds)
            {
                threatEye = _lastThreatPoint.Value + Vector3.up * 1.65f;
                return true;
            }

            threatEye = Vector3.zero;
            return false;
        }

        /// <summary>
        /// Whether lying down would still leave a shot on the threat, cached for a second at a
        /// time.
        ///
        /// The answer is sticky on purpose. Re-deciding every tick against a target that is moving
        /// would have the gunner bobbing up and down as the line clears and closes, and the whole
        /// value of the stance is that it stays put. A second is short enough to get back up when
        /// the target genuinely walks out of the lane, and long enough that it stays down through
        /// one walking past a tree.
        /// </summary>
        private bool CanFireFromStance(float eyeHeight, Vector3 threatEye)
        {
            if (Time.time >= _nextProneCheckTime)
            {
                _nextProneCheckTime = Time.time + 1f;
                _stanceKeepsLineOfSight = _controller.WouldKeepLineOfSightFromHeight(eyeHeight, threatEye);
            }

            return _stanceKeepsLineOfSight;
        }

        /// <summary>
        /// What this class wants to do with its body now that there is something to shoot at, and
        /// whether it can afford to.
        ///
        /// Getting low is only worth it if the bandit can still see out from down there. Both
        /// heights here are PlayerLook's own - 1.2m crouched, 0.35m prone - and both are where the
        /// line-of-sight test and the bullet start, so a stance that breaks the shot is refused and
        /// the bandit stays on its feet and fights instead.
        /// </summary>
        private BanditStance ResolveContactStance(bool hasThreat, Vector3 threatEye)
        {
            if (!hasThreat)
            {
                return BanditStance.Free;
            }

            switch (_profile.ContactStance)
            {
                case BanditStance.Prone:
                    return CanFireFromStance(BanditBotController.ProneEyeHeight, threatEye)
                        ? BanditStance.Prone
                        : BanditStance.Free;

                case BanditStance.Crouch:
                    return CanFireFromStance(BanditBotController.CrouchEyeHeight, threatEye)
                        ? BanditStance.Crouch
                        : BanditStance.Free;

                case BanditStance.Stand:
                    return BanditStance.Stand;

                default:
                    return BanditStance.Free;
            }
        }

        /// <summary>
        /// Where the threat is standing, as opposed to where its eye is. Same order of preference
        /// as <see cref="TryResolveThreatEye"/>; this is the one movement wants, since feet are
        /// what a destination is made of.
        /// </summary>
        private bool TryResolveThreatPosition(out Vector3 position)
        {
            Player target = _controller.CurrentTarget;
            if (target != null && target.life != null && !target.life.isDead)
            {
                position = target.transform.position;
                return true;
            }

            BanditSquad squad = _controller.Squad;
            if (squad != null && squad.HasFreshContact)
            {
                position = squad.ContactPosition;
                return true;
            }

            if (Time.time - _lastSawTargetTime < TargetMemorySeconds)
            {
                position = _lastKnownTargetPosition;
                return true;
            }

            position = Vector3.zero;
            return false;
        }

        private bool SquadInContact
        {
            get
            {
                BanditSquad squad = _controller.Squad;
                return squad != null && squad.HasFreshContact;
            }
        }

        /// <summary>
        /// Runs a cover search and, if it finds somewhere, commits the bot to walking there.
        /// Public so /banditcover can trigger one on demand and report what the search saw.
        /// </summary>
        public bool TryTakeCoverFrom(Vector3 threatEye, out BanditCoverSearchStats stats)
        {
            return TryTakeCoverFrom(threatEye, out stats, null);
        }

        /// <inheritdoc cref="TryTakeCoverFrom(Vector3,out BanditCoverSearchStats)"/>
        /// <param name="reports">Per-candidate verdicts for /banditcover to draw; null for live bots.</param>
        public bool TryTakeCoverFrom(Vector3 threatEye, out BanditCoverSearchStats stats,
            List<BanditCoverCandidateReport> reports)
        {
            // What the rest of the squad has already committed to, so this search steers around it
            // rather than converging on the same rock. Null for a lone bandit, which restores the
            // plain search exactly.
            BanditSquad squad = _controller.Squad;
            System.Collections.Generic.IList<Vector3> claimed = squad?.OtherCoverClaims(_controller);

            bool found = BanditCoverFinder.TryFindCover(
                _self.transform.position,
                threatEye,
                _config.CoverSearchRadius,
                _config.CoverRingSamples,
                _config.CoverMinimumThreatDistance,
                _profile.PreferredEngagementRange,
                PrefersToHide,
                out BanditCoverSpot spot,
                out stats,
                reports,
                claimed,
                _config.SquadCoverSeparation);

            if (!found)
            {
                return false;
            }

            _coverSpot = spot;
            _hasCover = true;
            _coverBreached = false;
            _peeking = false;
            _coverPhaseUntil = 0f;

            // Claimed the moment it commits, not on arrival: the walk there is exactly the window
            // in which a squadmate searching would otherwise pick the same spot.
            squad?.ClaimCover(_controller, spot.Position);

            Navigator.SetDestination(spot.Position);
            return true;
        }

        /// <summary>
        /// Gives up the current cover, and tells the squad the spot is free again. Every place that
        /// drops cover goes through here - a claim left behind would keep a squadmate away from a
        /// piece of cover nobody is using.
        /// </summary>
        private void DropCover()
        {
            _hasCover = false;
            _controller.Squad?.ReleaseCover(_controller);
        }

        /// <summary>The most recent cover spot the bot committed to, for /banditcover to report.</summary>
        public BanditCoverSpot CurrentCover => _coverSpot;
        public bool HasCover => _hasCover;

        /// <summary>
        /// Where the feet go, in priority order. Taking cover is the only tactical override;
        /// otherwise a bandit walks its orders and its patrol regardless of what it can see.
        /// </summary>
        private void TickMovement(float deltaTime, Player target)
        {
            // Closing on the enemy outranks sitting in cover, and has to: a breacher whose whole
            // job is to get within shotgun range would otherwise take cover at two hundred metres
            // and hold it, having satisfied a rule that was never meant to outrank its own class.
            // The same branch carries a bandit that has given up on its position and is moving to
            // find an angle - see MaybeReposition.
            if (TryGetAdvanceDestination(out Vector3 advanceTo))
            {
                State = BanditState.Engage;
                TickAdvance(deltaTime, advanceTo);
                return;
            }

            if (_hasCover)
            {
                State = BanditState.TakeCover;
                TickCover(deltaTime);
                return;
            }

            if (_commandedDestination.HasValue)
            {
                State = BanditState.Travel;
                TickCommandedMove(deltaTime);
                ApplyTravelOutputs();
                return;
            }

            if (_config.InvestigateEnabled && TickInvestigate(deltaTime))
            {
                return;
            }

            if (PatrolEnabled)
            {
                State = BanditState.Travel;
                TickPatrol(deltaTime);
                return;
            }

            State = BanditState.Idle;
            Navigator.Stop();
        }

        /// <summary>
        /// Whether this bandit should be walking at the enemy, and where to.
        ///
        /// Two different reasons to. A class with AdvanceOnTarget closes because that is how it
        /// fights - the breacher's shotgun reaches 30m and it is no use to anybody at 200. Any
        /// class repositions because where it is standing has produced no shot for a while, which
        /// is a temporary push to somewhere it can see from rather than a standing intent to charge.
        ///
        /// Both work off the threat's *reported* position rather than a visible target. That is the
        /// fix for a breacher that used to stop dead the moment it lost sight of you: it could only
        /// ever advance on somebody it was personally looking at, and the squad still knows where
        /// you went.
        /// </summary>
        private bool TryGetAdvanceDestination(out Vector3 destination)
        {
            destination = Vector3.zero;

            if (!TryResolveThreatPosition(out Vector3 threat))
            {
                return false;
            }

            float distance = FlatDistance(_self.transform.position, threat);

            // Its class closes to its own fighting range, and no further.
            bool closingIn = _profile.AdvanceOnTarget && distance > _profile.PreferredEngagementRange;

            // Or it has run out of ideas where it is. Stops the moment a shot opens up rather than
            // running the window down - the point of moving was the angle, and it now has one.
            bool huntingAnAngle = Time.time < _repositionUntil
                && Time.time - _controller.LastShotOpportunityTime > 1f
                && distance > MinimumRepositionApproach;

            if (!closingIn && !huntingAnAngle)
            {
                return false;
            }

            destination = threat;
            return true;
        }

        /// <summary>
        /// Notices that this bandit's position has stopped being worth holding and does something
        /// about it: gives up the cover it is in, searches again against where the enemy is *now*,
        /// and failing that walks toward them until it can see something.
        ///
        /// The case this exists for is a squad that took cover facing one way and then had the
        /// enemy move onto a flank. Cover is only surrendered when it stops *hiding* the bandit,
        /// and a rock that hid it from the front hides it just as well from the side - so without
        /// this, everyone except whoever happens to have the new angle stays tucked in behind it,
        /// perfectly safe and perfectly useless, for as long as the fight lasts.
        ///
        /// Squad-only, and hurt bandits are exempt. A lone bandit keeps holding the position it was
        /// given, which is what makes one useful for watching a single behaviour at a time.
        /// </summary>
        private void MaybeReposition(bool hasThreat)
        {
            if (!hasThreat || !SquadInContact || PrefersToHide)
            {
                return;
            }

            float idleSeconds = _config.RepositionAfterNoShotSeconds;
            if (idleSeconds <= 0f || Time.time < _nextRepositionTime)
            {
                return;
            }

            if (Time.time - _controller.LastShotOpportunityTime < idleSeconds)
            {
                return; // it has had a shot recently, so where it is standing is doing its job
            }

            _nextRepositionTime = Time.time + idleSeconds;
            _repositionUntil = Time.time + RepositionMoveSeconds;

            if (_hasCover)
            {
                // The claim goes with the spot, so the search that follows - and every squadmate's
                // - is free to consider it again from the new angle.
                DropCover();
                _nextCoverSearchTime = Time.time;
            }
        }

        /// <summary>
        /// Walks toward a place the enemy is believed to be, stopping at this class's preferred
        /// fighting range. Takes a position rather than a player, because the thing being walked at
        /// is usually a report from a squadmate rather than somebody in view.
        /// </summary>
        private void TickAdvance(float deltaTime, Vector3 threatPosition)
        {
            if (FlatDistance(_self.transform.position, threatPosition) <= _profile.PreferredEngagementRange)
            {
                Navigator.Stop();
                return;
            }

            // Re-issuing a destination resets the navigator's path and stuck tracking, so it only
            // happens when the threat has actually moved somewhere else.
            if (!Navigator.HasDestination
                || (Time.time >= _nextAdvanceRepathTime
                    && FlatDistance(Navigator.Destination, threatPosition) > 3f))
            {
                _nextAdvanceRepathTime = Time.time + 1f;
                Navigator.SetDestination(threatPosition);
            }

            Navigator.Tick(deltaTime);
            MoveDirection = Navigator.DesiredDirection;

            // Run the long stretch. Crossing open ground upright toward someone who is shooting is
            // exactly where the trade favours speed over the shots it gives up.
            ApplySprintToCover();
        }

        /// <summary>
        /// Walks a /banditgoto order to completion, in or out of combat. Arriving or getting
        /// wedged clears the order, which drops the bot back into whatever it was doing before.
        /// </summary>
        private void TickCommandedMove(float deltaTime)
        {
            if (Navigator.ConsumeGaveUp() || Navigator.ConsumeArrived())
            {
                _commandedDestination = null;
                Navigator.Stop();
                return;
            }

            if (!Navigator.HasDestination
                || FlatDistance(Navigator.Destination, _commandedDestination.Value) > 0.5f)
            {
                Navigator.SetDestination(_commandedDestination.Value);
            }

            Navigator.Tick(deltaTime);
            MoveDirection = Navigator.DesiredDirection;
        }

        private void TickCover(float deltaTime)
        {
            if (Navigator.ConsumeGaveUp())
            {
                // Couldn't get there - forget this spot and let the next search pick another.
                DropCover();
                return;
            }

            if (_coverBreached && Time.time >= _nextCoverAdjustTime)
            {
                _nextCoverAdjustTime = Time.time + CoverAdjustIntervalSeconds;
                _coverBreached = false;
                PullDeeperIntoCover();

                if (!_hasCover)
                {
                    return; // nowhere better here; the next search picks a different spot entirely
                }
            }

            // Turning peeking off mid-peek pulls the bot straight back in rather than letting the
            // current peek phase run out, so "/bandit peek stop" reads as immediate instead of
            // leaving it stood in the open for the rest of CoverPeekSeconds.
            if (!PeekEnabled && _peeking)
            {
                _peeking = false;
                _coverPhaseUntil = 0f;
                if (!_coverSpot.RequiresCrouch && _coverSpot.CanPeek)
                {
                    Navigator.SetDestination(_coverSpot.Position, 0.35f);
                }
            }

            // Set before the walking branch returns, so the bot is already leaning out as it steps
            // to the peek position rather than snapping into the lean once it stops.
            ApplyPeekLean();

            if (Navigator.HasDestination)
            {
                Navigator.Tick(deltaTime);
                MoveDirection = Navigator.DesiredDirection;
                ApplySprintToCover();
                return;
            }

            Navigator.ConsumeArrived();

            if (Time.time >= _coverPhaseUntil)
            {
                if (!PeekEnabled)
                {
                    // Nothing to alternate with - just keep renewing the hidden phase. Deliberately
                    // not re-issuing a destination: the bot is already on the spot, and setting one
                    // every few seconds would reset the navigator's path and stuck tracking for no
                    // movement at all.
                    _coverPhaseUntil = Time.time + _config.CoverHideSeconds;
                }
                else
                {
                    // Alternate hiding and showing yourself. Hurt bots stay down: no peeking until
                    // they have been left alone for a moment.
                    _peeking = !_peeking && !PrefersToHide;
                    _coverPhaseUntil = Time.time + (_peeking ? _config.CoverPeekSeconds : _config.CoverHideSeconds);

                    // Hard cover hides the bot standing as well as crouched, so the only way to
                    // shoot from it is to step out to the flank the finder already verified. That
                    // step is under a metre, hence the tight arrive radius - the default would call
                    // it done before the bot moved.
                    if (!_coverSpot.RequiresCrouch && _coverSpot.CanPeek)
                    {
                        Navigator.SetDestination(_peeking ? _coverSpot.PeekPosition : _coverSpot.Position, 0.35f);
                    }
                }
            }

            // Crouch cover is the good case: down is safe, up is a firing position, no walking.
            WantsCrouch = _coverSpot.RequiresCrouch && !_peeking;
        }

        /// <summary>
        /// Runs the long leg of a move to cover, and only the long leg.
        ///
        /// Sprinting is not free: vanilla PlayerStance refuses to sprint while aiming down sights,
        /// so the gun has to come down for the bot to run at all - which means a sprinting bandit
        /// cannot shoot. That is the right trade while crossing open ground where it has no angle
        /// anyway, and the wrong one over the last few metres, where lowering the rifle just throws
        /// away shots it could have taken on the way in.
        ///
        /// So the threshold is on the distance still to travel rather than the length of the trip:
        /// a bandit sent 40m away sprints the first 30 and walks the last 10 with its rifle up,
        /// which is also why this needs no hysteresis - the remaining distance only shrinks.
        /// Measured along the path, not straight-line, because a route around a building is exactly
        /// the case worth running.
        /// </summary>
        private void ApplySprintToCover()
        {
            if (!_config.AllowSprint || MoveDirection.sqrMagnitude < 0.0001f)
            {
                return;
            }

            if (Navigator.RemainingDistance < _config.SprintToCoverMinPathDistance)
            {
                return;
            }

            WantsSprint = true;
            WantsWeaponDown = true;
        }

        /// <summary>
        /// Shuffles further into the current cover after being shot behind it, or abandons the
        /// spot when there is nothing better within a couple of metres - at which point dropping
        /// _hasCover lets the ordinary search go and find somewhere else entirely.
        /// </summary>
        private void PullDeeperIntoCover()
        {
            if (BanditCoverFinder.TryPullDeeper(_coverSpot, ThreatEye(),
                    _config.CoverMinimumThreatDistance, _profile.PreferredEngagementRange,
                    out BanditCoverSpot deeper))
            {
                _coverSpot = deeper;

                // Back down and stay down for a moment: popping up on schedule right after being
                // hit is how a bot walks into the second shot.
                _peeking = false;
                _coverPhaseUntil = Time.time + _config.CoverHideSeconds;

                // The claim moves with it, or the squad would keep avoiding the spot this bandit
                // has just abandoned while walking onto the one it moved to.
                _controller.Squad?.ClaimCover(_controller, deeper.Position);

                Navigator.SetDestination(deeper.Position, 0.35f);
                return;
            }

            DropCover();
            _nextCoverSearchTime = Time.time; // search again immediately rather than in three seconds
        }

        /// <summary>
        /// Leans out towards the side the cover search verified a firing angle on, while peeking.
        ///
        /// The side is worked out against the bot's own right vector rather than stored with the
        /// spot, because the body turns to face the target and "left" has to mean the bot's left
        /// at the moment the key is pressed. Vanilla validates the lean itself
        /// (PlayerAnimator.isLeanSpaceEmpty against BLOCK_LEAN), so a bandit tucked too tightly
        /// against the trunk simply won't lean rather than clipping into it.
        /// </summary>
        private void ApplyPeekLean()
        {
            if (!_peeking || !_coverSpot.CanPeek)
            {
                return;
            }

            Vector3 offset = _coverSpot.PeekPosition - _coverSpot.Position;
            offset.y = 0f;
            if (offset.sqrMagnitude < 0.0001f)
            {
                return;
            }

            float side = Vector3.Dot(offset, _self.transform.right);
            WantsLeanRight = side > 0f;
            WantsLeanLeft = side < 0f;
        }

        /// <summary>
        /// Drops the current cover spot once it stops being cover - the shooter has moved, or the
        /// bot has been pushed off the spot.
        ///
        /// Two things here are easy to get wrong and were:
        ///
        /// The stance has to match what the bot is actually doing. This used to always test the
        /// crouching silhouette, which is what let a bandit in "hard cover" stand in full view
        /// believing it was hidden. Hard cover means the spot hid it *standing* when it was chosen,
        /// and TickCover leaves it standing - WantsCrouch is only set for crouch cover. A counter or
        /// a window sill stops hiding a standing body long before it stops hiding a crouching one,
        /// so as the threat walked around, the crouched test kept passing on a bot whose head and
        /// chest were plainly visible.
        ///
        /// And the position has to be where the bot actually is once it has arrived, not the spot it
        /// was aiming for. While it is still walking the spot is the right thing to judge - the bot
        /// isn't there yet - but afterwards the bot can be shoved off it, and what gets shot is the
        /// body, not the coordinate.
        /// </summary>
        private void ReleaseCoverIfStale(Vector3 threatEye)
        {
            if (!_hasCover || Time.time < _nextCoverValidationTime)
            {
                return;
            }

            // A peek is deliberate exposure - that is the whole point of it - so validating mid-peek
            // would drop cover every single time the bot leaned out.
            if (_peeking)
            {
                return;
            }

            _nextCoverValidationTime = Time.time + 1f;

            bool crouching = _coverSpot.RequiresCrouch;
            Vector3 checkPosition = Navigator.HasDestination
                ? _coverSpot.Position
                : _self.transform.position;

            if (!BanditCoverFinder.IsCoveredFrom(checkPosition, threatEye, crouching))
            {
                DropCover();
            }
        }

        /// <summary>
        /// Where the threat's eye is. Falls back to whatever the squad last reported and then to
        /// the last place this bandit saw someone, because while it is crouched in cover it has no
        /// visible target by definition - see the cover-memory branch in Tick.
        /// </summary>
        private Vector3 ThreatEye()
        {
            return TryResolveThreatEye(_controller.CurrentTarget, out Vector3 threatEye)
                ? threatEye
                : _lastKnownTargetPosition + Vector3.up * 1.65f;
        }

        /// <summary>
        /// Walks the recorded route, loitering at each waypoint. Getting wedged skips to the next
        /// one rather than grinding against whatever is in the way.
        /// </summary>
        private void TickPatrol(float deltaTime)
        {
            if (Navigator.ConsumeGaveUp())
            {
                _patrolDwellUntil = Time.time + 1f;
                AdvancePatrolIndex();
            }
            else if (Navigator.ConsumeArrived())
            {
                _patrolDwellUntil = Time.time + _config.PatrolWaypointDwellSeconds;
                AdvancePatrolIndex();
            }

            if (Time.time < _patrolDwellUntil)
            {
                Navigator.Stop();
                ScanAround();
                return;
            }

            if (_patrolRoute.Count == 0)
            {
                RefreshPatrolRoute();
            }

            if (_patrolIndex < 0 || _patrolIndex >= _patrolRoute.Count)
            {
                State = BanditState.Idle;
                Navigator.Stop();
                return;
            }

            Vector3 waypoint = _patrolRoute[_patrolIndex];
            if (!Navigator.HasDestination || FlatDistance(Navigator.Destination, waypoint) > 0.5f)
            {
                Navigator.SetDestination(waypoint);
            }

            Navigator.Tick(deltaTime);
            MoveDirection = Navigator.DesiredDirection;
            ApplyTravelOutputs();
        }

        /// <summary>
        /// Opt-in (InvestigateEnabled, off by default): after losing contact, go and look where
        /// the target was last seen, or - when shot by someone it never saw - back along the
        /// bullet. Returns true when it is driving movement this tick.
        /// </summary>
        private bool TickInvestigate(float deltaTime)
        {
            if (!_investigatePoint.HasValue)
            {
                if (Time.time - _lastSawTargetTime < TargetMemorySeconds)
                {
                    StartInvestigating(_lastKnownTargetPosition);
                }
                else if (_lastThreatPoint.HasValue && Time.time - _lastDamagedTime < DamageMemorySeconds)
                {
                    StartInvestigating(_lastThreatPoint.Value);
                    _lastThreatPoint = null;
                }
                else
                {
                    return false;
                }
            }

            if (Time.time >= _investigateUntil)
            {
                _investigatePoint = null;
                return false;
            }

            State = BanditState.Investigate;

            if (Navigator.ConsumeGaveUp() || Navigator.ConsumeArrived())
            {
                // Arrived, or couldn't get there. Stand and look around for the rest of the window.
                _investigateArrived = true;
                Navigator.Stop();
            }

            if (_investigateArrived)
            {
                ScanAround();
                return true;
            }

            if (!Navigator.HasDestination
                || FlatDistance(Navigator.Destination, _investigatePoint.Value) > 0.5f)
            {
                Navigator.SetDestination(_investigatePoint.Value);
            }

            Navigator.Tick(deltaTime);
            MoveDirection = Navigator.DesiredDirection;
            ApplyTravelOutputs();
            return true;
        }

        private void StartInvestigating(Vector3 point)
        {
            _investigatePoint = point;
            _investigateUntil = Time.time + InvestigateSeconds;
            _investigateArrived = false;

            // Consume the memory that triggered this, or finishing the investigation would
            // immediately re-trigger it on the same stale sighting.
            _lastSawTargetTime = float.MinValue;
        }

        /// <summary>
        /// Facing and sprint for any kind of travel: walk looking where you're going rather than
        /// moonwalking down the road. Only takes effect when the controller has no target to aim
        /// at - a bandit in contact keeps its gun up and strafes instead.
        /// </summary>
        private void ApplyTravelOutputs()
        {
            if (MoveDirection.sqrMagnitude > 0.0001f)
            {
                DesiredFacing = Mathf.Atan2(MoveDirection.x, MoveDirection.z) * Mathf.Rad2Deg;
                WantsSprint = _config.AllowSprint && State != BanditState.Investigate;
            }
            else
            {
                ScanAround();
            }
        }

        /// <summary>
        /// Sweeps the head about while standing still, so an idle bandit doesn't read as a statue -
        /// and, more practically, so its next move isn't locked to whatever way it happened to stop.
        /// </summary>
        private void ScanAround()
        {
            if (State == BanditState.Idle && !PatrolEnabled)
            {
                return;
            }

            if (Time.time >= _nextScanTime)
            {
                _nextScanTime = Time.time + ScanIntervalSeconds;
                _scanYaw = _self.transform.eulerAngles.y + UnityEngine.Random.Range(-70f, 70f);
            }
            DesiredFacing = _scanYaw;
        }

        private void AdvancePatrolIndex()
        {
            if (_patrolRoute.Count == 0)
            {
                return;
            }

            _patrolIndex++;
            if (_patrolIndex < _patrolRoute.Count)
            {
                return;
            }

            if (_config.PatrolLoop)
            {
                _patrolIndex = 0;
            }
            else
            {
                PatrolEnabled = false;
                _patrolIndex = _patrolRoute.Count - 1;
            }
        }

        private void RefreshPatrolRoute()
        {
            _patrolRoute.Clear();
            _patrolRoute.AddRange(BanditWaypointStore.GetRoute(_config.PatrolUseLocationNodesWhenNoWaypoints));

            // Start with whichever waypoint is closest, so a bandit spawned mid-route doesn't
            // march back to waypoint 0 first.
            _patrolIndex = 0;
            float nearest = float.MaxValue;
            Vector3 position = _self.transform.position;
            for (int i = 0; i < _patrolRoute.Count; i++)
            {
                float distance = FlatDistance(_patrolRoute[i], position);
                if (distance < nearest)
                {
                    nearest = distance;
                    _patrolIndex = i;
                }
            }
        }

        /// <summary>
        /// True when the bot is worth shooting at right now: the target can see it, or it is being
        /// hit by someone it can't see.
        /// </summary>
        private bool IsExposedTo(Player target)
        {
            return _controller.HasLineOfSightTo(target) || Time.time - _lastDamagedTime < DamageMemorySeconds;
        }

        private bool PrefersToHide =>
            _self.life != null
            && _self.life.health < HurtHealthThreshold
            && Time.time - _lastDamagedTime < 3f;

        private static Vector3 EyeOf(Player player)
        {
            return player.look != null && player.look.aim != null
                ? player.look.aim.position
                : player.transform.position + Vector3.up * 1.5f;
        }

        private static float FlatDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return (a - b).magnitude;
        }
    }
}
