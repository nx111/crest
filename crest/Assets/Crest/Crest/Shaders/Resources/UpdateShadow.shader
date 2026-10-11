// Crest Ocean System - MIT License (see LICENSE)
Shader "Hidden/Crest/Simulation/Update Shadow"
{
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #define CREST_SAMPLE_SHADOW_HARD
            #include "../Helpers/URP/Core.hlsl"
            #include "../ShaderLibrary/UpdateShadow.hlsl"
            half CrestSampleShadows(const float4 positionWS)
            {
                // The temporal shadow accumulator applies distance fade itself.
                return CrestMainLightShadow(positionWS.xyz, false);
            }
            half CrestComputeShadowFade(const float4 positionWS)
            {
                return GetMainLightShadowFade(positionWS.xyz);
            }
            ENDHLSL
        }
    }
}
