using UnityEngine.Rendering;
using System;

namespace UnityEngine.Experimental.Rendering.HDPipeline
{
    [Serializable]
    public enum SkyResolution
    {
        SkyResolution128 = 128,
        SkyResolution256 = 256,
        SkyResolution512 = 512,
        SkyResolution1024 = 1024,
        // TODO: Anything above 1024 cause a crash in Unity...
        //SkyResolution2048 = 2048,
        //SkyResolution4096 = 4096
    }

    [GenerateHLSL(PackingRules.Exact)]
    public enum LightSamplingParameters
    {
        TextureHeight = 256,
        TextureWidth  = 512
    }

    public enum EnvironementUpdateMode
    {
        OnChanged = 0,
        OnDemand,
        Realtime
    }

    public class BuiltinSkyParameters
    {
        public Matrix4x4                pixelCoordToViewDirMatrix;
        public Matrix4x4                invViewProjMatrix;
        public Vector3                  cameraPosWS;
        public Vector4                  screenSize;
        public CommandBuffer            commandBuffer;
        public Light                    sunLight;
        public RenderTargetIdentifier   colorBuffer;
        public RenderTargetIdentifier   depthBuffer;

        public static RenderTargetIdentifier nullRT = -1;
    }

    public class SkyManager
    {
        RenderTexture           m_SkyboxCubemapRT = null;
        RenderTexture           m_SkyboxGGXCubemapRT = null;
        RenderTexture           m_SkyboxMarginalRowCdfRT = null;
        RenderTexture           m_SkyboxConditionalCdfRT = null;

        Material                m_StandardSkyboxMaterial = null; // This is the Unity standard skybox material. Used to pass the correct cubemap to Enlighten.
        Material                m_BlitCubemapMaterial = null;

        IBLFilterGGX            m_iblFilterGgx = null;

        Vector4                 m_CubemapScreenSize;
        Matrix4x4[]             m_faceWorldToViewMatrixMatrices     = new Matrix4x4[6];
        Matrix4x4[]             m_facePixelCoordToViewDirMatrices   = new Matrix4x4[6];
        Matrix4x4[]             m_faceCameraInvViewProjectionMatrix = new Matrix4x4[6];

        BuiltinSkyParameters    m_BuiltinParameters = new BuiltinSkyParameters();
        SkyRenderer             m_Renderer = null;
        int                     m_SkyParametersHash = -1;
        bool                    m_NeedLowLevelUpdateEnvironment = false;
        int                     m_UpdatedFramesRequired = 2; // The first frame after the scene load is currently not rendered correctly
        float                   m_CurrentUpdateTime = 0.0f;

        bool                    m_useMIS = false;


        private SkySettings m_SkySettings;
        public SkySettings skySettings
        {
            set
            {
                if (m_SkySettings == value)
                    return;

                if (m_Renderer != null)
                {
                    m_Renderer.Cleanup();
                    m_Renderer = null;
                }

                m_SkyParametersHash = -1;
                m_SkySettings = value;
                m_UpdatedFramesRequired = 2;

                if (value != null)
                {
                    m_Renderer = value.GetRenderer();
                    m_Renderer.Build();
                }
            }
            get { return m_SkySettings; }
        }

        public Texture skyReflection { get { return m_SkyboxGGXCubemapRT; } }

