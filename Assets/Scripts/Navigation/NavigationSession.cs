using System;
using System.Collections.Generic;
using UnityEngine;
using Wayfinding.Data;
using Wayfinding.Positioning;

namespace Wayfinding.Navigation
{
    public enum NavigationState
    {
        /// <summary>No destination chosen. The room-entry screen is up.</summary>
        Idle,

        /// <summary>Destination chosen, but we do not know where the visitor is yet.</summary>
        WaitingForPosition,

        /// <summary>Computing a route.</summary>
        Routing,

        /// <summary>Following a valid path.</summary>
        Guiding,

        /// <summary>Wandered far enough off the path that it needs recomputing.</summary>
        OffRoute,

        /// <summary>Within arrival distance of the destination.</summary>
        Arrived,

        /// <summary>Something went wrong that the visitor needs told about.</summary>
        Failed
    }

    public enum TurnDirection
    {
        Straight,
        Left,
        Right,
        SlightLeft,
        SlightRight,
        TurnAround,
        Arrive
    }

    /// <summary>Everything the UI needs to describe the next few seconds of walking.</summary>
    public readonly struct GuidanceUpdate
    {
        public readonly float RemainingMeters;
        public readonly TurnDirection NextTurn;
        public readonly float MetersToNextTurn;

        /// <summary>Ready-to-display sentence, e.g. "Turn left in 8 m".</summary>
        public readonly string InstructionText;

        /// <summary>0 to 1, how much to trust the position behind this guidance.</summary>
        public readonly float PositionConfidence;

        public GuidanceUpdate(
            float remainingMeters,
            TurnDirection nextTurn,
            float metersToNextTurn,
            string instructionText,
            float positionConfidence)
        {
            RemainingMeters = remainingMeters;
            NextTurn = nextTurn;
            MetersToNextTurn = metersToNextTurn;
            InstructionText = instructionText;
            PositionConfidence = positionConfidence;
        }
    }

    /// <summary>
    /// The state machine that sits between "a visitor typed 412" and "there is a line on the
    /// floor". Owns the destination, decides when to recompute, notices arrival, and turns
    /// geometry into the sentence at the top of the screen.
    ///
    /// The single most important thing this file does is NOT recompute the path constantly.
    /// It is tempting to re-path every time the position updates — eight times a second — and it
    /// looks fine in the editor. On a phone it drains the battery, and worse, every recompute
    /// shifts the racing line slightly, so the line shimmers. Paths are recomputed on a timer, on
    /// going off-route, and on nothing else.
    /// </summary>
    public class NavigationSession : MonoBehaviour
    {
        [Header("Dependencies")]
        public BeaconManager beaconManager;
        public PathfindingEngine pathfindingEngine;
        public FloorMap floorMap;

        [Tooltip("The AR camera. Its facing is what left and right are relative to. Without it, " +
                 "heading falls back to direction of travel, which is only known while moving.")]
        public Transform headingSource;

        [Header("Recompute policy")]
        [Tooltip("Seconds between routine path recomputes while guiding. 2 to 4 feels responsive " +
                 "without shimmering.")]
        public float recomputeInterval = 3f;

        [Tooltip("Metres off the path before it is treated as a wrong turn rather than noise. " +
                 "Keep this comfortably wider than your positioning error or it will trigger " +
                 "constantly while the visitor walks a perfectly good straight line.")]
        public float offRouteThresholdMeters = 4f;

        [Tooltip("Seconds off-route before recomputing. Stops a single bad fix from re-routing " +
                 "someone who never actually left the corridor.")]
        public float offRouteGraceSeconds = 2f;

        [Header("Arrival")]
        [Tooltip("Metres from the destination that counts as arrived. Do not set this tight: " +
                 "beacon positioning is good to a couple of metres at best, and a visitor standing " +
                 "at the right door being told to keep walking is the failure they will remember.")]
        public float arrivalRadiusMeters = 3.5f;

        [Header("Turn detection")]
        [Tooltip("Degrees of bend that counts as a turn worth announcing. Below this it is a " +
                 "gentle curve and saying 'turn left' would be wrong.")]
        [Range(15f, 80f)]
        public float turnAngleThreshold = 35f;

        [Tooltip("Degrees above which a turn is described as a full left or right rather than a " +
                 "slight one.")]
        [Range(40f, 120f)]
        public float sharpTurnAngleThreshold = 65f;

        [Tooltip("Start announcing a turn this many metres before it.")]
        public float turnAnnounceDistanceMeters = 8f;

