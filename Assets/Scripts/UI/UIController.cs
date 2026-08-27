using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Wayfinding.AR;
using Wayfinding.Data;
using Wayfinding.Navigation;
using Wayfinding.Positioning;

namespace Wayfinding.UI
{
    /// <summary>
    /// The screen flow, and the last thing to build — by then the parts underneath have taught
    /// you what the screens actually need to say.
    ///
    /// Five screens:
    ///   SCAN     "Point your camera at the code by the entrance"
    ///   ENTER    a numeric keypad and a suggestion list
    ///   CONFIRM  "412 - Cardiology. About 60 m, 1 minute. Start walking?"
    ///   GUIDE    the instruction, the distance, and the camera view with the line on the floor
    ///   ARRIVED  "You have arrived at 412"
    ///
    /// Worth keeping in mind while building this: the person holding the phone is often anxious,
    /// often late, sometimes in pain, and frequently over seventy. That points at specific
    /// choices — large type, one instruction on screen at a time, no jargon, no more than one
    /// decision per screen, and never a dead end without a way forward. The Cancel button matters
    /// as much as the Go button.
    /// </summary>
    public class UIController : MonoBehaviour
    {
        public enum Screen
        {
            Scan,
            EnterRoom,
            Confirm,
            Guiding,
            Arrived,
            Error
        }

        [Header("Dependencies")]
        public NavigationSession navigationSession;
        public BeaconManager beaconManager;
        public QrAnchorResolver qrResolver;
        public ARWorldAligner worldAligner;
        public FloorMap floorMap;

        [Header("Screen roots")]
        public GameObject scanScreen;
        public GameObject enterRoomScreen;
        public GameObject confirmScreen;
        public GameObject guidingScreen;
        public GameObject arrivedScreen;
        public GameObject errorScreen;

        [Header("Scan screen")]
        public TMP_Text scanHintText;

        [Header("Room entry")]
        public TMP_InputField roomInput;
        public Button roomGoButton;
        public TMP_Text roomErrorText;

        [Tooltip("Container the suggestion buttons are spawned under.")]
        public Transform suggestionContainer;

        [Tooltip("A button prefab with a TMP_Text child, used for each suggestion.")]
        public Button suggestionButtonPrefab;

        [Header("Confirm screen")]
        public TMP_Text confirmRoomText;
        public TMP_Text confirmDetailText;
        public Button confirmStartButton;
        public Button confirmBackButton;

        [Header("Guiding screen")]
        [Tooltip("The main instruction: 'Turn left in 8 m'. This is the one thing on screen that " +
                 "must be readable at arm's length by someone who left their glasses at home.")]
        public TMP_Text instructionText;

        public TMP_Text remainingDistanceText;
        public TMP_Text destinationLabelText;
        public Image turnArrowImage;
        public Button cancelButton;

        [Tooltip("Shown when the position fix is poor, so the visitor knows to keep walking " +
                 "rather than trusting a wobbly arrow.")]
        public GameObject lowConfidenceIndicator;

        [Header("Turn arrow sprites")]
        public Sprite arrowStraight;
        public Sprite arrowLeft;
        public Sprite arrowRight;
        public Sprite arrowSlightLeft;
        public Sprite arrowSlightRight;
        public Sprite arrowTurnAround;
        public Sprite arrowArrive;

        [Header("Arrived screen")]
        public TMP_Text arrivedText;
        public Button arrivedDoneButton;

        [Header("Error screen")]
        public TMP_Text errorText;
        public Button errorRetryButton;

        [Header("Behaviour")]
        [Tooltip("Average walking speed used for the time estimate, in metres per second. Keep " +
                 "this conservative — promising three minutes and taking six is worse than the " +
                 "reverse.")]
        public float estimatedWalkSpeed = 0.9f;

        public Screen CurrentScreen { get; private set; } = Screen.Scan;