        void RebuildTextures(SkySettings skySettings)
        {
            int resolution = 256;
            // Parameters not set yet. We need them for the resolution.
            if (skySettings != null)
                resolution = (int)skySettings.resolution;

            if ((m_SkyboxCubemapRT != null) && (m_SkyboxCubemapRT.width != resolution))
            {
                Utilities.Destroy(m_SkyboxCubemapRT);
                Utilities.Destroy(m_SkyboxGGXCubemapRT);
                Utilities.Destroy(m_SkyboxMarginalRowCdfRT);
                Utilities.Destroy(m_SkyboxConditionalCdfRT);

                m_SkyboxCubemapRT = null;
                m_SkyboxGGXCubemapRT = null;
                m_SkyboxMarginalRowCdfRT = null;
                m_SkyboxConditionalCdfRT = null;
            }

            if (m_SkyboxCubemapRT == null)
            {
                m_SkyboxCubemapRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                m_SkyboxCubemapRT.dimension = TextureDimension.Cube;
                m_SkyboxCubemapRT.useMipMap = true;
                m_SkyboxCubemapRT.autoGenerateMips = false; // We will generate regular mipmap for filtered importance sampling manually
                m_SkyboxCubemapRT.filterMode = FilterMode.Trilinear;
                m_SkyboxCubemapRT.Create();

                m_SkyboxGGXCubemapRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
                m_SkyboxGGXCubemapRT.dimension = TextureDimension.Cube;
                m_SkyboxGGXCubemapRT.useMipMap = true;
                m_SkyboxGGXCubemapRT.autoGenerateMips = false;
                m_SkyboxGGXCubemapRT.filterMode = FilterMode.Trilinear;
                m_SkyboxGGXCubemapRT.Create();

                if (m_useMIS)
                {
                    int width  = (int)LightSamplingParameters.TextureWidth;
                    int height = (int)LightSamplingParameters.TextureHeight;

                    // + 1 because we store the value of the integral of the cubemap at the end of the texture.
                    m_SkyboxMarginalRowCdfRT = new RenderTexture(height + 1, 1, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
                    m_SkyboxMarginalRowCdfRT.useMipMap = false;
                    m_SkyboxMarginalRowCdfRT.autoGenerateMips = false;
                    m_SkyboxMarginalRowCdfRT.enableRandomWrite = true;
                    m_SkyboxMarginalRowCdfRT.filterMode = FilterMode.Point;
                    m_SkyboxMarginalRowCdfRT.Create();

                    // TODO: switch the format to R16 (once it's available) to save some bandwidth.
                    m_SkyboxConditionalCdfRT = new RenderTexture(width, height, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear);
                    m_SkyboxConditionalCdfRT.useMipMap = false;
                    m_SkyboxConditionalCdfRT.autoGenerateMips = false;
                    m_SkyboxConditionalCdfRT.enableRandomWrite = true;
                    m_SkyboxConditionalCdfRT.filterMode = FilterMode.Point;
                    m_SkyboxConditionalCdfRT.Create();
                }

                m_UpdatedFramesRequired = 2; // Special case. Even if update mode is set to OnDemand, we need to regenerate the environment after destroying the texture.
            }

            m_CubemapScreenSize = new Vector4((float)resolution, (float)resolution, 1.0f / (float)resolution, 1.0f / (float)resolution);
        }

        void RebuildSkyMatrices(float nearPlane, float farPlane)
        {
            if (!m_SkySettings) return;

            Matrix4x4 cubeProj = Matrix4x4.Perspective(90.0f, 1.0f, nearPlane, farPlane);

            // Ref: https://msdn.microsoft.com/en-us/library/windows/desktop/bb204881(v=vs.85).aspx
            Vector3[] lookAtList =
            {
                new Vector3(1.0f, 0.0f, 0.0f),
                new Vector3(-1.0f, 0.0f, 0.0f),
                new Vector3(0.0f, 1.0f, 0.0f),
                new Vector3(0.0f, -1.0f, 0.0f),
                new Vector3(0.0f, 0.0f, 1.0f),
                new Vector3(0.0f, 0.0f, -1.0f),
            };

            Vector3[] upVectorList =
            {
                new Vector3(0.0f, 1.0f, 0.0f),
                new Vector3(0.0f, 1.0f, 0.0f),
                new Vector3(0.0f, 0.0f, -1.0f),
                new Vector3(0.0f, 0.0f, 1.0f),
                new Vector3(0.0f, 1.0f, 0.0f),
                new Vector3(0.0f, 1.0f, 0.0f),
            };

            for (int i = 0; i < 6; ++i)
            {
                Matrix4x4 lookAt      = Matrix4x4.LookAt(Vector3.zero, lookAtList[i], upVectorList[i]);
                Matrix4x4 worldToView = lookAt * Matrix4x4.Scale(new Vector3(1.0f, 1.0f, -1.0f)); // Need to scale -1.0 on Z to match what is being done in the camera.wolrdToCameraMatrix API. ...
                Vector4   screenSize  = new Vector4((int)m_SkySettings.resolution, (int)m_SkySettings.resolution, 1.0f / (int)m_SkySettings.resolution, 1.0f / (int)m_SkySettings.resolution);

                m_faceWorldToViewMatrixMatrices[i]     = worldToView;
                m_facePixelCoordToViewDirMatrices[i]   = Utilities.ComputePixelCoordToWorldSpaceViewDirectionMatrix(0.5f * Mathf.PI, screenSize, worldToView, true);
                m_faceCameraInvViewProjectionMatrix[i] = Utilities.GetViewProjectionMatrix(lookAt, cubeProj).inverse;
            }
        }

        // Sets the global MIP-mapped cubemap '_SkyTexture' in the shader.
        // The texture being set is the sky (environment) map pre-convolved with GGX.
        public void SetGlobalSkyTexture()
        {
            Shader.SetGlobalTexture("_SkyTexture", m_SkyboxGGXCubemapRT);
        }

        public void Resize(float nearPlane, float farPlane)
        {
            // When loading RenderDoc, RenderTextures will go null
            RebuildTextures(skySettings);
            RebuildSkyMatrices(nearPlane, farPlane);
        }

        public void Build(RenderPipelineResources renderPipelinesResources)
        {
            // Create unititialized. Lazy initialization is performed later.
            m_iblFilterGgx = new IBLFilterGGX(renderPipelinesResources);

            // TODO: We need to have an API to send our sky information to Enlighten. For now use a workaround through skybox/cubemap material...
            m_StandardSkyboxMaterial = Utilities.CreateEngineMaterial(renderPipelinesResources.skyboxCubemap);

            m_BlitCubemapMaterial = Utilities.CreateEngineMaterial(renderPipelinesResources.blitCubemap);

            m_CurrentUpdateTime = 0.0f;
        }

        public void Cleanup()
        {
            Utilities.Destroy(m_StandardSkyboxMaterial);
            Utilities.Destroy(m_SkyboxCubemapRT);
            Utilities.Destroy(m_SkyboxGGXCubemapRT);
            Utilities.Destroy(m_SkyboxMarginalRowCdfRT);
            Utilities.Destroy(m_SkyboxConditionalCdfRT);

            if (m_Renderer != null)
                m_Renderer.Cleanup();
        }

        public bool IsSkyValid()
        {
            return m_Renderer != null && m_Renderer.IsSkyValid();
        }

        private void RenderSkyToCubemap(BuiltinSkyParameters builtinParams, SkySettings skySettings, RenderTexture target)
        {
            for (int i = 0; i < 6; ++i)
            {
                builtinParams.pixelCoordToViewDirMatrix = m_facePixelCoordToViewDirMatrices[i];
                builtinParams.invViewProjMatrix = m_faceCameraInvViewProjectionMatrix[i];
                builtinParams.colorBuffer = target;
                builtinParams.depthBuffer = BuiltinSkyParameters.nullRT;

                Utilities.SetRenderTarget(builtinParams.commandBuffer, target, ClearFlag.ClearNone, 0, (CubemapFace)i);
                m_Renderer.RenderSky(builtinParams, skySettings, true);
            }

            // Generate mipmap for our cubemap
            Debug.Assert(target.autoGenerateMips == false);
            builtinParams.commandBuffer.GenerateMips(target);
        }

        private void BlitCubemap(CommandBuffer cmd, Cubemap source, RenderTexture dest)
        {

            MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();

            for (int i = 0; i < 6; ++i)
            {
                Utilities.SetRenderTarget(cmd, dest, ClearFlag.ClearNone, 0, (CubemapFace)i);
                propertyBlock.SetTexture("_MainTex", source);
                propertyBlock.SetFloat("_faceIndex", (float)i);
                cmd.DrawProcedural(Matrix4x4.identity, m_BlitCubemapMaterial, 0, MeshTopology.Triangles, 3, 1, propertyBlock);
            }

            // Generate mipmap for our cubemap
            Debug.Assert(dest.autoGenerateMips == false);
            cmd.GenerateMips(dest);
        }

        private void RenderCubemapGGXConvolution(CommandBuffer cmd, BuiltinSkyParameters builtinParams, SkySettings skyParams, Texture input, RenderTexture target)
        {
            using (new Utilities.ProfilingSample("Update Env: GGX Convolution", cmd))
            {
                int mipCount = 1 + (int)Mathf.Log(input.width, 2.0f);
                if (mipCount < ((int)EnvConstants.SpecCubeLodStep + 1))
                {
                    Debug.LogWarning("RenderCubemapGGXConvolution: Cubemap size is too small for GGX convolution, needs at least " + ((int)EnvConstants.SpecCubeLodStep + 1) + " mip levels");
                    return;
                }

                if (!m_iblFilterGgx.IsInitialized())
                {
                    m_iblFilterGgx.Initialize(cmd);
                }

                // Copy the first mip
                using (new Utilities.ProfilingSample("Copy Original Mip", cmd))
                {
                    for (int f = 0; f < 6; f++)
                    {
                        cmd.CopyTexture(input, f, 0, target, f, 0);
                    }
                }

                using (new Utilities.ProfilingSample("GGX Convolution", cmd))
                {
                    if (m_useMIS && m_iblFilterGgx.SupportMIS)
                    {
                        m_iblFilterGgx.FilterCubemapMIS(cmd, input, target, mipCount, m_SkyboxConditionalCdfRT, m_SkyboxMarginalRowCdfRT, m_faceWorldToViewMatrixMatrices);
                    }
                    else
                    {
                        m_iblFilterGgx.FilterCubemap(cmd, input, target, mipCount, m_faceWorldToViewMatrixMatrices);
                    }
                }
            }
        }

        public void RequestEnvironmentUpdate()
        {
            m_UpdatedFramesRequired = Math.Max(m_UpdatedFramesRequired, 1);
        }

        public void UpdateEnvironment(HDCamera camera, Light sunLight, CommandBuffer cmd)
        {
            // We need one frame delay for this update to work since DynamicGI.UpdateEnvironment is executed directly but the renderloop is not (so we need to wait for the sky texture to be rendered first)
            if (m_NeedLowLevelUpdateEnvironment)
            {
                using (new Utilities.ProfilingSample("DynamicGI.UpdateEnvironment", cmd))
                {
                    // TODO: Properly send the cubemap to Enlighten. Currently workaround is to set the cubemap in a Skybox/cubemap material
                    m_StandardSkyboxMaterial.SetTexture("_Tex", m_SkyboxCubemapRT);
                    RenderSettings.skybox = m_StandardSkyboxMaterial; // Setup this material as the default to be use in RenderSettings
                    RenderSettings.ambientIntensity = 1.0f; // fix this to 1, this parameter should not exist!
                    RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox; // Force skybox for our HDRI
                    RenderSettings.reflectionIntensity = 1.0f;
                    RenderSettings.customReflection = null;
                    DynamicGI.UpdateEnvironment();

                    m_NeedLowLevelUpdateEnvironment = false;
                }
            }

            if (IsSkyValid())
            {
                m_CurrentUpdateTime += Time.deltaTime;

                m_BuiltinParameters.commandBuffer = cmd;
                m_BuiltinParameters.sunLight = sunLight;
                m_BuiltinParameters.screenSize = m_CubemapScreenSize;
                m_BuiltinParameters.cameraPosWS = camera.camera.transform.position;

                if (
                    m_UpdatedFramesRequired > 0 ||
                    (skySettings.updateMode == EnvironementUpdateMode.OnChanged && skySettings.GetHash() != m_SkyParametersHash) ||
                    (skySettings.updateMode == EnvironementUpdateMode.Realtime && m_CurrentUpdateTime > skySettings.updatePeriod)
                    )
                {
                    using (new Utilities.ProfilingSample("Sky Environment Pass", cmd))
                    {
                        using (new Utilities.ProfilingSample("Update Env: Generate Lighting Cubemap", cmd))
                        {
                            // Render sky into a cubemap - doesn't happen every frame, can be controlled
                            // Note that m_SkyboxCubemapRT is created with auto-generate mipmap, it mean that here we have also our mipmap correctly box filtered for importance sampling.
                            if(m_SkySettings.lightingOverride == null)
                                RenderSkyToCubemap(m_BuiltinParameters, skySettings, m_SkyboxCubemapRT);
                            // In case the user overrides the lighting, we already have a cubemap ready but we need to blit it anyway for potential resize and so that we can generate proper mipmaps for enlighten.
                            else
                                BlitCubemap(cmd, m_SkySettings.lightingOverride, m_SkyboxCubemapRT);
                        }

                        // Convolve downsampled cubemap
                        RenderCubemapGGXConvolution(cmd, m_BuiltinParameters, skySettings, m_SkyboxCubemapRT, m_SkyboxGGXCubemapRT);

                        m_NeedLowLevelUpdateEnvironment = true;
                        m_UpdatedFramesRequired--;
                        m_SkyParametersHash = skySettings.GetHash();
                        m_CurrentUpdateTime = 0.0f;
                        #if UNITY_EDITOR
                        // In the editor when we change the sky we want to make the GI dirty so when baking again the new sky is taken into account.
                        // Changing the hash of the rendertarget allow to say that GI is dirty
                        m_SkyboxCubemapRT.imageContentsHash = new Hash128((uint)skySettings.GetHash(), 0, 0, 0);
                        #endif
                    }
                }
            }
            else
            {
                if (m_SkyParametersHash != 0)
                {
                    using (new Utilities.ProfilingSample("Reset Sky Environment", cmd))
                    {
                        // Clear temp cubemap and redo GGX from black and then feed it to enlighten for default light probe.
                        Utilities.ClearCubemap(cmd, m_SkyboxCubemapRT, Color.black);
                        RenderCubemapGGXConvolution(cmd, m_BuiltinParameters, skySettings, m_SkyboxCubemapRT, m_SkyboxGGXCubemapRT);

                        m_SkyParametersHash = 0;
                        m_NeedLowLevelUpdateEnvironment = true;
                    }
                }
            }
        }

        public void RenderSky(HDCamera camera, Light sunLight, RenderTargetIdentifier colorBuffer, RenderTargetIdentifier depthBuffer, CommandBuffer cmd)
        {
            using (new Utilities.ProfilingSample("Sky Pass", cmd))
            {
                if (IsSkyValid())
                {
                    m_BuiltinParameters.commandBuffer = cmd;
                    m_BuiltinParameters.sunLight = sunLight;
                    m_BuiltinParameters.pixelCoordToViewDirMatrix = Utilities.ComputePixelCoordToWorldSpaceViewDirectionMatrix(camera.camera.fieldOfView * Mathf.Deg2Rad, camera.screenSize, camera.viewMatrix, false);
                    m_BuiltinParameters.invViewProjMatrix = camera.viewProjMatrix.inverse;
                    m_BuiltinParameters.cameraPosWS = camera.camera.transform.position;
                    m_BuiltinParameters.colorBuffer = colorBuffer;
                    m_BuiltinParameters.depthBuffer = depthBuffer;

                    m_Renderer.SetRenderTargets(m_BuiltinParameters);
                    m_Renderer.RenderSky(m_BuiltinParameters, skySettings, false);
                }
            }
        }

        public Texture2D ExportSkyToTexture()
        {
            if(m_Renderer == null)
            {
                Debug.LogError("Cannot export sky to a texture, no SkyRenderer is setup.");
                return null;
            }

            if(m_SkySettings == null)
            {
                Debug.LogError("Cannot export sky to a texture, no Sky settings are setup.");
                return null;
            }

            int resolution = (int)m_SkySettings.resolution;

            RenderTexture tempRT = new RenderTexture(resolution * 6, resolution, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear);
            tempRT.dimension = TextureDimension.Tex2D;
            tempRT.useMipMap = false;
            tempRT.autoGenerateMips = false;
            tempRT.filterMode = FilterMode.Trilinear;
            tempRT.Create();

            Texture2D temp = new Texture2D(resolution * 6, resolution, TextureFormat.RGBAFloat, false);
            Texture2D result = new Texture2D(resolution * 6, resolution, TextureFormat.RGBAFloat, false);

            // Note: We need to invert in Y the cubemap faces because the current sky cubemap is inverted (because it's a RT)
            // So to invert it again so that it's a proper cubemap image we need to do it in several steps because ReadPixels does not have scale parameters:
            // - Convert the cubemap into a 2D texture
            // - Blit and invert it to a temporary target.
            // - Read this target again into the result texture.
            int offset = 0;
            for (int i = 0; i < 6; ++i)
            {
                Graphics.SetRenderTarget(m_SkyboxCubemapRT, 0, (CubemapFace)i);
                temp.ReadPixels(new Rect(0, 0, resolution, resolution), offset, 0);
                temp.Apply();
                offset += resolution;
            }

            // Flip texture.
            // Temporarily disabled until proper API reaches trunk
            Graphics.Blit(temp, tempRT, new Vector2(1.0f, -1.0f), new Vector2(0.0f, 0.0f));

            result.ReadPixels(new Rect(0, 0, resolution * 6, resolution), 0, 0);
            result.Apply();

            Graphics.SetRenderTarget(null);
            Object.DestroyImmediate(temp);
            Object.DestroyImmediate(tempRT);

            return result;
        }
    }
}
