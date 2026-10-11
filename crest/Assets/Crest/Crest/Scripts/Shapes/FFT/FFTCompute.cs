// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

// Inspired by https://github.com/speps/GX-EncinoWaves

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Crest
{
    /// <summary>
    /// Runs FFT to generate water surface displacements
    /// </summary>
    public class FFTCompute
    {
        // Must match 'SIZE' param of first kernel in FFTCompute.compute
        const int FFT_KERNEL_0_RESOLUTION = 8;

        // Must match CASCADE_COUNT in FFTCompute.compute
        public const int CASCADE_COUNT = 16;

        bool _isInitialised = false;

        RenderTexture _spectrumInit;
        RenderTexture _spectrumHeight;
        RenderTexture _spectrumDisplaceX;
        RenderTexture _spectrumDisplaceZ;
        RenderTexture _tempFFT1;
        RenderTexture _tempFFT2;
        RenderTexture _tempFFT3;

        Texture2D _texButterfly;

        /// <summary>
        /// Generated 'raw', uncombined, wave data. Input for putting into AnimWaves data before combine pass.
        /// </summary>
        RenderTexture _waveBuffers;

        Texture2D _texSpectrumControls;
        bool _spectrumInitialised = false;
        Color[] _spectrumDataScratch = new Color[OceanWaveSpectrum.NUM_OCTAVES];

        ComputeShader _shaderSpectrum;
        ComputeShader _shaderFFT;

        int _kernelSpectrumInit;
        int _kernelSpectrumUpdate;

        // Generation data
        int _resolution;
        float _loopPeriod;
        float _windSpeed;
        float _windTurbulence;
        float _windDirRad;
        OceanWaveSpectrum _spectrum;

        float _generationTime = float.NaN;
        int _lastUsedFrame = -1;

        public static class ShaderIDs
        {
            public static readonly int s_Size = Shader.PropertyToID("_Size");
            public static readonly int s_WindSpeed = Shader.PropertyToID("_WindSpeed");
            public static readonly int s_Turbulence = Shader.PropertyToID("_Turbulence");
            public static readonly int s_Gravity = Shader.PropertyToID("_Gravity");
            public static readonly int s_Period = Shader.PropertyToID("_Period");
            public static readonly int s_WindDir = Shader.PropertyToID("_WindDir");
            public static readonly int s_SpectrumControls = Shader.PropertyToID("_SpectrumControls");
            public static readonly int s_ResultInit = Shader.PropertyToID("_ResultInit");
            public static readonly int s_Time = Shader.PropertyToID("_Time");
            public static readonly int s_Chop = Shader.PropertyToID("_Chop");
            public static readonly int s_Init0 = Shader.PropertyToID("_Init0");
            public static readonly int s_ResultHeight = Shader.PropertyToID("_ResultHeight");
            public static readonly int s_ResultDisplaceX = Shader.PropertyToID("_ResultDisplaceX");
            public static readonly int s_ResultDisplaceZ = Shader.PropertyToID("_ResultDisplaceZ");
            public static readonly int s_InputH = Shader.PropertyToID("_InputH");
            public static readonly int s_InputX = Shader.PropertyToID("_InputX");
            public static readonly int s_InputZ = Shader.PropertyToID("_InputZ");
            public static readonly int s_InputButterfly = Shader.PropertyToID("_InputButterfly");
            public static readonly int s_Output1 = Shader.PropertyToID("_Output1");
            public static readonly int s_Output2 = Shader.PropertyToID("_Output2");
            public static readonly int s_Output3 = Shader.PropertyToID("_Output3");
            public static readonly int s_Output = Shader.PropertyToID("_Output");
        }

        public FFTCompute(int resolution, float loopPeriod, float windSpeed, float windTurbulence, float windDirRad, OceanWaveSpectrum spectrum)
        {
            Debug.Assert(Mathf.NextPowerOfTwo(resolution) == resolution, "Crest: FFTCompute resolution must be power of 2");

            _resolution = resolution;
            _loopPeriod = loopPeriod;
            _windSpeed = windSpeed;
            _windTurbulence = windTurbulence;
            _windDirRad = windDirRad;
            _spectrum = spectrum;
        }

        public void Release()
        {
            if (_texButterfly != null) Helpers.Destroy(_texButterfly);
            if (_texSpectrumControls != null) Helpers.Destroy(_texSpectrumControls);
            if (_spectrumInit != null) _spectrumInit.Release();
            if (_spectrumHeight != null) _spectrumHeight.Release();
            if (_spectrumDisplaceX != null) _spectrumDisplaceX.Release();
            if (_spectrumDisplaceZ != null) _spectrumDisplaceZ.Release();
            if (_tempFFT1 != null) _tempFFT1.Release();
            if (_tempFFT2 != null) _tempFFT2.Release();
            if (_tempFFT3 != null) _tempFFT3.Release();

            if (_waveBuffers != null)
            {
                Helpers.Destroy(_waveBuffers);
                _waveBuffers = null;
            }

            _isInitialised = false;
            _spectrumInitialised = false;
            _generationTime = float.NaN;
        }

        void InitializeTextures()
        {
            Release();

            _shaderSpectrum = Resources.Load<ComputeShader>("FFT/FFTSpectrum");
            _kernelSpectrumInit = _shaderSpectrum.FindKernel("SpectrumInitalize");
            _kernelSpectrumUpdate = _shaderSpectrum.FindKernel("SpectrumUpdate");
            _shaderFFT = Resources.Load<ComputeShader>("FFT/FFTCompute");

            _texButterfly = new Texture2D(_resolution, Mathf.RoundToInt(Mathf.Log(_resolution, 2)), TextureFormat.RGBAFloat, false, true);

            _texSpectrumControls = new Texture2D(Crest.OceanWaveSpectrum.NUM_OCTAVES, 1, TextureFormat.RFloat, false, true);

            RenderTextureDescriptor rtd = new RenderTextureDescriptor();
            rtd.width = rtd.height = _resolution;
            rtd.dimension = TextureDimension.Tex2DArray;
            rtd.enableRandomWrite = true;
            rtd.depthBufferBits = 0;
            rtd.volumeDepth = CASCADE_COUNT;
            rtd.graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat;
            rtd.msaaSamples = 1;

            Helpers.SafeCreateRenderTexture(ref _spectrumInit, rtd);
            _spectrumInit.name = "CrestFFTSpectrumInit";
            _spectrumInit.Create();

            rtd.graphicsFormat = Helpers.GetCompatibleTextureFormat(GraphicsFormat.R32G32_SFloat, Helpers.s_DataGraphicsFormatUsage, "FFT", true);

            Helpers.SafeCreateRenderTexture(ref _spectrumHeight, rtd);
            _spectrumHeight.name = "CrestFFTSpectrumHeight";
            _spectrumHeight.Create();
            Helpers.SafeCreateRenderTexture(ref _spectrumDisplaceX, rtd);
            _spectrumDisplaceX.name = "CrestFFTSpectrumDisplaceX";
            _spectrumDisplaceX.Create();
            Helpers.SafeCreateRenderTexture(ref _spectrumDisplaceZ, rtd);
            _spectrumDisplaceZ.name = "CrestFFTSpectrumDisplaceZ";
            _spectrumDisplaceZ.Create();

            Helpers.SafeCreateRenderTexture(ref _tempFFT1, rtd);
            _tempFFT1.name = "CrestFFTOutput1";
            _tempFFT1.Create();
            Helpers.SafeCreateRenderTexture(ref _tempFFT2, rtd);
            _tempFFT2.name = "CrestFFTOutput2";
            _tempFFT2.Create();
            Helpers.SafeCreateRenderTexture(ref _tempFFT3, rtd);
            _tempFFT3.name = "CrestFFTOutput3";
            _tempFFT3.Create();

            // Raw wave data buffer
            _waveBuffers = new RenderTexture(_resolution, _resolution, 0, GraphicsFormat.R16G16B16A16_SFloat);
            _waveBuffers.wrapMode = TextureWrapMode.Repeat;
            _waveBuffers.antiAliasing = 1;
            _waveBuffers.filterMode = FilterMode.Bilinear;
            _waveBuffers.anisoLevel = 0;
            _waveBuffers.useMipMap = false;
            _waveBuffers.name = "FFTCascades";
            _waveBuffers.dimension = TextureDimension.Tex2DArray;
            _waveBuffers.volumeDepth = CASCADE_COUNT;
            _waveBuffers.enableRandomWrite = true;
            _waveBuffers.Create();

            InitializeButterfly(_resolution);

            InitialiseSpectrumHandControls();

            _isInitialised = true;
        }

        void CleanUp()
        {
            // Destroy to clear references.
            Helpers.Destroy(_spectrumInit);
            Helpers.Destroy(_spectrumHeight);
            Helpers.Destroy(_spectrumDisplaceX);
            Helpers.Destroy(_spectrumDisplaceZ);
            Helpers.Destroy(_tempFFT1);
            Helpers.Destroy(_tempFFT2);
            Helpers.Destroy(_tempFFT3);
            _spectrumInitialised = false;
        }

        internal static void CleanUpAll()
        {
            foreach (var generator in _generators)
            {
                generator.Value.Release();
                generator.Value.CleanUp();
            }

            _generators.Clear();
        }

        // A hash alone is not a wave condition identity. In particular, XORing
        // floats makes exchanging wind speed and turbulence produce the same key.
        readonly struct WaveConditions : System.IEquatable<WaveConditions>
        {
            readonly int _resolution;
            readonly float _loopPeriod;
            readonly float _windTurbulence;
            readonly float _windDirRad;
            readonly float _windSpeed;
            readonly OceanWaveSpectrum _spectrum;

            public WaveConditions(int resolution, float loopPeriod, float windTurbulence, float windDirRad, float windSpeed, OceanWaveSpectrum spectrum)
            {
                _resolution = resolution;
                _loopPeriod = loopPeriod;
                _windTurbulence = windTurbulence;
                _windDirRad = windDirRad;
                _windSpeed = windSpeed;
                _spectrum = spectrum;
            }

            public bool Equals(WaveConditions other)
            {
                return _resolution == other._resolution
                    && _loopPeriod.Equals(other._loopPeriod)
                    && _windTurbulence.Equals(other._windTurbulence)
                    && _windDirRad.Equals(other._windDirRad)
                    && _windSpeed.Equals(other._windSpeed)
                    && ReferenceEquals(_spectrum, other._spectrum);
            }

            public override bool Equals(object obj) => obj is WaveConditions other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    var hash = _resolution;
                    hash = hash * 397 ^ _loopPeriod.GetHashCode();
                    hash = hash * 397 ^ _windTurbulence.GetHashCode();
                    hash = hash * 397 ^ _windDirRad.GetHashCode();
                    hash = hash * 397 ^ _windSpeed.GetHashCode();
                    // Managed identity stays stable even after Unity destroys the
                    // spectrum's native object. Do not use Unity's null operator.
                    hash = hash * 397 ^ (ReferenceEquals(_spectrum, null) ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_spectrum));
                    return hash;
                }
            }
        }

        static readonly Dictionary<WaveConditions, FFTCompute> _generators = new Dictionary<WaveConditions, FFTCompute>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InitStatics()
        {
            CleanUpAll();
        }

        /// <summary>
        /// Computes water surface displacement, with wave components split across slices of the output texture array
        /// </summary>
        public static RenderTexture GenerateDisplacements(CommandBuffer buf, int resolution, float loopPeriod, float windTurbulence, float windDirRad, float windSpeed, float time, OceanWaveSpectrum spectrum, bool updateSpectrum)
        {
            // All static data arguments belong in the key and generator constructor.
            var conditions = new WaveConditions(resolution, loopPeriod, windTurbulence, windDirRad, windSpeed, spectrum);
            if (!_generators.TryGetValue(conditions, out var generator))
            {
                // No generator for these params - create one
                generator = new FFTCompute(resolution, loopPeriod, windSpeed, windTurbulence, windDirRad, spectrum);
                _generators.Add(conditions, generator);
            }

            generator._lastUsedFrame = OceanRenderer.FrameCount;

            // The remaining dynamic data arguments should be passed in to the generation here
            return generator.GenerateDisplacementsInternal(buf, time, updateSpectrum);
        }

        RenderTexture GenerateDisplacementsInternal(CommandBuffer buf, float time, bool updateSpectrum)
        {
            // A paused clock can request the same timestamp after an ocean rebuild
            // or a lost render texture. Cached time alone cannot validate GPU data.
            if (!TexturesAreValid())
            {
                InitializeTextures();
            }

            // Check if already generated, and we're not being asked to re-update the spectrum
            if (_generationTime == time && _spectrumInitialised && !updateSpectrum)
            {
                return _waveBuffers;
            }

            if (!_spectrumInitialised || updateSpectrum)
            {
                InitialiseSpectrumHandControls();
                InitializeSpectrum(buf);
                _spectrumInitialised = true;
            }

            UpdateSpectrum(buf, time);

            DispatchFFT(buf);

            _generationTime = time;

            return _waveBuffers;
        }

        bool TexturesAreValid()
        {
            return _isInitialised && _texButterfly != null && _texSpectrumControls != null
                && IsCreated(_spectrumInit) && IsCreated(_spectrumHeight)
                && IsCreated(_spectrumDisplaceX) && IsCreated(_spectrumDisplaceZ)
                && IsCreated(_tempFFT1) && IsCreated(_tempFFT2) && IsCreated(_tempFFT3)
                && IsCreated(_waveBuffers);
        }

        static bool IsCreated(RenderTexture texture) => texture != null && texture.IsCreated();

        /// <summary>
        /// Changing wave gen data can result in creating lots of new generators. This gives a way to notify
        /// that a parameter has changed. If there is no existing generator for the new param values, but there
        /// is a compatible generator which has not been used this frame, it is repurposed.
        /// </summary>
        public static void OnGenerationDataUpdated(int resolution, float loopPeriod,
            float windTurbulenceOld, float windDirRadOld, float windSpeedOld, OceanWaveSpectrum spectrumOld,
            float windTurbulenceNew, float windDirRadNew, float windSpeedNew, OceanWaveSpectrum spectrumNew)
        {
            var oldConditions = new WaveConditions(resolution, loopPeriod, windTurbulenceOld, windDirRadOld, windSpeedOld, spectrumOld);
            var newConditions = new WaveConditions(resolution, loopPeriod, windTurbulenceNew, windDirRadNew, windSpeedNew, spectrumNew);
            if (oldConditions.Equals(newConditions) || _generators.ContainsKey(newConditions))
            {
                // Keep ownership of the old generator: other shapes or recorded
                // commands can still reference its textures. CleanUpAll releases it.
                return;
            }

            var reusableConditions = oldConditions;
            if (!_generators.TryGetValue(oldConditions, out var generator) || generator._lastUsedFrame == OceanRenderer.FrameCount)
            {
                generator = null;
                // Merging shapes can leave a spare generator under an older key.
                // Reuse that storage when the shapes diverge again, so animated
                // conditions do not retain a new texture set on every frame.
                foreach (var entry in _generators)
                {
                    var candidate = entry.Value;
                    if (candidate._lastUsedFrame != OceanRenderer.FrameCount
                        && candidate._resolution == resolution && candidate._loopPeriod.Equals(loopPeriod))
                    {
                        reusableConditions = entry.Key;
                        generator = candidate;
                        break;
                    }
                }
            }

            if (generator != null)
            {
                // Shape updates precede all animated-wave draws. Rewriting an FFT
                // already used this frame would also change an earlier shape's
                // material texture before its draw. Only reuse an idle generator.
                _generators.Remove(reusableConditions);
                generator._windTurbulence = windTurbulenceNew;
                generator._windDirRad = windDirRadNew;
                generator._windSpeed = windSpeedNew;
                generator._spectrum = spectrumNew;
                generator._spectrumInitialised = false;
                _generators.Add(newConditions, generator);
            }
        }

        /// <summary>
        /// Number of FFT generators
        /// </summary>
        public static int GeneratorCount => _generators != null ? _generators.Count : 0;

        /// <summary>
        /// Computes the offsets used for the FFT calculation
        /// </summary>
        void InitializeButterfly(int resolution)
        {
            var log2Size = Mathf.RoundToInt(Mathf.Log(resolution, 2));
            var butterflyColors = new Color[resolution * log2Size];

            int offset = 1, numIterations = resolution >> 1;
            for (int rowIndex = 0; rowIndex < log2Size; rowIndex++)
            {
                int rowOffset = rowIndex * resolution;
                {
                    int start = 0, end = 2 * offset;
                    for (int iteration = 0; iteration < numIterations; iteration++)
                    {
                        var bigK = 0f;
                        for (int K = start; K < end; K += 2)
                        {
                            var phase = 2.0f * Mathf.PI * bigK * numIterations / resolution;
                            var cos = Mathf.Cos(phase);
                            var sin = Mathf.Sin(phase);
                            butterflyColors[rowOffset + K / 2] = new Color(cos, -sin, 0, 1);
                            butterflyColors[rowOffset + K / 2 + offset] = new Color(-cos, sin, 0, 1);

                            bigK += 1f;
                        }
                        start += 4 * offset;
                        end = start + 2 * offset;
                    }
                }
                numIterations >>= 1;
                offset <<= 1;
            }

            _texButterfly.SetPixels(butterflyColors);
            _texButterfly.Apply();
        }

        void InitialiseSpectrumHandControls()
        {
            for (var i = 0; i < OceanWaveSpectrum.NUM_OCTAVES; i++)
            {
                float pow = _spectrum._powerDisabled[i] ? 0f : Mathf.Pow(10f, _spectrum._powerLog[i]);
                pow *= _spectrum._multiplier * _spectrum._multiplier;
                _spectrumDataScratch[i] = pow * Color.white;
            }

            _texSpectrumControls.SetPixels(_spectrumDataScratch);
            _texSpectrumControls.Apply();
        }

        /// <summary>
        /// Computes base spectrum values based on wind speed & turbulence & spectrum controls
        /// </summary>
        void InitializeSpectrum(CommandBuffer buf)
        {
            buf.SetComputeIntParam(_shaderSpectrum, ShaderIDs.s_Size, _resolution);
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_WindSpeed, _windSpeed);
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_Turbulence, _windTurbulence);
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_Gravity, OceanRenderer.Instance.Gravity);
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_Period, _loopPeriod);
            buf.SetComputeVectorParam(_shaderSpectrum, ShaderIDs.s_WindDir, new Vector2(Mathf.Cos(_windDirRad), Mathf.Sin(_windDirRad)));
            buf.SetComputeTextureParam(_shaderSpectrum, _kernelSpectrumInit, ShaderIDs.s_SpectrumControls, _texSpectrumControls);
            buf.SetComputeTextureParam(_shaderSpectrum, _kernelSpectrumInit, ShaderIDs.s_ResultInit, _spectrumInit);
            buf.DispatchCompute(_shaderSpectrum, _kernelSpectrumInit, _resolution / 8, _resolution / 8, CASCADE_COUNT);
        }

        /// <summary>
        /// Computes a spectrum for the current time which can be FFT'd into the final surface
        /// </summary>
        void UpdateSpectrum(CommandBuffer buf, float time)
        {
            // Always set _Size as the compute shader returned from Resource.Load is the same asset every time and more
            // than one ShapeFFT will overwrite this value.
            buf.SetComputeIntParam(_shaderSpectrum, ShaderIDs.s_Size, _resolution);
            var timeScale = _spectrum._gravityScale;
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_Time, time * timeScale);
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_Chop, _spectrum._chop);
            // The bake and CPU queries loop in unscaled time. Quantize in the
            // same scaled-time domain used by the shader so fractional time
            // scales also close at the requested period. Zero freezes the
            // phase; a negative scale supplied by script reverses propagation.
            var scaledPeriod = _loopPeriod > 0f ? _loopPeriod * Mathf.Abs(timeScale) : _loopPeriod;
            buf.SetComputeFloatParam(_shaderSpectrum, ShaderIDs.s_Period, scaledPeriod);
            buf.SetComputeTextureParam(_shaderSpectrum, _kernelSpectrumUpdate, ShaderIDs.s_Init0, _spectrumInit);
            buf.SetComputeTextureParam(_shaderSpectrum, _kernelSpectrumUpdate, ShaderIDs.s_ResultHeight, _spectrumHeight);
            buf.SetComputeTextureParam(_shaderSpectrum, _kernelSpectrumUpdate, ShaderIDs.s_ResultDisplaceX, _spectrumDisplaceX);
            buf.SetComputeTextureParam(_shaderSpectrum, _kernelSpectrumUpdate, ShaderIDs.s_ResultDisplaceZ, _spectrumDisplaceZ);
            buf.DispatchCompute(_shaderSpectrum, _kernelSpectrumUpdate, _resolution / 8, _resolution / 8, CASCADE_COUNT);
        }

        /// <summary>
        /// FFT the spectrum into surface displacements
        /// </summary>
        void DispatchFFT(CommandBuffer buf)
        {
            var kernelOffset = 2 * Mathf.RoundToInt(Mathf.Log(_resolution / FFT_KERNEL_0_RESOLUTION, 2f));

            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_InputH, _spectrumHeight);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_InputX, _spectrumDisplaceX);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_InputZ, _spectrumDisplaceZ);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_InputButterfly, _texButterfly);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_Output1, _tempFFT1);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_Output2, _tempFFT2);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset, ShaderIDs.s_Output3, _tempFFT3);
            buf.DispatchCompute(_shaderFFT, kernelOffset, 1, _resolution, CASCADE_COUNT);

            buf.SetComputeTextureParam(_shaderFFT, kernelOffset + 1, ShaderIDs.s_InputH, _tempFFT1);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset + 1, ShaderIDs.s_InputX, _tempFFT2);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset + 1, ShaderIDs.s_InputZ, _tempFFT3);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset + 1, ShaderIDs.s_InputButterfly, _texButterfly);
            buf.SetComputeTextureParam(_shaderFFT, kernelOffset + 1, ShaderIDs.s_Output, _waveBuffers);
            buf.DispatchCompute(_shaderFFT, kernelOffset + 1, _resolution, 1, CASCADE_COUNT);
        }

        public static void OnGUI(int resolution, float loopPeriod, float windTurbulence, float windDirRad, float windSpeed, OceanWaveSpectrum spectrum)
        {
            _generators.TryGetValue(new WaveConditions(resolution, loopPeriod, windTurbulence, windDirRad, windSpeed, spectrum), out var generator);
            generator?.OnGUIInternal();
        }

        void OnGUIInternal()
        {
            if (_waveBuffers != null && _waveBuffers.IsCreated())
            {
                OceanDebugGUI.DrawTextureArray(_waveBuffers, 8, 0.5f, 20f);
            }

            if (_texSpectrumControls != null)
            {
                GUI.DrawTexture(new Rect(0f, 0f, 100f, 10f), _texSpectrumControls);
            }
        }
    }
}
