#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Captures a JSON snapshot of every <see cref="HapbeatEventMap"/> that has
    /// <c>revertPlayModeChanges=true</c> when Play mode begins, and restores the
    /// snapshot on Play exit. Provides non-destructive tuning — designers can tweak
    /// preset values live during Play without persisting mistakes.
    ///
    /// Snapshots are kept per-asset in a <see cref="Dictionary{TKey,TValue}"/> keyed
    /// by the asset's <see cref="UnityEngine.Object.GetInstanceID"/>. They survive
    /// a single Play-to-Edit cycle; a fresh Play press takes a new snapshot.
    /// </summary>
    [InitializeOnLoad]
    internal static class HapbeatEventMapPlaySnapshot
    {
        private struct Snapshot
        {
            public string json;
            public DateTime takenAt;
        }

        private static readonly Dictionary<int, Snapshot> _snapshots = new Dictionary<int, Snapshot>();

        static HapbeatEventMapPlaySnapshot()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.ExitingEditMode:
                    // Snapshot just before entering Play so we capture the last-saved
                    // disk state (before any Play-mode edits happen).
                    SnapshotAllOptedIn();
                    break;

                case PlayModeStateChange.EnteredEditMode:
                    // Play has finished — restore for any map that had the toggle set.
                    RestoreAllOptedIn();
                    break;
            }
        }

        /// <summary>
        /// Snapshot every loaded HapbeatEventMap whose <c>revertPlayModeChanges</c>
        /// toggle is on.
        /// </summary>
        private static void SnapshotAllOptedIn()
        {
            string[] guids = AssetDatabase.FindAssets("t:HapbeatEventMap");
            int count = 0;
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
                if (map == null) continue;
                if (!map.revertPlayModeChanges) continue;
                TakeSnapshot(map);
                count++;
            }
            if (count > 0)
                Debug.Log($"[Hapbeat] Snapshotted {count} EventMap(s) for Play-mode revert.");
        }

        /// <summary>
        /// Restore every EventMap whose snapshot exists AND whose toggle is still on.
        /// Toggling the flag off during Play keeps the edits.
        /// </summary>
        private static void RestoreAllOptedIn()
        {
            var toRestore = new List<HapbeatEventMap>();
            foreach (var kv in _snapshots)
            {
                var map = EditorUtility.InstanceIDToObject(kv.Key) as HapbeatEventMap;
                if (map == null) continue;
                if (!map.revertPlayModeChanges) continue;
                toRestore.Add(map);
            }

            int count = 0;
            foreach (var map in toRestore)
            {
                if (RestoreSnapshot(map))
                    count++;
            }
            if (count > 0)
                Debug.Log($"[Hapbeat] Reverted {count} EventMap(s) to pre-Play snapshot.");
        }

        // ----------------- public API for the editor UI -----------------

        public static void ManualSnapshot(HapbeatEventMap map)
        {
            if (map == null) return;
            TakeSnapshot(map);
            Debug.Log($"[Hapbeat] Snapshot taken for '{map.name}'.");
        }

        public static bool HasSnapshot(HapbeatEventMap map)
        {
            if (map == null) return false;
            return _snapshots.ContainsKey(map.GetInstanceID());
        }

        public static DateTime GetSnapshotTime(HapbeatEventMap map)
        {
            if (map == null) return default;
            return _snapshots.TryGetValue(map.GetInstanceID(), out var s) ? s.takenAt : default;
        }

        /// <summary>
        /// Restore the snapshot for <paramref name="map"/>. Returns true if restored.
        /// </summary>
        public static bool RestoreSnapshot(HapbeatEventMap map)
        {
            if (map == null) return false;
            if (!_snapshots.TryGetValue(map.GetInstanceID(), out var snap)) return false;
            if (string.IsNullOrEmpty(snap.json)) return false;

            Undo.RecordObject(map, "Restore EventMap Snapshot");
            JsonUtility.FromJsonOverwrite(snap.json, map);
            EditorUtility.SetDirty(map);
            return true;
        }

        private static void TakeSnapshot(HapbeatEventMap map)
        {
            _snapshots[map.GetInstanceID()] = new Snapshot
            {
                json = JsonUtility.ToJson(map),
                takenAt = DateTime.Now,
            };
        }
    }
}
#endif
