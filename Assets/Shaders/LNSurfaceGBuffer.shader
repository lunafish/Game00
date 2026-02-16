Shader "Custom/LNSurfaceGBuffer"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _SSSMask("SSS Mask", Range(0, 1)) = 1.0
        _ShadowStrength("Shadow Strength", Range(0, 1)) = 1.0
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _Subsurface("Subsurface", Range(0.0, 1.0)) = 0.0
        _Anisotropic("Anisotropic", Range(0.0, 1.0)) = 0.0
        _SpecularColor("Specular Color", Color) = (1, 1, 1, 1)
        _FresnelPower("Fresnel Power", Range(0.1, 10.0)) = 5.0
        _FresnelStrength("Fresnel Strength", Range(0, 5)) = 0.5
        _DiffuseWrap("Diffuse Wrap", Range(0.0, 1.0)) = 0.25
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

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
            float _Smoothness;
            float _Metallic;
            float _Subsurface;
            float _Anisotropic;
            float4 _SpecularColor;
            float _FresnelPower;
            float _FresnelStrength;
            float _DiffuseWrap;
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
            float4 GBuffer4 : SV_Target4; // Extra Data (Metallic, SpecularStrength, Packed(Subsurface, Anisotropic))
            float4 GBuffer5 : SV_Target5; // Extra Data 2 (Smoothness, Shadow, Unused, Unused)
        };

        FragmentOutput frag(Varyings input)
        {
            FragmentOutput output;
            float4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            
            // Shadow Calculation
            float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS.xyz);
            Light mainLight = GetMainLight(shadowCoord);
            float shadow = mainLight.shadowAttenuation;

            // GBuffer0: Albedo (RGB), Unused (A) -> Set to 1.0
            output.GBuffer0 = float4(color.rgb, 1.0); 

            // GBuffer1: Normal (RGB), Unused (A) -> Set to 1.0
            output.GBuffer1 = float4(normalize(input.normalWS) * 0.5 + 0.5, 1.0);
            
            // View-space Z를 사용한 Linear Eye Depth
            float3 positionVS = TransformWorldToView(input.positionWS.xyz);
            float linearDepth = -positionVS.z;
            output.GBuffer2 = float4(linearDepth.xxx, 1.0);
            
            // GBuffer3: LNSurface Mask (R), Fresnel Strength (G), Fresnel Power (B), Unused (A)
            output.GBuffer3 = float4(_SSSMask, _FresnelStrength / 5.0, _FresnelPower / 10.0, 1.0);

            // GBuffer4 Packing
            // R: Metallic
            // G: Specular Strength
            // B: Packed (Subsurface 4bit + Anisotropic 4bit)
            // A: Unused -> 1.0
            
            float specularStrength = max(max(_SpecularColor.r, _SpecularColor.g), _SpecularColor.b);
            
            // Pack Subsurface (0..1) and Anisotropic (0..1) into 8 bits (0..255)
            // Subsurface: High 4 bits, Anisotropic: Low 4 bits
            float packedSubsurface = floor(_Subsurface * 15.0 + 0.5); 
            float packedAnisotropic = floor(_Anisotropic * 15.0 + 0.5);
            float packedB = (packedSubsurface * 16.0 + packedAnisotropic) / 255.0;

            output.GBuffer4 = float4(_Metallic, specularStrength, packedB, 1.0);
            
            // GBuffer5: Smoothness (R), Shadow (G), Unused (BA)
            output.GBuffer5 = float4(_Smoothness, shadow, 1.0, 1.0);

            return output;
        }
        ENDHLSL

        // 1. UniversalForward 패스
        Pass
        {
            Name "UniversalForward"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert_forward
            #pragma fragment frag_forward
            
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT
            
            // Lighting.hlsl included in HLSLINCLUDE

            Varyings vert_forward(Attributes input)
            {
                return vert(input);
            }

            half4 frag_forward(Varyings input) : SV_Target
            {
                float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS.xyz);
                Light mainLight = GetMainLight(shadowCoord);
                
                half3 viewDirWS = GetWorldSpaceViewDir(input.positionWS.xyz);
                half3 normalWS = normalize(input.normalWS);

                float NdotL = saturate(dot(normalWS, mainLight.direction));
                half shadow = mainLight.shadowAttenuation;
                
                half4 albedo = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
                half3 ambient = SampleSH(normalWS) * albedo.rgb;
                half3 diffuse = albedo.rgb * mainLight.color * NdotL * shadow;
                
                return half4(diffuse + ambient, albedo.a);
            }
            ENDHLSL
        }

        // 2. ShadowCaster 패스
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ColorMask 0
            Cull Front
            
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
            Name "LNSurface_Front"
            Tags { "LightMode" = "LNSurface_Front" }
            Cull Back
            ZTest LEqual
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            
            // Shadow keywords needed for GetMainLight()
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _SHADOWS_SOFT

            ENDHLSL
        }

        Pass
        {
            Name "LNSurface_Back"
            Tags { "LightMode" = "LNSurface_Back" }
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
