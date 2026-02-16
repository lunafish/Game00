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
        }

        private class ComputePassData
        {
            internal ComputeShader compute;
            internal int kernel;
            internal TextureHandle frontColor;
            internal TextureHandle frontNormal; 
            internal TextureHandle frontDepth;
            internal TextureHandle backDepth;
            internal TextureHandle sceneColor; 
            internal TextureHandle mask; 
            internal TextureHandle extra; // GBuffer4: Metallic, SpecularStrength, Packed(Subsurface, Anisotropic)
            internal TextureHandle extra2; // GBuffer5: Smoothness
            internal TextureHandle result; // Input/Output
            
            internal float intensity;
            internal float thicknessScale;
            internal float enableSSS; 
            internal int lightingModel; // 0: HalfLambert, 1: DisneyBRDF
            internal Vector2 screenParams;
            internal Vector4 mainLightDirection;  
            internal Vector4 mainLightColor;
            internal Matrix4x4 cameraToWorldMatrix;
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
            // UniversalLightData lightData = frameData.Get<UniversalLightData>(); 

            var desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; 
            desc.enableRandomWrite = false;

            // 1. Create Textures
            TextureHandle frontColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Color", false);
            TextureHandle frontNormal = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Normal", false);
            TextureHandle frontDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Depth", false);
            TextureHandle backColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Color", false);
            TextureHandle backNormal = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Normal", false);
            TextureHandle backDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Depth", false);
            TextureHandle frontExtra = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Extra", false);
            TextureHandle backExtra = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Extra", false);
            TextureHandle frontExtra2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Front_Extra2", false);
            TextureHandle backExtra2 = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "LNSurface_Back_Extra2", false);
            
            // Mask Texture (ARGB32 for packed data)
            var maskDesc = desc;
            maskDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm; // ARGB32
            TextureHandle mask = UniversalRenderer.CreateRenderGraphTexture(renderGraph, maskDesc, "LNSurface_Mask", false);

            // 2. Front Pass
            RenderGBuffer(renderGraph, frameData, "Front", "LNSurface_Front", frontColor, frontNormal, frontDepth, mask, frontExtra, frontExtra2, true);

            // 3. Back Pass (Only if SSS is enabled)
            if (_settings.enableSSS)
            {
                RenderGBuffer(renderGraph, frameData, "Back", "LNSurface_Back", backColor, backNormal, backDepth, mask, backExtra, backExtra2, false);
            }
            else
            {
               // If disabled, we might want to clear backDepth or just leave it. 
               // For safety in shader reading, let's clear it if possible, but creating it is enough if we don't read garbage.
            }

            // 4. Lighting Pass (Disney BRDF)
            TextureHandle lightingResult = resourceData.activeColorTexture; // Fallback
            if (_settings.lightingComputeShader != null)
            {
                using (var builder = renderGraph.AddComputePass<ComputePassData>("LNSurface Lighting Pass", out var passData))
                {
                    var resultDesc = cameraData.cameraTargetDescriptor;
                    resultDesc.depthBufferBits = 0; // Fix: Disable depth buffer for Compute Shader output
                    resultDesc.enableRandomWrite = true;
                    // Fix: Ensure format supports Random Write (sRGB usually doesn't). Use FP16 or similar.
                    resultDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R16G16B16A16_SFloat;
                    RenderingUtils.ReAllocateIfNeeded(ref _resultHandle, resultDesc);

                    passData.compute = _settings.lightingComputeShader;
                    passData.kernel = _kernelLighting;
                    passData.screenParams = new Vector2(resultDesc.width, resultDesc.height);
                    passData.cameraToWorldMatrix = cameraData.camera.cameraToWorldMatrix;
                    passData.inverseProjectionMatrix = Matrix4x4.Inverse(cameraData.camera.projectionMatrix);

                    // Light Data - Dynamic
                    Light mainLight = RenderSettings.sun;
                    if (mainLight == null)
                    {
                        var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                        foreach (var light in lights)
                        {
                            if (light.type == LightType.Directional)
                            {
                                mainLight = light;
                                break;
                            }
                        }
                    }

                    Vector4 mainLightDir = new Vector4(0, 0, 1, 0);
                    Vector4 mainLightCol = Vector4.one;

                    if (mainLight != null)
                    {
                        // Direction TO light source (-forward)
                        mainLightDir = -mainLight.transform.forward;
                        mainLightCol = mainLight.color * mainLight.intensity;
                    }
                    
                    passData.mainLightDirection = mainLightDir;
                    passData.mainLightColor = mainLightCol;
                    passData.intensity = _settings.intensity;
                    passData.thicknessScale = _settings.thicknessScale;
                    passData.enableSSS = _settings.enableSSS ? 1.0f : 0.0f; 
                    passData.lightingModel = (int)_settings.lightingModel;

                    passData.frontColor = frontColor;
                    passData.frontNormal = frontNormal; 
                    passData.frontDepth = frontDepth;
                    passData.backDepth = backDepth; // Bind BackDepth
                    passData.mask = mask;
                    passData.extra = frontExtra;
                    passData.extra2 = frontExtra2;
                    passData.sceneColor = resourceData.activeColorTexture; // Bind Scene Color
                    
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
                    builder.UseTexture(passData.frontNormal); 
                    builder.UseTexture(passData.frontDepth);
                    builder.UseTexture(passData.backDepth); // Use BackDepth
                    builder.UseTexture(passData.mask);
                    builder.UseTexture(passData.extra);
                    builder.UseTexture(passData.extra2);
                    builder.UseTexture(passData.sceneColor); // Use Scene Color
                    builder.UseTexture(passData.result, AccessFlags.Write);

                    builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                    {
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Color", data.frontColor);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Normal", data.frontNormal); 
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Depth", data.frontDepth);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Back_Depth", data.backDepth); // Set BackDepth
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Mask", data.mask);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra", data.extra);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_LNSurface_Front_Extra2", data.extra2);
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SceneColor", data.sceneColor); // Set Scene Color
                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result", data.result);

                        context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightDirection", data.mainLightDirection);
                        context.cmd.SetComputeVectorParam(data.compute, "_MainLightColor", data.mainLightColor);
                        
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
                        
                        // Matrix params (Unused in shader currently but kept for safety/future)
                        context.cmd.SetComputeMatrixParam(data.compute, "_CameraToWorldMatrix", data.cameraToWorldMatrix);
                        context.cmd.SetComputeMatrixParam(data.compute, "_InverseProjectionMatrix", data.inverseProjectionMatrix);

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

        private void RenderGBuffer(RenderGraph renderGraph, ContextContainer frameData, string name, string lightMode, TextureHandle color, TextureHandle normal, TextureHandle depth, TextureHandle mask, TextureHandle extra, TextureHandle extra2, bool clear)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"LNSurface GBuffer {name} Pass", out var passData))
            {
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachment(normal, 1);
                builder.SetRenderAttachment(depth, 2);
                builder.SetRenderAttachment(mask, 3);
                builder.SetRenderAttachment(extra, 4);
                builder.SetRenderAttachment(extra2, 5);
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                if (clear)
                {
                    // builder.ClearGlobal... no.
                }

                DrawingSettings drawSettings = new DrawingSettings(new ShaderTagId(lightMode), new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque });
                
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((GBufferPassData data, RasterGraphContext context) =>
                {
                    if (clear) 
                    {
                        context.cmd.ClearRenderTarget(false, true, Color.clear); // Clear Colors
                    }
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}
