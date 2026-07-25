#if HAPBEAT_HAS_COMPOSITION_LAYERS
using System.Collections.Generic;
using Unity.XR.CompositionLayers;
using Unity.XR.CompositionLayers.Extensions;
using Unity.XR.CompositionLayers.Layers;
using Unity.XR.CompositionLayers.Services;
using UnityEngine;

namespace Hapbeat
{
    /// <summary>
    /// Renders a world-space uGUI <see cref="Canvas"/> into a <see cref="RenderTexture"/>
    /// and hands that texture to an OpenXR <b>quad composition layer</b>, so the panel is
    /// composited by the XR compositor instead of being drawn into the application's eye
    /// buffer.
    ///
    /// <para>
    /// Why this exists: a Canvas drawn into the eye buffer goes through the compositor's
    /// reprojection (TimeWarp) pass, which assumes everything it corrects is static in the
    /// scene — a view-following surface therefore receives the inverse correction and
    /// visibly swims on head motion. It is also resampled at whatever Render Scale the
    /// project's quality tier uses, which is what makes small text soft on standalone
    /// headsets. A composition layer bypasses both: the compositor samples this
    /// RenderTexture directly at display resolution, after reprojection.
    /// </para>
    ///
    /// <para>
    /// <b>All Composition Layers API usage lives in this file</b> — the panel itself only
    /// ever touches this class, and only from inside <c>#if HAPBEAT_HAS_COMPOSITION_LAYERS</c>,
    /// so the SDK still compiles in projects without the
    /// <c>com.unity.xr.compositionlayers</c> package.
    /// </para>
    ///
    /// <para>
    /// <b>Head-fixed pose.</b> Unity's composition layer API has no view-space (head-locked)
    /// layer option: <c>OpenXRQuadLayer</c> always submits the quad with
    /// <c>Space = OpenXRLayerUtility.GetCurrentAppSpace()</c>, i.e. the application's
    /// tracking-origin space. Head-fixed placement is therefore done the only way the API
    /// allows — by writing the layer GameObject's world pose from the camera pose every
    /// <c>LateUpdate</c> (see <see cref="SetPose"/>).
    /// </para>
    ///
    /// <para>
    /// <b>Why a keep-alive layer is needed.</b> See <see cref="CreateManagerKeepAlive"/> —
    /// the OpenXR side only ever hands the manager a layer provider once, at session begin,
    /// and only if a manager instance happens to be alive at that exact moment.
    /// </para>
    /// </summary>
    internal sealed class HapbeatPanelCompositionLayerSurface
    {
        // Where the source Canvas is parked while it is being captured. It must be far
        // enough from the played content that the scene's own cameras never see it (the
        // panel would otherwise be visible twice: once in the eye buffer, once as a layer).
        private static readonly Vector3 k_ParkPosition = new Vector3(0f, -1000f, 0f);

        // Distance between the capture camera and the parked Canvas, in meters. Only has to
        // clear the near plane — the camera is orthographic, so this does not affect framing.
        private const float CaptureCameraDistance = 1f;

        // RenderTexture clamp, per axis. Upper bound keeps a large panel / high pixel
        // density from allocating an unreasonable swapchain; lower bound keeps a very small
        // panel legible.
        private const int MinTextureSize = 64;
        private const int MaxTextureSize = 2048;

        private readonly Canvas _canvas;
        private readonly GameObject _cameraGo;
        private readonly GameObject _layerGo;
        private readonly Camera _camera;
        private readonly CompositionLayer _layer;

        // Layer indices every object under the Canvas had before it was moved onto the UI
        // layer for capture — restored whenever capture is suspended (see SetCaptureEngaged),
        // so the panel goes back to rendering exactly as it did before.
        private readonly GameObject[] _canvasObjects;
        private readonly int[] _canvasOriginalLayers;

        // Effective visibility is (panel enabled) AND (a provider is actually running):
        // the panel toggles the first via SetActive, the second via SetCaptureEngaged.
        private bool _panelActive = true;
        private bool _captureEngaged = true;

        private RenderTexture _renderTexture;

