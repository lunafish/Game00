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
        _SSSIntensity("SSS Intensity", Range(0, 10)) = 1.0
        _SSSThickness("SSS Thickness", Range(0, 100)) = 10.0
        _SpecularColor("Specular Color", Color) = (1, 1, 1, 1)
        _FresnelPower("Fresnel Power", Range(0.1, 10.0)) = 5.0
        _FresnelStrength("Fresnel Strength", Range(0, 5)) = 0.5
        _DiffuseWrap("Diffuse Wrap", Range(0.0, 1.0)) = 0.25
        
        [Header(Inner POM)]
        _InnerHeightMap("Inner Height Map", 2D) = "black" {}
        _InnerNormalMap("Inner Normal Map", 2D) = "bump" {}
        _InnerColor("Inner Color", Color) = (1,1,1,1)
        _InnerDepthScale("Inner Depth Scale", Range(0.0, 0.1)) = 0.02
        _InnerIOR("Inner IOR", Range(1.0, 3.0)) = 1.45
        _InnerBlend("Inner Blend", Range(0.0, 1.0)) = 0.5
        _InnerDepthFade("Inner Depth Fade", Range(0.0, 5.0)) = 1.0
        [Toggle] _UseInnerTriplanar("Use Inner Triplanar", Float) = 0.0
        _InnerTriplanarTile("Inner Triplanar Tile", Float) = 1.0
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
        
        TEXTURE2D(_InnerHeightMap);
        SAMPLER(sampler_InnerHeightMap);
        
        TEXTURE2D(_InnerNormalMap);
        SAMPLER(sampler_InnerNormalMap);

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
            
            float4 _InnerColor;
            float _InnerDepthScale;
            float _InnerIOR;
            float _InnerBlend;
            float _InnerDepthFade;
            float _UseInnerTriplanar;
            float _InnerTriplanarTile;
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
            float4 GBuffer0 : SV_Target0; // Color (RGB)
            float4 GBuffer1 : SV_Target1; // Packed: Normal(RG) + Depth(B)
            float4 GBuffer2 : SV_Target2; // Extra1: Mask, Metallic, Smoothness
            float4 GBuffer3 : SV_Target3; // Extra2: Shadow, Packed(Sub/Aniso), Packed(Int/Thick)
            float4 GBuffer4 : SV_Target4; // Inner POM: Height(R), Normal(GB)
            float4 GBuffer5 : SV_Target5; // Inner Params: Color(R), Scale/IOR(G), Blend/Fade(B)
        };

        FragmentOutputFront frag_front(Varyings input)
        {
            FragmentOutputFront output;
            float4 color = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv) * _BaseColor;
            
            // Shadow Calculation
            float4 shadowCoord = TransformWorldToShadowCoord(input.positionWS.xyz);
            Light mainLight = GetMainLight(shadowCoord);
            float shadow = mainLight.shadowAttenuation;

            // GBuffer0: Albedo (RGB)
            output.GBuffer0 = float4(color.rgb, 1.0); 

            // GBuffer1: Packed Normal (RG) + Linear Depth (B)
            float3 normalWS = normalize(input.normalWS);
            float2 encodedNormal = EncodeNormal(normalWS);
            
            float3 positionVS = TransformWorldToView(input.positionWS.xyz);
            float linearDepth = -positionVS.z;
            
            output.GBuffer1 = float4(encodedNormal, linearDepth, 1.0);
            
            // GBuffer2: Extra Data 1
            // R: Mask (Bitmask)
            // G: Metallic
            // B: Smoothness
            // A: Unused
            
            // Bitmask Packing for Mask Channel
            // Bit 0 (1): SSS Mask
            // Bit 1 (2): Use Inner Triplanar
            int maskFlags = 0;
            if (_SSSMask > 0.5) maskFlags |= 1;
            if (_UseInnerTriplanar > 0.5) maskFlags |= 2;
            
            output.GBuffer2 = float4(float(maskFlags) / 255.0, _Metallic, _Smoothness, 1.0);

            // GBuffer3: Extra Data 2
            // R: Shadow Attenuation
            // G: Packed (Subsurface 4bit + Anisotropic 4bit)
            // B: Packed (SSS Intensity 4bit + SSS Thickness 4bit)
            // A: Unused
            
            // Pack G: Subsurface & Anisotropic
            float packedSub = floor(_Subsurface * 15.0 + 0.5); 
            float packedAniso = floor(_Anisotropic * 15.0 + 0.5);
            float packedG = (packedSub * 16.0 + packedAniso) / 255.0;
            
            // Pack B: SSS Intensity & Thickness
            // Normalize ranges: Intensity (0-10), Thickness (0-100)
            float normIntensity = saturate(_SSSIntensity / 10.0);
            float normThickness = saturate(_SSSThickness / 100.0);
            
            float packedInt = floor(normIntensity * 15.0 + 0.5);
            float packedThick = floor(normThickness * 15.0 + 0.5);
            float packedB = (packedInt * 16.0 + packedThick) / 255.0;

            output.GBuffer3 = float4(shadow, packedG, packedB, 1.0);

            // GBuffer4: Inner POM Data
            // R: Height (0-1)
            // G: Normal X (0-1)
            // B: Normal Y (0-1)
            // A: Unused
            
            float height = 0;
            float3 innerNormal = float3(0,0,1);
            
            if (_UseInnerTriplanar > 0.5)
            {
                // Triplanar Sampling with UDN/Whiteout Blending
                float3 blendWeights = abs(normalWS);
                blendWeights = blendWeights / (blendWeights.x + blendWeights.y + blendWeights.z);
                
                float2 uvX = input.positionWS.zy * _InnerTriplanarTile;
                float2 uvY = input.positionWS.xz * _InnerTriplanarTile;
                float2 uvZ = input.positionWS.xy * _InnerTriplanarTile;
                
                float hX = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, uvX).r;
                float hY = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, uvY).r;
                float hZ = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, uvZ).r;
                
                height = hX * blendWeights.x + hY * blendWeights.y + hZ * blendWeights.z;
                
                // UDN Blending for Normals
                // 1. Unpack to -1..1
                float3 nX = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, uvX));
                float3 nY = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, uvY));
                float3 nZ = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, uvZ));
                
                // 2. Swizzle to World Space alignment
                // X-Plane (YZ): Normal is (1,0,0) -> Tangent Space Z maps to World X
                float3 worldNX = float3(0, nX.y, nX.x); 
                // Y-Plane (XZ): Normal is (0,1,0) -> Tangent Space Z maps to World Y
                float3 worldNY = float3(nY.x, 0, nY.y);
                // Z-Plane (XY): Normal is (0,0,1) -> Tangent Space Z maps to World Z
                float3 worldNZ = float3(nZ.x, nZ.y, nZ.z); // Z is up in tangent space, maps to Z in world if normal is Z
                
                // 3. Apply Sign of World Normal to flip back-facing normals
                worldNX.x *= sign(normalWS.x);
                worldNY.y *= sign(normalWS.y);
                worldNZ.z *= sign(normalWS.z); // Fix: Z component needs sign flip too
                
                // 4. Weighted Sum & Normalize
                // Note: This blends the *perturbations* onto the geometric normal
                // But here we want the *final* inner normal.
                // Since we don't have a base geometric normal for the inner surface (it's flat in tangent space),
                // we treat these as offsets from the geometric normal.
                
                // Simplified Whiteout:
                // Just blend the unpacked normals and add to geometric normal? No, that's Detail Normal.
                // Here we want the "Inner Surface Normal" which replaces the geometric normal for lighting.
                
                // Correct approach for Triplanar Normal Mapping:
                // We construct the normal vector in world space directly.
                
                // Re-swizzle for correct orientation relative to plane
                // Tangent Space Normal (x,y,z) -> World Space
                // Plane X (YZ): Tangent Y, Bitangent Z, Normal X
                float3 tX = float3(nX.z, nX.y, nX.x); 
                // Plane Y (XZ): Tangent X, Bitangent Z, Normal Y
                float3 tY = float3(nY.x, nY.z, nY.y);
                // Plane Z (XY): Tangent X, Bitangent Y, Normal Z
                float3 tZ = float3(nZ.x, nZ.y, nZ.z);
                
                // Apply signs
                tX.x *= sign(normalWS.x);
                tY.y *= sign(normalWS.y);
                tZ.z *= sign(normalWS.z);
                
                // Blend
                innerNormal = normalize(tX * blendWeights.x + tY * blendWeights.y + tZ * blendWeights.z);
                
                // Transform to Tangent Space of the geometric surface (for storage)
                // GBuffer stores normal in World Space (encoded), but here we are storing "Inner Normal"
                // The Compute Shader expects this to be in Tangent Space relative to the surface?
                // No, Compute Shader reconstructs World Normal from GBuffer1.
                // GBuffer4 stores "Inner Normal".
                // Let's check Compute Shader:
                // "Transform Local Inner Normal to World Space"
                // "innerNormalWS = normalize(tangentWS * localInnerNormal.x + ...)"
                // So GBuffer4 MUST store Tangent Space Normal.
                
                // Problem: We calculated World Space Inner Normal above.
                // We need to convert it back to the surface's Tangent Space.
                // But we don't have Tangent/Bitangent here in frag_front easily (without ddx/ddy or extra attributes).
                
                // Alternative: Store World Space Inner Normal directly?
                // GBuffer4 has only 2 channels for Normal (G, B).
                // We can use OctEncode to store World Space Normal in 2 channels!
                // But Compute Shader expects "Local Inner Normal" (Tangent Space).
                
                // Let's stick to the plan:
                // Since we are in Pixel Shader, we CAN use ddx/ddy to get Tangent Frame!
                // Or just use the World Space Normal we calculated, and project it onto the surface normal to get "approximate" tangent space normal.
                
                // Projection to Tangent Space (Approximate):
                // We want N_inner relative to N_geom.
                // N_inner = T * x + B * y + N * z
                // z = dot(N_inner, N_geom)
                // x, y = ... hard without T, B.
                
                // Let's change the strategy slightly:
                // Store World Space Normal in GBuffer4 using OctEncode.
                // Compute Shader will read it as World Space Normal directly.
                // This requires changing Compute Shader logic slightly (skip TBN transform).
                
                // BUT, to minimize changes and keep consistency:
                // Let's assume the surface is flat enough that we can just use the blended normal as is,
                // and encode it relative to the geometric normal? No, that's complex.
                
                // Let's use the "World Space Normal" storage.
                // GBuffer4.gb = EncodeNormal(innerNormalWS);
                // And in Compute Shader, we decode it to World Space directly.
                
                // Wait, GBuffer4 format is R8G8B8A8.
                // EncodeNormal outputs 0..1.
                // So we can store World Space Normal.
                
                // Let's update Compute Shader later to handle this.
                // For now, let's store the World Space Normal we calculated.
                
                // Re-verify Whiteout logic:
                // The logic above (tX, tY, tZ) is a valid way to blend world-space normals from triplanar.
                
            }
            else
            {
                // UV Sampling
                height = SAMPLE_TEXTURE2D(_InnerHeightMap, sampler_InnerHeightMap, input.uv).r;
                float3 n = UnpackNormal(SAMPLE_TEXTURE2D(_InnerNormalMap, sampler_InnerNormalMap, input.uv));
                
                // Convert Tangent Space Normal to World Space Normal
                // We need TBN matrix here.
                // Since we don't have tangents passed from vertex, we can't do this accurately without ddx/ddy.
                // Standard URP shaders pass tangents. We didn't.
                
                // Fallback: Assume object is mostly flat or use ddx/ddy
                // Or just store Tangent Space Normal (n) and let Compute Shader handle it (as it does now).
                // Current Compute Shader expects Tangent Space Normal in GBuffer4.
                
                innerNormal = n; // Keep as Tangent Space
            }
            
            // Decision:
            // If Triplanar: innerNormal is World Space.
            // If UV: innerNormal is Tangent Space.
            // This is inconsistent.
            
            // Solution: Always store Tangent Space Normal in GBuffer4.
            // For Triplanar, we must convert World Space result to Tangent Space.
            // We can construct TBN on the fly using ddx/ddy of Position.
            
            float3 ddxPos = ddx(input.positionWS.xyz);
            float3 ddyPos = ddy(input.positionWS.xyz);
            float3 T = normalize(ddxPos * input.uv.y - ddyPos * input.uv.x); // Rough approx if UVs exist
            // If UVs don't exist or are bad (Triplanar case), we need geometric TBN.
            
            // Geometric TBN from Normal only (for Triplanar)
            float3 tng = normalize(cross(float3(0,1,0), normalWS));
            if (abs(normalWS.y) > 0.99) tng = normalize(cross(float3(0,0,1), normalWS));
            float3 bitng = cross(normalWS, tng);
            
            if (_UseInnerTriplanar > 0.5)
            {
                // Convert World Space Inner Normal to Tangent Space
                float3 ws = innerNormal; // Calculated above
                innerNormal.x = dot(ws, tng);
                innerNormal.y = dot(ws, bitng);
                innerNormal.z = dot(ws, normalWS);
            }
            
            // Pack Normal (-1..1 -> 0..1)
            float2 packedInnerNormal = innerNormal.xy * 0.5 + 0.5;
            
            output.GBuffer4 = float4(height, packedInnerNormal.x, packedInnerNormal.y, 0.0);

            // GBuffer5: Inner POM Params (Packed)
            // R: Color (3:3:2 Packing) -> 8 bits
            // G: Scale (4 bits) | IOR (4 bits) -> 8 bits
            // B: Blend (4 bits) | Fade (4 bits) -> 8 bits
            // A: Unused
            
            // Pack Color (R3 G3 B2)
            // R: 0..7, G: 0..7, B: 0..3
            float r3 = floor(saturate(_InnerColor.r) * 7.0 + 0.5);
            float g3 = floor(saturate(_InnerColor.g) * 7.0 + 0.5);
            float b2 = floor(saturate(_InnerColor.b) * 3.0 + 0.5);
            float packedColor = (r3 * 32.0 + g3 * 4.0 + b2) / 255.0;
            
            // Pack Scale & IOR (4:4)
            // Scale: 0.0 ~ 0.1 -> 0..15
            // IOR: 1.0 ~ 3.0 -> 0..15
            float normScale = saturate(_InnerDepthScale / 0.1);
            float normIOR = saturate((_InnerIOR - 1.0) / 2.0);
            float pScale = floor(normScale * 15.0 + 0.5);
            float pIOR = floor(normIOR * 15.0 + 0.5);
            float packedScaleIOR = (pScale * 16.0 + pIOR) / 255.0;
            
            // Pack Blend & Fade (4:4)
            // Blend: 0.0 ~ 1.0 -> 0..15
            // Fade: 0.0 ~ 5.0 -> 0..15
            float normBlend = saturate(_InnerBlend);
            float normFade = saturate(_InnerDepthFade / 5.0);
            float pBlend = floor(normBlend * 15.0 + 0.5);
            float pFade = floor(normFade * 15.0 + 0.5);
            float packedBlendFade = (pBlend * 16.0 + pFade) / 255.0;
            
            output.GBuffer5 = float4(packedColor, packedScaleIOR, packedBlendFade, 0.0);

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
