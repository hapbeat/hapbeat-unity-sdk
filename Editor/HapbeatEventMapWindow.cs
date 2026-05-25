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
        // Persist last-selected EventMap so the window re-opens with the same
        // asset (rather than picking guids[0] which often = HandDemoEventMap
        // alphabetically). Stored as the asset's GUID string.
        private const string kSelectedMapKey = "HapbeatEventMap_SelectedGUID";

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
        private Dictionary<int, List<StateWiringInfo>> _stateWiringsByEntry = new Dictionary<int, List<StateWiringInfo>>();
        private Dictionary<int, List<ScriptWiringInfo>> _scriptWiringsByEntry = new Dictionary<int, List<ScriptWiringInfo>>();
        private List<TriggerInfo> _orphanedTriggers = new List<TriggerInfo>();

        // Cache of (preset.id → last sourceTransformPath we synced) so that
        // DrawBindingsList can detect a path change between draws and trigger
        // a deferred re-sync of scene-side HapbeatParameterBinding components.
        // Keyed by preset GUID — survives preset reordering inside the entry.
        private readonly Dictionary<string, string> _lastSyncedPathByPresetId = new Dictionary<string, string>();

        // Per-preset expansion state in the compact bindings list. Keyed by
        // preset.id. Default = collapsed; users click the summary row to
        // expand the editor for a single preset at a time.
        private readonly Dictionary<string, bool> _bindingExpanded = new Dictionary<string, bool>();

        private struct TriggerInfo
        {
            public HapbeatTriggerBase trigger;
            public string gameObjectName;
            public string typeName;
            public List<string> wiredEvents; // e.g. "XRGrabInteractable.selectEntered"
        }

        /// <summary>
        /// Wiring entry for a <see cref="HapbeatStateBehaviour"/> living on an
        /// AnimatorController state. Unlike scene Trigger components, these are
        /// asset-side and may be referenced by multiple scene Animator GOs.
        /// Stored separately from <see cref="TriggerInfo"/> so the Wiring panel
        /// can render an "State Wiring" section with the (controller, state,
        /// phase) info that doesn't exist on MonoBehaviour-based triggers.
        /// </summary>
        private struct StateWiringInfo
        {
            public HapbeatStateBehaviour behaviour;
            public UnityEditor.Animations.AnimatorController controller;
            public string layerName;
            public string stateName;
            public string phase;             // "Enter" or "Exit"
            public GameObject animatorObject; // a scene Animator using this controller (or null for asset-only)
        }

        /// <summary>
        /// Wiring entry for a custom MonoBehaviour script that references an
        /// EventMap entry by string (typically <c>[SerializeField] private
        /// string _eventName = "..."</c>). Detected heuristically by scanning
        /// every non-Hapbeat MonoBehaviour's serialized string fields and
        /// matching the value against entry <c>displayName</c> or
        /// <c>eventId</c>. Surfaces script-driven fires (e.g. ChargeShooter,
        /// custom game logic) in the EventMap Wiring panel so authors can see
        /// who is calling each entry without grep-ing the codebase.
        /// </summary>
        private struct ScriptWiringInfo
        {
            public MonoBehaviour script;
            public string componentName;     // e.g., "ChargeShooter"
            public string fieldName;         // e.g., "_eventName"
            public string matchedValue;      // the literal string in the field
            public string matchType;         // "displayName" or "eventId"
        }

        [MenuItem("Hapbeat/Event Map", false, 10)]
        public static void ShowWindow()
        {
            var window = GetWindow<HapbeatEventMapWindow>("Hapbeat Event Map");
            window.minSize = new Vector2(500, 300);
        }

        /// <summary>
        /// One-shot scene scaffolding for new users:
        /// (1) ensure <c>Assets/HapbeatSDK/</c> layout exists,
        /// (2) add the Event Router GameObject (if missing),
        /// (3) create an EventMap asset under <c>HapbeatSDK/EventMaps/</c>
        ///     named after the current scene (if no matching asset already exists),
        /// (4) open the Event Map Window with the new asset selected.
        /// <para>
        /// Designed so the user can immediately start authoring events without
        /// hunting through individual menu items. Each step is also exposed as
        /// its own menu (Create Event Router / Create Event Map / Create
        /// HapbeatSDK Folder) for advanced workflows where only one piece is needed.
        /// </para>
        /// </summary>
        [MenuItem("Hapbeat/Initial Scene Setup", false, 30)]
        public static void InitialSceneSetup()
        {
            HapbeatSDKFolderCreator.EnsureLayout(verbose: false);

            // 1. Event Router (idempotent: existing one is reused)
            bool routerExisted = FindObjectsByType<HapbeatManager>(FindObjectsSortMode.None).Length > 0;
            GameObject router;
            if (!routerExisted)
            {
                router = new GameObject("[Hapbeat Event Router]");
                Undo.RegisterCreatedObjectUndo(router, "Create Hapbeat Event Router");
                router.AddComponent<HapbeatManager>();
            }
            else
            {
                router = FindFirstObjectByType<HapbeatManager>().gameObject;
            }

            // 2. EventMap asset (named after the active scene for uniqueness across scenes;
            //    existing same-name asset is reused so re-running is a no-op).
            var map = EnsureSceneEventMapAsset();

            // 3. Ping + select + open window
            Selection.activeObject = map;
            EditorGUIUtility.PingObject(map);
            ShowWindow();

            string mapPath = AssetDatabase.GetAssetPath(map);
            Debug.Log(
                $"[Hapbeat] Initial Scene Setup 完了:\n" +
                $"  - Event Router: {(routerExisted ? "(既存を再利用)" : "新規追加")} {router.name}\n" +
                $"  - EventMap   : {mapPath}\n" +
                $"  - Hapbeat → Event Map ウィンドウを開きました。+ Entry でイベント追加から始められます。");
        }

        /// <summary>
        /// Asset-only creation of an EventMap. Mirrors the Project window's
        /// <c>Create → Hapbeat → Event Map</c> convenience but uses the standard
        /// HapbeatSDK/EventMaps/ location so first-time users don't have to hunt
        /// for the right folder.
        /// </summary>
        [MenuItem("Hapbeat/Create Event Map", false, 32)]
        public static void CreateEventMapAsset()
        {
            HapbeatSDKFolderCreator.EnsureLayout(verbose: false);
            var map = EnsureSceneEventMapAsset();
            Selection.activeObject = map;
            EditorGUIUtility.PingObject(map);
            Debug.Log($"[Hapbeat] EventMap を作成しました: {AssetDatabase.GetAssetPath(map)}");
        }

        /// <summary>
        /// Resolve (or create) the EventMap asset associated with the active
        /// scene. Naming: <c>HapbeatSDK/EventMaps/&lt;scene&gt;-EventMap.asset</c>
        /// when a scene is loaded, else <c>HapbeatEventMap.asset</c>. Reusing the
        /// same path keeps re-runs idempotent.
        /// </summary>
        private static HapbeatEventMap EnsureSceneEventMapAsset()
        {
            string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string baseName = string.IsNullOrEmpty(sceneName) ? "HapbeatEventMap" : $"{sceneName}-EventMap";
            string assetPath = $"{HapbeatSDKFolderCreator.kEventMapsDir}/{baseName}.asset";

            var existing = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(assetPath);
            if (existing != null) return existing;

            // GenerateUniqueAssetPath handles the "user previously deleted the .meta but the
            // path is stale" edge case by walking forward (foo-EventMap 1.asset, etc.).
            string finalPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
            var map = ScriptableObject.CreateInstance<HapbeatEventMap>();
            AssetDatabase.CreateAsset(map, finalPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(finalPath);
        }

        [MenuItem("Hapbeat/Create Event Router", false, 31)]
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
            // 前回選択していた EventMap を GUID から復元。無ければ
            // FindEventMap で「scene に最初にヒットした 1 個」にフォールバック。
            RestoreSelectedMapFromPrefs();
            if (_selectedMap == null) FindEventMap();
            _splitRatio = EditorPrefs.GetFloat(kSplitRatioKey, 0.42f);
            _splitRatio = Mathf.Clamp(_splitRatio, 0.2f, 0.8f);
            _viewMode = (ViewMode)EditorPrefs.GetInt(kViewModeKey, (int)ViewMode.List);
            RefreshIntensityCache();
            EnsureEntryIdsAssigned();
        }

        // Window が背景に回る / 閉じる際にディスク確定。これがないと
        // SetDirty 済みでも domain reload や Unity 終了で edit が失われる。
        private void OnDisable()
        {
            if (_selectedMap != null)
                AssetDatabase.SaveAssetIfDirty(_selectedMap);
        }

        private void OnLostFocus()
        {
            if (_selectedMap != null)
                AssetDatabase.SaveAssetIfDirty(_selectedMap);
        }

        private void RestoreSelectedMapFromPrefs()
        {
            string guid = EditorPrefs.GetString(kSelectedMapKey, "");
            if (string.IsNullOrEmpty(guid)) return;
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(path)) return;
            _selectedMap = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
        }

        private void RememberSelectedMap()
        {
            if (_selectedMap == null) { EditorPrefs.DeleteKey(kSelectedMapKey); return; }
            string path = AssetDatabase.GetAssetPath(_selectedMap);
            string guid = AssetDatabase.AssetPathToGUID(path);
            if (!string.IsNullOrEmpty(guid))
                EditorPrefs.SetString(kSelectedMapKey, guid);
        }

        // Tab タイトル: 未保存なら "Hapbeat Event Map *"
        private void UpdateWindowTitle()
        {
            const string baseTitle = "Hapbeat Event Map";
            bool dirty = _selectedMap != null && EditorUtility.IsDirty(_selectedMap);
            string desired = dirty ? baseTitle + " *" : baseTitle;
            if (titleContent.text != desired)
                titleContent = new GUIContent(desired);

            // Project window indicator 用に dirty な EventMap の GUID を共有
            DirtyEventMapGUID = dirty
                ? AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(_selectedMap))
                : null;
        }

        /// <summary>
        /// 現在 dirty な EventMap の GUID (= 未保存 / .asset 単位)。
        /// Project window indicator (file 末尾の [InitializeOnLoad] class) が読む。
        /// 同時に開ける window は 1 つ前提なので単一 string で OK。
        /// </summary>
        internal static string DirtyEventMapGUID;

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
            // Window タブの title に dirty 状態を反映 (* マーカ)
            // — 他 window にフォーカスがあっても tab を見れば状態が分かる。
            UpdateWindowTitle();

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

            int newIndex = (toSlot > from) ? toSlot - 1 : toSlot;

            Undo.RecordObject(_selectedMap, "Reorder Hapbeat Event Entries");
            var item = _selectedMap.entries[from];
            _selectedMap.entries.RemoveAt(from);
            _selectedMap.entries.Insert(newIndex, item);

            _selectedEntryIndex = newIndex;
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);

            // Scene triggers reference entries by stable GUID, so reorders do
            // not require any trigger-side rewrites. Just rescan so the
            // wired-trigger view re-associates rows with their new positions.
            ScanScene();

            var entryLabel = string.IsNullOrEmpty(item.displayName) ? item.GetSummary() : item.displayName;
            Debug.Log($"[Hapbeat] Reordered entry '{entryLabel}' (was #{from}, now #{newIndex}). " +
                      "Press Ctrl+Z to undo.");
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
                RememberSelectedMap();
                ScanScene();
                RefreshIntensityCache();
            }

            // Dirty インジケータ + Save ボタン (asset 単位)。
            // Unity は ScriptableObject の dirty 状態を Project window に
            // 表示しないので、ここで明示する。
            if (_selectedMap != null)
            {
                bool dirty = EditorUtility.IsDirty(_selectedMap);
                // 視覚的に分かりやすいよう dirty 時のみ色付き Save ボタンを表示。
                if (dirty)
                {
                    var prev = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.55f, 0.3f); // オレンジ = 未保存
                    if (GUILayout.Button(new GUIContent("● Save", "未保存の変更があります。クリック or Ctrl+S で disk に保存。"),
                        EditorStyles.toolbarButton, GUILayout.Width(70)))
                    {
                        AssetDatabase.SaveAssetIfDirty(_selectedMap);
                        // toolbar の表示が ● Save → ✓ Saved に切り替わるので
                        // 追加の notification は不要。
                    }
                    GUI.backgroundColor = prev;
                }
                else
                {
                    GUILayout.Label(new GUIContent("✓ Saved", "未保存変更はありません。"),
                        EditorStyles.toolbarButton, GUILayout.Width(70));
                }
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
                delayOffsetSeconds = src.delayOffsetSeconds,
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
                int triggerCount = (_triggersByEntry.TryGetValue(i, out var tList) ? tList.Count : 0)
                                 + (_stateWiringsByEntry.TryGetValue(i, out var sList) ? sList.Count : 0)
                                 + (_scriptWiringsByEntry.TryGetValue(i, out var scList) ? scList.Count : 0);
                bool hasTriggers = triggerCount > 0;
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

                    ParseTarget(entry.target, out _, out int pl, out string pos, out int gr);
                    string tgt = "all";
                    if (pl >= 1 || gr >= 1 || !string.IsNullOrEmpty(pos))
                    {
                        tgt = "";
                        if (pl >= 1) tgt += $"P{pl}";
                        if (!string.IsNullOrEmpty(pos))
                        {
                            if (tgt.Length > 0) tgt += "/";
                            tgt += pos.Replace("pos_", "");
                        }
                        if (gr >= 1)
                        {
                            if (tgt.Length > 0) tgt += "/";
                            tgt += $"G{gr}";
                        }
                    }
                    if (hasTriggers) tgt += $" {triggerCount}\u25cf";

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
            // Runtime resolution is id-based so insert / delete / reorder do
            // not require trigger-side rewrites.
        }

        private void DeleteEntry(int index)
        {
            if (_selectedMap == null || index < 0 || index >= _selectedMap.entries.Count) return;
            Undo.RecordObject(_selectedMap, "Remove Hapbeat Event Entry");
            _selectedMap.entries.RemoveAt(index);
            _selectedEntryIndex = Mathf.Min(_selectedEntryIndex, _selectedMap.entries.Count - 1);
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
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
            EditorUtility.SetDirty(_selectedMap);
            AssetDatabase.SaveAssetIfDirty(_selectedMap);
            // Pasted category/eventName/clip may point at a different manifest
            // entry than before — recompute the intensity cache.
            RefreshIntensityCache();
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
        }

        private void DrawSelectedEntryDetail()
        {
            if (_selectedMap == null || _selectedEntryIndex < 0 || _selectedEntryIndex >= _selectedMap.entries.Count)
                return;

            EditorGUILayout.LabelField("Entry Detail", EditorStyles.boldLabel);

            DrawTestPlayBar(_selectedMap.entries[_selectedEntryIndex], _selectedMap);
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
                    var clipProp = entryProp.FindPropertyRelative("streamClip");
                    EditorGUI.BeginChangeCheck();
                    EditorGUILayout.PropertyField(clipProp,
                        new GUIContent("Clip", "AudioClip to stream over UDP. Streamed as PCM16.\n" +
                            "Drag from your HapbeatSDK/Kits/<kit>/stream-clips/ folder.\n" +
                            "On change, the clip's owning Kit manifest is auto-attached to the Manifest slot."));
                    if (EditorGUI.EndChangeCheck())
                    {
                        // Commit the streamClip change first so AssetDatabase
                        // reads see the new value, then auto-attach the
                        // manifest from the clip's owning Kit folder.
                        so.ApplyModifiedProperties();
                        if (_selectedMap != null &&
                            _selectedEntryIndex >= 0 &&
                            _selectedEntryIndex < _selectedMap.entries.Count)
                        {
                            AutoAttachManifestForEntry(
                                _selectedMap.entries[_selectedEntryIndex], _selectedMap);
                        }
                        // Refresh so subsequent GUI reads the updated entry.
                        so.Update();
                    }
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
                    // Bindings UI was relocated to the bottom of the panel
                    // (just before Notes) — see DrawBindingsList call below.
                    break;
            }

            // Gain (all modes)
            EditorGUILayout.PropertyField(entryProp.FindPropertyRelative("gain"),
                new GUIContent("Gain",
                    "Master gain multiplier for this entry. 0.0 = silent, 1.0 = normal, 2.0 = maximum.\n\n" +
                    "For StreamClip with bindings: multiplied with the binding's StreamGain output.\n" +
                    "Example: Binding StreamGain = 1.0, entry Gain = 0.5 → stream is sent at 0.5 × samples"));

            // Delay offset (per-entry latency tweak; added to HapbeatConfig.hapticDelaySeconds)
            var delayOffsetProp = entryProp.FindPropertyRelative("delayOffsetSeconds");
            EditorGUILayout.PropertyField(delayOffsetProp,
                new GUIContent("Delay Offset (s)",
                    "この entry 個別の遅延オフセット (秒)。HapbeatConfig.hapticDelaySeconds " +
                    "(global) に加算される。最終値は 0 でクランプ。\n" +
                    "  ・正値: global より遅らせる (音の attack peak が遅い素材の補正など)\n" +
                    "  ・負値: global より早める\n" +
                    "Range -0.2〜+0.2、デフォルト 0。\n\n" +
                    "Global 側の調整は Hapbeat → Settings の「触覚遅延」から。"));

            // Effective delay readout (global + offset, clamped). Helps the
            // designer see "how late will this fire" without having to switch
            // to the Settings window mentally.
            float globalDelaySec = ResolveGlobalHapticDelaySeconds();
            float entryOffsetSec = delayOffsetProp.floatValue;
            float effectiveSec = Mathf.Max(0f, globalDelaySec + entryOffsetSec);
            string offsetSign = entryOffsetSec >= 0f ? "+" : "";
            EditorGUILayout.LabelField(
                $"   → 実効遅延: {effectiveSec * 1000f:F0}ms  " +
                $"(global {globalDelaySec * 1000f:F0}ms {offsetSign}{entryOffsetSec * 1000f:F0}ms)",
                EditorStyles.miniLabel);

            // --- Targeting ---
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Targeting", EditorStyles.miniBoldLabel);
            var targetProp = entryProp.FindPropertyRelative("target");
            ParseTarget(targetProp.stringValue, out string curPrefix, out int curPlayer, out string curPos, out int curGroup);

            // Prefix
            string newPrefix = EditorGUILayout.TextField(
                new GUIContent("Prefix",
                    "Optional team prefix for large multi-team setups.\n" +
                    "Example: team_red, booth_a.\n" +
                    "Leave empty for most projects."),
                curPrefix);

            // Player
            int newPlayer = EditorGUILayout.IntField(
                new GUIContent("Player",
                    "Player number (1-99). Set -1 to target all players.\n" +
                    "For broadcast to all devices, set Player = -1, Position = (none), Group = -1."),
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
                    "Select (none) to target all positions for the selected player."),
                posIdx, posOptions);
            string newPos = posValues[newPosIdx];

            // Group (encoded as 'group_<N>' suffix AFTER position per device-addressing.md \u00a72)
            int newGroup = EditorGUILayout.IntField(
                new GUIContent("Group",
                    "Group number (1-99). Set -1 to target all groups.\n" +
                    "Encoded as 'group_<N>' segment after position."),
                curGroup);

            // Build and preview
            string builtTarget = BuildTargetFromParts(newPrefix, newPlayer, newPos, newGroup);
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

                float nameW = 100;
                var sorted = byObject.OrderBy(kv => kv.Key.name).ToList();
                foreach (var kv in sorted)
                {
                    // Header row: name link + inline trigger-params editor
                    // (gain × multiplier, plus tick params if it's a TickTrigger).
                    // Reads/writes the live scene component directly — no preset
                    // duplication, just visibility + quick edit from EventMap.
                    DrawWiredObjectHeaderRow(kv.Key, nameW);

                    // Event lines, indented under the header.
                    for (int w = 0; w < kv.Value.Count; w++)
                    {
                        var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
                        GUI.Label(new Rect(rect.x + nameW + 4, rect.y,
                                           rect.width - nameW - 4, rect.height),
                            kv.Value[w], EditorStyles.miniLabel);
                    }
                }
            }

            // State-machine wiring (HapbeatStateBehaviour on AnimatorController state).
            // Shown as a separate section because the data model differs from
            // scene Trigger components — the wire is on an asset, not a GO.
            if (_stateWiringsByEntry.ContainsKey(_selectedEntryIndex))
            {
                EditorGUILayout.Space(4);
                DrawStateWiringSection(_stateWiringsByEntry[_selectedEntryIndex]);
            }

            // Script wiring (heuristic SerializeField string match) — surfaces
            // custom MonoBehaviours that fire this entry via script
            // (e.g. ChargeShooter._eventName = "charge_release").
            if (_scriptWiringsByEntry.ContainsKey(_selectedEntryIndex))
            {
                EditorGUILayout.Space(4);
                DrawScriptWiringSection(_scriptWiringsByEntry[_selectedEntryIndex]);
            }

            // Parameter Bindings — compact per-wired-object layout. Only
            // shown for StreamClip entries since bindings have no effect in
            // Command mode (the device side has no stream to modulate).
            if (entryProp.FindPropertyRelative("mode").enumValueIndex == (int)HapticMode.StreamClip)
            {
                EditorGUILayout.Space(4);
                DrawBindingsList(entryProp);
            }

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
        /// <summary>
        /// Header row for one wired GameObject in the Wiring section. Shows:
        ///   [GameObject name link]   [trigger-type tag]  gain× [F]  [tick params if TickTrigger]
        /// Inline-edits the LIVE scene component (no preset / link layer) so
        /// per-object tuning stays single-source-of-truth on the component.
        /// </summary>
        private void DrawWiredObjectHeaderRow(GameObject go, float nameW)
        {
            // Find a Hapbeat trigger component on this GO that fires the
            // currently-selected entry. Most GameObjects have just one,
            // but Sequence + UnityEvent triggers can coexist; pick the
            // first matching this entry id.
            HapbeatTriggerBase trigger = null;
            string entryId = null;
            if (_selectedMap != null && _selectedEntryIndex >= 0
                && _selectedEntryIndex < _selectedMap.entries.Count)
            {
                entryId = _selectedMap.entries[_selectedEntryIndex]?.id;
            }
            foreach (var t in go.GetComponents<HapbeatTriggerBase>())
            {
                if (t == null) continue;
                if (!ReferenceEquals(t.EventMap, _selectedMap)) continue;
                if (entryId != null && t.EntryId == entryId) { trigger = t; break; }
                if (trigger == null) trigger = t; // fallback: any trigger on this GO bound to this map
            }

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float typeTagW = 34f;   // tight: just enough for "Event"/"Tick"/"Seq"/"Coll"
            const float gainLabelW = 28f; // "gain"
            const float gainFieldW = 42f;
            const float tickFieldW = 50f;
            const float axisW = 50f;
            const float gap = 4f;
            const float tightGap = 1f;    // hugged label-to-field

            // Left: GameObject name link
            var nameRect = new Rect(rect.x, rect.y, nameW, rect.height);
            if (GUI.Button(nameRect, go.name, EditorStyles.linkLabel))
            {
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
            }

            float cursor = rect.x + nameW + gap;
            if (trigger == null)
            {
                // No matching trigger component (orphaned scan entry).
                GUI.Label(new Rect(cursor, rect.y, rect.width - (cursor - rect.x), rect.height),
                    "(no trigger)", EditorStyles.miniLabel);
                return;
            }

            // Type tag
            string typeTag = trigger switch
            {
                HapbeatSequenceTrigger _    => "Seq",
                HapbeatTickEmitter _        => "Tick",
                HapbeatCollisionTrigger _   => "Coll",
                HapbeatUnityEventTrigger _  => "Event",
                _                           => trigger.GetType().Name,
            };
            GUI.Label(new Rect(cursor, rect.y, typeTagW, rect.height), typeTag, EditorStyles.miniBoldLabel);
            cursor += typeTagW + tightGap;

            // Gain multiplier (always shown — applies to all trigger types).
            GUI.Label(new Rect(cursor, rect.y, gainLabelW, rect.height),
                new GUIContent("gain",
                    "Per-trigger gain multiplier。\n" +
                    "実効値 = entry.gain × この値。\n" +
                    "デフォルト 1.0 (素通し)。同じ EventMap entry を複数 GameObject で使い回すときに、" +
                    "objectごとに強度を微調整したい場合に使う。\n" +
                    "範囲: 0.0 〜 2.0"),
                EditorStyles.miniLabel);
            cursor += gainLabelW;

            float newGain = EditorGUI.FloatField(
                new Rect(cursor, rect.y, gainFieldW, rect.height),
                trigger.GainMultiplier);
            cursor += gainFieldW + gap;
            if (!Mathf.Approximately(newGain, trigger.GainMultiplier))
            {
                Undo.RecordObject(trigger, "Edit Hapbeat Trigger Gain");
                var so = new SerializedObject(trigger);
                so.FindProperty("_gainMultiplier").floatValue = Mathf.Clamp(newGain, 0f, 2f);
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(trigger);
            }

            // TickTrigger: tick threshold + axis inline.
            if (trigger is HapbeatTickEmitter tick)
            {
                const string thresholdTip =
                    "Δ = Tick Threshold (1 tick あたりの変化量)\n" +
                    "─────────────────────────\n" +
                    "入力値がこの量だけ累積で変化するたびに 1 回 fire する。\n" +
                    "ホイールやダイヤルの \"カチッ\" 感 (detent 触覚) を生む閾値。\n\n" +
                    "目安:\n" +
                    "  Slider 0..1   → Δ=0.1   で 10% ごと 1 tick\n" +
                    "  Slider 0..100 → Δ=5     で 5 ごと 1 tick\n" +
                    "  ScrollRect    → Δ=0.05  で 5% ごと 1 tick (正規化 0..1)\n\n" +
                    "小さいほど tick が密、大きいほど疎。\n" +
                    "0 にすると \"任意の変化で fire\" モード (= 元の cooldown 方式と等価)。";
                const string axisTip =
                    "Vector2 入力時の追跡軸\n" +
                    "─────────────────────────\n" +
                    "ScrollRect / MinMaxSlider のような Vector2 を吐く UI で、" +
                    "どの軸を tick 計算に使うかを選ぶ。\n\n" +
                    "  X   横軸 (横スクロール / MinMaxSlider の min ハンドル)\n" +
                    "  Y   縦軸 (縦スクロール / MinMaxSlider の max ハンドル)\n" +
                    "  Mag 2軸合成のベクトル長 (斜め移動も含めた総移動量)\n\n" +
                    "float 入力 (通常の Slider 等) では無視される。";

                GUI.Label(new Rect(cursor, rect.y, 14f, rect.height),
                    new GUIContent("Δ", thresholdTip), EditorStyles.miniLabel);
                cursor += 14f;

                var so = new SerializedObject(tick);
                var threshProp = so.FindProperty("_tickThreshold");
                var axisProp = so.FindProperty("_axis");

                // Wrap field in its own GUIContent so the tooltip propagates
                // to the field area as well (Unity gives float fields an empty
                // label by default → no hover hint).
                EditorGUI.BeginChangeCheck();
                var threshRect = new Rect(cursor, rect.y, tickFieldW, rect.height);
                float currentThresh = threshProp != null ? threshProp.floatValue : 0f;
                GUI.Label(threshRect, new GUIContent("", thresholdTip));
                float newThresh = EditorGUI.FloatField(threshRect, currentThresh);
                cursor += tickFieldW + gap;

                var axisRect = new Rect(cursor, rect.y, axisW, rect.height);
                GUI.Label(axisRect, new GUIContent("", axisTip));
                int newAxis = EditorGUI.Popup(axisRect,
                    axisProp != null ? axisProp.enumValueIndex : 0,
                    new[] { "X", "Y", "Mag" });
                cursor += axisW + gap;
                if (EditorGUI.EndChangeCheck())
                {
                    Undo.RecordObject(tick, "Edit Hapbeat Tick Trigger");
                    if (threshProp != null) threshProp.floatValue = Mathf.Max(0f, newThresh);
                    if (axisProp != null) axisProp.enumValueIndex = newAxis;
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(tick);
                }
            }
        }

        /// <summary>
        /// Render the "State Wiring:" section for entries fired by
        /// <see cref="HapbeatStateBehaviour"/>. One row per
        /// (animator GO, state, phase) pair, grouped by animator GO.
        /// Each row shows a link to the Animator's GameObject, the state
        /// name + phase (Enter/Exit), and the per-behaviour Gain multiplier
        /// edited inline.
        /// </summary>
        private void DrawStateWiringSection(List<StateWiringInfo> wirings)
        {
            EditorGUILayout.LabelField("State Wiring:", EditorStyles.miniBoldLabel);

            // Group by animator GameObject (null = "asset-only" group at the bottom).
            var byGo = new Dictionary<GameObject, List<StateWiringInfo>>();
            var assetOnly = new List<StateWiringInfo>();
            foreach (var w in wirings)
            {
                if (w.animatorObject == null) { assetOnly.Add(w); continue; }
                if (!byGo.TryGetValue(w.animatorObject, out var list))
                {
                    list = new List<StateWiringInfo>();
                    byGo[w.animatorObject] = list;
                }
                list.Add(w);
            }

            float nameW = 100f;
            foreach (var kv in byGo.OrderBy(p => p.Key.name))
            {
                DrawStateWiredObjectHeader(kv.Key, kv.Value[0], nameW);
                foreach (var w in kv.Value)
                    DrawStateWiringRow(w, nameW);
            }

            // Asset-only entries (no scene Animator uses the controller yet).
            if (assetOnly.Count > 0)
            {
                EditorGUILayout.LabelField(
                    "(Controllers without a scene Animator)",
                    EditorStyles.miniLabel);
                foreach (var w in assetOnly)
                    DrawStateWiringAssetOnlyRow(w, nameW);
            }
        }

        /// <summary>
        /// Header row for a scene Animator GameObject that uses a controller
        /// with at least one matching HapbeatStateBehaviour. Mirrors
        /// <see cref="DrawWiredObjectHeaderRow"/> visually: GO link + "State" tag
        /// + per-behaviour Gain editor (uses the first wiring's behaviour;
        /// users tweak each behaviour's gain individually from the rows below).
        /// </summary>
        private void DrawStateWiredObjectHeader(GameObject go, StateWiringInfo first, float nameW)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float typeTagW = 34f;
            const float gainLabelW = 28f;
            const float gainFieldW = 42f;
            const float gap = 4f;
            const float tightGap = 1f;

            // GO link
            if (GUI.Button(new Rect(rect.x, rect.y, nameW, rect.height),
                go.name, EditorStyles.linkLabel))
            {
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
            }

            float cursor = rect.x + nameW + gap;
            GUI.Label(new Rect(cursor, rect.y, typeTagW, rect.height),
                "State", EditorStyles.miniBoldLabel);
            cursor += typeTagW + tightGap;

            // Gain editor for the first matching behaviour. (When multiple
            // behaviours fire this entry on the same Animator, only the first
            // one's gain is shown here as a quick-edit affordance — the others
            // expose their gain via Project window selection.)
            if (first.behaviour != null)
            {
                GUI.Label(new Rect(cursor, rect.y, gainLabelW, rect.height),
                    new GUIContent("gain",
                        "Per-state-behaviour gain multiplier.\n" +
                        "実効値 = entry.gain × manifest.intensity × この値。"),
                    EditorStyles.miniLabel);
                cursor += gainLabelW;

                float newGain = EditorGUI.FloatField(
                    new Rect(cursor, rect.y, gainFieldW, rect.height),
                    first.behaviour.GainMultiplier);
                cursor += gainFieldW + gap;
                if (!Mathf.Approximately(newGain, first.behaviour.GainMultiplier))
                {
                    Undo.RecordObject(first.behaviour, "Edit Hapbeat State Gain");
                    first.behaviour.GainMultiplier = Mathf.Clamp(newGain, 0f, 2f);
                    EditorUtility.SetDirty(first.behaviour);
                }
            }
        }

        /// <summary>
        /// Detail row under a state-wiring header: controller / state / phase.
        /// Clicking pings the AnimatorController asset so the user can open
        /// the Animator window and locate the state.
        /// </summary>
        private void DrawStateWiringRow(StateWiringInfo w, float nameW)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            string ctrlName = w.controller != null ? w.controller.name : "(controller missing)";
            string label = $"{ctrlName} / {w.stateName}  ({w.phase})";
            var labelRect = new Rect(rect.x + nameW + 4, rect.y,
                rect.width - nameW - 4, rect.height);
            if (GUI.Button(labelRect, label, EditorStyles.linkLabel))
            {
                if (w.controller != null)
                {
                    Selection.activeObject = w.controller;
                    EditorGUIUtility.PingObject(w.controller);
                }
            }
        }

        /// <summary>
        /// Asset-only row when no scene Animator uses the controller.
        /// Shows the controller path + state name so the user can still
        /// navigate to the wire. No GO link / no gain editor (no instance to
        /// tweak — gain still applies, edit it via Project selection).
        /// </summary>
        private void DrawStateWiringAssetOnlyRow(StateWiringInfo w, float nameW)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            string ctrlName = w.controller != null ? w.controller.name : "(controller missing)";
            string label = $"  {ctrlName} / {w.stateName}  ({w.phase})";
            if (GUI.Button(new Rect(rect.x, rect.y, rect.width, rect.height),
                label, EditorStyles.linkLabel))
            {
                if (w.controller != null)
                {
                    Selection.activeObject = w.controller;
                    EditorGUIUtility.PingObject(w.controller);
                }
            }
        }

        /// <summary>
        /// Render the "Script Wiring:" section for entries referenced by
        /// non-Hapbeat MonoBehaviour scripts (e.g. ChargeShooter holding
        /// <c>_eventName = "charge_release"</c> in a SerializeField).
        /// One row per matching field, grouped by the script's GameObject so
        /// multiple field matches on the same GO collapse visually.
        /// </summary>
        private void DrawScriptWiringSection(List<ScriptWiringInfo> wirings)
        {
            EditorGUILayout.LabelField("Script Wiring:", EditorStyles.miniBoldLabel);

            // Group by GameObject for visual coherence with the other sections.
            var byGo = new Dictionary<GameObject, List<ScriptWiringInfo>>();
            foreach (var w in wirings)
            {
                if (w.script == null) continue;
                var go = w.script.gameObject;
                if (!byGo.TryGetValue(go, out var list))
                {
                    list = new List<ScriptWiringInfo>();
                    byGo[go] = list;
                }
                list.Add(w);
            }

            float nameW = 100f;
            foreach (var kv in byGo.OrderBy(p => p.Key.name))
            {
                DrawScriptWiredObjectHeader(kv.Key, kv.Value[0], nameW);
                foreach (var w in kv.Value)
                    DrawScriptWiringRow(w, nameW);
            }
        }

        /// <summary>
        /// Header row for one GameObject in the Script Wiring section.
        /// Shows the GO link + "Script" type tag + script component name.
        /// Clicking the script component name selects/pings the component
        /// so the user can inspect its fields directly.
        /// </summary>
        private void DrawScriptWiredObjectHeader(GameObject go, ScriptWiringInfo first, float nameW)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float typeTagW = 38f;
            const float gap = 4f;
            const float tightGap = 1f;

            // GO link
            if (GUI.Button(new Rect(rect.x, rect.y, nameW, rect.height),
                go.name, EditorStyles.linkLabel))
            {
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
            }

            float cursor = rect.x + nameW + gap;
            GUI.Label(new Rect(cursor, rect.y, typeTagW, rect.height),
                "Script", EditorStyles.miniBoldLabel);
            cursor += typeTagW + tightGap;

            // Component name as a clickable link to ping the script component.
            var componentRect = new Rect(cursor, rect.y, rect.width - (cursor - rect.x), rect.height);
            if (GUI.Button(componentRect, first.componentName, EditorStyles.linkLabel))
            {
                Selection.activeObject = first.script;
                EditorGUIUtility.PingObject(first.script);
            }
        }

        /// <summary>
        /// Detail row under a script-wiring header. Shows
        /// <c>fieldName = "matchedValue"</c> so the user can verify the match
        /// is intentional (heuristic detection has some false-positive risk
        /// when an unrelated string field coincidentally matches an entry
        /// name).
        /// </summary>
        private void DrawScriptWiringRow(ScriptWiringInfo w, float nameW)
        {
            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            // Trim the conventional Unity m_/_ prefix for display.
            string display = w.fieldName;
            if (display.StartsWith("m_")) display = display.Substring(2);
            else if (display.StartsWith("_")) display = display.Substring(1);
            string label = $"{display} = \"{w.matchedValue}\"  ({w.matchType})";
            var labelRect = new Rect(rect.x + nameW + 4, rect.y,
                rect.width - nameW - 4, rect.height);
            GUI.Label(labelRect, label, EditorStyles.miniLabel);
        }

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
        private static void DrawTestPlayBar(HapbeatEventEntry entry, HapbeatEventMap owningMap)
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

            // Manifest UI — right-aligned to boxRect's right edge:
            //   [Test Play btn]  [hint stretches]  [Manifest][field][⟳]
            // Components:
            //   - "Manifest" label on the left
            //   - Custom picker field (.json-only, "manifest" search preset)
            //   - Refresh button — re-runs auto-attach from clip's path
            const float manifestLabelW       = 58f;
            const float manifestFieldMin     = 80f;
            const float manifestFieldPreferred = 160f;
            const float refreshBtnW          = 22f;
            const float gap                  = 4f;
            const float rightPad             = 2f;

            float idealManifestRowW = manifestLabelW + gap + manifestFieldPreferred + gap + refreshBtnW;
            float minManifestRowW   = manifestLabelW + gap + manifestFieldMin       + gap + refreshBtnW;
            float availForManifest  = boxRect.xMax - btnRect.xMax - rightPad - gap;

            float manifestRowW;
            if (availForManifest >= idealManifestRowW) manifestRowW = idealManifestRowW;
            else if (availForManifest >= minManifestRowW) manifestRowW = availForManifest;
            else manifestRowW = 0f; // too narrow — manifest UI hidden

            float manifestRowX = boxRect.xMax - manifestRowW - rightPad;

            if (manifestRowW > 0f)
            {
                float labelX   = manifestRowX;
                float fieldX   = labelX + manifestLabelW + gap;
                float refreshX = manifestRowX + manifestRowW - refreshBtnW;
                float fieldW   = refreshX - fieldX - gap;

                var labelRect    = new Rect(labelX,   btnY, manifestLabelW, btnH);
                var manifestRect = new Rect(fieldX,   btnY, fieldW,         btnH);
                var refreshRect  = new Rect(refreshX, btnY, refreshBtnW,    btnH);

                string resolvedSource = HapbeatManifestIntensity.DescribeResolvedSource(entry);
                string manifestTooltip = entry.manifestOverride != null
                    ? $"Manifest override: {AssetDatabase.GetAssetPath(entry.manifestOverride)}\n" +
                      "Clear to auto-resolve. Use ⟳ to re-detect from the clip's folder."
                    : !string.IsNullOrEmpty(resolvedSource)
                        ? $"Auto-resolved from: {resolvedSource}\n" +
                          "Drop a *-manifest.json here to override, or click ⟳ to attach from clip's folder."
                        : "No manifest matched. Drop a *-manifest.json or click ⟳ to attach from the clip's folder.";

                GUI.Label(labelRect,
                    new GUIContent("Manifest", manifestTooltip),
                    EditorStyles.miniLabel);

                var newOverride = DrawManifestPickerField(
                    manifestRect, entry.manifestOverride, manifestTooltip,
                    out bool changed);
                if (changed && newOverride != entry.manifestOverride)
                {
                    if (owningMap != null)
                        Undo.RegisterCompleteObjectUndo(owningMap, "Set Manifest Override");
                    entry.manifestOverride = newOverride;
                    if (HapbeatManifestIntensity.TryGetIntensity(entry, out float resolved))
                        entry.SetCachedManifestIntensity(resolved);
                    else
                        entry.SetCachedManifestIntensity(-1f);
                    if (owningMap != null) EditorUtility.SetDirty(owningMap);
                }

                // Refresh button — re-run auto-attach from the clip's path.
                var refreshIcon = EditorGUIUtility.IconContent("Refresh");
                var refreshContent = new GUIContent(refreshIcon.image,
                    "Re-attach the manifest.\n" +
                    "• StreamClip entry: walks up from the clip's folder.\n" +
                    "• Command entry: resolves HapbeatSDK/Kits/<category>/<category>-manifest.json.\n" +
                    "Useful after moving a clip / Kit, or renaming a manifest.");
                if (GUI.Button(refreshRect, refreshContent))
                {
                    AutoAttachManifestForEntry(entry, owningMap);
                }
            }

            // The hint label fills the space between Test Play and the
            // manifest UI (or to the right edge if manifest UI is hidden).
            float hintRightEdge = manifestRowW > 0f ? manifestRowX - gap : boxRect.xMax;
            float hintAreaX = btnRect.xMax + 6f;
            float hintAreaW = Mathf.Max(0f, hintRightEdge - hintAreaX);

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

            if (inlineHint != null && hintAreaW > 8f)
            {
                var hintRect = new Rect(hintAreaX, boxRect.y, hintAreaW, boxRect.height);
                var hintStyle = new GUIStyle(EditorStyles.miniLabel)
                {
                    clipping = TextClipping.Clip,
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = hintColor },
                };
                GUI.Label(hintRect, new GUIContent(inlineHint, fullHintTooltip), hintStyle);
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
        /// <summary>
        /// Detect and attach the Kit manifest that owns this entry.
        /// Resolution order:
        /// <list type="number">
        ///   <item><b>streamClip path</b> (StreamClip / hybrid mode): walk up
        ///         from the clip's asset path until a <c>*manifest*.json</c>
        ///         is found in some ancestor folder.</item>
        ///   <item><b>category (= kit name)</b> (Command mode, or when
        ///         streamClip is null): look up
        ///         <c>HapbeatSDK/Kits/&lt;category&gt;/&lt;category&gt;-manifest.json</c>
        ///         via <see cref="HapbeatManifestIntensity.GetKitDirectory"/>.</item>
        /// </list>
        /// Updates <see cref="HapbeatEventEntry.manifestOverride"/> and
        /// refreshes the cached intensity. Called automatically when the
        /// user assigns / changes the entry's streamClip OR category, and
        /// manually via the Refresh button next to the Manifest field.
        /// </summary>
        private static void AutoAttachManifestForEntry(HapbeatEventEntry entry, HapbeatEventMap owningMap)
        {
            if (entry == null) return;

            // Try clip-based discovery first (more specific — works for any
            // entry that has a clip, regardless of mode).
            var found = HapbeatManifestIntensity.FindManifestForClip(entry.streamClip);

            // Fallback: category-based discovery for Command-mode entries
            // (or anywhere streamClip is empty). Maps category → kit folder
            // → <kit>-manifest.json.
            if (found == null && !string.IsNullOrEmpty(entry.category))
            {
                string kitDir = HapbeatManifestIntensity.GetKitDirectory(entry.category);
                if (!string.IsNullOrEmpty(kitDir))
                {
                    string manifestAssetPath = HapbeatManifestIntensity.FindKitManifest(kitDir);
                    if (!string.IsNullOrEmpty(manifestAssetPath))
                        found = AssetDatabase.LoadAssetAtPath<TextAsset>(manifestAssetPath);
                }
            }

            // No-op when nothing is found AND no current override (avoid
            // accidentally clearing a designer-pinned override).
            if (found == null && entry.manifestOverride == null) return;
            if (found == entry.manifestOverride)
            {
                // Same target — still refresh the cached intensity in case
                // the manifest's intensity value was edited externally.
                if (HapbeatManifestIntensity.TryGetIntensity(entry, out float same))
                    entry.SetCachedManifestIntensity(same);
                if (owningMap != null) EditorUtility.SetDirty(owningMap);
                return;
            }
            if (owningMap != null)
                Undo.RegisterCompleteObjectUndo(owningMap, "Auto-attach Manifest");
            entry.manifestOverride = found;
            if (HapbeatManifestIntensity.TryGetIntensity(entry, out float resolved))
                entry.SetCachedManifestIntensity(resolved);
            else
                entry.SetCachedManifestIntensity(-1f);
            if (owningMap != null) EditorUtility.SetDirty(owningMap);
        }

        /// <summary>
        /// Custom replacement for <c>EditorGUI.ObjectField</c> tailored to
        /// the manifest selector:
        /// <list type="bullet">
        ///   <item>Click anywhere on the field opens
        ///         <see cref="EditorGUIUtility.ShowObjectPicker{T}"/> pre-filtered
        ///         with the search string "manifest" so files like
        ///         <c>showcase-kit-manifest.json</c> surface at the top.</item>
        ///   <item>Drag-drop validates that the dropped asset is a TextAsset
        ///         whose path ends with ".json"; non-JSON drops are rejected
        ///         with a warning instead of silently overwriting the field.</item>
        ///   <item><c>changed</c> is set to true only when the underlying
        ///         override reference actually changes (matches the existing
        ///         <c>EditorGUI.EndChangeCheck</c> contract).</item>
        /// </list>
        /// </summary>
        private static TextAsset DrawManifestPickerField(
            Rect rect, TextAsset current, string tooltip, out bool changed)
        {
            changed = false;
            var e = Event.current;

            // 1. Drag-drop: accept TextAsset whose path ends with .json.
            if (rect.Contains(e.mousePosition))
            {
                if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
                {
                    TextAsset valid = null;
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (!(obj is TextAsset ta)) continue;
                        string p = AssetDatabase.GetAssetPath(ta);
                        if (!string.IsNullOrEmpty(p) &&
                            p.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                        {
                            valid = ta;
                            break;
                        }
                    }
                    DragAndDrop.visualMode = valid != null
                        ? DragAndDropVisualMode.Copy
                        : DragAndDropVisualMode.Rejected;
                    if (e.type == EventType.DragPerform)
                    {
                        if (valid != null)
                        {
                            DragAndDrop.AcceptDrag();
                            current = valid;
                            changed = true;
                        }
                        else if (DragAndDrop.objectReferences.Length > 0)
                        {
                            Debug.LogWarning(
                                "[Hapbeat] Manifest override must be a .json file " +
                                "(typically <kitname>-manifest.json).");
                        }
                        e.Use();
                    }
                }
            }

            // 2. Render the field as a clickable "object field" style box.
            //    We tag a control ID so we can correlate the picker close
            //    event with this specific field.
            int pickerCtrlId = GUIUtility.GetControlID(FocusType.Passive);
            string display = current != null ? current.name : "None (Manifest)";
            var content = new GUIContent(display, tooltip);
            // Custom render: standard objectField style + a small picker dot
            // on the right. Click anywhere → open filtered picker.
            if (GUI.Button(rect, content, EditorStyles.objectField))
            {
                EditorGUIUtility.ShowObjectPicker<TextAsset>(
                    current, false, "manifest", pickerCtrlId);
            }

            // 3. Listen for the picker close event matching our control ID.
            if (e.commandName == "ObjectSelectorUpdated"
                && EditorGUIUtility.GetObjectPickerControlID() == pickerCtrlId)
            {
                var picked = EditorGUIUtility.GetObjectPickerObject() as TextAsset;
                // Validate .json extension; reject other text assets.
                if (picked != null)
                {
                    string p = AssetDatabase.GetAssetPath(picked);
                    if (!p.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogWarning(
                            $"[Hapbeat] Manifest override must be a .json file. Got: {p}");
                        picked = current; // revert
                    }
                }
                if (picked != current)
                {
                    current = picked;
                    changed = true;
                }
                e.Use();
            }

            return current;
        }

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
                    {
                        // Device no longer reads manifest.intensity at runtime —
                        // SDK applies gain × intensity before putting it on the wire.
                        float eff = entry.GetEffectiveGain();
                        if (entry.CachedManifestIntensity < 0f)
                            Debug.LogWarning(
                                $"[Hapbeat] Test-play Command: manifest intensity not found for '{entry.eventId}'. " +
                                $"Sending gain={eff:F2} without intensity factor. Deploy the Kit from Studio or Refresh the EventMap.");
                        if (usePlayPath)
                            HapbeatManager.Instance.Play(entry.eventId, eff, label, target);
                        else
                            HapbeatEditorTransport.Play(entry.eventId, eff, target);
                    }
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
                        if (entry.CachedManifestIntensity < 0f)
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
                {
                    string stopTarget = entry.HasTarget ? entry.target : null;
                    if (usePlayPath)
                    {
                        var mgr = HapbeatManager.Instance;
                        if (!string.IsNullOrEmpty(entry.eventId))
                            mgr.Stop(entry.eventId, entry.displayName, stopTarget);
                        else
                            mgr.StopAll(stopTarget);
                    }
                    else
                    {
                        HapbeatEditorTransport.Stop(entry.eventId, stopTarget);
                    }
                    break;
                }
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

        // =====================================================================
        // Wired-object grouped preset rendering (per ?? feedback).
        //
        // Mental model:
        //   Wired GameObject is the parent (header). Underneath, the user sees
        //   the events wired to that object (read-only) and the bindings whose
        //   ownerObjectName matches that object (editable).
        //   "+ Binding" creates a new preset already owned by that GameObject.
        //   ownerObjectName is implicit from grouping; users do NOT pick it.
        //
        // Bindings without an owner appear in a "Shared (all wired)" foldout.
        // Bindings whose owner doesn't match any current trigger appear in an
        // "Orphan" foldout for cleanup.
        // =====================================================================

        /// <summary>
        /// Initialise a freshly-grown preset entry. Sets stable id, default
        /// values, and the supplied owner name so "+ Binding" buttons under
        /// each wired-object foldout produce correctly-scoped presets without
        /// the user having to touch ownerObjectName.
        /// </summary>
        private void InitNewPreset(SerializedProperty newProp, string ownerName)
        {
            newProp.FindPropertyRelative("_id").stringValue = System.Guid.NewGuid().ToString("N");
            var ownerProp = newProp.FindPropertyRelative("ownerObjectName");
            if (ownerProp != null) ownerProp.stringValue = ownerName ?? "";
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

        /// <summary>
        /// Render a single preset box. Returns the absolute index in
        /// <paramref name="bindingsProp"/> if the user clicked the delete
        /// button this frame, else -1. <paramref name="contextOwnerName"/>
        /// scopes the source-path resolution so "→ resolves to" finds
        /// the right trigger when multiple objects are wired to this entry.
        /// </summary>
        private int DrawSinglePresetBox(SerializedProperty bindingsProp, int presetIndex,
            string contextOwnerName)
        {
            var bp = bindingsProp.GetArrayElementAtIndex(presetIndex);
            string boxKey = bp.propertyPath;
            int deleteIdx = -1;

            // Backfill stable id (migration / Ctrl-D duplication path).
            var idProp = bp.FindPropertyRelative("_id");
            if (idProp != null && string.IsNullOrEmpty(idProp.stringValue))
                idProp.stringValue = System.Guid.NewGuid().ToString("N");

            EditorGUILayout.BeginVertical("box");

            EditorGUILayout.BeginHorizontal();
            var srcProp = bp.FindPropertyRelative("sourceProperty");
            var outProp = bp.FindPropertyRelative("outputParameter");
            string summary = $"#{presetIndex}  {(BindingSourceProperty)srcProp.enumValueIndex} → " +
                             $"{(BindingOutputParameter)outProp.enumValueIndex}";
            EditorGUILayout.LabelField(summary, EditorStyles.miniBoldLabel);
            if (GUILayout.Button("−", EditorStyles.miniButton, GUILayout.Width(22)))
                deleteIdx = presetIndex;
            EditorGUILayout.EndHorizontal();

            var pathProp = bp.FindPropertyRelative("sourceTransformPath");
            DrawSourcePathWithDragDrop(pathProp);
            DrawSourcePathPingRowScoped(pathProp, contextOwnerName);

            if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
                DrawLinkedBindingsRowScoped(idProp.stringValue, contextOwnerName);

            EditorGUILayout.PropertyField(srcProp,
                new GUIContent("Property", "Which value to read from the source Transform/Rigidbody."));

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Input Range",
                "Source value at min/max. Input is normalized to 0-1 within this range."));
            EditorGUILayout.PropertyField(bp.FindPropertyRelative("inputMin"), GUIContent.none);
            EditorGUILayout.PropertyField(bp.FindPropertyRelative("inputMax"), GUIContent.none);
            EditorGUILayout.EndHorizontal();

            var curveTypeProp = bp.FindPropertyRelative("curveType");
            EditorGUILayout.PropertyField(curveTypeProp,
                new GUIContent("Curve", "Shape of input-to-output mapping."));
            if ((BindingCurveType)curveTypeProp.enumValueIndex == BindingCurveType.Custom)
                EditorGUILayout.PropertyField(bp.FindPropertyRelative("customCurve"),
                    new GUIContent("Custom Curve"));

            EditorGUILayout.PropertyField(outProp,
                new GUIContent("Output",
                    "StreamGain: overall volume multiplier on the active StreamClip playback (0..2).\n" +
                    "StreamPan: stereo pan (-1..+1). Ignored for mono clips."));
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(new GUIContent("Output Range",
                "Target values at input min/max."));
            EditorGUILayout.PropertyField(bp.FindPropertyRelative("outputMin"), GUIContent.none);
            EditorGUILayout.PropertyField(bp.FindPropertyRelative("outputMax"), GUIContent.none);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.PropertyField(bp.FindPropertyRelative("debugLog"),
                new GUIContent("Debug Log",
                    "Log input/output values to console on change. Throttled by Interval/Change."));
            var dbgProp = bp.FindPropertyRelative("debugLog");
            if (dbgProp.boolValue)
            {
                EditorGUI.indentLevel++;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("Interval",
                    "Minimum seconds between log lines."));
                var intervalProp = bp.FindPropertyRelative("debugLogInterval");
                intervalProp.floatValue = EditorGUILayout.Slider(intervalProp.floatValue, 0.01f, 2f);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel(new GUIContent("Change",
                    "Minimum normalized-value change required to emit a line."));
                var threshProp = bp.FindPropertyRelative("debugLogChangeThreshold");
                if (threshProp != null)
                    threshProp.floatValue = EditorGUILayout.Slider(threshProp.floatValue, 0f, 1f);
                EditorGUILayout.EndHorizontal();
                EditorGUI.indentLevel--;
            }

            EditorGUILayout.EndVertical();

            if (Event.current.type == EventType.Repaint)
                _bindingBoxRectCache[boxKey] = GUILayoutUtility.GetLastRect();

            if (_bindingBoxRectCache.TryGetValue(boxKey, out var boxRect))
                HandlePathDragDrop(boxRect, pathProp, "box");

            return deleteIdx;
        }

        /// <summary>
        /// Draws the foldout for a single wired GameObject. Header shows
        /// counts and an "+ Binding" button that creates a preset already
        /// owned by this GameObject. Body lists wired events (read-only)
        /// and the matching bindings.
        /// </summary>
        private int DrawWiredObjectFoldout(SerializedProperty bindingsProp,
            string objectName, List<string> wiredEvents, List<int> presetIndices,
            int pendingDelete)
        {
            return DrawCompactBindingGroup(bindingsProp,
                headerLabel: objectName,
                ownerForAdd: objectName,
                presetIndices: presetIndices,
                pendingDelete: pendingDelete,
                allowAdd: true,
                addTooltip: $"{objectName} に紐付く新しい binding を追加",
                showAsLink: true);
        }

        /// <summary>Draws the "Shared (all wired)" or "Orphan" group.</summary>
        private int DrawSpecialGroupFoldout(SerializedProperty bindingsProp,
            string foldoutKeySuffix, string headerLabel, string ownerForAdd,
            List<int> presetIndices, int pendingDelete, bool allowAdd)
        {
            return DrawCompactBindingGroup(bindingsProp,
                headerLabel: headerLabel,
                ownerForAdd: ownerForAdd,
                presetIndices: presetIndices,
                pendingDelete: pendingDelete,
                allowAdd: allowAdd,
                addTooltip: "Wired 全対象に適用される binding を追加",
                showAsLink: false);
        }

        /// <summary>
        /// Compact 1-line group header (mimics existing Wiring section
        /// density). Header shows the GameObject name (clickable to ping in
        /// the link case) with a right-aligned "+ Binding" button. Each
        /// existing preset is one collapsed summary line below; click to
        /// expand the editor inline. No big nested box / Foldout chrome —
        /// the design goal is to keep vertical real estate cheap so the
        /// detail panel stays readable.
        /// </summary>
        private int DrawCompactBindingGroup(SerializedProperty bindingsProp,
            string headerLabel, string ownerForAdd, List<int> presetIndices,
            int pendingDelete, bool allowAdd, string addTooltip, bool showAsLink)
        {
            int bdCount = presetIndices != null ? presetIndices.Count : 0;

            // 1-line header: name on the left, "+ Binding" right-aligned.
            var headerRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float buttonW = 80f;
            const float buttonGap = 4f;
            float labelMaxW = headerRect.width - buttonW - buttonGap;
            var labelRect = new Rect(headerRect.x, headerRect.y, labelMaxW, headerRect.height);
            var addRect = new Rect(headerRect.x + headerRect.width - buttonW,
                                   headerRect.y, buttonW, headerRect.height);

            // Header label. Link style for wired GameObjects (clickable to ping
            // via SyncScene resolution); plain for Shared / Orphan groups.
            if (showAsLink)
            {
                if (GUI.Button(labelRect, headerLabel, EditorStyles.linkLabel))
                {
                    var resolved = ResolvePathInSceneScoped("", headerLabel);
                    if (resolved != null)
                    {
                        Selection.activeGameObject = resolved;
                        EditorGUIUtility.PingObject(resolved);
                    }
                }
            }
            else
            {
                GUI.Label(labelRect, headerLabel, EditorStyles.miniBoldLabel);
            }

            if (allowAdd &&
                GUI.Button(addRect, new GUIContent("+ Binding", addTooltip), EditorStyles.miniButton))
            {
                bindingsProp.arraySize++;
                var newProp = bindingsProp.GetArrayElementAtIndex(bindingsProp.arraySize - 1);
                InitNewPreset(newProp, ownerForAdd);
                // Pre-expand the new preset so the user sees the editor right
                // away (otherwise they have to hunt for the new collapsed row).
                var idProp = newProp.FindPropertyRelative("_id");
                if (idProp != null && !string.IsNullOrEmpty(idProp.stringValue))
                    _bindingExpanded[idProp.stringValue] = true;
            }

            // Existing presets — one compact summary row each.
            if (bdCount > 0)
            {
                foreach (var idx in presetIndices)
                {
                    int del = DrawCompactBindingRow(bindingsProp, idx, ownerForAdd);
                    if (del >= 0) pendingDelete = del;
                }
            }
            return pendingDelete;
        }

        /// <summary>
        /// Compact 1-line preset summary with collapse arrow + delete. Click
        /// the arrow or summary to expand the inline editor. Indent matches
        /// the visual nesting under the wired-object header.
        /// </summary>
        private int DrawCompactBindingRow(SerializedProperty bindingsProp, int presetIndex,
            string contextOwnerName)
        {
            var bp = bindingsProp.GetArrayElementAtIndex(presetIndex);
            var idProp = bp.FindPropertyRelative("_id");
            if (idProp != null && string.IsNullOrEmpty(idProp.stringValue))
                idProp.stringValue = System.Guid.NewGuid().ToString("N");
            string presetId = idProp != null ? idProp.stringValue : "";

            bool expanded = !string.IsNullOrEmpty(presetId) &&
                _bindingExpanded.TryGetValue(presetId, out var e) && e;

            var srcProp = bp.FindPropertyRelative("sourceProperty");
            var outProp = bp.FindPropertyRelative("outputParameter");
            string summary = $"#{presetIndex} {(BindingSourceProperty)srcProp.enumValueIndex} → " +
                             $"{(BindingOutputParameter)outProp.enumValueIndex}";

            int del = -1;

            // Manual rect so we can place arrow / label / delete on a single
            // tight line (EditorGUI.indentLevel adds significant padding).
            var rowRect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            const float indentX = 16f;
            const float arrowW = 14f;
            const float delW = 22f;
            var arrowRect = new Rect(rowRect.x + indentX, rowRect.y, arrowW, rowRect.height);
            var summaryRect = new Rect(rowRect.x + indentX + arrowW, rowRect.y,
                                       rowRect.width - indentX - arrowW - delW, rowRect.height);
            var deleteRect = new Rect(rowRect.x + rowRect.width - delW, rowRect.y, delW, rowRect.height);

            string arrow = expanded ? "▼" : "▶";
            if (GUI.Button(arrowRect, arrow, EditorStyles.label))
            {
                expanded = !expanded;
                if (!string.IsNullOrEmpty(presetId)) _bindingExpanded[presetId] = expanded;
            }
            if (GUI.Button(summaryRect, summary, EditorStyles.miniLabel))
            {
                expanded = !expanded;
                if (!string.IsNullOrEmpty(presetId)) _bindingExpanded[presetId] = expanded;
            }
            if (GUI.Button(deleteRect, "−", EditorStyles.miniButton))
                del = presetIndex;

            if (expanded)
            {
                // Render the editor body at one indent level deeper. We use
                // the existing DrawSinglePresetBox; its own header row is a
                // duplicate of the compact summary, but it costs only one
                // line and gives the user the delete button + index hint at
                // a glance.
                EditorGUI.indentLevel++;
                int innerDel = DrawSinglePresetBox(bindingsProp, presetIndex, contextOwnerName);
                if (innerDel >= 0) del = innerDel;
                EditorGUI.indentLevel--;
            }

            return del;
        }

        /// <summary>
        /// Owner-aware variant of <see cref="ResolvePathInScene"/>. When
        /// <paramref name="contextOwnerName"/> is non-empty, the matching
        /// wired GameObject is tried first as the resolution root — fixes
        /// the "preset under PawnController resolves to FlatSphere" bug
        /// where alphabetical scan order picked the wrong root.
        /// </summary>
        private GameObject ResolvePathInSceneScoped(string path, string contextOwnerName)
        {
            var candidates = new List<Transform>();
            if (_triggersByEntry.TryGetValue(_selectedEntryIndex, out var infos))
            {
                // Pass 1: prefer the trigger whose object name matches the context owner.
                if (!string.IsNullOrEmpty(contextOwnerName))
                {
                    foreach (var info in infos)
                    {
                        if (info.trigger == null) continue;
                        if (info.trigger.gameObject.name == contextOwnerName)
                            candidates.Add(info.trigger.transform);
                    }
                }
                // Pass 2: every other wired trigger as fallback.
                foreach (var info in infos)
                {
                    if (info.trigger == null) continue;
                    if (!string.IsNullOrEmpty(contextOwnerName) &&
                        info.trigger.gameObject.name == contextOwnerName) continue;
                    candidates.Add(info.trigger.transform);
                }
            }
            if (Selection.activeGameObject != null)
                candidates.Add(Selection.activeGameObject.transform);

            foreach (var root in candidates)
            {
                if (root == null) continue;
                if (string.IsNullOrEmpty(path) || path == ".") return root.gameObject;
                var child = root.Find(path);
                if (child != null) return child.gameObject;
            }
            return null;
        }

        /// <summary>Owner-aware variant of <see cref="DrawSourcePathPingRow"/>.</summary>
        private void DrawSourcePathPingRowScoped(SerializedProperty pathProp, string contextOwnerName)
        {
            string path = pathProp.stringValue;
            var resolved = ResolvePathInSceneScoped(path, contextOwnerName);

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelW = EditorGUIUtility.labelWidth;
            var labelRect = new Rect(rect.x, rect.y, labelW, rect.height);
            var valueRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(" → resolves to",
                "Source Path がどの GameObject に解決されるか。" +
                "親グループ (owner) のトリガーオブジェクトを優先。"));

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
                    string.IsNullOrEmpty(path) ? "(self)" : "(not resolvable — no matching target in scene)",
                    EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
            }
        }

        /// <summary>
        /// Scoped variant of <see cref="DrawLinkedBindingsRow"/> — only lists
        /// linked components on the wired GameObject named
        /// <paramref name="contextOwnerName"/> (or all if empty/null).
        /// </summary>
        private void DrawLinkedBindingsRowScoped(string presetId, string contextOwnerName)
        {
            var matches = new List<HapbeatParameterBinding>();
            var all = FindObjectsByType<HapbeatParameterBinding>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in all)
            {
                if (b == null) continue;
                if (!ReferenceEquals(b.LinkedEventMap, _selectedMap)) continue;
                if (b.LinkedBindingId != presetId) continue;
                if (!string.IsNullOrEmpty(contextOwnerName))
                {
                    // Restrict to the binding component whose nearest enclosing
                    // wired trigger object matches the context owner. We use the
                    // GameObject's name walking up the hierarchy — practical
                    // enough for typical scene layouts (binding lives on the
                    // trigger root or its child).
                    bool ownerMatch = false;
                    Transform t = b.transform;
                    while (t != null)
                    {
                        if (t.gameObject.name == contextOwnerName) { ownerMatch = true; break; }
                        t = t.parent;
                    }
                    if (!ownerMatch) continue;
                }
                matches.Add(b);
            }

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelW = EditorGUIUtility.labelWidth;
            var labelRect = new Rect(rect.x, rect.y, labelW, rect.height);
            var valueRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(" ↳ linked to",
                "シーン上で この preset にリンクされている HapbeatParameterBinding 一覧。"));

            if (matches.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.LabelField(valueRect, "(none — not yet synced)", EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
                return;
            }

            var first = matches[0];
            string firstName = first != null ? first.gameObject.name : "(missing)";
            string label = matches.Count == 1
                ? firstName
                : $"{firstName}  +{matches.Count - 1} more";

            if (GUI.Button(valueRect, label, EditorStyles.linkLabel))
            {
                Selection.activeGameObject = first.gameObject;
                EditorGUIUtility.PingObject(first.gameObject);
            }
        }

        private void DrawBindingsList(SerializedProperty entryProp)
        {
            var bindingsProp = entryProp.FindPropertyRelative("bindings");

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Parameter Bindings", EditorStyles.miniBoldLabel);
            GUILayout.FlexibleSpace();
            // Manual sync escape hatch — the per-frame path-change detector
            // below already handles drag/drop/text edits, but this lets the
            // user force a re-scan after editing scene triggers, etc.
            if (bindingsProp.arraySize > 0 &&
                GUILayout.Button(new GUIContent("Sync Scene", "シーン上のトリガー対象に preset を反映 / link"),
                    EditorStyles.miniButton, GUILayout.Width(80)))
            {
                int idx = _selectedEntryIndex;
                EditorApplication.delayCall += () =>
                {
                    SyncLinkedBindingsForEntry(idx);
                    UpdateSyncedPathCache(idx);
                };
            }
            EditorGUILayout.EndHorizontal();

            // Wider label for binding fields (80 → 95)
            float prevLabel = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 95;

            // Build the wired-object list (alphabetically) and a name -> events map.
            var wiredNames = new List<string>();
            var eventsByObject = new Dictionary<string, List<string>>();
            if (_triggersByEntry.TryGetValue(_selectedEntryIndex, out var infos))
            {
                var seenNames = new HashSet<string>();
                foreach (var info in infos)
                {
                    if (info.trigger == null) continue;
                    string nm = info.trigger.gameObject.name;
                    if (seenNames.Add(nm)) wiredNames.Add(nm);
                    if (!eventsByObject.TryGetValue(nm, out var evs))
                        eventsByObject[nm] = evs = new List<string>();
                    if (info.wiredEvents != null) evs.AddRange(info.wiredEvents);
                }
                foreach (var kv in eventsByObject)
                    if (kv.Value.Count == 0) kv.Value.Add("(manual)");
            }
            wiredNames.Sort();

            // Group preset indices by ownerObjectName for fast lookup per group.
            var presetIndicesByOwner = new Dictionary<string, List<int>>();
            for (int j = 0; j < bindingsProp.arraySize; j++)
            {
                var bpJ = bindingsProp.GetArrayElementAtIndex(j);
                var idPropJ = bpJ.FindPropertyRelative("_id");
                if (idPropJ != null && string.IsNullOrEmpty(idPropJ.stringValue))
                    idPropJ.stringValue = System.Guid.NewGuid().ToString("N");
                var ownerPropJ = bpJ.FindPropertyRelative("ownerObjectName");
                string ow = ownerPropJ != null ? (ownerPropJ.stringValue ?? "") : "";
                if (!presetIndicesByOwner.TryGetValue(ow, out var list))
                    presetIndicesByOwner[ow] = list = new List<int>();
                list.Add(j);
            }

            int pendingDelete = -1;

            // 1. Foldout per wired GameObject.
            foreach (var n in wiredNames)
            {
                eventsByObject.TryGetValue(n, out var evs);
                presetIndicesByOwner.TryGetValue(n, out var indices);
                pendingDelete = DrawWiredObjectFoldout(bindingsProp, n, evs, indices, pendingDelete);
                presetIndicesByOwner.Remove(n);
            }

            // 2. Shared group (no owner).
            bool hasShared = presetIndicesByOwner.TryGetValue("", out var sharedIndices)
                             && sharedIndices.Count > 0;
            if (hasShared || wiredNames.Count == 0)
            {
                pendingDelete = DrawSpecialGroupFoldout(bindingsProp,
                    "shared", "Shared (all wired)", ownerForAdd: "",
                    presetIndices: hasShared ? sharedIndices : null,
                    pendingDelete: pendingDelete, allowAdd: true);
                presetIndicesByOwner.Remove("");
            }

            // 3. Orphan groups - owner set but no wired trigger matches.
            foreach (var kv in presetIndicesByOwner)
            {
                pendingDelete = DrawSpecialGroupFoldout(bindingsProp,
                    "orphan|" + kv.Key,
                    "[orphan] " + kv.Key + " (no wired trigger)",
                    ownerForAdd: kv.Key,
                    presetIndices: kv.Value,
                    pendingDelete: pendingDelete, allowAdd: false);
            }

            // Deferred deletion after the loop to avoid GUI layout mismatch
            if (pendingDelete >= 0 && pendingDelete < bindingsProp.arraySize)
            {
                // 削除前に linked scene component を destroy しておく (EventMap = single
                // source of truth 方針: preset 消したら scene 側も巻き込む)。
                // standalone として残したい場合は事前に component 側で Unlink 必要。
                // Undo (Ctrl+Z) で preset + component 同時復活可能。
                DestroyLinkedComponentsForPreset(bindingsProp.GetArrayElementAtIndex(pendingDelete));
                bindingsProp.DeleteArrayElementAtIndex(pendingDelete);
            }

            EditorGUIUtility.labelWidth = prevLabel;

            // Detect path changes between draws and schedule a deferred sync.
            // We can't sync inline because (a) it mutates the scene which
            // disrupts the IMGUI layout and (b) text-field edits aren't applied
            // to the entry asset until ApplyModifiedProperties (called by the
            // outer Inspector flow). delayCall runs after IMGUI completes.
            int entryIdx = _selectedEntryIndex;
            bool needsSync = false;
            for (int i = 0; i < bindingsProp.arraySize; i++)
            {
                var bp = bindingsProp.GetArrayElementAtIndex(i);
                var idProp = bp.FindPropertyRelative("_id");
                var pathProp = bp.FindPropertyRelative("sourceTransformPath");
                var ownerPropChk = bp.FindPropertyRelative("ownerObjectName");
                var srcPropChk = bp.FindPropertyRelative("sourceProperty");
                if (idProp == null || pathProp == null) continue;
                string id = idProp.stringValue;
                string path = pathProp.stringValue ?? "";
                string owner = ownerPropChk != null ? (ownerPropChk.stringValue ?? "") : "";
                // SourceProperty も key に含める。SliderValue / External 等への
                // 切替で target 解決ロジック (_sourceSlider auto-wire 等) が
                // 変わるため、Property 変更でも sync を走らせる必要がある。
                int srcEnum = srcPropChk != null ? srcPropChk.enumValueIndex : -1;
                string key = $"{owner}|{path}|{srcEnum}";
                if (string.IsNullOrEmpty(id)) continue;
                if (!_lastSyncedPathByPresetId.TryGetValue(id, out var prev) || prev != key)
                {
                    needsSync = true;
                    break;
                }
            }
            if (needsSync)
            {
                EditorApplication.delayCall += () =>
                {
                    SyncLinkedBindingsForEntry(entryIdx);
                    UpdateSyncedPathCache(entryIdx);
                };
            }
        }

        /// <summary>
        /// Refresh the per-preset path cache after a sync run so subsequent
        /// draws don't mistake an unchanged path for "needs sync" again.
        /// </summary>
        /// <summary>
        /// 指定 preset (SerializedProperty) の id に link されている scene 上の
        /// HapbeatParameterBinding を全て探索して Undo.DestroyObjectImmediate する。
        /// preset 削除と同期して scene 側も clean に保つ用 (EventMap → scene の一方向同期)。
        /// </summary>
        private void DestroyLinkedComponentsForPreset(SerializedProperty presetProp)
        {
            if (presetProp == null) return;
            var idProp = presetProp.FindPropertyRelative("_id");
            if (idProp == null) return;
            string presetId = idProp.stringValue;
            if (string.IsNullOrEmpty(presetId)) return;

            int destroyed = 0;
            // scene 全体の HapbeatParameterBinding を walk (inactive 含む)
            var allBindings = UnityEngine.Object.FindObjectsByType<HapbeatParameterBinding>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in allBindings)
            {
                if (b == null) continue;
                if (!ReferenceEquals(b.LinkedEventMap, _selectedMap)) continue;
                if (b.LinkedBindingId != presetId) continue;
                Undo.DestroyObjectImmediate(b);
                destroyed++;
            }
            if (destroyed > 0)
                Debug.Log($"[Hapbeat] Preset 削除に伴い scene 上の linked binding component {destroyed} 個を destroy しました (Ctrl+Z で復活可)。");
        }

        private void UpdateSyncedPathCache(int entryIdx)
        {
            if (_selectedMap == null) return;
            if (entryIdx < 0 || entryIdx >= _selectedMap.entries.Count) return;
            var entry = _selectedMap.entries[entryIdx];
            if (entry == null || entry.bindings == null) return;
            foreach (var p in entry.bindings)
            {
                if (p == null || string.IsNullOrEmpty(p.id)) continue;
                // Match the change-detector key format (owner|path|sourceProperty)
                // so the cache hit reasoning in DrawBindingsList stays in sync.
                string owner = p.ownerObjectName ?? "";
                string path = p.sourceTransformPath ?? "";
                int srcEnum = (int)p.sourceProperty;
                _lastSyncedPathByPresetId[p.id] = $"{owner}|{path}|{srcEnum}";
            }
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
        private void DrawSourcePathWithDragDrop(SerializedProperty pathProp)
        {
            var rect = EditorGUILayout.GetControlRect();
            float labelW = EditorGUIUtility.labelWidth;
            float pickerW = 22;

            EditorGUI.LabelField(new Rect(rect.x, rect.y, labelW, rect.height),
                new GUIContent("Source Object",
                    "\u30d1\u30e9\u30e1\u30fc\u30bf\u306e\u53c2\u7167\u5143 GameObject\u3002\n" +
                    "\u5024 (= \u5185\u90e8\u306e sourceTransformPath) \u306f wired GameObject (owner) \u304b\u3089\u306e\u76f8\u5bfe\u30d1\u30b9\u3067\u4fdd\u5b58\u3002\n\n" +
                    "Empty / '.' = owner \u81ea\u8eab\n" +
                    "'Visual' = owner \u306e\u5b50 'Visual'\n" +
                    "'Body/Head' = owner \u306e\u5165\u308c\u5b50 'Body/Head'\n\n" +
                    "Drag a GameObject here or use the \u25ce picker button.\n" +
                    "(owner \u306e\u5b50\u5b6b\u3092 drop \u3059\u308b\u3068\u76f8\u5bfe\u30d1\u30b9\u304c\u8a08\u7b97\u3055\u308c\u3001\u305d\u308c\u4ee5\u5916\u306f\u540d\u524d\u306e\u307f\u304c\u5165\u308a\u307e\u3059\u3002)"));

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

        /// <summary>
        /// Accepts a drag&drop of a GameObject into the sourceTransformPath field.
        /// <paramref name="zone"/> identifies which hit-test rect matched (for diagnostics).
        /// </summary>
        private void HandlePathDragDrop(Rect dropRect, SerializedProperty pathProp, string zone)
        {
            var e = Event.current;

            // Only process drag events
            if (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)
                return;

            // Guard: if another handler already consumed this drag frame, skip.
            if (e.type == EventType.Used) return;

            bool inside = dropRect.Contains(e.mousePosition);
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
        /// Draws a one-line summary of HapbeatParameterBinding components in
        /// the scene that are currently linked to the preset with id
        /// <paramref name="presetId"/>. Lets the user click a name to ping
        /// the GameObject, or notice when nothing is linked yet.
        /// </summary>
        private void DrawLinkedBindingsRow(string presetId)
        {
            // Find every binding in scene linked to this preset id.
            var matches = new List<HapbeatParameterBinding>();
            var all = FindObjectsByType<HapbeatParameterBinding>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            foreach (var b in all)
            {
                if (b == null) continue;
                if (ReferenceEquals(b.LinkedEventMap, _selectedMap) &&
                    b.LinkedBindingId == presetId)
                {
                    matches.Add(b);
                }
            }

            var rect = EditorGUILayout.GetControlRect(false, EditorGUIUtility.singleLineHeight);
            float labelW = EditorGUIUtility.labelWidth;
            var labelRect = new Rect(rect.x, rect.y, labelW, rect.height);
            var valueRect = new Rect(rect.x + labelW, rect.y, rect.width - labelW, rect.height);

            EditorGUI.LabelField(labelRect, new GUIContent(" ↳ linked to",
                "シーン上で この preset にリンクされている HapbeatParameterBinding。" +
                "Source Path 設定後に自動アタッチされます。クリックで ping。"));

            if (matches.Count == 0)
            {
                EditorGUI.BeginDisabledGroup(true);
                EditorGUI.LabelField(valueRect, "(none — Source Path 未解決 / 未同期)", EditorStyles.miniLabel);
                EditorGUI.EndDisabledGroup();
                return;
            }

            // Render the first match as a clickable link, rest as count if any.
            var first = matches[0];
            string firstName = first != null ? first.gameObject.name : "(missing)";
            string label = matches.Count == 1
                ? firstName
                : $"{firstName}  +{matches.Count - 1} more";

            if (GUI.Button(valueRect, label, EditorStyles.linkLabel))
            {
                Selection.activeGameObject = first.gameObject;
                EditorGUIUtility.PingObject(first.gameObject);
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

        // =====================================================================
        // Auto-attach: ensure scene-side HapbeatParameterBinding components
        // exist and are linked for every BindingPreset under the entry.
        //
        // Workflow goal: the user authors bindings ENTIRELY from the EventMap
        // window — they pick a Source Path on a preset and the SDK takes care
        // of attaching/upgrading/linking the matching component on every
        // GameObject driven by the entry. No "Add Component" round trip.
        // =====================================================================

        /// <summary>
        /// For each preset on <paramref name="entry"/> with a resolvable
        /// sourceTransformPath, make sure every trigger GameObject bound to the
        /// entry has a <see cref="HapbeatParameterBinding"/> component linked
        /// to that preset on the resolved Transform.
        ///
        /// Reuses an existing unlinked binding (matching sourceProperty +
        /// outputParameter) on the target by upgrading it; otherwise adds a
        /// new linked component. Idempotent — calling it after the targets
        /// are already in sync is a no-op.
        /// </summary>
        private void SyncLinkedBindingsForEntry(int entryIdx)
        {
            if (_selectedMap == null) return;
            if (entryIdx < 0 || entryIdx >= _selectedMap.entries.Count) return;
            var entry = _selectedMap.entries[entryIdx];
            if (entry == null || entry.bindings == null || entry.bindings.Count == 0) return;
            if (!_triggersByEntry.TryGetValue(entryIdx, out var triggers) || triggers == null)
                return;

            int attached = 0, upgraded = 0, unlinked = 0;
            foreach (var preset in entry.bindings)
            {
                if (preset == null || string.IsNullOrEmpty(preset.id)) continue;

                // この preset が「居るべき」target GO の集合を先に決める
                // (= ownerObjectName scope を通過し、sourceTransformPath が解決できた先)
                var validTargets = new System.Collections.Generic.HashSet<Transform>();
                foreach (var info in triggers)
                {
                    if (info.trigger == null) continue;
                    if (!string.IsNullOrEmpty(preset.ownerObjectName) &&
                        info.trigger.gameObject.name != preset.ownerObjectName) continue;
                    var t = ResolvePresetPath(info.trigger.transform, preset.sourceTransformPath);
                    if (t != null) validTargets.Add(t);
                }

                // First pass: validTargets 以外に居る同じ preset id への link を片付ける。
                // ownerObjectName scope 外 + sourceTransformPath 変更で「居場所が
                // 変わった」場合の両方を 1 ループでカバー。
                // linker が auto-attach した orphan を残すと Showcase で重複表示
                // されて混乱の元なので、destroy する (Undo で復活可)。
                foreach (var info in triggers)
                {
                    if (info.trigger == null) continue;
                    foreach (var b in info.trigger.GetComponentsInChildren<HapbeatParameterBinding>(true))
                    {
                        if (b == null) continue;
                        if (!ReferenceEquals(b.LinkedEventMap, _selectedMap)) continue;
                        if (b.LinkedBindingId != preset.id) continue;
                        if (validTargets.Contains(b.transform)) continue; // ok、現在の正しい位置

                        // 古い位置の link は剥がして component ごと destroy。
                        // (Undo.DestroyObjectImmediate で Ctrl+Z で復活可)
                        Undo.DestroyObjectImmediate(b);
                        unlinked++;
                    }
                }

                foreach (var info in triggers)
                {
                    if (info.trigger == null) continue;
                    // ownerObjectName filter: when non-empty, only attach to
                    // the wired GameObject whose name matches. Empty = shared
                    // across every wired trigger (legacy behaviour).
                    if (!string.IsNullOrEmpty(preset.ownerObjectName) &&
                        info.trigger.gameObject.name != preset.ownerObjectName)
                    {
                        continue;
                    }
                    Transform root = info.trigger.transform;
                    Transform target = ResolvePresetPath(root, preset.sourceTransformPath);
                    if (target == null) continue; // path not resolvable on this trigger; skip silently

                    var existing = target.GetComponents<HapbeatParameterBinding>();

                    // 1. Already linked? その binding は新規 attach 不要だが、
                    //    preset.sourceProperty 変更 (e.g. → SliderValue) に追従して
                    //    _sourceSlider 等のソース参照は refresh しておく。
                    HapbeatParameterBinding alreadyLinkedBinding = null;
                    foreach (var b in existing)
                    {
                        if (b == null) continue;
                        if (ReferenceEquals(b.LinkedEventMap, _selectedMap) &&
                            b.LinkedBindingId == preset.id)
                        {
                            alreadyLinkedBinding = b;
                            break;
                        }
                    }
                    if (alreadyLinkedBinding != null)
                    {
                        // Source 参照を preset 設定に合わせて再 wire (idempotent)
                        var refreshSo = new SerializedObject(alreadyLinkedBinding);
                        refreshSo.FindProperty("_sourceTransform").objectReferenceValue = target;
                        refreshSo.FindProperty("_sourceProperty").enumValueIndex = (int)preset.sourceProperty;
                        refreshSo.FindProperty("_outputParameter").enumValueIndex = (int)preset.outputParameter;
                        if (preset.sourceProperty == BindingSourceProperty.SliderValue)
                        {
                            var slider = target.GetComponent<UnityEngine.UI.Slider>();
                            refreshSo.FindProperty("_sourceSlider").objectReferenceValue = slider;
                        }
                        refreshSo.ApplyModifiedProperties();
                        EditorUtility.SetDirty(alreadyLinkedBinding);
                        continue;
                    }

                    // 2. Find an unlinked binding to upgrade (matching property/output).
                    HapbeatParameterBinding upgradeCandidate = null;
                    foreach (var b in existing)
                    {
                        if (b == null) continue;
                        if (b.LinkedEventMap != null) continue;
                        if (!string.IsNullOrEmpty(b.LinkedBindingId)) continue;
                        var bso = new SerializedObject(b);
                        var spProp = bso.FindProperty("_sourceProperty");
                        var opProp = bso.FindProperty("_outputParameter");
                        if (spProp == null || opProp == null) continue;
                        if (spProp.enumValueIndex == (int)preset.sourceProperty &&
                            opProp.enumValueIndex == (int)preset.outputParameter)
                        {
                            upgradeCandidate = b;
                            break;
                        }
                    }

                    HapbeatParameterBinding binding;
                    if (upgradeCandidate != null)
                    {
                        Undo.RecordObject(upgradeCandidate, "Link Hapbeat Binding");
                        binding = upgradeCandidate;
                        upgraded++;
                    }
                    else
                    {
                        binding = Undo.AddComponent<HapbeatParameterBinding>(target.gameObject);
                        attached++;
                    }

                    var so = new SerializedObject(binding);
                    so.FindProperty("_linkedEventMap").objectReferenceValue = _selectedMap;
                    so.FindProperty("_linkedBindingId").stringValue = preset.id;
                    so.FindProperty("_sourceTransform").objectReferenceValue = target;
                    // Source / output mode mirror the preset so the runtime
                    // pre-link evaluation works even if the binding was just
                    // created (preset values take over the moment _linkedBindingId
                    // resolves, but matching the local fields keeps the Inspector
                    // consistent and protects against transient unlink states).
                    so.FindProperty("_sourceProperty").enumValueIndex = (int)preset.sourceProperty;
                    so.FindProperty("_outputParameter").enumValueIndex = (int)preset.outputParameter;
                    // SliderValue source の場合、target GO の Slider component を
                    // _sourceSlider に auto-wire (preset.sourceTransformPath が
                    // Slider component を持つ GO を指している前提)。
                    if (preset.sourceProperty == BindingSourceProperty.SliderValue)
                    {
                        var slider = target.GetComponent<UnityEngine.UI.Slider>();
                        so.FindProperty("_sourceSlider").objectReferenceValue = slider;
                    }
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(binding);
                }
            }

            if (attached > 0 || upgraded > 0 || unlinked > 0)
            {
                Debug.Log($"[Hapbeat] Sync bindings for entry '{entry.displayName}': " +
                          $"{attached} new, {upgraded} upgraded, {unlinked} unlinked (out of scope).");
                Repaint();
            }
        }

        /// <summary>
        /// Resolve a preset's <c>sourceTransformPath</c> against a trigger root.
        /// Empty / "." returns the root itself (self).
        /// </summary>
        private static Transform ResolvePresetPath(Transform root, string path)
        {
            if (root == null) return null;
            if (string.IsNullOrEmpty(path) || path == ".") return root;
            return root.Find(path);
        }

        /// <summary>
        /// Compute a relative path from the currently-selected Hierarchy object to the dropped one.
        /// Falls back to the dropped object's name if no ancestor match is found.
        /// </summary>
        private string ComputeRelativePath(GameObject dropped)
        {
            // Candidate roots, in priority order:
            //   1. GameObjects of triggers bound to the currently selected entry.
            //      This is the natural root because the resulting path will be
            //      resolved against the SAME triggers at runtime by the binding.
            //      It also handles "user drops the trigger object itself" → "" (self),
            //      which previously fell through to the name-only fallback and
            //      produced a "not resolvable" preset.
            //   2. The currently selected Hierarchy object — for the case where
            //      the entry has no triggers yet but the user is staging a path
            //      against a known scene root.
            //   3. Fallback: dropped.name (last resort, often "not resolvable"
            //      but better than nothing).
            var candidateRoots = new List<Transform>();
            if (_selectedEntryIndex >= 0 &&
                _triggersByEntry.TryGetValue(_selectedEntryIndex, out var infos))
            {
                foreach (var info in infos)
                {
                    if (info.trigger != null && info.trigger.transform != null)
                        candidateRoots.Add(info.trigger.transform);
                }
            }
            GameObject selected = Selection.activeGameObject;
            if (selected != null) candidateRoots.Add(selected.transform);

            foreach (var root in candidateRoots)
            {
                if (root == null) continue;
                if (root.gameObject == dropped) return ""; // self
                Transform cursor = dropped.transform;
                var segments = new List<string>();
                while (cursor != null && cursor != root)
                {
                    segments.Insert(0, cursor.name);
                    cursor = cursor.parent;
                }
                if (cursor == root)
                    return string.Join("/", segments);
            }

            // Last-resort fallback (will likely show "not resolvable" — at
            // least the path is non-empty so the user can edit it manually).
            return dropped.name;
        }

        // ── Event ID dropdown (Command mode) ─────────────────────────────────
        // Kit manifest scanning was consolidated into HapbeatManifestIntensity;
        // this window now consumes HapbeatManifestIntensity.LoadAllEvents()
        // and HapbeatManifestIntensity.GetKitDirectoryNames() instead of
        // owning a duplicate scanner / parser.

        /// <summary>
        /// Adds a "From Kit ▾" dropdown below the Event ID row that lists every
        /// command-mode event found in <c>HapbeatSDK/Kits/&lt;kit&gt;/&lt;kit&gt;-manifest.json</c>.
        /// Picking an entry splits it into category + name and writes both fields.
        /// </summary>
        private static void DrawKitEventIdDropdown(
            SerializedProperty categoryProp,
            SerializedProperty eventNameProp,
            SerializedObject so)
        {
            var events = HapbeatManifestIntensity.LoadAllEvents()
                .Where(e => e.mode == "command")
                .ToList();

            string curId = BuildEventId(categoryProp.stringValue, eventNameProp.stringValue);

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUIUtility.labelWidth);

            GUILayout.Label(
                events.Count == 0
                    ? "No Kit events found. Deploy a Kit from Studio."
                    : $"{events.Count} event(s) from {events.Select(e => e.kitName).Distinct().Count()} kit(s)",
                EditorStyles.miniLabel);
            GUILayout.FlexibleSpace();

            // Use EditorStyles.popup so the chevron shape matches Unity's
            // standard dropdown (same as the Mode field). The chevron is
            // rendered by the style \u2014 don't include "\u25be" in the label.
            if (GUILayout.Button("From Kit", EditorStyles.popup, GUILayout.Width(80)))
            {
                var menu = new GenericMenu();
                if (events.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent("No Kit manifests found"));
                    menu.AddSeparator("");
                    menu.AddItem(new GUIContent("Open HapbeatSDK/Kits folder"), false, () =>
                        RevealKitSubfolder(""));
                }
                else
                {
                    foreach (var group in events.GroupBy(e => e.kitName).OrderBy(g => g.Key))
                    {
                        foreach (var ev in group.OrderBy(e => e.eventId))
                        {
                            string menuPath = $"{group.Key}/{ev.eventId}";
                            bool isCurrent = ev.eventId == curId;
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
                    menu.AddItem(new GUIContent("Refresh"), false,
                        HapbeatManifestIntensity.Invalidate);
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
                Tr("Create HapbeatSDK Folder?", "HapbeatSDK フォルダを作成しますか？"),
                Tr(
                    "HapbeatSDK folder not found.\n\n" +
                    "This is the user-owned area where Hapbeat Studio exports Kits and the\n" +
                    "SDK generates Scenes / EventMaps for sample builds.\n" +
                    $"Layout: {HapbeatSDKFolderCreator.kSdkRoot}/{{Kits, Scenes, EventMaps}}\n" +
                    "(A HapbeatKitsReadme marker is placed inside Kits/.)\n" +
                    "You can move or rename the folder afterwards — the marker tracks Kits.\n\n" +
                    "Create it now?",

                    "HapbeatSDK フォルダがまだ見つかりません。\n\n" +
                    "ここは Hapbeat Studio が Kit を書き出し、SDK がシーン / EventMap を\n" +
                    "生成するユーザー領域です。\n" +
                    $"レイアウト: {HapbeatSDKFolderCreator.kSdkRoot}/{{Kits, Scenes, EventMaps}}\n" +
                    "(Kits/ 内に HapbeatKitsReadme マーカーが置かれます。)\n" +
                    "後で好きな場所にフォルダごと移動してかまいません — マーカーで追跡します。\n\n" +
                    "いま作成しますか？"),
                Tr("Create", "作成する"),
                Tr("Cancel", "キャンセル"));
            if (!confirmed) return null;

            HapbeatSDKFolderCreator.EnsureLayout(verbose: false);
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
            // dropW: width of the kit-name dropdown button to the right of
            // the category text field. The chevron icon is manually centred
            // (see DrawCenteredChevronButton) so this width affects only
            // the button's hit area, not the chevron position.
            float dropW = 22;
            float fieldStart = lineRect.x + labelW + 2;
            float totalFieldW = lineRect.width - labelW - 2;
            float catW = totalFieldW * 0.4f - dropW;
            float nameW = totalFieldW * 0.6f;
            // (dropW set below in the Category dropdown section once we know
            // we're rendering with popup style.)

            // Label
            EditorGUI.LabelField(new Rect(lineRect.x, lineRect.y, labelW, lineRect.height),
                new GUIContent("Event ID", "Composed as category.name. Sent to devices."));

            // Category text field — category = kit name by convention.
            // Auto-attach the matching manifest when the user changes it.
            string prevCategory = categoryProp.stringValue;
            categoryProp.stringValue = DrawPlaceholderRect(
                new Rect(fieldStart, lineRect.y, catW, lineRect.height),
                categoryProp.stringValue, "kit-name");
            bool categoryChanged = prevCategory != categoryProp.stringValue;

            // Category dropdown — populated from HapbeatSDK/Kits/ direct
            // subfolders so the user picks an actual installed Kit name
            // (not a legacy hardcoded list). EditorStyles.popup right-anchors
            // its chevron (looks off-center on a narrow icon button), so we
            // draw a miniButton background + a separately-centered chevron
            // icon (same image as Unity's standard popup chevron).
            var dropRect = new Rect(fieldStart + catW, lineRect.y, dropW, lineRect.height);
            if (DrawCenteredChevronButton(dropRect))
            {
                var menu = new GenericMenu();
                var kitNames = HapbeatManifestIntensity.GetKitDirectoryNames();
                if (kitNames.Count == 0)
                {
                    menu.AddDisabledItem(new GUIContent(
                        "No Kits found under Assets/HapbeatSDK/Kits/"));
                }
                else
                {
                    foreach (var name in kitNames)
                    {
                        string c = name;
                        menu.AddItem(new GUIContent(c), categoryProp.stringValue == c,
                            () =>
                            {
                                categoryProp.stringValue = c;
                                so.ApplyModifiedProperties();
                                // Re-attach manifest now that the kit changed.
                                if (_selectedMap != null &&
                                    _selectedEntryIndex >= 0 &&
                                    _selectedEntryIndex < _selectedMap.entries.Count)
                                {
                                    AutoAttachManifestForEntry(
                                        _selectedMap.entries[_selectedEntryIndex], _selectedMap);
                                }
                            });
                    }
                }
                menu.ShowAsContext();
            }

            // Event name text field
            eventNameProp.stringValue = DrawPlaceholderRect(
                new Rect(fieldStart + catW + dropW + 2, lineRect.y, nameW - 2, lineRect.height),
                eventNameProp.stringValue, "event-name");

            // Preview
            var entry = _selectedMap.entries[_selectedEntryIndex];
            string previewId = !string.IsNullOrEmpty(entry.eventId) ? entry.eventId : "kit-name.event-name";
            EditorGUI.BeginDisabledGroup(true);
            EditorGUILayout.TextField(new GUIContent(" \u2192 eventId"), previewId);
            EditorGUI.EndDisabledGroup();

            if (!string.IsNullOrEmpty(categoryProp.stringValue) && !HapbeatEventEntry.IsValidSegment(categoryProp.stringValue))
                EditorGUILayout.HelpBox("category: lowercase a-z, 0-9, -, _ only", MessageType.Warning);
            if (!string.IsNullOrEmpty(eventNameProp.stringValue) && !HapbeatEventEntry.IsValidSegment(eventNameProp.stringValue))
                EditorGUILayout.HelpBox("name: lowercase a-z, 0-9, -, _ only", MessageType.Warning);

            // If the user just edited the category (text field or dropdown),
            // auto-attach the matching Kit manifest. This complements the
            // streamClip change-handler so Command-mode entries also get
            // their intensity resolved without manual Refresh clicks.
            if (categoryChanged)
            {
                so.ApplyModifiedProperties();
                AutoAttachManifestForEntry(entry, _selectedMap);
                so.Update();
            }
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
            _stateWiringsByEntry.Clear();
            _scriptWiringsByEntry.Clear();
            _orphanedTriggers.Clear();

            if (_selectedMap == null) return;

            // ── Scene Trigger components (MonoBehaviour) ───────────────────
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
                int idx = ResolveTriggerEntryIndex(trigger.EntryId);
                AddWiring(idx, info);

                // SequenceTrigger's On Start / On Stop phases are also worth
                // surfacing in the wiring list so a user looking at an entry
                // sees every trigger that references it in ANY phase.
                if (trigger is HapbeatSequenceTrigger seq)
                {
                    int startIdx = ResolveTriggerEntryIndex(seq.OnStartEntryId);
                    int stopIdx  = ResolveTriggerEntryIndex(seq.OnStopEntryId);
                    if (startIdx != idx && startIdx >= 0) AddWiring(startIdx, info);
                    if (stopIdx != idx && stopIdx >= 0 && stopIdx != startIdx) AddWiring(stopIdx, info);
                }
            }

            // ── AnimatorController StateMachineBehaviours ───────────────────
            // HapbeatStateBehaviour lives on AnimatorController state assets,
            // not in the scene. Scan all controllers in the project so wires
            // authored in `Animator window → state → Add Behaviour` surface
            // here just like UnityEventTrigger does.
            ScanAnimatorControllers();

            // ── Script-driven wiring (heuristic SerializedField string match) ─
            // Custom MonoBehaviours that hold a `[SerializeField] private string
            // _eventName = "charge_release"` (typical ChargeShooter / game-logic
            // pattern) don't surface anywhere else. Walk all non-Hapbeat
            // MonoBehaviours and match their serialized string fields against
            // entry displayName / eventId.
            ScanScriptWirings();

            Repaint();
        }

        /// <summary>
        /// Walk every <c>AnimatorController</c> asset in the project, enumerate
        /// states in all layers (including nested state machines), and surface
        /// <see cref="HapbeatStateBehaviour"/> instances that reference the
        /// currently-selected EventMap. For each scene <c>Animator</c> using
        /// the controller, add a wiring row so the user can locate the affected
        /// GameObject(s). Asset-only (controller with no scene Animator) also
        /// gets a row with <c>animatorObject == null</c>.
        /// </summary>
        private void ScanAnimatorControllers()
        {
            // Pre-collect scene Animators once for O(controllers × animators) match.
            var sceneAnimators = FindObjectsByType<Animator>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            string[] controllerGuids = AssetDatabase.FindAssets("t:AnimatorController");
            foreach (var guid in controllerGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var ctrl = AssetDatabase.LoadAssetAtPath<UnityEditor.Animations.AnimatorController>(path);
                if (ctrl == null) continue;

                // Find scene Animators bound to THIS controller.
                var animatorsUsingCtrl = new List<GameObject>();
                foreach (var anim in sceneAnimators)
                {
                    if (anim == null) continue;
                    // runtimeAnimatorController may be an AnimatorOverrideController; resolve to its base.
                    var runtime = anim.runtimeAnimatorController;
                    var baseCtrl = runtime is UnityEngine.AnimatorOverrideController over
                        ? over.runtimeAnimatorController as UnityEditor.Animations.AnimatorController
                        : runtime as UnityEditor.Animations.AnimatorController;
                    if (baseCtrl == ctrl)
                        animatorsUsingCtrl.Add(anim.gameObject);
                }

                foreach (var layer in ctrl.layers)
                {
                    if (layer == null || layer.stateMachine == null) continue;
                    EnumerateStateBehaviours(layer.stateMachine, layer.name, ctrl, animatorsUsingCtrl);
                }
            }
        }

        /// <summary>
        /// Recursively enumerate all states in a state machine (including child
        /// state machines), check each state's behaviours for
        /// <see cref="HapbeatStateBehaviour"/>, and register matches into
        /// <see cref="_stateWiringsByEntry"/>.
        /// </summary>
        private void EnumerateStateBehaviours(
            UnityEditor.Animations.AnimatorStateMachine sm,
            string layerName,
            UnityEditor.Animations.AnimatorController ctrl,
            List<GameObject> animatorsUsingCtrl)
        {
            foreach (var stateRef in sm.states)
            {
                var state = stateRef.state;
                if (state == null) continue;

                foreach (var behaviour in state.behaviours)
                {
                    if (!(behaviour is HapbeatStateBehaviour hb)) continue;
                    if (hb.EventMap != _selectedMap) continue;

                    int enterIdx = ResolveTriggerEntryIndex(hb.EntryIdOnEnter);
                    int exitIdx  = ResolveTriggerEntryIndex(hb.EntryIdOnExit);

                    if (animatorsUsingCtrl.Count == 0)
                    {
                        // No scene Animator uses this controller — still surface
                        // it (asset-only) so the user can navigate via the
                        // controller asset link.
                        if (enterIdx >= 0) AddStateWiring(enterIdx, hb, ctrl, layerName, state.name, "Enter", null);
                        if (exitIdx  >= 0) AddStateWiring(exitIdx,  hb, ctrl, layerName, state.name, "Exit",  null);
                    }
                    else
                    {
                        foreach (var go in animatorsUsingCtrl)
                        {
                            if (enterIdx >= 0) AddStateWiring(enterIdx, hb, ctrl, layerName, state.name, "Enter", go);
                            if (exitIdx  >= 0) AddStateWiring(exitIdx,  hb, ctrl, layerName, state.name, "Exit",  go);
                        }
                    }
                }
            }

            // Recurse into nested state machines.
            foreach (var sub in sm.stateMachines)
            {
                if (sub.stateMachine != null)
                    EnumerateStateBehaviours(sub.stateMachine, layerName, ctrl, animatorsUsingCtrl);
            }
        }

        private void AddStateWiring(
            int idx,
            HapbeatStateBehaviour behaviour,
            UnityEditor.Animations.AnimatorController ctrl,
            string layerName,
            string stateName,
            string phase,
            GameObject animatorObject)
        {
            if (idx < 0 || idx >= _selectedMap.entries.Count) return;
            if (!_stateWiringsByEntry.TryGetValue(idx, out var list))
            {
                list = new List<StateWiringInfo>();
                _stateWiringsByEntry[idx] = list;
            }
            list.Add(new StateWiringInfo
            {
                behaviour = behaviour,
                controller = ctrl,
                layerName = layerName,
                stateName = stateName,
                phase = phase,
                animatorObject = animatorObject,
            });
        }

        /// <summary>
        /// Build a value-keyed lookup of EventMap entries by both displayName
        /// and eventId, so the script-field scan can match in O(1) per field.
        /// Each value maps to (entryIndex, matchType). Entries with empty
        /// names or duplicate values resolve to the first matching entry —
        /// callers can still navigate manually if they need a specific one.
        /// </summary>
        private Dictionary<string, (int idx, string matchType)> BuildEntryValueIndex()
        {
            var dict = new Dictionary<string, (int, string)>();
            for (int i = 0; i < _selectedMap.entries.Count; i++)
            {
                var e = _selectedMap.entries[i];
                if (e == null) continue;
                if (!string.IsNullOrEmpty(e.displayName) && !dict.ContainsKey(e.displayName))
                    dict[e.displayName] = (i, "displayName");
                if (!string.IsNullOrEmpty(e.eventId) && !dict.ContainsKey(e.eventId))
                    dict[e.eventId] = (i, "eventId");
            }
            return dict;
        }

        /// <summary>
        /// Scan every non-Hapbeat MonoBehaviour in open scenes and look for
        /// serialized <c>string</c> fields whose value matches an entry's
        /// displayName or eventId. Each match is recorded as a script-wiring
        /// row so the EventMap detail panel can surface "this entry is also
        /// fired from script X on GameObject Y".
        ///
        /// <para>
        /// Heuristic, not perfect: scripts that build event IDs dynamically
        /// (string.Format / runtime concat) won't be caught. False positives
        /// are possible if an unrelated string field coincidentally matches
        /// an entry name — the row shows the field name + value so the
        /// author can verify.
        /// </para>
        /// </summary>
        private void ScanScriptWirings()
        {
            var index = BuildEntryValueIndex();
            if (index.Count == 0) return;

            var allBehaviours = FindObjectsByType<MonoBehaviour>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var mb in allBehaviours)
            {
                if (mb == null) continue;
                // Skip Hapbeat's own components — they're covered by the
                // trigger / state / binding scans above. Filtering by
                // namespace catches HapbeatTriggerBase, HapbeatBridge,
                // HapbeatParameterBinding, HapbeatManager, HapbeatStatusOverlay,
                // HapbeatKeyDispatcher, HapbeatEventLogger, HapbeatActionHelper,
                // etc. in one shot.
                var type = mb.GetType();
                if (type.Namespace != null &&
                    (type.Namespace == "Hapbeat" || type.Namespace.StartsWith("Hapbeat.")))
                {
                    // Sub-namespaces like Hapbeat.Samples.Showcase.* SHOULD be
                    // scanned (ChargeShooter lives in Hapbeat.Samples.Showcase
                    // and is exactly the kind of user-facing script we want
                    // to surface). Only skip the framework namespace itself
                    // and its direct ".Editor" / ".Internal" children.
                    if (type.Namespace == "Hapbeat" ||
                        type.Namespace == "Hapbeat.Editor" ||
                        type.Namespace == "Hapbeat.Internal")
                        continue;
                }

                var so = new SerializedObject(mb);
                var iter = so.GetIterator();
                while (iter.NextVisible(true))
                {
                    if (iter.propertyType != SerializedPropertyType.String) continue;
                    string val = iter.stringValue;
                    if (string.IsNullOrEmpty(val)) continue;
                    if (!index.TryGetValue(val, out var hit)) continue;

                    AddScriptWiring(hit.idx, new ScriptWiringInfo
                    {
                        script = mb,
                        componentName = type.Name,
                        fieldName = iter.name,
                        matchedValue = val,
                        matchType = hit.matchType,
                    });
                }
            }
        }

        private void AddScriptWiring(int idx, ScriptWiringInfo info)
        {
            if (idx < 0 || idx >= _selectedMap.entries.Count) return;
            if (!_scriptWiringsByEntry.TryGetValue(idx, out var list))
            {
                list = new List<ScriptWiringInfo>();
                _scriptWiringsByEntry[idx] = list;
            }
            list.Add(info);
        }

        /// <summary>
        /// Resolve a trigger's entry index in the currently-selected map from
        /// its stable GUID. Returns -1 when the id is empty or stale.
        /// </summary>
        private int ResolveTriggerEntryIndex(string id)
        {
            if (_selectedMap == null || string.IsNullOrEmpty(id)) return -1;
            return _selectedMap.IndexOfId(id);
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
                if (comp == null || comp is HapbeatTriggerBase)
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
        /// Editor-time accessor for the global haptic latency offset stored in
        /// the project's <c>HapbeatConfig</c>. Returns 0 when no Config asset
        /// exists yet (typical for fresh projects). Used by the entry detail
        /// panel's "実効遅延" readout — the runtime path reads the same value
        /// via <c>HapbeatManager.Instance.HapticDelaySeconds</c>.
        /// </summary>
        private static float ResolveGlobalHapticDelaySeconds()
        {
            var guids = UnityEditor.AssetDatabase.FindAssets("t:HapbeatConfig");
            if (guids == null || guids.Length == 0) return 0f;
            var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
            var cfg = UnityEditor.AssetDatabase.LoadAssetAtPath<HapbeatConfig>(path);
            return cfg != null ? cfg.hapticDelaySeconds : 0f;
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

        // Cached chevron icon for the kit-category dropdown button. Unity's
        // standard popup chevron lives in a few internal icon names — pick
        // the dark variant first (Editor default theme), fall back to the
        // light name, then to null (in which case the helper draws a text
        // glyph as a final fallback).
        private static Texture _kitDropdownChevron;
        private static bool _kitDropdownChevronCached;
        private static Texture KitDropdownChevron
        {
            get
            {
                if (_kitDropdownChevronCached) return _kitDropdownChevron;
                _kitDropdownChevronCached = true;
                _kitDropdownChevron =
                    (EditorGUIUtility.IconContent("d_icon dropdown")?.image) ??
                    (EditorGUIUtility.IconContent("icon dropdown")?.image) ??
                    (EditorGUIUtility.IconContent("d_PopupCurveSwatch")?.image) ??
                    null;
                return _kitDropdownChevron;
            }
        }

        /// <summary>
        /// Draws a miniButton-sized rectangle with Unity's standard popup
        /// chevron centred both horizontally and vertically. Returns true
        /// when the user clicks. Used by the Event-ID category dropdown
        /// where the popup style's default right-anchored chevron looks
        /// off-centre on a narrow icon button.
        /// </summary>
        private static bool DrawCenteredChevronButton(Rect rect)
        {
            var e = Event.current;
            bool clicked = false;

            // Hit-testing: dispatch on MouseDown so the menu opens on press
            // (matches the timing users expect from popups).
            if (e.type == EventType.MouseDown
                && e.button == 0
                && rect.Contains(e.mousePosition))
            {
                clicked = true;
                GUI.changed = true;
                e.Use();
            }

            if (e.type == EventType.Repaint)
            {
                // Background — flat miniButton box (no built-in chevron).
                EditorStyles.miniButton.Draw(rect, false, false, false, false);

                // Chevron — manually centred.
                var chevron = KitDropdownChevron;
                if (chevron != null)
                {
                    float iw = Mathf.Min(chevron.width, rect.width - 4f);
                    float ih = Mathf.Min(chevron.height, rect.height - 4f);
                    var iconRect = new Rect(
                        rect.x + (rect.width - iw) * 0.5f,
                        rect.y + (rect.height - ih) * 0.5f,
                        iw, ih);
                    GUI.DrawTexture(iconRect, chevron, ScaleMode.ScaleToFit, true);
                }
                else
                {
                    // Fallback when no chevron icon is shipped in this Unity
                    // build — draw a Unicode triangle, centred.
                    var glyphStyle = new GUIStyle(EditorStyles.miniLabel)
                    {
                        alignment = TextAnchor.MiddleCenter,
                        fontSize = 11,
                    };
                    glyphStyle.Draw(rect, new GUIContent("▾"), 0);
                }
            }

            return clicked;
        }

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
        // Target parsing / encoding は HapbeatTargetEditorUtil に集約。
        // HapbeatManager Inspector (Test play Targeting) と仕様共有のため一本化。
        private static void ParseTarget(string target, out string prefix, out int player, out string position, out int group)
            => HapbeatTargetEditorUtil.ParseTarget(target, out prefix, out player, out position, out group);

        private static string BuildTargetFromParts(string prefix, int player, string position, int group)
            => HapbeatTargetEditorUtil.BuildTargetFromParts(prefix, player, position, group);
    }

    // ----- Project window で dirty な EventMap に ● を描画 -----
    [InitializeOnLoad]
    internal static class HapbeatEventMapProjectIndicator
    {
        static HapbeatEventMapProjectIndicator()
        {
            EditorApplication.projectWindowItemOnGUI += OnProjectGUI;
        }

        private static void OnProjectGUI(string guid, Rect rect)
        {
            if (string.IsNullOrEmpty(HapbeatEventMapWindow.DirtyEventMapGUID)) return;
            if (HapbeatEventMapWindow.DirtyEventMapGUID != guid) return;

            // asset 行の右端付近に ● を描画
            var dotRect = new Rect(rect.xMax - 14f, rect.y + 1f, 12f, 14f);
            var prev = GUI.color;
            GUI.color = new Color(1f, 0.55f, 0.3f); // オレンジ
            GUI.Label(dotRect, new GUIContent("●", "未保存の変更あり"));
            GUI.color = prev;
        }
    }
}
#endif
