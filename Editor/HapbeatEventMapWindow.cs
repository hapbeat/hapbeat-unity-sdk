#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using static Hapbeat.Editor.HapbeatLocalization;

namespace Hapbeat.Editor
{
    /// <summary>
    /// Editor window showing all event map entries and their scene trigger bindings.
    /// Provides a centralized dashboard of "what triggers what" and "where is it attached".
    /// Window > Hapbeat > Event Map
    /// </summary>
    public class HapbeatEventMapWindow : EditorWindow
    {
        private HapbeatEventMap _selectedMap;
        private Vector2 _scrollPosition;
        private Vector2 _detailScrollPos;
        private int _selectedEntryIndex = -1;

        // Split ratio between list (left) and detail (right) panels
        private const string kSplitRatioKey = "HapbeatEventMap_SplitRatio";
        private float _splitRatio = 0.42f;
        private bool _isDraggingSplit;
        private const float SplitterWidth = 4f;

        // Clipboard for copy/paste
        private static HapbeatEventEntry _clipboardEntry;

        // View mode: List (detail editor on the right) or Table (spreadsheet bulk edit).
        private enum ViewMode { List, Table }
        private ViewMode _viewMode = ViewMode.List;
        private const string kViewModeKey = "HapbeatEventMap_ViewMode";

        // Drag-to-reorder state (used by both List and Table views).
        // _dragStartIndex >= 0 while the user is potentially dragging a row.
        // _dragConfirmed is set once the mouse moves past the movement threshold —
        // before that the mousedown is treated as a plain click-to-select.
        private int _dragStartIndex = -1;
        private Vector2 _dragStartPos;
        private bool _dragConfirmed;
        private int _dropSlotIndex = -1; // 0..entries.Count, slot to insert BEFORE
        private readonly List<Rect> _rowRects = new List<Rect>(); // cached per repaint
        // Larger threshold than Unity's built-in 4-5px: with 4px, accidental mouse
        // jitter during a plain click would trigger a row reorder, silently
        // shuffling entries. 10px is well past typical click-jitter while still
        // feeling responsive for an intentional drag.
        private const float DragThresholdPx = 10f;

        // Multi-selection in Table mode (Ctrl-click / Shift-click).
        private readonly HashSet<int> _tableMultiSelected = new HashSet<int>();
        private int _tableLastClickedIndex = -1; // for Shift-click range selection

        /// <summary>
        /// Display labels for <see cref="HapticMode"/>. Order MUST match the enum
        /// declaration (Command, StreamClip) so the popup index round-trips
        /// cleanly.
        /// </summary>
        private static readonly string[] s_ModeLabels =
        {
            "FIRE (Command)",
            "CLIP (Stream Clip)",
        };

        // Cached scene scan results
        private Dictionary<int, List<TriggerInfo>> _triggersByEntry = new Dictionary<int, List<TriggerInfo>>();
        private List<TriggerInfo> _orphanedTriggers = new List<TriggerInfo>();

        private struct TriggerInfo
        {
            public HapbeatTriggerBase trigger;
            public string gameObjectName;
            public string typeName;
            public List<string> wiredEvents; // e.g. "XRGrabInteractable.selectEntered"
        }

        [MenuItem("Hapbeat/Event Map")]
        [MenuItem("Window/Hapbeat/Event Map")]
        public static void ShowWindow()
        {
            var window = GetWindow<HapbeatEventMapWindow>("Hapbeat Event Map");
            window.minSize = new Vector2(500, 300);
        }

        [MenuItem("Hapbeat/Create Event Router", false, 50)]
        [MenuItem("GameObject/Hapbeat/Event Router", false, 10)]
        public static void CreateEventRouter()
        {
            // Check if one already exists
            var existing = FindObjectsByType<HapbeatManager>(FindObjectsSortMode.None);
            GameObject router = new GameObject("[Hapbeat Event Router]");
            Undo.RegisterCreatedObjectUndo(router, "Create Hapbeat Event Router");

            // Add HapbeatManager if not in scene
            if (existing.Length == 0)
                router.AddComponent<HapbeatManager>();

            Selection.activeGameObject = router;
            Debug.Log("[Hapbeat] Event Router を作成しました。ここに AnimatorTrigger / UnityEventTrigger を追加してください。");
        }

        private void OnEnable()
        {
            FindEventMap();
            _splitRatio = EditorPrefs.GetFloat(kSplitRatioKey, 0.42f);
            _splitRatio = Mathf.Clamp(_splitRatio, 0.2f, 0.8f);
            _viewMode = (ViewMode)EditorPrefs.GetInt(kViewModeKey, (int)ViewMode.List);
            RefreshIntensityCache();
            EnsureEntryIdsAssigned();
        }

        /// <summary>
        /// Proactively lazy-assign stable ids to every entry in the currently-selected
        /// EventMap so subsequent resolutions go through the id path even before any
        /// trigger edit. Persists the assignment to disk so it survives domain reload.
        /// </summary>
        private void EnsureEntryIdsAssigned()
        {
            if (_selectedMap == null) return;
            bool anyAssigned = false;
            foreach (var entry in _selectedMap.entries)
            {
                if (entry == null) continue;
                if (!entry.HasId)
                {
                    _ = entry.id; // triggers lazy-assign
                    anyAssigned = true;
                }
            }
            if (anyAssigned)
            {
                EditorUtility.SetDirty(_selectedMap);
                AssetDatabase.SaveAssetIfDirty(_selectedMap);
            }
        }

        /// <summary>
        /// Force-save the currently-selected EventMap to disk. Use after every
        /// mutation (add / delete / reorder / edit) so the change survives a
        /// subsequent script recompile / domain reload — SetDirty alone does
        /// not guarantee the asset is written before the reload wipes memory state.
        /// </summary>
        private void PersistEventMap()
        {
            if (_selectedMap == null) return;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
        }

        private void OnGUI()
        {
            DrawToolbar();
            EditorGUILayout.Space(3);

            if (_selectedMap == null)
            {
                EditorGUILayout.HelpBox(
                    Tr("No Event Map found.\nCreate one via Assets > Create > Hapbeat > Event Map.",
                       "Event Map が見つかりません。\nAssets > Create > Hapbeat > Event Map で作成してください。"),
                    MessageType.Info);
                return;
            }

            // Keyboard navigation
            HandleKeyboard();

            // NOTE: drag reorder input handling (MouseDrag/MouseUp) is called from inside
            // the scroll view (at the end of DrawEntryTable / DrawEntryTableGrid), because
            // the row-rect cache captures coordinates in scroll-view-local space. Mouse
            // events must be hit-tested in the same space — calling it out here would
            // leave the drop indicator one scroll-view-origin off (this was the source of
            // the pre-fix "line shows one row below cursor" bug).

            if (_viewMode == ViewMode.Table)
            {
                // Full-width spreadsheet. No detail panel — designed for bulk editing
                // and reordering many entries at once.
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
                DrawEntryTableGrid();
                EditorGUILayout.EndScrollView();
                return;
            }

            float leftWidth = position.width * _splitRatio;

            EditorGUILayout.BeginHorizontal();

            // Left panel
            EditorGUILayout.BeginVertical(GUILayout.Width(leftWidth));
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            DrawEntryTable();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            // Splitter bar
            DrawSplitter();

            // Right panel
            EditorGUILayout.BeginVertical();
            _detailScrollPos = EditorGUILayout.BeginScrollView(_detailScrollPos);
            DrawSelectedEntryDetail();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        private void DrawSplitter()
        {
            var rect = GUILayoutUtility.GetRect(SplitterWidth, SplitterWidth,
                GUILayout.ExpandHeight(true), GUILayout.Width(SplitterWidth));

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.25f));

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);

