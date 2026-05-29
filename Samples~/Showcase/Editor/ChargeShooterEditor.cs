#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using Hapbeat;
using Hapbeat.Editor;

namespace Hapbeat.Samples.Showcase.Editor
{
    /// <summary>
    /// Custom Inspector for <see cref="ChargeShooter"/>.
    /// 4 つの haptic entry (loop / shot-light / shot-heavy / threshold) を EventMap から
    /// dropdown で選ばせる。SDK の <see cref="HapbeatTriggerBaseEditor.DrawEntryDropdown"/>
    /// を共有して、Trigger コンポーネントと同じ UX を提供する。
    /// </summary>
    [CustomEditor(typeof(ChargeShooter))]
    public class ChargeShooterEditor : UnityEditor.Editor
    {
        // Scene refs
        private SerializedProperty _muzzleProp;
        private SerializedProperty _projectilePrefabLightProp;
        private SerializedProperty _projectilePrefabHeavyProp;
        private SerializedProperty _chargeBarProp;
        private SerializedProperty _chargeBarFillProp;

        // EventMap + entry refs
        private SerializedProperty _eventMapProp;
        private SerializedProperty _loopIdProp, _loopIndexProp;
        private SerializedProperty _shotLightIdProp, _shotLightIndexProp;
        private SerializedProperty _shotHeavyIdProp, _shotHeavyIndexProp;
        private SerializedProperty _thresholdIdProp, _thresholdIndexProp;

        private void OnEnable()
        {
            _muzzleProp = serializedObject.FindProperty("_muzzle");
            _projectilePrefabLightProp = serializedObject.FindProperty("_projectilePrefabLight");
            _projectilePrefabHeavyProp = serializedObject.FindProperty("_projectilePrefabHeavy");
            _chargeBarProp = serializedObject.FindProperty("_chargeBar");
            _chargeBarFillProp = serializedObject.FindProperty("_chargeBarFill");

            _eventMapProp = serializedObject.FindProperty("_eventMap");
            _loopIdProp = serializedObject.FindProperty("_chargeLoopEntryId");
            _loopIndexProp = serializedObject.FindProperty("_chargeLoopEntryIndex");
            _shotLightIdProp = serializedObject.FindProperty("_chargeShotLightEntryId");
            _shotLightIndexProp = serializedObject.FindProperty("_chargeShotLightEntryIndex");
            _shotHeavyIdProp = serializedObject.FindProperty("_chargeShotHeavyEntryId");
            _shotHeavyIndexProp = serializedObject.FindProperty("_chargeShotHeavyEntryIndex");
            _thresholdIdProp = serializedObject.FindProperty("_chargeThresholdEntryId");
            _thresholdIndexProp = serializedObject.FindProperty("_chargeThresholdEntryIndex");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            // === Scene refs ===
            EditorGUILayout.LabelField("Scene refs", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_muzzleProp);
            EditorGUILayout.PropertyField(_projectilePrefabLightProp);
            EditorGUILayout.PropertyField(_projectilePrefabHeavyProp);
            EditorGUILayout.PropertyField(_chargeBarProp);
            EditorGUILayout.PropertyField(_chargeBarFillProp);

            EditorGUILayout.Space();

            // === Haptic (resolved via HapbeatEventMap) ===
            EditorGUILayout.LabelField("Haptic (resolved via HapbeatEventMap)", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_eventMapProp);

            var eventMap = _eventMapProp.objectReferenceValue as HapbeatEventMap;
            HapbeatTriggerBaseEditor.DrawEntryDropdown(eventMap, _loopIdProp,
                "Loop Entry",
                "チャージ中ループ再生する entry。Mode=StreamClip + Loop=ON 推奨。");
            HapbeatTriggerBaseEditor.DrawEntryDropdown(eventMap, _shotLightIdProp,
                "Shot Light Entry",
                "chargeT < threshold での発射 1-shot entry。Command/StreamClip どちらでも対応。");
            HapbeatTriggerBaseEditor.DrawEntryDropdown(eventMap, _shotHeavyIdProp,
                "Shot Heavy Entry",
                "chargeT >= threshold での発射 1-shot entry。未設定なら Light で代替。");
            HapbeatTriggerBaseEditor.DrawEntryDropdown(eventMap, _thresholdIdProp,
                "Threshold Entry",
                "閾値超え 1-shot entry。Command/StreamClip どちらでも対応。");

            EditorGUILayout.Space();

            // === 残りのフィールドはデフォルト描画 ===
            // (Charge modulator / tuning / debug 等。Scene refs と EventMap 系は除外)
            DrawPropertiesExcluding(serializedObject,
                "m_Script",
                "_muzzle", "_projectilePrefabLight", "_projectilePrefabHeavy",
                "_chargeBar", "_chargeBarFill",
                "_eventMap",
                "_chargeLoopEntryId", "_chargeLoopEntryIndex",
                "_chargeShotLightEntryId", "_chargeShotLightEntryIndex",
                "_chargeShotHeavyEntryId", "_chargeShotHeavyEntryIndex",
                "_chargeThresholdEntryId", "_chargeThresholdEntryIndex");

            serializedObject.ApplyModifiedProperties();
        }
    }
}
#endif
