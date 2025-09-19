#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"
#include "shared/point-light.hlsl"
#include "shared/pbr.hlsl"

cbuffer Transforms : register(b0)
{
    float4x4 CameraToClipSpace;
    float4x4 ClipSpaceToCamera;
    float4x4 WorldToCamera;
    float4x4 CameraToWorld;
    float4x4 WorldToClipSpace;
    float4x4 ClipSpaceToWorld;
    float4x4 ObjectToWorld;
    float4x4 WorldToObject;
    float4x4 ObjectToCamera;
    float4x4 ObjectToClipSpace;
};

cbuffer Params : register(b1)
{
    float4 Color;

    float AlphaCutOff;
    float UseFlatShading;
};

cbuffer FogParams : register(b2)
{
    float4 FogColor;
    float FogDistance;
    float FogBias;
}

cbuffer PointLights : register(b3)
{
    PointLight Lights[8];
    int ActiveLightCount;
}

cbuffer PbrParams : register(b4)
{
    float4 BaseColor;
    float4 EmissiveColor;
    float Roughness;
    float Specular;
    float Metal;
}

cbuffer Params : register(b5)
{
    /*{FLOAT_PARAMS}*/
}

struct vsOutput
{
    float2 texCoord : TEXCOORD;
    float4 pixelPosition : SV_POSITION;
    float3 worldPosition : POSITION;
    float3x3 tbnToWorld : TBASIS;
    float fog : VPOS;
    float3 faceNormal : FACE_NORMAL; // Add this for flat shading
};

struct psInput
{
    float2 texCoord : TEXCOORD;
    float4 pixelPosition : SV_POSITION;
    float3 worldPosition : POSITION;
    float3x3 tbnToWorld : TBASIS;
    float fog : VPOS;
    float3 flatNormal : FACE_NORMAL; // Add flat normal from geometry shader
};

sampler WrappedSampler : register(s0);
//sampler LinearSampler : register(s1);
sampler ClampedSampler : register(s1);

StructuredBuffer<PbrVertex> PbrVertices : register(t0);
StructuredBuffer<int3> FaceIndices : register(t1);

Texture2D<float4> BaseColorMap : register(t2);
Texture2D<float4> EmissiveColorMap : register(t3);
Texture2D<float4> RSMOMap : register(t4);
Texture2D<float4> NormalMap : register(t5);

TextureCube<float4> PrefilteredSpecular : register(t6);
Texture2D<float4> BRDFLookup : register(t7);

vsOutput vsMain(uint id : SV_VertexID)
{
    vsOutput output;

    int faceIndex = id / 3; //  (id % verticesPerInstance) / 3;
    int faceVertexIndex = id % 3;

    PbrVertex vertex = PbrVertices[FaceIndices[faceIndex][faceVertexIndex]];

    float4 posInObject = float4(vertex.Position, 1);

    float4 posInClipSpace = mul(posInObject, ObjectToClipSpace);
    output.pixelPosition = posInClipSpace;

    float2 uv = vertex.TexCoord;
    output.texCoord = float2(uv.x, 1 - uv.y);

    // Pass tangent space basis vectors (for normal mapping).
    float3x3 TBN = float3x3(vertex.Tangent, vertex.Bitangent, vertex.Normal);
    TBN = mul(TBN, (float3x3)ObjectToWorld);

    output.tbnToWorld = float3x3(
        normalize(TBN._m00_m01_m02),
        normalize(TBN._m10_m11_m12),
        normalize(TBN._m20_m21_m22));

    output.worldPosition = mul(posInObject, ObjectToWorld).xyz;

    // Fog
    if (FogDistance > 0)
    {
        float4 posInCamera = mul(posInObject, ObjectToCamera);
        float fog = pow(saturate(-posInCamera.z / FogDistance), FogBias);
        output.fog = fog;
    }

    // Store the original normal for potential use
    output.faceNormal = mul(vertex.Normal, (float3x3)ObjectToWorld);

    return output;
}

