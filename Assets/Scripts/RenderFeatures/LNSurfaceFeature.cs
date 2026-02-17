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

        public LayerMask layerMask = -1;
        public ComputeShader lightingComputeShader; // Disney BRDF Lighting
        public LightingModel lightingModel = LightingModel.DisneyBRDF; // Selection
        public bool enableSSS = true; 
        [Range(0f, 10f)] public float intensity = 1.0f;
        [Range(0f, 100f)] public float thicknessScale = 10.0f;

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
            internal TextureHandle frontExtra; // Mask(R), Metallic(G), Smoothness(B), Packed(A)
            
            internal TextureHandle backColor;
            internal TextureHandle backPacked; // Normal(RG) + Depth(B)
            
            internal TextureHandle sceneColor; 
            internal TextureHandle ssaoTexture; // SSAO Blurred Result
            internal TextureHandle ssrTexture; // SSR Blurred Result
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

            // 1. Create Textures
            // Front Pass Textures
            TextureHandle frontColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Color", false);
            
            var packedDesc = desc;
            packedDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat; // High Precision for Normal & Depth
            TextureHandle frontPacked = UniversalRenderer.CreateRenderGraphTexture(renderGraph, packedDesc, "LNSurface_Front_Packed", false);
            
            var extraDesc = desc;
            extraDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm;
            TextureHandle frontExtra = UniversalRenderer.CreateRenderGraphTexture(renderGraph, extraDesc, "LNSurface_Front_Extra", false);

            // Back Pass Textures
            TextureHandle backColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Color", false);
            TextureHandle backPacked = UniversalRenderer.CreateRenderGraphTexture(renderGraph, packedDesc, "LNSurface_Back_Packed", false);

            // SSAO Textures (R8 for performance)
            var aoDesc = desc;
            aoDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm;
            aoDesc.enableRandomWrite = true;
            TextureHandle aoRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoDesc, "LNSurface_AO_Raw", false);
            TextureHandle aoBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, aoDesc, "LNSurface_AO_Blurred", false);

            // SSR Textures (B10G11R11_UFloatPack32 for HDR color)
            var ssrDesc = desc;
            ssrDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.B10G11R11_UFloatPack32;
            ssrDesc.enableRandomWrite = true;
            TextureHandle ssrRaw = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Raw", false);
            TextureHandle ssrBlurred = UniversalRenderer.CreateRenderGraphTexture(renderGraph, ssrDesc, "LNSurface_SSR_Blurred", false);

            // 2. Front Pass
            RenderGBufferFront(renderGraph, frameData, "Front", "LNSurface_Front", frontColor, frontPacked, frontExtra);

            // 3. Back Pass (Only if SSS is enabled)
            if (_settings.enableSSS)
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
                    passData.screenParams = new Vector4(desc.width, desc.height, 1.0f / desc.width, 1.0f / desc.height);
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
                    
                    passData.screenParams = new Vector4(desc.width, desc.height, 1.0f / desc.width, 1.0f / desc.height);
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

                    passData.screenParams = new Vector4(desc.width, desc.height, 1.0f / desc.width, 1.0f / desc.height);
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

                    passData.screenParams = new Vector4(desc.width, desc.height, 1.0f / desc.width, 1.0f / desc.height);

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
                    passData.enableSSR = _settings.enableSSR ? 1.0f : 0.0f;
                    passData.enableSSAO = _settings.enableSSAO ? 1.0f : 0.0f;

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
                    passData.intensity = _settings.intensity;
                    passData.thicknessScale = _settings.thicknessScale;
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
                    builder.UseTexture(passData.sceneColor); 
                    if (_settings.enableSSAO)
                    {
                        builder.UseTexture(passData.ssaoTexture);
                    }
                    if (_settings.enableSSR)
                    {
                        builder.UseTexture(passData.ssrTexture);
                    }
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Color", data.frontColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Packed", data.frontPacked); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Packed", data.backPacked); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Color", data.backColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.frontExtra);
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
                        context.cmd.SetComputeFloatParam(data.compute, "_SSS_Intensity", data.intensity);
                        context.cmd.SetComputeFloatParam(data.compute, "_SSS_ThicknessScale", data.thicknessScale);
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
                        
                        // Bind SSAO & SSR Textures
                        if (data.enableSSAO > 0.5f)
                        {
                            context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_AO_Blurred", data.ssaoTexture);
                        }

                        if (data.enableSSR > 0.5f)
                        {
                            context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_SSR_Blurred", data.ssrTexture);
                        }

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

        private void RenderGBufferFront(RenderGraph renderGraph, ContextContainer frameData, string name, string lightMode, TextureHandle color, TextureHandle packed, TextureHandle extra)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"LNSurface GBuffer {name} Pass", out var passData))
            {
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachment(packed, 1);
                builder.SetRenderAttachment(extra, 2);
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
