using System;
using UnityEngine.Rendering;

#if UNITY_EDITOR
    using UnityEditor;
#endif

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
[GenerateHLSL]
public struct VolumeProperties
{
    public Vector3 scattering; // [0, 1], prefer sRGB
    public float   extinction; // [0, 1], prefer sRGB
    public float   asymmetry;  // Global (scene) property
    public float   align16_0;
    public float   align16_1;
    public float   align16_2;

    public static VolumeProperties GetNeutralVolumeProperties()
    {
        VolumeProperties properties = new VolumeProperties();

        properties.scattering = Vector3.zero;
        properties.extinction = 0;
        properties.asymmetry  = 0;

        return properties;
    }
} // struct VolumeProperties

[Serializable]
public class VolumeParameters
{
    public Bounds bounds;       // Position and dimensions in meters
    public Color  albedo;       // Single scattering albedo [0, 1]
    public float  meanFreePath; // In meters [1, inf]. Should be chromatic - this is an optimization!
    public float  anisotropy;   // [-1, 1]; 0 = isotropic

    public VolumeParameters()
    {
        bounds       = new Bounds(Vector3.zero, Vector3.positiveInfinity);
        albedo       = new Color(0.5f, 0.5f, 0.5f);
        meanFreePath = 10.0f;
        anisotropy   = 0.0f;
    }

    public bool IsVolumeUnbounded()
    {
        return bounds.size.x == float.PositiveInfinity &&
               bounds.size.y == float.PositiveInfinity &&
               bounds.size.z == float.PositiveInfinity;
    }

    public Vector3 GetAbsorptionCoefficient()
    {
        float   extinction = GetExtinctionCoefficient();
        Vector3 scattering = GetScatteringCoefficient();

        return Vector3.Max(new Vector3(extinction, extinction, extinction) - scattering, Vector3.zero);
    }

    public Vector3 GetScatteringCoefficient()
    {
        float extinction = GetExtinctionCoefficient();

        return new Vector3(albedo.r * extinction, albedo.g * extinction, albedo.b * extinction);
    }

    public float GetExtinctionCoefficient()
    {
        return 1.0f / meanFreePath;
    }

    public void Constrain()
    {
        bounds.size = Vector3.Max(bounds.size, Vector3.zero);

        albedo.r = Mathf.Clamp01(albedo.r);
        albedo.g = Mathf.Clamp01(albedo.g);
        albedo.b = Mathf.Clamp01(albedo.b);

        meanFreePath = Mathf.Max(meanFreePath, 1.0f);

        anisotropy = Mathf.Clamp(anisotropy, -1.0f, 1.0f);
    }

    public VolumeProperties GetProperties()
    {
        VolumeProperties properties = new VolumeProperties();

        properties.scattering = GetScatteringCoefficient();
        properties.extinction = GetExtinctionCoefficient();
        properties.asymmetry  = anisotropy;

        return properties;
    }
} // class VolumeParameters

public partial class HDRenderPipeline : RenderPipeline
{
    bool  m_VolumetricLightingEnabled   = false;
    int   m_VolumetricBufferTileSize    = 4;     // In pixels, must be a power of 2
    float m_VolumetricBufferMaxFarPlane = 64.0f; // Distance in meters

    RenderTexture          m_VolumetricLightingBufferCurrentFrame = null;
    RenderTexture          m_VolumetricLightingBufferAccumulation = null;
    RenderTargetIdentifier m_VolumetricLightingBufferCurrentFrameRT;
    RenderTargetIdentifier m_VolumetricLightingBufferAccumulationRT;

    ComputeShader m_VolumetricLightingCS { get { return m_Asset.renderPipelineResources.volumetricLightingCS; } }

    void ComputeVolumetricBufferResolution(int screenWidth, int screenHeight, ref int w, ref int h, ref int d)
    {
        int s = m_VolumetricBufferTileSize;
        Debug.Assert((s & (s - 1)) == 0, "m_VolumetricBufferTileSize must be a power of 2.");

        w = (screenWidth  + s - 1) / s;
        h = (screenHeight + s - 1) / s;
        d = 64;
    }

