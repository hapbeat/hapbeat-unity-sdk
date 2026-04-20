using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Diagnostic helper that logs a tagged, time-stamped line to the Unity Console
    /// whenever one of its LogEvent / LogHover... / LogSelect... / LogActivate... methods
    /// is invoked via UnityEvent.
    ///
    /// Intended use: attach to an XRI Interactable GameObject, wire every UnityEvent
    /// (hoverEntered, hoverExited, firstHoverEntered, lastHoverExited, selectEntered,
    /// selectExited, activated, deactivated) to the matching log method, then enter
    /// Play mode and observe the firing order in the Console. Lets the designer pick
    /// the correct event to wire Hapbeat Fire() / Stop() to.
    ///
    /// The SDK does NOT depend on XRI — this component only logs strings. Wire by hand
    /// or use 'Hapbeat > Debug > Attach Event Logger to Selected' which auto-wires
    /// any UnityEvent field it can discover on the selected GameObject via reflection.
    /// </summary>
    [AddComponentMenu("Hapbeat/Hapbeat Event Logger (Diagnostic)")]
    public class HapbeatEventLogger : MonoBehaviour
    {
        [Tooltip("Label prefix for logged lines. Empty = use GameObject name.")]
        [SerializeField]
        private string _label = "";

        [Tooltip("Include the Time.unscaledTime stamp in each line. Useful for measuring " +
                 "inter-event latency (e.g. ray-hover-exit \u2192 poke-hover-enter gap).")]
        [SerializeField]
        private bool _includeTimestamp = true;

        [Tooltip("Colorize the [EventLog] prefix in rich-text-enabled consoles.")]
        [SerializeField]
        private bool _colorize = true;

        [Tooltip("Prefix color for the log line (only used when Colorize is on).")]
        [SerializeField]
        private Color _color = new Color(0.55f, 0.85f, 1.0f);

        private string Label => string.IsNullOrEmpty(_label) ? gameObject.name : _label;

        /// <summary>
        /// Generic tagged logger. Wire any UnityEvent(string) to this method; in the
        /// Inspector set the string parameter to describe the source event
        /// (e.g. "hoverEntered", "selectEntered").
        /// </summary>
        public void LogEvent(string tag)
        {
            EmitLog(tag);
        }

        // Shortcuts for UnityEvents that can't pass a string (UnityEvent, UnityEvent<T>
        // where T is not string). The XRI interactable events carry an args object, so
        // these no-arg methods are the easiest way to wire without an adapter.
        public void LogHoverEntered()      { EmitLog("hoverEntered"); }
        public void LogHoverExited()       { EmitLog("hoverExited"); }
        public void LogFirstHoverEntered() { EmitLog("firstHoverEntered"); }
        public void LogLastHoverExited()   { EmitLog("lastHoverExited"); }
        public void LogSelectEntered()     { EmitLog("selectEntered"); }
        public void LogSelectExited()      { EmitLog("selectExited"); }
        public void LogFirstSelectEntered(){ EmitLog("firstSelectEntered"); }
        public void LogLastSelectExited()  { EmitLog("lastSelectExited"); }
        public void LogActivated()         { EmitLog("activated"); }
        public void LogDeactivated()       { EmitLog("deactivated"); }
        public void LogFocusEntered()      { EmitLog("focusEntered"); }
        public void LogFocusExited()       { EmitLog("focusExited"); }

        private void EmitLog(string tag)
        {
            string prefix = "[EventLog]";
            if (_colorize)
            {
                string hex = ColorUtility.ToHtmlStringRGB(_color);
                prefix = $"<color=#{hex}>{prefix}</color>";
            }

            string timeStamp = _includeTimestamp
                ? $"  @t={Time.unscaledTime:F3}"
                : "";

            Debug.Log($"{prefix} {Label}: {tag}{timeStamp}", this);
        }
    }
}
