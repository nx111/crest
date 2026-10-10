// Crest Ocean System - MIT License (see LICENSE)
Shader "Crest/Examples/UnderwaterTransparentSurfaceShader"
{
	Properties
	{
		_Color ("Color", Color) = (1,1,1,1)
		_Glossiness ("Smoothness", Range(0,1)) = 0.5
		_Metallic ("Metallic", Range(0,1)) = 0.0
		_FogMultiplier ("Fog Multiplier", Range(0, 1)) = 1.0
	}

    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "Queue"="Transparent" "RenderType"="Transparent" }
        Pass
        {
            Tags { "LightMode"="UniversalForwardOnly" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #pragma multi_compile _ CREST_WATER_VOLUME_2D CREST_WATER_VOLUME_HAS_BACKFACE
            #pragma multi_compile _ CREST_SUBSURFACESCATTERING_ON
            #pragma multi_compile _ CREST_SHADOWS_ON

        #include "../../../Crest/Shaders/Helpers/URP/Core.hlsl"
        struct Attributes { float3 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
        struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; float2 uv : TEXCOORD2; float4 screenPos : TEXCOORD3; float fog : TEXCOORD4; UNITY_VERTEX_OUTPUT_STEREO };
        Varyings Vert(Attributes input)
        {
            Varyings o = (Varyings)0;
            UNITY_SETUP_INSTANCE_ID(input);
            UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
            o.positionWS = TransformObjectToWorld(input.positionOS);
            o.positionCS = TransformWorldToHClip(o.positionWS);
            o.normalWS = TransformObjectToWorldNormal(input.normalOS);
            o.uv = input.uv;
            o.screenPos = ComputeScreenPos(o.positionCS);
            o.fog = ComputeFogFactor(o.positionCS.z);
            return o;
        }
        half4 Shade(Varyings i, half4 albedo, half metallic, half smoothness, half3 emission)
        {
            InputData data = (InputData)0;
            data.positionWS = i.positionWS;
            data.normalWS = normalize(i.normalWS);
            data.viewDirectionWS = GetWorldSpaceNormalizeViewDir(i.positionWS);
            data.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
            data.bakedGI = SampleSH(data.normalWS);
            data.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
            data.shadowMask = half4(1,1,1,1);
            SurfaceData surface = (SurfaceData)0;
            surface.albedo = albedo.rgb;
            surface.alpha = albedo.a;
            surface.metallic = metallic;
            surface.smoothness = smoothness;
            surface.normalTS = half3(0,0,1);
            surface.occlusion = 1;
            surface.emission = emission;
            return UniversalFragmentPBR(data, surface);
        }

            #if defined(CREST_WATER_VOLUME_2D) || defined(CREST_WATER_VOLUME_HAS_BACKFACE)
            #define CREST_WATER_VOLUME 1
            #endif
            #include "../../../Crest/Shaders/Underwater/UnderwaterEffectIncludes.hlsl"
            half4 _Color;
            half _Glossiness, _Metallic, _FogMultiplier;
            half4 Frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                half4 color = Shade(i, _Color, _Metallic, _Glossiness, 0);
                if (!CrestApplyUnderwaterFog(i.screenPos.xy / i.screenPos.w, i.positionWS, i.positionCS.z, _FogMultiplier, color.rgb))
                    color.rgb = MixFog(color.rgb, i.fog);
                return color;
            }
            ENDHLSL
        }
    }
}
