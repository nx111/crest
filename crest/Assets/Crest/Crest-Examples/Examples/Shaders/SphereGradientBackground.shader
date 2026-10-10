// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

Shader "Hidden/Crest/SphereGradientBackground"
{
	Properties
	{
		_ColorTowardsSun("_ColorTowardsSun", Color) = (1, 1, 1)
		_ColorAwayFromSun("_ColorAwayFromSun", Color) = (1, 1, 1)
		_Exponent("_Exponent", Float) = 1.0
	}
	SubShader
	{
		Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" "Queue"="Background" }
		LOD 100

		Pass
		{
			Tags { "LightMode"="UniversalForwardOnly" }
			Cull Front
			Blend Off
			ZWrite Off

			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

			struct Attributes
			{
				float4 vertex : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings
			{
				float4 vertex : SV_POSITION;
				float3 positionWS : TEXCOORD0;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			Varyings vert (Attributes v)
			{
				Varyings o;
				ZERO_INITIALIZE(Varyings, o);
				UNITY_SETUP_INSTANCE_ID(v);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

				o.vertex = TransformObjectToHClip(v.vertex.xyz);
				o.positionWS = TransformObjectToWorld(v.vertex.xyz);
				return o;
			}

			CBUFFER_START(UnityPerMaterial)
				float3 _ColorTowardsSun;
				float3 _ColorAwayFromSun;
				float _Exponent;
			CBUFFER_END

			float4 frag (Varyings i) : SV_Target
			{
				float3 viewDirection = normalize(i.positionWS - _WorldSpaceCameraPos);
				Light light = GetMainLight();

				float alpha = saturate(0.5 * dot(viewDirection, light.direction) + 0.5);
				alpha = pow(alpha, _Exponent);

				float3 col = lerp(_ColorAwayFromSun, _ColorTowardsSun, alpha);
				return float4(col * saturate(max(light.color.r, max(light.color.g, light.color.b))), 1.0);
			}
			ENDHLSL
		}
	}
}
