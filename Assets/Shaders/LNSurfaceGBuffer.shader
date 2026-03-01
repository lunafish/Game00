Shader "Custom/LNSurfaceGBuffer"
{
    Properties
    {
        _BaseMap("Base Map", 2D) = "white" {}
        _BaseColor("Base Color", Color) = (1,1,1,1)
        _BumpMap("Normal Map", 2D) = "bump" {}
        _RoughnessMap("Roughness Map", 2D) = "black" {}
        _SSSMask("SSS Mask", Range(0, 1)) = 1.0
        _ShadowStrength("Shadow Strength", Range(0, 1)) = 1.0
        _Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
        _Metallic("Metallic", Range(0.0, 1.0)) = 0.0
        _Subsurface("Subsurface", Range(0.0, 1.0)) = 0.0
        _Anisotropic("Anisotropic", Range(0.0, 1.0)) = 0.0
        _SSSIntensity("SSS Intensity", Range(0, 10)) = 1.0
        _SSSThickness("SSS Thickness", Range(0, 100)) = 10.0
        _SpecularColor("Specular Color", Color) = (1, 1, 1, 1)
        _FresnelPower("Fresnel Power", Range(0.1, 10.0)) = 5.0
        _FresnelStrength("Fresnel Strength", Range(0, 5)) = 0.5
        _DiffuseWrap("Diffuse Wrap", Range(0.0, 1.0)) = 0.25
        
        [Header(Inner POM)]
        [Toggle(_INNER_POM_ON)] _EnableInnerPOM("Enable Inner POM", Float) = 0.0
        [Toggle] _UseInnerBaseMap("Use Inner Base Map", Float) = 0.0
        _InnerBaseMap("Inner Base Map", 2D) = "white" {}
        _InnerHeightMap("Inner Height Map", 2D) = "black" {}
        _InnerNormalMap("Inner Normal Map", 2D) = "bump" {}
        _InnerColor("Inner Color", Color) = (1,1,1,1)
        _InnerSurfaceThickness("Inner Surface Thickness", Range(0.0, 0.2)) = 0.0
        _InnerIOR("Inner IOR", Range(1.0, 3.0)) = 1.45
        _InnerBlend("Inner Blend", Range(0.0, 1.0)) = 0.5
        _InnerDepthFade("Inner Depth Fade", Range(0.0, 5.0)) = 1.0
        [Toggle] _UseInnerTriplanar("Use Inner Triplanar", Float) = 0.0
        _InnerTriplanarTile("Inner Triplanar Tile", Float) = 1.0
        _ResinAbsorption("Resin Absorption", Range(0.0, 10.0)) = 1.0
        _ResinTurbidity("Resin Turbidity", Range(0.0, 1.0)) = 0.0
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
            float4 tangentOS : TANGENT;
            float2 uv : TEXCOORD0;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 normalWS : TEXCOORD0;
            float4 tangentWS : TEXCOORD1;
            float2 uv : TEXCOORD3;
            float4 positionWS : TEXCOORD4;
        };

        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        TEXTURE2D(_BumpMap); SAMPLER(sampler_BumpMap);
        TEXTURE2D(_RoughnessMap); SAMPLER(sampler_RoughnessMap);
        
        TEXTURE2D(_InnerBaseMap); SAMPLER(sampler_InnerBaseMap);
        TEXTURE2D(_InnerHeightMap); SAMPLER(sampler_InnerHeightMap);
        TEXTURE2D(_InnerNormalMap); SAMPLER(sampler_InnerNormalMap);

        CBUFFER_START(UnityPerMaterial)
            float4 _BaseMap_ST;
            float4 _BaseColor;
            float _SSSMask;
            float _ShadowStrength;
            float _Smoothness;
            float _Metallic;
            float _Subsurface;
            float _Anisotropic;
            float _SSSIntensity;
            float _SSSThickness;
            float4 _SpecularColor;
            float _FresnelPower;
            float _FresnelStrength;
            float _DiffuseWrap;
            
            float _EnableInnerPOM;
            float _UseInnerBaseMap;
            float4 _InnerColor;
            float _InnerSurfaceThickness;
            float _InnerIOR;
            float _InnerBlend;
            float _InnerDepthFade;
            float _UseInnerTriplanar;
            float _InnerTriplanarTile;
            float _ResinAbsorption;
            float _ResinTurbidity;
        CBUFFER_END

        Varyings vert(Attributes input)
        {
            Varyings output;
            output.positionWS = float4(TransformObjectToWorld(input.positionOS.xyz), 1.0);
            output.positionCS = TransformWorldToHClip(output.positionWS.xyz);
            output.normalWS = TransformObjectToWorldNormal(input.normalOS);
            output.tangentWS = float4(TransformObjectToWorldDir(input.tangentOS.xyz), input.tangentOS.w);
            output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
            return output;
        }

        // Octahedron Normal Encoding
        float2 OctWrap(float2 v)
        {
            return (1.0 - abs(v.yx)) * (v.xy >= 0.0 ? 1.0 : -1.0);
        }

        float2 EncodeNormal(float3 n)
        {
            n /= (abs(n.x) + abs(n.y) + abs(n.z));
            n.xy = n.z >= 0.0 ? n.xy : OctWrap(n.xy);
            return n.xy * 0.5 + 0.5;
        }

        // --- Front Pass Output Structure ---
        struct FragmentOutputFront
        {
            float4 GBuffer0 : SV_Target0; // Color (RGB), Unused (A)
            float4 GBuffer1 : SV_Target1; // Packed: Normal(RG) + Depth(B) + Mask(A)
            float4 GBuffer2 : SV_Target2; // Metallic(R), Smoothness(G), Shadow(B), Packed Sub/Aniso(A)
            float4 GBuffer3 : SV_Target3; // Inner Height(R), Inner Normal X(G), Inner Normal Y(B), Packed SSS Int/Thick(A)
            float4 GBuffer4 : SV_Target4; // Inner Color(R), Inner Thick/IOR(G), Inner Blend/Fade(B), Packed Abs/Turbidity(A)
        };

        FragmentOutputFront frag_front(Varyings input)
        {
            FragmentOutputFront output;
            float4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            
            // Shadow Calculation
            float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS.xyz);
            Light mainLight = GetMainLight(shadowCoord);
            float shadow = mainLight.shadowAttenuation;

            // GBuffer0: Albedo (RGB) + Unused (A)
            output.GBuffer0 = float4(color.rgb, 1.0); 

            // Bitmask Packing for Mask Channel
            int maskFlags = 0;
            if (_SSSMask > 0.5) maskFlags |= 1;
            if (_UseInnerTriplanar > 0.5) maskFlags |= 2;

            // --- Normal Mapping ---
            float3 normalWS = normalize(input.normalWS);
            float3 tangentWS = normalize(input.tangentWS.xyz);
            float3 bitangentWS = cross(normalWS, tangentWS) * input.tangentWS.w;
            float3x3 tbn = float3x3(tangentWS, bitangentWS, normalWS);
            
            float3 normalTS = UnpackNormal(SAMPLE_TEXTURE2D(_BumpMap, sampler_BumpMap, input.uv));
            normalWS = normalize(mul(normalTS, tbn));

            // GBuffer1: Packed Normal (RG) + Linear Depth (B) + Mask (A)
            float2 encodedNormal = EncodeNormal(normalWS);
            float3 positionVS = TransformWorldToView(input.positionWS.xyz);
            float linearDepth = -positionVS.z;
            
            output.GBuffer1 = float4(encodedNormal, linearDepth, float(maskFlags) / 255.0);
            
            // --- Roughness / Smoothness ---
            float roughness = SAMPLE_TEXTURE2D(_RoughnessMap, sampler_RoughnessMap, input.uv).r;
            float smoothness = (1.0 - roughness) * _Smoothness;

            // --- Pack Extra Data ---
            float packedSub = floor(_Subsurface * 15.0 + 0.5); 
            float packedAniso = floor(_Anisotropic * 15.0 + 0.5);
            float packedSubAniso = (packedSub * 16.0 + packedAniso) / 255.0;
            
            // GBuffer2: Metallic, Smoothness, Shadow, Packed Sub/Aniso
            output.GBuffer2 = float4(_Metallic, smoothness, shadow, packedSubAniso);

            // Pack SSS Intensity & Thickness
            float normIntensity = saturate(_SSSIntensity / 10.0);
            float normThickness = saturate(_SSSThickness / 100.0);
            
            float packedInt = floor(normIntensity * 15.0 + 0.5);
            float packedThick = floor(normThickness * 15.0 + 0.5);
            float packedSSSIntThick = (packedInt * 16.0 + packedThick) / 255.0;

            // GBuffer3: Inner POM Data & SSS Params
            float height = 0;
            float3 innerNormal = float3(0,0,1);
            float3 innerAlbedo = float3(1,1,1);
            
            float surfaceThickness = _InnerSurfaceThickness;
            float blend = _InnerBlend;
            
            if (_EnableInnerPOM < 0.5)
            {
                surfaceThickness = 0.0;
                blend = 0.0;
            }
            
            if (_UseInnerTriplanar > 0.5)
            {
                // Triplanar Sampling
                float3 blendWeights = abs(normalWS);
                blendWeights = blendWeights / (blendWeights.x + blendWeights.y + blendWeights.z);
                
                float2 uvX = input.positionWS.zy * _InnerTriplanarTile;
                float2 uvY = input.positionWS.xz * _InnerTriplanarTile;
                float2 uvZ = input.positionWS.xy * _InnerTriplanarTile;
                
                // Height
                float hX = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, uvX).r;
                float hY = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, uvY).r;
                float hZ = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, uvZ).r;
                height = hX * blendWeights.x + hY * blendWeights.y + hZ * blendWeights.z;
                
                // Albedo
                if (_UseInnerBaseMap > 0.5)
                {
                    float3 cX = SAMPLE_TEXTURE2D(_InnerBaseMap, sampler_InnerBaseMap, uvX).rgb;
                    float3 cY = SAMPLE_TEXTURE2D(_InnerBaseMap, sampler_InnerBaseMap, uvY).rgb;
                    float3 cZ = SAMPLE_TEXTURE2D(_InnerBaseMap, sampler_InnerBaseMap, uvZ).rgb;
                    innerAlbedo = cX * blendWeights.x + cY * blendWeights.y + cZ * blendWeights.z;
                }

                // Normal
                float3 nX = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, uvX));
                float3 nY = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, uvY));
                float3 nZ = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, uvZ));
                
                float3 tX = float3(nX.z, nX.y, nX.x); 
                float3 tY = float3(nY.x, nY.z, nY.y);
                float3 tZ = float3(nZ.x, nZ.y, nZ.z);
                
                tX.x *= sign(normalWS.x);
                tY.y *= sign(normalWS.y);
                tZ.z *= sign(normalWS.z);
                
                innerNormal = normalize(tX * blendWeights.x + tY * blendWeights.y + tZ * blendWeights.z);
            }
            else
            {
                // UV Sampling
                height = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, input.uv).r;
                
                if (_UseInnerBaseMap > 0.5)
                {
                    innerAlbedo = SAMPLE_TEXTURE2D(_InnerBaseMap, sampler_InnerBaseMap, input.uv).rgb;
                }

                float3 n = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, input.uv));
                
                // Construct World Space Tangent Basis using Screen Derivatives
                float3 ddxPos = ddx(input.positionWS.xyz);
                float3 ddyPos = ddy(input.positionWS.xyz);
                float2 ddxUV = ddx(input.uv);
                float2 ddyUV = ddy(input.uv);
                
                float det = ddxUV.x * ddyUV.y - ddyUV.x * ddxUV.y;
                float r = 1.0 / (det >= 0.0 ? max(det, 1e-5) : min(det, -1e-5));
                float3 tng = normalize((ddxPos * ddyUV.y - ddyPos * ddxUV.y) * r);
                tng = normalize(tng - normalWS * dot(normalWS, tng));
                float signFactor = det < 0.0 ? -1.0 : 1.0;
                float3 bitng = signFactor * cross(normalWS, tng);
                
                innerNormal = normalize(tng * n.x + bitng * n.y + normalWS * n.z);
            }

            // Pack Normal using Octahedron Encoding
            float2 packedInnerNormal = EncodeNormal(innerNormal);
            
            output.GBuffer3 = float4(height, packedInnerNormal.x, packedInnerNormal.y, packedSSSIntThick);

            // GBuffer4: Inner POM Params (Packed)
            
            // Pack Color (R3 G3 B2) - Use Texture OR Color
            float3 finalInnerColor = (_UseInnerBaseMap > 0.5) ? innerAlbedo : _InnerColor.rgb;

            float r3 = floor(saturate(finalInnerColor.r) * 7.0 + 0.5);
            float g3 = floor(saturate(finalInnerColor.g) * 7.0 + 0.5);
            float b2 = floor(saturate(finalInnerColor.b) * 3.0 + 0.5);
            float packedColor = (r3 * 32.0 + g3 * 4.0 + b2) / 255.0;
            
            // Pack Thickness & IOR (4:4)
            float normInnerThickness = saturate(surfaceThickness / 0.2);
            float normIOR = saturate((_InnerIOR - 1.0) / 2.0);
            float pThickness = floor(normInnerThickness * 15.0 + 0.5);
            float pIOR = floor(normIOR * 15.0 + 0.5);
            float packedThickIOR = (pThickness * 16.0 + pIOR) / 255.0;
            
            // Pack Blend & Fade (4:4)
            float normBlend = saturate(blend);
            float normFade = saturate(_InnerDepthFade / 5.0);
            float pBlend = floor(normBlend * 15.0 + 0.5);
            float pFade = floor(normFade * 15.0 + 0.5);
            float packedBlendFade = (pBlend * 16.0 + pFade) / 255.0;
            
            // Pack Absorption & Turbidity (4:4)
            float normAbsorption = saturate(_ResinAbsorption / 10.0);
            float normTurbidity = saturate(_ResinTurbidity);
            float pAbs = floor(normAbsorption * 15.0 + 0.5);
            float pTurb = floor(normTurbidity * 15.0 + 0.5);
            float packedAbsTurb = (pAbs * 16.0 + pTurb) / 255.0;
            
            output.GBuffer4 = float4(packedColor, packedThickIOR, packedBlendFade, packedAbsTurb);

            return output;
        }

        // --- Back Pass Output Structure ---
        struct FragmentOutputBack
        {
            float4 GBuffer0 : SV_Target0; // Color (RGB)
            float4 GBuffer1 : SV_Target1; // Packed: Normal(RG) + Depth(B)
        };

        FragmentOutputBack frag_back(Varyings input)
        {
            FragmentOutputBack output;
            float4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            
            // GBuffer0: Albedo (RGB)
            output.GBuffer0 = float4(color.rgb, 1.0); 

            // GBuffer1: Packed Normal (RG) + Linear Depth (B)
            float3 normalWS = normalize(input.normalWS);
            float2 encodedNormal = EncodeNormal(normalWS);
            
            float3 positionVS = TransformWorldToView(input.positionWS.xyz);
            float linearDepth = -positionVS.z;
            
            output.GBuffer1 = float4(encodedNormal, linearDepth, 1.0);

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
                
                // Specular (Blinn-Phong)
                float3 halfDir = normalize(mainLight.direction + viewDirWS);
                float NdotH = saturate(dot(normalWS, halfDir));
                float specular = pow(NdotH, _Smoothness * 128.0) * _SpecularColor.rgb;
                
                return half4(diffuse + ambient + specular * shadow, albedo.a);
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
            #pragma fragment frag_front
            
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
            #pragma fragment frag_back
            ENDHLSL
        }
    }
}