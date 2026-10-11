// Diagnostic entry points into the production HLSL, not a replacement water shader.
Shader "Hidden/Crest/Realism Audit"
{
    Properties
    {
        _FoamWhiteColor("Foam", Color) = (1,1,1,1)
        _WaveFoamLightScale("Foam diffuse", Float) = 1
        _WaveFoamSpecularBoost("Foam specular", Float) = 0
        _WaveFoamSpecularFallOff("Foam exponent", Float) = 293
    }
    SubShader
    {
        Pass
        {
            Cull Off ZWrite Off ZTest Always
            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag
            #define _APPLYNORMALMAPPING_ON 1
            #define _FOAM_ON 1
            #define _FOAM3DLIGHTING_ON 1
            #define _FLOW_ON 1
            #define _SURFACE_TYPE_TRANSPARENT 1
            #include "Assets/Crest/Crest/Shaders/Helpers/URP/Core.hlsl"
            #include "Assets/Crest/Crest/Shaders/ShaderLibrary/Common.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanGlobals.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanInputsDriven.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanShaderData.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanHelpersNew.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanVertHelpers.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanShaderHelpers.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanLightingHelpers.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanNormalMapping.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanSpecular.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanFoam.hlsl"
            #include "Assets/Crest/Crest/Shaders/OceanAdditionalLights.hlsl"

            float4 _AuditBase, _AuditDetail, _AuditNormal0, _AuditNormal1;
            float4 _AuditWeights, _AuditLight, _AuditVariance;
            int _AuditCase;
            float4 Vert(uint id : SV_VertexID) : SV_POSITION
            {
                return float4(id == 1 ? 3 : -1, id == 2 ? 3 : -1, 0, 1);
            }
            float4 Frag() : SV_Target
            {
                if (_AuditCase == 0)
                    return float4(CrestReorientNormal(_AuditBase.xyz, _AuditDetail.xy), 1);
                CrestFoamLightingData data = (CrestFoamLightingData)0;
                data.normal0 = _AuditNormal0.xyz;
                data.normal1 = _AuditNormal1.xyz;
                data.weights = _AuditWeights.xy;
                data.variance0 = _AuditVariance.x;
                data.variance1 = _AuditVariance.y;
                Light light = (Light)0;
                light.direction = _AuditLight.xyz;
                light.color = 1;
                light.distanceAttenuation = light.shadowAttenuation = 1;
                half3 scatter = 0, specular = 0, foam = 0;
                CrestAccumulateAdditionalLight(light, half3(0,1,0), half3(0,1,0),
                    half3(0,0,0), 0, false, 0, data, scatter, specular, foam);
                return float4(foam, 1);
            }
            ENDHLSL
        }
    }
}
