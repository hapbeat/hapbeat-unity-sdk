#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatParameterBinding.
    /// When the binding is linked to an EventMap preset, tuning fields are shown
    /// read-only and the user is directed to the EventMap for edits. Live preview
    /// progress bars appear in Play mode.
    /// </summary>
    [CustomEditor(typeof(HapbeatParameterBinding))]
    [CanEditMultipleObjects]
    public class HapbeatParameterBindingEditor : UnityEditor.Editor
    {
        private static GUIStyle _linkBannerStyle;
        private static Texture2D _linkBannerBg;

        /// <summary>
        /// Lazily build a GUIStyle whose background is a solid blue tint. Used for
        /// the "linked" banner at the top of the inspector so the state is visually
        /// distinct from a standard gray HelpBox on both light and dark themes.
        /// </summary>
        private static GUIStyle GetLinkBannerStyle()
        {
            if (_linkBannerStyle != null && _linkBannerBg != null) return _linkBannerStyle;
            _linkBannerBg = new Texture2D(1, 1);
            // Blue tint — slightly translucent so the editor background shows through
            // just enough to match the theme (still readable in dark mode).
            _linkBannerBg.SetPixel(0, 0, new Color(0.22f, 0.46f, 0.82f, 0.42f));
            _linkBannerBg.Apply();
            _linkBannerBg.hideFlags = HideFlags.HideAndDontSave;
            _linkBannerStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(10, 10, 8, 8),
                wordWrap = true,
                richText = true,
            };
            _linkBannerStyle.normal.background = _linkBannerBg;
            _linkBannerStyle.normal.textColor = EditorGUIUtility.isProSkin
                ? new Color(0.92f, 0.96f, 1f)
                : new Color(0.05f, 0.10f, 0.25f);
            return _linkBannerStyle;
        }
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var binding = (HapbeatParameterBinding)target;
            var linkedMapProp = serializedObject.FindProperty("_linkedEventMap");
            var linkedIdProp = serializedObject.FindProperty("_linkedBindingId");
            var preset = binding.ResolveLinkedPreset();
            bool isLinked = linkedMapProp.objectReferenceValue != null
                            && !string.IsNullOrEmpty(linkedIdProp.stringValue);

            // ---- Link banner ----
            DrawLinkBanner(linkedMapProp, linkedIdProp, preset, isLinked);

            EditorGUILayout.Space(4);

            // ---- Source (always editable — it's scene-local, not a tuning value) ----
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sourceTransform"),
                new GUIContent("Transform", "Object to read the input variable from. " +
                    "Scene-local reference; not managed by the EventMap link."));

            // ---- Tuning fields (linked = read-only, standalone = editable) ----
            using (new EditorGUI.DisabledScope(isLinked))
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_sourceProperty"),
                    new GUIContent("Property", "Which property of the Transform/Rigidbody to read."));

                var srcProp = (BindingSourceProperty)serializedObject.FindProperty("_sourceProperty").enumValueIndex;
                if (srcProp == BindingSourceProperty.VelocityMagnitude || srcProp == BindingSourceProperty.AngularVelocityMagnitude)
                {
                    var srcTransform = serializedObject.FindProperty("_sourceTransform").objectReferenceValue as Transform;
                    if (srcTransform != null && srcTransform.GetComponent<Rigidbody>() == null)
                        EditorGUILayout.HelpBox("Rigidbody not found on source Transform.", MessageType.Warning);
                }

                EditorGUILayout.PropertyField(serializedObject.FindProperty("_inputMin"),
                    new GUIContent("Input Min", "Input value mapped to Output Min."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_inputMax"),
                    new GUIContent("Input Max", "Input value mapped to Output Max."));

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Mapping", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_curveType"),
                    new GUIContent("Curve", "How input is mapped to output.\nLinear, EaseIn (x\u00b2), EaseOut (1-(1-x)\u00b2), Exponential, Custom."));

                var curveType = (BindingCurveType)serializedObject.FindProperty("_curveType").enumValueIndex;
                if (curveType == BindingCurveType.Custom)
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_customCurve"),
                        new GUIContent("Custom Curve", "X: 0-1 normalized input, Y: 0-1 output factor."));
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_outputParameter"),
                    new GUIContent("Parameter", "Volume: AudioSource.volume (haptic intensity)\nPitch: AudioSource.pitch (vibration frequency)\nPan: AudioSource.panStereo (L/R)\nBridgeGain: HapbeatAudioBridge.Gain"));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_outputMin"),
                    new GUIContent("Output Min", "Output value when input = inputMin."));
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_outputMax"),
                    new GUIContent("Output Max", "Output value when input = inputMax."));

                // Debug section
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_debugLog"),
                    new GUIContent("Console Log",
                        "Log values to Unity console. Emits only when the normalized " +
                        "value changes \u2265 Change Threshold, throttled by Log Interval."));
                if (serializedObject.FindProperty("_debugLog").boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_debugLogInterval"),
                        new GUIContent("Log Interval",
                            "Minimum seconds between console log entries (throttle)."));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_debugLogChangeThreshold"),
                        new GUIContent("Change Threshold",
                            "Minimum normalized-value change (0-1) to emit a log line. " +
                            "0 = log every interval regardless of change."));
                    EditorGUI.indentLevel--;
                }
            }

            // ---- Live preview in play mode ----
            if (Application.isPlaying)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Live", EditorStyles.boldLabel);

                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.FloatField("Input (raw)", binding.CurrentInput);
                EditorGUI.EndDisabledGroup();

                var normRect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(normRect, binding.CurrentNormalized,
                    $"Normalized  {binding.CurrentNormalized:F2}");

                // Determine effective output range for the progress bar.
                float outMin = preset != null ? preset.outputMin : serializedObject.FindProperty("_outputMin").floatValue;
                float outMax = preset != null ? preset.outputMax : serializedObject.FindProperty("_outputMax").floatValue;
                float outRange = Mathf.Abs(outMax - outMin);
                float outNormalized = outRange > 0.0001f
                    ? Mathf.Clamp01((binding.CurrentOutput - outMin) / (outMax - outMin))
                    : 0f;
                var outRect = EditorGUILayout.GetControlRect(false, 18);
                var outParam = preset != null
                    ? preset.outputParameter
                    : (BindingOutputParameter)serializedObject.FindProperty("_outputParameter").enumValueIndex;
                EditorGUI.ProgressBar(outRect, outNormalized,
                    $"{outParam}  {binding.CurrentOutput:F3}");

                Repaint();
            }

            serializedObject.ApplyModifiedProperties();
        }

        // ------------------------------------------------------------------------

        /// <summary>
        /// Draw a banner at the top of the Inspector indicating whether the binding is
        /// linked to an EventMap preset (tuning values come from the preset), or
        /// standalone (tuning values are local to this component).
        /// </summary>
        private void DrawLinkBanner(
            SerializedProperty linkedMapProp,
            SerializedProperty linkedIdProp,
            HapbeatBindingPreset preset,
            bool isLinked)
        {
            EditorGUILayout.LabelField("Event Map Link", EditorStyles.boldLabel);

            if (!isLinked)
            {
                EditorGUILayout.HelpBox(
                    "Standalone binding \u2014 tuning values are read from the SerializedFields " +
                    "below. Run 'Hapbeat/Batch Setup' to link this to an EventMap preset so " +
                    "the values stay synchronised with the EventMap.",
                    MessageType.None);
                EditorGUILayout.PropertyField(linkedMapProp,
                    new GUIContent("Linked Event Map", "Assign to enable live-tuning from EventMap preset."));
                return;
            }

            var map = linkedMapProp.objectReferenceValue as HapbeatEventMap;
            string shortId = linkedIdProp.stringValue;
            if (!string.IsNullOrEmpty(shortId) && shortId.Length > 8)
                shortId = shortId.Substring(0, 8) + "\u2026";

            // Locate the owning entry (just for a nicer label).
            string entryLabel = "(preset not found)";
            if (preset != null && map != null)
            {
                for (int ei = 0; ei < map.entries.Count; ei++)
                {
                    var entry = map.entries[ei];
                    if (entry?.bindings == null) continue;
                    for (int bi = 0; bi < entry.bindings.Count; bi++)
                    {
                        if (ReferenceEquals(entry.bindings[bi], preset))
                        {
                            string name = string.IsNullOrEmpty(entry.displayName) ? $"entry[{ei}]" : entry.displayName;
                            entryLabel = $"{name} / binding #{bi}";
                            break;
                        }
                    }
                }
            }

            if (preset != null)
            {
                // Tinted blue banner — we bypass HelpBox (gray) and draw our own styled box
                // so the "linked" state reads clearly against both light and dark themes.
                string msg =
                    $"<b>Linked \u2192 {map.name} / {entryLabel}</b>\n" +
                    $"Tuning values come from the EventMap preset (edit there). " +
                    $"Fields below are read-only.";
                EditorGUILayout.LabelField(msg, GetLinkBannerStyle());
            }
            else
            {
                EditorGUILayout.HelpBox(
                    $"Linked to {map?.name ?? "(missing map)"} but preset id '{shortId}' " +
                    $"was not found. Falling back to local values. " +
                    $"Re-run 'Hapbeat/Batch Setup' to refresh the link, or click Unlink.",
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Event Map", GUILayout.Width(120)))
            {
                Selection.activeObject = map;
                EditorGUIUtility.PingObject(map);
                EditorApplication.ExecuteMenuItem("Hapbeat/Event Map");
            }
            if (GUILayout.Button("Unlink", GUILayout.Width(80)))
            {
                if (EditorUtility.DisplayDialog(
                        "Unlink Binding",
                        "Unlink this binding from the EventMap preset? " +
                        "After unlinking, values will be edited locally (will not " +
                        "track EventMap changes).",
                        "Unlink", "Cancel"))
                {
                    linkedMapProp.objectReferenceValue = null;
                    linkedIdProp.stringValue = "";
                }
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
#endif