    void CreateVolumetricLightingBuffers(int width, int height)
    {
        if (m_VolumetricLightingBufferAccumulation != null)
        {
            m_VolumetricLightingBufferAccumulation.Release();
            m_VolumetricLightingBufferCurrentFrame.Release();
        }

        int w = 0, h = 0, d = 0;
        ComputeVolumetricBufferResolution(width, height, ref w, ref h, ref d);

        m_VolumetricLightingBufferCurrentFrame = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        m_VolumetricLightingBufferCurrentFrame.filterMode        = FilterMode.Bilinear;    // Custom trilinear
        m_VolumetricLightingBufferCurrentFrame.dimension         = TextureDimension.Tex3D; // Prefer 3D Thick tiling layout
        m_VolumetricLightingBufferCurrentFrame.volumeDepth       = d;
        m_VolumetricLightingBufferCurrentFrame.enableRandomWrite = true;
        m_VolumetricLightingBufferCurrentFrame.Create();
        m_VolumetricLightingBufferCurrentFrameRT = new RenderTargetIdentifier(m_VolumetricLightingBufferCurrentFrame);

        m_VolumetricLightingBufferAccumulation = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
        m_VolumetricLightingBufferAccumulation.filterMode        = FilterMode.Bilinear;    // Custom trilinear
        m_VolumetricLightingBufferAccumulation.dimension         = TextureDimension.Tex3D; // Prefer 3D Thick tiling layout
        m_VolumetricLightingBufferAccumulation.volumeDepth       = d;
        m_VolumetricLightingBufferAccumulation.enableRandomWrite = true;
        m_VolumetricLightingBufferAccumulation.Create();
        m_VolumetricLightingBufferAccumulationRT = new RenderTargetIdentifier(m_VolumetricLightingBufferAccumulation);
    }

    void ClearVolumetricLightingBuffers(CommandBuffer cmd, bool isFirstFrame)
    {
        using (new Utilities.ProfilingSample("Clear volumetric lighting buffers", cmd))
        {
            Utilities.SetRenderTarget(cmd, m_VolumetricLightingBufferCurrentFrameRT, ClearFlag.ClearColor, Color.black);

            if (isFirstFrame)
            {
                Utilities.SetRenderTarget(cmd, m_VolumetricLightingBufferAccumulation, ClearFlag.ClearColor, Color.black);
            }
        }
    }

    // Returns 'true' if the global fog is enabled, 'false' otherwise.
    public static bool SetGlobalVolumeProperties(bool volumetricLightingEnabled, CommandBuffer cmd, ComputeShader cs = null)
    {
        HomogeneousFog globalFogComponent = null;

        if (volumetricLightingEnabled)
        {
            HomogeneousFog[] fogComponents = Object.FindObjectsOfType(typeof(HomogeneousFog)) as HomogeneousFog[];

            foreach (HomogeneousFog fogComponent in fogComponents)
            {
                if (fogComponent.enabled && fogComponent.volumeParameters.IsVolumeUnbounded())
                {
                    globalFogComponent = fogComponent;
                    break;
                }
            }
        }

        // TODO: may want to cache these results somewhere.
        VolumeProperties globalFogProperties = (globalFogComponent != null) ? globalFogComponent.volumeParameters.GetProperties()
                                                                            : VolumeProperties.GetNeutralVolumeProperties();
        if (cs)
        {
            cmd.SetComputeVectorParam(cs, HDShaderIDs._GlobalFog_Scattering, globalFogProperties.scattering);
            cmd.SetComputeFloatParam( cs, HDShaderIDs._GlobalFog_Extinction, globalFogProperties.extinction);
            cmd.SetComputeFloatParam( cs, HDShaderIDs._GlobalFog_Asymmetry,  globalFogProperties.asymmetry);
        }
        else
        {
            cmd.SetGlobalVector(HDShaderIDs._GlobalFog_Scattering, globalFogProperties.scattering);
            cmd.SetGlobalFloat( HDShaderIDs._GlobalFog_Extinction, globalFogProperties.extinction);
            cmd.SetGlobalFloat( HDShaderIDs._GlobalFog_Asymmetry,  globalFogProperties.asymmetry);
        }

        return (globalFogComponent != null);
    }

