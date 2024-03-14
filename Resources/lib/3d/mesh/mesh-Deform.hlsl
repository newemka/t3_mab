#include "lib/shared/hash-functions.hlsl"
#include "lib/shared/noise-functions.hlsl"
#include "lib/shared/pbr.hlsl"

cbuffer Params : register(b0)
{
    float4x4 TransformMatrix;
    float TwistAxis;
    float Twist;
    float UseVertexSelection;
    float Shift;
}

StructuredBuffer<PbrVertex> SourceVerts : t0;        
RWStructuredBuffer<PbrVertex> ResultVerts : u0;   


[numthreads(64,1,1)]
void main(uint3 i : SV_DispatchThreadID)
{
    uint numStructs, stride;
    SourceVerts.GetDimensions(numStructs, stride);
    if(i.x >= numStructs) {
        return;
    }
    
    float s = UseVertexSelection > 0.5 ? SourceVerts[i.x].Selected : 1;
    float3 pos = SourceVerts[i.x].Position;
    float3 translation = float3(TransformMatrix[3].x, TransformMatrix[3].y, TransformMatrix[3].z); // Extracting translation component from TransformMatrix

    // Subtract deformation origin from vertex position
    pos -= translation;
    float3 twistedPos = pos; // Initialize the twisted position

    // Apply twist along specified axis
    float twistAmount;
    switch ((int) TwistAxis){
        case 0:
            twistAmount = (Shift +pos.x) * (Twist * s);
            twistedPos.y = pos.y * cos(twistAmount) - pos.z * sin(twistAmount);
            twistedPos.z = pos.y * sin(twistAmount) + pos.z * cos(twistAmount);
            break;
        case 1:
            twistAmount = (Shift +pos.y) * (Twist * s);
            twistedPos.x = pos.x * cos(twistAmount) - pos.z * sin(twistAmount);
            twistedPos.z = pos.x * sin(twistAmount) + pos.z * cos(twistAmount);
            break;
        case 2:
            twistAmount = (Shift +pos.z) * (Twist * s);
            twistedPos.x = pos.x * cos(twistAmount) - pos.y * sin(twistAmount);
            twistedPos.y = pos.x * sin(twistAmount) + pos.y * cos(twistAmount);
            break;
    }

    // Add deformation origin back to the twisted position
    twistedPos += translation;

    // Apply matrix transformation
    ResultVerts[i.x].Position = lerp(pos, twistedPos, s) ;

    // Apply rotation to normal, tangent, and bitangent based on twist
    float3x3 rotationMatrix;
    switch ((int) TwistAxis){
        case 0:
            rotationMatrix = float3x3(1, 0, 0,
                                       0, cos(twistAmount), -sin(twistAmount),
                                       0, sin(twistAmount), cos(twistAmount));
            break;
        case 1:
            rotationMatrix = float3x3(cos(twistAmount), 0, -sin(twistAmount),
                                       0, 1, 0,
                                       sin(twistAmount), 0, cos(twistAmount));
            break;
        case 2:
            rotationMatrix = float3x3(cos(twistAmount), -sin(twistAmount), 0,
                                       sin(twistAmount), cos(twistAmount), 0,
                                       0, 0, 1);
            break;
    }
 
    ResultVerts[i.x].Normal = normalize(lerp(SourceVerts[i.x].Normal, mul(rotationMatrix, SourceVerts[i.x].Normal), s));
    ResultVerts[i.x].Tangent = normalize(lerp(SourceVerts[i.x].Tangent, mul(rotationMatrix, SourceVerts[i.x].Tangent), s));
    ResultVerts[i.x].Bitangent = normalize(lerp(SourceVerts[i.x].Bitangent, mul(rotationMatrix, SourceVerts[i.x].Bitangent), s));

    ResultVerts[i.x].TexCoord = SourceVerts[i.x].TexCoord;

    ResultVerts[i.x].Selected = SourceVerts[i.x].Selected;
}
