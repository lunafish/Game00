using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

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

        public LayerMask layerMask = -1;
        public ComputeShader lightingComputeShader; // Disney BRDF Lighting
        public LightingModel lightingModel = LightingModel.DisneyBRDF; // Selection
        
        [Header("Optimization")]
        public ResolutionScale resolutionScale = ResolutionScale.Half;

        public bool enableSSS = true; 
        // intensity and thicknessScale removed (controlled by material)

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
        [Range(0.1f, 2.0f)] public float ssgiRayStepSize = 0.5f;

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

        public LNSurfacePass(LNSurfaceSettings settings)
        {
            _settings = settings;
            renderPassEvent = settings.renderPassEvent;
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, settings.layerMask);
            _resultHandle = RTHandles.Alloc("_LNSurface_Result", name: "_LNSurface_Result");
            
            if (settings.lightingComputeShader != null)
                _kernelLighting = settings.lightingComputeShader.FindKernel("CSMain");
        }

        public void Dispose()
        {
            _resultHandle?.Release();
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
            
            internal TextureHandle backColor;
            internal TextureHandle backPacked; // Normal(RG) + Depth(B)
            
            internal TextureHandle sceneColor; 
            internal TextureHandle ssaoTexture; // SSAO Blurred Result
            internal TextureHandle ssrTexture; // SSR Blurred Result
            internal TextureHandle ssgiTexture; // SSGI Blurred Result
            internal TextureHandle result; // Input/Output
            
            internal float intensity;
            internal float thicknessScale;
            internal float enableSSS; 
            internal int lightingModel; // 0: HalfLambert, 1: DisneyBRDF
            internal Vector2 screenParams;

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
            internal float ssgiRayStepSize;

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
            
            // Ambient SH
            internal Vector4[] shAr;
            internal Vector4[] shAg;
            internal Vector4[] shAb;
            internal Vector4[] shBr;
            internal Vector4[] shBg;
            internal Vector4[] shBb;
            internal Vector4[] shC;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>(); 

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

            // Back Pass Textures (Full Res)
            TextureHandle backColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Color", false);
            TextureHandle backPacked = UniversalRenderer.CreateRenderGraphTexture(renderGraph, packedDesc, "LNSurface_Back_Packed", false);

            // SSAO Textures (Scaled Res, R8)
            var aoDesc = scaledDesc;
            aoDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm;
            aoDesc.enableRandomWrite = true;
            TextureHandle aoRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoDesc, "LNSurface_AO_Raw", false);
            TextureHandle aoBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoDesc, "LNSurface_AO_Blurred", false);

            // SSR Textures (Scaled Res, HDR)
            var ssrDesc = scaledDesc;
            ssrDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32;
            ssrDesc.enableRandomWrite = true;
            TextureHandle ssrRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Raw", false);
            TextureHandle ssrBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Blurred", false);

            // SSGI Textures (Scaled Res, HDR)
            TextureHandle ssgiRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSGI_Raw", false);
            TextureHandle ssgiBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSGI_Blurred", false);

            // 2. Front Pass
            RenderGBufferFront(renderGraph, frameData, "Front", "LNSurface_Front", frontColor, frontPacked, frontExtra, frontExtra2);

            // 3. Back Pass (Only if SSS OR SSR is enabled)
            if (_settings.enableSSS || _settings.enableSSR)
            {
                RenderGBufferBack(renderGraph, frameData, "Back", "LNSurface_Back", backColor, backPacked);
            }

            // 4. SSAO Calculation Pass
            if (_settings.enableSSAO)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSAO Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CS_SSAO");
                    
                    passData.frontPacked = frontPacked;
                    passData.backPacked = backPacked;
                    passData.frontExtra = frontExtra; // Mask
                    passData.result = aoRaw;

                    builder.UseTexture(passData.frontPacked);
                    builder.UseTexture(passData.backPacked);
                    builder.UseTexture(passData.frontExtra); 
                    builder.UseTexture(passData.result, AccessFlags.Write);
                    
                    passData.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    passData.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                    passData.projectionMatrix = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false);
                    // Use Scaled Res Params
                    passData.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passData.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;
                    passData.ssaoRadius = _settings.ssaoRadius;
                    passData.ssaoIntensity = _settings.ssaoIntensity;
                    passData.ssaoSampleCount = _settings.ssaoSampleCount;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra); // Mask
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_AO", data.result);
                        
                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSAO", data.enableSSAO);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Radius", data.ssaoRadius);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Intensity", data.ssaoIntensity);
                        context.cmd.SetComputeIntParam(data.compute, "_SSAO_SampleCount", data.ssaoSampleCount);
                        
                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // 5. SSAO Bilateral Blur Pass
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSAO Blur Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CS_SSAO_Blur");
                    
                    passData.frontPacked = frontPacked; // Depth
                    passData.frontExtra = aoRaw; // Use frontExtra field for RAW AO input
                    passData.result = aoBlurred;
                    
                    builder.UseTexture(passData.frontPacked);
                    builder.UseTexture(passData.frontExtra);
                    builder.UseTexture(passData.result, AccessFlags.Write);
                    
                    // Use Scaled Res Params
                    passData.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passData.ssaoRadius = _settings.ssaoRadius; // For variable radius

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_AO_Raw", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_AO", data.result);
                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Radius", data.ssaoRadius);
                        
                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
            }

            // 6. SSR Calculation Pass
            if (_settings.enableSSR && _settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSR Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CS_SSR");

                    passData.frontPacked = frontPacked;
                    passData.backPacked = backPacked;
                    passData.backColor = backColor;
                    passData.sceneColor = resourceData.activeColorTexture; 
                    passData.frontExtra = frontExtra; // Mask & Smoothness
                    passData.result = ssrRaw;

                    builder.UseTexture(passData.frontPacked);
                    builder.UseTexture(passData.backPacked);
                    builder.UseTexture(passData.backColor);
                    builder.UseTexture(passData.sceneColor);
                    builder.UseTexture(passData.frontExtra);
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    // Use Scaled Res Params
                    passData.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passData.ssrMaxSteps = _settings.ssrMaxSteps;
                    passData.ssrStepSize = _settings.ssrStepSize;
                    passData.ssrThickness = _settings.ssrThickness;
                    passData.worldToCameraMatrix = cameraData.GetViewMatrix();
                    passData.cameraToWorldMatrix = cameraData.GetViewMatrix().inverse;
                    passData.projectionMatrix = cameraData.GetProjectionMatrix();
                    passData.inverseProjectionMatrix = cameraData.GetProjectionMatrix().inverse;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Color", data.backColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSR", data.result);

                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                        context.cmd.SetComputeIntParam(data.compute, "_SSR_MaxSteps", data.ssrMaxSteps);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSR_StepSize", data.ssrStepSize);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSR_Thickness", data.ssrThickness);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // 7. SSR Bilateral Blur Pass
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSR Blur Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CS_SSR_Blur");

                    passData.frontPacked = frontPacked; // Depth
                    passData.frontExtra = frontExtra; // Smoothness
                    passData.ssrTexture = ssrRaw; // Source SSR
                    passData.result = ssrBlurred; // Final SSR

                    builder.UseTexture(passData.frontPacked);
                    builder.UseTexture(passData.frontExtra);
                    builder.UseTexture(passData.ssrTexture);
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    // Use Scaled Res Params
                    passData.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSR_Raw", data.ssrTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSR", data.result);
                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
            }

            // 7.5 SSGI Calculation Pass
            if (_settings.enableSSGI && _settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSGI Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CS_SSGI");

                    passData.frontColor = frontColor; 
                    passData.frontPacked = frontPacked;
                    passData.frontExtra = frontExtra; // Mask
                    passData.backPacked = backPacked; // Needed for Raymarch
                    passData.sceneColor = resourceData.activeColorTexture;
                    passData.result = ssgiRaw;

                    builder.UseTexture(passData.frontColor); 
                    builder.UseTexture(passData.frontPacked);
                    builder.UseTexture(passData.frontExtra);
                    builder.UseTexture(passData.backPacked);
                    builder.UseTexture(passData.sceneColor);
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    // Use Scaled Res Params
                    passData.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);
                    passData.ssgiSampleCount = _settings.ssgiSampleCount;
                    passData.ssgiRayStepSize = _settings.ssgiRayStepSize;
                    passData.worldToCameraMatrix = cameraData.GetViewMatrix();
                    passData.cameraToWorldMatrix = cameraData.GetViewMatrix().inverse;
                    passData.projectionMatrix = cameraData.GetProjectionMatrix();
                    passData.inverseProjectionMatrix = cameraData.GetProjectionMatrix().inverse;

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Color", data.frontColor); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSGI", data.result);

                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                        context.cmd.SetComputeIntParam(data.compute, "_SSGI_SampleCount", data.ssgiSampleCount);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSGI_RayStepSize", data.ssgiRayStepSize);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }

                // 7.6 SSGI Blur Pass
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface SSGI Blur Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CS_SSGI_Blur");

                    passData.frontPacked = frontPacked; // Depth
                    passData.ssgiTexture = ssgiRaw; // Source SSGI
                    passData.result = ssgiBlurred; // Final SSGI

                    builder.UseTexture(passData.frontPacked);
                    builder.UseTexture(passData.ssgiTexture);
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    // Use Scaled Res Params
                    passData.screenParams = new Vector4(scaledDesc.width, scaledDesc.height, 1.0f / scaledDesc.width, 1.0f / scaledDesc.height);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSGI_Raw", data.ssgiTexture);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result_SSGI", data.result);
                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);

                        int gx = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int gy = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, gx, gy, 1);
                    });
                }
            }

            // 8. Lighting Pass (Disney BRDF)
            TextureHandle lightingResult = resourceData.activeColorTexture; // Fallback
            if (_settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface Lighting Pass", out var passData))
                {
                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _settings.lightingComputeShader.FindKernel("CSMain");

                    var resultDesc = cameraData.cameraTargetDescriptor;
                    resultDesc.depthBufferBits = 0; 
                    resultDesc.enableRandomWrite = true;
                    resultDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                    RenderingUtils.ReAllocateHandleIfNeeded(ref _resultHandle, resultDesc, name: "_LNSurface_LightingResult");

                    passData.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    passData.worldToCameraMatrix = cameraData.camera.worldToCameraMatrix;
                    passData.projectionMatrix = GL.GetGPUProjectionMatrix(cameraData.camera.projectionMatrix, false);
                    passData.inverseProjectionMatrix = passData.projectionMatrix.inverse;
                    passData.screenParams = new Vector4(resultDesc.width, resultDesc.height, 1.0f / resultDesc.width, 1.0f / resultDesc.height);

                    // SSR Params
                    passData.enableSSR = _settings.enableSSR ? 1.0f : 0.0f;
                    passData.ssrMaxSteps = _settings.ssrMaxSteps;
                    passData.ssrStepSize = _settings.ssrStepSize;
                    passData.ssrThickness = _settings.ssrThickness;

                    // Main Light
                    Vector4 mainLightDir = new Vector4(0, 1, 0, 0);
                    Vector4 mainLightCol = Vector4.zero;

                    if (lightData.mainLightIndex != -1)
                    {
                        var light = lightData.visibleLights[lightData.mainLightIndex];
                        mainLightDir = -light.localToWorldMatrix.GetColumn(2);
                        mainLightCol = light.finalColor;
                    }

                    passData.mainLightDirection = mainLightDir;
                    passData.mainLightColor = mainLightCol;
                    
                    if (_settings.enableSSAO)
                    {
                        passData.ssaoTexture = aoBlurred; 
                    }

                    if (_settings.enableSSR)
                    {
                        passData.ssrTexture = ssrBlurred;
                    }

                    // SSGI Texture Binding Logic
                    if (_settings.enableSSGI)
                    {
                        passData.ssgiTexture = ssgiBlurred;
                    }
                    else
                    {
                        // Bind dummy texture to prevent "Property not set" error
                        passData.ssgiTexture = frontColor; // Any valid texture
                    }

                    passData.enableSSR = _settings.enableSSR ? 1.0f : 0.0f;
                    passData.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;
                    passData.enableSSGI = _settings.enableSSGI ? 1.0f : 0.0f;
                    passData.ssgiIntensity = _settings.ssgiIntensity;

                    // Additional Lights
                    passData.additionalLightPositions = new Vector4[16];
                    passData.additionalLightColors = new Vector4[16];
                    passData.additionalLightAttenuations = new Vector4[16];

                    int addCount = 0;
                    for (int i = 0; i < lightData.visibleLights.Length && addCount < 16; i++)
                    {
                        if (i == lightData.mainLightIndex)
                            continue;

                        var light = lightData.visibleLights[i];
                        passData.additionalLightPositions[addCount] = light.localToWorldMatrix.GetColumn(3);
                        passData.additionalLightColors[addCount] = light.finalColor;
                        
                        float rangeSq = light.range * light.range;
                        passData.additionalLightAttenuations[addCount] = new Vector4(1.0f / Mathf.Max(rangeSq, 0.0001f), 1.0f, 0, 0);
                        
                        addCount++;
                    }
                    passData.additionalLightCount = addCount;
                    // passData.intensity = _settings.intensity; // Removed
                    // passData.thicknessScale = _settings.thicknessScale; // Removed
                    passData.enableSSS = _settings.enableSSS ? 1.0f : 0.0f; 
                    passData.lightingModel = (int)_settings.lightingModel;

                    // SSAO Init
                    passData.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;
                    passData.ssaoRadius = _settings.ssaoRadius;
                    passData.ssaoIntensity = _settings.ssaoIntensity;
                    passData.ssaoSampleCount = _settings.ssaoSampleCount;

                    passData.frontColor = frontColor;
                    passData.frontPacked = frontPacked; 
                    passData.backPacked = backPacked; 
                    passData.backColor = backColor;
                    passData.sceneColor = resourceData.activeColorTexture; 
                    passData.frontExtra = frontExtra;
                    passData.frontExtra2 = frontExtra2; // Added
                    
                    passData.result = renderGraph.ImportTexture(_resultHandle);
                    lightingResult = passData.result;
                    
                    // SH Data Extraction
                    SphericalHarmonicsL2 sh = RenderSettings.ambientProbe;
                    passData.shAr = new Vector4[1]; passData.shAg = new Vector4[1]; passData.shAb = new Vector4[1];
                    passData.shBr = new Vector4[1]; passData.shBg = new Vector4[1]; passData.shBb = new Vector4[1];
                    passData.shC = new Vector4[1];
                    
                    // L0, L1
                    passData.shAr[0] = new Vector4(sh[0, 3], sh[0, 1], sh[0, 2], sh[0, 0] - sh[0, 6]);
                    passData.shAg[0] = new Vector4(sh[1, 3], sh[1, 1], sh[1, 2], sh[1, 0] - sh[1, 6]);
                    passData.shAb[0] = new Vector4(sh[2, 3], sh[2, 1], sh[2, 2], sh[2, 0] - sh[2, 6]);
                    
                    // L2
                    passData.shBr[0] = new Vector4(sh[0, 4], sh[0, 6], sh[0, 5] * 3.0f, sh[0, 7]);
                    passData.shBg[0] = new Vector4(sh[1, 4], sh[1, 6], sh[1, 5] * 3.0f, sh[1, 7]);
                    passData.shBb[0] = new Vector4(sh[2, 4], sh[2, 6], sh[2, 5] * 3.0f, sh[2, 7]);
                    
                    passData.shC[0] = new Vector4(sh[0, 8], sh[2, 8], sh[1, 8], 1.0f);

                    builder.UseTexture(passData.frontColor);
                    builder.UseTexture(passData.frontPacked); 
                    builder.UseTexture(passData.backPacked); 
                    builder.UseTexture(passData.backColor);
                    builder.UseTexture(passData.frontExtra);
                    builder.UseTexture(passData.frontExtra2); // Added
                    builder.UseTexture(passData.sceneColor); 
                    if (_settings.enableSSAO)
                    {
                        builder.UseTexture(passData.ssaoTexture);
                    }
                    if (_settings.enableSSR)
                    {
                        builder.UseTexture(passData.ssrTexture);
                    }
                    // Always use SSGI texture (real or dummy) to prevent binding error
                    builder.UseTexture(passData.ssgiTexture);
                    
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Color", data.frontColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Color", data.backColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra2", data.frontExtra2); // Added
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result", data.result);

                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightDirection", data.mainLightDirection);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightColor", data.mainLightColor);
                        
                        // Additional Lights
                        context.cmd.SetComputeIntParam(data.compute, "_AdditionalLightCount", data.additionalLightCount);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_AdditionalLightPositions", data.additionalLightPositions);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_AdditionalLightColors", data.additionalLightColors);
                        context.cmd.SetComputeVectorArrayParam(data.compute, "_AdditionalLightAttenuations", data.additionalLightAttenuations);
                        
                        // SSS Params
                        // context.cmd.SetComputeFloatParam(data.compute, "_SSS_Intensity", data.intensity); // Removed
                        // context.cmd.SetComputeFloatParam(data.compute, "_SSS_ThicknessScale", data.thicknessScale); // Removed
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSS", data.enableSSS);
                        context.cmd.SetComputeIntParam(data.compute, "_LightingModel", data.lightingModel);
                        
                        // SH Params
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHAr", data.shAr[0]);
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHAg", data.shAg[0]);
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHAb", data.shAb[0]);
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHBr", data.shBr[0]);
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHBg", data.shBg[0]);
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHBb", data.shBb[0]);
                        context.cmd.SetComputeVectorParam(data.compute, "unity_SHC", data.shC[0]);
                        
                        // Matrix params
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_WorldToCameraMatrix", data.worldToCameraMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_ProjectionMatrix", data.projectionMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);

                        // SSR Params
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSR", data.enableSSR);
                        context.cmd.SetComputeIntParam(data.compute, "_SSR_MaxSteps", data.ssrMaxSteps);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSR_StepSize", data.ssrStepSize);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSR_Thickness", data.ssrThickness);
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSAO", data.enableSSAO);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSAO_Intensity", data.ssaoIntensity);
                        context.cmd.SetComputeIntParam(data.compute, "_SSAO_SampleCount", data.ssaoSampleCount);

                        // SSGI Params
                        context.cmd.SetComputeFloatParam(data.compute, "_EnableSSGI", data.enableSSGI);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSGI_Intensity", data.ssgiIntensity);
                        
                        // Bind SSAO & SSR & SSGI Textures
                        if (data.enableSSAO > 0.5f)
                        {
                            context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_AO_Blurred", data.ssaoTexture);
                        }

                        if (data.enableSSR > 0.5f)
                        {
                            context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_SSR_Blurred", data.ssrTexture);
                        }

                        // Always bind SSGI texture (real or dummy)
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_SSGI_Blurred", data.ssgiTexture);

                        int groupsX = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                        int groupsY = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                        context.cmd.DispatchCompute(data.compute, data.kernel, groupsX, groupsY, 1);
                    });
                }
            }

            // 6. Blit Final Result
            TextureHandle finalResult = lightingResult;
            RenderGraphUtils.BlitMaterialParameters blitParams = new(finalResult, resourceData.activeColorTexture, 
                Blitter.GetBlitMaterial(TextureDimension.Tex2D), 0);
            renderGraph.AddBlitPass(blitParams, "LNSurface Final Blit");
        }

        private void RenderGBufferFront(RenderGraph renderGraph, ContextContainer frameData, string name, string lightMode, TextureHandle color, TextureHandle packed, TextureHandle extra, TextureHandle extra2)
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
            
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"LNSurface GBuffer {name} Pass", out var passData))
            {
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachment(packed, 1);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);
                
                passData.packedTexture = packed;

                DrawingSettings drawSettings = new DrawingSettings(new ShaderTagId(lightMode), new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque });
                
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                
                builder.UseRendererList(passData.rendererList);
                // builder.UseTexture(passData.packedTexture, AccessFlags.Write); // Removed to fix ArgumentException

                builder.SetRenderFunc((GBufferPassData data, RasterGraphContext context) =>
                {
                    // Clear Back Packed Texture with High Depth Value (e.g., 1000 in Blue channel)
                    // R: Normal X, G: Normal Y, B: Depth, A: Unused
                    context.cmd.ClearRenderTarget(false, true, new Color(0, 0, 1000.0f, 0)); 

                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}
