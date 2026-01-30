using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;

public class ChromaticAberrationFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class ChromaticAberrationSettings
    {
        public ComputeShader computeShader;
        [Range(0f, 0.01f)]
        public float intensity = 0.001f;
        public RenderPassEvent renderPassEvent = RenderPassEvent.AfterRenderingPostProcessing;
    }

    public ChromaticAberrationSettings settings = new ChromaticAberrationSettings();
    private ChromaticAberrationPass _pass;

    public override void Create()
    {
        _pass = new ChromaticAberrationPass(settings);
    }

    protected override void Dispose(bool disposing)
    {
        _pass?.Dispose();
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (settings.computeShader == null)
        {
            Debug.LogWarningFormat("Missing Compute Shader. {0} feature will not execute. Check for missing reference in the assigned renderer.", GetType().Name);
            return;
        }
        renderer.EnqueuePass(_pass);
    }

    private class ChromaticAberrationPass : ScriptableRenderPass
    {
        private ChromaticAberrationSettings _settings;
        private RTHandle ResultHandle;
        private ComputeShader Comp;
        private int KernelMain;

        public ChromaticAberrationPass(ChromaticAberrationSettings settings)
        {
            _settings = settings;
            renderPassEvent = _settings.renderPassEvent;
            ResultHandle = RTHandles.Alloc("Chromatic Aberration", name: "_ChromaticAberration");
            Comp = settings.computeShader;
            if (Comp != null)
                KernelMain = Comp.FindKernel("CSMain");
        }

        public void Dispose()
        {
            ResultHandle?.Release();
        }

        private class PassData
        {
            internal float intensity;
            internal Vector4 screenParams;
            internal TextureHandle source;
            internal TextureHandle destination;
            internal int dispatchGroupX;
            internal int dispatchGroupY;
        }
        
        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

            // 1. Compute Pass
            using (var builder = renderGraph.AddComputePass<PassData>("Chromatic Aberration Pass", out var passData))
            {
                var desc = cameraData.cameraTargetDescriptor;
                desc.depthStencilFormat = GraphicsFormat.None;
                desc.enableRandomWrite = true;
                RenderingUtils.ReAllocateIfNeeded(ref ResultHandle, desc, FilterMode.Point);
                passData.destination = renderGraph.ImportTexture(ResultHandle);
                passData.source = resourceData.cameraColor;
                passData.intensity = _settings.intensity;
                
                int width = desc.width;
                int height = desc.height;
                passData.screenParams = new Vector4(width, height, 0, 0);
                passData.dispatchGroupX = Mathf.CeilToInt(width / 8.0f);
                passData.dispatchGroupY = Mathf.CeilToInt(height / 8.0f);

                builder.UseTexture(passData.source);
                builder.UseTexture(passData.destination);
                builder.AllowPassCulling(false);
                // builder.UseAllGlobalTextures(true);

                builder.SetRenderFunc((PassData data, ComputeGraphContext context) =>
                {
                    context.cmd.SetComputeTextureParam(Comp, KernelMain, "Source", data.source);
                    context.cmd.SetComputeTextureParam(Comp, KernelMain, "Result", data.destination);
                    
                    context.cmd.SetComputeFloatParam(Comp, "_Intensity", data.intensity);
                    context.cmd.SetComputeVectorParam(Comp, "_ScreenParams", data.screenParams);
                    context.cmd.DispatchCompute(Comp, KernelMain, data.dispatchGroupX, data.dispatchGroupY, 1);
                });
            }
            
            TextureHandle Result = renderGraph.ImportTexture( ResultHandle );
            RenderGraphUtils.BlitMaterialParameters param = new(Result, resourceData.activeColorTexture,
                Blitter.GetBlitMaterial(TextureDimension.Tex2D), 0);
            renderGraph.AddBlitPass(param, "Chromatic Aberration Blit Pass");
        }
    }
}