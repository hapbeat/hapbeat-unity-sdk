#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Custom inspector for HapbeatAnimatorTrigger.
    /// Extends the base trigger editor with Animator parameter name dropdown.
    /// </summary>
    [CustomEditor(typeof(HapbeatAnimatorTrigger))]
    [CanEditMultipleObjects]
    public class HapbeatAnimatorTriggerEditor : HapbeatTriggerBaseEditor
    {
        private SerializedProperty _targetAnimatorProp;
        private SerializedProperty _parameterNameProp;
        private SerializedProperty _conditionProp;
        private SerializedProperty _thresholdProp;

        protected override void OnEnable()
        {
            base.OnEnable();
            _targetAnimatorProp = serializedObject.FindProperty("_targetAnimator");
            _parameterNameProp = serializedObject.FindProperty("_parameterName");
            _conditionProp = serializedObject.FindProperty("_condition");
            _thresholdProp = serializedObject.FindProperty("_threshold");
        }

        public override void OnInspectorGUI()
        {
            // Draw the EventMap + entry dropdown from base
            serializedObject.Update();

            // EventMap and entry (from base)
            var eventMapProp = serializedObject.FindProperty("_eventMap");
            EditorGUILayout.PropertyField(eventMapProp);

            var eventMap = eventMapProp.objectReferenceValue as HapbeatEventMap;
            var entryIndexProp = serializedObject.FindProperty("_entryIndex");
            if (eventMap != null && eventMap.entries.Count > 0)
            {
                string[] names = eventMap.GetDisplayNames();
                int currentIndex = Mathf.Clamp(entryIndexProp.intValue, 0, names.Length - 1);
                int newIndex = EditorGUILayout.Popup("Event", currentIndex, names);
                entryIndexProp.intValue = newIndex;

                var entry = eventMap.GetEntry(newIndex);
                if (entry != null)
                {
                    EditorGUI.indentLevel++;
                    EditorGUI.BeginDisabledGroup(true);
                    EditorGUILayout.TextField("Event ID", entry.eventId);
                    EditorGUILayout.FloatField("Gain", entry.gain);
                    EditorGUI.EndDisabledGroup();
                    EditorGUI.indentLevel--;
                }
            }
            else
            {
                EditorGUILayout.HelpBox("Event Map を設定してください。", MessageType.Warning);
            }

            // Trigger enabled + cooldown
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_triggerEnabled"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("_cooldown"));

            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField("Animator Settings", EditorStyles.boldLabel);

            // Target Animator
            EditorGUILayout.PropertyField(_targetAnimatorProp);

            // Parameter name as dropdown if Animator is assigned
            var animator = _targetAnimatorProp.objectReferenceValue as Animator;
            if (animator != null)
            {
                var paramNames = GetAnimatorParameterNames(animator);
                if (paramNames.Count > 0)
                {
                    int currentIdx = paramNames.IndexOf(_parameterNameProp.stringValue);
                    if (currentIdx < 0) currentIdx = 0;

                    int newIdx = EditorGUILayout.Popup(
                        new GUIContent("Parameter", "Animator parameter to monitor"),
                        currentIdx, paramNames.ToArray());

                    _parameterNameProp.stringValue = paramNames[newIdx];
                }
                else
                {
                    EditorGUILayout.HelpBox("Animator にパラメータがありません。", MessageType.Warning);
                    EditorGUILayout.PropertyField(_parameterNameProp);
                }
            }
            else
            {
                // No animator assigned, fall back to text field
                EditorGUILayout.PropertyField(_parameterNameProp, new GUIContent("Parameter Name"));
                EditorGUILayout.HelpBox("Animator を設定するとパラメータがドロップダウンで選択できます。", MessageType.Info);
            }

            // Condition
            EditorGUILayout.PropertyField(_conditionProp);

            // Show threshold only for Int/Float conditions
            var condition = (HapbeatAnimatorTrigger.Condition)_conditionProp.enumValueIndex;
            bool needsThreshold = condition != HapbeatAnimatorTrigger.Condition.BoolBecameTrue
                               && condition != HapbeatAnimatorTrigger.Condition.BoolBecameFalse;
            if (needsThreshold)
            {
                EditorGUILayout.PropertyField(_thresholdProp);
            }

            // Diagnostics
            var verboseProp = serializedObject.FindProperty("_verboseLog");
            if (verboseProp != null)
            {
                EditorGUILayout.Space(5);
                EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
                EditorGUILayout.PropertyField(verboseProp);
            }

            serializedObject.ApplyModifiedProperties();
        }

        private List<string> GetAnimatorParameterNames(Animator animator)
        {
            var names = new List<string>();

            // Try to get from AnimatorController (editor only)
            var controller = animator.runtimeAnimatorController as AnimatorController;
            if (controller != null)
            {
                foreach (var param in controller.parameters)
                {
                    names.Add(param.name);
                }
            }
            else if (animator.parameterCount > 0)
            {
                // Fallback: read from the Animator directly (works in play mode)
                foreach (var param in animator.parameters)
                {
                    names.Add(param.name);
                }
            }

            return names;
        }
    }
}
#endif
