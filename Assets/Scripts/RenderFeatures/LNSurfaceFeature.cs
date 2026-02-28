using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using System.Collections.Generic;

public class LNSurfaceFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class LNSurfaceSettings
    {
        public enum LightingModel
        {
            HalfLambert,
            DisneyBRDF
        }

        public enum ResolutionScale
        {
            Full = 1,
            Half = 2,
            Quarter = 4
        }

        public enum ShadowTintMode
        {
            Custom,
            Complementary
        }

        public enum DebugMode
        {
            None = 0,
            HiZ = 1,
            SSR_Raw = 2,
            SSR_Blurred = 3,
            SSGI_Blurred = 4,
            InnerPOM = 5
        }

        public LayerMask layerMask = -1;
        public ComputeShader lightingComputeShader; // Disney BRDF Lighting
        public LightingModel lightingModel = LightingModel.DisneyBRDF; // Selection
        
        [Header("Optimization")]
        public ResolutionScale resolutionScale = ResolutionScale.Half;
        public bool useBackfaceRaymarching = true; // Added Option

        public bool enableSSS = true; 
        // intensity and thicknessScale removed (controlled by material)

        [Header("Material Global Settings")]
        [Range(0.0f, 2.0f)] public float specularStrength = 1.0f;
        [Range(0.0f, 1.0f)] public float diffuseWrap = 0.25f;

        [Header("Stylized Shadow Tint")]
        public ShadowTintMode shadowTintMode = ShadowTintMode.Complementary;
        public Color shadowTintColor = new Color(0.0f, 0.0f, 0.2f, 1.0f); // Default Dark Blue
        [Range(0.0f, 1.0f)] public float shadowTintIntensity = 0.5f;
        [Range(0.0f, 1.0f)] public float shadowThreshold = 0.5f; // 0.5 Default

        [Header("Inner POM")]
        public bool enableInnerPOM = true;
        // Parameters moved to Material Property Block (Per-Pixel Control)
        // Global settings removed here as they are now controlled by GBuffer

        [Header("Screen Space Reflections")]
        public bool enableSSR = true;
        [Range(8, 128)] public int ssrMaxSteps = 64;
        [Range(0.01f, 1.0f)] public float ssrStepSize = 0.1f;
        [Range(0.01f, 1.0f)] public float ssrThickness = 0.1f;

        [Header("Ambient Occlusion")]
        public bool enableSSAO = true;
        [Range(0.01f, 5.0f)] public float ssaoRadius = 0.5f;
        [Range(0f, 10f)] public float ssaoIntensity = 1.0f;
        [Range(4, 32)] public int ssaoSampleCount = 16;

        [Header("Global Illumination")]
        public bool enableSSGI = true;
        [Range(0f, 5f)] public float ssgiIntensity = 1.0f;
        [Range(1, 16)] public int ssgiSampleCount = 4;
        [Range(8, 128)] public int ssgiMaxSteps = 64; // Added SSGI Max Steps
        [Range(0.1f, 2.0f)] public float ssgiRayStepSize = 0.5f;

        [Header("Contact Shadows")]
        public bool enableSSCS = true;
        [Range(0.1f, 10.0f)] public float sscsDistance = 1.0f;
        [Range(4, 32)] public int sscsMaxSteps = 16;
        [Range(0.01f, 1.0f)] public float sscsThickness = 0.1f;
        [Range(0.0f, 2.0f)] public float sscsIntensity = 1.0f; // Added Intensity

        [Header("Performance")]
        public bool enableCheckerboard = true;
        public DebugMode debugMode = DebugMode.None;

        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    public LNSurfaceSettings settings = new LNSurfaceSettings();
    private LNSurfacePass _pass;

    public override void Create()
    {
        _pass = new LNSurfacePass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }

    private class LNSurfacePass : ScriptableRenderPass
    {
        private LNSurfaceSettings _settings;
        private FilteringSettings _filteringSettings;
        private RTHandle _resultHandle;
        private int _kernelLighting;
        private int _frameCount = 0;
        
        // History Data for Temporal Reprojection
        private Dictionary<Camera, Matrix4x4> _prevViewProjMatrices = new Dictionary<Camera, Matrix4x4>();
        
        // History Buffers (Double Buffering per Camera)
        // Key: Camera, Value: [ReadBuffer, WriteBuffer]
        private Dictionary<Camera, RTHandle[]> _ssrHistoryBuffers = new Dictionary<Camera, RTHandle[]>();
        private Dictionary<Camera, RTHandle[]> _ssgiHistoryBuffers = new Dictionary<Camera, RTHandle[]>(); // SSGI History

        public LNSurfacePass(LNSurfaceSettings settings)
        {
            _settings = settings;
            renderPassEvent = settings.renderPassEvent;
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, settings.layerMask);
            _resultHandle = RTHandles.Alloc("_LNSurface_Result", name: "_LNSurface_Result");
            
            if (settings.lightingComputeShader != null)
                _kernelLighting = settings.lightingComputeShader.FindKernel("CSMain");
        }

        private RTHandle m_HiZPyramidRT;

        public void Dispose()
        {
            m_HiZPyramidRT?.Release();
            _resultHandle?.Release();
            foreach (var buffers in _ssrHistoryBuffers.Values)
            {
                if (buffers != null)
                {
                    buffers[0]?.Release();
                    buffers[1]?.Release();
                }
            }
            _ssrHistoryBuffers.Clear();
            
            foreach (var buffers in _ssgiHistoryBuffers.Values)
            {
                if (buffers != null)
                {
                    buffers[0]?.Release();
                    buffers[1]?.Release();
                }
            }
            _ssgiHistoryBuffers.Clear();
        }

        private class GBufferPassData
        {
            internal RendererListHandle rendererList;
            internal TextureHandle packedTexture; // For clearing
        }

        private class ComputePassData
        {
            internal ComputeShader compute;
            internal int kernel;
            
            // Textures
            internal TextureHandle frontColor;
            internal TextureHandle frontPacked; // Normal(RG) + Depth(B)
            internal TextureHandle frontExtra; // Mask(R), Metallic(G), Smoothness(B)
            internal TextureHandle frontExtra2; // Shadow(R), Packed(G), Packed(B)
            internal TextureHandle frontInner; // Inner POM: Height(R), Normal(GB)
            internal TextureHandle frontInnerParams; // Inner POM Params: Color(R), Scale/IOR(G), Blend/Fade(B)
            
            internal TextureHandle backColor;
            internal TextureHandle backPacked; // Normal(RG) + Depth(B)
            
            internal TextureHandle sceneColor; 
            internal TextureHandle ssaoTexture; // SSAO Blurred Result
            internal TextureHandle ssrTexture; // SSR Blurred Result
            internal TextureHandle ssgiTexture; // SSGI Blurred Result
            internal TextureHandle sscsTexture; // SSCS Result
            internal TextureHandle result; // Input/Output
            
            // Temporal Reprojection Textures
            internal TextureHandle historyBuffer;
            internal TextureHandle newHistoryBuffer;
            internal TextureHandle currentFrameResult;
            
            internal float intensity;
            internal float thicknessScale;
            internal float enableSSS; 
            internal int lightingModel; // 0: HalfLambert, 1: DisneyBRDF
            internal Vector2 screenParams;

            // Material Global Settings
            internal float specularStrength;
            internal float diffuseWrap;

            // Shadow Tint Settings
            internal int shadowTintMode;
            internal Vector4 shadowTintColor;
            internal float shadowTintIntensity;
            internal float shadowThreshold;

            // Inner POM Settings
            internal float enableInnerPOM;
            // Per-pixel params are now in GBuffer

            // SSR Params
            internal float enableSSR;
            internal int ssrMaxSteps;
            internal float ssrStepSize;
            internal float ssrThickness;

            // SSAO Params
            internal float enableSSAO;
            internal float ssaoRadius;
            internal float ssaoIntensity;
            internal int ssaoSampleCount;

            // SSGI Params
            internal float enableSSGI;
            internal float ssgiIntensity;
            internal int ssgiSampleCount;
            internal int ssgiMaxSteps; // Added
            internal float ssgiRayStepSize;

            // SSCS Params
            internal float enableSSCS;
            internal float sscsDistance;
            internal int sscsMaxSteps;
            internal float sscsThickness;
            internal float sscsIntensity;

            // Performance Params
            internal float enableCheckerboard;
            internal int frameCount;
            internal int debugMode;
            internal float useBackfaceRaymarching; // Added

            // Light Data
            internal Vector4 mainLightDirection;  
            internal Vector4 mainLightColor;
            internal int additionalLightCount;
            internal Vector4[] additionalLightPositions;
            internal Vector4[] additionalLightColors;
            internal Vector4[] additionalLightAttenuations;

            internal Matrix4x4 cameraToWorldMatrix;
            internal Matrix4x4 worldToCameraMatrix;
            internal Matrix4x4 projectionMatrix;
            internal Matrix4x4 inverseProjectionMatrix;
            
            // Temporal Reprojection Data
            internal Matrix4x4 prevViewProjMatrix;
            
            // Ambient SH
            internal Vector4[] shAr;
            internal Vector4[] shAg;
            internal Vector4[] shAb;
            internal Vector4[] shBr;
            internal Vector4[] shBg;
            internal Vector4[] shBb;
            internal Vector4[] shC;

            // Hi-Z
            internal TextureHandle hizSource;
            internal TextureHandle hizPyramid;
            internal int mipLevel;
            internal int maxMipLevel;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>(); 

            _frameCount++;
            var desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; 
            desc.enableRandomWrite = false;

            // Scaled Resolution Descriptor for Optimization
            int scale = (int)_settings.resolutionScale;
            var scaledDesc = desc;
            scaledDesc.width = Mathf.Max(1, desc.width / scale);
            scaledDesc.height = Mathf.Max(1, desc.height / scale);

            // 1. Create Textures
            // Front Pass Textures (Full Res)
            TextureHandle frontColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Color", false);
            
            var packedDesc = desc;
            packedDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat; // High Precision for Normal & Depth
            TextureHandle frontPacked = UniversalRenderer.CreateRenderGraphTexture(renderGraph, packedDesc, "LNSurface_Front_Packed", false);
            
            var extraDesc = desc;
            extraDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
            TextureHandle frontExtra = UniversalRenderer.CreateRenderGraphTexture(renderGraph, extraDesc, "LNSurface_Front_Extra", false);
            TextureHandle frontExtra2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, extraDesc, "LNSurface_Front_Extra2", false);
            TextureHandle frontInner = UniversalRenderer.CreateRenderGraphTexture(renderGraph, extraDesc, "LNSurface_Front_Inner", false); // Inner POM
            TextureHandle frontInnerParams = UniversalRenderer.CreateRenderGraphTexture(renderGraph, extraDesc, "LNSurface_Front_InnerParams", false); // Inner POM Params

            // Back Pass Textures (Full Res)
            // [Optimization] Enable auto-clear (clear: true) to remove explicit clear pass
            TextureHandle backColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Color", true);
            TextureHandle backPacked = UniversalRenderer.CreateRenderGraphTexture(renderGraph, packedDesc, "LNSurface_Back_Packed", false);

            // SSAO + SSCS Textures (Scaled Res, R8G8)
            var aoSscsDesc = scaledDesc;
            aoSscsDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8_UNorm;
            aoSscsDesc.enableRandomWrite = true;
            TextureHandle aoSscsRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoSscsDesc, "LNSurface_AO_SSCS_Raw", false);
            TextureHandle aoSscsBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoSscsDesc, "LNSurface_AO_SSCS_Blurred", false);

            // SSR Textures (Scaled Res, HDR)
            var ssrDesc = scaledDesc;
            ssrDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32;
            ssrDesc.enableRandomWrite = true;
            TextureHandle ssrRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Raw", false);
            TextureHandle ssrBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Blurred", false);
            TextureHandle ssrDenoised = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Denoised", false); // Denoised Result

            // SSGI Textures (Scaled Res, HDR)
            TextureHandle ssgiRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSGI_Raw", false);
            TextureHandle ssgiBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSGI_Blurred", false);
            TextureHandle ssgiDenoised = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSGI_Denoised", false); // Denoised Result



            // 2. Front Pass
            RenderGBufferFront(renderGraph, frameData, "Front", "LNSurface_Front", frontColor, frontPacked, frontExtra, frontExtra2, frontInner, frontInnerParams);

            // 3. Back Pass (Only if SSS is enabled)
            if (_settings.enableSSS || _settings.enableSSR || _settings.enableSSCS || _settings.enableSSGI)
            {
                RenderGBufferBack(renderGraph, frameData, "Back", "LNSurface_Back", backColor, backPacked);
            }

            // 4. SSAO + SSCS Calculation Pass
            if (_settings.enableSSAO || _settings.enableSSCS)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSAO_SSCS Pass", out var passDataAO))
                {
                    passDataAO.compute = _settings.lightingComputeShader;
                    passDataAO.kernel = _settings.lightingComputeShader.FindKernel("CS_SSAO_SSCS");
                    
                    passDataAO.frontPacked = frontPacked;
                    passDataAO.backPacked = backPacked;
                    passDataAO.frontExtra = frontExtra; // Mask
                    passDataAO.result = aoSscsRaw;
                    
                    builder.UseTexture(passDataAO.frontPacked);
                    builder.UseTexture(passDataAO.backPacked);
                    builder.UseTexture(passDataAO.frontExtra); 
                    builder.UseTexture(passDataAO.result, AccessFlags.Write);
                    
                    passDataAO.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    passDataAO.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                    passDataAO.projectionMatrix = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false);
                    passDataAO.inverseProjectionMatrix = passDataAO.projectionMatrix.inverse;
                    // Use Scaled Res Params
                    passDataAO.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    
                    passDataAO.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;
                    passDataAO.ssaoRadius = _settings.ssaoRadius;
                    passDataAO.ssaoIntensity = _settings.ssaoIntensity;
                    passDataAO.ssaoSampleCount = _settings.ssaoSampleCount;
                    
                    passDataAO.enableSSCS = _settings.enableSSCS ? 1.0f : 0.0f;
                    passDataAO.sscsDistance = _settings.sscsDistance;
                    passDataAO.sscsMaxSteps = _settings.sscsMaxSteps;
                    passDataAO.sscsThickness = _settings.sscsThickness;
                    passDataAO.sscsIntensity = _settings.sscsIntensity;
                    
                    // Main Light
                    Vector4 mainLightDir = new Vector4(0, 1, 0, 0);
                    if (lightData.mainLightIndex != -1)
                    {
                        var light = lightData.visibleLights[lightData.mainLightIndex];
                        mainLightDir = -light.localToWorldMatrix.GetColumn(2);
                    }
                    passDataAO.mainLightDirection = mainLightDir;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_AO_SSCS", data.result);
                        
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightDirection", data.mainLightDirection);
                        
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSAO", data.enableSSAO);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Radius", data.ssaoRadius);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Intensity", data.ssaoIntensity);
                        context.cmd.SetComputeIntParam(data.compute, "_SSAO_SampleCount", data.ssaoSampleCount);
                        
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSCS", data.enableSSCS);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSCS_Distance", data.sscsDistance);
                        context.cmd.SetComputeIntParam(data.compute, "_SSCS_MaxSteps", data.sscsMaxSteps);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSCS_Thickness", data.sscsThickness);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSCS_Intensity", data.sscsIntensity);
                        
                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
            }

            // 5. SSAO_SSCS Bilateral Blur Pass
            if (_settings.enableSSAO && _settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSAO_SSCS Blur Pass", out var passDataAOBlur))
                {
                    passDataAOBlur.compute = _settings.lightingComputeShader;
                    passDataAOBlur.kernel = _settings.lightingComputeShader.FindKernel("CS_SSAO_SSCS_Blur");
                    
                    passDataAOBlur.frontPacked = frontPacked; // Depth
                    passDataAOBlur.frontExtra = aoSscsRaw; // RAW input
                    passDataAOBlur.result = aoSscsBlurred;
                    
                    builder.UseTexture(passDataAOBlur.frontPacked);
                    builder.UseTexture(passDataAOBlur.frontExtra);
                    builder.UseTexture(passDataAOBlur.result, AccessFlags.Write);
                    
                    passDataAOBlur.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passDataAOBlur.ssaoRadius = _settings.ssaoRadius;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_AO_SSCS_Raw", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_AO_SSCS", data.result);
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Radius", data.ssaoRadius);
                        
                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
            }

            // --- Hi-Z Pyramid Generation ---
            TextureHandle hizPyramid = TextureHandle.nullHandle;
            if ((_settings.enableSSR || _settings.enableSSGI) && _settings.lightingComputeShader != null)
            {
                int hizWidth = scaledDesc.width;
                int hizHeight = scaledDesc.height;
                int mips = Mathf.FloorToInt(Mathf.Log(Mathf.Max(hizWidth, hizHeight), 2)) + 1;
                mips = Mathf.Min(mips, 10); // Limit levels for performance

                // Persistent RTHandle allocation for Hi-Z Pyramid (RenderGraph transient mips work inconsistently)
                if (m_HiZPyramidRT == null || m_HiZPyramidRT.rt.width != hizWidth || m_HiZPyramidRT.rt.height != hizHeight)
                {
                    m_HiZPyramidRT?.Release();
                    RenderTextureDescriptor hizDesc = new RenderTextureDescriptor(hizWidth, hizHeight, UnityEngine.Experimental.Rendering.GraphicsFormat.R16_SFloat, 0);
                    hizDesc.enableRandomWrite = true;
                    hizDesc.useMipMap = true;
                    hizDesc.autoGenerateMips = true;
                    m_HiZPyramidRT = RTHandles.Alloc(hizDesc, name: "LNSurface HiZ Pyramid v9");
                }
                
                hizPyramid = renderGraph.ImportTexture(m_HiZPyramidRT);

                // Pass 1: Initialize Mip 0 from Front Depth
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface HiZ Initialize", out var passDataHiZ))
                {
                    passDataHiZ.compute = _settings.lightingComputeShader;
                    passDataHiZ.kernel = _settings.lightingComputeShader.FindKernel("CS_HiZ_Initialize");
                    passDataHiZ.frontPacked = frontPacked;
                    passDataHiZ.result = hizPyramid;
                    passDataHiZ.screenParams = new Vector4(hizWidth, hizHeight, 1.0f / hizWidth, 1.0f / hizHeight);

                    builder.UseTexture(passDataHiZ.frontPacked);
                    builder.UseTexture(passDataHiZ.result, AccessFlags.Write);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HiZ_Destination", data.result, 0);
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams);
                        
                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // Subsequent Passes: Downsample to higher mips
                for (int i = 1; i < mips; i++)
                {
                    int dw = Mathf.Max(1, hizWidth >> i);
                    int dh = Mathf.Max(1, hizHeight >> i);

                    using (var builder = renderGraph.AddComputePass<ComputePassData>($"LNSurface HiZ Downsample Mip {i}", out var passDataHiZDown))
                    {
                        passDataHiZDown.compute = _settings.lightingComputeShader;
                        passDataHiZDown.kernel = _settings.lightingComputeShader.FindKernel("CS_HiZ_Downsample");
                        passDataHiZDown.hizPyramid = hizPyramid;
                        passDataHiZDown.mipLevel = i;
                        passDataHiZDown.screenParams = new Vector4(dw, dh, 1.0f / dw, 1.0f / dh);

                        // UseTexture for both read and write on different mips
                        builder.UseTexture(passDataHiZDown.hizPyramid, AccessFlags.ReadWrite);

                        builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                        {
                            context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HiZ_Source", data.hizPyramid, data.mipLevel - 1);
                            context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HiZ_Destination", data.hizPyramid, data.mipLevel);
                            
                            int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                            int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                            context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                        });
                    }
                }
            }

            // 6. SSR Calculation Pass
            if (_settings.enableSSR && _settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSR Pass", out var passDataSSR))
                {
                    passDataSSR.compute = _settings.lightingComputeShader;
                    passDataSSR.kernel = _settings.lightingComputeShader.FindKernel("CS_SSR");

                    passDataSSR.frontPacked = frontPacked;
                    passDataSSR.backPacked = backPacked;
                    passDataSSR.backColor = backColor;
                    passDataSSR.sceneColor = resourceData.activeColorTexture; 
                    passDataSSR.frontExtra = frontExtra; // Mask & Smoothness
                    passDataSSR.result = ssrRaw;
                    passDataSSR.hizPyramid = hizPyramid;

                    builder.UseTexture(passDataSSR.frontPacked);
                    builder.UseTexture(passDataSSR.backPacked);
                    builder.UseTexture(passDataSSR.backColor);
                    builder.UseTexture(passDataSSR.sceneColor);
                    builder.UseTexture(passDataSSR.frontExtra);
                    builder.UseTexture(passDataSSR.result, AccessFlags.Write);
                    if (passDataSSR.hizPyramid.IsValid()) builder.UseTexture(passDataSSR.hizPyramid);

                    // Use Scaled Res Params
                    passDataSSR.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passDataSSR.ssrMaxSteps = _settings.ssrMaxSteps;
                    passDataSSR.ssrStepSize = _settings.ssrStepSize;
                    passDataSSR.ssrThickness = _settings.ssrThickness;
                    passDataSSR.worldToCameraMatrix = cameraData.GetViewMatrix();
                    passDataSSR.cameraToWorldMatrix = cameraData.GetViewMatrix().inverse;
                    passDataSSR.projectionMatrix = cameraData.GetProjectionMatrix();
                    passDataSSR.inverseProjectionMatrix = cameraData.GetProjectionMatrix().inverse;
                    passDataSSR.enableCheckerboard = _settings.enableCheckerboard ? 1.0f : 0.0f;
                    passDataSSR.frameCount = _frameCount;
                    passDataSSR.maxMipLevel = Mathf.Min(10, Mathf.FloorToInt(Mathf.Log(Mathf.Max(scaledDesc.width, scaledDesc.height), 2)));
                    passDataSSR.debugMode = (int)_settings.debugMode;
                    passDataSSR.useBackfaceRaymarching = _settings.useBackfaceRaymarching ? 1.0f : 0.0f; // Added

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Color", data.backColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSR", data.result);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HiZ_Pyramid", data.hizPyramid.IsValid() ? data.hizPyramid : data.frontPacked); // Default to front depth if HiZ invalid

                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams); // Renamed
                        context.cmd.SetComputeIntParam(data.compute, "_SSR_MaxSteps", data.ssrMaxSteps);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSR_StepSize", data.ssrStepSize);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSR_Thickness", data.ssrThickness);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableCheckerboard", data.enableCheckerboard);
                        context.cmd.SetComputeIntParam(data.compute, "_FrameCount", data.frameCount);
                        context.cmd.SetComputeIntParam(data.compute, "_HiZ_MaxMip", data.maxMipLevel);
                        context.cmd.SetComputeIntParam(data.compute, "_DebugMode", data.debugMode);
                        context.cmd.SetComputeFloatParam(data.compute, "_UseBackfaceRaymarching", data.useBackfaceRaymarching); // Added

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
     // 7. SSR Bilateral Blur Pass
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSR Blur Pass", out var passDataSSRBlur))
                {
                    passDataSSRBlur.compute = _settings.lightingComputeShader;
                    passDataSSRBlur.kernel = _settings.lightingComputeShader.FindKernel("CS_SSR_Blur");

                    passDataSSRBlur.frontPacked = frontPacked; // Depth
                    passDataSSRBlur.frontExtra = frontExtra; // Smoothness
                    passDataSSRBlur.ssrTexture = ssrRaw; // Source SSR
                    passDataSSRBlur.result = ssrBlurred; // Final SSR

                    builder.UseTexture(passDataSSRBlur.frontPacked);
                    builder.UseTexture(passDataSSRBlur.frontExtra);
                    builder.UseTexture(passDataSSRBlur.ssrTexture);
                    builder.UseTexture(passDataSSRBlur.result, AccessFlags.Write);

                    // Use Scaled Res Params
                    passDataSSRBlur.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSR_Raw", data.ssrTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSR", data.result);
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams); // Renamed

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // 7.2 SSR Temporal Reprojection Pass (New)
                // History Buffer Management
                if (!_ssrHistoryBuffers.TryGetValue(cameraData.camera, out var historyBuffers))
                {
                    historyBuffers = new RTHandle[2];
                    var historyDesc = ssrDesc;
                    historyDesc.enableRandomWrite = true;
                    historyBuffers[0] = RTHandles.Alloc(historyDesc, name: $"_LNSurface_SSR_History0_{cameraData.camera.name}");
                    historyBuffers[1] = RTHandles.Alloc(historyDesc, name: $"_LNSurface_SSR_History1_{cameraData.camera.name}");
                    _ssrHistoryBuffers[cameraData.camera] = historyBuffers;
                }

                // Swap buffers (Ping-Pong)
                var readHistory = historyBuffers[0];
                var writeHistory = historyBuffers[1];
                historyBuffers[0] = writeHistory;
                historyBuffers[1] = readHistory;
                _ssrHistoryBuffers[cameraData.camera] = historyBuffers; // Update reference

                // Import persistent history into RenderGraph
                TextureHandle historyReadHandle = renderGraph.ImportTexture(readHistory);
                TextureHandle historyWriteHandle = renderGraph.ImportTexture(writeHistory);

                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSR Temporal Pass", out var passDataSSRTemporal))
                {
                    passDataSSRTemporal.compute = _settings.lightingComputeShader;
                    passDataSSRTemporal.kernel = _settings.lightingComputeShader.FindKernel("CS_TemporalReprojection");

                    passDataSSRTemporal.frontPacked = frontPacked; // Depth for Velocity
                    passDataSSRTemporal.currentFrameResult = ssrBlurred; // Input (Noisy)
                    passDataSSRTemporal.historyBuffer = historyReadHandle; // Previous Frame
                    passDataSSRTemporal.newHistoryBuffer = historyWriteHandle; // Output (Denoised)
                    
                    builder.UseTexture(passDataSSRTemporal.frontPacked);
                    builder.UseTexture(passDataSSRTemporal.currentFrameResult);
                    builder.UseTexture(passDataSSRTemporal.historyBuffer);
                    builder.UseTexture(passDataSSRTemporal.newHistoryBuffer, AccessFlags.Write);

                    passDataSSRTemporal.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passDataSSRTemporal.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    
                    Matrix4x4 currentViewProj = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false) * cameraData.camera.worldToCameraMatrix;
                    if (!_prevViewProjMatrices.TryGetValue(cameraData.camera, out Matrix4x4 prevViewProj))
                    {
                        prevViewProj = currentViewProj;
                    }
                    passDataSSRTemporal.prevViewProjMatrix = prevViewProj;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_CurrentFrameResult", data.currentFrameResult);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HistoryBuffer", data.historyBuffer);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_NewHistoryBuffer", data.newHistoryBuffer);
                        
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_PrevViewProjMatrix", data.prevViewProjMatrix);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableCheckerboard", _settings.enableCheckerboard ? 1.0f : 0.0f);
                        context.cmd.SetComputeIntParam(data.compute, "_FrameCount", _frameCount);

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
                
                // Update SSR Texture to use the Denoised Result
                ssrDenoised = historyWriteHandle; 
            }

            // 7.5 SSGI Calculation Pass
            if (_settings.enableSSGI && _settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSGI Pass", out var passDataSSGI))
                {
                    passDataSSGI.compute = _settings.lightingComputeShader;
                    passDataSSGI.kernel = _settings.lightingComputeShader.FindKernel("CS_SSGI");

                    passDataSSGI.frontColor = frontColor; 
                    passDataSSGI.frontPacked = frontPacked;
                    passDataSSGI.frontExtra = frontExtra; // Mask
                    passDataSSGI.backPacked = backPacked; // Needed for Raymarch
                    passDataSSGI.backColor = backColor; // Added
                    passDataSSGI.sceneColor = resourceData.activeColorTexture;
                    passDataSSGI.result = ssgiRaw;
                    passDataSSGI.hizPyramid = hizPyramid;

                    builder.UseTexture(passDataSSGI.frontColor); 
                    builder.UseTexture(passDataSSGI.frontPacked);
                    builder.UseTexture(passDataSSGI.frontExtra);
                    builder.UseTexture(passDataSSGI.backPacked);
                    builder.UseTexture(passDataSSGI.backColor); // Added
                    builder.UseTexture(passDataSSGI.sceneColor);
                    builder.UseTexture(passDataSSGI.result, AccessFlags.Write);
                    if (passDataSSGI.hizPyramid.IsValid()) builder.UseTexture(passDataSSGI.hizPyramid);

                    // Use Scaled Res Params
                    passDataSSGI.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passDataSSGI.ssgiSampleCount = _settings.ssgiSampleCount;
                    passDataSSGI.ssgiRayStepSize = _settings.ssgiRayStepSize;
                    passDataSSGI.worldToCameraMatrix = cameraData.GetViewMatrix();
                    passDataSSGI.cameraToWorldMatrix = cameraData.GetViewMatrix().inverse;
                    passDataSSGI.projectionMatrix = cameraData.GetProjectionMatrix();
                    passDataSSGI.inverseProjectionMatrix = cameraData.GetProjectionMatrix().inverse;
                    passDataSSGI.enableCheckerboard = _settings.enableCheckerboard ? 1.0f : 0.0f;
                    passDataSSGI.frameCount = _frameCount;
                    passDataSSGI.maxMipLevel = Mathf.Min(10, Mathf.FloorToInt(Mathf.Log(Mathf.Max(scaledDesc.width, scaledDesc.height), 2)));
                    passDataSSGI.ssgiMaxSteps = _settings.ssgiMaxSteps; // Pass SSGI Max Steps
                    passDataSSGI.useBackfaceRaymarching = _settings.useBackfaceRaymarching ? 1.0f : 0.0f; // Added

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Color", data.frontColor); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Color", data.backColor); // Added
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSGI", data.result);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HiZ_Pyramid", data.hizPyramid.IsValid() ? data.hizPyramid : data.frontPacked); // Default to front depth if HiZ invalid

                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams); // Renamed
                        context.cmd.SetComputeIntParam(data.compute, "_SSGI_SampleCount", data.ssgiSampleCount);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSGI_RayStepSize", data.ssgiRayStepSize);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableCheckerboard", data.enableCheckerboard);
                        context.cmd.SetComputeIntParam(data.compute, "_FrameCount", data.frameCount);
                        context.cmd.SetComputeIntParam(data.compute, "_HiZ_MaxMip", data.maxMipLevel);
                        context.cmd.SetComputeIntParam(data.compute, "_SSGI_MaxSteps", data.ssgiMaxSteps); // Set SSGI Max Steps
                        context.cmd.SetComputeFloatParam(data.compute, "_UseBackfaceRaymarching", data.useBackfaceRaymarching); // Added

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // 7.6 SSGI Blur Pass
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSGI Blur Pass", out var passDataSSGIBlur))
                {
                    passDataSSGIBlur.compute = _settings.lightingComputeShader;
                    passDataSSGIBlur.kernel = _settings.lightingComputeShader.FindKernel("CS_SSGI_Blur");

                    passDataSSGIBlur.frontPacked = frontPacked; // Depth
                    passDataSSGIBlur.ssgiTexture = ssgiRaw; // Source SSGI
                    passDataSSGIBlur.result = ssgiBlurred; // Final SSGI

                    builder.UseTexture(passDataSSGIBlur.frontPacked);
                    builder.UseTexture(passDataSSGIBlur.ssgiTexture);
                    builder.UseTexture(passDataSSGIBlur.result, AccessFlags.Write);

                    // Use Scaled Res Params
                    passDataSSGIBlur.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSGI_Raw", data.ssgiTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSGI", data.result);
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams); // Renamed

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // 7.7 SSGI Temporal Reprojection Pass (New)
                // History Buffer Management
                if (!_ssgiHistoryBuffers.TryGetValue(cameraData.camera, out var ssgiHistoryBuffers))
                {
                    ssgiHistoryBuffers = new RTHandle[2];
                    var historyDesc = ssrDesc; // Same format as SSR
                    historyDesc.enableRandomWrite = true;
                    ssgiHistoryBuffers[0] = RTHandles.Alloc(historyDesc, name: $"_LNSurface_SSGI_History0_{cameraData.camera.name}");
                    ssgiHistoryBuffers[1] = RTHandles.Alloc(historyDesc, name: $"_LNSurface_SSGI_History1_{cameraData.camera.name}");
                    _ssgiHistoryBuffers[cameraData.camera] = ssgiHistoryBuffers;
                }

                // Swap buffers (Ping-Pong)
                var readHistory = ssgiHistoryBuffers[0];
                var writeHistory = ssgiHistoryBuffers[1];
                ssgiHistoryBuffers[0] = writeHistory;
                ssgiHistoryBuffers[1] = readHistory;
                _ssgiHistoryBuffers[cameraData.camera] = ssgiHistoryBuffers; // Update reference

                // Import persistent history into RenderGraph
                TextureHandle historyReadHandle = renderGraph.ImportTexture(readHistory);
                TextureHandle historyWriteHandle = renderGraph.ImportTexture(writeHistory);

                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSGI Temporal Pass", out var passDataSSGITemporal))
                {
                    passDataSSGITemporal.compute = _settings.lightingComputeShader;
                    passDataSSGITemporal.kernel = _settings.lightingComputeShader.FindKernel("CS_TemporalReprojection");

                    passDataSSGITemporal.frontPacked = frontPacked; // Depth for Velocity
                    passDataSSGITemporal.currentFrameResult = ssgiBlurred; // Input (Noisy)
                    passDataSSGITemporal.historyBuffer = historyReadHandle; // Previous Frame
                    passDataSSGITemporal.newHistoryBuffer = historyWriteHandle; // Output (Denoised)
                    
                    builder.UseTexture(passDataSSGITemporal.frontPacked);
                    builder.UseTexture(passDataSSGITemporal.currentFrameResult);
                    builder.UseTexture(passDataSSGITemporal.historyBuffer);
                    builder.UseTexture(passDataSSGITemporal.newHistoryBuffer, AccessFlags.Write);

                    passDataSSGITemporal.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passDataSSGITemporal.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    
                    Matrix4x4 currentViewProj = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false) * cameraData.camera.worldToCameraMatrix;
                    if (!_prevViewProjMatrices.TryGetValue(cameraData.camera, out Matrix4x4 prevViewProj))
                    {
                        prevViewProj = currentViewProj;
                    }
                    passDataSSGITemporal.prevViewProjMatrix = prevViewProj;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_CurrentFrameResult", data.currentFrameResult);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HistoryBuffer", data.historyBuffer);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_NewHistoryBuffer", data.newHistoryBuffer);
                        
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_PrevViewProjMatrix", data.prevViewProjMatrix);

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
                
                // Update SSGI Texture to use the Denoised Result
                ssgiDenoised = historyWriteHandle; 
            }



            // 8. Lighting Pass (Disney BRDF)
            TextureHandle lightingResult = resourceData.activeColorTexture; // Fallback
            if (_settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface Lighting Pass", out var passDataLighting))
                {
                    passDataLighting.compute = _settings.lightingComputeShader;
                    passDataLighting.kernel = _settings.lightingComputeShader.FindKernel("CSMain");

                    var resultDesc = cameraData.cameraTargetDescriptor;
                    resultDesc.depthBufferBits = 0; 
                    resultDesc.enableRandomWrite = true;
                    resultDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                    RenderingUtils.ReAllocateHandleIfNeeded(ref _resultHandle, resultDesc, name: "_LNSurface_LightingResult");

                    passDataLighting.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    passDataLighting.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                    passDataLighting.projectionMatrix = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false);
                    passDataLighting.inverseProjectionMatrix = passDataLighting.projectionMatrix.inverse;
                    passDataLighting.screenParams = new Vector4(resultDesc.width, resultDesc.height, 1.0f / resultDesc.width, 1.0f / resultDesc.height);

                    // Temporal Reprojection Matrix Update
                    Matrix4x4 currentViewProj = passDataLighting.projectionMatrix * passDataLighting.worldToCameraMatrix;
                    if (!_prevViewProjMatrices.TryGetValue(cameraData.camera, out Matrix4x4 prevViewProj))
                    {
                        prevViewProj = currentViewProj; // First frame
                    }
                    passDataLighting.prevViewProjMatrix = prevViewProj;
                    _prevViewProjMatrices[cameraData.camera] = currentViewProj; // Update for next frame

                    // SSR Params
                    passDataLighting.enableSSR = _settings.enableSSR ? 1.0f : 0.0f;
                    passDataLighting.ssrMaxSteps = _settings.ssrMaxSteps;
                    passDataLighting.ssrStepSize = _settings.ssrStepSize;
                    passDataLighting.ssrThickness = _settings.ssrThickness;

                    // Main Light
                    Vector4 mainLightDir = new Vector4(0, 1, 0, 0);
                    Vector4 mainLightCol = Vector4.zero;

                    if (lightData.mainLightIndex != -1)
                    {
                        var light = lightData.visibleLights[lightData.mainLightIndex];
                        mainLightDir = -light.localToWorldMatrix.GetColumn(2);
                        mainLightCol = light.finalColor;
                    }

                    passDataLighting.mainLightDirection = mainLightDir;
                    passDataLighting.mainLightColor = mainLightCol;
                    
                    if (_settings.enableSSAO || _settings.enableSSCS)
                    {
                        passDataLighting.ssaoTexture = aoSscsBlurred; 
                    }
                    else
                    {
                        passDataLighting.ssaoTexture = frontColor; // Dummy
                    }

                    if (_settings.enableSSR)
                    {
                        // Use Denoised SSR if available, otherwise Blurred
                        passDataLighting.ssrTexture = ssrDenoised.IsValid() ? ssrDenoised : ssrBlurred;
                    }
                    else
                    {
                        passDataLighting.ssrTexture = ssrBlurred; // Dummy with UAV support
                    }

                    // SSGI Texture Binding Logic
                    if (_settings.enableSSGI)
                    {
                        // Use Denoised SSGI if available, otherwise Blurred
                        passDataLighting.ssgiTexture = ssgiDenoised.IsValid() ? ssgiDenoised : ssgiBlurred;
                    }
                    else
                    {
                        // Bind dummy texture to prevent "Property not set" error
                        passDataLighting.ssgiTexture = frontColor; // Any valid texture
                    }



                    passDataLighting.enableSSR = _settings.enableSSR ? 1.0f : 0.0f;
                    passDataLighting.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;
                    passDataLighting.enableSSGI = _settings.enableSSGI ? 1.0f : 0.0f;
                    passDataLighting.enableSSCS = _settings.enableSSCS ? 1.0f : 0.0f;
                    passDataLighting.debugMode = (int)_settings.debugMode;
                    passDataLighting.ssgiIntensity = _settings.ssgiIntensity;
                    passDataLighting.sscsIntensity = _settings.sscsIntensity; // Added

                    // Material Global Settings
                    passDataLighting.specularStrength = _settings.specularStrength;
                    passDataLighting.diffuseWrap = _settings.diffuseWrap;

                    // Shadow Tint Settings
                    passDataLighting.shadowTintMode = (int)_settings.shadowTintMode;
                    passDataLighting.shadowTintColor = _settings.shadowTintColor;
                    passDataLighting.shadowTintIntensity = _settings.shadowTintIntensity;
                    passDataLighting.shadowThreshold = _settings.shadowThreshold;

                    // Inner POM Settings
                    passDataLighting.enableInnerPOM = _settings.enableInnerPOM ? 1.0f : 0.0f;
                    // Per-pixel params are now in GBuffer

                    // Additional Lights
                    passDataLighting.additionalLightPositions = new Vector4[16];
                    passDataLighting.additionalLightColors = new Vector4[16];
                    passDataLighting.additionalLightAttenuations = new Vector4[16];

                    int addCount = 0;
                    for (int i = 0; i < lightData.visibleLights.Length && addCount < 16; i++)
                    {
                        if (i == lightData.mainLightIndex)
                            continue;

                        var light = lightData.visibleLights[i];
                        passDataLighting.additionalLightPositions[addCount] = light.localToWorldMatrix.GetColumn(3);
                        passDataLighting.additionalLightColors[addCount] = light.finalColor;
                        
                        float rangeSq = light.range * light.range;
                        passDataLighting.additionalLightAttenuations[addCount] = new Vector4(1.0f / Mathf.Max(rangeSq, 0.0001f), 1.0f, 0, 0);
                        
                        addCount++;
                    }
                    passDataLighting.additionalLightCount = addCount;
                    // passDataLighting.intensity = _settings.intensity; // Removed
                    // passDataLighting.thicknessScale = _settings.thicknessScale; // Removed
                    passDataLighting.enableSSS = _settings.enableSSS ? 1.0f : 0.0f; 
                    passDataLighting.lightingModel = (int)_settings.lightingModel;

                    // SSAO Init
                    passDataLighting.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;
                    passDataLighting.ssaoRadius = _settings.ssaoRadius;
                    passDataLighting.ssaoIntensity = _settings.ssaoIntensity;
                    passDataLighting.ssaoSampleCount = _settings.ssaoSampleCount;

                    passDataLighting.frontColor = frontColor;
                    passDataLighting.frontPacked = frontPacked; 
                    passDataLighting.backPacked = backPacked; 
                    passDataLighting.backColor = backColor;
                    passDataLighting.sceneColor = resourceData.activeColorTexture; 
                    passDataLighting.frontExtra = frontExtra;
                    passDataLighting.frontExtra2 = frontExtra2; // Added
                    passDataLighting.frontInner = frontInner; // Inner POM
                    passDataLighting.frontInnerParams = frontInnerParams; // Inner POM Params
                    
                    passDataLighting.result = renderGraph.ImportTexture(_resultHandle);
                    lightingResult = passDataLighting.result;
                    passDataLighting.hizPyramid = hizPyramid; // Added
                    
                    // SH Data Extraction
                    SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
                    passDataLighting.shAr = new Vector4[1]; passDataLighting.shAg = new Vector4[1]; passDataLighting.shAb = new Vector4[1];
                    passDataLighting.shBr = new Vector4[1]; passDataLighting.shBg = new Vector4[1]; passDataLighting.shBb = new Vector4[1];
                    passDataLighting.shC = new Vector4[1];
                    
                    // L0, L1
                    passDataLighting.shAr[0] = new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0] - sh[0, 6]);
                    passDataLighting.shAg[0] = new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0] - sh[1, 6]);
                    passDataLighting.shAb[0] = new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0] - sh[2, 6]);
                    
                    // L2
                    passDataLighting.shBr[0] = new Vector4(sh[0, 4], sh[0, 6], sh[0, 5] * 3.0f, sh[0, 7]);
                    passDataLighting.shBg[0] = new Vector4(sh[1, 4], sh[1, 6], sh[1, 5] * 3.0f, sh[1, 7]);
                    passDataLighting.shBb[0] = new Vector4(sh[2, 4], sh[2, 6], sh[2, 5] * 3.0f, sh[2, 7]);
                    
                    passDataLighting.shC[0] = new Vector4(sh[0, 8], sh[2, 8], sh[1, 8], 1.0f);

                    builder.UseTexture(passDataLighting.frontColor);
                    builder.UseTexture(passDataLighting.frontPacked); 
                    builder.UseTexture(passDataLighting.backPacked); 
                    builder.UseTexture(passDataLighting.backColor);
                    builder.UseTexture(passDataLighting.frontExtra);
                    builder.UseTexture(passDataLighting.frontExtra2); // Added
                    builder.UseTexture(passDataLighting.frontInner); // Inner POM
                    builder.UseTexture(passDataLighting.frontInnerParams); // Inner POM Params
                    builder.UseTexture(passDataLighting.sceneColor); 
                    builder.UseTexture(passDataLighting.result, AccessFlags.Write); // Ensure result is writable

                    // Always use textures (real or dummy) to prevent binding error
                    builder.UseTexture(passDataLighting.ssaoTexture);
                    builder.UseTexture(passDataLighting.ssrTexture);
                    builder.UseTexture(passDataLighting.ssgiTexture);
                    if (passDataLighting.hizPyramid.IsValid()) builder.UseTexture(passDataLighting.hizPyramid); // Added

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Color", data.frontColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Color", data.backColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra2", data.frontExtra2);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Inner", data.frontInner); // Inner POM
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_InnerParams", data.frontInnerParams); // Inner POM Params
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor);
                        
                        // Always bind textures, even if disabled (using dummy)
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_AO_SSCS_Blurred", data.ssaoTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_SSR_Blurred", data.ssrTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_SSGI_Blurred", data.ssgiTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSR", data.ssrTexture); // Fixed: Bind SSR result even if used only for debug visualization in CSMain
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_HiZ_Pyramid", data.hizPyramid.IsValid() ? data.hizPyramid : data.frontPacked); // Added
                        
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result", data.result);

                        // Set Params
                        context.cmd.SetComputeVectorParam(data.compute, "_LNSurface_ScreenParams", data.screenParams);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightDirection", data.mainLightDirection);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightColor", data.mainLightColor);
                        
                        context.cmd.SetComputeIntParam(data.compute, "_AdditionalLightCount", data.additionalLightCount);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_AdditionalLightPositions", data.additionalLightPositions);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_AdditionalLightColors", data.additionalLightColors);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_AdditionalLightAttenuations", data.additionalLightAttenuations);
                        
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);
                        
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSAO", data.enableSSAO);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSR", data.enableSSR);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSGI", data.enableSSGI);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSCS", data.enableSSCS);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSGI_Intensity", data.ssgiIntensity);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSCS_Intensity", data.sscsIntensity); // Added
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSS", data.enableSSS);
                        context.cmd.SetComputeIntParam(data.compute, "_LightingModel", data.lightingModel);
                        context.cmd.SetComputeIntParam(data.compute, "_DebugMode", data.debugMode);

                        // Material Global Settings
                        context.cmd.SetComputeFloatParam(data.compute, "_SpecularStrength", data.specularStrength);
                        context.cmd.SetComputeFloatParam(data.compute, "_DiffuseWrap", data.diffuseWrap);

                        // Shadow Tint Settings
                        context.cmd.SetComputeIntParam(data.compute, "_ShadowTintMode", data.shadowTintMode);
                        context.cmd.SetComputeVectorParam(data.compute, "_ShadowTintColor", data.shadowTintColor);
                        context.cmd.SetComputeFloatParam(data.compute, "_ShadowTintIntensity", data.shadowTintIntensity);
                        context.cmd.SetComputeFloatParam(data.compute, "_ShadowThreshold", data.shadowThreshold);

                        // Inner POM Settings
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableInnerPOM", data.enableInnerPOM);
                        // Per-pixel params are now in GBuffer
                        
                        // SH
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHAr", data.shAr);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHAg", data.shAg);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHAb", data.shAb);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHBr", data.shBr);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHBg", data.shBg);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHBb", data.shBb);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_SHC", data.shC);

                        int groupsX = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int groupsY = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, groupsX, groupsY, 1);
                    });
                }
            }
            
            // 6. Blit Final Result
            TextureHandle finalResult = lightingResult;
            var blitParams = new RenderGraphUtils.BlitMaterialParameters(finalResult, resourceData.activeColorTexture, 
                Blitter.GetBlitMaterial(TextureDimension.Tex2D), 0);
            renderGraph.AddBlitPass(blitParams, "LNSurface Final Blit");
        } // End of RecordRenderGraph

        private void RenderGBufferFront(RenderGraph renderGraph, ContextContainer frameData, string name, string lightMode, TextureHandle color, TextureHandle packed, TextureHandle extra, TextureHandle extra2, TextureHandle inner, TextureHandle innerParams)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"LNSurface GBuffer {name} Pass", out var passData))
            {
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachment(packed, 1);
                builder.SetRenderAttachment(extra, 2);
                builder.SetRenderAttachment(extra2, 3);
                builder.SetRenderAttachment(inner, 4); // Inner POM
                builder.SetRenderAttachment(innerParams, 5); // Inner POM Params
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                DrawingSettings drawSettings = new DrawingSettings(new ShaderTagId(lightMode), new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque });
                
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((GBufferPassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, Color.clear); 
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }

        private void RenderGBufferBack(RenderGraph renderGraph, ContextContainer frameData, string name, string lightMode, TextureHandle color, TextureHandle packed)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            
            // [Removed] 1. Clear Back Color (Black) - Handled by CreateRenderGraphTexture(clear: true)

            // [Kept] 2. Clear Back Packed (Depth=1000)
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"Clear {name} Packed", out var passData))
            {
                builder.SetRenderAttachment(packed, 0, AccessFlags.Write);
                builder.SetRenderFunc((GBufferPassData data, RasterGraphContext context) =>
                {
                    context.cmd.ClearRenderTarget(false, true, new Color(0, 0, 1000.0f, 0));
                });
            }

            // [Kept] 3. Render Pass (No Clear)
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"LNSurface GBuffer {name} Pass", out var passData))
            {
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachment(packed, 1);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Read); // Read Only
                
                passData.packedTexture = packed;

                DrawingSettings drawSettings = new DrawingSettings(new ShaderTagId(lightMode), new SortingSettings(cameraData.camera) { criteria = SortingCriteria.BackToFront });
                
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((GBufferPassData data, RasterGraphContext context) =>
                {
                    // No Clear Here
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}