        public event Action<NavigationState> StateChanged;

        /// <summary>Fires whenever the path is replaced. The list is reused — copy it if you keep it.</summary>
        public event Action<IReadOnlyList<Vector3>> PathChanged;

        /// <summary>Fires on every position update while guiding.</summary>
        public event Action<GuidanceUpdate> GuidanceUpdated;

        /// <summary>Fires once when the visitor reaches the destination.</summary>
        public event Action<RoomNode> Arrived;

        /// <summary>Fires with a plain-English reason when routing fails.</summary>
        public event Action<string> Failed;

        public NavigationState State { get; private set; } = NavigationState.Idle;
        public RoomNode Destination { get; private set; }

        /// <summary>Current path in world space. Empty when not guiding.</summary>
        public IReadOnlyList<Vector3> CurrentPath => _currentPath;

        private readonly List<Vector3> _currentPath = new List<Vector3>();
        private float _nextRecomputeTime;
        private float _offRouteSince = -1f;
        private Vector3 _lastWorldPosition;
        private bool _hasWorldPosition;

        private void OnEnable()
        {
            if (beaconManager != null)
            {
                beaconManager.PositionUpdated += OnPositionUpdated;
            }
        }

        private void OnDisable()
        {
            if (beaconManager != null)
            {
                beaconManager.PositionUpdated -= OnPositionUpdated;
            }
        }

        // ------------------------------------------------------------------
        // Public control
        // ------------------------------------------------------------------

        /// <summary>Starts guiding to a room number. False if no such room exists.</summary>
        public bool SetDestination(string roomNumber)
        {
            if (floorMap == null)
            {
                SetFailed("No floor map loaded.");
                return false;
            }

            RoomNode room = floorMap.FindRoom(roomNumber);

            if (room == null)
            {
                return false;
            }

            SetDestination(room);
            return true;
        }

        /// <summary>Starts guiding to a specific room.</summary>
        public void SetDestination(RoomNode room)
        {
            Destination = room;
            _currentPath.Clear();
            _offRouteSince = -1f;
            _nextRecomputeTime = 0f;

            if (room == null)
            {
                SetState(NavigationState.Idle);
                return;
            }

            if (beaconManager == null || !beaconManager.HasFix)
            {
                SetState(NavigationState.WaitingForPosition);
                return;
            }

            SetState(NavigationState.Routing);
            Recompute(beaconManager.CurrentFix);
        }

        /// <summary>Stops guiding and clears the path.</summary>
        public void Cancel()
        {
            Destination = null;
            _currentPath.Clear();
            PathChanged?.Invoke(_currentPath);
            SetState(NavigationState.Idle);
        }

        /// <summary>Forces an immediate recompute. Used by the "recalculate" button.</summary>
        public void ForceRecompute()
        {
            if (Destination != null && beaconManager != null && beaconManager.HasFix)
            {
                Recompute(beaconManager.CurrentFix);
            }
        }

        // ------------------------------------------------------------------
        // Position handling
        // ------------------------------------------------------------------

        private void OnPositionUpdated(PositionFix fix)
        {
            if (!fix.IsValid || pathfindingEngine == null)
            {
                return;
            }

            _lastWorldPosition = pathfindingEngine.FloorToWorld(fix.FloorPosition);
            _hasWorldPosition = true;

            if (Destination == null)
            {
                return;
            }

            switch (State)
            {
                case NavigationState.WaitingForPosition:
                    SetState(NavigationState.Routing);
                    Recompute(fix);
                    break;

                case NavigationState.Routing:
                    Recompute(fix);
                    break;

                case NavigationState.Guiding:
                case NavigationState.OffRoute:
                    UpdateGuiding(fix);
                    break;
            }
        }

