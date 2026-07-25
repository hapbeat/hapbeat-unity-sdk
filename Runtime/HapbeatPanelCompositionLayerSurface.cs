#if HAPBEAT_HAS_COMPOSITION_LAYERS
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

        private readonly GameObject _cameraGo;
        private readonly GameObject _layerGo;
        private readonly Camera _camera;
        private RenderTexture _renderTexture;

        private HapbeatPanelCompositionLayerSurface(GameObject cameraGo, Camera camera, GameObject layerGo, RenderTexture renderTexture)
        {
            _cameraGo = cameraGo;
            _camera = camera;
            _layerGo = layerGo;
            _renderTexture = renderTexture;
        }

        /// <summary>
        /// True once a composition layer provider is actually running — i.e. the OpenXR
        /// <c>Composition Layers</c> feature is enabled and its subsystem has started
        /// (<c>OpenXRCompositionLayersFeature</c> assigns
        /// <see cref="CompositionLayerManager.LayerProvider"/> on subsystem start). False
        /// means submitting a layer would render nothing, so the caller should keep using
        /// its in-eye-buffer fallback.
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

            // Park the Canvas. Everything under it moves to the UI layer so the capture
            // camera's culling mask can be narrowed to just this content.
            canvas.transform.SetPositionAndRotation(k_ParkPosition, Quaternion.identity);
            int uiLayer = LayerMask.NameToLayer("UI");
            if (uiLayer < 0) uiLayer = 5; // built-in UI layer index; NameToLayer only fails if it was renamed
            SetLayerRecursively(canvas.gameObject, uiLayer);

            // Capture camera: orthographic, framed exactly on the panel rect, clearing to
            // fully transparent so the quad's alpha comes from the panel's own graphics.
            var cameraGo = new GameObject("HapbeatAddressOverrideLayerCamera");
            cameraGo.transform.SetPositionAndRotation(
                k_ParkPosition - Vector3.forward * CaptureCameraDistance, Quaternion.identity);
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

            return new HapbeatPanelCompositionLayerSurface(cameraGo, camera, layerGo, renderTexture);
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
            if (_layerGo != null) _layerGo.SetActive(active);
            if (_camera != null) _camera.enabled = active;
        }

        /// <summary>Destroys the capture camera, the layer GameObject and the RenderTexture.</summary>
        public void Dispose()
        {
            if (_camera != null) _camera.targetTexture = null;
            if (_layerGo != null) Object.Destroy(_layerGo);
            if (_cameraGo != null) Object.Destroy(_cameraGo);
            ReleaseTexture(_renderTexture);
            _renderTexture = null;
        }

        private static void ReleaseTexture(RenderTexture renderTexture)
        {
            if (renderTexture == null) return;
            renderTexture.Release();
            Object.Destroy(renderTexture);
        }

        private static void SetLayerRecursively(GameObject go, int layer)
        {
            go.layer = layer;
            for (int i = 0; i < go.transform.childCount; i++)
                SetLayerRecursively(go.transform.GetChild(i).gameObject, layer);
        }
    }
}
#endif
