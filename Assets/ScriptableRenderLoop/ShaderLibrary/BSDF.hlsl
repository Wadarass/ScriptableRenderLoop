#ifndef UNITY_BSDF_INCLUDED
#define UNITY_BSDF_INCLUDED

// Note: All NDF and diffuse term have a version with and without divide by PI.
// Version with divide by PI are use for direct lighting.
// Version without divide by PI are use for image based lighting where often the PI cancel during importance sampling

//-----------------------------------------------------------------------------
// Fresnel term
//-----------------------------------------------------------------------------

float F_Schlick(float f0, float f90, float u)
{
    float x     = 1.0 - u;
    float x5    = x * x;
    x5          = x5 * x5 * x;
    return (f90 - f0) * x5 + f0; // sub mul mul mul sub mad
}

float F_Schlick(float f0, float u)
{
    return F_Schlick(f0, 1.0, u);
}

float3 F_Schlick(float3 f0, float f90, float u)
{
    float x     = 1.0 - u;
    float x5    = x * x;
    x5          = x5 * x5 * x;
    return (float3(f90, f90, f90) - f0) * x5 + f0; // sub mul mul mul sub mad
}

float3 F_Schlick(float3 f0, float u)
{
    return F_Schlick(f0, 1.0, u);
}

//-----------------------------------------------------------------------------
// Specular BRDF
//-----------------------------------------------------------------------------

// With analytical light (not image based light) we clamp the minimun roughness in the NDF to avoid numerical instability.
#define UNITY_MIN_ROUGHNESS 0.002

float2 D_GGXNoPI_Frac(float NdotH, float roughness)
{
    roughness = max(roughness, UNITY_MIN_ROUGHNESS);

    float a2 = roughness * roughness;
    float f  = (NdotH * a2 - NdotH) * NdotH + 1.0;

    return float2(a2, f * f);
}

float D_GGXNoPI(float NdotH, float roughness)
{
    float2 d = D_GGXNoPI_Frac(NdotH, roughness);
    return d.x / d.y;
}

float2 D_GGX_Frac(float NdotH, float roughness)
{
    float2 d = D_GGXNoPI_Frac(NdotH, roughness);
    return float2(d.x, d.y * PI);
}

float D_GGX(float NdotH, float roughness)
{
    float2 d = D_GGX_Frac(NdotH, roughness);
    return d.x / d.y;
}

float D_GGX_Inverse(float NdotH, float roughness)
{
    float2 d = D_GGX_Frac(NdotH, roughness);
    return d.y / d.x;
}

// Ref: Understanding the Masking-Shadowing Function in Microfacet-Based BRDFs, p. 19, 29.
float2 G_MaskingSmithGGX_Frac(float NdotV, float VdotH, float roughness)
{
    roughness = max(roughness, UNITY_MIN_ROUGHNESS);

    // G1(V, H)    = HeavisideStep(VdotH) / (1 + Λ(V)).
    // Λ(V)        = -0.5 + 0.5 * sqrt(1 + 1 / a²).
    // a           = 1 / (roughness * tan(theta)).
    // 1 + Λ(V)    = 0.5 + 0.5 * sqrt(1 + roughness² * tan²(theta)).
    // tan²(theta) = (1 - cos²(theta)) / cos²(theta) = 1 / cos²(theta) - 1.

    float hs = VdotH > 0.0 ? 1.0 : 0.0;
    float a2 = roughness * roughness;
    float z2 = NdotV * NdotV;

    return float2(hs, 0.5 + 0.5 * sqrt(1.0 + a2 * (1.0 / z2 - 1.0)));
}

float G_MaskingSmithGGX(float NdotV, float VdotH, float roughness)
{
    float2 g = G_MaskingSmithGGX_Frac(NdotV, VdotH, roughness);
    return g.x / g.y;
}

// Ref: Understanding the Masking-Shadowing Function in Microfacet-Based BRDFs, p. 12.
float D_GGX_Visible(float NdotH, float NdotV, float VdotH, float roughness)
{
    float2 d = D_GGX_Frac(NdotH, roughness);
    // Note that we pass 1 instead of 'VdotH' since the multiplication will already clamp.
    float2 g = G_MaskingSmithGGX_Frac(NdotV, 1.0, roughness);

    return (d.x * g.x * VdotH) / (d.y * g.y * NdotV);
}

// Ref: http://jcgt.org/published/0003/02/03/paper.pdf
float V_SmithJointGGX(float NdotL, float NdotV, float roughness)
{
    // Original formulation:
    //  lambda_v    = (-1 + sqrt(a2 * (1 - NdotL2) / NdotL2 + 1)) * 0.5f;
    //  lambda_l    = (-1 + sqrt(a2 * (1 - NdotV2) / NdotV2 + 1)) * 0.5f;
    //  G           = 1 / (1 + lambda_v + lambda_l);

    float a = roughness; 
    float a2 = a * a;
    // Reorder code to be more optimal
    float lambdaV = NdotL * sqrt((-NdotV * a2 + NdotV) * NdotV + a2);
    float lambdaL = NdotV * sqrt((-NdotL * a2 + NdotL) * NdotL + a2);

    // Simplify visibility term: (2.0f * NdotL * NdotV) /  ((4.0f * NdotL * NdotV) * (lambda_v + lambda_l));
    return 0.5 / (lambdaV + lambdaL);
}

