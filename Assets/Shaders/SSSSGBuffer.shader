Shader "Custom/SSSSGBuffer"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _SSSMask("SSS Mask", Range(0, 1)) = 1.0
        _ShadowStrength("Shadow Strength", Range(0, 1)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 normalWS : TEXCOORD0;
            float2 uv : TEXCOORD1;
            float4 positionWS : TEXCOORD3;
        };

        TEXTURE2D(_BaseMap);
        SAMPLER(sampler_BaseMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _SSSMask;
            float _ShadowStrength;
        CBUFFER_END

        Varyings vert(Attributes input)
        {
            Varyings output;
            output.positionWS = float4(TransformObjectToWorld(input.positionOS.xyz), 1.0);
            output.positionCS = TransformWorldToHClip(output.positionWS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            return output;
        }

        struct FragmentOutput
        {
            float4 GBuffer0 : SV_Target0; // Color
            float4 GBuffer1 : SV_Target1; // Normal
            float4 GBuffer2 : SV_Target2; // Depth
            float4 GBuffer3 : SV_Target3; // Mask
        };

        FragmentOutput frag(Varyings input)
        {
            FragmentOutput output;
            float4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            
            output.GBuffer0 = float4(color.rgb, 1.0); // 알파는 사용하지 않으므로 1.0
            output.GBuffer1 = float4(normalize(input.normalWS) * 0.5 + 0.5, 1.0);
            
            // View-space Z를 사용한 Linear Eye Depth
            float3 positionVS = TransformWorldToView(input.positionWS.xyz);
            float linearDepth = -positionVS.z;
            output.GBuffer2 = float4(linearDepth.xxx, 1.0);
            
            // GBuffer3에 SSS 마스크 출력
            output.GBuffer3 = float4(_SSSMask.xxx, 1.0);

            return output;
        }
        ENDHLSL

        // 1. UniversalForward 패스 (Half Lambert + 그림자 수신)
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert_forward
            #pragma fragment frag_forward
            
            // Shadow keywords
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            Varyings vert_forward(Attributes input)
            {
                return vert(input); // Reuse existing vert logic
            }

            half4 frag_forward(Varyings input) : SV_Target
            {
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS.xyz);
                Light mainLight = GetMainLight(shadowCoord);
                
                // Half Lambert 계산
                float3 normal = normalize(input.normalWS);
                float NdotL = dot(normal, mainLight.direction);
                float halfLambert = NdotL * 0.5 + 0.5;
                halfLambert = halfLambert * halfLambert; // 부드러운 감쇠를 위해 제곱
                
                half shadow = mainLight.shadowAttenuation;
                
                // 그림자 경계 부드럽게 처리
                shadow = smoothstep(-0.2, 1.2, shadow);
                
                // 그림자 강도 적용
                // 강도(Strength)에 따라 1.0(그림자 없음)과 실제 그림자 값 사이를 보간
                shadow = lerp(1.0, shadow, _ShadowStrength);
                
                // 합성: 알베도 * HalfLambert * 그림자 * 조명색상
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                float3 lighting = halfLambert * mainLight.color * shadow;
                
                return float4(albedo.rgb * lighting, albedo.a);
            }
            ENDHLSL
        }

        // 2. ShadowCaster 패스 (그림자 투사)
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            Cull Front // 아티팩트를 줄이기 위해 그림자 캐스터에는 Front Culling 사용
            
            HLSLPROGRAM
            #pragma vertex vert_shadow
            #pragma fragment frag_shadow
            
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            
            float3 _LightDirection;

            struct VaryingsShadow
            {
                float4 positionCS : SV_POSITION;
            };

            VaryingsShadow vert_shadow(Attributes input)
            {
                VaryingsShadow output;
                float3 positionWS = TransformObjectToWorld(input.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);

                output.positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
                
                #if UNITY_REVERSED_Z
                    output.positionCS.z = min(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #else
                    output.positionCS.z = max(output.positionCS.z, output.positionCS.w * UNITY_NEAR_CLIP_VALUE);
                #endif
                return output;
            }

            half4 frag_shadow(VaryingsShadow input) : SV_Target
            {
                return 0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "SSSS_Front"
            Tags { "LightMode" = "SSSS_Front" }
            Cull Back
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }

        Pass
        {
            Name "SSSS_Back"
            Tags { "LightMode" = "SSSS_Back" }
            Cull Front
            ZTest Greater
            ZWrite Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            ENDHLSL
        }
    }
}
