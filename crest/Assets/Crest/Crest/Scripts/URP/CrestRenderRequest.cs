// Crest Ocean System - MIT License (see LICENSE)
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    internal static class CrestRenderRequest
    {
        internal static void Render(Camera camera, RenderTexture destination, bool shadows)
        {
            var data = camera.GetUniversalAdditionalCameraData();
            data.renderType = CameraRenderType.Base;
            data.renderPostProcessing = false;
            data.renderShadows = shadows;
            data.requiresColorOption = CameraOverrideOption.Off;
            data.requiresDepthOption = CameraOverrideOption.Off;
            data.allowXRRendering = false;
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = destination };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
                throw new System.InvalidOperationException("Crest requires an active URP asset to render depth caches and reflections.");
            RenderPipeline.SubmitRenderRequest(camera, request);
        }
    }
}
