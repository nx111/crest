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
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #define CREST_SAMPLE_SHADOW_HARD
            #include "../Helpers/URP/Core.hlsl"
            #include "../ShaderLibrary/UpdateShadow.hlsl"
            half CrestSampleShadows(float4 positionWS)
            {
                return MainLightRealtimeShadow(TransformWorldToShadowCoord(positionWS.xyz));
            }
            half CrestComputeShadowFade(float4 positionWS)
            {
                return GetMainLightShadowFade(positionWS.xyz);
            }
            ENDHLSL
        }
    }
}
