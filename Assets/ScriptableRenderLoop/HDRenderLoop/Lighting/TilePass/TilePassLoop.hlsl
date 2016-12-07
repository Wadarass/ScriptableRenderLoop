//-----------------------------------------------------------------------------
// LightLoop
// ----------------------------------------------------------------------------

#ifdef LIGHTLOOP_TILE_PASS

// Calculate the offset in global light index light for current light category
int GetTileOffset(Coordinate coord, uint lightCategory)
{
    uint2 tileIndex = coord.unPositionSS / TILE_SIZE;
    return (tileIndex.y + lightCategory * _NumTileY) * _NumTileX + tileIndex.x;
}

void GetCountAndStartTile(Coordinate coord, uint lightCategory, float linearDepth, out uint start, out uint lightCount)
{
    const int tileOffset = GetTileOffset(coord, lightCategory);

    // The first entry inside a tile is the number of light for lightCategory (thus the +0)
    lightCount = g_vLightListGlobal[DWORD_PER_TILE * tileOffset + 0] & 0xffff;
    start = tileOffset;
}

uint FetchIndexTile(uint tileOffset, uint lightIndex)
{
    const uint lightIndexPlusOne = lightIndex + 1; // Add +1 as first slot is reserved to store number of light
    // Light index are store on 16bit
    return (g_vLightListGlobal[DWORD_PER_TILE * tileOffset + (lightIndexPlusOne >> 1)] >> ((lightIndexPlusOne & 1) * DWORD_PER_TILE)) & 0xffff;
}


#ifdef USE_FPTL_LIGHTLIST

void GetCountAndStart(Coordinate coord, uint lightCategory, float linearDepth, out uint start, out uint lightCount)
{
    GetCountAndStartTile(coord, lightCategory, linearDepth, start, lightCount);
}

uint FetchIndex(uint tileOffset, uint lightIndex)
{
    return FetchIndexTile(tileOffset, lightIndex);
}

#else

#include "ClusteredUtils.hlsl"

void GetCountAndStartCluster(Coordinate coord, uint lightCategory, float linearDepth, out uint start, out uint lightCount)
{
    uint2 tileIndex = coord.unPositionSS / TILE_SIZE;

    float logBase = g_fClustBase;
    if (g_isLogBaseBufferEnabled)
    {
        logBase = g_logBaseBuffer[tileIndex.y * _NumTileX + tileIndex.x];
    }

    int clustIdx = SnapToClusterIdxFlex(linearDepth, logBase, g_isLogBaseBufferEnabled != 0);

    int nrClusters = (1 << g_iLog2NumClusters);
    const int idx = ((lightCategory * nrClusters + clustIdx) * _NumTileY + tileIndex.y) * _NumTileX + tileIndex.x;
    uint dataPair = g_vLayeredOffsetsBuffer[idx];
    start = dataPair & 0x7ffffff;
    lightCount = (dataPair >> 27) & 31;
}

uint FetchIndexCluster(uint tileOffset, uint lightIndex)
{
    return g_vLightListGlobal[tileOffset + lightIndex];
}

void GetCountAndStart(Coordinate coord, uint lightCategory, float linearDepth, out uint start, out uint lightCount)
{
    if (_UseTileLightList)
        GetCountAndStartTile(coord, lightCategory, linearDepth, start, lightCount);
    else
        GetCountAndStartCluster(coord, lightCategory, linearDepth, start, lightCount);
}

uint FetchIndex(uint tileOffset, uint lightIndex)
{
    if (_UseTileLightList)
        return FetchIndexTile(tileOffset, lightIndex);
    else
        return FetchIndexCluster(tileOffset, lightIndex);
}

#endif

// bakeDiffuseLighting is part of the prototype so a user is able to implement a "base pass" with GI and multipass direct light (aka old unity rendering path)
void LightLoop( float3 V, float3 positionWS, Coordinate coord, PreLightData prelightData, BSDFData bsdfData, float3 bakeDiffuseLighting,
                out float3 diffuseLighting,
                out float3 specularLighting)
{
#ifdef USE_CLUSTERED_LIGHTLIST
    float linearDepth = TransformWorldToView(positionWS).z; // View space linear depth
#else
    float linearDepth = 0.0; // unused
#endif

    LightLoopContext context;
    ZERO_INITIALIZE(LightLoopContext, context);

    diffuseLighting = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);

    uint i = 0; // Declare once to avoid the D3D11 compiler warning.