[maxvertexcount(3)]
void gsMain(triangle vsOutput input[3], inout TriangleStream<psInput> output)
{
    // Calculate flat normal for the entire triangle
    float3 p0 = input[0].worldPosition;
    float3 p1 = input[1].worldPosition;
    float3 p2 = input[2].worldPosition;
    
    float3 edge1 = p1 - p0;
    float3 edge2 = p2 - p0;
    float3 flatNormal = normalize(cross(edge1, edge2));
    
    // Pass the same flat normal to all vertices of the triangle
    for (int i = 0; i < 3; i++)
    {
        psInput element;
        element.texCoord = input[i].texCoord;
        element.pixelPosition = input[i].pixelPosition;
        element.worldPosition = input[i].worldPosition;
        element.tbnToWorld = input[i].tbnToWorld;
        element.fog = input[i].fog;
        element.flatNormal = flatNormal; // Pass the computed flat normal
        
        output.Append(element);
    }
    
    output.RestartStrip();
}

//=== Global functions ==============================================
/*{GLOBALS}*/

//=== Additional Resources ==========================================
/*{RESOURCES(t8)}*/

//=== Field functions ===============================================
/*{FIELD_FUNCTIONS}*/

//-------------------------------------------------------------------

//-------------------------------------------------------------------
inline float4 GetField(float4 p)
{
#ifndef USE_WORLDSPACE
    //p.xyz = mul(float4(p.xyz, 1), WorldToObject).xyz;
#endif
    float4 f = 1;
    /*{FIELD_CALL}*/

    return f;
}

float GetDistance(float3 p3)
{
    return GetField(float4(p3.xyz, 0)).w;
}
//===================================================================

#include "shared/pbr-render.hlsl"

float3 ComputeNormal(psInput pin, float3x3 tbnToWorld)
{
    float3 N;
    if (UseFlatShading > 0.5)
    {
        // Use the flat normal from geometry shader
        N = normalize(pin.flatNormal);
        
        // Optionally apply normal map details (though this may reduce the "flat" look)
        float4 normalMap = NormalMap.Sample(WrappedSampler, pin.texCoord);
        float3 normalDetail = normalize(2.0 * normalMap.rgb - 1.0);
        
        // Create a simple TBN using the flat normal
        float3 T = normalize(ddx(pin.worldPosition));
        float3 B = normalize(cross(N, T));
        float3x3 flatTBN = float3x3(T, B, N);
        
        // Apply normal map if desired
        N = normalize(mul(normalDetail, flatTBN));
    }
    else
    {
        // Standard shading
        float4 normalMap = NormalMap.Sample(WrappedSampler, pin.texCoord);
        N = normalize(2.0 * normalMap.rgb - 1.0);
        N = normalize(mul(N, tbnToWorld));
    }
    return N;
}

float4 psMain(psInput pin) : SV_TARGET
{
    float4 roughnessMetallicOcclusion = RSMOMap.Sample(WrappedSampler, pin.texCoord);

    frag.Roughness = saturate(roughnessMetallicOcclusion.x + Roughness);
    frag.Metalness = saturate(roughnessMetallicOcclusion.y + Metal);
    frag.Occlusion = roughnessMetallicOcclusion.z;
    frag.albedo = BaseColorMap.Sample(WrappedSampler, pin.texCoord);
    frag.uv = pin.texCoord;
    frag.N = ComputeNormal(pin, pin.tbnToWorld);
    frag.fog = pin.fog;
    frag.worldPosition = pin.worldPosition;

    float4 eyePosition = mul(float4(0, 0, 0, 1), CameraToWorld);
    frag.Lo = normalize(eyePosition.xyz - frag.worldPosition);

    float4 litColor = ComputePbr();

    litColor.rgba *= GetField(float4(pin.worldPosition.xyz, 0)).rgba;
    if (AlphaCutOff > 0 && litColor.a < AlphaCutOff)
    {
        discard;
    }
    return litColor;
}
