#include "shared/hash-functions.hlsl"
#include "shared/noise-functions.hlsl"
#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"

cbuffer Params : register(b0)
{
    float3 Center;
    float Amount;
    float3 UpVector;
    float UseWAsWeight;
    float Flip;
    float BillboardMode; // 0 = Look at Center, 1 = Billboard to camera, 2 = Screen space
    float BaseScale;
    float ScaleMode; // 0 = No scaling, 1 = Screen space scaling
    float FOVFactor; // Adjust this to match your camera's field of view
}

cbuffer Transforms : register(b1)
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

StructuredBuffer<Point> SourcePoints : t0;
RWStructuredBuffer<Point> ResultPoints : u0;

static const float PointSpace = 0;
static const float ObjectSpace = 1;
static const float WorldSpace = 2;

[numthreads(64, 1, 1)] void main(uint3 i : SV_DispatchThreadID)
{
    uint numStructs, stride;
    SourcePoints.GetDimensions(numStructs, stride);
    if (i.x >= numStructs)
    {
        return;
    }

    Point p = SourcePoints[i.x];
    float3 originalFX1 = p.FX1;

    float weight = UseWAsWeight > 0.5 ? p.FX1 : 1;
    weight *= Amount;

    float4 newRot;
    float distanceToCamera = 1.0;
    
    if (BillboardMode > 1.5) // Screen space (mode 2)
    {
        float3 cameraForward = normalize(CameraToWorld[2].xyz);
        float3 cameraUp = normalize(CameraToWorld[1].xyz);
        
        float sign = Flip > 0.5 ? -1 : 1;
        newRot = qLookAt(cameraForward * sign, cameraUp);
        
        // Calculate distance to camera plane
        float3 cameraPos = CameraToWorld[3].xyz;
        float3 toPoint = p.Position - cameraPos;
        distanceToCamera = dot(toPoint, cameraForward);
    }
    else if (BillboardMode > 0.5) // Billboard to camera (mode 1)
    {
        float3 cameraPos = CameraToWorld[3].xyz;
        float3 toCamera = normalize(cameraPos - p.Position);
        
        float sign = Flip > 0.5 ? -1 : 1;
        newRot = qLookAt(toCamera * sign, normalize(UpVector));
        
        float3 cameraForward = normalize(CameraToWorld[2].xyz);
        float3 toPoint = p.Position - cameraPos;
        distanceToCamera = dot(toPoint, cameraForward);
    }
    else // Look at Center (mode 0)
    {
        float sign = Flip > 0.5 ? -1 : 1;
        newRot = qLookAt(normalize(Center - p.Position) * sign, normalize(UpVector));
        
        float3 forward = qRotateVec3(float3(0, 0, 1), newRot);
        float4 alignment = qFromAngleAxis(3.141578, forward);
        newRot = qMul(alignment, newRot);
        
        distanceToCamera = 1.0;
    }

    p.Rotation = normalize(qSlerp(normalize(p.Rotation), normalize(newRot), weight));

    // Apply screen space scaling if enabled and in screen space mode
    if (ScaleMode > 0.5 && BillboardMode > 1.5)
    {
        float fov = radians(FOVFactor);
        // For perspective-correct scaling, we need to account for the field of view
        // The scale should be proportional to distance to maintain constant screen size
        float scaleFactor = BaseScale * distanceToCamera * fov;

        // Alternative: Use the clip space transformation to get proper scaling
        // Transform point to clip space to see its actual screen size
        float4 clipPos = mul(float4(p.Position, 1.0), WorldToClipSpace);
        float w = clipPos.w;
        
        // Scale factor based on perspective division
        // This should give us consistent screen size regardless of distance
        scaleFactor = BaseScale * w * fov;
        
        p.FX1 = float3(scaleFactor, scaleFactor, scaleFactor) * originalFX1;
    }
    else
    {
        p.FX1 = originalFX1;
    }

    ResultPoints[i.x] = p;
}