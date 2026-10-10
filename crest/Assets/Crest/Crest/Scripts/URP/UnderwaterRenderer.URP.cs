// Crest Ocean System - MIT License (see LICENSE)
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;

namespace Crest
{
    public partial class UnderwaterRenderer
    {
        RTHandle _urpMask, _urpDepth, _urpVolumeFront, _urpVolumeBack;
        RenderTextureDescriptor _urpDescriptor;
        bool _recordingURP;

        internal void DeclareURPTextures(RenderGraph graph, IUnsafeRenderGraphBuilder builder,
            RenderTextureDescriptor descriptor, bool write)
        {
            if (_overlay) return;
            SetUpMaskTextures(descriptor);
            if (_mode != Mode.FullScreen && _volumeGeometry != null) SetUpVolumeTextures(descriptor);
            if (!_maskRT.IsCreated()) _maskRT.Create();
            if (!_depthRT.IsCreated()) _depthRT.Create();
            if (_mode != Mode.FullScreen && _volumeGeometry != null)
            {
                if (!_volumeFrontFaceRT.IsCreated()) _volumeFrontFaceRT.Create();
                if (_mode != Mode.Portal && !_volumeBackFaceRT.IsCreated()) _volumeBackFaceRT.Create();
            }
            if (_urpMask == null)
            {
                _urpMask = RTHandles.Alloc(_maskRT);
                _urpDepth = RTHandles.Alloc(_depthRT);
                _urpVolumeFront = RTHandles.Alloc(_volumeFrontFaceRT);
                _urpVolumeBack = RTHandles.Alloc(_volumeBackFaceRT);
            }
            var access = write ? AccessFlags.ReadWrite : AccessFlags.Read;
            var mask = graph.ImportTexture(_urpMask);
            var depth = graph.ImportTexture(_urpDepth);
            builder.UseTexture(mask, access);
            builder.UseTexture(depth, access);
            if (write)
            {
                builder.SetGlobalTextureAfterPass(mask, ShaderIDs.s_CrestOceanMaskTexture);
                builder.SetGlobalTextureAfterPass(depth, ShaderIDs.s_CrestOceanMaskDepthTexture);
            }
            if (_mode != Mode.FullScreen && _volumeGeometry != null)
            {
                var front = graph.ImportTexture(_urpVolumeFront);
                builder.UseTexture(front, access);
                if (write) builder.SetGlobalTextureAfterPass(front, ShaderIDs.s_CrestWaterVolumeFrontFaceTexture);
                if (_mode != Mode.Portal)
                {
                    var back = graph.ImportTexture(_urpVolumeBack);
                    builder.UseTexture(back, access);
                    if (write) builder.SetGlobalTextureAfterPass(back, ShaderIDs.s_CrestWaterVolumeBackFaceTexture);
                }
            }
        }

        void PrepareURPCamera(Camera camera, RenderTextureDescriptor descriptor)
        {
            _camera = camera;
            _urpDescriptor = descriptor;
            XRHelpers.Update(camera);
            // URP supplies UNITY_MATRIX_I_VP; retain these for shared material API consumers.
            _gpuInverseViewProjectionMatrix = (GL.GetGPUProjectionMatrix(camera.projectionMatrix, true) * camera.worldToCameraMatrix).inverse;
        }

        internal void RecordURPMask(CommandBuffer buffer, Camera camera, RenderTextureDescriptor descriptor)
        {
            var originalCamera = _camera;
            try
            {
                PrepareURPCamera(camera, descriptor);
                _recordingURP = true;
                SetURPKeyword(buffer, "CREST_UNDERWATER_BEFORE_TRANSPARENT", _enableShaderAPI);
                if (!_overlay && Helpers.MaskIncludesLayer(camera.cullingMask, _layer)) OnPreRenderOceanMask(buffer);
            }
            finally { _recordingURP = false; _camera = originalCamera; }
        }

        internal void RecordURPEffect(CommandBuffer buffer, Camera camera, RenderTextureDescriptor descriptor, RenderTargetIdentifier color)
        {
            if (!Helpers.MaskIncludesLayer(camera.cullingMask, _layer)) return;
            var originalCamera = _camera;
            var originalColor = _colorTarget;
            try
            {
                PrepareURPCamera(camera, descriptor);
                _recordingURP = true;
                SetURPKeyword(buffer, "CREST_UNDERWATER_BEFORE_TRANSPARENT", _enableShaderAPI);
                _colorTarget = color;
                OnPreRenderUnderwaterEffect(buffer);
                _firstRender = false;
            }
            finally { _recordingURP = false; _camera = originalCamera; _colorTarget = originalColor; }
        }

        void ReleaseURPHandles()
        {
            // Wrappers do not own the RenderTextures. OnDisableMask manages the underlying textures.
            _urpMask?.Release(); _urpMask = null;
            _urpDepth?.Release(); _urpDepth = null;
            _urpVolumeFront?.Release(); _urpVolumeFront = null;
            _urpVolumeBack?.Release(); _urpVolumeBack = null;
        }

        static void SetURPKeyword(CommandBuffer buffer, string keyword, bool enabled)
        {
            if (buffer == null) Helpers.SetGlobalKeyword(keyword, enabled);
            else if (enabled) buffer.EnableShaderKeyword(keyword);
            else buffer.DisableShaderKeyword(keyword);
        }
    }
}
