// Crest Ocean System - MIT License (see LICENSE)
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    internal sealed class CrestReflectionCullPass : ScriptableRenderPass
    {
        readonly bool _invertCulling;

        sealed class PassData
        {
            public bool invertCulling;
        }

        internal CrestReflectionCullPass(bool invertCulling)
        {
            _invertCulling = invertCulling;
            // Include depth/depth-normal prepasses as well as opaque and transparent draws.
            renderPassEvent = invertCulling ? RenderPassEvent.AfterRenderingShadows : RenderPassEvent.AfterRenderingTransparents;
        }

        public override void RecordRenderGraph(RenderGraph graph, ContextContainer frameData)
        {
            if (frameData.Get<UniversalCameraData>().camera != CrestRenderRequest.ReflectionCamera) return;

            using (var builder = graph.AddUnsafePass<PassData>(
                _invertCulling ? "Crest reflection invert culling" : "Crest reflection restore culling", out var data))
            {
                data.invertCulling = _invertCulling;
                // These state changes must execute even without any visible scene geometry.
                builder.AllowGlobalStateModification(true);
                builder.SetRenderFunc((PassData pass, UnsafeGraphContext context) =>
                    context.cmd.SetInvertCulling(pass.invertCulling));
            }
        }
    }
}