        private readonly List<Button> _suggestionButtons = new List<Button>();
        private RoomNode _pendingDestination;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        private void OnEnable()
        {
            if (navigationSession != null)
            {
                navigationSession.StateChanged += OnNavigationStateChanged;
                navigationSession.GuidanceUpdated += OnGuidanceUpdated;
                navigationSession.Arrived += OnArrived;
                navigationSession.Failed += OnNavigationFailed;
            }

            if (qrResolver != null)
            {
                qrResolver.AnchorResolved += OnAnchorResolved;
                qrResolver.UnknownCodeScanned += OnUnknownCode;
            }

            if (beaconManager != null)
            {
                beaconManager.ScannerStatusChanged += OnScannerStatusChanged;
            }

            WireButtons();
        }

        private void OnDisable()
        {
            if (navigationSession != null)
            {
                navigationSession.StateChanged -= OnNavigationStateChanged;
                navigationSession.GuidanceUpdated -= OnGuidanceUpdated;
                navigationSession.Arrived -= OnArrived;
                navigationSession.Failed -= OnNavigationFailed;
            }

            if (qrResolver != null)
            {
                qrResolver.AnchorResolved -= OnAnchorResolved;
                qrResolver.UnknownCodeScanned -= OnUnknownCode;
            }

            if (beaconManager != null)
            {
                beaconManager.ScannerStatusChanged -= OnScannerStatusChanged;
            }
        }

        private void Start()
        {
            ShowScreen(Screen.Scan);

            if (scanHintText != null)
            {
                scanHintText.text = "Point your camera at the code near the entrance";
            }
        }

        private void WireButtons()
        {
            if (roomGoButton != null)
            {
                roomGoButton.onClick.RemoveListener(OnGoPressed);
                roomGoButton.onClick.AddListener(OnGoPressed);
            }

            if (roomInput != null)
            {
                roomInput.onValueChanged.RemoveListener(OnRoomInputChanged);
                roomInput.onValueChanged.AddListener(OnRoomInputChanged);
                roomInput.onSubmit.RemoveListener(OnRoomSubmitted);
                roomInput.onSubmit.AddListener(OnRoomSubmitted);
            }

            if (confirmStartButton != null)
            {
                confirmStartButton.onClick.RemoveListener(OnStartPressed);
                confirmStartButton.onClick.AddListener(OnStartPressed);
            }

            if (confirmBackButton != null)
            {
                confirmBackButton.onClick.RemoveListener(OnBackToEntryPressed);
                confirmBackButton.onClick.AddListener(OnBackToEntryPressed);
            }

            if (cancelButton != null)
            {
                cancelButton.onClick.RemoveListener(OnCancelPressed);
                cancelButton.onClick.AddListener(OnCancelPressed);
            }

            if (arrivedDoneButton != null)
            {
                arrivedDoneButton.onClick.RemoveListener(OnDonePressed);
                arrivedDoneButton.onClick.AddListener(OnDonePressed);
            }

            if (errorRetryButton != null)
            {
                errorRetryButton.onClick.RemoveListener(OnRetryPressed);
                errorRetryButton.onClick.AddListener(OnRetryPressed);
            }
        }

        // ------------------------------------------------------------------
        // Screen management
        // ------------------------------------------------------------------

        public void ShowScreen(Screen screen)
        {
            CurrentScreen = screen;

            SetActive(scanScreen, screen == Screen.Scan);
            SetActive(enterRoomScreen, screen == Screen.EnterRoom);
            SetActive(confirmScreen, screen == Screen.Confirm);
            SetActive(guidingScreen, screen == Screen.Guiding);
            SetActive(arrivedScreen, screen == Screen.Arrived);
            SetActive(errorScreen, screen == Screen.Error);
        }

        private static void SetActive(GameObject target, bool active)
        {
            if (target != null && target.activeSelf != active)
            {
                target.SetActive(active);
            }
        }

        // ------------------------------------------------------------------
        // Scan
        // ------------------------------------------------------------------

        private void OnAnchorResolved(FloorMap.QrAnchor anchor)
        {
            ShowScreen(Screen.EnterRoom);

            if (roomInput != null)
            {
                roomInput.text = string.Empty;
                roomInput.ActivateInputField();
            }

            ClearSuggestions();
            SetText(roomErrorText, string.Empty);
        }

