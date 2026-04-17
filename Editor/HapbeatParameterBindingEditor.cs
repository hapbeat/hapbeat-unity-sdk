#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatParameterBinding.
    /// Shows conditional fields and live preview of current values.
    /// </summary>
    [CustomEditor(typeof(HapbeatParameterBinding))]
    [CanEditMultipleObjects]
    public class HapbeatParameterBindingEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // Source
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_sourceTransform"),
                new GUIContent("Transform", "Object to read the input variable from."));
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

            // Mapping
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

            // Output
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_outputParameter"),
                new GUIContent("Parameter", "Volume: AudioSource.volume (haptic intensity)\nPitch: AudioSource.pitch (vibration frequency)\nPan: AudioSource.panStereo (L/R)\nBridgeGain: HapbeatAudioBridge.Gain"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_outputMin"),
                new GUIContent("Output Min", "Output value when input = inputMin."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_outputMax"),
                new GUIContent("Output value when input = inputMax."));

            // Debug section
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_debugLog"),
                new GUIContent("Console Log", "Log values to Unity console."));
            if (serializedObject.FindProperty("_debugLog").boolValue)
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_debugLogInterval"),
                    new GUIContent("Log Interval", "Seconds between console log entries."));
            }

            // Live preview in play mode
            if (Application.isPlaying)
            {
                var binding = (HapbeatParameterBinding)target;
                EditorGUILayout.Space(2);

                // Raw input
                EditorGUI.BeginDisabledGroup(true);
                EditorGUILayout.FloatField("Input (raw)", binding.CurrentInput);
                EditorGUI.EndDisabledGroup();

                // Normalized 0-1 bar
                var normRect = EditorGUILayout.GetControlRect(false, 18);
                EditorGUI.ProgressBar(normRect, binding.CurrentNormalized,
                    $"Normalized  {binding.CurrentNormalized:F2}");

                // Output bar (normalized to outputMin..outputMax visual range)
                float outMin = serializedObject.FindProperty("_outputMin").floatValue;
                float outMax = serializedObject.FindProperty("_outputMax").floatValue;
                float outRange = Mathf.Abs(outMax - outMin);
                float outNormalized = outRange > 0.0001f
                    ? Mathf.Clamp01((binding.CurrentOutput - outMin) / (outMax - outMin))
                    : 0f;
                var outRect = EditorGUILayout.GetControlRect(false, 18);
                var outParam = (BindingOutputParameter)serializedObject.FindProperty("_outputParameter").enumValueIndex;
                EditorGUI.ProgressBar(outRect, outNormalized,
                    $"{outParam}  {binding.CurrentOutput:F3}");

                // Force repaint for live updates
                Repaint();
            }

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
