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

            // ---- Source (always editable — scene-local, not a tuning value) ----
            EditorGUILayout.LabelField("Source", EditorStyles.boldLabel);

            // Property の表示: linked 時は preset 側の値を read-only 表示
            // (= runtime が実際に使う値)。Standalone 時は local field を編集可。
            BindingSourceProperty srcPropSelected;
            if (isLinked && preset != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.EnumPopup(
                        new GUIContent("Property (linked)",
                            "Linked preset の値。Runtime はこれを使う。編集は EventMap window で。"),
                        preset.sourceProperty);
                }
                srcPropSelected = preset.sourceProperty;
            }
            else
            {
                EditorGUILayout.PropertyField(serializedObject.FindProperty("_sourceProperty"),
                    new GUIContent("Property",
                        "読む値の種類。Transform 系 (LocalPosition*, Velocity* 等) は Source Transform を、" +
                        "SliderValue は Source Slider を、External は SetValue(float) push を使う。"));
                srcPropSelected = (BindingSourceProperty)serializedObject.FindProperty("_sourceProperty").enumValueIndex;
            }

            // Source Property の選択に応じて適切な source object field を出す
            switch (srcPropSelected)
            {
                case BindingSourceProperty.SliderValue:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_sourceSlider"),
                        new GUIContent("Source Slider",
                            "Slider.value (input) を読み取る対象。Slider component を持つ GameObject をドラッグ。"));
                    break;
                case BindingSourceProperty.External:
                    EditorGUILayout.HelpBox(
                        "External: 外部 script から SetValue(float) を呼ぶ。\n" +
                        "Inspector で wiring したい場合は、source の UnityEvent<float> " +
                        "(例: Slider.onValueChanged) を SetValue(dynamic float) にバインドする。",
                        MessageType.Info);
                    break;
                default:
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_sourceTransform"),
                        new GUIContent("Source Transform",
                            "値を読み取る Transform。Velocity* は同 GO の Rigidbody を内部参照。"));
                    if (srcPropSelected == BindingSourceProperty.VelocityMagnitude ||
                        srcPropSelected == BindingSourceProperty.AngularVelocityMagnitude)
                    {
                        var srcTransform = serializedObject.FindProperty("_sourceTransform").objectReferenceValue as Transform;
                        if (srcTransform != null && srcTransform.GetComponent<Rigidbody>() == null)
                            EditorGUILayout.HelpBox("Rigidbody not found on source Transform.", MessageType.Warning);
                    }
                    break;
            }

            // ---- Tuning fields ----
            // Linked: preset \u5024\u3092 read-only \u8868\u793a (runtime \u306f preset \u3092\u8aad\u3080\u305f\u3081\u3001local field \u306f\u610f\u5473\u306a\u3057)
            // Standalone: local SerializedFields \u3092\u7de8\u96c6\u53ef
            if (isLinked && preset != null)
            {
                using (new EditorGUI.DisabledScope(true))
                {
                    EditorGUILayout.FloatField("Input Min", preset.inputMin);
                    EditorGUILayout.FloatField("Input Max", preset.inputMax);

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Mapping (linked)", EditorStyles.boldLabel);
                    EditorGUILayout.EnumPopup("Curve", preset.curveType);
                    if (preset.curveType == BindingCurveType.Custom)
                        EditorGUILayout.CurveField("Custom Curve", preset.customCurve);

                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Output (linked)", EditorStyles.boldLabel);
                    EditorGUILayout.EnumPopup("Parameter", preset.outputParameter);
                    EditorGUILayout.FloatField("Output Min", preset.outputMin);
                    EditorGUILayout.FloatField("Output Max", preset.outputMax);
                }
            }
            else
            {
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
                    new GUIContent("Parameter",
                        "StreamGain: overall volume multiplier on the active StreamClip " +
                        "playback (0..2). Use for intensity modulation.\n" +
                        "StreamPan: stereo pan (-1..+1). Ignored for mono clips."));
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

            // ---- Live preview (Play mode + Edit mode 両対応) ----
            // Edit mode では Update() が走らないので EvaluateNow() を呼んで Current* を refresh する。
            // (EvaluateNow は副作用で CurrentInput/Normalized/Output を更新する)
            // これにより scene を編集しながら slider / transform 値の binding 出力が live で確認できる。
            {
                if (!Application.isPlaying)
                {
                    try { binding.EvaluateNow(); } catch { /* uninitialized state は無視 */ }
                }

                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField(Application.isPlaying ? "Live" : "Live (edit-mode preview)",
                    EditorStyles.boldLabel);

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
                    "below. EventMap \u306e preset \u3068 link \u3059\u308c\u3070\u5024\u304c EventMap \u306b\u96c6\u7d04\u3055\u308c\u307e\u3059\u3002",
                    MessageType.None);
                EditorGUILayout.PropertyField(linkedMapProp,
                    new GUIContent("Linked Event Map", "preset \u3092 link \u3059\u308b EventMap asset \u3092\u9078\u629e\u3002"));

                // Linked Event Map \u304c\u30bb\u30c3\u30c8\u3055\u308c\u305f\u3089\u3001preset \u3092\u9078\u3076 dropdown \u3092\u51fa\u3059
                var pickedMap = linkedMapProp.objectReferenceValue as HapbeatEventMap;
                if (pickedMap != null)
                {
                    DrawPresetPicker(pickedMap, linkedIdProp);
                }
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
                    $"Re-run 'Hapbeat/Open Batch Setup' to refresh the link, or click Unlink.",
                    MessageType.Warning);
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Open Event Map", GUILayout.Width(120)))
            {
                Selection.activeObject = map;
                EditorGUIUtility.PingObject(map);
                EditorApplication.ExecuteMenuItem("Hapbeat/Open Event Map");
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

        /// <summary>
        /// child Transform から親方向に walk して、EntryId == targetEntryId の
        /// HapbeatTriggerBase が乗っている GO の Transform を返す。
        /// 見つからなければ null (preset.sourceTransformPath はデフォルト "" で残る)。
        ///
        /// このメソッドは、binding component と同 GO の別 entry trigger
        /// (e.g. TickEmitter for slider_tick) を誤って trigger root と判断
        /// しないために存在する。
        /// </summary>
        private static Transform FindOwningTriggerRoot(Transform child, string targetEntryId)
        {
            if (child == null || string.IsNullOrEmpty(targetEntryId)) return null;
            var cursor = child;
            while (cursor != null)
            {
                var triggers = cursor.GetComponents<HapbeatTriggerBase>();
                foreach (var t in triggers)
                {
                    if (t == null) continue;
                    if (t.EntryId == targetEntryId)
                        return cursor;
                }
                cursor = cursor.parent;
            }
            return null;
        }

        /// <summary>
        /// child Transform から triggerRoot までの相対パスを "A/B/C" 形式で返す。
        /// child == triggerRoot なら ""。child が root の子孫でない場合は child.name の fallback。
        /// </summary>
        private static string ComputePathToTriggerRoot(Transform child, Transform triggerRoot)
        {
            if (child == null || triggerRoot == null) return "";
            if (child == triggerRoot) return "";
            var segments = new System.Collections.Generic.List<string>();
            var cursor = child;
            while (cursor != null && cursor != triggerRoot)
            {
                segments.Add(cursor.name);
                cursor = cursor.parent;
            }
            if (cursor != triggerRoot)
            {
                // root の子孫でなかった (= triggerRoot を経由せず scene 根まで行った)
                return child.name;
            }
            segments.Reverse();
            return string.Join("/", segments);
        }

        /// <summary>
        /// Standalone binding に対して「どの EventMap entry のどの preset に link するか」
        /// を選ばせる popup。flat な (entry / preset idx) のリストで選択 →
        /// _linkedBindingId をセット。preset が無ければ「new」で entry に作成。
        /// </summary>
        private void DrawPresetPicker(HapbeatEventMap map, SerializedProperty linkedIdProp)
        {
            // 全 preset を flat list 化
            var labels = new System.Collections.Generic.List<string>();
            var presetIds = new System.Collections.Generic.List<string>();
            var entryIndices = new System.Collections.Generic.List<int>();

            labels.Add("(select preset to link)");
            presetIds.Add("");
            entryIndices.Add(-1);

            for (int ei = 0; ei < map.entries.Count; ei++)
            {
                var entry = map.entries[ei];
                if (entry?.bindings == null) continue;
                string entryName = string.IsNullOrEmpty(entry.displayName) ? $"entry[{ei}]" : entry.displayName;
                for (int bi = 0; bi < entry.bindings.Count; bi++)
                {
                    var p = entry.bindings[bi];
                    if (p == null) continue;
                    string label = $"{entryName} / #{bi} {p.sourceProperty}→{p.outputParameter}";
                    labels.Add(label);
                    presetIds.Add(p.id);
                    entryIndices.Add(ei);
                }
                // 「new in this entry」も追加
                labels.Add($"{entryName} / + new preset");
                presetIds.Add($"NEW:{ei}");
                entryIndices.Add(ei);
            }

            int currentIdx = 0;
            // 既存 _linkedBindingId と一致する label を選択状態に
            string curId = linkedIdProp.stringValue;
            if (!string.IsNullOrEmpty(curId))
            {
                for (int i = 0; i < presetIds.Count; i++)
                    if (presetIds[i] == curId) { currentIdx = i; break; }
            }

            int newIdx = EditorGUILayout.Popup(
                new GUIContent("Link Preset",
                    "EventMap の entry / preset を選択して link 確立。'+ new preset' で空 preset を作成し link。"),
                currentIdx, labels.ToArray());

            if (newIdx != currentIdx && newIdx > 0)
            {
                string chosen = presetIds[newIdx];
                if (chosen.StartsWith("NEW:"))
                {
                    // 新規 preset 作成
                    int ei = entryIndices[newIdx];
                    if (ei >= 0 && ei < map.entries.Count)
                    {
                        var entry = map.entries[ei];
                        Undo.RecordObject(map, "Create Binding Preset");
                        var newPreset = new HapbeatBindingPreset();
                        // component の現値を preset 初期値に反映
                        var src = serializedObject.FindProperty("_sourceProperty");
                        var op = serializedObject.FindProperty("_outputParameter");
                        if (src != null) newPreset.sourceProperty = (BindingSourceProperty)src.enumValueIndex;
                        if (op != null) newPreset.outputParameter = (BindingOutputParameter)op.enumValueIndex;

                        // sourceTransformPath を「component の GO」基準に設定。
                        // 注意: 単純な GetComponentInParent<HapbeatTriggerBase>() だと
                        // 同 GO 上の別 entry を発火する trigger (e.g. Slider GO の
                        // TickEmitter) を拾ってしまうので、**選択 entry と同じ EntryId**
                        // を持つ trigger を上方向に探索する。
                        var bindingComp = (HapbeatParameterBinding)target;
                        Transform triggerRoot = FindOwningTriggerRoot(bindingComp.transform, entry.id);
                        if (triggerRoot != null)
                        {
                            newPreset.sourceTransformPath = ComputePathToTriggerRoot(
                                bindingComp.transform, triggerRoot);
                        }

                        // SliderValue 時は Slider の min/max を Input Range に
                        // 自動コピーすると、フル range を意図通り mapping できる
                        // (デフォルト 0..1 のままだと slider -1..1 の左半分がクランプされる)。
                        if (newPreset.sourceProperty == BindingSourceProperty.SliderValue)
                        {
                            var sl = bindingComp.GetComponent<UnityEngine.UI.Slider>();
                            if (sl != null)
                            {
                                newPreset.inputMin = sl.minValue;
                                newPreset.inputMax = sl.maxValue;
                            }
                        }

                        // Pan output のデフォルト range は -1..1 が自然 (gain は 0..1 のままで OK)
                        if (newPreset.outputParameter == BindingOutputParameter.StreamPan)
                        {
                            newPreset.outputMin = -1f;
                            newPreset.outputMax = 1f;
                        }

                        entry.bindings.Add(newPreset);
                        EditorUtility.SetDirty(map);
                        AssetDatabase.SaveAssetIfDirty(map);
                        linkedIdProp.stringValue = newPreset.id;
                        Debug.Log($"[Hapbeat] 新規 preset を entry '{entry.displayName}' に作成し link 確立 " +
                                  $"(sourceTransformPath='{newPreset.sourceTransformPath}')。");
                    }
                }
                else
                {
                    linkedIdProp.stringValue = chosen;
                }
            }
        }
    }
}
#endif
