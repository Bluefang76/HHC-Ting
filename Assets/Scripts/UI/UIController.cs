using UnityEngine;

namespace Wayfinder.UI
{
    /// <summary>
    /// Screen flow and status messaging.
    ///
    /// UI is LAST in the build order (see CLAUDE.md). This file exists to hold the
    /// shape of the flow, not to be polished now.
    ///
    /// The flow: scan the entrance QR -> enter a room number -> follow the line -> arrive.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class UIController : MonoBehaviour
    {
        public enum Screen
        {
            ScanQr,
            EnterRoom,
            Navigating,
            Arrived,
            Degraded    // positioning or tracking is not trustworthy — say so
        }

        [SerializeField] private GameObject scanQrPanel;
        [SerializeField] private GameObject enterRoomPanel;
        [SerializeField] private GameObject navigatingPanel;
        [SerializeField] private GameObject arrivedPanel;
        [SerializeField] private GameObject degradedPanel;

        public Screen Current { get; private set; } = Screen.ScanQr;

        public void Show(Screen screen)
        {
            Current = screen;
            if (scanQrPanel != null) scanQrPanel.SetActive(screen == Screen.ScanQr);
            if (enterRoomPanel != null) enterRoomPanel.SetActive(screen == Screen.EnterRoom);
            if (navigatingPanel != null) navigatingPanel.SetActive(screen == Screen.Navigating);
            if (arrivedPanel != null) arrivedPanel.SetActive(screen == Screen.Arrived);
            if (degradedPanel != null) degradedPanel.SetActive(screen == Screen.Degraded);
        }

        /// <summary>Distance remaining, meters. Drives the "X m to go" readout.</summary>
        public void SetRemainingDistance(float meters)
        {
            // TODO: format for a visitor, not an engineer. Feet may be the right unit here.
        }

        /// <summary>
        /// Explain, in plain language, why the app cannot guide right now — and give a
        /// useful fallback ("head toward the main elevators") rather than a blank screen.
        /// </summary>
        public void ShowDegraded(string reason)
        {
            Show(Screen.Degraded);
            // TODO: map internal reasons to visitor-readable text. Never show enum names.
        }
    }
}
