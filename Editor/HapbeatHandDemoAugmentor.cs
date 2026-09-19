#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace Hapbeat.Editor
{
    /// <summary>
    /// One-shot menu command that adds the Hapbeat "XRI Hand Demo" haptic wiring
    /// on top of a scene the user imported themselves from the XR Interaction
    /// Toolkit ("Hands Interaction Demo").
    ///
    /// <para>
    /// <b>Why a tool instead of a scene.</b> The XRI samples ship under the Unity
    /// Companion License, so a modified copy of <c>HandsDemoScene.unity</c> cannot be
    /// redistributed. This command carries only the Hapbeat side of the diff — the
    /// components, field values and UnityEvent wiring — and applies it to the user's
    /// own copy of the scene. Nothing XRI-authored is contained in this package.
    /// </para>
    ///
    /// <para>
    /// <b>No XRI compile dependency.</b> This file references only Hapbeat runtime types.
    /// XRI components (<c>XRGrabInteractable</c>, <c>XRSimpleInteractable</c>) are located on
    /// the scene GameObjects by <see cref="Type.Name"/> at runtime, and their UnityEvents are
    /// edited through <see cref="SerializedObject"/> property paths, so no XRI assembly
    /// reference is needed and the package still compiles in projects without XRI.
    /// The <c>Samples~/XriHelpers</c> helper components are resolved the same way
    /// (by full type name) because they only exist after that sample is imported.
    /// </para>
    ///
    /// <para>
    /// <b>Idempotent.</b> Components that are already present and UnityEvent calls that
    /// already point at the same target/method are skipped, so running the command twice
    /// does not duplicate anything. Every mutation is registered with <see cref="Undo"/>
    /// and collapses into a single undo step.
    /// </para>
    ///
    /// <para>
    /// Prerequisites, in order:
    /// <list type="number">
    ///   <item>Import "Hands Interaction Demo" from the XR Interaction Toolkit package
    ///         and open <c>HandsDemoScene.unity</c>.</item>
    ///   <item>Import the Hapbeat SDK samples "XR Helpers" and "XRI Hand Demo (haptics add-on)"
    ///         from the Package Manager.</item>
    ///   <item>Run this command.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static class HapbeatHandDemoAugmentor
    {
        // ── EventMap entry ids (stable GUIDs inside both hand EventMaps) ─────────

        private const string EntryGrabLight      = "6a24573fe8fc487a88a93a3871cc25e7";
        private const string EntryGrabHeavy      = "c05efeacaf0a4da9a94728c70ea78824";
        private const string EntryGrabHoldLight  = "df94bc4c409d45b19889b9cf750bb49d";
        private const string EntryGrabHoldMiddle = "ab0c6a7c4b204f8996bd3c1355339157";
        private const string EntryGrabHoldHeavy  = "6f906707c8954309944d1f3375c68ed5";
        private const string EntrySnapFit        = "2e3f8d3cbbb94046ba1e662d79f5753b";
        private const string EntryUiClickLight   = "902e14644a14462db93d87122c05d889";
        private const string EntryUiClickHeavy   = "ce34d69b94c441e092772525a2c075df";
        private const string EntryScratch        = "06a735ddbc794d1cb948ada0c814b339";
        private const string EntryPushFeedback   = "3e60a1fa323245b2ac8ee3c3cba8a538";

        // ── Binding ids (bindings[] inside the same EventMap) ────────────────────

        private const string BindingPawn       = "172af27674664d87a69b3cd2795173fc";
        private const string BindingFlatSphere = "7229f2bc2f854e3e84898e4ae2f2b6a0";
        private const string BindingPokeButton = "9888d0f9dfad464f83af7b8706a5d8bb";

        // ── Scene paths ─────────────────────────────────────────────────────────

        private const string RouterPath   = "[Hapbeat Event Router]";
        private const string PawnPath     = "Table/Left/Board/PawnController";
        private const string SpherePath   = "Table/Left/Board/FlatSphereController";
        private const string PokePath     = "Table/Front Left/PokeButton";
        private const string PokeInnerPath = "Table/Front Left/PokeButton/Button";
        private const string ShapePath    = "Table/Right/SimpleSocketShape";
        private const string SocketPath   = "Table/Right/SnapSocketTitle/SimpleSocket";
        private const string ScrollPath   = "Table/Front/Scrollview Canvas/Panel/Scroll View";
        private const string SliderPath   = "Table/Front Left/UI Poke Components/Elements/MinMaxSlider";

        private static readonly string[] TouchpadButtonPaths =
        {
            "Table/Front/Touchpad Flat/Buttons/Row 1/TouchPad Button 1",
            "Table/Front/Touchpad Flat/Buttons/Row 1/TouchPad Button 2",
            "Table/Front/Touchpad Flat/Buttons/Row 1/TouchPad Button 3",
            "Table/Front/Touchpad Flat/Buttons/Row 2/TouchPad Button 4",
            "Table/Front/Touchpad Flat/Buttons/Row 2/TouchPad Button 5",
            "Table/Front/Touchpad Flat/Buttons/Row 2/TouchPad Button 6",
            "Table/Front/Touchpad Flat/Buttons/Row 3/TouchPad Button 7",
            "Table/Front/Touchpad Flat/Buttons/Row 3/TouchPad Button 8",
            "Table/Front/Touchpad Flat/Buttons/Row 3/TouchPad Button 9",
        };

        // Poke UI elements that are plain click/toggle sources (uniform settings).
        private static readonly string[] PokeUiTogglePaths =
        {
            "Table/Front Left/UI Poke Components/Elements/Icon Toggle",
            "Table/Front Left/UI Poke Components/Elements/Text Toggle",
        };

        private static readonly string[] PokeUiButtonPaths =
        {
            "Table/Front Left/UI Poke Components/Elements/Icon Button",
            "Table/Front Left/UI Poke Components/Elements/TextButton",
        };

        private const string PokeUiDropdownPath = "Table/Front Left/UI Poke Components/Elements/Dropdown";

        private const string GrabArrowPath    = "Table/Front Right/Arrow";
        private const string GrabCylinderPath = "Table/Front Right/Cylinder";
        private const string Cube1Path        = "Table/Front Right/Cubes/Cube 1";
        private const string Cube2Path        = "Table/Front Right/Cubes/Cube 2";
        private const string Cube3Path        = "Table/Front Right/Cubes/Cube 3";

        // Paths whose absence means "this is not the XRI Hands Interaction Demo".
        private static readonly string[] AnchorPaths =
        {
            "Table",
            PokePath,
            Cube1Path,
            SocketPath,
        };

        // ── XriHelpers sample types (resolved by name; may be absent) ────────────

        private const string GrabFilterTypeName   = "Hapbeat.Samples.XriHelpers.HapbeatXRGrabFilter";
        private const string SocketFilterTypeName = "Hapbeat.Samples.XriHelpers.HapbeatXRSocketFilter";
        private const string HandSideRouterTypeName = "Hapbeat.Samples.XriHelpers.HapbeatXRHandSideRouter";

        // XRI interactable events used as diagnostic sources (§2.2 of the recipe).
        private static readonly string[] DiagnosticEventFields =
        {
            "m_Activated", "m_Deactivated",
            "m_FirstFocusEntered", "m_FirstHoverEntered", "m_FirstSelectEntered",
            "m_FocusEntered", "m_FocusExited",
            "m_HoverEntered", "m_HoverExited",
            "m_LastFocusExited", "m_LastHoverExited", "m_LastSelectExited",
            "m_SelectEntered", "m_SelectExited",
        };

        // ── Menu entries ────────────────────────────────────────────────────────

        [MenuItem("Hapbeat/Samples/Augment XRI Hand Demo", false, 60)]
        private static void AugmentMenu() => Run(false);

        [MenuItem("Hapbeat/Samples/Augment XRI Hand Demo (+ diagnostic Event Logger)", false, 61)]
        private static void AugmentWithDiagnosticsMenu() => Run(true);

        // Intended for maintainer automation with Unity's -executeMethod. It acts
        // on the currently open XRI scene, saves only the dirty loaded scenes, and
        // never enters Play mode.
        public static void AugmentOpenSceneForAutomation()
        {
            Run(false);
            for (var i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (scene.isLoaded && scene.isDirty)
                    EditorSceneManager.SaveScene(scene);
            }
            AssetDatabase.SaveAssets();
        }

        // ── Entry point ─────────────────────────────────────────────────────────

        private static void Run(bool includeDiagnostics)
        {
            var warnings = new List<string>();

            var maps = LoadEventMaps();
            if (maps == null)
            {
                EditorUtility.DisplayDialog(
                    "Hapbeat — XRI Hand Demo",
                    "HandsDemoLeftEventMap.asset and HandsDemoRightEventMap.asset were not found in this project.\n\n" +
                    "Import the Hapbeat SDK sample \"XRI Hand Demo (haptics add-on)\" from the " +
                    "Package Manager first, then run this command again.",
                    "OK");
                return;
            }

            if (FindTypeByFullName(HandSideRouterTypeName) == null)
            {
                EditorUtility.DisplayDialog(
                    "Hapbeat — XRI Hand Demo",
                    "HapbeatXRHandSideRouter was not found.\n\n" +
                    "Import the Hapbeat SDK sample \"XR Helpers\" before augmenting the XRI Hand Demo.",
                    "OK");
                return;
            }

            var missingAnchors = new List<string>();
            foreach (var anchor in AnchorPaths)
            {
                if (FindByPath(anchor) == null) missingAnchors.Add(anchor);
            }
            if (missingAnchors.Count > 0)
            {
                EditorUtility.DisplayDialog(
                    "Hapbeat — XRI Hand Demo",
                    "The open scene does not look like the XR Interaction Toolkit " +
                    "\"Hands Interaction Demo\" (HandsDemoScene).\n\n" +
                    "Missing expected GameObject(s):\n  " + string.Join("\n  ", missingAnchors) + "\n\n" +
                    "Import \"Hands Interaction Demo\" from the XR Interaction Toolkit package, " +
                    "open HandsDemoScene.unity, then run this command again.",
                    "OK");
                return;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Hapbeat: Augment XRI Hand Demo");

            var stats = new Stats();
            var dirtyScenes = new HashSet<Scene>();

            BuildComponents(maps, includeDiagnostics, stats, warnings, dirtyScenes);
            BuildWiring(maps, includeDiagnostics, stats, warnings, dirtyScenes);

            foreach (var scene in dirtyScenes)
            {
                if (scene.IsValid()) EditorSceneManager.MarkSceneDirty(scene);
            }

            Undo.CollapseUndoOperations(undoGroup);

            foreach (var w in warnings) Debug.LogWarning("[Hapbeat] Hand Demo augmentor: " + w);

            Debug.Log($"[Hapbeat] XRI Hand Demo augmentation finished — " +
                      $"components added {stats.ComponentsAdded} (skipped {stats.ComponentsSkipped}), " +
                      $"UnityEvent wires added {stats.WiresAdded} (skipped {stats.WiresSkipped}), " +
                      $"warnings {warnings.Count}. " +
                      (includeDiagnostics ? "Diagnostic Event Logger included." : "Diagnostic Event Logger omitted."));
        }

        private sealed class Stats
        {
            public int ComponentsAdded;
            public int ComponentsSkipped;
            public int WiresAdded;
            public int WiresSkipped;
        }

        private sealed class SideMaps
        {
            public HapbeatEventMap Left;
            public HapbeatEventMap Right;
        }

        // ── Component placement (§1 of the recipe) ───────────────────────────────

        private static void BuildComponents(SideMaps maps, bool includeDiagnostics,
                                            Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            // Scene root: the manager. Only create the router GameObject when the
            // scene has no manager at all, so re-runs (and users who already put a
            // manager somewhere else) don't end up with two singletons.
            if (UnityEngine.Object.FindFirstObjectByType<HapbeatManager>(FindObjectsInactive.Include) == null)
            {
                var router = new GameObject(RouterPath);
                Undo.RegisterCreatedObjectUndo(router, "Create Hapbeat Event Router");
                Undo.AddComponent<HapbeatManager>(router);
                stats.ComponentsAdded += 1;
                dirty.Add(router.scene);
            }
            else
            {
                stats.ComponentsSkipped += 1;
            }

            foreach (var path in TouchpadButtonPaths)
                AddSideTrigger<HapbeatUnityEventTrigger>(path, maps, EntryUiClickHeavy, stats, warnings, dirty);

            foreach (var path in PokeUiTogglePaths)
                AddSideTrigger<HapbeatUnityEventTrigger>(path, maps, EntryUiClickLight, stats, warnings, dirty);
            foreach (var path in PokeUiButtonPaths)
                AddSideTrigger<HapbeatUnityEventTrigger>(path, maps, EntryUiClickLight, stats, warnings, dirty);
            AddSideTrigger<HapbeatUnityEventTrigger>(PokeUiDropdownPath, maps, EntryUiClickLight, stats, warnings, dirty);

            AddSideTick(SliderPath, maps, EntryUiClickLight, 0.4f, 0.05f, stats, warnings, dirty);
            AddSideTick(ScrollPath, maps, EntryUiClickLight, 0.8f, 0.165f, stats, warnings, dirty);

            AddSideTrigger<HapbeatUnityEventTrigger>(GrabArrowPath, maps, EntryGrabLight, stats, warnings, dirty);
            AddSideTrigger<HapbeatUnityEventTrigger>(GrabCylinderPath, maps, EntryGrabLight, stats, warnings, dirty);

            AddSideSequence(Cube1Path, maps, EntryGrabHoldLight, EntryGrabLight, EntryGrabLight, 0f, stats, warnings, dirty);
            AddSideSequence(Cube2Path, maps, EntryGrabHoldLight, EntryGrabLight, EntryGrabLight, 0f, stats, warnings, dirty);
            AddSideSequence(Cube3Path, maps, EntryGrabHoldMiddle, EntryGrabHeavy, EntryGrabHeavy, 0.1f, stats, warnings, dirty);

            AddSideSequence(PawnPath, maps, EntryScratch, null, null, 0f, stats, warnings, dirty);
            AddSideBinding(PawnPath, PawnPath, maps, BindingPawn, null, null, stats, warnings, dirty);

            AddSideSequence(SpherePath, maps, EntryScratch, null, null, 0f, stats, warnings, dirty);
            AddSideBinding(SpherePath, SpherePath, maps, BindingFlatSphere, null, null, stats, warnings, dirty);

            if (includeDiagnostics)
                AddComponentWithFields<HapbeatEventLogger>(PokePath, stats, warnings, dirty, null);

            AddSideTrigger<HapbeatUnityEventTrigger>(PokePath, maps, EntryPushFeedback, stats, warnings, dirty);
            // Inverted input range (min > max): the button travels downwards, so the
            // deepest press maps to the strongest gain. Effective values come from the
            // linked EventMap binding; these locals are the fallback.
            AddSideBinding(PokePath, PokeInnerPath, maps, BindingPokeButton, 0.0165f, 0f, stats, warnings, dirty);
            // Second binding on the inner Button, as recorded in the reference scene.
            // HapbeatTriggerBase.FindBindingForEntry takes the first match in
            // Self -> Children -> Parent order, so this one is redundant in practice;
            // it is reproduced to stay faithful to the reference wiring.
            AddSideBinding(PokeInnerPath, PokeInnerPath, maps, BindingPokeButton, null, null, stats, warnings, dirty);

            AddSideSequence(ShapePath, maps, EntryGrabHoldHeavy, EntryGrabHeavy, EntryGrabHeavy, 0f, stats, warnings, dirty);
            AddSideTrigger<HapbeatUnityEventTrigger>(SocketPath, maps, EntrySnapFit, stats, warnings, dirty);

            // The socket helper suppresses a known XRI re-hover after deselect.
            AddComponentByTypeName(SocketPath, SocketFilterTypeName, stats, warnings, dirty);
        }

        private static void AddSideTrigger<T>(string path, SideMaps maps, string entryId,
                                              Stats stats, List<string> warnings, HashSet<Scene> dirty)
            where T : HapbeatTriggerBase
        {
            var left = GetOrAddMappedTrigger<T>(path, maps.Left, entryId, stats, warnings, dirty, null);
            var right = GetOrAddMappedTrigger<T>(path, maps.Right, entryId, stats, warnings, dirty, null);
            EnsureHandSideRouter(path, stats, warnings, dirty);
        }

        private static void AddSideSequence(string path, SideMaps maps, string loopEntry,
                                            string onStartEntry, string onStopEntry, float cooldown,
                                            Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            Action<SerializedObject> configure = so =>
            {
                SetProperty(so, "_onStartEntryId", onStartEntry ?? string.Empty, warnings);
                SetProperty(so, "_onStopEntryId", onStopEntry ?? string.Empty, warnings);
                if (cooldown > 0f) SetProperty(so, "_cooldown", cooldown, warnings);
            };
            GetOrAddMappedTrigger<HapbeatSequenceTrigger>(path, maps.Left, loopEntry, stats, warnings, dirty, configure);
            GetOrAddMappedTrigger<HapbeatSequenceTrigger>(path, maps.Right, loopEntry, stats, warnings, dirty, configure);
            EnsureHandSideRouter(path, stats, warnings, dirty);
        }

        private static void AddSideTick(string path, SideMaps maps, string entryId, float gainMultiplier,
                                        float tickThreshold, Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            Action<SerializedObject> configure = so =>
            {
                SetProperty(so, "_gainMultiplier", gainMultiplier, warnings);
                SetProperty(so, "_tickThreshold", tickThreshold, warnings);
            };
            GetOrAddMappedTrigger<HapbeatTickEmitter>(path, maps.Left, entryId, stats, warnings, dirty, configure);
            GetOrAddMappedTrigger<HapbeatTickEmitter>(path, maps.Right, entryId, stats, warnings, dirty, configure);
            EnsureHandSideRouter(path, stats, warnings, dirty);
        }

        private static void AddSideBinding(string path, string sourceTransformPath, SideMaps maps, string bindingId,
                                           float? inputMin, float? inputMax, Stats stats, List<string> warnings,
                                           HashSet<Scene> dirty)
        {
            var sourceGo = FindByPath(sourceTransformPath);
            if (sourceGo == null)
                warnings.Add($"binding source Transform '{sourceTransformPath}' not found — binding on '{path}' left without a source.");

            AddMappedBinding(path, sourceGo, maps.Left, bindingId, inputMin, inputMax, stats, warnings, dirty);
            AddMappedBinding(path, sourceGo, maps.Right, bindingId, inputMin, inputMax, stats, warnings, dirty);
        }

        private static void AddMappedBinding(string path, GameObject sourceGo, HapbeatEventMap map, string bindingId,
                                             float? inputMin, float? inputMax, Stats stats, List<string> warnings,
                                             HashSet<Scene> dirty)
        {
            var go = FindByPath(path);
            if (go == null)
            {
                warnings.Add($"GameObject '{path}' not found — skipped HapbeatParameterBinding.");
                stats.ComponentsSkipped += 1;
                return;
            }

            HapbeatParameterBinding binding = null;
            foreach (var candidate in go.GetComponents<HapbeatParameterBinding>())
            {
                var candidateSo = new SerializedObject(candidate);
                var candidateMap = candidateSo.FindProperty("_linkedEventMap").objectReferenceValue as HapbeatEventMap;
                if (candidateMap == map)
                {
                    binding ??= candidate;
                    continue;
                }

                // Replace only bindings created by the pre-side-routing version of
                // this sample. Bindings for any other user-authored map remain.
                if (candidateMap != null && candidateMap.name == "HandsDemoEventMap")
                    Undo.DestroyObjectImmediate(candidate);
            }

            if (binding == null)
            {
                binding = Undo.AddComponent<HapbeatParameterBinding>(go);
                stats.ComponentsAdded += 1;
                dirty.Add(go.scene);
            }
            else
            {
                stats.ComponentsSkipped += 1;
            }

            var so = new SerializedObject(binding);
            if (sourceGo != null) SetProperty(so, "_sourceTransform", sourceGo.transform, warnings);
            SetProperty(so, "_linkedEventMap", map, warnings);
            SetProperty(so, "_linkedBindingId", bindingId, warnings);
            if (inputMin.HasValue) SetProperty(so, "_inputMin", inputMin.Value, warnings);
            if (inputMax.HasValue) SetProperty(so, "_inputMax", inputMax.Value, warnings);
            so.ApplyModifiedProperties();
        }

        private static T GetOrAddMappedTrigger<T>(string path, HapbeatEventMap map, string entryId,
                                                   Stats stats, List<string> warnings, HashSet<Scene> dirty,
                                                   Action<SerializedObject> configure)
            where T : HapbeatTriggerBase
        {
            var go = FindByPath(path);
            if (go == null)
            {
                warnings.Add($"GameObject '{path}' not found — skipped {typeof(T).Name}.");
                stats.ComponentsSkipped += 1;
                return null;
            }

            T result = null;
            foreach (var candidate in go.GetComponents<T>())
            {
                var candidateSo = new SerializedObject(candidate);
                var candidateMap = candidateSo.FindProperty("_eventMap").objectReferenceValue as HapbeatEventMap;
                if (candidateMap == map)
                {
                    result = candidate;
                    break;
                }

                // Version 0.5.0 and earlier put one all-target map directly on
                // each sample object. This augmentor owns those components, so
                // remove only that named legacy map before creating side-specific
                // replacements. User-authored maps are never touched.
                if (candidateMap != null && candidateMap.name == "HandsDemoEventMap")
                    Undo.DestroyObjectImmediate(candidate);
            }

            if (result == null)
            {
                result = Undo.AddComponent<T>(go);
                stats.ComponentsAdded += 1;
                dirty.Add(go.scene);
            }
            else
            {
                stats.ComponentsSkipped += 1;
            }

            var so = new SerializedObject(result);
            SetProperty(so, "_eventMap", map, warnings);
            SetProperty(so, "_entryId", entryId, warnings);
            configure?.Invoke(so);
            so.ApplyModifiedProperties();
            return result;
        }

        private static Component EnsureHandSideRouter(string path, Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            var type = FindTypeByFullName(HandSideRouterTypeName);
            if (type == null)
            {
                warnings.Add($"type '{HandSideRouterTypeName}' not found — import XR Helpers.");
                stats.ComponentsSkipped += 1;
                return null;
            }

            var go = FindByPath(path);
            if (go == null) return null;
            var existing = go.GetComponent(type);
            if (existing != null) return existing;

            var router = Undo.AddComponent(go, type);
            stats.ComponentsAdded += 1;
            dirty.Add(go.scene);
            return router;
        }

        private static T FindMappedTrigger<T>(string path, HapbeatEventMap map) where T : HapbeatTriggerBase
        {
            var go = FindByPath(path);
            if (go == null) return null;
            foreach (var candidate in go.GetComponents<T>())
            {
                var so = new SerializedObject(candidate);
                if (so.FindProperty("_eventMap").objectReferenceValue == map) return candidate;
            }
            return null;
        }

        private static Component FindHandSideRouter(string path)
        {
            var go = FindByPath(path);
            var type = FindTypeByFullName(HandSideRouterTypeName);
            return go != null && type != null ? go.GetComponent(type) : null;
        }

        private static void WireToComponent(Component source, string sourcePath, string eventField, Component target,
                                            string method, PersistentListenerMode mode, string stringArgument,
                                            Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            if (source == null || target == null)
            {
                warnings.Add($"missing source or target for {sourcePath}.{eventField} -> {method}.");
                stats.WiresSkipped += 1;
                return;
            }

            var result = AddPersistentCall(source, eventField, target, method, mode, stringArgument, warnings);
            if (result == WireResult.Added)
            {
                stats.WiresAdded += 1;
                dirty.Add(source.gameObject.scene);
            }
            else
            {
                stats.WiresSkipped += 1;
            }
        }

        private static void WireSourceToRouter(string sourcePath, string[] sourceComponentNames, string eventField,
                                               string routerMethod, PersistentListenerMode mode, Stats stats,
                                               List<string> warnings, HashSet<Scene> dirty)
        {
            var go = FindByPath(sourcePath);
            var source = go != null ? FindComponentByTypeName(go, sourceComponentNames) : null;
            WireToComponent(source, sourcePath, eventField, FindHandSideRouter(sourcePath), routerMethod, mode,
                            null, stats, warnings, dirty);
        }

        private static void WireRouterToSideTriggers<T>(string routerPath, SideMaps maps, string leftEvent,
                                                        string rightEvent, string targetPath, string method,
                                                        PersistentListenerMode mode, Stats stats,
                                                        List<string> warnings, HashSet<Scene> dirty)
            where T : HapbeatTriggerBase
        {
            var leftTarget = FindMappedTrigger<T>(targetPath, maps.Left);
            var rightTarget = FindMappedTrigger<T>(targetPath, maps.Right);
            var router = FindHandSideRouter(routerPath);
            WireToComponent(router, routerPath, leftEvent, leftTarget, method, mode, null, stats, warnings, dirty);
            WireToComponent(router, routerPath, rightEvent, rightTarget, method, mode, null, stats, warnings, dirty);
        }

        private static void AddTrigger<T>(string path, HapbeatEventMap map, string entryId,
                                          Stats stats, List<string> warnings, HashSet<Scene> dirty)
            where T : Component
        {
            AddComponentWithFields<T>(path, stats, warnings, dirty, so =>
            {
                SetProperty(so, "_eventMap", map, warnings);
                SetProperty(so, "_entryId", entryId, warnings);
            });
        }

        private static void AddSequence(string path, HapbeatEventMap map, string loopEntry,
                                        string onStartEntry, string onStopEntry, float cooldown,
                                        Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            AddComponentWithFields<HapbeatSequenceTrigger>(path, stats, warnings, dirty, so =>
            {
                SetProperty(so, "_eventMap", map, warnings);
                SetProperty(so, "_entryId", loopEntry, warnings);
                SetProperty(so, "_onStartEntryId", onStartEntry ?? string.Empty, warnings);
                SetProperty(so, "_onStopEntryId", onStopEntry ?? string.Empty, warnings);
                if (cooldown > 0f) SetProperty(so, "_cooldown", cooldown, warnings);
            });
        }

        private static void AddBinding(string path, string sourceTransformPath, HapbeatEventMap map,
                                       string bindingId, float? inputMin, float? inputMax,
                                       Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            var sourceGo = FindByPath(sourceTransformPath);
            if (sourceGo == null)
            {
                warnings.Add($"binding source Transform '{sourceTransformPath}' not found — " +
                             $"binding on '{path}' left without a source.");
            }

            AddComponentWithFields<HapbeatParameterBinding>(path, stats, warnings, dirty, so =>
            {
                if (sourceGo != null) SetProperty(so, "_sourceTransform", sourceGo.transform, warnings);
                SetProperty(so, "_linkedEventMap", map, warnings);
                SetProperty(so, "_linkedBindingId", bindingId, warnings);
                if (inputMin.HasValue) SetProperty(so, "_inputMin", inputMin.Value, warnings);
                if (inputMax.HasValue) SetProperty(so, "_inputMax", inputMax.Value, warnings);
            });
        }

        private static void AddComponentWithFields<T>(string path, Stats stats, List<string> warnings,
                                                      HashSet<Scene> dirty, Action<SerializedObject> configure)
            where T : Component
        {
            AddComponentInternal(path, typeof(T), stats, warnings, dirty, configure);
        }

        private static void AddComponentByTypeName(string path, string fullTypeName, Stats stats,
                                                   List<string> warnings, HashSet<Scene> dirty)
        {
            var type = FindTypeByFullName(fullTypeName);
            if (type == null)
            {
                warnings.Add($"type '{fullTypeName}' not found — skipping it and its wiring on '{path}'. " +
                             "Import the Hapbeat SDK sample \"XR Helpers\" from the Package Manager.");
                stats.ComponentsSkipped += 1;
                return;
            }
            AddComponentInternal(path, type, stats, warnings, dirty, null);
        }

        private static void AddComponentInternal(string path, Type type, Stats stats, List<string> warnings,
                                                 HashSet<Scene> dirty, Action<SerializedObject> configure)
        {
            var go = FindByPath(path);
            if (go == null)
            {
                warnings.Add($"GameObject '{path}' not found — skipped {type.Name}. " +
                             "The XRI demo hierarchy may differ in your XRI version.");
                stats.ComponentsSkipped += 1;
                return;
            }

            var existing = go.GetComponent(type);
            if (existing != null)
            {
                stats.ComponentsSkipped += 1;
                return;
            }

            var component = Undo.AddComponent(go, type);
            stats.ComponentsAdded += 1;
            dirty.Add(go.scene);

            if (configure == null) return;
            var so = new SerializedObject(component);
            configure(so);
            so.ApplyModifiedProperties();
        }

        private static void SetProperty(SerializedObject so, string name, object value, List<string> warnings)
        {
            var p = so.FindProperty(name);
            if (p == null)
            {
                warnings.Add($"serialized field '{name}' not found on {so.targetObject.GetType().Name} — value not applied.");
                return;
            }

            switch (p.propertyType)
            {
                case SerializedPropertyType.String:
                    p.stringValue = (string)value;
                    break;
                case SerializedPropertyType.Float:
                    p.floatValue = Convert.ToSingle(value);
                    break;
                case SerializedPropertyType.Integer:
                    p.intValue = Convert.ToInt32(value);
                    break;
                case SerializedPropertyType.Boolean:
                    p.boolValue = Convert.ToBoolean(value);
                    break;
                case SerializedPropertyType.ObjectReference:
                    p.objectReferenceValue = (UnityEngine.Object)value;
                    break;
                default:
                    warnings.Add($"serialized field '{name}' has unsupported type {p.propertyType} — value not applied.");
                    break;
            }
        }

        // ── UnityEvent wiring (§2 of the recipe) ─────────────────────────────────

        private static void BuildWiring(SideMaps maps, bool includeDiagnostics, Stats stats,
                                        List<string> warnings, HashSet<Scene> dirty)
        {
            foreach (var path in TouchpadButtonPaths)
            {
                WireSourceToRouter(path, new[] { "Button" }, "m_OnClick", "RouteVoid", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatUnityEventTrigger>(path, maps, "OnLeftRouted", "OnRightRouted", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
            }

            foreach (var path in PokeUiTogglePaths)
            {
                WireSourceToRouter(path, new[] { "Toggle" }, "onValueChanged", "RouteVoid", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatUnityEventTrigger>(path, maps, "OnLeftRouted", "OnRightRouted", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
            }
            foreach (var path in PokeUiButtonPaths)
            {
                WireSourceToRouter(path, new[] { "Button" }, "m_OnClick", "RouteVoid", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatUnityEventTrigger>(path, maps, "OnLeftRouted", "OnRightRouted", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
            }
            WireSourceToRouter(PokeUiDropdownPath, new[] { "Dropdown", "TMP_Dropdown" }, "m_OnValueChanged", "RouteVoid", PersistentListenerMode.Void, stats, warnings, dirty);
            WireRouterToSideTriggers<HapbeatUnityEventTrigger>(PokeUiDropdownPath, maps, "OnLeftRouted", "OnRightRouted", PokeUiDropdownPath, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);

            WireSourceToRouter(SliderPath, new[] { "Slider" }, "m_OnValueChanged", "RouteFloat", PersistentListenerMode.EventDefined, stats, warnings, dirty);
            WireRouterToSideTriggers<HapbeatTickEmitter>(SliderPath, maps, "OnLeftRoutedFloat", "OnRightRoutedFloat", SliderPath, "Fire", PersistentListenerMode.EventDefined, stats, warnings, dirty);
            WireSourceToRouter(ScrollPath, new[] { "ScrollRect" }, "m_OnValueChanged", "RouteVector2", PersistentListenerMode.EventDefined, stats, warnings, dirty);
            WireRouterToSideTriggers<HapbeatTickEmitter>(ScrollPath, maps, "OnLeftRoutedVector2", "OnRightRoutedVector2", ScrollPath, "Fire", PersistentListenerMode.EventDefined, stats, warnings, dirty);

            foreach (var path in new[] { GrabArrowPath, GrabCylinderPath })
            {
                WireRouterToSideTriggers<HapbeatUnityEventTrigger>(path, maps, "OnLeftSelectEntered", "OnRightSelectEntered", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatUnityEventTrigger>(path, maps, "OnLeftSelectExited", "OnRightSelectExited", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
            }

            foreach (var path in new[] { Cube1Path, Cube2Path, Cube3Path })
            {
                WireRouterToSideTriggers<HapbeatSequenceTrigger>(path, maps, "OnLeftSelectEntered", "OnRightSelectEntered", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatSequenceTrigger>(path, maps, "OnLeftSelectExited", "OnRightSelectExited", path, "Stop", PersistentListenerMode.Void, stats, warnings, dirty);
            }

            foreach (var path in new[] { PawnPath, SpherePath })
            {
                WireRouterToSideTriggers<HapbeatSequenceTrigger>(path, maps, "OnLeftFirstSelectEntered", "OnRightFirstSelectEntered", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatSequenceTrigger>(path, maps, "OnLeftSelectEntered", "OnRightSelectEntered", path, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatSequenceTrigger>(path, maps, "OnLeftLastSelectExited", "OnRightLastSelectExited", path, "Stop", PersistentListenerMode.Void, stats, warnings, dirty);
                WireRouterToSideTriggers<HapbeatSequenceTrigger>(path, maps, "OnLeftSelectExited", "OnRightSelectExited", path, "Stop", PersistentListenerMode.Void, stats, warnings, dirty);
            }

            WireRouterToSideTriggers<HapbeatUnityEventTrigger>(PokePath, maps, "OnLeftFirstHoverEntered", "OnRightFirstHoverEntered", PokePath, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
            WireRouterToSideTriggers<HapbeatUnityEventTrigger>(PokePath, maps, "OnLeftLastHoverExited", "OnRightLastHoverExited", PokePath, "Stop", PersistentListenerMode.Void, stats, warnings, dirty);

            var socketFilterType = FindTypeByFullName(SocketFilterTypeName);
            var socketFilter = socketFilterType != null ? FindByPath(SocketPath)?.GetComponent(socketFilterType) : null;
            WireToComponent(socketFilter, SocketPath, "OnInitialHoverEntered", FindHandSideRouter(ShapePath), "RouteVoid", PersistentListenerMode.Void, null, stats, warnings, dirty);
            WireRouterToSideTriggers<HapbeatUnityEventTrigger>(ShapePath, maps, "OnLeftRouted", "OnRightRouted", SocketPath, "Fire", PersistentListenerMode.Void, stats, warnings, dirty);
            WireRouterToSideTriggers<HapbeatSequenceTrigger>(ShapePath, maps, "OnLeftRouted", "OnRightRouted", ShapePath, "Stop", PersistentListenerMode.Void, stats, warnings, dirty);

            if (!includeDiagnostics) return;
            foreach (var field in DiagnosticEventFields)
                Wire(PokePath, new[] { "XRSimpleInteractable" }, field, PokePath, typeof(HapbeatEventLogger),
                     "LogEvent", PersistentListenerMode.String, StripSerializedPrefix(field), stats, warnings, dirty);
        }

        private static void BuildLegacyWiring(bool includeDiagnostics, Stats stats,
                                        List<string> warnings, HashSet<Scene> dirty)
        {
            // uGUI sources on the touchpad: Button.onClick -> Fire.
            foreach (var path in TouchpadButtonPaths)
                Wire(path, new[] { "Button" }, "m_OnClick", path, typeof(HapbeatUnityEventTrigger),
                     "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);

            foreach (var path in PokeUiTogglePaths)
                Wire(path, new[] { "Toggle" }, "onValueChanged", path, typeof(HapbeatUnityEventTrigger),
                     "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);

            foreach (var path in PokeUiButtonPaths)
                Wire(path, new[] { "Button" }, "m_OnClick", path, typeof(HapbeatUnityEventTrigger),
                     "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);

            Wire(PokeUiDropdownPath, new[] { "Dropdown", "TMP_Dropdown" }, "m_OnValueChanged",
                 PokeUiDropdownPath, typeof(HapbeatUnityEventTrigger),
                 "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);

            // Continuous UI sources pass their own value through, so the call runs in
            // EventDefined mode and binds to the matching HapbeatTickEmitter.Fire
            // overload (float for Slider, Vector2 for ScrollRect).
            Wire(SliderPath, new[] { "Slider" }, "m_OnValueChanged", SliderPath, typeof(HapbeatTickEmitter),
                 "Fire", PersistentListenerMode.EventDefined, null, stats, warnings, dirty);
            Wire(ScrollPath, new[] { "ScrollRect" }, "m_OnValueChanged", ScrollPath, typeof(HapbeatTickEmitter),
                 "Fire", PersistentListenerMode.EventDefined, null, stats, warnings, dirty);

            // Grabbables: select entered/exited -> Fire/Stop.
            foreach (var path in new[] { GrabArrowPath, GrabCylinderPath })
            {
                Wire(path, new[] { "XRGrabInteractable" }, "m_SelectEntered", path,
                     typeof(HapbeatUnityEventTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
                Wire(path, new[] { "XRGrabInteractable" }, "m_SelectExited", path,
                     typeof(HapbeatUnityEventTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
            }

            foreach (var path in new[] { Cube1Path, Cube2Path, Cube3Path })
            {
                Wire(path, new[] { "XRGrabInteractable" }, "m_SelectEntered", path,
                     typeof(HapbeatSequenceTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
                Wire(path, new[] { "XRGrabInteractable" }, "m_SelectExited", path,
                     typeof(HapbeatSequenceTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);
            }

            // Scrub objects: both the first/last and the per-interactor events are wired,
            // matching the reference scene. HapbeatSequenceTrigger's re-entry guard makes
            // the duplicate pair harmless.
            foreach (var path in new[] { PawnPath, SpherePath })
            {
                Wire(path, new[] { "XRGrabInteractable" }, "m_FirstSelectEntered", path,
                     typeof(HapbeatSequenceTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
                Wire(path, new[] { "XRGrabInteractable" }, "m_SelectEntered", path,
                     typeof(HapbeatSequenceTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
                Wire(path, new[] { "XRGrabInteractable" }, "m_LastSelectExited", path,
                     typeof(HapbeatSequenceTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);
                Wire(path, new[] { "XRGrabInteractable" }, "m_SelectExited", path,
                     typeof(HapbeatSequenceTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);
            }

            // Poke button: hover depth drives a looping stream.
            Wire(PokePath, new[] { "XRSimpleInteractable" }, "m_FirstHoverEntered", PokePath,
                 typeof(HapbeatUnityEventTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
            Wire(PokePath, new[] { "XRSimpleInteractable" }, "m_LastHoverExited", PokePath,
                 typeof(HapbeatUnityEventTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);

            // Socket / snap. XRI's own select events are left untouched — the two filter
            // components sit in front of them and expose hand-vs-socket specific events,
            // which is what gets wired here.
            var grabFilterType = FindTypeByFullName(GrabFilterTypeName);
            var socketFilterType = FindTypeByFullName(SocketFilterTypeName);

            if (grabFilterType != null)
            {
                WireFromType(ShapePath, grabFilterType, "OnHandSelected", ShapePath,
                             typeof(HapbeatSequenceTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
                WireFromType(ShapePath, grabFilterType, "OnHandReleased", ShapePath,
                             typeof(HapbeatSequenceTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);
                WireFromType(ShapePath, grabFilterType, "OnSocketSelected", ShapePath,
                             typeof(HapbeatSequenceTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);
            }

            if (socketFilterType != null)
            {
                WireFromType(SocketPath, socketFilterType, "OnInitialHoverEntered", SocketPath,
                             typeof(HapbeatUnityEventTrigger), "Fire", PersistentListenerMode.Void, null, stats, warnings, dirty);
                // Snapping into the socket also ends the shape's hold loop.
                WireFromType(SocketPath, socketFilterType, "OnInitialHoverEntered", ShapePath,
                             typeof(HapbeatSequenceTrigger), "Stop", PersistentListenerMode.Void, null, stats, warnings, dirty);
            }

            if (!includeDiagnostics) return;

            foreach (var field in DiagnosticEventFields)
            {
                // LogEvent takes a string, which the interactable events do not supply,
                // so the call runs in String mode with the source event name as literal.
                Wire(PokePath, new[] { "XRSimpleInteractable" }, field, PokePath, typeof(HapbeatEventLogger),
                     "LogEvent", PersistentListenerMode.String, StripSerializedPrefix(field),
                     stats, warnings, dirty);
            }
        }

        private static void Wire(string sourcePath, string[] sourceComponentNames, string eventField,
                                 string targetPath, Type targetType, string method,
                                 PersistentListenerMode mode, string stringArgument,
                                 Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            var sourceGo = FindByPath(sourcePath);
            if (sourceGo == null)
            {
                warnings.Add($"GameObject '{sourcePath}' not found — skipped wiring {eventField} -> {targetType.Name}.{method}.");
                stats.WiresSkipped += 1;
                return;
            }

            var source = FindComponentByTypeName(sourceGo, sourceComponentNames);
            if (source == null)
            {
                warnings.Add($"none of [{string.Join(", ", sourceComponentNames)}] found on '{sourcePath}' — " +
                             $"skipped wiring {eventField} -> {targetType.Name}.{method}.");
                stats.WiresSkipped += 1;
                return;
            }

            WireCore(source, sourcePath, eventField, targetPath, targetType, method, mode, stringArgument,
                     stats, warnings, dirty);
        }

        private static void WireFromType(string sourcePath, Type sourceType, string eventField,
                                         string targetPath, Type targetType, string method,
                                         PersistentListenerMode mode, string stringArgument,
                                         Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            var sourceGo = FindByPath(sourcePath);
            var source = sourceGo != null ? sourceGo.GetComponent(sourceType) : null;
            if (source == null)
            {
                warnings.Add($"{sourceType.Name} not found on '{sourcePath}' — " +
                             $"skipped wiring {eventField} -> {targetType.Name}.{method}.");
                stats.WiresSkipped += 1;
                return;
            }

            WireCore(source, sourcePath, eventField, targetPath, targetType, method, mode, stringArgument,
                     stats, warnings, dirty);
        }

        private static void WireCore(Component source, string sourcePath, string eventField,
                                     string targetPath, Type targetType, string method,
                                     PersistentListenerMode mode, string stringArgument,
                                     Stats stats, List<string> warnings, HashSet<Scene> dirty)
        {
            var targetGo = FindByPath(targetPath);
            var target = targetGo != null ? targetGo.GetComponent(targetType) : null;
            if (target == null)
            {
                warnings.Add($"{targetType.Name} not found on '{targetPath}' — " +
                             $"skipped wiring {sourcePath}.{eventField}.");
                stats.WiresSkipped += 1;
                return;
            }

            var result = AddPersistentCall(source, eventField, target, method, mode, stringArgument, warnings);
            switch (result)
            {
                case WireResult.Added:
                    stats.WiresAdded += 1;
                    dirty.Add(source.gameObject.scene);
                    break;
                case WireResult.AlreadyPresent:
                    stats.WiresSkipped += 1;
                    break;
                default:
                    stats.WiresSkipped += 1;
                    break;
            }
        }

        private enum WireResult { Added, AlreadyPresent, Failed }

        /// <summary>
        /// Appends a persistent call to a UnityEvent by editing its serialized
        /// <c>m_PersistentCalls.m_Calls</c> array. Going through SerializedObject rather
        /// than <c>UnityEventTools</c> is what keeps this file free of any reference to
        /// the XRI event types (SelectEnterEvent etc.), which cannot be named here.
        /// </summary>
        private static WireResult AddPersistentCall(Component source, string eventField, Component target,
                                                    string method, PersistentListenerMode mode,
                                                    string stringArgument, List<string> warnings)
        {
            var so = new SerializedObject(source);
            var calls = so.FindProperty(eventField + ".m_PersistentCalls.m_Calls");
            if (calls == null || !calls.isArray)
            {
                warnings.Add($"UnityEvent '{eventField}' not found on {source.GetType().Name} " +
                             $"('{source.gameObject.name}') — skipped {method}. " +
                             "The XRI version in this project may name the event differently.");
                return WireResult.Failed;
            }

            for (int i = 0; i < calls.arraySize; i++)
            {
                var existing = calls.GetArrayElementAtIndex(i);
                if (existing.FindPropertyRelative("m_Target").objectReferenceValue != target) continue;
                if (existing.FindPropertyRelative("m_MethodName").stringValue != method) continue;
                if (stringArgument != null &&
                    existing.FindPropertyRelative("m_Arguments.m_StringArgument").stringValue != stringArgument) continue;
                return WireResult.AlreadyPresent;
            }

            int index = calls.arraySize;
            calls.InsertArrayElementAtIndex(index);
            var call = calls.GetArrayElementAtIndex(index);

            call.FindPropertyRelative("m_Target").objectReferenceValue = target;
            var typeNameProp = call.FindPropertyRelative("m_TargetAssemblyTypeName");
            if (typeNameProp != null) typeNameProp.stringValue = AssemblyTypeName(target.GetType());
            call.FindPropertyRelative("m_MethodName").stringValue = method;
            call.FindPropertyRelative("m_Mode").enumValueIndex = (int)mode;
            call.FindPropertyRelative("m_CallState").enumValueIndex = (int)UnityEventCallState.RuntimeOnly;

            // InsertArrayElementAtIndex duplicates the preceding element, so every
            // argument slot is reset explicitly rather than left inherited.
            var args = call.FindPropertyRelative("m_Arguments");
            args.FindPropertyRelative("m_ObjectArgument").objectReferenceValue = null;
            args.FindPropertyRelative("m_ObjectArgumentAssemblyTypeName").stringValue = "UnityEngine.Object, UnityEngine";
            args.FindPropertyRelative("m_IntArgument").intValue = 0;
            args.FindPropertyRelative("m_FloatArgument").floatValue = 0f;
            args.FindPropertyRelative("m_StringArgument").stringValue = stringArgument ?? string.Empty;
            args.FindPropertyRelative("m_BoolArgument").boolValue = false;

            so.ApplyModifiedProperties();
            return WireResult.Added;
        }

        // ── Lookup helpers ──────────────────────────────────────────────────────

        private static SideMaps LoadEventMaps()
        {
            HapbeatEventMap left = null;
            HapbeatEventMap right = null;
            foreach (var guid in AssetDatabase.FindAssets("HandsDemoLeftEventMap t:HapbeatEventMap"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
                if (map != null) { left = map; break; }
            }
            foreach (var guid in AssetDatabase.FindAssets("HandsDemoRightEventMap t:HapbeatEventMap"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var map = AssetDatabase.LoadAssetAtPath<HapbeatEventMap>(path);
                if (map != null) { right = map; break; }
            }
            return left != null && right != null ? new SideMaps { Left = left, Right = right } : null;
        }

        /// <summary>
        /// Resolves a "Root/Child/Grandchild" path across every loaded scene.
        /// <c>Transform.Find</c> is used rather than <c>GameObject.Find</c> so inactive
        /// objects are matched too.
        /// </summary>
        private static GameObject FindByPath(string path)
        {
            int slash = path.IndexOf('/');
            string rootName = slash < 0 ? path : path.Substring(0, slash);
            string rest = slash < 0 ? null : path.Substring(slash + 1);

            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (var root in scene.GetRootGameObjects())
                {
                    if (root.name != rootName) continue;
                    if (rest == null) return root;
                    var child = root.transform.Find(rest);
                    if (child != null) return child.gameObject;
                }
            }
            return null;
        }

        private static Component FindComponentByTypeName(GameObject go, string[] typeNames)
        {
            var components = go.GetComponents<Component>();
            foreach (var name in typeNames)
            {
                foreach (var c in components)
                {
                    if (c != null && c.GetType().Name == name) return c;
                }
            }
            return null;
        }

        private static Type FindTypeByFullName(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null) return type;
            }
            return null;
        }

        private static string AssemblyTypeName(Type type)
        {
            return type.FullName + ", " + type.Assembly.GetName().Name;
        }

        private static string StripSerializedPrefix(string serializedName)
        {
            if (!serializedName.StartsWith("m_", StringComparison.Ordinal)) return serializedName;
            var trimmed = serializedName.Substring(2);
            return char.ToLowerInvariant(trimmed[0]) + trimmed.Substring(1);
        }
    }
}
#endif
