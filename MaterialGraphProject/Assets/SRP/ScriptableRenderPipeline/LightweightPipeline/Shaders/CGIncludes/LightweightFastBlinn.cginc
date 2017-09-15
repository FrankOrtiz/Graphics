﻿#ifndef LIGHTWEIGHT_FASTBLINN_INCLUDED
#define LIGHTWEIGHT_FASTBLINN_INCLUDED

#include "UnityStandardInput.cginc"
#include "LightweightCore.cginc"

struct SurfaceFastBlinn
{
	float3 Diffuse;     // diffuse color
	float3 Specular;    // specular color
	float3 Normal;      // tangent space normal, if written
	half3 Emission;
	half Glossiness;    // 0=rough, 1=smooth
	float Alpha;        // alpha for transparencies
};

SurfaceFastBlinn InitializeSurfaceFastBlinn()
{
	SurfaceFastBlinn s;
	s.Diffuse = float3(0.5, 0.5, 0.5);
	s.Specular = float3(0, 0, 0);
	s.Normal = float3(.5, .5, 1);
	s.Emission = 0;
	s.Glossiness = 0;
	s.Alpha = 1;
	return s;
}

void DefineSurface(LightweightVertexOutput i, inout SurfaceFastBlinn o);

half4 LightweightFragmentFastBlinn(LightweightVertexOutput i) : SV_Target
{
	SurfaceFastBlinn o = InitializeSurfaceFastBlinn();
	DefineSurface(i, o);

    // Keep for compatibility reasons. Shader Inpector throws a warning when using cutoff
    // due overdraw performance impact.
#ifdef _ALPHATEST_ON
    clip(o.Alpha - _Cutoff);
#endif

	half3 normal;
#if _NORMALMAP
	half3 tangentToWorld0 = half3(i.tangent.x, i.binormal.x, i.normal.x);
	half3 tangentToWorld1 = half3(i.tangent.y, i.binormal.y, i.normal.y);
	half3 tangentToWorld2 = half3(i.tangent.z, i.binormal.z, i.normal.z);
	normal = normalize(half3(dot(o.Normal, tangentToWorld0), dot(o.Normal, tangentToWorld1), dot(o.Normal, tangentToWorld2)));
#else
	normal = normalize(i.normal);
#endif

    half3 viewDir = i.viewDir.xyz;
    float3 worldPos = i.posWS.xyz;

    half3 lightDirection;
                
	half4 specularGloss = half4(o.Specular, o.Glossiness);

#ifdef _SHADOWS
#if _NORMALMAP
	half3 vertexNormal = half3(tangentToWorld0.z, tangentToWorld1.z, tangentToWorld2.z); // Fix this
#else
	half3 vertexNormal = i.normal;
#endif
#endif

#ifndef _MULTIPLE_LIGHTS
    LightInput lightInput;
    INITIALIZE_MAIN_LIGHT(lightInput);
    half lightAtten = ComputeLightAttenuation(lightInput, normal, worldPos, lightDirection);
#ifdef _SHADOWS
    lightAtten *= ComputeShadowAttenuation(vertexNormal, i.posWS, _ShadowLightDirection.xyz);
#endif
				
#ifdef LIGHTWEIGHT_SPECULAR_HIGHLIGHTS
    half3 color = LightingBlinnPhong(o.Diffuse, specularGloss, lightDirection, normal, viewDir, lightAtten) * lightInput.color;
#else
    half3 color = LightingLambert(o.Diffuse, lightDirection, normal, lightAtten) * lightInput.color;
#endif
    
#else
    half3 color = half3(0, 0, 0);

#ifdef _SHADOWS
    half shadowAttenuation = ComputeShadowAttenuation(vertexNormal, i.posWS, _ShadowLightDirection.xyz);
#endif
    int pixelLightCount = min(globalLightCount.x, unity_LightIndicesOffsetAndCount.y);
    for (int lightIter = 0; lightIter < pixelLightCount; ++lightIter)
    {
        LightInput lightData;
        int lightIndex = unity_4LightIndices0[lightIter];
        INITIALIZE_LIGHT(lightData, lightIndex);
        half lightAtten = ComputeLightAttenuation(lightData, normal, worldPos, lightDirection);
#ifdef _SHADOWS
        lightAtten *= max(shadowAttenuation, half(lightIndex != _ShadowData.x));
#endif

#ifdef LIGHTWEIGHT_SPECULAR_HIGHLIGHTS
        color += LightingBlinnPhong(o.Diffuse, specularGloss, lightDirection, normal, viewDir, lightAtten) * lightData.color;
#else
        color += LightingLambert(o.Diffuse, lightDirection, normal, lightAtten) * lightData.color;
#endif
    }

#endif // _MULTIPLE_LIGHTS

	color += o.Emission;

#if defined(LIGHTMAP_ON)
    color += (DecodeLightmap(UNITY_SAMPLE_TEX2D(unity_Lightmap, i.uv01.zw)) + i.fogCoord.yzw) * o.Diffuse;
#elif defined(_VERTEX_LIGHTS) || defined(_LIGHT_PROBES_ON)
    color += i.fogCoord.yzw * o.Diffuse;
#endif

#if _REFLECTION_CUBEMAP
    // TODO: we can use reflect vec to compute specular instead of half when computing cubemap reflection
    half3 reflectVec = reflect(-i.viewDir.xyz, normal);
    color += texCUBE(_Cube, reflectVec).rgb * o.Specular;
#elif defined(_REFLECTION_PROBE)
    half3 reflectVec = reflect(-i.viewDir.xyz, normal);
    half4 reflectionProbe = UNITY_SAMPLE_TEXCUBE(unity_SpecCube0, reflectVec);
    color += reflectionProbe.rgb * (reflectionProbe.a * unity_SpecCube0_HDR.x) * o.Specular;
#endif

    UNITY_APPLY_FOG(i.fogCoord, color);

    return OutputColor(color, o.Alpha);
};

#endif