        private void OnUnknownCode(string code)
        {
            if (scanHintText != null)
            {
                scanHintText.text = "That code is not one of ours. Look for the wayfinding code " +
                                    "near the main entrance.";
            }
        }

        // ------------------------------------------------------------------
        // Room entry
        // ------------------------------------------------------------------

        private void OnRoomInputChanged(string value)
        {
            SetText(roomErrorText, string.Empty);
            RebuildSuggestions(value);
        }

        private void OnRoomSubmitted(string value)
        {
            OnGoPressed();
        }

        private void RebuildSuggestions(string partial)
        {
            ClearSuggestions();

            if (floorMap == null || suggestionContainer == null || suggestionButtonPrefab == null)
            {
                return;
            }

            List<RoomNode> matches = floorMap.SuggestRooms(partial);

            foreach (RoomNode room in matches)
            {
                Button button = Instantiate(suggestionButtonPrefab, suggestionContainer);
                var label = button.GetComponentInChildren<TMP_Text>();

                if (label != null)
                {
                    label.text = room.FullLabel;
                }

                RoomNode captured = room;
                button.onClick.AddListener(() => SelectRoom(captured));
                _suggestionButtons.Add(button);
            }
        }

        private void ClearSuggestions()
        {
            foreach (Button button in _suggestionButtons)
            {
                if (button != null)
                {
                    Destroy(button.gameObject);
                }
            }

            _suggestionButtons.Clear();
        }

        private void OnGoPressed()
        {
            if (floorMap == null || roomInput == null)
            {
                return;
            }

            RoomNode room = floorMap.FindRoom(roomInput.text);

            if (room == null)
            {
                // Say what to do next, not just what went wrong. A visitor standing in a lobby
                // being told "invalid input" has nowhere to go.
                SetText(roomErrorText,
                    $"We could not find room {roomInput.text} on this floor. Check the number on " +
                    "your appointment letter, or ask at the desk.");
                return;
            }

            SelectRoom(room);
        }

        private void SelectRoom(RoomNode room)
        {
            _pendingDestination = room;
            ShowScreen(Screen.Confirm);

            SetText(confirmRoomText, room.FullLabel);

            string detail = BuildRouteEstimate(room);
            SetText(confirmDetailText, detail);
        }

        /// <summary>
        /// Straight-line estimate for the confirm screen. Deliberately approximate — this runs
        /// before the real path is computed, and the visitor only needs to know whether this is a
        /// thirty-second walk or a five-minute one.
        /// </summary>
        private string BuildRouteEstimate(RoomNode room)
        {
            if (beaconManager == null || !beaconManager.HasFix || floorMap == null)
            {
                return "Getting your position...";
            }

            float meters = floorMap.ToMeters(
                Vector2.Distance(beaconManager.CurrentFix.FloorPosition, room.approachPosition));

            // Corridors bend, so the walk is always longer than the straight line.
            meters *= 1.25f;

            int minutes = Mathf.Max(1, Mathf.RoundToInt(meters / Mathf.Max(estimatedWalkSpeed, 0.3f) / 60f));

            string department = string.IsNullOrEmpty(room.department) ? "" : $"{room.department}\n";

            return $"{department}About {NavigationSession.FormatDistance(meters)}, " +
                   $"roughly {minutes} minute{(minutes == 1 ? "" : "s")} on foot.";
        }

        private void OnBackToEntryPressed()
        {
            _pendingDestination = null;
            ShowScreen(Screen.EnterRoom);
        }

        // ------------------------------------------------------------------
        // Guidance
        // ------------------------------------------------------------------

        private void OnStartPressed()
        {
            if (_pendingDestination == null || navigationSession == null)
            {
                return;
            }

            navigationSession.SetDestination(_pendingDestination);
            SetText(destinationLabelText, _pendingDestination.FullLabel);
            ShowScreen(Screen.Guiding);
        }

