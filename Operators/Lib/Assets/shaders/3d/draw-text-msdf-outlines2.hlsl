
static const float3 Quad[] = 
{
  float3(0, -1, 0),
  float3( 1, -1, 0), 
  float3( 1,  0, 0), 
  float3( 1,  0, 0), 
  float3(0,  0, 0), 
  float3(0, -1, 0), 

};

static const float4 UV[] = 
{ 
    //    min  max
     //   U V  U V
  float4( 1, 0, 0, 1), 
  float4( 0, 0, 1, 1), 
  float4( 0, 1, 1, 0), 
  float4( 0, 1, 1, 0), 
  float4( 1, 1, 0, 0), 
  float4( 1, 0, 0, 1), 
};

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
    float4 OutlineColor;
    float4 FillColor;
    float Inset;
    float Outset;
    float Sharpness;
    float BillboardMode;
};

struct GridEntry
{
    float3 Position;     
    float Size;             // 3
    float AspectRatio;      // 4
    float4 Orientation;     // 5
    float4 Color;           // 9
    float4 UvMinMax;        // 13
    uint CharIndex;         // 17
    uint LineNumber;        // 18
    float2 Offset;
};

struct Output
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD;
    float4 color : COLOR;
};

StructuredBuffer<GridEntry> GridEntries : t0;
Texture2D<float4> fontTexture : register(t1);
sampler texSampler : register(s0);


Output vsMain(uint id: SV_VertexID)
{
    Output output;

    int vertexIndex = id % 6;
    int entryIndex = id / 6;
    float3 quadPos = Quad[vertexIndex];

    GridEntry entry = GridEntries[entryIndex];

    // First, get the letter's position in object space
    float3 letterPos = entry.Position;
    
    // Add the quad offset to create the vertex position in object space
    float3 vertexPos = letterPos;
    vertexPos.xy += quadPos.xy * float2(entry.Size * entry.AspectRatio, entry.Size);
    
    float4 worldPos;
    
    if (BillboardMode > 0.5) // Billboard mode 
    {
        float4 layoutCenter = float4(0, 0, 0, 1);
        float4 worldCenter = mul(layoutCenter, ObjectToWorld);
        float3 localOffset = letterPos;
        
        // Add the quad offset to the letter offset
        localOffset.xy += quadPos.xy * float2(entry.Size * entry.AspectRatio, entry.Size);      
        
        // Create billboard rotation matrix
        float3 zAxis = normalize(CameraToWorld[2].xyz);
        float3 xAxis = normalize(cross(float3(0, 1, 0), zAxis));
        float3 yAxis = cross(zAxis, xAxis);
        
        float3x3 billboardRot = float3x3(
            xAxis,    yAxis, zAxis
        );
        
        // Rotate the local offset
        float3 rotatedOffset = mul(localOffset, billboardRot);
        
        // Final world position = world center + rotated offset
        worldPos = float4(worldCenter.xyz + rotatedOffset, 1);
    }
    else
    {
        worldPos = mul(float4(vertexPos, 1), ObjectToWorld);
    }
    
    float4 quadPosInCamera = mul(worldPos, WorldToCamera);
    output.position = mul(quadPosInCamera, CameraToClipSpace);

    float4 uv = entry.UvMinMax * UV[vertexIndex];
    output.texCoord = uv.xy + uv.zw;
    
    return output;
}


struct PsInput
{
    float4 position : SV_POSITION;
    float2 texCoord : TEXCOORD;
    float4 color : COLOR;
};

float median(float r, float g, float b) {
    return max(min(r, g), min(max(r, g), b));
}

float4 psMain(PsInput input) : SV_TARGET
{
    float3 smpl = fontTexture.Sample(texSampler, input.texCoord).rgb;
    float sd = median(smpl.r, smpl.g, smpl.b) - 0.5;

    float pxDist = fwidth(sd);
    float toPixels = Sharpness / max(pxDist, 1e-5);

    float fill = clamp(sd * toPixels + 0.5, 0.0, 1.0);

    float outerEdge = sd + Outset;
    float innerEdge = Inset - sd;      
    float outlineSd = min(outerEdge, innerEdge);
    float outline = clamp(outlineSd * toPixels + 0.5, 0.0, 1.0);

    if (FillColor.a < 0.01)
    {
        return float4(OutlineColor.rgb, outline);
    }

    float3 rgb = lerp(OutlineColor.rgb, FillColor.rgb, 1.0-outline);
    float a    = max(outline * OutlineColor.a, fill * FillColor.a);

    return float4(rgb, a);
}