#ifdef PROCESS_DIRECTIONAL_LIGHT
    for (i = 0; i < _DirectionalLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;

        EvaluateBSDF_Directional(   context, V, positionWS, prelightData, _DirectionalLightDatas[i], bsdfData,
                                    localDiffuseLighting, localSpecularLighting);

        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }
#endif

#ifdef PROCESS_PUNCTUAL_LIGHT
    // TODO: Convert the for loop below to a while on each type as we know we are sorted!
    uint punctualLightStart;
    uint punctualLightCount;
    GetCountAndStart(coord, LIGHTCATEGORY_PUNCTUAL, linearDepth, punctualLightStart, punctualLightCount);
    for (i = 0; i < punctualLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;

        EvaluateBSDF_Punctual(  context, V, positionWS, prelightData, _LightDatas[FetchIndex(punctualLightStart, i)], bsdfData,
                                localDiffuseLighting, localSpecularLighting);

        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }
#endif

#ifdef PROCESS_AREA_LIGHT
    // TODO: Convert the for loop below to a while on each type as we know we are sorted!
    uint areaLightStart;
    uint areaLightCount;
    GetCountAndStart(coord, LIGHTCATEGORY_AREA, linearDepth, areaLightStart, areaLightCount);
    for (i = 0; i < areaLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;

        uint areaIndex = FetchIndex(areaLightStart, i);

        if(_LightDatas[areaIndex].lightType == GPULIGHTTYPE_LINE)
        {
            EvaluateBSDF_Line(  context, V, positionWS, prelightData, _LightDatas[areaIndex], bsdfData,
                                localDiffuseLighting, localSpecularLighting);
        }
        else
        {
            EvaluateBSDF_Area(  context, V, positionWS, prelightData, _LightDatas[areaIndex], bsdfData,
                                localDiffuseLighting, localSpecularLighting);
        }


        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }
#endif

#ifdef PROCESS_ENV_LIGHT

    float3 iblDiffuseLighting = float3(0.0, 0.0, 0.0);
    float3 iblSpecularLighting = float3(0.0, 0.0, 0.0);

    // Only apply sky IBL if the sky texture is available.
    if (_EnvLightSkyEnabled)
    {
        float3 localDiffuseLighting, localSpecularLighting;
        float2 weight;
        // The sky is a single cubemap texture separate from the reflection probe texture array (different resolution and compression)
        context.sampleReflection = SINGLE_PASS_CONTEXT_SAMPLE_SKY;
        EnvLightData envLightSky = InitSkyEnvLightData(0); // The sky data are generated on the fly so the compiler can optimize the code
        EvaluateBSDF_Env(context, V, positionWS, prelightData, envLightSky, bsdfData, localDiffuseLighting, localSpecularLighting, weight);
        iblDiffuseLighting = lerp(iblDiffuseLighting, localDiffuseLighting, weight.x); // Should be remove by the compiler if it is smart as all is constant 0
        iblSpecularLighting = lerp(iblSpecularLighting, localSpecularLighting, weight.y);
    }

    uint envLightStart;
    uint envLightCount;
    GetCountAndStart(coord, LIGHTCATEGORY_ENV, linearDepth, envLightStart, envLightCount);

    for (i = 0; i < envLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;
        float2 weight;
        context.sampleReflection = SINGLE_PASS_CONTEXT_SAMPLE_REFLECTION_PROBES;
        EvaluateBSDF_Env(context, V, positionWS, prelightData, _EnvLightDatas[FetchIndex(envLightStart, i)], bsdfData, localDiffuseLighting, localSpecularLighting, weight);
        iblDiffuseLighting = lerp(iblDiffuseLighting, localDiffuseLighting, weight.x); // Should be remove by the compiler if it is smart as all is constant 0
        iblSpecularLighting = lerp(iblSpecularLighting, localSpecularLighting, weight.y);
    }

    diffuseLighting += iblDiffuseLighting;
    specularLighting += iblSpecularLighting;
#endif

    // TODO: currently apply GI at the same time as reflection
#ifdef PROCESS_ENV_LIGHT
    // Add indirect diffuse + emissive (if any)
    diffuseLighting += bakeDiffuseLighting;