        private void UpdateGuiding(PositionFix fix)
        {
            if (_currentPath.Count < 2)
            {
                Recompute(fix);
                return;
            }

            // Arrival is measured against the destination itself, not the end of the path — the
            // path end can be a little off after snapping, and this is the one number the visitor
            // will judge the whole app on.
            Vector3 destinationWorld = pathfindingEngine.FloorToWorld(Destination.approachPosition);
            Vector3 flatDelta = destinationWorld - _lastWorldPosition;
            flatDelta.y = 0f;

            if (flatDelta.magnitude <= arrivalRadiusMeters)
            {
                _currentPath.Clear();
                PathChanged?.Invoke(_currentPath);
                SetState(NavigationState.Arrived);
                Arrived?.Invoke(Destination);
                return;
            }

            float distanceFromPath = PathfindingEngine.DistanceFromPath(_currentPath, _lastWorldPosition);

            if (distanceFromPath > offRouteThresholdMeters)
            {
                if (_offRouteSince < 0f)
                {
                    _offRouteSince = Time.unscaledTime;
                    SetState(NavigationState.OffRoute);
                }
                else if (Time.unscaledTime - _offRouteSince >= offRouteGraceSeconds)
                {
                    Recompute(fix);
                    return;
                }
            }
            else
            {
                _offRouteSince = -1f;

                if (State == NavigationState.OffRoute)
                {
                    SetState(NavigationState.Guiding);
                }
            }

            if (Time.unscaledTime >= _nextRecomputeTime)
            {
                Recompute(fix);
                return;
            }

            PublishGuidance(fix);
        }

        private void Recompute(PositionFix fix)
        {
            if (pathfindingEngine == null || Destination == null)
            {
                return;
            }

            _nextRecomputeTime = Time.unscaledTime + recomputeInterval;

            PathResult result = pathfindingEngine.FindPath(
                fix.FloorPosition, Destination.approachPosition, _currentPath);

            if (result == PathResult.NotReady)
            {
                SetState(NavigationState.Routing);
                return;
            }

            if (result != PathResult.Success && result != PathResult.Partial)
            {
                SetFailed(PathfindingEngine.Explain(result));
                return;
            }

            _offRouteSince = -1f;
            SetState(NavigationState.Guiding);
            PathChanged?.Invoke(_currentPath);
            PublishGuidance(fix);
        }

        // ------------------------------------------------------------------
        // Guidance text
        // ------------------------------------------------------------------

        private void PublishGuidance(PositionFix fix)
        {
            float remaining = PathfindingEngine.RemainingDistance(_currentPath, _lastWorldPosition);

            TurnDirection turn = FindNextTurn(out float metersToTurn);
            string text = BuildInstructionText(turn, metersToTurn, remaining, fix.Confidence);

            GuidanceUpdated?.Invoke(new GuidanceUpdate(
                remaining, turn, metersToTurn, text, fix.Confidence));
        }

        /// <summary>
        /// Looks ahead along the path for the next meaningful bend.
        ///
        /// The path has been resampled to points every half metre, so consecutive points barely
        /// change direction. Comparing neighbours would find no turns at all. Instead this
        /// compares the direction over a look-back window against the direction over a look-ahead
        /// window — which is how a person perceives a corner: not as a vertex, but as the corridor
        /// heading somewhere different than it was.
        /// </summary>
        private TurnDirection FindNextTurn(out float metersToTurn)
        {
            metersToTurn = 0f;

            if (_currentPath.Count < 3)
            {
                return TurnDirection.Arrive;
            }

            int startIndex = PathfindingEngine.NearestSegmentIndex(
                _currentPath, _lastWorldPosition, out Vector3 projected);

            float accumulated = Vector3.Distance(projected, _currentPath[startIndex + 1]);
            const float windowMeters = 2f;

            for (int i = startIndex + 1; i < _currentPath.Count - 1; i++)
            {
                Vector3 incoming = DirectionOverWindow(i, -windowMeters);
                Vector3 outgoing = DirectionOverWindow(i, windowMeters);

                if (incoming.sqrMagnitude < 0.001f || outgoing.sqrMagnitude < 0.001f)
                {
                    accumulated += Vector3.Distance(_currentPath[i], _currentPath[i + 1]);
                    continue;
                }

                float signedAngle = Vector3.SignedAngle(incoming, outgoing, Vector3.up);

                if (Mathf.Abs(signedAngle) >= turnAngleThreshold)
                {
                    metersToTurn = accumulated;

                    if (Mathf.Abs(signedAngle) >= 150f)
                    {
                        return TurnDirection.TurnAround;
                    }

                    bool sharp = Mathf.Abs(signedAngle) >= sharpTurnAngleThreshold;

                    if (signedAngle > 0f)
                    {
                        return sharp ? TurnDirection.Right : TurnDirection.SlightRight;
                    }

                    return sharp ? TurnDirection.Left : TurnDirection.SlightLeft;
                }

                accumulated += Vector3.Distance(_currentPath[i], _currentPath[i + 1]);
            }

            metersToTurn = accumulated;
            return TurnDirection.Arrive;
        }

