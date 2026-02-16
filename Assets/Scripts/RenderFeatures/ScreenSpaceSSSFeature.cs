using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using System.Collections.Generic;

public class ScreenSpaceSSSFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class SSSSSettings
    {
        public LayerMask layerMask = -1;
        public ComputeShader computeShader;
        [Range(0f, 10f)] public float intensity = 1.0f;
        [Range(0f, 100f)] public float thicknessScale = 10.0f;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    public SSSSSettings settings = new SSSSSettings();
    private SSSSPass _pass;

    public override void Create()
    {
        _pass = new SSSSPass(settings);
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(_pass);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }

    private class SSSSPass : ScriptableRenderPass
    {
        private SSSSSettings _settings;
        private FilteringSettings _filteringSettings;
        private RTHandle _resultHandle;
        private int _kernelMain;

        public SSSSPass(SSSSSettings settings)
        {
            _settings = settings;
            renderPassEvent = settings.renderPassEvent;
            _filteringSettings = new FilteringSettings(RenderQueueRange.opaque, settings.layerMask);
            _resultHandle = RTHandles.Alloc("_SSSS_Result", name: "_SSSS_Result");
            if (settings.computeShader != null)
                _kernelMain = settings.computeShader.FindKernel("CSMain");
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
            internal TextureHandle sssMask; // Added
            internal TextureHandle result;
            internal float intensity;
            internal float thicknessScale;
            internal Vector2 screenParams;
            internal Vector4 mainLightDirection; 
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (_settings.computeShader == null) return;

            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalLightData lightData = frameData.Get<UniversalLightData>(); 

            var desc = cameraData.cameraTargetDescriptor;
            desc.depthBufferBits = 0; 
            desc.enableRandomWrite = false;

                // G-Buffer 텍스처 생성 (1. 컬러, 2. 노멀, 3. 뎁스)
                // 실제 SSSS 구현에서는 6개의 텍스처가 필요합니다 (Front 3개 + Back 3개).
                // 여기서는 MRT(Multiple Render Targets)를 사용하여 한 번에 4개의 텍스처를 출력합니다.
            TextureHandle frontColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "SSSS_Front_Color", false);
            TextureHandle frontNormal = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "SSSS_Front_Normal", false);
            TextureHandle frontDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "SSSS_Front_Depth", false);
            TextureHandle backColor = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "SSSS_Back_Color", false);
            TextureHandle backNormal = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "SSSS_Back_Normal", false);
            TextureHandle backDepth = UniversalRenderer.CreateRenderGraphTexture(renderGraph, desc, "SSSS_Back_Depth", false);
                        // 마스크 텍스처 생성
            var maskDesc = desc;
            maskDesc.graphicsFormat = UnityEngine.Experimental.Rendering.GraphicsFormat.R8_UNorm;
            TextureHandle sssMask = UniversalRenderer.CreateRenderGraphTexture(renderGraph, maskDesc, "SSSS_Mask", false);

                // 2. Front 패스 렌더링
            RenderGBuffer(renderGraph, frameData, "Front", "SSSS_Front", frontColor, frontNormal, frontDepth, sssMask);

                // 3. Back 패스 렌더링
                // Back 패스는 마스크가 필요 없지만, RenderGBuffer 함수 시그니처를 맞추기 위해 전달합니다.
                // (쉐이더에서 해당 타겟에 쓰기를 시도하므로 바인딩은 되어 있어야 함)
            RenderGBuffer(renderGraph, frameData, "Back", "SSSS_Back", backColor, backNormal, backDepth, sssMask);

                // 4. Compute 패스
            using (var builder = renderGraph.AddComputePass<ComputePassData>("SSSS Compute Pass", out var passData))
            {
                var resultDesc = cameraData.cameraTargetDescriptor;
                resultDesc.enableRandomWrite = true;
                resultDesc.depthBufferBits = 0;
                RenderingUtils.ReAllocateIfNeeded(ref _resultHandle, resultDesc);

                passData.compute = _settings.computeShader;
                passData.kernel = _kernelMain;
                passData.intensity = _settings.intensity;
                passData.thicknessScale = _settings.thicknessScale;
                passData.screenParams = new Vector2(resultDesc.width, resultDesc.height);
                                // 메인 라이트 방향 가져오기
                    Vector4 mainLightDir = Vector4.zero;
                    if (lightData.mainLightIndex != -1) 
                    {
                        VisibleLight mainLight = lightData.visibleLights[lightData.mainLightIndex]; 
                        Vector3 forward = mainLight.localToWorldMatrix.GetColumn(2);
                        mainLightDir = new Vector4(-forward.x, -forward.y, -forward.z, 0);
                    }
                    passData.mainLightDirection = mainLightDir;

                passData.frontColor = frontColor;
                passData.frontNormal = frontNormal; 
                passData.frontDepth = frontDepth;
                passData.backDepth = backDepth;
                passData.sceneColor = resourceData.activeColorTexture;                    passData.sssMask = sssMask; // 마스크 전달
                passData.result = renderGraph.ImportTexture(_resultHandle);

                builder.UseTexture(passData.frontColor);
                builder.UseTexture(passData.frontNormal); 
                builder.UseTexture(passData.frontDepth);
                builder.UseTexture(passData.backDepth);
                builder.UseTexture(passData.sceneColor);                    builder.UseTexture(passData.sssMask); // 마스크 사용
                builder.UseTexture(passData.result, AccessFlags.Write);

                builder.SetRenderFunc((ComputePassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSSS_Front_Color", data.frontColor);
                    context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSSS_Front_Normal", data.frontNormal); 
                    context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSSS_Front_Depth", data.frontDepth);
                    context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSSS_Back_Depth", data.backDepth);
                    context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSSS_Scene_Color", data.sceneColor);                        context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_SSSS_Mask", data.sssMask); // 마스크 바인딩
                    context.cmd.SetComputeTextureParam(data.compute, data.kernel, "_Result", data.result);

                    context.cmd.SetComputeFloatParam(data.compute, "_SSS_Intensity", data.intensity);
                    context.cmd.SetComputeFloatParam(data.compute, "_SSS_ThicknessScale", data.thicknessScale);
                    context.cmd.SetComputeVectorParam(data.compute, "_ScreenParams", data.screenParams);
                    context.cmd.SetComputeVectorParam(data.compute, "_MainLightDirection", data.mainLightDirection);

                    int groupsX = Mathf.CeilToInt(data.screenParams.x / 8.0f);
                    int groupsY = Mathf.CeilToInt(data.screenParams.y / 8.0f);
                    context.cmd.DispatchCompute(data.compute, data.kernel, groupsX, groupsY, 1);
                });
            }

                        // 5. Blit 패스 (최종 합성)
            TextureHandle finalResult = renderGraph.ImportTexture(_resultHandle);
            RenderGraphUtils.BlitMaterialParameters blitParams = new(finalResult, resourceData.activeColorTexture, 
                Blitter.GetBlitMaterial(TextureDimension.Tex2D), 0);
            renderGraph.AddBlitPass(blitParams, "SSSS Final Blit");
        }

        private void RenderGBuffer(RenderGraph renderGraph, ContextContainer frameData, string name, string lightMode, TextureHandle color, TextureHandle normal, TextureHandle depth, TextureHandle mask)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            
            using (var builder = renderGraph.AddRasterRenderPass<GBufferPassData>($"SSSS GBuffer {name} Pass", out var passData))
            {
                builder.SetRenderAttachment(color, 0);
                builder.SetRenderAttachment(normal, 1);
                builder.SetRenderAttachment(depth, 2);
                builder.SetRenderAttachment(mask, 3); // 마스크를 MRT 3번에 바인딩
                builder.SetRenderAttachmentDepth(resourceData.activeDepthTexture, AccessFlags.Write);

                DrawingSettings drawSettings = new DrawingSettings(new ShaderTagId(lightMode), new SortingSettings(cameraData.camera) { criteria = SortingCriteria.CommonOpaque });
                
                var rendererListParams = new RendererListParams(renderingData.cullResults, drawSettings, _filteringSettings);
                passData.rendererList = renderGraph.CreateRendererList(rendererListParams);
                
                builder.UseRendererList(passData.rendererList);

                builder.SetRenderFunc((GBufferPassData data, RasterGraphContext context) =>
                {
                    context.cmd.DrawRendererList(data.rendererList);
                });
            }
        }
    }
}
