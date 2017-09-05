#ifndef UNITY_VOLUME_RENDERING_INCLUDED
#define UNITY_VOLUME_RENDERING_INCLUDED

// Reminder:
// Optical_Depth(x, y) = Integral{x, y}{Extinction(t) dt}
// Transmittance(x, y) = Exp(-Optical_Depth(x, y))
// Transmittance(x, z) = Transmittance(x, y) * Transmittance(y, z)
// Integral{a, b}{Transmittance(0, x) dx} = Transmittance(0, a) * Integral{0, b - a}{Transmittance(a, a + x) dx}

float OpticalDepthHomogeneous(float extinction, float intervalLength)
{
    return extinction * intervalLength;
}

float3 OpticalDepthHomogeneous(float3 extinction, float intervalLength)
{
    return extinction * intervalLength;
}

float Transmittance(float opticalDepth)
{
    return exp(-opticalDepth);
}

float3 Transmittance(float3 opticalDepth)
{
    return exp(-opticalDepth);
}

// Integral{0, b - a}{Transmittance(a, a + x) dx}.
float TransmittanceIntegralHomogeneous(float extinction, float intervalLength)
{
    return rcp(extinction) - rcp(extinction) * exp(-extinction * intervalLength);
}

// Integral{0, b - a}{Transmittance(a, a + x) dx}.
float3 TransmittanceIntegralHomogeneous(float3 extinction, float intervalLength)
{
    return rcp(extinction) - rcp(extinction) * exp(-extinction * intervalLength);
}

float IsotropicPhaseFunction()
{
    return INV_FOUR_PI;
}

float HenyeyGreensteinPhasePartConstant(float asymmetry)
{
    float g = asymmetry;

    return INV_FOUR_PI * (1 - g * g);
}

float HenyeyGreensteinPhasePartVarying(float asymmetry, float LdotD)
{
    float g = asymmetry;

    return pow(abs(1 + g * g - 2 * g * LdotD), -1.5);
}

float HenyeyGreensteinPhaseFunction(float asymmetry, float LdotD)
{
    return HenyeyGreensteinPhasePartConstant(asymmetry) *
           HenyeyGreensteinPhasePartVarying(asymmetry, LdotD);
}

float4 GetInScatteredRadianceAndTransmittance(float2 positionSS, float depthVS,
                                              TEXTURE3D(vBuffer), SAMPLER3D(bilinearSampler),
                                              float4 vBufferProjParams, int numSlices = 64)
{
    float3 positionVB = float3(positionSS, vBufferProjParams.x + vBufferProjParams.y * rcp(depthVS));

    // We cannot simply perform trilinear interpolation since the distance between slices is Z-encoded.
    float slice0 = floor(positionVB.z * numSlices);
    float slice1 =  ceil(positionVB.z * numSlices);
    float z0     = saturate(slice0 * rcp(numSlices));
    float z1     = saturate(slice1 * rcp(numSlices));
    float d0     = LinearEyeDepth(z0, vBufferProjParams);
    float d1     = LinearEyeDepth(z1, vBufferProjParams);

    // Perform 2 bilinear taps.
    float4 v0 = SAMPLE_TEXTURE3D(vBuffer, bilinearSampler, float3(positionVB.xy, z0));
    float4 v1 = SAMPLE_TEXTURE3D(vBuffer, bilinearSampler, float3(positionVB.xy, z1));
    float4 vt = lerp(v0, v1, (depthVS - d0) / (d1 - d0));

    return float4(vt.rgb, Transmittance(vt.a));
}

#endif // UNITY_VOLUME_RENDERING_INCLUDED
