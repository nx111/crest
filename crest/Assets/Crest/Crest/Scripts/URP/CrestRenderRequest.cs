// Crest Ocean System - MIT License (see LICENSE)
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Crest
{
    internal static class CrestRenderRequest
    {
        // Reflection probes also use CameraType.Reflection, but do not have a mirrored view.
        internal static Camera ReflectionCamera { get; private set; }

        internal static void Render(Camera camera, RenderTexture destination, bool shadows, bool invertCulling = false)
        {
            if (GraphicsSettings.currentRenderPipeline == null)
            {
                camera.Render();
                return;
            }

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
            var previousReflectionCamera = ReflectionCamera;
            ReflectionCamera = invertCulling ? camera : null;
            try
            {
                RenderPipeline.SubmitRenderRequest(camera, request);
            }
            finally
            {
                ReflectionCamera = previousReflectionCamera;
            }
        }
    }
}