        private HapbeatPanelCompositionLayerSurface(
            Canvas canvas, GameObject cameraGo, Camera camera, GameObject layerGo, CompositionLayer layer,
            RenderTexture renderTexture, GameObject[] canvasObjects, int[] canvasOriginalLayers)
        {
            _canvas = canvas;
            _cameraGo = cameraGo;
            _camera = camera;
            _layerGo = layerGo;
            _layer = layer;
            _renderTexture = renderTexture;
            _canvasObjects = canvasObjects;
            _canvasOriginalLayers = canvasOriginalLayers;
        }

        /// <summary>
        /// True once a composition layer provider is actually running — i.e. the OpenXR
        /// <c>Composition Layers</c> feature is enabled, the XR session has begun, and a
        /// manager instance existed at that moment (see <see cref="CreateManagerKeepAlive"/>).
        /// False means submitting a layer would render nothing, so the caller should keep
        /// using its in-eye-buffer fallback.
        ///
        /// <para>
        /// Note this can go false again mid-run: <c>OpenXRCompositionLayersFeature.OnSessionEnd</c>
        /// clears the provider, which happens on an ordinary headset doff, and restores it on
        /// the next session begin. Callers must treat it as a live signal, not a one-shot.
        /// </para>
        /// </summary>
        public static bool ProviderAvailable
        {
            get
            {
                var manager = CompositionLayerManager.Instance;
                return manager != null && manager.LayerProvider != null;
            }
        }

        /// <summary>
        /// True while a <c>CompositionLayerManager</c> instance exists. Purely diagnostic:
        /// it separates "the manager isn't even running" from "the manager is running but
        /// nobody gave it a provider" in the panel's fallback warning.
        /// </summary>
        public static bool ManagerAlive => CompositionLayerManager.Instance != null;

        /// <summary>
        /// Creates a texture-less quad layer whose only job is to keep a
        /// <c>CompositionLayerManager</c> instance alive, and returns its GameObject so the
        /// caller can destroy it once a real layer exists.
        ///
        /// <para>
        /// <b>Why.</b> <c>OpenXRCompositionLayersFeature</c> assigns the layer provider in
        /// exactly one place — <c>OnSessionBegin</c> — and it does so only
        /// <c>if (CompositionLayerManager.Instance != null)</c>. The manager, in turn, stops
        /// itself (<c>StopCompositionLayerManager</c>) as soon as an update finds no active
        /// and no known layers, and once stopped its <c>Instance</c> property returns null
        /// until something creates a layer again. So in a scene that contains no composition
        /// layer at startup — which is every scene that only creates one on demand, like this
        /// panel — the manager is already stopped when the session begins, the feature
        /// silently skips the assignment, and no layer created later will ever be composited,
        /// no matter how long the panel waits for a provider. Holding one enabled layer from
        /// the moment the panel is built keeps the manager alive across session begin (and
        /// across the end/begin cycle of a headset doff) so the assignment actually happens.
        /// </para>
        ///
        /// <para>
        /// The keep-alive carries no <see cref="TexturesExtension"/>, so
        /// <c>OpenXRQuadLayer.CreateSwapchain</c> returns false for it and nothing is
        /// allocated or drawn — it only occupies a layer order slot.
        /// </para>
        /// </summary>
        public static GameObject CreateManagerKeepAlive()
        {
            var go = new GameObject("HapbeatCompositionLayerKeepAlive");
            go.SetActive(false);
            var layer = go.AddComponent<CompositionLayer>();
            layer.ChangeLayerDataType<QuadLayerData>();
            if (layer.LayerData is QuadLayerData quad)
                quad.Size = Vector2.zero;
            go.SetActive(true);
            return go;
        }

