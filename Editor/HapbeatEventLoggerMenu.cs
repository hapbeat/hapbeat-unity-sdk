#if UNITY_EDITOR
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Menu commands for attaching and auto-wiring a <see cref="HapbeatEventLogger"/>
    /// to any UnityEvent-bearing components on the selected GameObject.
    ///
    /// Uses pure reflection (no XRI namespace references) so this works for any
    /// component that exposes UnityEvents — not just XRI Interactables.
    /// </summary>
    internal static class HapbeatEventLoggerMenu
    {
        // Top-level (flat). 旧 "Hapbeat/Debug/" submenu は 2026-05-26 に廃止。
        private const string kAttachMenu = "Hapbeat/Attach Event Logger to Selected";
        private const string kRemoveMenu = "Hapbeat/Remove Event Logger Wiring from Selected";

        [MenuItem(kAttachMenu, false, 53)]
        private static void AttachAndWire()
        {
            var go = Selection.activeGameObject;
            if (go == null)
            {
                EditorUtility.DisplayDialog("Hapbeat Event Logger",
                    "No GameObject is selected. Select an object with UnityEvents " +
                    "(e.g. an XRI Interactable) before running this command.", "OK");
                return;
            }

            var logger = go.GetComponent<HapbeatEventLogger>();
            if (logger == null)
                logger = Undo.AddComponent<HapbeatEventLogger>(go);

            int wired = 0;
            int skipped = 0;
            var report = new StringBuilder();

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null) continue;
                if (comp is HapbeatEventLogger) continue;

                var events = FindUnityEvents(comp);
                foreach (var (name, evt) in events)
                {
                    if (IsLoggerAlreadyWired(evt, logger, name))
                    {
                        skipped++;
                        continue;
                    }

                    // AddStringPersistentListener works for any UnityEventBase — it stores
                    // the string argument and invokes LogEvent(stringArg) when the event
                    // fires, ignoring whatever payload the event itself carries.
                    Undo.RecordObject(comp, "Wire Hapbeat Event Logger");
                    UnityEventTools.AddStringPersistentListener(evt, logger.LogEvent, name);
                    EditorUtility.SetDirty(comp);
                    wired++;

                    report.AppendLine($"\u2022 {comp.GetType().Name}.{name}");
                }
            }

            if (wired == 0)
            {
                EditorUtility.DisplayDialog("Hapbeat Event Logger",
                    $"No UnityEvents found to wire on '{go.name}'.\n\n" +
                    $"Add an XRI Interactable (or any component exposing UnityEvents) first.\n\n" +
                    $"Already-wired events: {skipped}", "OK");
                return;
            }

            EditorUtility.DisplayDialog("Hapbeat Event Logger",
                $"Wired {wired} UnityEvent(s) on '{go.name}' to HapbeatEventLogger.LogEvent.\n" +
                $"Skipped {skipped} already-wired event(s).\n\n" +
                $"Events wired:\n{report}\n" +
                $"Enter Play mode to see the firing order in the Console.", "OK");
        }

        [MenuItem(kRemoveMenu, false, 54)]
        private static void RemoveWiring()
        {
            var go = Selection.activeGameObject;
            if (go == null) return;

            var logger = go.GetComponent<HapbeatEventLogger>();
            if (logger == null)
            {
                EditorUtility.DisplayDialog("Hapbeat Event Logger",
                    "No HapbeatEventLogger on the selected GameObject.", "OK");
                return;
            }

            int removed = 0;
            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is HapbeatEventLogger) continue;

                foreach (var (name, evt) in FindUnityEvents(comp))
                {
                    // Walk the persistent call list and drop any that target our logger.
                    for (int i = evt.GetPersistentEventCount() - 1; i >= 0; i--)
                    {
                        if (evt.GetPersistentTarget(i) == logger &&
                            evt.GetPersistentMethodName(i) == nameof(HapbeatEventLogger.LogEvent))
                        {
                            Undo.RecordObject(comp, "Remove Hapbeat Event Logger Wiring");
                            UnityEventTools.RemovePersistentListener(evt, i);
                            EditorUtility.SetDirty(comp);
                            removed++;
                        }
                    }
                }
            }

            EditorUtility.DisplayDialog("Hapbeat Event Logger",
                $"Removed {removed} wired listener(s) from '{go.name}'.", "OK");
        }

        /// <summary>
        /// Returns every field of <paramref name="component"/> whose value is a
        /// <see cref="UnityEventBase"/> (i.e. UnityEvent, UnityEvent&lt;T&gt;, or any subclass).
        /// </summary>
        private static IEnumerable<(string name, UnityEventBase evt)> FindUnityEvents(Component component)
        {
            var type = component.GetType();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            // Walk the inheritance chain — UnityEvent fields often live on base classes
            // (e.g. XRBaseInteractable defines hoverEntered, subclasses only add behavior).
            while (type != null && type != typeof(object))
            {
                foreach (var field in type.GetFields(flags | BindingFlags.DeclaredOnly))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(field.FieldType)) continue;

                    var value = field.GetValue(component) as UnityEventBase;
                    if (value == null) continue;

                    // Use the clean field name. For XRI these are things like
                    // "hoverEntered", "firstHoverEntered", "selectEntered", etc.
                    yield return (CleanFieldName(field.Name), value);
                }
                type = type.BaseType;
            }

            // Also check properties that return UnityEventBase (rare, but safe).
            foreach (var prop in component.GetType().GetProperties(flags))
            {
                if (!typeof(UnityEventBase).IsAssignableFrom(prop.PropertyType)) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                UnityEventBase value;
                try { value = prop.GetValue(component) as UnityEventBase; }
                catch { continue; }
                if (value == null) continue;
                yield return (prop.Name, value);
            }
        }

        /// <summary>Strip leading underscores and common "m_" prefix from a field name.</summary>
        private static string CleanFieldName(string fieldName)
        {
            if (string.IsNullOrEmpty(fieldName)) return fieldName;
            string s = fieldName;
            if (s.StartsWith("m_") && s.Length > 2) s = s.Substring(2);
            while (s.StartsWith("_") && s.Length > 1) s = s.Substring(1);
            // Lower-case the first character for UnityEvent fields that ended up PascalCase.
            if (char.IsUpper(s[0])) s = char.ToLowerInvariant(s[0]) + s.Substring(1);
            return s;
        }

        private static bool IsLoggerAlreadyWired(UnityEventBase evt, HapbeatEventLogger logger, string tag)
        {
            for (int i = 0; i < evt.GetPersistentEventCount(); i++)
            {
                if (evt.GetPersistentTarget(i) == logger &&
                    evt.GetPersistentMethodName(i) == nameof(HapbeatEventLogger.LogEvent))
                {
                    return true;
                }
            }
            return false;
        }
    }
}
#endif
