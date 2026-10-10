// Crest Ocean System - MIT License (see LICENSE)
Shader "Crest/Examples/Masked"
{
	Properties
	{
		_Color("Color", Color) = (1.0, 1.0, 1.0, 1.0)
		[Toggle] _Emission("Emission", Float) = 0.0
		[HDR] _EmissionColor("Color", Color) = (0.0, 0.0, 0.0)
		_Albedo("Albedo (RGB)", 2D) = "white" {}
		_Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
		_Metallic("Metallic", Range(0.0, 1.0)) = 0.0
		_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
		[KeywordEnum(None, Masked, Fill)] _Mask("Masked", Float) = 1.0
	}


    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        HLSLINCLUDE
        #pragma target 4.5
        #pragma shader_feature_local _MASK_NONE _MASK_MASKED _MASK_FILL
        #pragma shader_feature_local _EMISSION_ON

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
            #if defined(_MAIN_LIGHT_SHADOWS_SCREEN)
            data.shadowCoord = i.screenPos;
            #else
            data.shadowCoord = TransformWorldToShadowCoord(i.positionWS);
            #endif
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

        TEXTURE2D(_Albedo); SAMPLER(sampler_Albedo);
        UNITY_DECLARE_SCREENSPACE_TEXTURE(_CrestOceanMaskTexture);
        UNITY_DECLARE_SCREENSPACE_TEXTURE(_CrestWaterVolumeFrontFaceTexture);
        UNITY_DECLARE_SCREENSPACE_TEXTURE(_CrestWaterVolumeBackFaceTexture);
        UNITY_DECLARE_SCREENSPACE_TEXTURE(_FillTexture);
        half4 _Color, _EmissionColor;
        half _Smoothness, _Metallic, _Cutoff;
        float4 _Albedo_ST;
        half4 Albedo(Varyings i)
        {
            half4 c = SAMPLE_TEXTURE2D(_Albedo, sampler_Albedo, i.uv * _Albedo_ST.xy + _Albedo_ST.zw) * _Color;
            clip(c.a - _Cutoff);
            return c;
        }
        void ApplyMask(Varyings i)
        {
            float2 uv = i.screenPos.xy / i.screenPos.w;
            #if defined(_MASK_MASKED)
            float front = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CrestWaterVolumeFrontFaceTexture, uv).r;
            float back = UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CrestWaterVolumeBackFaceTexture, uv).r;
            if ((front > 0 && front < i.positionCS.z) || (back > i.positionCS.z && back > 0) ||
                UNITY_SAMPLE_SCREENSPACE_TEXTURE(_CrestOceanMaskTexture, uv).r == 0) discard;
            #elif defined(_MASK_FILL)
            if (UNITY_SAMPLE_SCREENSPACE_TEXTURE(_FillTexture, uv).r == 0) discard;
            #endif
        }
        half4 Frag(Varyings i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            ApplyMask(i);
            half4 c = Albedo(i);
            half3 emission = 0;
            #if defined(_EMISSION_ON)
            emission = c.rgb * c.a * _EmissionColor.rgb;
            #endif
            half4 color = Shade(i, c, _Metallic, _Smoothness, emission);
            color.rgb = MixFog(color.rgb, i.fog);
            return color;
        }
        half4 FragDepth(Varyings i) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
            ApplyMask(i); Albedo(i); return 0;
        }
        float3 _LightDirection, _LightPosition;
        Varyings VertShadow(Attributes input)
        {
            Varyings o = Vert(input);
            float3 direction = _LightDirection;
            #if defined(_CASTING_PUNCTUAL_LIGHT_SHADOW)
            direction = normalize(_LightPosition - o.positionWS);
            #endif
            o.positionCS = TransformWorldToHClip(ApplyShadowBias(o.positionWS, o.normalWS, direction));
            o.positionCS.z = min(o.positionCS.z, UNITY_NEAR_CLIP_VALUE * o.positionCS.w);
            return o;
        }
        half4 FragShadow(Varyings i) : SV_Target { Albedo(i); return 0; }
        ENDHLSL
        Pass
        {
            Name "Forward" Tags { "LightMode"="UniversalForwardOnly" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile_fragment _ _REFLECTION_PROBE_BLENDING
            ENDHLSL
        }
        Pass
        {
            Name "DepthOnly" Tags { "LightMode"="DepthOnly" } ColorMask 0
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragDepth
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster" Tags { "LightMode"="ShadowCaster" } ColorMask 0
            HLSLPROGRAM
            #pragma vertex VertShadow
            #pragma fragment FragShadow
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            ENDHLSL
        }
    }
}