        /// <summary>
        /// Parks <paramref name="canvas"/> out of the scene's view, points a dedicated
        /// orthographic camera at it, and creates a quad composition layer fed by that
        /// camera's RenderTexture. Returns null (after logging) if anything required is
        /// missing.
        /// </summary>
        /// <param name="canvas">The world-space Canvas to capture. Its transform is moved.</param>
        /// <param name="worldSize">Panel size in meters — used for both the capture framing and the quad size.</param>
        /// <param name="pixelDensity">Multiplier applied to the Canvas's own pixel size to get the RenderTexture resolution.</param>
        /// <param name="worldScale">World units per Canvas UI pixel (the Canvas's uniform local scale).</param>
        public static HapbeatPanelCompositionLayerSurface TryCreate(Canvas canvas, Vector2 worldSize, float pixelDensity, float worldScale)
        {
            if (canvas == null) return null;

            float width = Mathf.Max(worldSize.x, 0.001f);
            float height = Mathf.Max(worldSize.y, 0.001f);
            float scale = Mathf.Max(worldScale, 0.0001f);

            // Canvas pixel extent (worldSize / scale — the same figure Build() uses for
            // sizeDelta), scaled by the panel's text-sharpness knob so the capture matches
            // what the Canvas rasterizes rather than under- or over-sampling it.
            float density = Mathf.Max(pixelDensity, 1f);
            int textureWidth = Mathf.Clamp(Mathf.RoundToInt(width / scale * density), MinTextureSize, MaxTextureSize);
            int textureHeight = Mathf.Clamp(Mathf.RoundToInt(height / scale * density), MinTextureSize, MaxTextureSize);

            var renderTexture = new RenderTexture(textureWidth, textureHeight, 24, RenderTextureFormat.ARGB32)
            {
                name = "HapbeatAddressOverrideLayerRT",
            };
            renderTexture.Create();

            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5; // built-in UI layer index; NameToLayer only fails if it was renamed

            // Snapshot the Canvas's layer assignment before touching it, so suspending
            // capture (provider lost) can put the panel back exactly as it was.
            var canvasObjects = new List<GameObject>();
            CollectRecursively(canvas.gameObject, canvasObjects);
            var originalLayers = new int[canvasObjects.Count];
            for (int i = 0; i < canvasObjects.Count; i++)
                originalLayers[i] = canvasObjects[i].layer;

            // Capture camera: orthographic, framed exactly on the panel rect, clearing to
            // fully transparent so the quad's alpha comes from the panel's own graphics.
            // Placed on the +Z side looking back down -Z, because a Canvas faces its own +Z:
            // viewing it from -Z would capture its back and mirror every glyph.
            var cameraGo = new GameObject("HapbeatAddressOverrideLayerCamera");
            cameraGo.transform.SetPositionAndRotation(
                k_ParkPosition + Vector3.forward * CaptureCameraDistance,
                Quaternion.LookRotation(-Vector3.forward, Vector3.up));
            var camera = cameraGo.AddComponent<Camera>();
            camera.orthographic = true;
            camera.orthographicSize = height * 0.5f;
            camera.aspect = width / height;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = CaptureCameraDistance * 2f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
            camera.cullingMask = 1 << uiLayer;
            camera.useOcclusionCulling = false;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.depth = -100f; // renders before the scene cameras; the layer texture must be ready this frame
            camera.targetTexture = renderTexture;

            // Layer GameObject: built inactive so CompositionLayer/TexturesExtension see a
            // fully configured object in their OnEnable rather than registering a layer with
            // no data and no texture.
            var layerGo = new GameObject("HapbeatAddressOverrideCompositionLayer");
            layerGo.SetActive(false);
            var compositionLayer = layerGo.AddComponent<CompositionLayer>();
            compositionLayer.ChangeLayerDataType<QuadLayerData>();
            if (compositionLayer.LayerData is QuadLayerData quadLayerData)
            {
                quadLayerData.Size = new Vector2(width, height);
                // The layer GameObject carries no meaningful scale (only a pose is written
                // to it), so take the size verbatim instead of multiplying by lossyScale.
                quadLayerData.ApplyTransformScale = false;
            }
            else
            {
                Object.Destroy(layerGo);
                Object.Destroy(cameraGo);
                ReleaseTexture(renderTexture);
                Debug.LogWarning("[Hapbeat] HapbeatAddressOverridePanel: could not create a Quad composition layer " +
                    "(unexpected LayerData type). Falling back to the in-eye-buffer panel.");
                return null;
            }

            var texturesExtension = layerGo.AddComponent<TexturesExtension>();
            texturesExtension.sourceTexture = TexturesExtension.SourceTextureEnum.LocalTexture;
            texturesExtension.TargetEye = TexturesExtension.TargetEyeEnum.Both;
            texturesExtension.LeftTexture = renderTexture;
            texturesExtension.RightTexture = renderTexture;

            layerGo.SetActive(true);

            var surface = new HapbeatPanelCompositionLayerSurface(
                canvas, cameraGo, camera, layerGo, compositionLayer, renderTexture,
                canvasObjects.ToArray(), originalLayers);
            surface.ParkCanvas(uiLayer);
            return surface;
        }

