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
        // Example/custom inputs can record commands after the water mask, before scene depth/opaques.
        public static event System.Action<CommandBuffer, Camera, RenderTextureDescriptor> BeforeSceneRendering;
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetEvents() => BeforeSceneRendering = null;
        static readonly int s_MainLightShadowCascadeCount = Shader.PropertyToID("_CrestMainLightShadowCascadeCount");
        CrestPass _prepare, _before, _after;

        public override void Create()
        {
            _prepare = new CrestPass(0, RenderPassEvent.BeforeRenderingPrePasses);
            _before = new CrestPass(1, RenderPassEvent.BeforeRenderingTransparents);
            _after = new CrestPass(2, RenderPassEvent.BeforeRenderingPostProcessing);
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var camera = renderingData.cameraData.camera;
            if (OceanRenderer.Instance == null || camera.cameraType == CameraType.Reflection ||
                camera.cameraType == CameraType.Preview || renderingData.cameraData.renderType == CameraRenderType.Overlay)
                return;

            renderer.EnqueuePass(_prepare);
            // Always request depth and opaque color: the ocean surface needs both even without underwater.
            renderer.EnqueuePass(_before);
            var underwater = UnderwaterRenderer.Get(camera);
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
                public Matrix4x4 view, projection;
                public TextureHandle color, depth, copy, opaque;
                public bool effect;
            }

            public CrestPass(int stage, RenderPassEvent at)
            {
                _stage = stage;
                renderPassEvent = at;
                if (stage != 0)
                {
                    ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Color);
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
                    data.projection = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(),
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
                            cmd.DisableShaderKeyword("CREST_WATER_VOLUME_2D");
                            cmd.DisableShaderKeyword("CREST_WATER_VOLUME_HAS_BACKFACE");
                            cmd.DisableShaderKeyword("CREST_UNDERWATER_BEFORE_TRANSPARENT");
                            var ocean = OceanRenderer.Instance;
                            if (ocean == null) return;
                            foreach (var tile in ocean.Tiles) tile.BindOceanData(pass.camera);
                            if (pass.camera == ocean.ViewCamera && ocean._lodDataShadow != null &&
                                ocean._lodDataShadow.enabled && ocean._lodDataShadow.BufCopyShadowMap != null && LodDataMgrShadow.s_processData)
                                ocean._lodDataShadow.BuildCommandBuffer(ocean, cmd);
                            RestoreCamera(pass, cmd);
                            if (pass.underwater != null && pass.underwater.IsActive)
                                pass.underwater.RecordURPMask(cmd, pass.camera, pass.descriptor);
                            BeforeSceneRendering?.Invoke(cmd, pass.camera, pass.descriptor);
                            RestoreCamera(pass, cmd);
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
                RenderingUtils.SetViewAndProjectionMatrices(cmd, pass.view, pass.projection, true);
                cmd.SetViewport(new Rect(0, 0, pass.descriptor.width, pass.descriptor.height));
            }
        }
    }
}