    // The projection is not reversed (0 = near, 1 = far).
    // x = f/(f-n), y = (n*f)/(n-f), z = (n-f)/(n*f), w = 1/n.
    Vector4 ComputeVolumetricBufferProjectionParams(float cameraNearPlane, float cameraFarPlane)
    {
        float n = cameraNearPlane;
        float f = Math.Min(cameraFarPlane, m_VolumetricBufferMaxFarPlane);

        Vector4 projParams = new Vector4();

        projParams.x = f/(f-n);
        projParams.y = (n*f)/(n-f);
        projParams.z = (n-f)/(n*f);
        projParams.w = 1/n;

        return projParams;
    }

    void VolumetricLightingPass(CommandBuffer cmd, HDCamera camera)
    {
        if (!SetGlobalVolumeProperties(m_VolumetricLightingEnabled, cmd, m_VolumetricLightingCS)) { return; }

        using (new Utilities.ProfilingSample("VolumetricLighting", cmd))
        {
            bool enableClustered = m_Asset.tileSettings.enableClustered && m_Asset.tileSettings.enableTileAndCluster;

            int volumetricLightingKernel = m_VolumetricLightingCS.FindKernel(enableClustered ? "VolumetricLightingClustered"
                                                                                             : "VolumetricLightingAllLights");
            camera.SetupComputeShader(m_VolumetricLightingCS, cmd);

            // Compute custom near and far flipping planes of the volumetric lighting buffer.
            Vector4 projParams = ComputeVolumetricBufferProjectionParams(camera.camera.nearClipPlane, camera.camera.farClipPlane);

            // Compute dimensions of the buffer.
            int w = 0, h = 0, d = 0;
            ComputeVolumetricBufferResolution((int)camera.screenSize.x, (int)camera.screenSize.y, ref w, ref h, ref d);
            Vector4 dimensions = new Vector4(w, h, d, Mathf.Log(m_VolumetricBufferTileSize, 2));

            // Compose the matrix which allows us to compute the world space view direction.
            Matrix4x4 transform = Utilities.ComputePixelCoordToWorldSpaceViewDirectionMatrix(camera.camera.fieldOfView * Mathf.Deg2Rad, new Vector4(w, h, 1.0f / w, 1.0f / h), camera.viewMatrix, false);

            cmd.SetComputeVectorParam( m_VolumetricLightingCS, HDShaderIDs._vBufferDimensions,       dimensions);
            cmd.SetComputeVectorParam( m_VolumetricLightingCS, HDShaderIDs._vBufferProjParams,       projParams);
            cmd.SetComputeMatrixParam( m_VolumetricLightingCS, HDShaderIDs._vBufferCoordToViewDirWS, transform);
            cmd.SetComputeVectorParam( m_VolumetricLightingCS, HDShaderIDs._Time,                    Shader.GetGlobalVector(HDShaderIDs._Time));
            cmd.SetComputeTextureParam(m_VolumetricLightingCS, volumetricLightingKernel, HDShaderIDs._VolumetricLightingBufferCurrentFrame, m_VolumetricLightingBufferCurrentFrameRT);
            cmd.SetComputeTextureParam(m_VolumetricLightingCS, volumetricLightingKernel, HDShaderIDs._VolumetricLightingBufferAccumulation, m_VolumetricLightingBufferAccumulationRT);

            // Pass clustered light data (if present) to the compute shader.
            m_LightLoop.PushGlobalParams(camera.camera, cmd, m_VolumetricLightingCS, volumetricLightingKernel, true);
            cmd.SetComputeIntParam(m_VolumetricLightingCS, HDShaderIDs._UseTileLightList, 0);

            cmd.DispatchCompute(m_VolumetricLightingCS, volumetricLightingKernel, (w + 15) / 16, (h + 15) / 16, 1);
        }
    }
} // class HDRenderPipeline
} // namespace UnityEngine.Experimental.Rendering.HDPipeline