#endif
}

#else // LIGHTLOOP_SINGLE_PASS

// bakeDiffuseLighting is part of the prototype so a user is able to implement a "base pass" with GI and multipass direct light (aka old unity rendering path)
void LightLoop( float3 V, float3 positionWS, Coordinate coord, PreLightData prelightData, BSDFData bsdfData, float3 bakeDiffuseLighting,
                out float3 diffuseLighting,
                out float3 specularLighting)
{
    LightLoopContext context;
    ZERO_INITIALIZE(LightLoopContext, context);

    diffuseLighting = float3(0.0, 0.0, 0.0);
    specularLighting = float3(0.0, 0.0, 0.0);

    uint i = 0; // Declare once to avoid the D3D11 compiler warning.

    for (i = 0; i < _DirectionalLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;

        EvaluateBSDF_Directional(   context, V, positionWS, prelightData, _DirectionalLightDatas[i], bsdfData,
                                    localDiffuseLighting, localSpecularLighting);

        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }

    for (i = 0; i < _PunctualLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;

        EvaluateBSDF_Punctual(  context, V, positionWS, prelightData, _LightDatas[i], bsdfData,
                                localDiffuseLighting, localSpecularLighting);

        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }

    // Area are store with punctual, just offset the index
    for (i = _PunctualLightCount; i < _AreaLightCount + _PunctualLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;

        if (_LightDatas[i].lightType == GPULIGHTTYPE_LINE)
        {
            EvaluateBSDF_Line(  context, V, positionWS, prelightData, _LightDatas[i], bsdfData,
                                localDiffuseLighting, localSpecularLighting);
        }
        else
        {
            EvaluateBSDF_Area(  context, V, positionWS, prelightData, _LightDatas[i], bsdfData,
                                localDiffuseLighting, localSpecularLighting);
        }

        diffuseLighting += localDiffuseLighting;
        specularLighting += localSpecularLighting;
    }

    // TODO: Check the reflection hierarchy, for the current system (matching legacy unity) we must sort from bigger solid angle to lower (lower override bigger). So begging by sky
    // TODO: Change the way it is done by reversing the order, from smaller solid angle to bigger, so we can early out when the weight is 1.
    float3 iblDiffuseLighting = float3(0.0, 0.0, 0.0);
    float3 iblSpecularLighting = float3(0.0, 0.0, 0.0);

    // Only apply sky IBL if the sky texture is available.
    if (_EnvLightSkyEnabled)
    {
        float3 localDiffuseLighting, localSpecularLighting;
        float2 weight;
        // The sky is a single cubemap texture separate from the reflection probe texture array (different resolution and compression)
        context.sampleReflection = SINGLE_PASS_CONTEXT_SAMPLE_SKY;
        EnvLightData envLightSky = InitSkyEnvLightData(0); // The sky data are generated on the fly so the compiler can optimize the code
        EvaluateBSDF_Env(context, V, positionWS, prelightData, envLightSky, bsdfData, localDiffuseLighting, localSpecularLighting, weight);
        iblDiffuseLighting = lerp(iblDiffuseLighting, localDiffuseLighting, weight.x); // Should be remove by the compiler if it is smart as all is constant 0
        iblSpecularLighting = lerp(iblSpecularLighting, localSpecularLighting, weight.y);
    }

    for (i = 0; i < _EnvLightCount; ++i)
    {
        float3 localDiffuseLighting, localSpecularLighting;
        float2 weight;
        context.sampleReflection = SINGLE_PASS_CONTEXT_SAMPLE_REFLECTION_PROBES;
        EvaluateBSDF_Env(context, V, positionWS, prelightData, _EnvLightDatas[i], bsdfData, localDiffuseLighting, localSpecularLighting, weight);
        iblDiffuseLighting = lerp(iblDiffuseLighting, localDiffuseLighting, weight.x); // Should be remove by the compiler if it is smart as all is constant 0
        iblSpecularLighting = lerp(iblSpecularLighting, localSpecularLighting, weight.y);
    }

    diffuseLighting += iblDiffuseLighting;
    specularLighting += iblSpecularLighting;

    // Add indirect diffuse + emissive (if any)
    diffuseLighting += bakeDiffuseLighting;
}

#endif