        private void OnNavigationStateChanged(NavigationState state)
        {
            switch (state)
            {
                case NavigationState.WaitingForPosition:
                    SetText(instructionText, "Finding you...");
                    SetText(remainingDistanceText, string.Empty);
                    break;

                case NavigationState.Routing:
                    SetText(instructionText, "Working out the way...");
                    break;

                case NavigationState.OffRoute:
                    SetText(instructionText, "Checking the route...");
                    break;

                case NavigationState.Arrived:
                    ShowScreen(Screen.Arrived);
                    break;

                case NavigationState.Idle:
                    ShowScreen(Screen.EnterRoom);
                    break;
            }
        }

        private void OnGuidanceUpdated(GuidanceUpdate guidance)
        {
            if (CurrentScreen != Screen.Guiding)
            {
                return;
            }

            SetText(instructionText, guidance.InstructionText);
            SetText(remainingDistanceText,
                $"{NavigationSession.FormatDistance(guidance.RemainingMeters)} to go");

            if (turnArrowImage != null)
            {
                Sprite sprite = SpriteForTurn(guidance.NextTurn);

                if (sprite != null)
                {
                    turnArrowImage.sprite = sprite;
                    turnArrowImage.enabled = true;
                }
            }

            SetActive(lowConfidenceIndicator, guidance.PositionConfidence < 0.35f);
        }

        private Sprite SpriteForTurn(TurnDirection turn)
        {
            switch (turn)
            {
                case TurnDirection.Left: return arrowLeft;
                case TurnDirection.Right: return arrowRight;
                case TurnDirection.SlightLeft: return arrowSlightLeft;
                case TurnDirection.SlightRight: return arrowSlightRight;
                case TurnDirection.TurnAround: return arrowTurnAround;
                case TurnDirection.Arrive: return arrowArrive;
                default: return arrowStraight;
            }
        }

        private void OnCancelPressed()
        {
            navigationSession?.Cancel();
            _pendingDestination = null;
            ShowScreen(Screen.EnterRoom);
        }

        // ------------------------------------------------------------------
        // Arrival and errors
        // ------------------------------------------------------------------

        private void OnArrived(RoomNode room)
        {
            SetText(arrivedText, $"You have arrived at {room.FullLabel}");
            ShowScreen(Screen.Arrived);
        }

        private void OnDonePressed()
        {
            navigationSession?.Cancel();
            _pendingDestination = null;

            if (roomInput != null)
            {
                roomInput.text = string.Empty;
            }

            ShowScreen(Screen.EnterRoom);
        }

        private void OnNavigationFailed(string reason)
        {
            SetText(errorText, "We could not work out a route to that room.\n\nPlease ask at the " +
                               "front desk, and let them know the app could not find the way.");
            Debug.LogWarning($"[UIController] Navigation failed: {reason}");
            ShowScreen(Screen.Error);
        }

        private void OnRetryPressed()
        {
            if (_pendingDestination != null && navigationSession != null)
            {
                navigationSession.SetDestination(_pendingDestination);
                ShowScreen(Screen.Guiding);
                return;
            }

            ShowScreen(Screen.EnterRoom);
        }

        /// <summary>
        /// Bluetooth problems get their own words. "Error 4" helps nobody; "Turn on Bluetooth"
        /// is something a visitor can act on without finding a member of staff.
        /// </summary>
        private void OnScannerStatusChanged(BeaconScannerStatus status, string detail)
        {
            switch (status)
            {
                case BeaconScannerStatus.PermissionDenied:
                    SetText(errorText,
                        "This app needs Bluetooth permission to find where you are inside the " +
                        "building.\n\nYou can turn it on in your phone's settings for this app.");
                    ShowScreen(Screen.Error);
                    break;

                case BeaconScannerStatus.BluetoothOff:
                    SetText(errorText,
                        "Please switch Bluetooth on.\n\nThe app uses it to work out where you are " +
                        "inside the building - it does not connect to anything or collect any " +
                        "information about you.");
                    ShowScreen(Screen.Error);
                    break;

                case BeaconScannerStatus.Unsupported:
                    SetText(errorText,
                        "This phone cannot use the indoor positioning this app needs.\n\nPlease " +
                        "ask at the front desk for directions.");
                    ShowScreen(Screen.Error);
                    break;
            }
        }

        private static void SetText(TMP_Text target, string value)
        {
            if (target != null)
            {
                target.text = value;
            }
        }
    }
}