        /// <summary>One-line description of what was actually created, for the success log.</summary>
        public string Describe()
        {
            int w = _renderTexture != null ? _renderTexture.width : 0;
            int h = _renderTexture != null ? _renderTexture.height : 0;
            int order = _layer != null ? _layer.Order : 0;
            return $"{w}x{h} RenderTexture, layer order {order}";
        }

        /// <summary>
        /// Writes the composition layer's world pose. Called every <c>LateUpdate</c> by the
        /// panel with a camera-derived pose — see the class doc for why head-fixed placement
        /// has to be done this way.
        /// </summary>
        public void SetPose(Vector3 position, Quaternion rotation)
        {
            if (_layerGo != null)
                _layerGo.transform.SetPositionAndRotation(position, rotation);
        }

        /// <summary>Shows/hides the layer and stops/starts the capture camera with it.</summary>
        public void SetActive(bool active)
        {
            _panelActive = active;
            ApplyVisibility();
        }

        /// <summary>
        /// Engages or suspends the capture. Suspending un-parks the Canvas and restores its
        /// original layers so the panel renders in the eye buffer again — used when the
        /// provider disappears mid-run (an XR session end clears it), so a headset doff
        /// leaves the panel visible instead of blank.
        /// </summary>
        public void SetCaptureEngaged(bool engaged)
        {
            if (_captureEngaged == engaged) return;
            _captureEngaged = engaged;

            if (engaged)
            {
                int uiLayer = LayerMask.NameToLayer("UI");
                if (uiLayer < 0) uiLayer = 5;
                ParkCanvas(uiLayer);
            }
            else
            {
                RestoreCanvasLayers();
            }

            ApplyVisibility();
        }

        /// <summary>Destroys the capture camera, the layer GameObject and the RenderTexture.</summary>
        public void Dispose()
        {
            RestoreCanvasLayers();
            if (_camera != null) _camera.targetTexture = null;
            if (_layerGo != null) Object.Destroy(_layerGo);
            if (_cameraGo != null) Object.Destroy(_cameraGo);
            ReleaseTexture(_renderTexture);
            _renderTexture = null;
        }

        private void ApplyVisibility()
        {
            bool visible = _panelActive && _captureEngaged;
            if (_layerGo != null) _layerGo.SetActive(visible);
            if (_camera != null) _camera.enabled = visible;
        }

        private void ParkCanvas(int uiLayer)
        {
            if (_canvas == null) return;
            _canvas.transform.SetPositionAndRotation(k_ParkPosition, Quaternion.identity);
            for (int i = 0; i < _canvasObjects.Length; i++)
            {
                if (_canvasObjects[i] != null)
                    _canvasObjects[i].layer = uiLayer;
            }
        }

        private void RestoreCanvasLayers()
        {
            if (_canvasObjects == null) return;
            for (int i = 0; i < _canvasObjects.Length; i++)
            {
                if (_canvasObjects[i] != null)
                    _canvasObjects[i].layer = _canvasOriginalLayers[i];
            }
        }

        private static void ReleaseTexture(RenderTexture renderTexture)
        {
            if (renderTexture == null) return;
            renderTexture.Release();
            Object.Destroy(renderTexture);
        }

        private static void CollectRecursively(GameObject go, List<GameObject> into)
        {
            into.Add(go);
            for (int i = 0; i < go.transform.childCount; i++)
                CollectRecursively(go.transform.GetChild(i).gameObject, into);
        }
    }
}
#endif
