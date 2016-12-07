Shader "Hidden/HDRenderLoop/SkyProcedural"
{
    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend One Zero

            HLSLPROGRAM
            #pragma target 5.0
            #pragma only_renderers d3d11 // TEMP: unitl we go futher in dev

            #pragma vertex Vert
            #pragma fragment Frag

            #pragma multi_compile _ ATMOSPHERICS_DEBUG
            #pragma multi_compile _ ATMOSPHERICS_OCCLUSION_FULLSKY
            #pragma multi_compile _ PERFORM_SKY_OCCLUSION_TEST

            #ifndef PERFORM_SKY_OCCLUSION_TEST
                #define IS_RENDERING_SKY
            #endif

            #include "Color.hlsl"
            #include "Common.hlsl"
            #include "CommonLighting.hlsl"
            #include "Assets/ScriptableRenderLoop/HDRenderLoop/ShaderVariables.hlsl"
            #include "AtmosphericScattering.hlsl"

            TEXTURECUBE(_SkyDomeCubemap);
            SAMPLERCUBE(sampler_SkyDomeCubemap);

            uniform float4x4 _InvViewProjMatrix;
            uniform float4x4 _SkyDomeRotation;
            uniform float    _SkyDomeExposure;
            uniform float3   _SkyDomeTint;
            // uniform float2   _ShadowBiasSkyRayleighMie;

            struct Attributes
            {
                float3 positionCS : POSITION;
                float3 eyeVector : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 eyeVector : TEXCOORD0;
            };

            Varyings Vert(Attributes input)
            {
                // TODO: implement SV_vertexID full screen quad
                Varyings output;
                output.positionCS = float4(input.positionCS.xy, UNITY_RAW_FAR_CLIP_VALUE, 1.0);
                output.eyeVector  = input.eyeVector;

                return output;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                Coordinate coord = GetCoordinate(input.positionCS.xy, _ScreenSize.zw);
                float3     dir   = mul(normalize(input.eyeVector), _SkyDomeRotation);

                // If the sky box is too far away (depth set to 0), the resulting look is too foggy.
                const float skyDepth = 0.01;

                float3 skyColor = float3(0.0, 0.0, 0.0);

            #ifdef PERFORM_SKY_OCCLUSION_TEST
                // Determine whether the sky is occluded by the scene geometry.
                // Do not perform blending with the environment map if the sky is occluded.
                float rawDepth = max(skyDepth, LOAD_TEXTURE2D(_CameraDepthTexture, coord.unPositionSS).r);

                if (rawDepth <= skyDepth)
                {
                    skyColor  = SAMPLE_TEXTURECUBE_LOD(_SkyDomeCubemap, sampler_SkyDomeCubemap, dir, 0).rgb;
                    skyColor *= exp2(_SkyDomeExposure) * _SkyDomeTint;
                }
            #else
                float rawDepth = skyDepth;
                skyColor  = SAMPLE_TEXTURECUBE_LOD(_SkyDomeCubemap, sampler_SkyDomeCubemap, dir, 0).rgb;
                skyColor *= exp2(_SkyDomeExposure) * _SkyDomeTint;
            #endif

                float3 positionWS = UnprojectToWorld(rawDepth, coord.positionSS, _InvViewProjMatrix);

                float4 c1, c2, c3;
                VolundTransferScatter(positionWS, c1, c2, c3);

                float4 coord1 = float4(c1.rgb + c3.rgb, max(0.f, 1.f - c1.a - c3.a));
                float3 coord2 = c2.rgb;

                float  sunCos = dot(dir, _SunDirection);
                float  miePh  = MiePhase(sunCos, _MiePhaseAnisotropy);

                float2 occlusion  = float2(1.0, 1.0); // TODO.
                float  extinction = coord1.a;
                float3 scatter    = coord1.rgb * occlusion.x + coord2 * miePh * occlusion.y;

                #ifdef ATMOSPHERICS_DEBUG
                    switch (_AtmosphericsDebugMode)
                    {
                        case ATMOSPHERICS_DBG_RAYLEIGH:           return c1;
                        case ATMOSPHERICS_DBG_MIE:                return c2 * miePh;
                        case ATMOSPHERICS_DBG_HEIGHT:             return c3;
                        case ATMOSPHERICS_DBG_SCATTERING:         return float4(scatter, 0.0);
                        case ATMOSPHERICS_DBG_OCCLUSION:          return float4(occlusion.xy, 0.0, 0.0);
                        case ATMOSPHERICS_DBG_OCCLUDEDSCATTERING: return float4(scatter, 0.0);
                    }
                #endif

                // Apply extinction to the scene color when performing alpha-blending.
                return ClampToFloat16Max(float4(skyColor * extinction + scatter, extinction));
            }

            ENDHLSL
        }

    }
    Fallback Off
}
