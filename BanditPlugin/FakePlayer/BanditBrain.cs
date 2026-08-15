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
            TakeCover
        }

        public BanditState State { get; private set; } = BanditState.Idle;

        /// <summary>World-space unit vector on the XZ plane, or zero to stand still.</summary>
        public Vector3 MoveDirection { get; private set; }

        public bool WantsSprint { get; private set; }
        public bool WantsCrouch { get; private set; }
        public bool WantsJump { get; private set; }

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

        private readonly BanditBotController _controller;
        private readonly Player _self;
        private readonly BanditConfiguration _config;

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

        private float _nextAdvanceRepathTime;
        private float _nextScanTime;
        private float _scanYaw;

        public BanditBrain(BanditBotController controller, Player self)
        {
            _controller = controller;
            _self = self;
            _config = BanditPlugin.Instance.Configuration.Instance;

            Navigator = new BanditNavigator(self)
            {
                ArriveRadius = _config.ArriveRadius,
                RepathIntervalSeconds = _config.RepathIntervalSeconds,
                NavmeshSnapDistance = _config.NavmeshSnapDistance,
                AllowJumping = _config.AllowJumping
            };

            CoverEnabled = _config.CoverByDefault;
            PeekEnabled = _config.PeekByDefault;

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

            _hasCover = false;
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

        public void Tick(float deltaTime, Player target)
        {
            MoveDirection = Vector3.zero;
            WantsSprint = false;
            WantsCrouch = false;
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

            if (!_config.MovementEnabled)
            {
                State = BanditState.Idle;
                return;
            }

            if (target != null && !target.life.isDead)
            {
                _lastKnownTargetPosition = target.transform.position;
                _lastSawTargetTime = Time.time;
                MaybeTakeCover(target);
            }
            else if (_hasCover && Time.time - _lastSawTargetTime > TargetMemorySeconds)
            {
                // Cover is held for a few seconds after the target goes invisible, because
                // crouching behind cover is what *causes* it to go invisible - the controller only
                // acquires players it has line of sight to, and ducking breaks that line by
                // design. Releasing immediately would make the bot stand up and walk off
                // mid-firefight.
                _hasCover = false;
            }

            TickMovement(deltaTime, target);

            WantsJump |= Navigator.WantsJump;
        }

        /// <summary>
        /// Combat's only say in where the feet go. Everything else the bot does while fighting -
        /// aiming, firing, tracking - happens in the controller and leaves movement alone, so a
        /// patrol or a /banditgoto keeps running through a firefight rather than being suspended
        /// by it.
        /// </summary>
        private void MaybeTakeCover(Player target)
        {
            if (!CoverEnabled)
            {
                return;
            }

            ReleaseCoverIfStale();

            if (_hasCover || Time.time < _nextCoverSearchTime || !IsExposedTo(target))
            {
                return;
            }
            _nextCoverSearchTime = Time.time + _config.CoverSearchIntervalSeconds;

            TryTakeCoverFrom(EyeOf(target), out _);
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
            bool found = BanditCoverFinder.TryFindCover(
                _self.transform.position,
                threatEye,
                _config.CoverSearchRadius,
                _config.CoverRingSamples,
                _config.CoverMinimumThreatDistance,
                _config.PreferredEngagementRange,
                PrefersToHide,
                out BanditCoverSpot spot,
                out stats,
                reports);

            if (!found)
            {
                return false;
            }

            _coverSpot = spot;
            _hasCover = true;
            _coverBreached = false;
            _peeking = false;
            _coverPhaseUntil = 0f;
            Navigator.SetDestination(spot.Position);
            return true;
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

            if (_config.AdvanceOnTarget && target != null && !target.life.isDead)
            {
                State = BanditState.Engage;
                TickAdvance(deltaTime, target);
                return;
            }

            State = BanditState.Idle;
            Navigator.Stop();
        }

        /// <summary>
        /// Opt-in (AdvanceOnTarget, off by default): walk at a target that is further away than
        /// the preferred engagement range. Off by default because a bandit that closes on whoever
        /// it sees is a chase behaviour, not something a patrolling bandit should do unasked.
        /// </summary>
        private void TickAdvance(float deltaTime, Player target)
        {
            if (FlatDistance(_self.transform.position, target.transform.position) <= _config.PreferredEngagementRange)
            {
                Navigator.Stop();
                return;
            }

            // Re-issuing a destination resets the navigator's path and stuck tracking, so it only
            // happens when the target has actually moved somewhere else.
            if (!Navigator.HasDestination
                || (Time.time >= _nextAdvanceRepathTime
                    && FlatDistance(Navigator.Destination, target.transform.position) > 3f))
            {
                _nextAdvanceRepathTime = Time.time + 1f;
                Navigator.SetDestination(target.transform.position);
            }

            Navigator.Tick(deltaTime);
            MoveDirection = Navigator.DesiredDirection;
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
                _hasCover = false;
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
                    _config.CoverMinimumThreatDistance, _config.PreferredEngagementRange,
                    out BanditCoverSpot deeper))
            {
                _coverSpot = deeper;

                // Back down and stay down for a moment: popping up on schedule right after being
                // hit is how a bot walks into the second shot.
                _peeking = false;
                _coverPhaseUntil = Time.time + _config.CoverHideSeconds;
                Navigator.SetDestination(deeper.Position, 0.35f);
                return;
            }

            _hasCover = false;
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
        private void ReleaseCoverIfStale()
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

            if (!BanditCoverFinder.IsCoveredFrom(checkPosition, ThreatEye(), crouching))
            {
                _hasCover = false;
            }
        }

        /// <summary>
        /// Where the threat's eye is. Falls back to the last place the target was seen, because
        /// while the bot is crouched in cover it has no visible target by definition - see the
        /// cover-memory branch in Tick.
        /// </summary>
        private Vector3 ThreatEye()
        {
            Player target = _controller.CurrentTarget;
            return target != null && !target.life.isDead
                ? EyeOf(target)
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
