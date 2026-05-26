#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hapbeat.Samples.Tutorial.EditorTools
{
    /// <summary>
    /// DoorController の custom inspector。
    /// Bindings リストを table 形式 (各 row = 1 input key → 1 trigger + 任意数の deferred bool)
    /// で描画する。gate は持たない (Animator transition condition で代用)。
    /// </summary>
    [CustomEditor(typeof(DoorController))]
    public class DoorControllerEditor : UnityEditor.Editor
    {
        private SerializedProperty _bindingsProp;
        private SerializedProperty _isClosedParamProp;
        private SerializedProperty _openStateNameProp;
        private SerializedProperty _inputAcceptingStatesProp;
        private SerializedProperty _verboseLogProp;

        private void OnEnable()
        {
            _bindingsProp = serializedObject.FindProperty("_bindings");
            _isClosedParamProp = serializedObject.FindProperty("_isClosedParameter");
            _openStateNameProp = serializedObject.FindProperty("_openStateName");
            _inputAcceptingStatesProp = serializedObject.FindProperty("_inputAcceptingStates");
            _verboseLogProp = serializedObject.FindProperty("_verboseLog");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var ctrl = (DoorController)target;
            var animator = ctrl != null ? ctrl.GetComponent<Animator>() : null;
            var controller = animator != null
                ? animator.runtimeAnimatorController as AnimatorController
                : null;

            // Always include "" (none) at index 0 so dropdown can clear the field
            var boolOptions = new List<string> { "" };
            var triggerOptions = new List<string> { "" };
            var stateOptions = new List<string> { "" };
            if (controller != null)
            {
                foreach (var p in controller.parameters)
                {
                    if (p.type == AnimatorControllerParameterType.Bool) boolOptions.Add(p.name);
                    else if (p.type == AnimatorControllerParameterType.Trigger) triggerOptions.Add(p.name);
                }
                foreach (var layer in controller.layers)
                    CollectStateNames(layer.stateMachine, stateOptions);
            }

            // Binding dropdown では IsClosed (state mirror で auto-managed) を選択肢から外す。
            // ユーザーが誤って binding の deferred bool に IsClosed を指定して mirror と競合する事故を防止。
            var boolOptionsForBindings = new List<string>(boolOptions);
            string isClosedParamName = _isClosedParamProp.stringValue;
            if (!string.IsNullOrEmpty(isClosedParamName))
                boolOptionsForBindings.Remove(isClosedParamName);

            DrawBindingsSection(boolOptionsForBindings, triggerOptions, controller != null);

            EditorGUILayout.Space(10);
            DrawInputFilterSection(stateOptions);

            EditorGUILayout.Space(10);
            DrawStateMirrorSection(boolOptions, stateOptions);

            EditorGUILayout.Space(10);
            DrawDiagnosticsSection();

            serializedObject.ApplyModifiedProperties();
        }

        // ---------------------------------------------------------------
        //  Sections
        // ---------------------------------------------------------------

        private void DrawBindingsSection(List<string> boolOpts, List<string> triggerOpts, bool controllerOk)
        {
            EditorGUILayout.LabelField("Input → Animator Bindings", EditorStyles.boldLabel);
            if (!controllerOk)
            {
                EditorGUILayout.HelpBox(
                    "Animator or AnimatorController not resolved — parameter は手入力です。" +
                    "Animator + Controller を設定すれば dropdown 化されます。",
                    MessageType.Info);
            }
            EditorGUILayout.HelpBox(
                "各 row: input key 押下時に Animator trigger を発火し、必要なら 1 frame 後に bool を更新する。" +
                "分岐条件 (gate) は Animator transition condition 側で表現する (二重設定を避けるため)。",
                MessageType.None);

            for (int i = 0; i < _bindingsProp.arraySize; i++)
            {
                if (DrawBindingRow(i, boolOpts, triggerOpts))
                {
                    i--; // row was deleted; arraySize already decremented
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Binding", GUILayout.MaxWidth(120)))
            {
                int newIdx = _bindingsProp.arraySize;
                _bindingsProp.arraySize++;
                var elem = _bindingsProp.GetArrayElementAtIndex(newIdx);
                ResetBindingToBlank(elem);
            }
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Populate Z2 Defaults", GUILayout.MaxWidth(180)))
            {
                Undo.RecordObject(target, "Populate Z2 Defaults");
                ((DoorController)target).EditorPopulateZ2Defaults();
                EditorUtility.SetDirty(target);
                serializedObject.Update();
            }
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>Returns true if the row was deleted.</summary>
        private bool DrawBindingRow(int index, List<string> boolOpts, List<string> triggerOpts)
        {
            var elem = _bindingsProp.GetArrayElementAtIndex(index);
            var labelProp = elem.FindPropertyRelative("label");
            var keyProp = elem.FindPropertyRelative("key");
            var triggerProp = elem.FindPropertyRelative("triggerParameter");
            var deferredListProp = elem.FindPropertyRelative("deferredBools");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Row header: # / label / key / remove
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField($"#{index + 1}", GUILayout.MaxWidth(28));
            labelProp.stringValue = EditorGUILayout.TextField(labelProp.stringValue, GUILayout.MinWidth(120));
            EditorGUILayout.LabelField("Key", GUILayout.MaxWidth(28));
            EditorGUILayout.PropertyField(keyProp, GUIContent.none, GUILayout.MinWidth(80));
            if (GUILayout.Button("✕", GUILayout.MaxWidth(28)))
            {
                _bindingsProp.DeleteArrayElementAtIndex(index);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.EndVertical();
                return true;
            }
            EditorGUILayout.EndHorizontal();

            // Trigger
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Set Trigger", GUILayout.MaxWidth(110));
            DrawDropdownInline(triggerProp, triggerOpts);
            EditorGUILayout.EndHorizontal();

            // Deferred bools sublist
            EditorGUILayout.LabelField("Deferred bool updates (+1 frame)", EditorStyles.miniBoldLabel);
            for (int j = 0; j < deferredListProp.arraySize; j++)
            {
                if (DrawDeferredBoolRow(deferredListProp, j, boolOpts))
                {
                    j--; // deleted
                }
            }
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            if (GUILayout.Button("+ Add deferred bool", GUILayout.MaxWidth(150)))
            {
                int n = deferredListProp.arraySize;
                deferredListProp.arraySize++;
                var d = deferredListProp.GetArrayElementAtIndex(n);
                d.FindPropertyRelative("parameter").stringValue = "";
                d.FindPropertyRelative("mode").enumValueIndex = (int)DeferredBoolMode.Toggle;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            return false;
        }

        /// <summary>Returns true if deleted.</summary>
        private bool DrawDeferredBoolRow(SerializedProperty list, int j, List<string> boolOpts)
        {
            var d = list.GetArrayElementAtIndex(j);
            var paramProp = d.FindPropertyRelative("parameter");
            var modeProp = d.FindPropertyRelative("mode");

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(20);
            EditorGUILayout.LabelField($"#{j + 1}", GUILayout.MaxWidth(28));
            DrawDropdownInline(paramProp, boolOpts);
            EditorGUILayout.LabelField("→", GUILayout.MaxWidth(16));
            EditorGUILayout.PropertyField(modeProp, GUIContent.none, GUILayout.MaxWidth(80));
            if (GUILayout.Button("✕", GUILayout.MaxWidth(24)))
            {
                list.DeleteArrayElementAtIndex(j);
                EditorGUILayout.EndHorizontal();
                return true;
            }
            EditorGUILayout.EndHorizontal();
            return false;
        }

        private void DrawInputFilterSection(List<string> stateOpts)
        {
            EditorGUILayout.LabelField("Input Filtering", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "現在の Animator state がリストに含まれる時のみ binding を評価する。" +
                "リスト空なら filter 無効 (全 state で受付)。\n" +
                "transient state (Slam/LockEngage/LockRelease/LockedRattle) を含めなければ、" +
                "アニメーション中の input が無視され queue 残留もしない。",
                MessageType.None);

            // List<string> を inline dropdown で描画
            for (int i = 0; i < _inputAcceptingStatesProp.arraySize; i++)
            {
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(4);
                EditorGUILayout.LabelField($"#{i + 1}", GUILayout.MaxWidth(28));
                var elem = _inputAcceptingStatesProp.GetArrayElementAtIndex(i);
                DrawDropdownInline(elem, stateOpts);
                if (GUILayout.Button("✕", GUILayout.MaxWidth(24)))
                {
                    _inputAcceptingStatesProp.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndHorizontal();
                    i--;
                    continue;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add accepted state", GUILayout.MaxWidth(180)))
            {
                int newIdx = _inputAcceptingStatesProp.arraySize;
                _inputAcceptingStatesProp.arraySize++;
                _inputAcceptingStatesProp.GetArrayElementAtIndex(newIdx).stringValue = "";
            }
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStateMirrorSection(List<string> boolOpts, List<string> stateOpts)
        {
            EditorGUILayout.LabelField("State Mirror (auto-managed)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "LateUpdate で Animator current state を見て IsClosed を自動同期する。script は IsClosed を直接 toggle しない。",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Is Closed Param (Bool)", GUILayout.MaxWidth(200));
            DrawDropdownInline(_isClosedParamProp, boolOpts);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Open State Name", GUILayout.MaxWidth(200));
            DrawDropdownInline(_openStateNameProp, stateOpts);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawDiagnosticsSection()
        {
            EditorGUILayout.LabelField("Diagnostics", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_verboseLogProp);
        }

        // ---------------------------------------------------------------
        //  Helpers
        // ---------------------------------------------------------------

        private static void ResetBindingToBlank(SerializedProperty elem)
        {
            elem.FindPropertyRelative("label").stringValue = "(new)";
            elem.FindPropertyRelative("key").enumValueIndex = 0; // Key.None
            elem.FindPropertyRelative("triggerParameter").stringValue = "";
            var list = elem.FindPropertyRelative("deferredBools");
            list.arraySize = 0;
        }

        private static void DrawDropdownInline(SerializedProperty stringProp, List<string> options)
        {
            string current = stringProp.stringValue;
            var working = new List<string>(options);
            int currentIdx = working.IndexOf(current);
            bool prependMissing = currentIdx < 0;
            if (prependMissing)
            {
                working.Insert(0, current);
                currentIdx = 0;
            }

            var display = new string[working.Count];
            for (int i = 0; i < working.Count; i++)
            {
                string s = working[i];
                if (prependMissing && i == 0)
                    display[i] = $"<missing: {s}>";
                else if (string.IsNullOrEmpty(s))
                    display[i] = "<none>";
                else
                    display[i] = s;
            }

            int newIdx = EditorGUILayout.Popup(currentIdx, display);
            if (newIdx >= 0 && newIdx < working.Count && working[newIdx] != current)
                stringProp.stringValue = working[newIdx];
        }

        private static void CollectStateNames(AnimatorStateMachine sm, List<string> outList)
        {
            if (sm == null) return;
            foreach (var s in sm.states)
            {
                if (s.state != null && !string.IsNullOrEmpty(s.state.name) && !outList.Contains(s.state.name))
                    outList.Add(s.state.name);
            }
            foreach (var child in sm.stateMachines)
                CollectStateNames(child.stateMachine, outList);
        }
    }
}
#endif
