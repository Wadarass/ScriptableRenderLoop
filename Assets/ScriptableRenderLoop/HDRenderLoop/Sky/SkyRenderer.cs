using UnityEngine.Rendering;
using UnityEngine.Experimental.Rendering;
using System.Collections.Generic;
using System;


namespace UnityEngine.Experimental.ScriptableRenderLoop
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

    [Serializable]
    public class SkyParameters
    {
        public Texture skyHDRI;
        public float rotation = 0.0f;
        public float exposure = 0.0f;
        public float multiplier = 1.0f;       
        public SkyResolution skyResolution = SkyResolution.SkyResolution256;
    }


    public class SkyRenderer
    {
        RenderTexture m_SkyboxCubemapRT = null;
        RenderTexture m_SkyboxGGXCubemapRT = null;

        Material m_StandardSkyboxMaterial = null; // This is the Unity standard skybox material. Used to pass the correct cubemap to Enlighten.
        Material m_SkyHDRIMaterial = null; // Renders a cubemap into a render texture (can be cube or 2D)
        Material m_GGXConvolveMaterial = null; // Apply GGX convolution to cubemap

        SkyParameters m_bakedSkyParameters = new SkyParameters(); // This is the SkyParam used when baking and convolving the sky.

        MaterialPropertyBlock m_RenderSkyPropertyBlock = null;

        GameObject[] m_CubemapFaceCamera = new GameObject[6];
        Mesh[] m_CubemapFaceMesh = new Mesh[6];

        Mesh BuildSkyMesh(Camera camera, bool forceUVBottom)
        {
            Vector4 vertData0 = new Vector4(-1.0f, -1.0f, 1.0f, 1.0f);
            Vector4 vertData1 = new Vector4(1.0f, -1.0f, 1.0f, 1.0f);
            Vector4 vertData2 = new Vector4(1.0f, 1.0f, 1.0f, 1.0f);
            Vector4 vertData3 = new Vector4(-1.0f, 1.0f, 1.0f, 1.0f);

            Vector3[] vertData = new Vector3[4];
            vertData[0] = new Vector3(vertData0.x, vertData0.y, vertData0.z);
            vertData[1] = new Vector3(vertData1.x, vertData1.y, vertData1.z);
            vertData[2] = new Vector3(vertData2.x, vertData2.y, vertData2.z);
            vertData[3] = new Vector3(vertData3.x, vertData3.y, vertData3.z);

            // Get view vector based on the frustum, i.e (invert transform frustum get position etc...)
            Vector3[] eyeVectorData = new Vector3[4];

            Matrix4x4 transformMatrix = camera.cameraToWorldMatrix * camera.projectionMatrix.inverse;

            Vector4 posWorldSpace0 = transformMatrix * vertData0;
            Vector4 posWorldSpace1 = transformMatrix * vertData1;
            Vector4 posWorldSpace2 = transformMatrix * vertData2;
            Vector4 posWorldSpace3 = transformMatrix * vertData3;

            Vector3 temp = camera.GetComponent<Transform>().position;
            Vector4 cameraPosition = new Vector4(temp.x, temp.y, temp.z, 0.0f);

            Vector4 direction0 = (posWorldSpace0 / posWorldSpace0.w - cameraPosition);
            Vector4 direction1 = (posWorldSpace1 / posWorldSpace1.w - cameraPosition);
            Vector4 direction2 = (posWorldSpace2 / posWorldSpace2.w - cameraPosition);
            Vector4 direction3 = (posWorldSpace3 / posWorldSpace3.w - cameraPosition);

            if (SystemInfo.graphicsUVStartsAtTop && !forceUVBottom)
            {
                eyeVectorData[3] = new Vector3(direction0.x, direction0.y, direction0.z).normalized;
                eyeVectorData[2] = new Vector3(direction1.x, direction1.y, direction1.z).normalized;
                eyeVectorData[1] = new Vector3(direction2.x, direction2.y, direction2.z).normalized;
                eyeVectorData[0] = new Vector3(direction3.x, direction3.y, direction3.z).normalized;
            }
            else
            {
                eyeVectorData[0] = new Vector3(direction0.x, direction0.y, direction0.z).normalized;
                eyeVectorData[1] = new Vector3(direction1.x, direction1.y, direction1.z).normalized;
                eyeVectorData[2] = new Vector3(direction2.x, direction2.y, direction2.z).normalized;
                eyeVectorData[3] = new Vector3(direction3.x, direction3.y, direction3.z).normalized;
            }

            // Write out the mesh
            var triangles = new int[6] { 0, 1, 2, 2, 3, 0 };

            return new Mesh
            {
                vertices = vertData,
                normals = eyeVectorData,
                triangles = triangles
            };
        }

        void RebuildTextures(SkyParameters skyParameters)
        {
            if ((m_SkyboxCubemapRT != null) && (m_SkyboxCubemapRT.width != (int)skyParameters.skyResolution))
            {
                Utilities.Destroy(m_SkyboxCubemapRT);
                Utilities.Destroy(m_SkyboxGGXCubemapRT);
            }
            
            if (m_SkyboxCubemapRT == null)
            {
                m_SkyboxCubemapRT = new RenderTexture((int)skyParameters.skyResolution, (int)skyParameters.skyResolution, 1, RenderTextureFormat.ARGBHalf);
                m_SkyboxCubemapRT.dimension = TextureDimension.Cube;
                m_SkyboxCubemapRT.useMipMap = true;
                m_SkyboxCubemapRT.autoGenerateMips = true; // Generate regular mipmap for filtered importance sampling
                m_SkyboxCubemapRT.filterMode = FilterMode.Trilinear;
                m_SkyboxCubemapRT.Create();

                m_SkyboxGGXCubemapRT = new RenderTexture((int)skyParameters.skyResolution, (int)skyParameters.skyResolution, 1, RenderTextureFormat.ARGBHalf);
                m_SkyboxGGXCubemapRT.dimension = TextureDimension.Cube;
                m_SkyboxGGXCubemapRT.useMipMap = true;
                m_SkyboxGGXCubemapRT.autoGenerateMips = false;
                m_SkyboxGGXCubemapRT.filterMode = FilterMode.Trilinear;
                m_SkyboxGGXCubemapRT.Create();
            }
        }

        // Sets the global MIP-mapped cubemap '_SkyTexture' in the shader.
        // The texture being set is the sky (environment) map pre-convolved with GGX.
        public void SetGlobalSkyTexture()
        {
            Shader.SetGlobalTexture("_SkyTexture", m_SkyboxGGXCubemapRT);
        }

        public void Resize(SkyParameters skyParameters)
        {
            // When loading RenderDoc, RenderTextures will go null
            RebuildTextures(skyParameters);
        }

        public void Rebuild()
        {
            // TODO: We need to have an API to send our sky information to Enlighten. For now use a workaround through skybox/cubemap material...
            m_StandardSkyboxMaterial = Utilities.CreateEngineMaterial("Skybox/Cubemap");

            m_SkyHDRIMaterial = Utilities.CreateEngineMaterial("Hidden/HDRenderLoop/SkyHDRI");
            m_GGXConvolveMaterial = Utilities.CreateEngineMaterial("Hidden/HDRenderLoop/GGXConvolve");

            m_RenderSkyPropertyBlock = new MaterialPropertyBlock();            

            Matrix4x4 cubeProj = Matrix4x4.Perspective(90.0f, 1.0f, 0.1f, 1.0f);

            Vector3[] lookAtList = {
                            new Vector3(1.0f, 0.0f, 0.0f),
                            new Vector3(-1.0f, 0.0f, 0.0f),
                            new Vector3(0.0f, 1.0f, 0.0f),
                            new Vector3(0.0f, -1.0f, 0.0f),
                            new Vector3(0.0f, 0.0f, 1.0f),
                            new Vector3(0.0f, 0.0f, -1.0f),
                        };

            Vector3[] UpVectorList = {
                            new Vector3(0.0f, 1.0f, 0.0f),
                            new Vector3(0.0f, 1.0f, 0.0f),
                            new Vector3(0.0f, 0.0f, -1.0f),
                            new Vector3(0.0f, 0.0f, 1.0f),
                            new Vector3(0.0f, 1.0f, 0.0f),
                            new Vector3(0.0f, 1.0f, 0.0f),
                        };

            for (int i = 0; i < 6; ++i)
            {
                m_CubemapFaceCamera[i] = new GameObject();
                m_CubemapFaceCamera[i].hideFlags = HideFlags.HideAndDontSave;

                Camera camera = m_CubemapFaceCamera[i].AddComponent<Camera>();
                camera.projectionMatrix = cubeProj;
                Transform transform = camera.GetComponent<Transform>();
                transform.LookAt(lookAtList[i], UpVectorList[i]);

                // When rendering into a texture the render will be flip (due to legacy unity openGL behavior), so we need to flip UV here...
                m_CubemapFaceMesh[i] = BuildSkyMesh(camera, true);
            }
        }

        public void Cleanup()
        {
            Utilities.Destroy(m_StandardSkyboxMaterial);
            Utilities.Destroy(m_SkyHDRIMaterial);
            Utilities.Destroy(m_GGXConvolveMaterial);
            Utilities.Destroy(m_SkyboxCubemapRT);
            Utilities.Destroy(m_SkyboxGGXCubemapRT);

            for(int i = 0 ; i < 6 ; ++i)
            {
                Utilities.Destroy(m_CubemapFaceCamera[i]);
            }

        }

        public bool IsSkyValid(SkyParameters parameters)
        {
            // Later we will also test shader for procedural skies.
            return parameters.skyHDRI != null;
        }

        private void RenderSky(Camera camera, SkyParameters skyParameters, Mesh skyMesh, RenderLoop renderLoop)
        {
            m_RenderSkyPropertyBlock.SetTexture("_Cubemap", skyParameters.skyHDRI);
            m_RenderSkyPropertyBlock.SetVector("_SkyParam", new Vector4(skyParameters.exposure, skyParameters.multiplier, skyParameters.rotation, 0.0f));
            m_RenderSkyPropertyBlock.SetMatrix("_InvViewProjMatrix", Utilities.GetViewProjectionMatrix(camera).inverse);

            var cmd = new CommandBuffer { name = "" };
            cmd.DrawMesh(skyMesh, Matrix4x4.identity, m_SkyHDRIMaterial, 0, 0, m_RenderSkyPropertyBlock);
            renderLoop.ExecuteCommandBuffer(cmd);
            cmd.Dispose();
        }

        private void RenderSkyToCubemap(SkyParameters skyParameters, RenderTexture target, RenderLoop renderLoop)
        {
            for (int i = 0; i < 6; ++i)
            {
                Utilities.SetRenderTarget(renderLoop, target, 0, (CubemapFace)i);
                Camera faceCamera = m_CubemapFaceCamera[i].GetComponent<Camera>();
                RenderSky(faceCamera, skyParameters, m_CubemapFaceMesh[i], renderLoop);
            }
        }

        private void RenderCubemapGGXConvolution(Texture input, RenderTexture target, RenderLoop renderLoop)
        {
            using (new Utilities.ProfilingSample("Sky Pass: GGX Convolution", renderLoop))
            {
                int mipCount = 1 + (int)Mathf.Log(input.width, 2.0f);
                if (mipCount < ((int)EnvConstants.SpecCubeLodStep + 1))
                {
                    Debug.LogWarning("RenderCubemapGGXConvolution: Cubemap size is too small for GGX convolution, needs at least " + ((int)EnvConstants.SpecCubeLodStep + 1) + " mip levels");
                    return;
                }

                // Copy the first mip.

                // TEMP code until CopyTexture is implemented for command buffer
                // All parameters are neutral because exposure/multiplier have already been applied in the first copy.
                SkyParameters skyParams = new SkyParameters();
                skyParams.exposure = 0.0f;
                skyParams.multiplier = 1.0f;
                skyParams.rotation = 0.0f;
                skyParams.skyHDRI = input;
                RenderSkyToCubemap(skyParams, target, renderLoop);
                // End temp

                //for (int f = 0; f < 6; f++)
                //    Graphics.CopyTexture(input, f, 0, target, f, 0);

                // Do the convolution on remaining mipmaps
                float invOmegaP = (6.0f * input.width * input.width) / (4.0f * Mathf.PI); // Solid angle associated to a pixel of the cubemap;

                m_GGXConvolveMaterial.SetTexture("_MainTex", input);
                m_GGXConvolveMaterial.SetFloat("_MipMapCount", mipCount);
                m_GGXConvolveMaterial.SetFloat("_InvOmegaP", invOmegaP);

                for (int mip = 1; mip < ((int)EnvConstants.SpecCubeLodStep + 1); ++mip)
                {
                    MaterialPropertyBlock propertyBlock = new MaterialPropertyBlock();
                    propertyBlock.SetFloat("_Level", mip);

                    for (int face = 0; face < 6; ++face)
                    {
                        Utilities.SetRenderTarget(renderLoop, target, mip, (CubemapFace)face);

                        var cmd = new CommandBuffer { name = "" };
                        cmd.DrawMesh(m_CubemapFaceMesh[face], Matrix4x4.identity, m_GGXConvolveMaterial, 0, 0, propertyBlock);
                        renderLoop.ExecuteCommandBuffer(cmd);
                        cmd.Dispose();
                    }
                }

            }
        }

        public void RenderSky(Camera camera, SkyParameters skyParameters, RenderTargetIdentifier colorBuffer, RenderTargetIdentifier depthBuffer, RenderLoop renderLoop)
        {
            using (new Utilities.ProfilingSample("Sky Pass", renderLoop))
            {
                if (IsSkyValid(skyParameters))
                {
                    // Trigger a rebuild of cubemap / convolution
                    // TODO: can we have some kind of hash value here ? +> use or override GetHashCode() + include a refresh rate value in parameters
                    // TODO: we could apply multiplier/exposure and rotation on the final result (i.e on the sky ibl and on lightprobe / lightmap, but can be tricky as Unity seems to merge sky information with
                    // other lighting into SH / lightmap.
                    if (skyParameters.skyResolution != m_bakedSkyParameters.skyResolution ||
                        skyParameters.exposure != m_bakedSkyParameters.exposure ||
                        skyParameters.rotation != m_bakedSkyParameters.rotation ||
                        skyParameters.multiplier != m_bakedSkyParameters.multiplier ||
                        skyParameters.skyHDRI != m_bakedSkyParameters.skyHDRI)
                    {
                        using (new Utilities.ProfilingSample("Sky Pass: Render Cubemap", renderLoop))
                        {
                            // Render sky into a cubemap - doesn't happen every frame, can be controlled
                            RenderSkyToCubemap(skyParameters, m_SkyboxCubemapRT, renderLoop);
                            // Convolve downsampled cubemap
                            RenderCubemapGGXConvolution(m_SkyboxCubemapRT, m_SkyboxGGXCubemapRT, renderLoop);

                            // TODO: Properly send the cubemap to Enlighten. Currently workaround is to set the cubemap in a Skybox/cubemap material
                            m_StandardSkyboxMaterial.SetTexture("_Tex", m_SkyboxCubemapRT);
                            RenderSettings.skybox = m_StandardSkyboxMaterial; // Setup this material as the default to be use in RenderSettings
                            RenderSettings.ambientIntensity = 1.0f; // fix this to 1, this parameter should not exist!
                            RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Skybox; // Force skybox for our HDRI
                            RenderSettings.reflectionIntensity = 1.0f;
                            RenderSettings.customReflection = null;
                            DynamicGI.UpdateEnvironment();
                        }

                        // Cleanup all this...
                        m_bakedSkyParameters.skyHDRI = skyParameters.skyHDRI;
                        m_bakedSkyParameters.skyResolution = skyParameters.skyResolution;
                        m_bakedSkyParameters.exposure = skyParameters.exposure;
                        m_bakedSkyParameters.rotation = skyParameters.rotation;
                        m_bakedSkyParameters.multiplier = skyParameters.multiplier;
                    }

                    // Render the sky itself
                    Utilities.SetRenderTarget(renderLoop, colorBuffer, depthBuffer);
                    RenderSky(camera, skyParameters, BuildSkyMesh(camera, false), renderLoop);
                }
            }
        }
    }
}
