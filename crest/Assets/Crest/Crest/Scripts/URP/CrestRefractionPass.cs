// Crest Ocean System - MIT License (see LICENSE)
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    /// <summary>Opt-in transparent composition followed by a resolved, full-resolution pre-water color copy.</summary>
    internal sealed class CrestRefractionPass : ScriptableRenderPass
    {
        internal static readonly int EnabledId = Shader.PropertyToID("_CrestRefractionEnabled");
        internal static readonly int DrawingId = Shader.PropertyToID("_CrestRefractionPass");
        static readonly int s_TextureId = Shader.PropertyToID("_CrestRefractionTexture");
        static readonly GlobalKeyword s_ShadowScreen = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_SCREEN");
        static readonly GlobalKeyword s_ShadowSingle = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS");
        static readonly GlobalKeyword s_ShadowCascades = GlobalKeyword.Create("_MAIN_LIGHT_SHADOWS_CASCADE");
        static readonly List<ShaderTagId> s_Tags = new List<ShaderTagId>
        {
            new ShaderTagId("SRPDefaultUnlit"), new ShaderTagId("UniversalForward"), new ShaderTagId("UniversalForwardOnly")
        };
        readonly bool _copy;
        internal LayerMask layers;

        sealed class DrawData
        {
            public RendererListHandle renderers;
            public bool shadows;
            public int cascades;
        }
        sealed class CopyData { public TextureHandle source, destination; }

        internal CrestRefractionPass(bool copy, RenderPassEvent at)
        {
            _copy = copy;
            renderPassEvent = at;
            ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);
            requiresIntermediateTexture = true;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            var camera = frameData.Get<UniversalCameraData>();
            if (camera.cameraTargetDescriptor.graphicsFormat == UnityEngine.Experimental.Rendering.GraphicsFormat.None) return;
            if (_copy)
            {
                if (resources.isActiveTargetBackBuffer) return;
                var descriptor = camera.cameraTargetDescriptor;
                descriptor.msaaSamples = 1;
                descriptor.bindMS = false;
                descriptor.depthBufferBits = 0;
                descriptor.depthStencilFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.None;
                descriptor.enableRandomWrite = false;
                using (var builder = graph.AddRasterRenderPass<CopyData>("Crest pre-water refraction color", out var data))
                {
                    data.source = resources.activeColorTexture;
                    // Distorted UVs need linear filtering; the URP helper defaults to point sampling.
                    data.destination = UniversalRenderer.CreateRenderGraphTexture(graph, descriptor, "Crest refraction color", false,
                        FilterMode.Bilinear, TextureWrapMode.Clamp);
                    builder.UseTexture(data.source, AccessFlags.Read);
                    builder.SetRenderAttachment(data.destination, 0, AccessFlags.Write);
                    builder.SetGlobalTextureAfterPass(data.destination, s_TextureId);
                    builder.AllowGlobalStateModification(true);
                    builder.SetRenderFunc((CopyData pass, RasterGraphContext context) =>
                    {
                        Blitter.BlitTexture(context.cmd, pass.source, new Vector4(1, 1, 0, 0), 0, false);
                        context.cmd.SetGlobalInt(EnabledId, 1);
                    });
                }
                return;
            }

            var rendering = frameData.Get<UniversalRenderingData>();
            var lights = frameData.Get<UniversalLightData>();
            var shadows = frameData.Get<UniversalShadowData>();
            var drawing = RenderingUtils.CreateDrawingSettings(s_Tags, rendering, camera, lights, SortingCriteria.CommonTransparent);
            var filter = new FilteringSettings(RenderQueueRange.transparent, layers);
            using (var builder = graph.AddRasterRenderPass<DrawData>("Crest refracted transparents", out var data))
            {
                data.renderers = graph.CreateRendererList(new RendererListParams(rendering.cullResults, drawing, filter));
                data.shadows = shadows.supportsMainLightShadows && resources.mainShadowsTexture.IsValid();
                data.cascades = shadows.mainLightShadowCascadesCount;
                builder.UseRendererList(data.renderers);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderAttachmentDepth(resources.activeDepthTexture, AccessFlags.ReadWrite);
                // Materials may sample opaque color, shadows, the water mask and other global graph textures.
                builder.UseAllGlobalTextures(true);
                if (resources.mainShadowsTexture.IsValid()) builder.UseTexture(resources.mainShadowsTexture);
                if (resources.additionalShadowsTexture.IsValid()) builder.UseTexture(resources.additionalShadowsTexture);
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((DrawData pass, RasterGraphContext context) =>
                {
                    context.cmd.SetGlobalInt(DrawingId, 1);
                    // URP's own screen-shadow reset runs at event 450. This earlier transparent draw needs
                    // the same atlas/cascade keywords, since opaque screen depth does not describe transparents.
                    context.cmd.SetKeyword(s_ShadowScreen, false);
                    context.cmd.SetKeyword(s_ShadowSingle, pass.shadows && pass.cascades == 1);
                    context.cmd.SetKeyword(s_ShadowCascades, pass.shadows && pass.cascades > 1);
                    context.cmd.SetGlobalVector("_DrawObjectPassData", Vector4.zero);
                    context.cmd.SetGlobalFloat("_AlphaToMaskAvailable", 0);
                    context.cmd.DrawRendererList(pass.renderers);
                    context.cmd.SetGlobalInt(DrawingId, 0);
                });
            }
        }
    }
}