        /// <summary>
        /// Average direction of the path around an index, over a window of metres. Negative
        /// distance looks backward. Averaging over a window rather than a single pair is what
        /// makes turn detection immune to the resampling.
        /// </summary>
        private Vector3 DirectionOverWindow(int index, float windowMeters)
        {
            bool forward = windowMeters > 0f;
            float budget = Mathf.Abs(windowMeters);

            Vector3 origin = _currentPath[index];
            Vector3 furthest = origin;
            float travelled = 0f;

            int step = forward ? 1 : -1;

            for (int i = index; i >= 0 && i < _currentPath.Count; i += step)
            {
                int next = i + step;

                if (next < 0 || next >= _currentPath.Count)
                {
                    break;
                }

                travelled += Vector3.Distance(_currentPath[i], _currentPath[next]);
                furthest = _currentPath[next];

                if (travelled >= budget)
                {
                    break;
                }
            }

            Vector3 direction = forward ? furthest - origin : origin - furthest;
            direction.y = 0f;
            return direction.normalized;
        }

        private string BuildInstructionText(
            TurnDirection turn,
            float metersToTurn,
            float remainingMeters,
            float confidence)
        {
            // Be honest rather than confidently wrong. A visitor who is told "finding you" for
            // three seconds forgives it; one who is sent down the wrong corridor does not.
            if (confidence < 0.25f)
            {
                return "Finding your position - keep walking";
            }

            if (turn == TurnDirection.Arrive || metersToTurn > turnAnnounceDistanceMeters)
            {
                if (remainingMeters <= arrivalRadiusMeters * 1.5f)
                {
                    return $"{Destination?.roomNumber} is just ahead";
                }

                return $"Continue for {FormatDistance(remainingMeters)}";
            }

            string direction;

            switch (turn)
            {
                case TurnDirection.Left:
                    direction = "Turn left";
                    break;

                case TurnDirection.Right:
                    direction = "Turn right";
                    break;

                case TurnDirection.SlightLeft:
                    direction = "Bear left";
                    break;

                case TurnDirection.SlightRight:
                    direction = "Bear right";
                    break;

                case TurnDirection.TurnAround:
                    return "Turn around";

                default:
                    direction = "Continue";
                    break;
            }

            if (metersToTurn < 2f)
            {
                return $"{direction} now";
            }

            return $"{direction} in {FormatDistance(metersToTurn)}";
        }

        /// <summary>
        /// Rounds distances the way a person would say them. "Continue for 43.7 m" is precision
        /// nobody asked for and precision this system does not have.
        /// </summary>
        public static string FormatDistance(float meters)
        {
            if (meters < 10f)
            {
                return $"{Mathf.RoundToInt(meters)} m";
            }

            if (meters < 100f)
            {
                return $"{Mathf.RoundToInt(meters / 5f) * 5} m";
            }

            return $"{Mathf.RoundToInt(meters / 10f) * 10} m";
        }

        // ------------------------------------------------------------------
        // State
        // ------------------------------------------------------------------

        private void SetState(NavigationState state)
        {
            if (State == state)
            {
                return;
            }

            State = state;
            StateChanged?.Invoke(state);
        }

        private void SetFailed(string reason)
        {
            SetState(NavigationState.Failed);
            Failed?.Invoke(reason);
            Debug.LogWarning($"[NavigationSession] {reason}");
        }

        /// <summary>
        /// Which way the visitor is facing, in world space. Prefers the camera, because that is
        /// what they are actually looking through. Falls back to direction of travel, which is
        /// only meaningful while they are moving — and is exactly why the entrance QR code
        /// matters: it gives a heading before anyone has taken a step.
        /// </summary>
        public Vector3 CurrentHeadingWorld
        {
            get
            {
                if (headingSource != null)
                {
                    Vector3 forward = headingSource.forward;
                    forward.y = 0f;

                    if (forward.sqrMagnitude > 0.001f)
                    {
                        return forward.normalized;
                    }
                }

                if (beaconManager != null && beaconManager.HasFix)
                {
                    Vector2 velocity = beaconManager.CurrentFix.Velocity;

                    if (velocity.sqrMagnitude > 0.01f)
                    {
                        return new Vector3(velocity.x, 0f, velocity.y).normalized;
                    }
                }

                return Vector3.forward;
            }
        }

        /// <summary>True once a position has been resolved at least once this session.</summary>
        public bool HasWorldPosition => _hasWorldPosition;

        /// <summary>Last known world position of the visitor.</summary>
        public Vector3 LastWorldPosition => _lastWorldPosition;
    }
}
