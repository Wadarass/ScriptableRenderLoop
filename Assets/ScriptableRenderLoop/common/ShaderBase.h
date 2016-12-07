#ifndef __SHADERBASE_H__
#define __SHADERBASE_H__

#ifdef SHADER_API_PSSL

#ifndef Texture2DMS
	#define Texture2DMS		MS_Texture2D
#endif

#ifndef SampleCmpLevelZero
	#define SampleCmpLevelZero				SampleCmpLOD0
#endif

#ifndef firstbithigh
	#define firstbithigh		FirstSetBit_Hi
#endif

#endif


#define __HLSL      1
#define public


#define unistruct   cbuffer
#define hbool       bool

#define _CB_REGSLOT(x)      : register(x)
#define _QALIGN(x)          : packoffset(c0);


float FetchDepth(Texture2D depthTexture, uint2 pixCoord)
{
	float zdpth = depthTexture.Load(uint3(pixCoord.xy, 0)).x;
#if !defined(SHADER_API_GLES3) && !defined(SHADER_API_GLCORE)
	zdpth = 1.0 - zdpth;
#endif
    return zdpth;
}

float FetchDepthMSAA(Texture2DMS<float> depthTexture, uint2 pixCoord, uint sampleIdx)
{
	float zdpth = depthTexture.Load(uint3(pixCoord.xy, 0), sampleIdx).x;
#if !defined(SHADER_API_GLES3) && !defined(SHADER_API_GLCORE)
	zdpth = 1.0 - zdpth;
#endif
    return zdpth;
}

#endif