// Precompute part of lambdaV
float GetSmithJointGGXLambdaV(float NdotV, float roughness)
{
    float a = roughness;
    float a2 = a * a;
    return sqrt((-NdotV * a2 + NdotV) * NdotV + a2);
}

float V_SmithJointGGX(float NdotL, float NdotV, float roughness, float lambdaV)
{
    float a = roughness;
    float a2 = a * a;
    // Reorder code to be more optimal
    lambdaV *= NdotL;
    float lambdaL = NdotV * sqrt((-NdotL * a2 + NdotL) * NdotL + a2);

    // Simplify visibility term: (2.0f * NdotL * NdotV) /  ((4.0f * NdotL * NdotV) * (lambda_v + lambda_l));
    return 0.5 / (lambdaV + lambdaL);
}

float V_SmithJointGGXApprox(float NdotL, float NdotV, float roughness)
{
    float a = roughness;
    // Approximation of the above formulation (simplify the sqrt, not mathematically correct but close enough)
    float lambdaV = NdotL * (NdotV * (1 - a) + a);
    float lambdaL = NdotV * (NdotL * (1 - a) + a);

    return 0.5 / (lambdaV + lambdaL);
}

// Precompute part of LambdaV
float GetSmithJointGGXApproxLambdaV(float NdotV, float roughness)
{
    float a = roughness;
    return NdotV * (1 - a) + a;
}

float V_SmithJointGGXApprox(float NdotL, float NdotV, float roughness, float lambdaV)
{
    float a = roughness;
    // Approximation of the above formulation (simplify the sqrt, not mathematically correct but close enough)
    lambdaV *= NdotL;
    float lambdaL = NdotV * (NdotL * (1 - a) + a);

    return 0.5 / (lambdaV + lambdaL);
}

// roughnessT -> roughness in tangent direction
// roughnessB -> roughness in bitangent direction
float D_GGXAnisoNoPI(float TdotH, float BdotH, float NdotH, float roughnessT, float roughnessB)
{
    roughnessT = max(roughnessT, UNITY_MIN_ROUGHNESS);
    roughnessB = max(roughnessB, UNITY_MIN_ROUGHNESS);

    float f = TdotH * TdotH / (roughnessT * roughnessT) + BdotH * BdotH / (roughnessB * roughnessB) + NdotH * NdotH;
    return 1.0 / (roughnessT * roughnessB * f * f);
}

float D_GGXAniso(float TdotH, float BdotH, float NdotH, float roughnessT, float roughnessB)
{
    return INV_PI * D_GGXAnisoNoPI(TdotH, BdotH, NdotH, roughnessT, roughnessB);
}

// Ref: https://cedec.cesa.or.jp/2015/session/ENG/14698.html The Rendering Materials of Far Cry 4
float V_SmithJointGGXAniso(float TdotV, float BdotV, float NdotV, float TdotL, float BdotL, float NdotL, float roughnessT, float roughnessB)
{
    float aT = roughnessT;
    float aT2 = aT * aT;
    float aB = roughnessB;
    float aB2 = aB * aB;

    float lambdaV = NdotL * sqrt(aT2 * TdotV * TdotV + aB2 * BdotV * BdotV + NdotV * NdotV);
    float lambdaL = NdotV * sqrt(aT2 * TdotL * TdotL + aB2 * BdotL * BdotL + NdotL * NdotL);

    return 0.5 / (lambdaV + lambdaL);
}

float GetSmithJointGGXAnisoLambdaV(float TdotV, float BdotV, float NdotV, float roughnessT, float roughnessB)
{
    float aT = roughnessT;
    float aT2 = aT * aT;
    float aB = roughnessB;
    float aB2 = aB * aB;

    return sqrt(aT2 * TdotV * TdotV + aB2 * BdotV * BdotV + NdotV * NdotV);
}

float V_SmithJointGGXAnisoLambdaV(float TdotV, float BdotV, float NdotV, float TdotL, float BdotL, float NdotL, float roughnessT, float roughnessB, float lambdaV)
{
    float aT = roughnessT;
    float aT2 = aT * aT;
    float aB = roughnessB;
    float aB2 = aB * aB;

    lambdaV *= NdotL;
    float lambdaL = NdotV * sqrt(aT2 * TdotL * TdotL + aB2 * BdotL * BdotL + NdotL * NdotL);

    return 0.5 / (lambdaV + lambdaL);
}

//-----------------------------------------------------------------------------
// Diffuse BRDF - diffuseColor is expected to be multiply by the caller
//-----------------------------------------------------------------------------

float LambertNoPI()
{
    return 1.0;
}

float Lambert()
{
    return INV_PI;
}

float DisneyDiffuseNoPI(float NdotV, float NdotL, float LdotH, float perceptualRoughness)
{
    float fd90 = 0.5 + 2 * LdotH * LdotH * perceptualRoughness;
    // Two schlick fresnel term
    float lightScatter = F_Schlick(1.0, fd90, NdotL);
    float viewScatter = F_Schlick(1.0, fd90, NdotV);

    return lightScatter * viewScatter;
}

float DisneyDiffuse(float NdotV, float NdotL, float LdotH, float perceptualRoughness)
{
    return INV_PI * DisneyDiffuseNoPI(NdotV, NdotL, LdotH, perceptualRoughness);
}


#endif // UNITY_BSDF_INCLUDED
