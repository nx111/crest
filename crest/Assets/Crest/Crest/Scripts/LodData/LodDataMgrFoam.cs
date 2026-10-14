// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace Crest
{
    using SettingsType = SimSettingsFoam;

    /// <summary>
    /// A persistent foam simulation that moves around with a displacement LOD. The input is fully combined water surface shape.
    /// </summary>
    public class LodDataMgrFoam : LodDataMgrPersistent
    {
        protected override string ShaderSim => "UpdateFoam";
        protected override int krnl_ShaderSim => _shader.FindKernel(ShaderSim);
        public override string SimName => "Foam";
        protected override GraphicsFormat RequestedTextureFormat => Settings._renderTextureGraphicsFormat;
        static Texture2DArray s_nullTexture => TextureArrayHelpers.BlackTextureArray;
        protected override Texture2DArray NullTexture => s_nullTexture;

        internal static readonly string MATERIAL_KEYWORD_PROPERTY = "_Foam";
        internal static readonly string MATERIAL_KEYWORD = MATERIAL_KEYWORD_PREFIX + "_FOAM_ON";
        internal const string ERROR_MATERIAL_KEYWORD_MISSING = "Foam is not enabled on the ocean material and will not be visible.";
        internal const string ERROR_MATERIAL_KEYWORD_MISSING_FIX = "Tick the <i>Enable</i> option in the <i>Foam</i> parameter section on the material currently assigned to the <i>OceanRenderer</i> component.";
        internal const string ERROR_MATERIAL_KEYWORD_ON_FEATURE_OFF = "The Foam feature is disabled on this component but is enabled on the ocean material.";
        internal const string ERROR_MATERIAL_KEYWORD_ON_FEATURE_OFF_FIX = "If this is not intentional, either enable the <i>Create Foam Sim</i> option on this component to turn it on, or disable the <i>Foam</i> feature on the ocean material to save performance.";

        readonly int sp_FoamFadeRate = Shader.PropertyToID("_FoamFadeRate");
        readonly int sp_WaveFoamStrength = Shader.PropertyToID("_WaveFoamStrength");
        readonly int sp_WaveFoamCoverage = Shader.PropertyToID("_WaveFoamCoverage");
        readonly int sp_ShorelineFoamMaxDepth = Shader.PropertyToID("_ShorelineFoamMaxDepth");
        readonly int sp_ShorelineFoamStrength = Shader.PropertyToID("_ShorelineFoamStrength");
        readonly int sp_NeedsPrewarming = Shader.PropertyToID("_NeedsPrewarming");
        readonly int sp_CrestMinimumWavesSlice = Shader.PropertyToID("_CrestMinimumWavesSlice");
        readonly int sp_CrestFoamInputScale = Shader.PropertyToID("_CrestFoamInputScale");
        readonly int sp_CrestFoamStochasticRounding = Shader.PropertyToID("_CrestFoamStochasticRounding");
        readonly int sp_CrestFoamRoundingStep = Shader.PropertyToID("_CrestFoamRoundingStep");

        float _inputTimeScale;
        int _foamRoundingStep;

        public override SimSettingsBase SettingsBase => Settings;
        public SettingsType Settings => _ocean._simSettingsFoam != null ? _ocean._simSettingsFoam : GetDefaultSettings<SettingsType>();

        public LodDataMgrFoam(OceanRenderer ocean) : base(ocean)
        {
            Start();
        }

        public override void Start()
        {
            base.Start();

#if UNITY_EDITOR
            if (OceanRenderer.Instance.OceanMaterial != null
                && OceanRenderer.Instance.OceanMaterial.HasProperty(MATERIAL_KEYWORD_PROPERTY)
                && !OceanRenderer.Instance.OceanMaterial.IsKeywordEnabled(MATERIAL_KEYWORD))
            {
                Debug.LogWarning("Crest: " + ERROR_MATERIAL_KEYWORD_MISSING + " " + ERROR_MATERIAL_KEYWORD_MISSING_FIX, _ocean);
            }
#endif
        }

        protected override void SetAdditionalSimParams(IPropertyWrapper simMaterial)
        {
            base.SetAdditionalSimParams(simMaterial);

            // Called once per dispatch, not once per frame. A zero-time step has
            // zero input scale, so reprojection does not advance the random seed.
            if (_inputTimeScale > 0f)
            {
                _foamRoundingStep = unchecked(_foamRoundingStep + 1);
            }
            simMaterial.SetInt(sp_CrestFoamRoundingStep, _foamRoundingStep);
            // Use the allocated compatible format, including multi-channel fallbacks.
            var format = CompatibleTextureFormat;
            simMaterial.SetInt(sp_CrestFoamStochasticRounding,
                format == GraphicsFormat.R16_SFloat || format == GraphicsFormat.R16G16_SFloat
                || format == GraphicsFormat.R16G16B16A16_SFloat ? 1 : 0);

            // Prewarm simulation for first frame or teleporting. It will not be the same results as running the
            // simulation for multiple frames - but good enough.
            simMaterial.SetFloat(sp_NeedsPrewarming, Settings._prewarm && _needsPrewarmingThisStep ? 1f : 0f);
            simMaterial.SetFloat(sp_FoamFadeRate, Settings._foamFadeRate);
            simMaterial.SetFloat(sp_WaveFoamStrength, Settings._waveFoamStrength);
            simMaterial.SetFloat(sp_WaveFoamCoverage, Settings._waveFoamCoverage);
            simMaterial.SetFloat(sp_ShorelineFoamMaxDepth, Settings._shorelineFoamMaxDepth);
            simMaterial.SetFloat(sp_ShorelineFoamStrength, Settings._shorelineFoamStrength);
            simMaterial.SetVector(OceanRenderer.sp_oceanCenterPosWorld, OceanRenderer.Instance.Root.position);
            simMaterial.SetInt(sp_CrestMinimumWavesSlice, Mathf.Clamp(Settings.FilterWaves, 0, _ocean.CurrentLodCount - 1));

            // assign animated waves - to slot 1 current frame data
            LodDataMgrAnimWaves.Bind(simMaterial);

            // assign sea floor depth - to slot 1 current frame data
            LodDataMgrSeaFloorDepth.Bind(simMaterial);

            // assign flow - to slot 1 current frame data
            LodDataMgrFlow.Bind(simMaterial);
        }

        protected override void GetSimSubstepData(float timeToSimulate, out int numSubsteps, out float substepDt)
        {
            numSubsteps = Mathf.FloorToInt(timeToSimulate * Settings._simulationFrequency);

            substepDt = numSubsteps > 0 ? (1f / Settings._simulationFrequency) : 0f;
            _inputTimeScale = CalculateFoamInputScale(substepDt, Settings._foamFadeRate);
        }

        static float IntegratedSourceWeight(float deltaTime, float fadeRate)
        {
            var exponent = fadeRate * deltaTime;
            if (exponent <= 0.001f)
            {
                return deltaTime * (1f - exponent * (0.5f - exponent / 6f));
            }

            return (1f - Mathf.Exp(-exponent)) / fadeRate;
        }

        internal static float CalculateFoamInputScale(float deltaTime, float fadeRate)
        {
            var dt = Mathf.Max(deltaTime, 0f);
            var rate = Mathf.Max(fadeRate, 0f);
            // Existing input strengths describe one deposit at the default 30 Hz.
            // Normalize the integrated source term to preserve that exact deposit,
            // while making other simulation frequencies produce the same source.
            const float referenceStep = 1f / 30f;
            return IntegratedSourceWeight(dt, rate) / IntegratedSourceWeight(referenceStep, rate);
        }

        protected override void SubmitDraws(int lodIdx, CommandBuffer buf)
        {
            // Only the built-in additive foam shaders use this multiplier. Override
            // and custom input shaders retain their existing output semantics.
            buf.SetGlobalFloat(sp_CrestFoamInputScale, _inputTimeScale);
            base.SubmitDraws(lodIdx, buf);
        }

        readonly static string s_textureArrayName = "_LD_TexArray_Foam";
        private static TextureArrayParamIds s_textureArrayParamIds = new TextureArrayParamIds(s_textureArrayName);
        public static int ParamIdSampler(bool sourceLod = false) => s_textureArrayParamIds.GetId(sourceLod);
        protected override int GetParamIdSampler(bool sourceLod = false) => ParamIdSampler(sourceLod);

        public static void Bind(IPropertyWrapper properties)
        {
            if (OceanRenderer.Instance._lodDataFoam != null)
            {
                properties.SetTexture(OceanRenderer.Instance._lodDataFoam.GetParamIdSampler(), OceanRenderer.Instance._lodDataFoam.DataTexture);
            }
            else
            {
                properties.SetTexture(ParamIdSampler(), s_nullTexture);
            }
        }

        public static void BindNullToGraphicsShaders() => Shader.SetGlobalTexture(ParamIdSampler(), s_nullTexture);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void InitStatics()
        {
            // Init here from 2019.3 onwards
            s_textureArrayParamIds = new TextureArrayParamIds(s_textureArrayName);
        }
    }
}