            var e = Event.current;
            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                _isDraggingSplit = true;
                e.Use();
            }
            else if (e.type == EventType.MouseDrag && _isDraggingSplit)
            {
                _splitRatio = Mathf.Clamp(e.mousePosition.x / position.width, 0.2f, 0.8f);
                Repaint();
                e.Use();
            }
            else if (e.type == EventType.MouseUp && _isDraggingSplit)
            {
                _isDraggingSplit = false;
                EditorPrefs.SetFloat(kSplitRatioKey, _splitRatio);
                e.Use();
            }
        }

        private void HandleKeyboard()
        {
            if (_selectedMap == null || _selectedMap.entries.Count == 0) return;
            var e = Event.current;
            if (e.type != EventType.KeyDown) return;

            if (e.keyCode == KeyCode.UpArrow)
            {
                _selectedEntryIndex = Mathf.Max(0, _selectedEntryIndex - 1);
                GUIUtility.keyboardControl = 0;
                e.Use();
                Repaint();
            }
            else if (e.keyCode == KeyCode.DownArrow)
            {
                _selectedEntryIndex = Mathf.Min(_selectedMap.entries.Count - 1, _selectedEntryIndex + 1);
                GUIUtility.keyboardControl = 0;
                e.Use();
                Repaint();
            }
            else if (e.keyCode == KeyCode.Escape && _dragStartIndex >= 0)
            {
                CancelDrag();
                e.Use();
                Repaint();
            }
        }

        /// <summary>
        /// Process MouseDrag / MouseUp for an in-flight drag. MUST be called from
        /// inside the same scroll view where row rects were captured, because the
        /// rect cache lives in scroll-view-local coordinates.
        /// Row-level MouseDown (the drag-start registration) is handled inside
        /// <see cref="DrawEntryTable"/> and <see cref="DrawEntryTableGrid"/>.
        /// </summary>
        private void HandleDragReorderInsideScroll()
        {
            if (_selectedMap == null) return;
            if (_dragStartIndex < 0) return;

            var e = Event.current;

            if (e.type == EventType.MouseDrag)
            {
                // Promote to confirmed drag once movement exceeds threshold.
                if (!_dragConfirmed)
                {
                    if (Vector2.Distance(_dragStartPos, e.mousePosition) > DragThresholdPx)
                        _dragConfirmed = true;
                }
                if (_dragConfirmed)
                {
                    _dropSlotIndex = ComputeDropSlot(e.mousePosition);
                    e.Use();
                    Repaint();
                }
            }
            else if (e.type == EventType.MouseUp)
            {
                if (_dragConfirmed && _dropSlotIndex >= 0)
                    ApplyReorder(_dragStartIndex, _dropSlotIndex);

                _dragStartIndex = -1;
                _dragConfirmed = false;
                _dropSlotIndex = -1;
                e.Use();
                Repaint();
            }
        }


        private void CancelDrag()
        {
            _dragStartIndex = -1;
            _dragConfirmed = false;
            _dropSlotIndex = -1;
        }

        /// <summary>
        /// Given the current mouse position, return the slot index (0..entries.Count)
        /// where the dragged row would be inserted if released now. A slot "k" means
        /// "insert immediately before row k" (slot == count = append to the end).
        /// Returns -1 when the mouse is far outside the list area.
        /// </summary>
        private int ComputeDropSlot(Vector2 mousePos)
        {
            if (_rowRects.Count == 0) return -1;

            // Above the list → slot 0.
            if (mousePos.y < _rowRects[0].yMin) return 0;

            for (int i = 0; i < _rowRects.Count; i++)
            {
                var r = _rowRects[i];
                if (mousePos.y >= r.yMin && mousePos.y < r.yMax)
                {
                    // Upper half → before this row; lower half → after.
                    float mid = r.yMin + r.height * 0.5f;
                    return mousePos.y < mid ? i : i + 1;
                }
            }
            // Below the list → append.
            return _rowRects.Count;
        }

        /// <summary>
        /// Draw the blue drop line showing where the dragged row will land on release.
        /// </summary>
        private void DrawDropSlotIndicator()
        {
            if (_rowRects.Count == 0) return;
            Rect line;
            if (_dropSlotIndex <= 0)
            {
                var r0 = _rowRects[0];
                line = new Rect(r0.x, r0.yMin - 1, r0.width, 2);
            }
            else if (_dropSlotIndex >= _rowRects.Count)
            {
                var rN = _rowRects[_rowRects.Count - 1];
                line = new Rect(rN.x, rN.yMax - 1, rN.width, 2);
            }
            else
            {
                var r = _rowRects[_dropSlotIndex];
                line = new Rect(r.x, r.yMin - 1, r.width, 2);
            }
            EditorGUI.DrawRect(line, new Color(0.3f, 0.7f, 1f, 1f));
        }

        /// <summary>
        /// Move entry at index <paramref name="from"/> to insert position
        /// <paramref name="toSlot"/> (0..entries.Count). No-op if the target is
        /// the same logical position. Selection follows the moved item.
        ///
        /// All scene triggers bound to this map also have their stored entry
        /// indices remapped so wirings survive the reorder. Scene scan includes
        /// INACTIVE GameObjects so triggers on disabled branches aren't silently
        /// left pointing at the wrong entry.
        ///
        /// All sub-operations (map edit + per-trigger remap) are collapsed into
        /// a single Undo step so Ctrl+Z reverts everything at once.
        ///
        /// <para><b>Known limitation</b>: triggers that live only in prefab assets
        /// (not instantiated in any open scene) are not reached by this remap —
        /// see the warning logged when that case is suspected. Re-author those
        /// triggers manually or load the prefab into a scene before reordering.
        /// </para>
        /// </summary>
        private void ApplyReorder(int from, int toSlot)
        {
            if (_selectedMap == null) return;
            if (from < 0 || from >= _selectedMap.entries.Count) return;
            if (toSlot < 0 || toSlot > _selectedMap.entries.Count) return;

            // Same-position drops (before self or immediately after self) are no-ops.
            if (toSlot == from || toSlot == from + 1) return;

            int count = _selectedMap.entries.Count;
            int newIndex = (toSlot > from) ? toSlot - 1 : toSlot;

            // Build an oldIndex → newIndex map so we can rewrite scene triggers.
            // Meaning: a trigger whose stored _entryIndex == i should be updated
            // to map[i] after the reorder to still point at the same entry object.
            var map = new int[count];
            for (int i = 0; i < count; i++)
            {
                if (i == from) { map[i] = newIndex; continue; }
                int adjusted = i;
                if (i > from) adjusted -= 1;      // removing 'from' shifts above down
                if (adjusted >= newIndex) adjusted += 1; // inserting at newIndex shifts at/after up
                map[i] = adjusted;
            }

            // Group every sub-Undo step under one label so the user only has to
            // press Ctrl+Z once to revert the entire reorder + remap.
            Undo.SetCurrentGroupName("Reorder Hapbeat Event Entries");
            int undoGroup = Undo.GetCurrentGroup();

            Undo.RecordObject(_selectedMap, "Reorder Hapbeat Event Entries");
            var item = _selectedMap.entries[from];
            _selectedMap.entries.RemoveAt(from);
            _selectedMap.entries.Insert(newIndex, item);

            _selectedEntryIndex = newIndex;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);

            // Remap all scene triggers that reference this map so their bindings
            // continue to point at the same logical entry after the reorder.
            int remappedCount = RemapTriggerEntryIndices(map);

            Undo.CollapseUndoOperations(undoGroup);

            // Trigger→entry bindings are keyed by index, so the scene view
            // needs to be rescanned to re-associate them.
            ScanScene();

            // Always log reorders (not just when triggers were touched) so an
            // accidental drag leaves an audit trail the user can spot + Ctrl-Z.
            var entryLabel = string.IsNullOrEmpty(item.displayName) ? item.GetSummary() : item.displayName;
            Debug.Log($"[Hapbeat] Reordered entry '{entryLabel}' (was #{from}, now #{newIndex})" +
                      (remappedCount > 0
                          ? $"; remapped {remappedCount} scene trigger reference(s). Press Ctrl+Z to undo."
                          : ". Press Ctrl+Z to undo."));
        }

        /// <summary>
        /// Walk every <see cref="HapbeatTriggerBase"/> in open scenes (including
        /// those on INACTIVE GameObjects, so disabled branches stay in sync) and
        /// rewrite their serialized <c>_entryIndex</c> (plus SequenceTrigger's
        /// on-start / on-stop indices) using the supplied old→new index map.
        ///
        /// Returns the number of individual property writes performed.
        /// </summary>
        /// <summary>
        /// Sync every scene trigger's <c>_entryIndex</c> display cache from its
        /// stable id. Use after structural mutations that invalidate list
        /// positions (insert / delete) but have no meaningful oldToNew map.
        /// Triggers without a stable id are left alone (legacy — unsafe to guess).
        /// Returns the number of triggers whose cache was updated.
        /// </summary>
        private int SyncTriggerIndexCaches()
        {
            if (_selectedMap == null) return 0;

            var triggers = FindObjectsByType<HapbeatTriggerBase>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int totalWrites = 0;

            foreach (var trig in triggers)
            {
                if (trig == null || trig.EventMap != _selectedMap) continue;

                var so = new SerializedObject(trig);
                int writesHere = 0;

                writesHere += SyncIndexFromIdOnly(so, "_entryId", "_entryIndex") ? 1 : 0;
                writesHere += SyncIndexFromIdOnly(so, "_onStartEntryId", "_onStartEntryIndex") ? 1 : 0;
                writesHere += SyncIndexFromIdOnly(so, "_onStopEntryId", "_onStopEntryIndex") ? 1 : 0;

                if (writesHere > 0)
                {
                    Undo.RecordObject(trig, "Sync Hapbeat Trigger Index Cache");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(trig);
                    totalWrites += writesHere;
                }
            }
            return totalWrites;
        }

        private bool SyncIndexFromIdOnly(SerializedObject so, string idPropName, string indexPropName)
        {
            var idProp = so.FindProperty(idPropName);
            var indexProp = so.FindProperty(indexPropName);
            if (idProp == null || indexProp == null) return false;
            if (string.IsNullOrEmpty(idProp.stringValue)) return false;
            int newIdx = _selectedMap.IndexOfId(idProp.stringValue);
            if (newIdx < 0 || newIdx == indexProp.intValue) return false;
            indexProp.intValue = newIdx;
            return true;
        }

        private int RemapTriggerEntryIndices(int[] oldToNew)
        {
            // FindObjectsInactive.Include is critical: triggers on disabled
            // GameObjects are otherwise invisible to the editor's default scene
            // scan, and would drift silently after a reorder.
            var triggers = FindObjectsByType<HapbeatTriggerBase>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            int totalWrites = 0;

            foreach (var trig in triggers)
            {
                if (trig == null || trig.EventMap != _selectedMap) continue;

                var so = new SerializedObject(trig);
                int writesHere = 0;

                // For each (idProp, indexProp) pair: if the trigger has a stable
                // id, recompute the index cache from the map (authoritative path).
                // Otherwise fall back to oldToNew remap for legacy triggers.
                writesHere += SyncIndexCacheFromId(so, "_entryId", "_entryIndex", oldToNew) ? 1 : 0;
                writesHere += SyncIndexCacheFromId(so, "_onStartEntryId", "_onStartEntryIndex", oldToNew) ? 1 : 0;
                writesHere += SyncIndexCacheFromId(so, "_onStopEntryId", "_onStopEntryIndex", oldToNew) ? 1 : 0;

                if (writesHere > 0)
                {
                    Undo.RecordObject(trig, "Remap Hapbeat Trigger Index");
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(trig);
                    totalWrites += writesHere;
                }
            }
            return totalWrites;
        }

        /// <summary>
        /// Sync a trigger's index cache after an EventMap reorder. When the id
        /// field is populated, recompute the index from the map. When it's
        /// empty (legacy), fall back to the oldToNew remap.
        /// </summary>
        private bool SyncIndexCacheFromId(SerializedObject so,
            string idPropName, string indexPropName, int[] oldToNew)
        {
            var idProp = so.FindProperty(idPropName);
            var indexProp = so.FindProperty(indexPropName);
            if (indexProp == null) return false;

            if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
            {
                int newIdx = _selectedMap.IndexOfId(idProp.stringValue);
                if (newIdx >= 0 && newIdx != indexProp.intValue)
                {
                    indexProp.intValue = newIdx;
                    return true;
                }
                return false;
            }

            return RemapIntProperty(indexProp, oldToNew);
        }

        private static bool RemapIntProperty(SerializedProperty prop, int[] oldToNew)
        {
            if (prop == null) return false;
            int cur = prop.intValue;
            if (cur < 0 || cur >= oldToNew.Length) return false; // -1 ("none") stays as-is
            int mapped = oldToNew[cur];
            if (mapped == cur) return false;
            prop.intValue = mapped;
            return true;
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);

            // Explicit width calculation based on window width, since GUILayout's
            // MinWidth/MaxWidth/ExpandWidth/FlexibleSpace interaction with the
            // EditorStyles.toolbar style doesn't reliably shrink ObjectField when
            // the window is narrow (the IMGUI competition logic keeps ObjectField
            // pinned to its preferred width). By computing widths ourselves, the
            // toolbar remains fully visible even at ~380px window widths.
            float winW = position.width;
            // Fixed right-side area: Batch(80) + Scan(80) + ↻(24) + List(42) + Table(48) + gaps(~14) + margin
            const float rightFixedW = 288f;
            // When _selectedMap is null, the ↻ button doesn't render — shave off its 24px.
            float rightFixed = _selectedMap == null ? rightFixedW - 24f : rightFixedW;
            // Add trailing +/- (24+24) and endcap spacing
            rightFixed += 52f;

            float leftRoom = Mathf.Max(80f, winW - rightFixed);
            float labelW = Mathf.Clamp(leftRoom * 0.25f, 35f, 70f);
            float objW = Mathf.Clamp(leftRoom - labelW - 4f, 60f, 240f);

            EditorGUILayout.LabelField("Event Map:", GUILayout.Width(labelW));
            var newMap = (HapbeatEventMap)EditorGUILayout.ObjectField(
                _selectedMap, typeof(HapbeatEventMap), false, GUILayout.Width(objW));
            if (newMap != _selectedMap)
            {
                _selectedMap = newMap;
                ScanScene();
                RefreshIntensityCache();
            }

            GUILayout.FlexibleSpace();

            if (GUILayout.Button("Batch Setup", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                HapbeatBatchSetupWindow.ShowWindow();
            }

            if (GUILayout.Button("Scan Scene", EditorStyles.toolbarButton, GUILayout.Width(80)))
            {
                ScanScene();
            }

            // Refresh the cached manifest intensity values on every entry in the map.
            // The cache is also refreshed automatically when the entries list changes
            // (see RefreshIntensityCache), but manual refresh is useful after Studio
            // re-deploys a Kit without any Unity-side change.
            if (_selectedMap != null && GUILayout.Button(
                new GUIContent("\u21bb", "Refresh manifest intensity cache for all entries in this map."),
                EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                HapbeatManifestIntensity.Invalidate();
                RefreshIntensityCache();
                ShowNotification(new GUIContent("Intensity cache refreshed"));
            }

            // View mode toggle (List | Table)
            using (new EditorGUI.DisabledScope(_selectedMap == null))
            {
                bool listSel = _viewMode == ViewMode.List;
                bool tableSel = _viewMode == ViewMode.Table;
                // Use miniButtonLeft/Right for a segmented look.
                var prevCol = GUI.backgroundColor;
                if (listSel) GUI.backgroundColor = new Color(0.5f, 0.75f, 1f);
                if (GUILayout.Button("List", EditorStyles.miniButtonLeft, GUILayout.Width(42)))
                    SetViewMode(ViewMode.List);
                GUI.backgroundColor = prevCol;
                if (tableSel) GUI.backgroundColor = new Color(0.5f, 0.75f, 1f);
                if (GUILayout.Button("Table", EditorStyles.miniButtonRight, GUILayout.Width(48)))
                    SetViewMode(ViewMode.Table);
                GUI.backgroundColor = prevCol;
            }

            if (_selectedMap != null && GUILayout.Button("+", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                Undo.RecordObject(_selectedMap, "Add Hapbeat Event Entry");
                // Inherit the last entry's mode so batches of same-mode entries
                // can be added in a row without re-setting the popup each time.
                HapticMode inheritedMode = GetInheritedMode();
                _selectedMap.entries.Add(new HapbeatEventEntry
                {
                    mode = inheritedMode,
                    displayName = "",
                    category = "",
                    eventName = ""
                });
                _selectedEntryIndex = _selectedMap.entries.Count - 1;
                EditorUtility.SetDirty(_selectedMap);
                AssetDatabase.SaveAssetIfDirty(_selectedMap);
            }

            bool canDelete = _selectedMap != null && _selectedEntryIndex >= 0
                && _selectedEntryIndex < _selectedMap.entries.Count;
            EditorGUI.BeginDisabledGroup(!canDelete);
            if (GUILayout.Button("\u2212", EditorStyles.toolbarButton, GUILayout.Width(24)))
            {
                Undo.RecordObject(_selectedMap, "Remove Hapbeat Event Entry");
                _selectedMap.entries.RemoveAt(_selectedEntryIndex);
                _selectedEntryIndex = Mathf.Min(_selectedEntryIndex, _selectedMap.entries.Count - 1);
                EditorUtility.SetDirty(_selectedMap);
                AssetDatabase.SaveAssetIfDirty(_selectedMap);
                ScanScene();
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.EndHorizontal();
        }

        private static readonly Color SelectedBg = new Color(0.24f, 0.48f, 0.90f, 0.6f);
        private static readonly Color SelectedText = Color.white;
        private static readonly Color MultiSelectBg = new Color(0.24f, 0.48f, 0.90f, 0.30f);
        private static readonly Color HandleBg = new Color(0f, 0f, 0f, 0.12f);
        private static readonly Color ZebraBg = new Color(1f, 1f, 1f, 0.04f);

        // Table-mode column widths (excluding Name, which flexes to fill).
        private const float ColHandleW = 20f;
        private const float ColNumW = 28f;
        private const float ColModeW = 70f;
        private const float ColIdClipW = 180f;
        private const float ColGainW = 48f;
        private const float ColTargetW = 110f;
        private const float ColDeleteW = 20f;
        private const float ColGap = 2f;

        /// <summary>
        /// Spreadsheet-style view for bulk editing. Columns: [≡ drag handle] [#]
        /// [Mode ▾] [Name] [Event / Clip] [Gain] [Target] [✕].
        /// Multi-select via Ctrl-click (toggle) or Shift-click (range). Right-click
        /// on a selection to apply batch operations (e.g. set mode, set gain).
        /// </summary>
        private void DrawEntryTableGrid()
        {
            if (_selectedMap == null) return;

            var so = new SerializedObject(_selectedMap);
            var entriesProp = so.FindProperty("entries");

            // Header row
            DrawTableHeader();

            // Rebuild row rects every repaint for drag hit-testing.
            if (Event.current.type == EventType.Repaint)
                _rowRects.Clear();

            float rowH = EditorGUIUtility.singleLineHeight + 4;

            for (int i = 0; i < entriesProp.arraySize; i++)
            {
                var entryProp = entriesProp.GetArrayElementAtIndex(i);
                var entry = _selectedMap.entries[i];
                var rowRect = GUILayoutUtility.GetRect(0, rowH, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                    _rowRects.Add(rowRect);

                bool isSelected = _selectedEntryIndex == i || _tableMultiSelected.Contains(i);

                // Row background
                if (Event.current.type == EventType.Repaint)
                {
                    if (_selectedEntryIndex == i)
                        EditorGUI.DrawRect(rowRect, SelectedBg);
                    else if (_tableMultiSelected.Contains(i))
                        EditorGUI.DrawRect(rowRect, MultiSelectBg);
                    else if ((i & 1) == 1)
                        EditorGUI.DrawRect(rowRect, ZebraBg);
                }

                // Lay out columns
                float x = rowRect.x + 2;
                float y = rowRect.y + 2;
                float h = rowRect.height - 4;
                float totalW = rowRect.width - 4;

                // Handle column
                var handleRect = new Rect(x, y, ColHandleW, h);
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(handleRect, HandleBg);
                var handleStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                };
                GUI.Label(handleRect, "\u2630", handleStyle); // ☰
                EditorGUIUtility.AddCursorRect(handleRect, MouseCursor.Pan);
                x += ColHandleW + ColGap;

                // # index
                var numRect = new Rect(x, y, ColNumW, h);
                GUI.Label(numRect, i.ToString(), EditorStyles.centeredGreyMiniLabel);
                x += ColNumW + ColGap;

                // --- Cell editing ---
                // If this row is part of a multi-selection, edits to its cells are
                // propagated to ALL selected rows (spreadsheet-style). For single
                // selection, edits apply only to this row.
                bool propagate = _tableMultiSelected.Count > 1 && _tableMultiSelected.Contains(i);

                // Mode popup
                var modeRect = new Rect(x, y, ColModeW, h);
                var modeProp = entryProp.FindPropertyRelative("mode");
                int oldMode = modeProp.enumValueIndex;
                int newModeIdx = EditorGUI.Popup(modeRect, oldMode,
                    new[] { "FIRE", "CLIP", "LIVE" });
                if (newModeIdx != oldMode)
                    WriteToSelected(entriesProp, i, propagate,
                        p => p.FindPropertyRelative("mode").enumValueIndex = newModeIdx);
                x += ColModeW + ColGap;

                // Width allocation for flexible columns (Name, Event/Clip)
                float usedRight = ColGainW + ColGap + ColTargetW + ColGap + ColDeleteW;
                float flexArea = totalW - (ColHandleW + ColGap + ColNumW + ColGap + ColModeW + ColGap) - usedRight;
                float nameW = Mathf.Max(100, flexArea - ColIdClipW - ColGap);
                float idClipW = ColIdClipW;
                if (flexArea < nameW + ColGap + idClipW)
                    idClipW = Mathf.Max(80, flexArea - nameW - ColGap);

                // Name field
                var nameRect = new Rect(x, y, nameW, h);
                var nameProp = entryProp.FindPropertyRelative("displayName");
                string oldName = nameProp.stringValue;
                string newName = EditorGUI.TextField(nameRect, oldName);
                if (newName != oldName)
                    WriteToSelected(entriesProp, i, propagate,
                        p => p.FindPropertyRelative("displayName").stringValue = newName);
                x += nameW + ColGap;

                // Event / Clip cell (mode-dependent)
                var idClipRect = new Rect(x, y, idClipW, h);
                DrawTableIdClipCell(idClipRect, entriesProp, i, entry, propagate);
                x += idClipW + ColGap;

                // Gain field
                var gainRect = new Rect(x, y, ColGainW, h);
                var gainProp = entryProp.FindPropertyRelative("gain");
                float oldGain = gainProp.floatValue;
                float newGain = EditorGUI.FloatField(gainRect, oldGain);
                if (!Mathf.Approximately(newGain, oldGain))
                    WriteToSelected(entriesProp, i, propagate,
                        p => p.FindPropertyRelative("gain").floatValue = newGain);
                x += ColGainW + ColGap;

                // Target (read-only summary — click row + go to List mode for full editor)
                var targetRect = new Rect(x, y, ColTargetW, h);
                var targetProp = entryProp.FindPropertyRelative("target");
                string targetText = string.IsNullOrEmpty(targetProp.stringValue) ? "all" : targetProp.stringValue;
                GUI.Label(targetRect, targetText, EditorStyles.miniLabel);
                x += ColTargetW + ColGap;

                // Delete button
                var delRect = new Rect(x, y, ColDeleteW, h);
                if (GUI.Button(delRect, "\u00d7", EditorStyles.miniButton))
                {
                    int idx = i;
                    EditorApplication.delayCall += () => DeleteEntry(idx);
                    GUI.FocusControl(null);
                }

                // --- Input handling ---
                HandleTableRowInput(i, rowRect, handleRect);
            }

            so.ApplyModifiedProperties();

            // Handle MouseDrag/MouseUp in the same coordinate space as _rowRects.
            HandleDragReorderInsideScroll();

            // Drop indicator during drag
            if (_dragConfirmed && _dropSlotIndex >= 0 && Event.current.type == EventType.Repaint)
                DrawDropSlotIndicator();

            // Empty-state + batch toolbar
            if (entriesProp.arraySize == 0)
            {
                EditorGUILayout.LabelField("(empty \u2014 click + to add)", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            EditorGUILayout.Space(4);
            DrawTableBatchToolbar();
        }

        private void DrawTableHeader()
        {
            var rect = GUILayoutUtility.GetRect(0, EditorGUIUtility.singleLineHeight + 2,
                GUILayout.ExpandWidth(true));

            if (Event.current.type == EventType.Repaint)
                EditorGUI.DrawRect(rect, new Color(0f, 0f, 0f, 0.10f));

            var style = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleLeft,
            };
            var cstyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                alignment = TextAnchor.MiddleCenter,
            };

            float x = rect.x + 2;
            float y = rect.y;
            float h = rect.height;
            float totalW = rect.width - 4;

            GUI.Label(new Rect(x, y, ColHandleW, h), "", style);
            x += ColHandleW + ColGap;
            GUI.Label(new Rect(x, y, ColNumW, h), "#", cstyle);
            x += ColNumW + ColGap;
            GUI.Label(new Rect(x, y, ColModeW, h), "Mode", style);
            x += ColModeW + ColGap;

            float usedRight = ColGainW + ColGap + ColTargetW + ColGap + ColDeleteW;
            float flexArea = totalW - (ColHandleW + ColGap + ColNumW + ColGap + ColModeW + ColGap) - usedRight;
            float nameW = Mathf.Max(100, flexArea - ColIdClipW - ColGap);
            float idClipW = ColIdClipW;
            if (flexArea < nameW + ColGap + idClipW)
                idClipW = Mathf.Max(80, flexArea - nameW - ColGap);

            GUI.Label(new Rect(x, y, nameW, h), "Name", style);
            x += nameW + ColGap;
            GUI.Label(new Rect(x, y, idClipW, h), "Event ID / Clip", style);
            x += idClipW + ColGap;
            GUI.Label(new Rect(x, y, ColGainW, h), "Gain", cstyle);
            x += ColGainW + ColGap;
            GUI.Label(new Rect(x, y, ColTargetW, h), "Target", style);
        }

        private void DrawTableIdClipCell(
            Rect rect,
            SerializedProperty entriesProp,
            int rowIndex,
            HapbeatEventEntry entry,
            bool propagate)
        {
            var entryProp = entriesProp.GetArrayElementAtIndex(rowIndex);
            switch (entry.mode)
            {
                case HapticMode.Command:
                    {
                        // Show "category.name" as a single inline field; round-trip through split.
                        var catProp = entryProp.FindPropertyRelative("category");
                        var nameProp = entryProp.FindPropertyRelative("eventName");
                        string composed = BuildEventId(catProp.stringValue, nameProp.stringValue);
                        string edited = EditorGUI.TextField(rect, composed);
                        if (edited != composed)
                        {
                            SplitEventId(edited, out string c, out string n);
                            WriteToSelected(entriesProp, rowIndex, propagate, p =>
                            {
                                p.FindPropertyRelative("category").stringValue = c;
                                p.FindPropertyRelative("eventName").stringValue = n;
                            });
                        }
                        break;
                    }
                case HapticMode.StreamClip:
                    {
                        var clipProp = entryProp.FindPropertyRelative("streamClip");
                        var old = clipProp.objectReferenceValue;
                        var picked = EditorGUI.ObjectField(rect, old, typeof(AudioClip), false);
                        if (picked != old)
                            WriteToSelected(entriesProp, rowIndex, propagate,
                                p => p.FindPropertyRelative("streamClip").objectReferenceValue = picked);
                        break;
                    }
            }
        }

        /// <summary>
        /// Apply <paramref name="apply"/> to the anchor row and, if <paramref name="propagate"/>
        /// is true, to every other row in <see cref="_tableMultiSelected"/>. Anchor change
        /// is always applied so single-row edits work normally.
        /// </summary>
        private void WriteToSelected(
            SerializedProperty entriesProp,
            int anchorIndex,
            bool propagate,
            Action<SerializedProperty> apply)
        {
            if (entriesProp == null || apply == null) return;
            if (anchorIndex < 0 || anchorIndex >= entriesProp.arraySize) return;

            // Always write the anchor row
            apply(entriesProp.GetArrayElementAtIndex(anchorIndex));

            if (!propagate) return;

            foreach (int k in _tableMultiSelected)
            {
                if (k == anchorIndex) continue;
                if (k < 0 || k >= entriesProp.arraySize) continue;
                apply(entriesProp.GetArrayElementAtIndex(k));
            }
        }

        /// <summary>
        /// Handle click / ctrl-click / shift-click selection + drag-to-reorder initiation
        /// + right-click context menu for a table row.
        /// </summary>
        private void HandleTableRowInput(int i, Rect rowRect, Rect handleRect)
        {
            var e = Event.current;

            // Drag only starts from the ☰ handle column (so editing cells doesn't trigger drags).
            if (e.type == EventType.MouseDown && e.button == 0 && handleRect.Contains(e.mousePosition))
            {
                _selectedEntryIndex = i;
                _dragStartIndex = i;
                _dragStartPos = e.mousePosition;
                _dragConfirmed = false;
                _dropSlotIndex = -1;
                GUIUtility.keyboardControl = 0;
                e.Use();
                Repaint();
                return;
            }

            // Click on row (outside any editable widget we consumed already) selects.
            // Use the handle-excluded area to avoid stealing focus from widgets.
            if (e.type == EventType.MouseDown && e.button == 0 && rowRect.Contains(e.mousePosition))
            {
                // The other cells consume their own events before reaching here, so a
                // MouseDown arriving at the row level means the user clicked on row
                // chrome (background, # column, target column, etc.) — treat as select.
                if (e.control || e.command)
                {
                    // Ctrl-click toggles multi-selection
                    if (_tableMultiSelected.Contains(i)) _tableMultiSelected.Remove(i);
                    else _tableMultiSelected.Add(i);
                    _selectedEntryIndex = i;
                }
                else if (e.shift && _tableLastClickedIndex >= 0)
                {
                    // Shift-click extends range from last-clicked to i.
                    int lo = Mathf.Min(_tableLastClickedIndex, i);
                    int hi = Mathf.Max(_tableLastClickedIndex, i);
                    _tableMultiSelected.Clear();
                    for (int k = lo; k <= hi; k++) _tableMultiSelected.Add(k);
                    _selectedEntryIndex = i;
                }
                else
                {
                    _tableMultiSelected.Clear();
                    _selectedEntryIndex = i;
                }
                _tableLastClickedIndex = i;
                GUIUtility.keyboardControl = 0;
                e.Use();
                Repaint();
            }

            // Right-click: context menu (single-row ops + batch ops if a multi-selection exists)
            if (e.type == EventType.ContextClick && rowRect.Contains(e.mousePosition))
            {
                if (!_tableMultiSelected.Contains(i))
                {
                    _tableMultiSelected.Clear();
                    _selectedEntryIndex = i;
                }
                ShowTableContextMenu(i);
                e.Use();
            }
        }

        private void ShowTableContextMenu(int anchorIndex)
        {
            var menu = new GenericMenu();
            var selected = GetSelectedRowIndicesUnion(anchorIndex);

            if (selected.Count <= 1)
            {
                menu.AddItem(new GUIContent("Copy Entry Values"), false, () => CopyEntry(anchorIndex));
                if (_clipboardEntry != null)
                    menu.AddItem(new GUIContent("Paste Entry Values"), false, () => PasteEntry(anchorIndex));
                else
                    menu.AddDisabledItem(new GUIContent("Paste Entry Values"));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Add Entry Above"), false, () => InsertEntry(anchorIndex));
                menu.AddItem(new GUIContent("Add Entry Below"), false, () => InsertEntry(anchorIndex + 1));
                menu.AddItem(new GUIContent("Duplicate Entry"), false, () => DuplicateEntry(anchorIndex));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Delete Entry"), false, () => DeleteEntry(anchorIndex));
            }
            else
            {
                menu.AddDisabledItem(new GUIContent($"{selected.Count} entries selected"));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Set Mode/FIRE (Command)"), false, () => BatchSetMode(selected, HapticMode.Command));
                menu.AddItem(new GUIContent("Set Mode/CLIP (Stream Clip)"), false, () => BatchSetMode(selected, HapticMode.StreamClip));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Set Gain.../0.5"), false, () => BatchSetGain(selected, 0.5f));
                menu.AddItem(new GUIContent("Set Gain.../1.0"), false, () => BatchSetGain(selected, 1.0f));
                menu.AddItem(new GUIContent("Set Gain.../2.0"), false, () => BatchSetGain(selected, 2.0f));
                menu.AddSeparator("");
                menu.AddItem(new GUIContent("Duplicate All"), false, () => BatchDuplicate(selected));
                menu.AddItem(new GUIContent("Delete All"), false, () => BatchDelete(selected));
            }

            menu.ShowAsContext();
        }

        private List<int> GetSelectedRowIndicesUnion(int anchorIndex)
        {
            var set = new HashSet<int>(_tableMultiSelected);
            if (anchorIndex >= 0) set.Add(anchorIndex);
            return set.OrderBy(x => x).ToList();
        }

        private void BatchSetMode(List<int> indices, HapticMode mode)
        {
            if (_selectedMap == null) return;
            Undo.RecordObject(_selectedMap, "Set Hapbeat Entry Mode (batch)");
            foreach (int i in indices)
                if (i >= 0 && i < _selectedMap.entries.Count)
                    _selectedMap.entries[i].mode = mode;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
        }

        private void BatchSetGain(List<int> indices, float gain)
        {
            if (_selectedMap == null) return;
            Undo.RecordObject(_selectedMap, "Set Hapbeat Entry Gain (batch)");
            foreach (int i in indices)
                if (i >= 0 && i < _selectedMap.entries.Count)
                    _selectedMap.entries[i].gain = gain;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
        }

        private void BatchDuplicate(List<int> indices)
        {
            if (_selectedMap == null) return;
            Undo.RecordObject(_selectedMap, "Duplicate Hapbeat Entries (batch)");
            // Duplicate bottom-up so intermediate indices remain valid.
            var sorted = indices.OrderByDescending(x => x).ToList();
            foreach (int i in sorted)
            {
                if (i < 0 || i >= _selectedMap.entries.Count) continue;
                var dup = CloneEntry(_selectedMap.entries[i], displayNameSuffix: " (copy)");
                _selectedMap.entries.Insert(i + 1, dup);
            }
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            _tableMultiSelected.Clear();
            SyncTriggerIndexCaches();
            ScanScene();
            RefreshIntensityCache();
        }

        /// <summary>
        /// Full deep copy of a <see cref="HapbeatEventEntry"/>. Covers ALL
        /// user-visible fields (mode / displayName / eventId parts / streamClip /
        /// loop / bindings / gain / target / group / notes) so clipboard
        /// operations and duplicate/batch-duplicate don't silently drop data.
        ///
        /// Binding GUIDs are regenerated so duplicated presets don't alias back to
        /// the source — otherwise a runtime <see cref="HapbeatParameterBinding"/>
        /// linked to the source preset would also pick up the duplicate.
        /// </summary>
        private static HapbeatEventEntry CloneEntry(HapbeatEventEntry src, string displayNameSuffix)
        {
            if (src == null) return null;
            var dst = new HapbeatEventEntry
            {
                mode = src.mode,
                displayName = src.displayName + displayNameSuffix,
                category = src.category,
                eventName = src.eventName,
                streamClip = src.streamClip,
                loop = src.loop,
                bindings = CloneBindings(src.bindings),
                gain = src.gain,
                target = src.target,
                group = src.group,
                notes = src.notes,
            };
            // Propagate the manifest intensity cache. The new entry points at
            // the same category/eventName/streamClip as the source, so the
            // resolved intensity is identical. Without this, dup'd entries fire
            // at plain `gain` (intensity treated as -1 / unknown) until the
            // next RefreshIntensityCache pass — a source of the "sometimes
            // intensity not applied" bug.
            dst.SetCachedManifestIntensity(src.CachedManifestIntensity);
            return dst;
        }

        /// <summary>
        /// Deep-copy a list of binding presets. Each copy gets a fresh GUID so
        /// runtime links (HapbeatParameterBinding._linkedBindingId) don't end up
        /// aliasing to the wrong source after a duplicate.
        /// </summary>
        private static List<HapbeatBindingPreset> CloneBindings(List<HapbeatBindingPreset> src)
        {
            if (src == null) return new List<HapbeatBindingPreset>();
            var result = new List<HapbeatBindingPreset>(src.Count);
            foreach (var b in src)
            {
                if (b == null) continue;
                var copy = new HapbeatBindingPreset
                {
                    sourceTransformPath = b.sourceTransformPath,
                    sourceProperty = b.sourceProperty,
                    inputMin = b.inputMin,
                    inputMax = b.inputMax,
                    curveType = b.curveType,
                    customCurve = b.customCurve != null ? new AnimationCurve(b.customCurve.keys) : null,
                    outputParameter = b.outputParameter,
                    outputMin = b.outputMin,
                    outputMax = b.outputMax,
                    debugLog = b.debugLog,
                    debugLogInterval = b.debugLogInterval,
                    debugLogChangeThreshold = b.debugLogChangeThreshold,
                };
                copy.RegenerateId();
                result.Add(copy);
            }
            return result;
        }

        private void BatchDelete(List<int> indices)
        {
            if (_selectedMap == null) return;
            if (!EditorUtility.DisplayDialog(
                    Tr("Delete Entries", "エントリを削除"),
                    Tr($"Delete {indices.Count} selected entries?",
                       $"選択中の {indices.Count} エントリを削除しますか？"),
                    Tr("Delete", "削除"),
                    Tr("Cancel", "キャンセル")))
                return;

            Undo.RecordObject(_selectedMap, "Delete Hapbeat Entries (batch)");
            var sorted = indices.OrderByDescending(x => x).ToList();
            foreach (int i in sorted)
                if (i >= 0 && i < _selectedMap.entries.Count)
                    _selectedMap.entries.RemoveAt(i);
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            _tableMultiSelected.Clear();
            _selectedEntryIndex = Mathf.Min(_selectedEntryIndex, _selectedMap.entries.Count - 1);
            ScanScene();
        }

        private void DrawTableBatchToolbar()
        {
            int selCount = _tableMultiSelected.Count;
            using (new GUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                GUILayout.Label(
                    selCount == 0
                        ? Tr("Tip: click rows with Ctrl/Shift to select multiple for batch editing.",
                             "ヒント: Ctrl / Shift クリックで複数行選択 → 右クリックで一括編集。")
                        : Tr($"{selCount} rows selected.", $"{selCount} 行選択中。"),
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                if (selCount > 0 && GUILayout.Button("Clear selection", EditorStyles.miniButton, GUILayout.Width(120)))
                {
                    _tableMultiSelected.Clear();
                    Repaint();
                }
            }
        }

        private void DrawEntryTable()
        {
            if (_selectedMap == null) return;

            // Rebuild row-rect cache on Repaint so drag hit-testing has fresh data.
            if (Event.current.type == EventType.Repaint)
                _rowRects.Clear();

            for (int i = 0; i < _selectedMap.entries.Count; i++)
            {
                var entry = _selectedMap.entries[i];
                bool hasTriggers = _triggersByEntry.ContainsKey(i) && _triggersByEntry[i].Count > 0;
                bool isSelected = _selectedEntryIndex == i;

                // Single-line card using manual Rect layout for clipping control
                float rowHeight = EditorGUIUtility.singleLineHeight + 4;
                var cardRect = GUILayoutUtility.GetRect(0, rowHeight, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint)
                    _rowRects.Add(cardRect);

                // Selection highlight
                if (Event.current.type == EventType.Repaint)
                {
                    if (isSelected)
                        EditorGUI.DrawRect(cardRect, SelectedBg);

                    // --- Build 3 segments: name (never clip) | eventId (clip first) | target (high priority) ---
                    // Format: "[index] <mode-icon> <display-name>"
                    //   e.g. "[0] ▶ Click", "[1] ♪ Landing", "[2] ~ PushFeedback"
                    string name = !string.IsNullOrEmpty(entry.displayName) ? entry.displayName : "(new)";
                    string icon = entry.GetModeIcon();
                    string nameText = string.IsNullOrEmpty(icon)
                        ? $"[{i}] {name}"
                        : $"[{i}] {icon} {name}";

                    ParseTarget(entry.target, out _, out int pl, out string pos);
                    string tgt = "all";
                    if (pl >= 1 || !string.IsNullOrEmpty(pos))
                    {
                        tgt = "";
                        if (pl >= 1) tgt += $"P{pl}";
                        if (!string.IsNullOrEmpty(pos))
                        {
                            if (tgt.Length > 0) tgt += "/";
                            tgt += pos.Replace("pos_", "");
                        }
                    }
                    if (hasTriggers) tgt += $" {_triggersByEntry[i].Count}\u25cf";

                    string eid = entry.GetSummary();

                    // Styles
                    Color normalDim = Color.gray;
                    var nameStyle = new GUIStyle(EditorStyles.label)
                    {
                        fontStyle = isSelected ? FontStyle.Bold : FontStyle.Normal
                    };
                    var dimStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        clipping = TextClipping.Clip
                    };
                    var rightStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleRight
                    };
                    if (isSelected)
                    {
                        nameStyle.normal.textColor = SelectedText;
                        dimStyle.normal.textColor = new Color(0.75f, 0.85f, 1f);
                        rightStyle.normal.textColor = new Color(0.9f, 0.95f, 1f);
                    }
                    else
                    {
                        dimStyle.normal.textColor = normalDim;
                        rightStyle.normal.textColor = normalDim;
                    }

                    // Layout: [name (never clip)] [eventId (clip first)] [target (hide if no room)]
                    float pad = 4;
                    float nameW = nameStyle.CalcSize(new GUIContent(nameText)).x + pad;
                    float tgtW = !string.IsNullOrEmpty(tgt) ? rightStyle.CalcSize(new GUIContent(tgt)).x + pad : 0;
                    float totalW = cardRect.width - 4;
                    float remaining = totalW - nameW;

                    // Name always drawn
                    var nameRect = new Rect(cardRect.x + 2, cardRect.y, nameW, cardRect.height);
                    GUI.Label(nameRect, nameText, nameStyle);

                    // Priority for extra info when the card is narrow:
                    //   1. eventId / summary (the "description" of what this entry does) — kept as long as possible
                    //   2. target — hidden first when no room
                    // Rationale: the description tells the designer WHAT the entry is;
                    // target is secondary routing info that can be inferred from context.
                    if (remaining > 30)
                    {
                        bool hasEid = !string.IsNullOrEmpty(eid);
                        bool hasTgt = tgtW > 0;

                        if (hasEid && hasTgt && remaining >= tgtW + 30)
                        {
                            // Plenty of room: eventId (clipped) + target on the right
                            float eidW = remaining - tgtW;
                            var eidRect = new Rect(nameRect.xMax, cardRect.y, eidW, cardRect.height);
                            GUI.Label(eidRect, eid, dimStyle);
                            var tgtRect = new Rect(cardRect.xMax - tgtW - 2, cardRect.y, tgtW, cardRect.height);
                            GUI.Label(tgtRect, tgt, rightStyle);
                        }
                        else if (hasEid)
                        {
                            // Medium-narrow: keep the description (clipped), drop the target.
                            var eidRect = new Rect(nameRect.xMax, cardRect.y, remaining - 2, cardRect.height);
                            GUI.Label(eidRect, eid, dimStyle);
                        }
                        else if (hasTgt && remaining >= tgtW)
                        {
                            // No description available — fall back to showing target.
                            var tgtRect = new Rect(cardRect.xMax - tgtW - 2, cardRect.y, tgtW, cardRect.height);
                            GUI.Label(tgtRect, tgt, rightStyle);
                        }
                        // else: too narrow, show name only
                    }
                }

                GUIUtility.GetControlID(FocusType.Passive, cardRect);

                // Click anywhere to select + defocus text fields.
                // Also record a drag-start candidate; HandleDragReorderInsideScroll
                // promotes it to an actual drag once the mouse moves past threshold.
                if (Event.current.type == EventType.MouseDown
                    && Event.current.button == 0
                    && cardRect.Contains(Event.current.mousePosition))
                {
                    _selectedEntryIndex = i;
                    GUIUtility.keyboardControl = 0; // defocus text fields
                    _dragStartIndex = i;
                    _dragStartPos = Event.current.mousePosition;
                    _dragConfirmed = false;
                    _dropSlotIndex = -1;
                    Event.current.Use();
                    Repaint();
                }

                // Right-click context menu
                if (Event.current.type == EventType.ContextClick
                    && cardRect.Contains(Event.current.mousePosition))
                {
                    _selectedEntryIndex = i;
                    var ctx = new GenericMenu();
                    int idx = i;
                    ctx.AddItem(new GUIContent("Copy Entry Values"), false, () => CopyEntry(idx));
                    bool canPaste = _clipboardEntry != null;
                    if (canPaste)
                        ctx.AddItem(new GUIContent("Paste Entry Values"), false, () => PasteEntry(idx));
                    else
                        ctx.AddDisabledItem(new GUIContent("Paste Entry Values"));
                    ctx.AddSeparator("");
                    ctx.AddItem(new GUIContent("Add Entry Above"), false, () => InsertEntry(idx));
                    ctx.AddItem(new GUIContent("Add Entry Below"), false, () => InsertEntry(idx + 1));
                    ctx.AddItem(new GUIContent("Duplicate Entry"), false, () => DuplicateEntry(idx));
                    ctx.AddSeparator("");
                    ctx.AddItem(new GUIContent("Delete Entry"), false, () => DeleteEntry(idx));
                    ctx.ShowAsContext();
                    Event.current.Use();
                }
            }

            // Handle MouseDrag/MouseUp in the same coordinate space as _rowRects.
            // Must be called AFTER rows are drawn so _rowRects is fresh on Repaint.
            HandleDragReorderInsideScroll();

            // Draw the drop-slot indicator when a drag is in flight.
            if (_dragConfirmed && _dropSlotIndex >= 0 && Event.current.type == EventType.Repaint)
                DrawDropSlotIndicator();

            if (_selectedMap.entries.Count == 0)
                EditorGUILayout.LabelField("(empty \u2014 click + to add)", EditorStyles.centeredGreyMiniLabel);
        }

        private void SetViewMode(ViewMode mode)
        {
            if (_viewMode == mode) return;
            _viewMode = mode;
            EditorPrefs.SetInt(kViewModeKey, (int)mode);
            // Abort any in-flight drag on mode switch.
            CancelDrag();
            Repaint();
        }

        /// <summary>
        /// Pick a sensible default mode for a new entry: the adjacent existing
        /// entry's mode (previous neighbour preferred; next neighbour as fallback).
        /// Falls back to Command when the map is empty.
        /// </summary>
        private HapticMode GetInheritedMode(int hintIndex = -1)
        {
            if (_selectedMap == null || _selectedMap.entries.Count == 0)
                return HapticMode.Command;

            // Prefer the immediately-preceding neighbour, then the currently selected,
            // then the last entry.
            if (hintIndex >= 1 && hintIndex - 1 < _selectedMap.entries.Count)
                return _selectedMap.entries[hintIndex - 1].mode;
            if (hintIndex >= 0 && hintIndex < _selectedMap.entries.Count)
                return _selectedMap.entries[hintIndex].mode;
            if (_selectedEntryIndex >= 0 && _selectedEntryIndex < _selectedMap.entries.Count)
                return _selectedMap.entries[_selectedEntryIndex].mode;
            return _selectedMap.entries[_selectedMap.entries.Count - 1].mode;
        }

        private void InsertEntry(int index)
        {
            if (_selectedMap == null) return;
            Undo.RecordObject(_selectedMap, "Insert Hapbeat Event Entry");
            index = Mathf.Clamp(index, 0, _selectedMap.entries.Count);
            _selectedMap.entries.Insert(index, new HapbeatEventEntry
            {
                mode = GetInheritedMode(index),
            });
            _selectedEntryIndex = index;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            // New entry starts with _cachedManifestIntensity = -1 so its stream
            // mode would silently ignore Studio-authored intensity until refreshed.
            RefreshIntensityCache();
            // Insert shifts every existing entry at/after `index` down by one.
            // Runtime resolution is id-based so this doesn't break wiring, but
            // the display cache on scene triggers would go stale until next
            // Inspector touch. Sync now so EventMap's wiring column + triggers'
            // serialized cache agree with the new list position.
            SyncTriggerIndexCaches();
        }

        private void DeleteEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Remove Hapbeat Event Entry");
            _selectedMap.entries.RemoveAt(index);
            _selectedEntryIndex = Mathf.Min(_selectedEntryIndex, _selectedMap.entries.Count - 1);
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            SyncTriggerIndexCaches();
            ScanScene();
        }

        private void CopyEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            _clipboardEntry = CloneEntry(_selectedMap.entries[index], displayNameSuffix: "");
        }

        private void PasteEntry(int index)
        {
            if (_selectedMap == null || _clipboardEntry == null) return;
            if (index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Paste Hapbeat Event Entry");
            var dst = _selectedMap.entries[index];
            // Paste everything that defines the event's BEHAVIOUR. Keep the
            // destination's displayName/notes so the user preserves the row's
            // identity / documentation.
            dst.mode = _clipboardEntry.mode;
            dst.category = _clipboardEntry.category;
            dst.eventName = _clipboardEntry.eventName;
            dst.streamClip = _clipboardEntry.streamClip;
            dst.loop = _clipboardEntry.loop;
            dst.bindings = CloneBindings(_clipboardEntry.bindings);
            dst.gain = _clipboardEntry.gain;
            dst.target = _clipboardEntry.target;
            dst.group = _clipboardEntry.group;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            // Pasted category/eventName/clip may point at a different manifest
            // entry than before — recompute the intensity cache.
            RefreshIntensityCache();
            SyncTriggerIndexCaches();
        }

        private void DuplicateEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Duplicate Hapbeat Event Entry");
            var dup = CloneEntry(_selectedMap.entries[index], displayNameSuffix: " (copy)");
            _selectedMap.entries.Insert(index + 1, dup);
            _selectedEntryIndex = index + 1;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            // Belt-and-suspenders: CloneEntry already copies the cache, but
            // rescan in case the duplicate's fields drifted since last refresh.
            RefreshIntensityCache();
            SyncTriggerIndexCaches();
        }

        private void DrawSelectedEntryDetail()
        {
            if (_selectedMap == null || _selectedEntryIndex < 0 || _selectedEntryIndex >= _selectedMap.entries.Count)
                return;

            EditorGUILayout.LabelField("Entry Detail", EditorStyles.boldLabel);

            DrawTestPlayBar(_selectedMap.entries[_selectedEntryIndex]);
            EditorGUILayout.Space(2);

            var so = new SerializedObject(_selectedMap);
            var entriesProp = so.FindProperty("entries");
            var entryProp = entriesProp.GetArrayElementAtIndex(_selectedEntryIndex);

            // Use consistent label width for alignment
            float prevLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 70;

            EditorGUI.BeginChangeCheck();

            // Name
            var nameProp = entryProp.FindPropertyRelative("displayName");
            nameProp.stringValue = EditorGUILayout.TextField(
                new GUIContent("Name", "Human-readable label for this event (e.g. Grab, Click)."),
                nameProp.stringValue);

            // Mode — labelled to match Studio's FIRE / CLIP / LIVE shorthand so
            // authors see the same terminology across both tools. The underlying
            // enum names (Command / StreamClip) stay unchanged,
            // so serialized data is unaffected.
            var modeProp = entryProp.FindPropertyRelative("mode");
            int newModeIdx = EditorGUILayout.Popup(
                new GUIContent("Mode",
                    "FIRE: send eventId, device plays pre-flashed Kit clip.\n" +
                    "CLIP: SDK streams a Kit WAV over UDP as PCM16.\n" +
                    "LIVE: SDK captures an AudioSource and streams it."),
                modeProp.enumValueIndex,
                s_ModeLabels);
            modeProp.enumValueIndex = newModeIdx;

            var entry = _selectedMap.entries[_selectedEntryIndex];

            // Mode-specific fields
            switch (entry.mode)
            {
                case HapticMode.Command:
                    DrawCommandFields(entryProp, so);
                    DrawKitEventIdDropdown(
                        entryProp.FindPropertyRelative("category"),
                        entryProp.FindPropertyRelative("eventName"),
                        so);
                    break;
                case HapticMode.StreamClip:
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("streamClip"),
                        new GUIContent("Clip", "AudioClip to stream over UDP. Streamed as PCM16.\nDrag from your HapbeatKits/<kit>/stream-clips/ folder."));
                    DrawKitFolderHint("stream-clips");
                    // Loop is meaningful for StreamClip — used by HapbeatSequenceTrigger's
                    // hold phase so a short clip repeats until Stop() is called, and by
                    // continuously-modulated effects (drag/scrape) driven by bindings.
                    EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("loop"),
                        new GUIContent("Loop",
                            "Keep re-streaming this clip until Stop() is called.\n" +
                            "Use for HapbeatSequenceTrigger's hold phase, and for " +
                            "continuously-modulated effects (drag / scrape) whose gain " +
                            "is driven by a HapbeatParameterBinding.\n" +
                            "Leave off for one-shot impacts."));
                    DrawBindingsList(entryProp);
                    break;
            }

            // Gain (all modes)
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("gain"),
                new GUIContent("Gain",
                    "Master gain multiplier for this entry. 0.0 = silent, 1.0 = normal, 2.0 = maximum.\n\n" +
                    "For StreamClip with bindings: multiplied with the binding's StreamGain output.\n" +
                    "Example: Binding StreamGain = 1.0, entry Gain = 0.5 → stream is sent at 0.5 × samples"));

            // --- Targeting ---
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Targeting", EditorStyles.miniBoldLabel);
            var targetProp = entryProp.FindPropertyRelative("target");
            ParseTarget(targetProp.stringValue, out string curPrefix, out int curPlayer, out string curPos);

            // Prefix
            string newPrefix = EditorGUILayout.TextField(
                new GUIContent("Prefix",
                    "Optional team/group prefix for large multi-team setups.\n" +
                    "Example: team_red, booth_a.\n" +
                    "Leave empty for most projects."),
                curPrefix);

            // Player
            int newPlayer = EditorGUILayout.IntField(
                new GUIContent("Player",
                    "Player number (1-99). Set -1 to target all players.\n" +
                    "For broadcast to all devices, set Player = -1 and Position = (none)."),
                curPlayer);

            // Position
            var posOptions = new string[HapbeatEventEntry.StandardPositions.Length + 1];
            var posValues = new string[posOptions.Length];
            posOptions[0] = "(none \u2014 all positions)";
            posValues[0] = "";
            for (int p = 0; p < HapbeatEventEntry.StandardPositions.Length; p++)
            {
                posOptions[p + 1] = $"{HapbeatEventEntry.PositionLabels[p]}";
                posValues[p + 1] = HapbeatEventEntry.StandardPositions[p];
            }
            int posIdx = System.Array.IndexOf(posValues, curPos);
            if (posIdx < 0) posIdx = 0;
            int newPosIdx = EditorGUILayout.Popup(
                new GUIContent("Position",
                    "Body position of the target device.\n" +
                    "Select (none) to target all positions for the selected player.\n" +
                    "For broadcast, set both Player = -1 and Position = (none)."),
                posIdx, posOptions);
            string newPos = posValues[newPosIdx];

            // Build and preview
            string builtTarget = BuildTargetFromParts(newPrefix, newPlayer, newPos);
            if (builtTarget != targetProp.stringValue)
                targetProp.stringValue = builtTarget;

            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" \u2192 target"),
                string.IsNullOrEmpty(builtTarget) ? "(broadcast \u2014 all devices)" : builtTarget);
            EditorGUI.EndDisabledGroup();

            // Wiring in scene — grouped by GameObject
            if (_triggersByEntry.ContainsKey(_selectedEntryIndex))
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("Wiring:", EditorStyles.miniBoldLabel);

                // Group triggers by GameObject (skip destroyed)
                var byObject = new Dictionary<GameObject, List<string>>();
                bool needRescan = false;
                foreach (var info in _triggersByEntry[_selectedEntryIndex])
                {
                    if (info.trigger == null) { needRescan = true; continue; }
                    var go = info.trigger.gameObject;
                    if (!byObject.ContainsKey(go))
                        byObject[go] = new List<string>();
                    if (info.wiredEvents != null)
                        byObject[go].AddRange(info.wiredEvents);
                    if (byObject[go].Count == 0)
                        byObject[go].Add("(manual)");
                }
                if (needRescan)
                {
                    // Defer ScanScene to next frame to avoid mid-draw layout change
                    EditorApplication.delayCall += ScanScene;
                    // Do NOT early-return here — would cause GUILayout Begin/End mismatch.
                    // Continue drawing with the (possibly stale) list; next frame will refresh.
                }

                float nameW = 80;
                var sorted = byObject.OrderBy(kv => kv.Key.name).ToList();
                foreach (var kv in sorted)
                {
                    for (int w = 0; w < kv.Value.Count; w++)
                    {
                        var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                        float wireW = rect.width - nameW - 4;

                        // Show object name only on first line
                        if (w == 0)
                        {
                            if (GUI.Button(new Rect(rect.x, rect.y, nameW, rect.height),
                                kv.Key.name, EditorStyles.linkLabel))
                            {
                                Selection.activeGameObject = kv.Key;
                                EditorGUIUtility.PingObject(kv.Key);
                            }
                        }

                        GUI.Label(new Rect(rect.x + nameW + 4, rect.y, wireW, rect.height),
                            kv.Value[w], EditorStyles.miniLabel);
                    }
                }
            }

            // Parameter bindings attached to this entry (linked via preset id).
            // Grouped by the GameObject that owns the binding component so the
            // user can jump straight from entry → component in the hierarchy.
            DrawBindingsWiringList(entryProp);

            // Notes (last)
            EditorGUILayout.Space(4);
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("notes"),
                new GUIContent("Notes", "Designer notes. Not sent to devices."));

            EditorGUIUtility.labelWidth = prevLabelWidth;
            if (EditorGUI.EndChangeCheck())
            {
                so.ApplyModifiedProperties();
                // Clip / eventId edits may change the manifest lookup result for
                // this entry, so refresh the cached intensity. Cheap (cached).
                RefreshIntensityCache();
            }
        }

        /// <summary>
        /// Draw a list of every <see cref="HapbeatParameterBinding"/> in open
        /// scenes that is linked to a preset belonging to the currently-selected
        /// entry. Each row is clickable (pings the binding's GameObject).
        /// Bindings don't show up in the trigger-side "Wiring:" list because
        /// they're attached by preset id, not by trigger reference — this
        /// surfaces them so the user can see at a glance which objects
        /// modulate this entry at runtime.
        /// </summary>
        private void DrawBindingsWiringList(SerializedProperty entryProp)
        {
            if (_selectedMap == null) return;
            var bindingsProp = entryProp.FindPropertyRelative("bindings");
            if (bindingsProp == null || bindingsProp.arraySize == 0) return;

            // Collect the preset ids for this entry.
            var presetIds = new HashSet<string>();
            for (int i = 0; i < bindingsProp.arraySize; i++)
            {
                var idProp = bindingsProp.GetArrayElementAtIndex(i).FindPropertyRelative("_id");
                if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
                    presetIds.Add(idProp.stringValue);
            }
            if (presetIds.Count == 0) return;

            // Find matching HapbeatParameterBinding components in the scene.
            var all = UnityEngine.Object.FindObjectsByType<HapbeatParameterBinding>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            var matches = new List<HapbeatParameterBinding>();
            foreach (var b in all)
            {
                if (b == null) continue;
                if (!ReferenceEquals(b.LinkedEventMap, _selectedMap)) continue;
                if (!presetIds.Contains(b.LinkedBindingId)) continue;
                matches.Add(b);
            }
            if (matches.Count == 0) return;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Bindings:", EditorStyles.miniBoldLabel);

            // Group by GameObject for a compact display.
            var byGo = new Dictionary<GameObject, List<HapbeatParameterBinding>>();
            foreach (var b in matches)
            {
                var go = b.gameObject;
                if (!byGo.ContainsKey(go)) byGo[go] = new List<HapbeatParameterBinding>();
                byGo[go].Add(b);
            }

            float nameW = 80;
            foreach (var kv in byGo.OrderBy(k => k.Key.name))
            {
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    var b = kv.Value[i];
                    var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                    float detailW = rect.width - nameW - 4;

                    if (i == 0)
                    {
                        if (GUI.Button(new Rect(rect.x, rect.y, nameW, rect.height),
                            kv.Key.name, EditorStyles.linkLabel))
                        {
                            Selection.activeGameObject = kv.Key;
                            EditorGUIUtility.PingObject(b); // ping the exact component
                        }
                    }

                    // Short details: property → output param. Matches the
                    // summary shown next to the binding entry in the preset list.
                    var preset = b.ResolveLinkedPreset();
                    string detail = preset != null
                        ? $"{preset.sourceProperty} \u2192 {preset.outputParameter}"
                        : "(preset missing)";
                    GUI.Label(new Rect(rect.x + nameW + 4, rect.y, detailW, rect.height),
                        detail, EditorStyles.miniLabel);
                }
            }
        }

        /// <summary>
        /// Inline "Test Play" / "Stop" bar at the top of the entry detail panel.
        ///
        /// Routing logic:
        /// <list type="bullet">
        ///   <item><b>Play mode</b>: use the runtime <see cref="HapbeatManager"/> singleton
        ///     so the event fires through exactly the same pipeline as an in-scene trigger.</item>
        ///   <item><b>Edit mode</b>: fall through to <see cref="HapbeatEditorTransport"/>,
        ///     which opens its own UDP broadcast client via the project's HapbeatConfig.
        ///     No Play-mode entry required, so designers can iterate on entries while
        ///     authoring the scene.</item>
        /// </list>
        ///
        /// Manual-rect layout is used so the buttons are guaranteed to stay visible
        /// as the detail panel shrinks — the summary label clips instead of the buttons.
        /// Buttons fire on <b>MouseDown</b> (press), not MouseUp (release), so feedback
        /// feels instant when tuning haptic intensity.
        ///
        /// For StreamClip entries with parameter bindings, test-play streams
        /// the clip with the entry's effective gain; runtime modulation from
        /// bindings only takes effect in Play mode via the in-scene trigger.
        /// </summary>
        private static void DrawTestPlayBar(HapbeatEventEntry entry)
        {
            bool inPlay = Application.isPlaying;
            bool playPath = inPlay
                && HapbeatManager.Instance != null
                && HapbeatManager.Instance.IsConnected;

            // In Edit mode, the transport is opened lazily when the user clicks.
            // Show the buttons as enabled as long as we can POTENTIALLY open it.
            bool canFire = playPath || !inPlay;

            string hint = null;
            if (inPlay && !playPath)
                hint = "HapbeatManager is not connected yet. Wait a moment or add one via Hapbeat > Create Event Router.";

            // Extra warning for stream modes with no matching manifest intensity —
            // the designer's authored intensity is being silently ignored.
            bool missingIntensity = entry.mode == HapticMode.StreamClip
                && entry.streamClip != null
                && entry.CachedManifestIntensity <= 0f;

            bool isStreaming = playPath
                ? HapbeatManager.Instance.IsStreaming
                : HapbeatEditorTransport.IsStreaming;

            // Single-row bar: [▶ Test Play / ■ Stop toggle]  inline-hint
            // The bar is sized from the CURRENT layout context (the detail scroll
            // view), so boxRect.width is the detail panel's usable width.
            const float padding = 2f;
            const float btnH = 20f;
            const float boxH = 24f;

            var boxRect = GUILayoutUtility.GetRect(0, boxH, GUILayout.ExpandWidth(true));

            // Compact mode: when the detail panel is narrow, collapse to icon-only.
            bool compact = boxRect.width < 260f;
            float btnW = compact ? 30f : 88f;
            string playLabel = compact ? "\u25b6" : "\u25b6 Test Play";
            string stopLabel = compact ? "\u25a0" : "\u25a0 Stop";

            float btnY = boxRect.y + (boxRect.height - btnH) * 0.5f;
            var btnRect = new Rect(boxRect.x + padding, btnY, btnW, btnH);

            // Toggle Play <-> Stop based on live streaming state. Keeps the UI
            // single-purpose — "press to fire, press again to stop" — and avoids
            // the ambiguity of two side-by-side buttons that both look active.
            bool showStop = canFire && isStreaming;
            string label = showStop ? stopLabel : playLabel;
            string tooltip = showStop
                ? "Stop — end the in-flight stream for this entry."
                : canFire
                    ? (inPlay
                        ? "Test Play — fires through HapbeatManager (same path as runtime triggers)."
                        : "Test Play — fires via the editor UDP transport (uses HapbeatConfig port/group).")
                    : hint;

            Color? bg = showStop
                ? new Color(0.9f, 0.45f, 0.45f)
                : canFire ? new Color(0.4f, 0.8f, 0.4f) : (Color?)null;

            DrawPressButton(btnRect, new GUIContent(label, tooltip), canFire, bg,
                () => { if (showStop) TestStopEntry(entry); else TestPlayEntry(entry); });

            // Preserve Stop's horizontal footprint so the hint label lines up
            // regardless of whether the toggle is showing Play or Stop.
            var stopRect = btnRect;

            // Inline hint / status text — rendered on the SAME row as the buttons
            // (right of them) so the bar never takes a second line's worth of height.
            // Priority: connection hint > missing intensity > streaming indicator.
            string inlineHint = null;
            Color hintColor = Color.gray;
            string fullHintTooltip = null;

            if (!string.IsNullOrEmpty(hint))
            {
                inlineHint = hint;
                fullHintTooltip = hint;
                hintColor = new Color(0.95f, 0.85f, 0.55f);
            }
            else if (missingIntensity)
            {
                string msg = "\u26a0 manifest intensity not found \u2014 using gain as-is";
                inlineHint = msg;
                fullHintTooltip = msg +
                    "\n\nDeploy the Kit from Studio, or Refresh the EventMap (↻ toolbar).";
                hintColor = new Color(1f, 0.8f, 0.4f);
            }
            else if (isStreaming)
            {
                inlineHint = "\u266a Streaming\u2026  (Stop to end)";
                fullHintTooltip = inlineHint;
                hintColor = new Color(0.4f, 0.85f, 0.5f);
            }

            if (inlineHint != null)
            {
                float hintX = stopRect.xMax + 6f;
                float hintW = Mathf.Max(0f, boxRect.xMax - hintX - 2f);
                if (hintW > 8f)
                {
                    var hintRect = new Rect(hintX, boxRect.y, hintW, boxRect.height);
                    var hintStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        clipping = TextClipping.Clip,
                        alignment = TextAnchor.MiddleLeft,
                        normal = { textColor = hintColor },
                    };
                    GUI.Label(hintRect, new GUIContent(inlineHint, fullHintTooltip), hintStyle);
                }
            }
        }

        /// <summary>
        /// A button that fires its action on <b>MouseDown</b> (press) instead of
        /// <see cref="GUI.Button"/>'s default MouseUp behaviour. The button's visual
        /// state (hover, tooltip, disabled) is still rendered via GUI.Button — we just
        /// consume the MouseDown event first so the action is dispatched on press.
        ///
        /// Handy for test-play style feedback where perceived latency matters.
        /// </summary>
        private static void DrawPressButton(Rect rect, GUIContent content, bool enabled,
            Color? bgColor, Action onPress)
        {
            var e = Event.current;
            if (enabled
                && e.type == EventType.MouseDown
                && e.button == 0
                && rect.Contains(e.mousePosition))
            {
                onPress?.Invoke();
                e.Use(); // stop GUI.Button from also firing on MouseUp
            }

            using (new EditorGUI.DisabledScope(!enabled))
            {
                var prev = GUI.backgroundColor;
                if (bgColor.HasValue) GUI.backgroundColor = bgColor.Value;
                // Return value intentionally ignored — GUI.Button only exists here for
                // rendering + tooltip/hover behaviour; all click dispatching happens
                // via the MouseDown intercept above.
                GUI.Button(rect, content);
                GUI.backgroundColor = prev;
            }
        }

        private static void TestPlayEntry(HapbeatEventEntry entry)
        {
            string label = string.IsNullOrEmpty(entry.displayName) ? entry.GetSummary() : entry.displayName;
            string target = entry.HasTarget ? entry.target : null;

            // Prefer the runtime path when the manager is active + connected
            // (so behaviour matches an in-scene trigger exactly). Otherwise use
            // the edit-mode transport, which opens its own UDP socket.
            bool usePlayPath = Application.isPlaying
                && HapbeatManager.Instance != null
                && HapbeatManager.Instance.IsConnected;

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (string.IsNullOrEmpty(entry.eventId))
                    {
                        Debug.LogWarning("[Hapbeat] Test-play: Command entry has no eventId.");
                        return;
                    }
                    // Command: device has the flashed intensity and applies it internally.
                    // Send entry.gain raw.
                    if (usePlayPath)
                        HapbeatManager.Instance.Play(entry.eventId, entry.gain, entry.group, label, target);
                    else
                        HapbeatEditorTransport.Play(entry.eventId, entry.gain, entry.group, target);
                    break;

                case HapticMode.StreamClip:
                    {
                        if (entry.streamClip == null)
                        {
                            Debug.LogWarning("[Hapbeat] Test-play: StreamClip entry has no clip.");
                            return;
                        }
                        // Stream modes must apply manifest.intensity themselves because the
                        // device just replays the raw PCM it received. GetEffectiveGain returns
                        // gain × intensity when the intensity is cached, else plain gain.
                        float eff = entry.GetEffectiveGain();
                        if (entry.CachedManifestIntensity <= 0f)
                            Debug.LogWarning(
                                $"[Hapbeat] Test-play StreamClip: manifest intensity not found for '{entry.streamClip.name}'. " +
                                $"Sending gain={eff:F2} without intensity factor. Deploy the Kit from Studio or Refresh the EventMap.");
                        if (usePlayPath)
                            HapbeatManager.Instance.StreamAudioClip(entry.streamClip, eff, target, entry.loop);
                        else
                            HapbeatEditorTransport.StartStream(entry.streamClip, eff, target, entry.loop);
                    }
                    break;

            }
        }

        private static void TestStopEntry(HapbeatEventEntry entry)
        {
            bool usePlayPath = Application.isPlaying
                && HapbeatManager.Instance != null
                && HapbeatManager.Instance.IsConnected;

            switch (entry.mode)
            {
                case HapticMode.Command:
                    if (usePlayPath)
                    {
                        var mgr = HapbeatManager.Instance;
                        if (!string.IsNullOrEmpty(entry.eventId))
                            mgr.Stop(entry.eventId, entry.group, entry.displayName);
                        else
                            mgr.StopAll();
                    }
                    else
                    {
                        HapbeatEditorTransport.Stop(entry.eventId, entry.group);
                    }
                    break;
                case HapticMode.StreamClip:
                    if (usePlayPath)
                        HapbeatManager.Instance.StopStream();
                    else
                        HapbeatEditorTransport.StopStream();
                    break;
            }
        }

        // Cached rects for drag&drop detection (keyed by SerializedProperty.propertyPath).
        // Captured during EventType.Repaint — Unity's drag events can report mousePosition
        // in a different coordinate space than GetControlRect() returns during drag-only
        // passes, so we must use a known-good rect captured from a full paint pass.
        private static Dictionary<string, Rect> _bindingBoxRectCache = new Dictionary<string, Rect>();
        private static Dictionary<string, Rect> _sourcePathRectCache = new Dictionary<string, Rect>();

        private void DrawBindingsList(SerializedProperty entryProp)
        {
            var bindingsProp = entryProp.FindPropertyRelative("bindings");

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Parameter Bindings", EditorStyles.miniBoldLabel);
            if (GUILayout.Button("+", EditorStyles.miniButton, GUILayout.Width(22)))
            {
                bindingsProp.arraySize++;
                var newProp = bindingsProp.GetArrayElementAtIndex(bindingsProp.arraySize - 1);
                // Assign a fresh GUID so runtime HapbeatParameterBinding instances can
                // link to this preset by id (stable across list reordering).
                newProp.FindPropertyRelative("_id").stringValue = System.Guid.NewGuid().ToString("N");
                newProp.FindPropertyRelative("sourceTransformPath").stringValue = "";
                newProp.FindPropertyRelative("sourceProperty").enumValueIndex = (int)BindingSourceProperty.LocalPositionY;
                newProp.FindPropertyRelative("inputMin").floatValue = 0f;
                newProp.FindPropertyRelative("inputMax").floatValue = 1f;
                newProp.FindPropertyRelative("curveType").enumValueIndex = (int)BindingCurveType.Linear;
                newProp.FindPropertyRelative("outputParameter").enumValueIndex = (int)BindingOutputParameter.StreamGain;
                newProp.FindPropertyRelative("outputMin").floatValue = 0f;
                newProp.FindPropertyRelative("outputMax").floatValue = 1f;
                newProp.FindPropertyRelative("debugLog").boolValue = false;
                newProp.FindPropertyRelative("debugLogInterval").floatValue = 0.1f;
                newProp.FindPropertyRelative("debugLogChangeThreshold").floatValue = 0.02f;
            }
            EditorGUILayout.EndHorizontal();

            if (bindingsProp.arraySize == 0)
            {
                EditorGUILayout.LabelField("  (no bindings \u2014 click + to add)", EditorStyles.miniLabel);
                return;
            }

            // Wider label for binding fields (80 → 95)
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 95;

            int pendingDelete = -1;
            for (int i = 0; i < bindingsProp.arraySize; i++)
            {
                var bp = bindingsProp.GetArrayElementAtIndex(i);
                string boxKey = bp.propertyPath;

                // Backfill a GUID if missing (migration from pre-id presets, or
                // preset duplicated via Ctrl-D which copies the id from the source).
                var idProp = bp.FindPropertyRelative("_id");
                if (idProp != null && string.IsNullOrEmpty(idProp.stringValue))
                    idProp.stringValue = System.Guid.NewGuid().ToString("N");

                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                var srcProp = bp.FindPropertyRelative("sourceProperty");
                var outProp = bp.FindPropertyRelative("outputParameter");
                string summary = $"#{i}  {(BindingSourceProperty)srcProp.enumValueIndex} \u2192 {(BindingOutputParameter)outProp.enumValueIndex}";
                EditorGUILayout.LabelField(summary, EditorStyles.miniBoldLabel);
                if (GUILayout.Button("\u2212", EditorStyles.miniButton, GUILayout.Width(22)))
                    pendingDelete = i;
                EditorGUILayout.EndHorizontal();

                var pathProp = bp.FindPropertyRelative("sourceTransformPath");
                DrawSourcePathWithDragDrop(pathProp);
                DrawSourcePathPingRow(pathProp);

                EditorGUILayout.PropertyField(srcProp,
                    new GUIContent("Property", "Which value to read from the source Transform/Rigidbody."));

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("Input Range",
                    "Source value at min/max. Input is normalized to 0-1 within this range.\n" +
                    "Example: LocalPositionY 0 (idle) → -0.01 (pressed) \u21d2 Min=0, Max=-0.01"));
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("inputMin"), GUIContent.none);
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("inputMax"), GUIContent.none);
                EditorGUILayout.EndHorizontal();

                var curveTypeProp = bp.FindPropertyRelative("curveType");
                EditorGUILayout.PropertyField(curveTypeProp,
                    new GUIContent("Curve", "Shape of input-to-output mapping."));
                if ((BindingCurveType)curveTypeProp.enumValueIndex == BindingCurveType.Custom)
                    EditorGUILayout.PropertyField(bp.FindPropertyRelative("customCurve"), new GUIContent("Custom Curve"));

                EditorGUILayout.PropertyField(outProp,
                    new GUIContent("Output",
                        "StreamGain: overall volume multiplier on the active StreamClip " +
                        "playback (0..2). Use for intensity modulation.\n" +
                        "StreamPan: stereo pan (-1..+1). Ignored for mono clips.\n\n" +
                        "Final device samples = audio × StreamGain × entry.gain × " +
                        "(per-channel pan coefficients)"));
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("Output Range",
                    "Target values at input min/max."));
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("outputMin"), GUIContent.none);
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("outputMax"), GUIContent.none);
                EditorGUILayout.EndHorizontal();

                // Debug log — only emits when (value changed >= threshold) AND (interval elapsed).
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("debugLog"),
                    new GUIContent("Debug Log",
                        "Log input/output values to console. Only emitted when the " +
                        "normalized value changed by 'Change' or more AND at least " +
                        "'Interval' seconds passed since the last log line."));
                var dbgProp = bp.FindPropertyRelative("debugLog");
                if (dbgProp.boolValue)
                {
                    EditorGUI.indentLevel++;
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent("Interval",
                        "Minimum seconds between log lines (throttle)."));
                    var intervalProp = bp.FindPropertyRelative("debugLogInterval");
                    intervalProp.floatValue = EditorGUILayout.Slider(intervalProp.floatValue, 0.01f, 2f);
                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.PrefixLabel(new GUIContent("Change",
                        "Minimum normalized-value change (0-1 scale) to emit a line. " +
                        "0 = log every interval regardless of change."));
                    var threshProp = bp.FindPropertyRelative("debugLogChangeThreshold");
                    if (threshProp != null)
                        threshProp.floatValue = EditorGUILayout.Slider(threshProp.floatValue, 0f, 1f);
                    EditorGUILayout.EndHorizontal();
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();

                // Capture the entire binding box rect during Repaint.
                // Used as a fallback drop zone so dragging anywhere inside the binding
                // box (not just the narrow Source Path line) updates sourceTransformPath.
                if (Event.current.type == EventType.Repaint)
                    _bindingBoxRectCache[boxKey] = GUILayoutUtility.GetLastRect();

                // Fallback drop handler — only activates if the text-field-level drop
                // didn't consume the event (e.g., mouse outside text field but inside box).
                if (_bindingBoxRectCache.TryGetValue(boxKey, out var boxRect))
                {
                    var pathPropForBox = bp.FindPropertyRelative("sourceTransformPath");
                    HandlePathDragDrop(boxRect, pathPropForBox, "box");
                }
            }

            // Deferred deletion after the loop to avoid GUI layout mismatch
            if (pendingDelete >= 0 && pendingDelete < bindingsProp.arraySize)
                bindingsProp.DeleteArrayElementAtIndex(pendingDelete);

            EditorGUIUtility.labelWidth = prevLabel;
        }

        /// <summary>
        /// Source Path text field with drag&drop support.
        /// The entire text area is a drop zone; a small object picker (◎) button
        /// on the right lets the user browse GameObjects as well.
        ///
        /// Drop detection uses a rect captured during EventType.Repaint and stored in
        /// <see cref="_sourcePathRectCache"/>. GetControlRect() returns different values
        /// during drag-only event passes inside nested scroll views, so the cached
        /// Repaint-era rect is the only reliable source of truth for hit-testing.
        /// </summary>
        private static void DrawSourcePathWithDragDrop(SerializedProperty pathProp)
        {
            var rect = EditorGUILayout.GetControlRect();
            float labelW = EditorGUIUtility.labelWidth;
            float pickerW = 22;

            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, rect.height),
                new GUIContent("Source Path",
                    "Relative path from target to source Transform.\n" +
                    "Empty / '.' = target itself.\n" +
                    "'Visual' = child named Visual.\n" +
                    "'Body/Head' = nested child.\n\n" +
                    "Drag a GameObject here or use the \u25ce picker button.\n" +
                    "If it's a descendant of the Hierarchy-selected object,\n" +
                    "the relative path is computed. Otherwise, the name is used."));

            var fullRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);
            var textRect = new Rect(fullRect.x, fullRect.y, fullRect.width - pickerW, fullRect.height);
            var pickerRect = new Rect(textRect.xMax, fullRect.y, pickerW, fullRect.height);

            // Cache the text-field rect during Repaint so drag events can hit-test against
            // a known-good rect. The rect returned by GetControlRect() during a drag-only
            // pass may be off because the layout cursor inside a scroll view hasn't been
            // advanced the same way as during Layout/Repaint.
            string key = pathProp.propertyPath;
            if (Event.current.type == EventType.Repaint)
                _sourcePathRectCache[key] = textRect;

            // Resolve drop rect: prefer the cached Repaint rect, fall back to live rect.
            Rect dragRect = _sourcePathRectCache.TryGetValue(key, out var cached) ? cached : textRect;

            // Handle drag&drop on text area only (picker has its own drag handling).
            // Runs BEFORE TextField so the drag event is consumed before IMGUI's text
            // selection logic can steal it.
            HandlePathDragDrop(dragRect, pathProp, "text");

            pathProp.stringValue = EditorGUI.TextField(textRect, pathProp.stringValue);

            // Object picker button (adjacent to text field, no gap)
            var picked = EditorGUI.ObjectField(pickerRect, null, typeof(GameObject), true) as GameObject;
            if (picked != null)
            {
                pathProp.stringValue = ComputeRelativePath(picked);
                GUI.FocusControl(null);
            }
        }

        // Toggle with Hapbeat > Debug > Log Drag&Drop menu (off by default).
        private const string kDragDebugPrefKey = "Hapbeat.EventMap.DragDropDebug";
        private static bool DragDebugEnabled => EditorPrefs.GetBool(kDragDebugPrefKey, false);

        [MenuItem("Hapbeat/Debug/Log Drag&Drop Events", false, 500)]
        private static void ToggleDragDebug()
        {
            bool v = !DragDebugEnabled;
            EditorPrefs.SetBool(kDragDebugPrefKey, v);
            Debug.Log($"[Hapbeat] Drag&Drop event logging: {(v ? "ON" : "OFF")}");
        }

        [MenuItem("Hapbeat/Debug/Log Drag&Drop Events", true)]
        private static bool ToggleDragDebugValidate()
        {
            Menu.SetChecked("Hapbeat/Debug/Log Drag&Drop Events", DragDebugEnabled);
            return true;
        }

        /// <summary>
        /// Accepts a drag&drop of a GameObject into the sourceTransformPath field.
        /// <paramref name="zone"/> identifies which hit-test rect matched (for diagnostics).
        /// </summary>
        private static void HandlePathDragDrop(Rect dropRect, SerializedProperty pathProp, string zone)
        {
            var e = Event.current;

            // Only process drag events
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;

            // Guard: if another handler already consumed this drag frame, skip.
            if (e.type == EventType.Used) return;

            bool inside = dropRect.Contains(e.mousePosition);

            if (DragDebugEnabled)
                Debug.Log($"[DragDrop:{zone}] event={e.type} pos={e.mousePosition} rect={dropRect} inside={inside} refs={DragAndDrop.objectReferences.Length}");

            if (!inside) return;

            GameObject droppedGo = null;
            foreach (var obj in DragAndDrop.objectReferences)
            {
                if (obj is GameObject go) { droppedGo = go; break; }
            }

            DragAndDrop.visualMode = droppedGo != null
                ? DragAndDropVisualMode.Copy
                : DragAndDropVisualMode.Rejected;

            if (e.type == EventType.DragPerform && droppedGo != null)
            {
                DragAndDrop.AcceptDrag();
                pathProp.stringValue = ComputeRelativePath(droppedGo);
                pathProp.serializedObject.ApplyModifiedProperties();
                GUI.FocusControl(null);
                if (DragDebugEnabled)
                    Debug.Log($"[DragDrop:{zone}] DROPPED '{droppedGo.name}' → '{pathProp.stringValue}'");
            }
            e.Use();
        }

        /// <summary>
        /// Draws a small row showing the resolved GameObject for the source path
        /// (based on wiring triggers or Hierarchy selection). Click to ping.
        /// </summary>
        private void DrawSourcePathPingRow(SerializedProperty pathProp)
        {
            string path = pathProp.stringValue;

            // Try resolving via scene triggers bound to this entry, fall back to Selection.
            var resolved = ResolvePathInScene(path);

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelW = EditorGUIUtility.labelWidth;
            var labelRect = new Rect(rect.x, rect.y, labelW, rect.height);
            var valueRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(" \u2192 resolves to",
                "Shows which GameObject the path resolves to, using scene triggers or Hierarchy selection as root. Click to ping."));

            if (resolved != null)
            {
                if (GUI.Button(valueRect, resolved.name, EditorStyles.linkLabel))
                {
                    Selection.activeGameObject = resolved;
                    EditorGUIUtility.PingObject(resolved);
                }
            }
            else
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.LabelField(valueRect,
                    string.IsNullOrEmpty(path) ? "(self)" : "(not resolvable \u2014 no matching target in scene)",
                    EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }
        }

        /// <summary>
        /// Try to resolve a source path relative to:
        /// 1. GameObjects of triggers bound to the currently selected entry
        /// 2. The currently selected Hierarchy object
        /// Returns the first resolved GameObject or null.
        /// </summary>
        private GameObject ResolvePathInScene(string path)
        {
            var candidates = new List<Transform>();

            // Triggers for the currently selected entry
            if (_triggersByEntry.ContainsKey(_selectedEntryIndex))
            {
                foreach (var info in _triggersByEntry[_selectedEntryIndex])
                {
                    if (info.trigger != null)
                        candidates.Add(info.trigger.transform);
                }
            }

            // Hierarchy selection fallback
            if (Selection.activeGameObject != null)
                candidates.Add(Selection.activeGameObject.transform);

            foreach (var root in candidates)
            {
                if (root == null) continue;
                if (string.IsNullOrEmpty(path) || path == ".")
                    return root.gameObject;
                var child = root.Find(path);
                if (child != null) return child.gameObject;
            }
            return null;
        }

        /// <summary>
        /// Compute a relative path from the currently-selected Hierarchy object to the dropped one.
        /// Falls back to the dropped object's name if no ancestor match is found.
        /// </summary>
        private static string ComputeRelativePath(GameObject dropped)
        {
            // Use the Hierarchy-selected object as the target root
            GameObject selected = Selection.activeGameObject;
            if (selected != null && selected != dropped)
            {
                Transform cursor = dropped.transform;
                var segments = new List<string>();
                while (cursor != null && cursor.gameObject != selected)
                {
                    segments.Insert(0, cursor.name);
                    cursor = cursor.parent;
                }
                if (cursor != null && cursor.gameObject == selected)
                    return string.Join("/", segments);
            }

            // Fallback: use the dropped object's name only
            return dropped.name;
        }

        // ── Kit manifest scanner ─────────────────────────────────────────────

        /// <summary>
        /// Cached event metadata parsed from all kits' manifest.json files under
        /// Assets/HapbeatKits/. Invalidated on a short TTL and whenever a user
        /// clicks "Refresh" in the Event ID dropdown.
        /// </summary>
        private struct KitManifestEvent
        {
            public string kitId;       // folder name under HapbeatKits/, e.g. "hand-demo-kit"
            public string eventId;     // full ID, e.g. "impact.hit-soft"
            public string mode;        // "command" (default) / "stream_clip" / "stream_source"
            public string description; // optional description from manifest
        }

        private static List<KitManifestEvent> _cachedKitEvents;
        private static double _cachedKitEventsTime = -1;
        private const double KitEventCacheTtl = 3.0;

        private static List<KitManifestEvent> LoadKitManifestEvents()
        {
            double now = EditorApplication.timeSinceStartup;
            if (_cachedKitEvents != null && now - _cachedKitEventsTime < KitEventCacheTtl)
                return _cachedKitEvents;

            var result = new List<KitManifestEvent>();
            // Resolve via marker asset so the user can move/rename the folder freely.
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (!string.IsNullOrEmpty(kitsRoot) && AssetDatabase.IsValidFolder(kitsRoot))
            {
                foreach (string kitDir in AssetDatabase.GetSubFolders(kitsRoot))
                {
                    string manifestAssetPath = $"{kitDir}/manifest.json";
                    // AssetPath → absolute path ("Assets/..." is relative to project)
                    string absPath = Path.Combine(
                        Application.dataPath,
                        manifestAssetPath.Substring("Assets/".Length));
                    if (!File.Exists(absPath)) continue;
                    try
                    {
                        string json = File.ReadAllText(absPath);
                        string kitId = Path.GetFileName(kitDir);
                        ParseManifestEventsInto(json, kitId, result);
                    }
                    catch (System.Exception e)
                    {
                        UnityEngine.Debug.LogWarning(
                            $"[Hapbeat] Failed to parse {manifestAssetPath}: {e.Message}");
                    }
                }
            }

            _cachedKitEvents = result;
            _cachedKitEventsTime = now;
            return result;
        }

        private static void InvalidateKitManifestCache()
        {
            _cachedKitEvents = null;
            _cachedKitEventsTime = -1;
        }

        /// <summary>
        /// Extract the top-level "events" object from a manifest.json and push each
        /// entry's id/mode/description into <paramref name="output"/>. Uses brace-depth
        /// tracking rather than a JSON library so the SDK stays dependency-free.
        /// </summary>
        private static void ParseManifestEventsInto(string json, string kitId, List<KitManifestEvent> output)
        {
            // Locate the "events" : { ... } block at the top level.
            var eventsMatch = Regex.Match(json, "\"events\"\\s*:\\s*\\{");
            if (!eventsMatch.Success) return;

            int blockStart = eventsMatch.Index + eventsMatch.Length;
            int blockEnd = FindMatchingBrace(json, blockStart);
            if (blockEnd < 0) return;

            string block = json.Substring(blockStart, blockEnd - blockStart);

            // Walk top-level keys inside the events block.
            int pos = 0;
            while (pos < block.Length)
            {
                var keyMatch = Regex.Match(
                    block.Substring(pos), "\"([^\"]+)\"\\s*:\\s*\\{");
                if (!keyMatch.Success) break;

                string eventId = keyMatch.Groups[1].Value;
                int entryStart = pos + keyMatch.Index + keyMatch.Length;
                int entryEnd = FindMatchingBrace(block, entryStart);
                if (entryEnd < 0) break;

                string entryBody = block.Substring(entryStart, entryEnd - entryStart);

                string mode = "command"; // default per kit-format spec (absent mode = command)
                var modeMatch = Regex.Match(entryBody, "\"mode\"\\s*:\\s*\"([^\"]+)\"");
                if (modeMatch.Success) mode = modeMatch.Groups[1].Value;

                string description = "";
                var descMatch = Regex.Match(entryBody, "\"description\"\\s*:\\s*\"([^\"]*)\"");
                if (descMatch.Success) description = descMatch.Groups[1].Value;

                output.Add(new KitManifestEvent
                {
                    kitId = kitId,
                    eventId = eventId,
                    mode = mode,
                    description = description,
                });

                pos = entryEnd + 1;
            }
        }

        /// <summary>
        /// Given the index of the character AFTER an opening "{", return the index
        /// of its matching "}" (brace-balanced). Returns -1 if unbalanced.
        /// Naively ignores string-literal escaping; acceptable for well-formed Studio output.
        /// </summary>
        private static int FindMatchingBrace(string s, int openAfterIdx)
        {
            int depth = 1;
            int i = openAfterIdx;
            bool inString = false;
            while (i < s.Length && depth > 0)
            {
                char c = s[i];
                if (c == '"' && (i == 0 || s[i - 1] != '\\')) inString = !inString;
                else if (!inString)
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                if (depth > 0) i++;
            }
            return depth == 0 ? i : -1;
        }

        // ── Event ID dropdown (Command mode) ─────────────────────────────────

        /// <summary>
        /// Adds a "From Kit ▾" dropdown below the Event ID row that lists every
        /// command-mode event found in Assets/HapbeatKits/*/manifest.json. Picking
        /// an entry splits it into category + name and writes both fields.
        /// </summary>
        private static void DrawKitEventIdDropdown(
            SerializedProperty categoryProp,
            SerializedProperty eventNameProp,
            SerializedObject so)
        {
            var events = LoadKitManifestEvents()
                .Where(e => e.mode == "command")
                .ToList();

            string curId = BuildEventId(categoryProp.stringValue, eventNameProp.stringValue);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUIUtility.labelWidth);

            GUILayout.Label(
                events.Count == 0
                    ? "No Kit events found. Deploy a Kit from Studio."
                    : $"{events.Count} event(s) from {events.Select(e => e.kitId).Distinct().Count()} kit(s)",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            if (GUILayout.Button("From Kit \u25be", EditorStyles.miniButton, GUILayout.Width(80)))
            {
                var menu = new GenericMenu();
                if (events.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("No Kit manifests found"));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Open HapbeatKits folder"), false, () =>
                        RevealKitSubfolder(""));
                }
                else
                {
                    // Group by kit id, kits alphabetical, events alphabetical within kit
                    foreach (var group in events.GroupBy(e => e.kitId).OrderBy(g => g.Key))
                    {
                        foreach (var ev in group.OrderBy(e => e.eventId))
                        {
                            string menuPath = $"{group.Key}/{ev.eventId}";
                            bool isCurrent = ev.eventId == curId;
                            // capture-by-value for the closure
                            string capturedId = ev.eventId;
                            menu.AddItem(new GUIContent(menuPath), isCurrent, () =>
                            {
                                SplitEventId(capturedId, out string cat, out string name);
                                categoryProp.stringValue = cat;
                                eventNameProp.stringValue = name;
                                so.ApplyModifiedProperties();
                            });
                        }
                    }
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Refresh"), false, InvalidateKitManifestCache);
                }
                menu.ShowAsContext();
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string BuildEventId(string cat, string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            if (string.IsNullOrEmpty(cat)) return name;
            return $"{cat}.{name}";
        }

        private static void SplitEventId(string eventId, out string cat, out string name)
        {
            int dot = eventId.IndexOf('.');
            if (dot > 0)
            {
                cat = eventId.Substring(0, dot);
                name = eventId.Substring(dot + 1);
            }
            else
            {
                cat = "";
                name = eventId;
            }
        }

        // ── Kit folder hint ──────────────────────────────────────────────────

        /// <summary>
        /// Show a small inline hint + "Reveal" button pointing to the kit subfolder
        /// that corresponds to the current mode (clips/ for Command, stream-clips/ for
        /// StreamClip).  Searches Assets/HapbeatKits/ for installed kits
        /// and pings the first matching subfolder in the Project window.
        /// </summary>
        private static void DrawKitFolderHint(string subfolder)
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUIUtility.labelWidth);
            string label = subfolder == "clips"
                ? "Look in: <kits-root>/<kit>/clips/"
                : "Look in: <kits-root>/<kit>/stream-clips/";
            GUILayout.Label(label, EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reveal", EditorStyles.miniButton, GUILayout.Width(54)))
                RevealKitSubfolder(subfolder);
            EditorGUILayout.EndHorizontal();
        }

        /// <summary>
        /// Resolve the kits root folder, offering to create it if no marker
        /// exists. The folder isn't auto-created on SDK import — so the first
        /// time a user clicks "Reveal" / "From Kit ▾" without having run the
        /// setup menu, they hit a create-or-cancel dialog here.
        /// </summary>
        /// <returns>Asset-relative path of the kits root, or <c>null</c> if the
        /// user cancelled / creation failed.</returns>
        private static string ResolveOrCreateKitsRoot()
        {
            string kitsRoot = HapbeatKitsReadme.FindKitsRootPath();
            if (!string.IsNullOrEmpty(kitsRoot) && AssetDatabase.IsValidFolder(kitsRoot))
                return kitsRoot;

            bool confirmed = EditorUtility.DisplayDialog(
                Tr("Create HapbeatKits Folder?", "HapbeatKits フォルダを作成しますか？"),
                Tr(
                    "HapbeatKits folder not found.\n\n" +
                    "This is where Hapbeat Studio exports Kits (manifest.json + audio files).\n" +
                    $"It will be created at {HapbeatKitsReadme.DefaultKitsRootPath}/ with a " +
                    "HapbeatKitsReadme marker asset inside.\n" +
                    "You can move or rename the folder afterwards — the marker tracks it.\n\n" +
                    "Create it now?",

                    "HapbeatKits フォルダがまだ見つかりません。\n\n" +
                    "これは Hapbeat Studio が Kit (manifest.json + 音源) を書き出す先です。\n" +
                    $"作成する場合、既定の場所 {HapbeatKitsReadme.DefaultKitsRootPath}/ に作り、" +
                    "マーカーアセット (HapbeatKitsReadme) を中に置きます。\n" +
                    "後で好きな場所にフォルダごと移動してかまいません — マーカーで追跡します。\n\n" +
                    "いま作成しますか？"),
                Tr("Create", "作成する"),
                Tr("Cancel", "キャンセル"));
            if (!confirmed) return null;

            if (!HapbeatKitsFolderCreator.EnsureFolderAndReadme(openReadme: true))
                return null;

            return HapbeatKitsReadme.FindKitsRootPath();
        }

        private static void RevealKitSubfolder(string subfolder)
        {
            string kitsRoot = ResolveOrCreateKitsRoot();
            if (string.IsNullOrEmpty(kitsRoot)) return;

            // Empty subfolder = caller wants the kits root itself
            if (string.IsNullOrEmpty(subfolder))
            {
                var rootObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(kitsRoot);
                if (rootObj != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = rootObj;
                    EditorGUIUtility.PingObject(rootObj);
                }
                return;
            }

            // Collect all kit sub-directories under Assets/HapbeatKits/
            string[] kitDirs = AssetDatabase.GetSubFolders(kitsRoot);
            if (kitDirs == null || kitDirs.Length == 0)
            {
                // Folder exists but no kit has been deployed yet — open the root
                // so the user can see the README / drop a kit in.
                var rootObj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(kitsRoot);
                if (rootObj != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = rootObj;
                    EditorGUIUtility.PingObject(rootObj);
                }
                UnityEngine.Debug.LogWarning(
                    $"[Hapbeat] {kitsRoot}/ contains no kits yet. " +
                    "Open Hapbeat Studio, point its working directory at this folder, then Save/Deploy a Kit.");
                return;
            }

            // Find the first kit that has the requested subfolder
            foreach (string kitDir in kitDirs)
            {
                string targetPath = $"{kitDir}/{subfolder}";
                UnityEngine.Object folder =
                    AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(targetPath);
                if (folder != null)
                {
                    EditorUtility.FocusProjectWindow();
                    Selection.activeObject = folder;
                    EditorGUIUtility.PingObject(folder);
                    return;
                }
            }

            // Subfolder doesn't exist yet — ping the kits root and hint to Save/Deploy
            string hint = subfolder == "clips"
                ? "No clips/ folder found. Deploy a Kit that contains Command events."
                : "No stream-clips/ folder found. Deploy a Kit that contains StreamClip events.";
            UnityEngine.Debug.LogWarning($"[Hapbeat] {hint}");
            UnityEngine.Object kitsFolder =
                AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(kitsRoot);
            if (kitsFolder != null)
            {
                EditorUtility.FocusProjectWindow();
                Selection.activeObject = kitsFolder;
                EditorGUIUtility.PingObject(kitsFolder);
            }
        }

        private void DrawCommandFields(SerializedProperty entryProp, SerializedObject so)
        {
            var categoryProp = entryProp.FindPropertyRelative("category");
            var eventNameProp = entryProp.FindPropertyRelative("eventName");

            // Manual Rect layout for consistent alignment
            var lineRect = EditorGUILayout.GetControlRect();
            float labelW = EditorGUIUtility.labelWidth;
            float dropW = 16;
            float fieldStart = lineRect.x + labelW + 2;
            float totalFieldW = lineRect.width - labelW - 2;
            float catW = totalFieldW * 0.4f - dropW;
            float nameW = totalFieldW * 0.6f;

            // Label
            EditorGUI.LabelField(new Rect(lineRect.x, lineRect.y, labelW, lineRect.height),
                new GUIContent("Event ID", "Composed as category.name. Sent to devices."));

            // Category text field
            categoryProp.stringValue = DrawPlaceholderRect(
                new Rect(fieldStart, lineRect.y, catW, lineRect.height),
                categoryProp.stringValue, "clip");

            // Category dropdown button
            var dropRect = new Rect(fieldStart + catW, lineRect.y, dropW, lineRect.height);
            if (EditorGUI.DropdownButton(dropRect, GUIContent.none, FocusType.Passive))
            {
                var menu = new GenericMenu();
                foreach (var cat in HapbeatEventEntry.StandardCategories)
                {
                    string c = cat;
                    menu.AddItem(new GUIContent(c), categoryProp.stringValue == c,
                        () => { categoryProp.stringValue = c; so.ApplyModifiedProperties(); });
                }
                menu.ShowAsContext();
            }

            // Event name text field
            eventNameProp.stringValue = DrawPlaceholderRect(
                new Rect(fieldStart + catW + dropW + 2, lineRect.y, nameW - 2, lineRect.height),
                eventNameProp.stringValue, "hit");

            // Preview
            var entry = _selectedMap.entries[_selectedEntryIndex];
            string previewId = !string.IsNullOrEmpty(entry.eventId) ? entry.eventId : "clip.hit";
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" \u2192 eventId"), previewId);
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(categoryProp.stringValue) && !HapbeatEventEntry.IsValidSegment(categoryProp.stringValue))
                EditorGUILayout.HelpBox("category: lowercase a-z, 0-9, -, _ only", MessageType.Warning);
            if (!string.IsNullOrEmpty(eventNameProp.stringValue) && !HapbeatEventEntry.IsValidSegment(eventNameProp.stringValue))
                EditorGUILayout.HelpBox("name: lowercase a-z, 0-9, -, _ only", MessageType.Warning);
        }

        /// <summary>Draw a text field with placeholder at a specific Rect.</summary>
        private static string DrawPlaceholderRect(Rect rect, string value, string placeholder)
        {
            string result = EditorGUI.TextField(rect, value);
            if (string.IsNullOrEmpty(result) && !EditorGUIUtility.editingTextField)
                EditorGUI.LabelField(rect, placeholder, PhStyle);
            return result;
        }

        private void ScanScene()
        {
            _triggersByEntry.Clear();
            _orphanedTriggers.Clear();

            if (_selectedMap == null) return;

            // Include inactive so wires on disabled branches stay visible.
            var allTriggers = FindObjectsByType<HapbeatTriggerBase>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var trigger in allTriggers)
            {
                if (trigger == null) continue;
                if (trigger.EventMap != _selectedMap) continue;

                var info = new TriggerInfo
                {
                    trigger = trigger,
                    gameObjectName = trigger.gameObject.name,
                    typeName = GetTriggerTypeName(trigger),
                    wiredEvents = FindWiredEvents(trigger)
                };

                // Resolve the trigger's loop entry via stable id (authoritative).
                // The _entryIndex display cache may be stale — e.g. after the
                // user inserts a new entry in the EventMap, the trigger's
                // _entryId still points at the right entry, but _entryIndex
                // hasn't been rewritten yet. Use id-based lookup so the wiring
                // column matches what the trigger actually fires at runtime.
                int idx = ResolveTriggerEntryIndex(trigger.EntryId, trigger.EntryIndex);
                AddWiring(idx, info);

                // SequenceTrigger's On Start / On Stop phases are also worth
                // surfacing in the wiring list so a user looking at an entry
                // sees every trigger that references it in ANY phase.
                if (trigger is HapbeatSequenceTrigger seq)
                {
                    int startIdx = ResolveTriggerEntryIndex(seq.OnStartEntryId, seq.OnStartEntryIndex);
                    int stopIdx = ResolveTriggerEntryIndex(seq.OnStopEntryId, seq.OnStopEntryIndex);
                    if (startIdx != idx && startIdx >= 0) AddWiring(startIdx, info);
                    if (stopIdx != idx && stopIdx >= 0 && stopIdx != startIdx) AddWiring(stopIdx, info);
                }
            }

            Repaint();
        }

        /// <summary>
        /// Resolve a trigger's entry index from its stable id (authoritative)
        /// falling back to the supplied <paramref name="legacyIndex"/> cache
        /// when the id is empty (unmigrated legacy trigger).
        /// </summary>
        private int ResolveTriggerEntryIndex(string id, int legacyIndex)
        {
            if (_selectedMap == null) return -1;
            if (!string.IsNullOrEmpty(id))
                return _selectedMap.IndexOfId(id);
            return legacyIndex;
        }

        private void AddWiring(int idx, TriggerInfo info)
        {
            if (idx >= 0 && idx < _selectedMap.entries.Count)
            {
                if (!_triggersByEntry.ContainsKey(idx))
                    _triggersByEntry[idx] = new List<TriggerInfo>();
                _triggersByEntry[idx].Add(info);
            }
            else
            {
                _orphanedTriggers.Add(info);
            }
        }

        /// <summary>
        /// Find which UnityEvent fields on sibling components reference this trigger.
        /// Returns list like ["XRGrabInteractable.selectEntered", "Button.onClick"]
        /// </summary>
        private List<string> FindWiredEvents(HapbeatTriggerBase trigger)
        {
            var result = new List<string>();
            var go = trigger.gameObject;

            foreach (var comp in go.GetComponents<Component>())
            {
                if (comp == null || comp is HapbeatTriggerBase || comp is HapbeatEvent)
                    continue;

                string compName = comp.GetType().Name;
                var so = new SerializedObject(comp);
                var iter = so.GetIterator();

                while (iter.NextVisible(true))
                {
                    if (iter.propertyType != SerializedPropertyType.Generic || iter.depth > 2)
                        continue;

                    // Check for persistent calls
                    SerializedProperty callsProp = null;
                    var directCalls = iter.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    if (directCalls != null)
                        callsProp = directCalls;
                    else
                    {
                        var inner = iter.FindPropertyRelative("m_Event");
                        if (inner != null)
                            callsProp = inner.FindPropertyRelative("m_PersistentCalls.m_Calls");
                    }

                    if (callsProp == null || !callsProp.isArray) continue;

                    for (int c = 0; c < callsProp.arraySize; c++)
                    {
                        var call = callsProp.GetArrayElementAtIndex(c);
                        var targetRef = call.FindPropertyRelative("m_Target");
                        if (targetRef != null && targetRef.objectReferenceValue == trigger)
                        {
                            string method = call.FindPropertyRelative("m_MethodName")?.stringValue ?? "?";
                            // Clean field name: m_SelectEntered → selectEntered
                            string fieldName = iter.name;
                            if (fieldName.StartsWith("m_"))
                                fieldName = fieldName.Substring(2);
                            if (fieldName.StartsWith("First") || fieldName.StartsWith("Last"))
                                fieldName = fieldName.Substring(5);
                            if (fieldName.Length > 0)
                                fieldName = char.ToLower(fieldName[0]) + fieldName.Substring(1);
                            result.Add($"{compName}.{fieldName} \u2192 {method}");
                        }
                    }
                }
            }

            return result;
        }

        private string GetTriggerTypeName(HapbeatTriggerBase trigger)
        {
            if (trigger is HapbeatCollisionTrigger) return "Collision";
            if (trigger is HapbeatAnimatorTrigger) return "Animator";
            if (trigger is HapbeatUnityEventTrigger) return "Event";
            return trigger.GetType().Name;
        }

        private void FindEventMap()
        {
            if (_selectedMap != null) return;

            string[] guids = AssetDatabase.FindAssets("t:HapbeatEventMap");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _selectedMap = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
            }
        }

        private void OnFocus()
        {
            ScanScene();
            // Refresh intensity cache on focus — Studio might have re-deployed a Kit
            // while this window was in the background.
            HapbeatManifestIntensity.Invalidate();
            RefreshIntensityCache();
        }

        /// <summary>
        /// Walk every entry in the current map and re-populate
        /// <see cref="HapbeatEventEntry.CachedManifestIntensity"/> from the manifests.
        /// Only writes when the value actually changed, to keep the asset clean.
        /// </summary>
        private void RefreshIntensityCache()
        {
            if (_selectedMap == null) return;

            var so = new SerializedObject(_selectedMap);
            var entriesProp = so.FindProperty("entries");
            bool anyChanged = false;

            for (int i = 0; i < entriesProp.arraySize; i++)
            {
                var entryProp = entriesProp.GetArrayElementAtIndex(i);
                var entry = _selectedMap.entries[i];

                float newValue = HapbeatManifestIntensity.TryGetIntensity(entry, out float found)
                    ? found
                    : -1f;

                var cacheProp = entryProp.FindPropertyRelative("_cachedManifestIntensity");
                if (cacheProp == null) continue;
                if (!Mathf.Approximately(cacheProp.floatValue, newValue))
                {
                    cacheProp.floatValue = newValue;
                    anyChanged = true;
                }
            }

            if (anyChanged)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(_selectedMap);
                AssetDatabase.SaveAssetIfDirty(_selectedMap);
            }
        }

        // --- Placeholder text field helpers ---
        // Draw real TextField always, overlay placeholder only when unfocused + empty.
        // This ensures correct focus ring behavior.

        private static GUIStyle _phStyle;
        private static GUIStyle PhStyle => _phStyle ??= new GUIStyle(EditorStyles.label)
        {
            fontStyle = FontStyle.Italic,
            normal = { textColor = new Color(0.5f, 0.5f, 0.5f, 0.5f) },
            padding = new RectOffset(3, 0, 0, 0)
        };

        private static string DrawPlaceholderField(string label, string value, string placeholder)
        {
            string result = EditorGUILayout.TextField(label, value);
            if (string.IsNullOrEmpty(result) && !IsFieldFocused())
            {
                var rect = GUILayoutUtility.GetLastRect();
                rect.x += EditorGUIUtility.labelWidth + 2;
                rect.width -= EditorGUIUtility.labelWidth + 2;
                EditorGUI.LabelField(rect, placeholder, PhStyle);
            }
            return result;
        }

        private static string DrawPlaceholderFieldInline(string value, string placeholder, float width = 0)
        {
            var opts = width > 0 ? new[] { GUILayout.Width(width) } : new GUILayoutOption[0];
            string result = EditorGUILayout.TextField(value, opts);
            if (string.IsNullOrEmpty(result) && !IsFieldFocused())
            {
                var rect = GUILayoutUtility.GetLastRect();
                EditorGUI.LabelField(rect, placeholder, PhStyle);
            }
            return result;
        }

        private static bool IsFieldFocused()
        {
            return GUIUtility.keyboardControl != 0
                && EditorGUIUtility.editingTextField;
        }

        // --- Target address helpers ---

        /// <summary>
        /// Parse a target string back into prefix, player, position parts.
        /// Handles: "", "player_1", "*/pos_neck", "player_1/pos_neck", "red/player_1/pos_neck"
        /// </summary>
        private static void ParseTarget(string target, out string prefix, out int player, out string position)
        {
            prefix = "";
            player = -1;
            position = "";

            if (string.IsNullOrEmpty(target)) return;

            var parts = target.Split('/');
            var prefixParts = new List<string>();

            foreach (var part in parts)
            {
                if (part.StartsWith("player_") && int.TryParse(part.Substring(7), out int p))
                    player = p;
                else if (part.StartsWith("pos_"))
                    position = part;
                else if (part != "*")
                    prefixParts.Add(part);
            }

            prefix = string.Join("/", prefixParts);
        }

        /// <summary>
        /// Build a target string from separate parts (matching manager's _build_address format).
        /// player=-1 → wildcard or omit. position="" → omit.
        /// </summary>
        private static string BuildTargetFromParts(string prefix, int player, string position)
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(prefix))
                parts.Add(prefix.Trim());

            if (player >= 1)
                parts.Add($"player_{player}");
            else if (!string.IsNullOrEmpty(position))
                parts.Add("*"); // wildcard player when only position is set

            if (!string.IsNullOrEmpty(position))
                parts.Add(position);

            return string.Join("/", parts);
        }
    }
}
#endif
