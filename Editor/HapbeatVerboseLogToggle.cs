#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// One-shot menu command: turn off every <c>_verboseLog</c> / <c>_debugLog</c>
    /// SerializeField on Hapbeat-related components across the loaded scenes
    /// and the project's AnimatorController state behaviours.
    ///
    /// <para>
    /// Use after a debugging session to silence the Console without hunting
    /// each component individually. Affects components in the
    /// <c>Hapbeat.*</c> namespace (framework + samples) — non-Hapbeat user
    /// scripts that happen to expose a <c>_verboseLog</c> field are left alone.
    /// </para>
    ///
    /// <para>
    /// Coverage:
    /// <list type="bullet">
    ///   <item>All scene <see cref="MonoBehaviour"/> components in Hapbeat namespaces
    ///         (Trigger / Bridge / ParameterBinding / Manager / sample scripts).</item>
    ///   <item>All <see cref="HapbeatStateBehaviour"/> instances on every
    ///         AnimatorController asset in the project (walks layers + nested
    ///         state machines so Z2-style door wires get flipped too).</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class HapbeatVerboseLogToggle
    {
        // Bool SerializeField names recognised as "verbose logging" switches.
        // Add to this list when a new Hapbeat component adopts a different
        // name. Keep it short — too broad a match risks toggling unrelated
        // diagnostics flags on user scripts.
        private static readonly string[] kKnownFlags = { "_verboseLog", "_debugLog" };

        [MenuItem("Hapbeat/Disable Verbose Log on All Hapbeat Components", false, 61)]
        public static void DisableVerboseLogAll()
        {
            int scnCount = DisableInLoadedScenes();
            int ctrlCount = DisableInAnimatorControllers();

            Debug.Log(
                $"[Hapbeat] Verbose log disabled on {scnCount + ctrlCount} component(s): " +
                $"{scnCount} scene MonoBehaviour, {ctrlCount} AnimatorController state behaviour. " +
                "Re-enable individually via each component's Inspector if needed.");
        }

        // ── Scene scan ─────────────────────────────────────────────────────

        private static int DisableInLoadedScenes()
        {
            int touched = 0;
            var allBehaviours = Object.FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var mb in allBehaviours)
            {
                if (mb == null) continue;
                if (!IsHapbeatTargetType(mb.GetType())) continue;

                var so = new SerializedObject(mb);
                bool changed = false;
                foreach (var name in kKnownFlags)
                    changed |= TryClearBool(so, name);
                if (changed)
                {
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(mb);
                    touched++;
                }
            }
            return touched;
        }

        // ── AnimatorController state-machine behaviour scan ────────────────

        private static int DisableInAnimatorControllers()
        {
            int touched = 0;
            string[] guids = AssetDatabase.FindAssets("t:AnimatorController");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
                if (ctrl == null) continue;
                bool ctrlChanged = false;
                foreach (var layer in ctrl.layers)
                {
                    if (layer?.stateMachine == null) continue;
                    ctrlChanged |= WalkStateMachine(layer.stateMachine, ref touched);
                }
                if (ctrlChanged)
                    EditorUtility.SetDirty(ctrl);
            }
            return touched;
        }

        private static bool WalkStateMachine(AnimatorStateMachine sm, ref int touched)
        {
            bool any = false;
            foreach (var stateRef in sm.states)
            {
                var state = stateRef.state;
                if (state == null) continue;
                foreach (var behaviour in state.behaviours)
                {
                    if (behaviour == null) continue;
                    if (!IsHapbeatTargetType(behaviour.GetType())) continue;
                    var so = new SerializedObject(behaviour);
                    bool changed = false;
                    foreach (var name in kKnownFlags)
                        changed |= TryClearBool(so, name);
                    if (changed)
                    {
                        so.ApplyModifiedProperties();
                        EditorUtility.SetDirty(behaviour);
                        touched++;
                        any = true;
                    }
                }
            }
            foreach (var sub in sm.stateMachines)
            {
                if (sub.stateMachine != null)
                    any |= WalkStateMachine(sub.stateMachine, ref touched);
            }
            return any;
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        /// <summary>
        /// True for any type living in a <c>Hapbeat.*</c> namespace, except
        /// editor-only / internal infrastructure. Samples (Showcase, Tutorial)
        /// ARE included — they're exactly the kind of scripts a user wants
        /// quieted after a debugging session.
        /// </summary>
        private static bool IsHapbeatTargetType(System.Type t)
        {
            var ns = t?.Namespace;
            if (string.IsNullOrEmpty(ns)) return false;
            if (ns == "Hapbeat.Editor" || ns == "Hapbeat.Internal") return false;
            return ns == "Hapbeat" || ns.StartsWith("Hapbeat.");
        }

        private static bool TryClearBool(SerializedObject so, string fieldName)
        {
            var prop = so.FindProperty(fieldName);
            if (prop == null || prop.propertyType != SerializedPropertyType.Boolean) return false;
            if (!prop.boolValue) return false;
            prop.boolValue = false;
            return true;
        }
    }
}
#endif
