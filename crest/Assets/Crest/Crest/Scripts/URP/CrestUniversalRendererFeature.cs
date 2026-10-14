// Crest Ocean System - MIT License (see LICENSE)
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    /// <summary>URP 17 Render Graph integration. Add once to every renderer used by water cameras.</summary>
    public sealed class CrestUniversalRendererFeature : ScriptableRendererFeature
    {
        [Tooltip("Transparent objects drawn before water and included in refraction. Exclude these layers from the Universal Renderer's Transparent Layer Mask. Do not include the water layer.")]
        [SerializeField] LayerMask _refractedLayers;
        public LayerMask RefractedLayers { get => _refractedLayers; set => _refractedLayers = value; }

        // Example/custom inputs can record commands after the water mask, before scene depth/opaques.
        public static event System.Action<CommandBuffer, Camera, RenderTextureDescriptor> BeforeSceneRendering;
        public static event System.Action<CommandBuffer, Camera, RenderTextureDescriptor> AfterDepthRendering;
        public static event System.Action<CommandBuffer, Camera, RenderTextureDescriptor> AfterOpaqueRendering;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetEvents()
        {
            BeforeSceneRendering = null;
            AfterDepthRendering = null;
            AfterOpaqueRendering = null;
        }
        static readonly int s_MainLightShadowCascadeCount = Shader.PropertyToID("_CrestMainLightShadowCascadeCount");
        CrestPass _prepare, _before, _after;
        CrestRefractionPass _refractionObjects, _refractionCopy;
        CrestCallbackPass _afterDepth, _afterOpaque;
        CrestReflectionCullPass _reflectionInvert, _reflectionRestore;

        public override void Create()
        {
            _prepare = new CrestPass(0, RenderPassEvent.BeforeRenderingPrePasses);
            _refractionObjects = new CrestRefractionPass(false, (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 2));
            _before = new CrestPass(1, (RenderPassEvent)((int)RenderPassEvent.BeforeRenderingTransparents - 1));
            _refractionCopy = new CrestRefractionPass(true, RenderPassEvent.BeforeRenderingTransparents);
            _after = new CrestPass(2, RenderPassEvent.BeforeRenderingPostProcessing);
            _afterDepth = new CrestCallbackPass(true, RenderPassEvent.AfterRenderingPrePasses);
            _afterOpaque = new CrestCallbackPass(false, RenderPassEvent.AfterRenderingOpaques);
            _reflectionInvert = new CrestReflectionCullPass(true);
            _reflectionRestore = new CrestReflectionCullPass(false);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (camera.cameraType == CameraType.Preview ||
                renderingData.cameraData.cameraTargetDescriptor.graphicsFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None)
                return;

            if (camera == CrestRenderRequest.ReflectionCamera)
            {
                renderer.EnqueuePass(_reflectionInvert);
                renderer.EnqueuePass(_reflectionRestore);
            }

            // These layers replace the renderer's ordinary transparent draw, including when no ocean is active,
            // in reflections and on overlay cameras. Otherwise excluded objects would disappear in those views.
            if (_refractedLayers.value != 0)
            {
                _refractionObjects.layers = _refractedLayers;
                renderer.EnqueuePass(_refractionObjects);
            }
            if (OceanRenderer.Instance == null || camera.cameraType == CameraType.Reflection ||
                renderingData.cameraData.renderType == CameraRenderType.Overlay) return;

            renderer.EnqueuePass(_prepare);
            var underwater = UnderwaterRenderer.Get(camera);
            var earlyUnderwater = underwater != null && underwater.isActiveAndEnabled && underwater.IsActive &&
                underwater.EnableShaderAPI && Helpers.MaskIncludesLayer(camera.cullingMask, underwater._layer);
            // The custom refraction copy already supplies surface background color. Early fog also refreshes
            // opaque color for shader API consumers, so retain that input only when it is actually used.
            _before.ConfigureInput(ScriptableRenderPassInput.Depth |
                ((_refractedLayers.value == 0 || earlyUnderwater) ? ScriptableRenderPassInput.Color : ScriptableRenderPassInput.None));
            renderer.EnqueuePass(_before);
            if (AfterDepthRendering != null) renderer.EnqueuePass(_afterDepth);
            if (AfterOpaqueRendering != null) renderer.EnqueuePass(_afterOpaque);
            if (_refractedLayers.value != 0) renderer.EnqueuePass(_refractionCopy);
            if (underwater != null && underwater.isActiveAndEnabled && !underwater.EnableShaderAPI)
                renderer.EnqueuePass(_after);
        }

        sealed class CrestPass : ScriptableRenderPass
        {
            readonly int _stage;
            sealed class PassData
            {
                public int stage;
                public Camera camera;
                public UnderwaterRenderer underwater;
                public RenderTextureDescriptor descriptor;
                public int shadowCascadeCount;
                public Matrix4x4 view, projection, gpuProjection;
                public TextureHandle color, depth, copy, opaque;
                public bool effect;
            }

            public CrestPass(int stage, RenderPassEvent at)
            {
                _stage = stage;
                renderPassEvent = at;
                if (stage != 0)
                {
                    ConfigureInput(ScriptableRenderPassInput.Depth);
                    requiresIntermediateTexture = true;
                }
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var resources = frameData.Get<UniversalResourceData>();
                var cameraData = frameData.Get<UniversalCameraData>();
                var shadowData = frameData.Get<UniversalShadowData>();
                var underwater = UnderwaterRenderer.Get(cameraData.camera);
                if (underwater != null && !underwater.isActiveAndEnabled) underwater = null;
                if (underwater != null && !Helpers.MaskIncludesLayer(cameraData.camera.cullingMask, underwater._layer)) underwater = null;
                var effect = underwater != null && underwater.IsActive &&
                    ((_stage == 1 && underwater.EnableShaderAPI) || (_stage == 2 && !underwater.EnableShaderAPI));
                if (_stage != 0 && !effect) return;
                // Reading the swapchain is not supported. requiresIntermediateTexture normally prevents this.
                if (effect && resources.isActiveTargetBackBuffer) return;

                var descriptor = cameraData.cameraTargetDescriptor;
                var copyDescriptor = descriptor;
                copyDescriptor.msaaSamples = 1;
                copyDescriptor.bindMS = false;
                copyDescriptor.depthBufferBits = 0;
                copyDescriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
                using (var builder = graph.AddUnsafePass<PassData>(
                    _stage == 0 ? "Crest shadows and water mask" : "Crest underwater", out var data))
                {
                    data.stage = _stage;
                    data.camera = cameraData.camera;
                    data.underwater = underwater;
                    data.descriptor = descriptor;
                    data.shadowCascadeCount = shadowData.mainLightShadowCascadesCount;
                    data.view = cameraData.GetViewMatrix();
                    data.projection = cameraData.GetProjectionMatrix();
                    data.gpuProjection = GL.GetGPUProjectionMatrix(data.projection,
                        !resources.isActiveTargetBackBuffer || cameraData.targetTexture != null);
                    data.effect = effect;
                    data.color = resources.activeColorTexture;
                    data.depth = resources.cameraDepthTexture;
                    data.opaque = resources.cameraOpaqueTexture;
                    if (resources.mainShadowsTexture.IsValid()) builder.UseTexture(resources.mainShadowsTexture);
                    if (effect)
                    {
                        builder.UseTexture(data.color, AccessFlags.ReadWrite);
                        builder.UseTexture(data.depth);
                        data.copy = UniversalRenderer.CreateRenderGraphTexture(graph, copyDescriptor, "Crest camera color", false);
                        builder.UseTexture(data.copy, AccessFlags.ReadWrite);
                        // Refresh opaque color after early underwater fog so refraction does not sample unfogged color.
                        if (_stage == 1 && data.opaque.IsValid()) builder.UseTexture(data.opaque, AccessFlags.Write);
                    }
                    if (underwater != null && underwater.IsActive)
                        underwater.DeclareURPTextures(graph, builder, descriptor, _stage == 0);
                    else if (_stage == 0)
                    {
                        builder.SetGlobalTextureAfterPass(graph.defaultResources.blackTextureXR, UnderwaterRenderer.ShaderIDs.s_CrestOceanMaskTexture);
                        builder.SetGlobalTextureAfterPass(graph.defaultResources.blackTextureXR, UnderwaterRenderer.ShaderIDs.s_CrestOceanMaskDepthTexture);
                        builder.SetGlobalTextureAfterPass(graph.defaultResources.blackTextureXR, UnderwaterRenderer.ShaderIDs.s_CrestWaterVolumeFrontFaceTexture);
                        builder.SetGlobalTextureAfterPass(graph.defaultResources.blackTextureXR, UnderwaterRenderer.ShaderIDs.s_CrestWaterVolumeBackFaceTexture);
                    }
                    // Crest's LOD textures are persistent external simulation resources.
                    // Preserve the pass and ordering across global texture/keyword updates.
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData pass, UnsafeGraphContext context) =>
                    {
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        cmd.SetGlobalInt(s_MainLightShadowCascadeCount, pass.shadowCascadeCount);
                        if (pass.stage == 0)
                        {
                            // Resolve waterline handling per rendered camera, after URP's MSAA fallbacks.
                            cmd.SetGlobalInt(OceanRenderer.sp_CrestDepthTextureOffset, pass.descriptor.msaaSamples > 1 ? 1 : 0);
                            cmd.SetGlobalInt(CrestRefractionPass.EnabledId, 0);
                            cmd.SetGlobalInt(CrestRefractionPass.DrawingId, 0);
                            cmd.DisableShaderKeyword("CREST_WATER_VOLUME_2D");
                            cmd.DisableShaderKeyword("CREST_WATER_VOLUME_HAS_BACKFACE");
                            cmd.DisableShaderKeyword("CREST_UNDERWATER_BEFORE_TRANSPARENT");
                            var ocean = OceanRenderer.Instance;
                            if (ocean == null) return;
                            if (pass.camera == ocean.ViewCamera && ocean._lodDataShadow != null &&
                                ocean._lodDataShadow.enabled && ocean._lodDataShadow.BufCopyShadowMap != null && LodDataMgrShadow.s_processData)
                                ocean._lodDataShadow.BuildCommandBuffer(ocean, cmd);
                            RestoreCamera(pass, cmd);
                            if (pass.underwater != null && pass.underwater.IsActive)
                                pass.underwater.RecordURPMask(cmd, pass.camera, pass.descriptor);
                            RestoreCamera(pass, cmd);
                            if (BeforeSceneRendering != null)
                            {
                                BeforeSceneRendering.Invoke(cmd, pass.camera, pass.descriptor);
                                RestoreCamera(pass, cmd);
                            }
                        }
                        else if (pass.effect)
                        {
                            context.cmd.SetRenderTarget(pass.copy);
                            Blitter.BlitTexture(cmd, pass.color, new Vector4(1, 1, 0, 0), 0, false);
                            cmd.SetGlobalTexture(UnderwaterRenderer.ShaderIDs.s_CrestCameraColorTexture, pass.copy);
                            cmd.SetGlobalTexture("_CameraDepthTexture", pass.depth);
                            pass.underwater.RecordURPEffect(cmd, pass.camera, pass.descriptor, pass.color);
                            if (pass.stage == 1 && pass.opaque.IsValid())
                            {
                                context.cmd.SetRenderTarget(pass.opaque);
                                Blitter.BlitTexture(cmd, pass.color, new Vector4(1, 1, 0, 0), 0, false);
                            }
                        }
                    });
                }
            }

            static void RestoreCamera(PassData pass, CommandBuffer cmd)
            {
                // Explicit simulation draws change the legacy camera state and target viewport.
                cmd.SetupCameraProperties(pass.camera);
                // Match URP's camera setup: native camera state takes the non-GPU projection.
                // Overwriting only global GPU matrices can disagree with legacy camera constants
                // and flip opaque geometry on Vulkan while offscreen captures still look correct.
                cmd.SetViewProjectionMatrices(pass.view, pass.projection);
                var inverseView = pass.view.inverse;
                var inverseProjection = pass.gpuProjection.inverse;
                var worldToCamera = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * pass.view;
                cmd.SetGlobalMatrix("unity_WorldToCamera", worldToCamera);
                cmd.SetGlobalMatrix("unity_CameraToWorld", worldToCamera.inverse);
                cmd.SetGlobalMatrix("unity_MatrixInvV", inverseView);
                cmd.SetGlobalMatrix("unity_MatrixInvP", inverseProjection);
                cmd.SetGlobalMatrix("unity_MatrixInvVP", inverseView * inverseProjection);
                cmd.SetViewport(new Rect(0, 0, pass.descriptor.width, pass.descriptor.height));
            }
        }

        sealed class CrestCallbackPass : ScriptableRenderPass
        {
            readonly bool _afterDepth;
            sealed class PassData
            {
                public Camera camera;
                public RenderTextureDescriptor descriptor;
                public TextureHandle color, depth;
                public System.Action<CommandBuffer, Camera, RenderTextureDescriptor> callback;
            }

            public CrestCallbackPass(bool afterDepth, RenderPassEvent at)
            {
                _afterDepth = afterDepth;
                renderPassEvent = at;
            }

            public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
            {
                var camera = frameData.Get<UniversalCameraData>();
                var resources = frameData.Get<UniversalResourceData>();
                using (var builder = graph.AddUnsafePass<PassData>("Crest camera callbacks", out var data))
                {
                    data.camera = camera.camera;
                    data.descriptor = camera.cameraTargetDescriptor;
                    data.color = resources.activeColorTexture;
                    data.depth = resources.activeDepthTexture;
                    data.callback = _afterDepth ? AfterDepthRendering : AfterOpaqueRendering;
                    builder.UseTexture(data.color, AccessFlags.ReadWrite);
                    builder.UseTexture(data.depth, AccessFlags.ReadWrite);
                    builder.UseAllGlobalTextures(true);
                    builder.AllowPassCulling(false);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((PassData pass, UnsafeGraphContext context) =>
                    {
                        // Depth-writing callbacks must target the scene attachments, never the water mask.
                        context.cmd.SetRenderTarget(pass.color, pass.depth);
                        var cmd = CommandBufferHelpers.GetNativeCommandBuffer(context.cmd);
                        pass.callback?.Invoke(cmd, pass.camera, pass.descriptor);
                        context.cmd.SetRenderTarget(pass.color, pass.depth);
                        cmd.SetViewport(new Rect(0, 0, pass.descriptor.width, pass.descriptor.height));
                    });
                }
            }
        }
    }
}
