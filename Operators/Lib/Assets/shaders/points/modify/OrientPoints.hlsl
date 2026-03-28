#include "shared/hash-functions.hlsl"
#include "shared/noise-functions.hlsl"
#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"

cbuffer FloatParams : register(b0)
{
    float3 Target;
    float Amount;
    float3 UpVector;
}

cbuffer IntParams : register(b1)
{
    int UseWAsWeight;
    int Flip;
    int AmountFactor;
    int OrientationMode; // 0 = Look at Center, 2 = screen space, 3 = Billboard to camera
}

cbuffer Transforms : register(b2)
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
StructuredBuffer<Point> PreviousSourcePoints : t1;
RWStructuredBuffer<Point> ResultPoints : u0;

[numthreads(64, 1, 1)] void main(uint3 i : SV_DispatchThreadID)
{
    uint numStructs, stride;
    SourcePoints.GetDimensions(numStructs, stride);
    if (i.x >= numStructs)
    {
        return;
    }

    Point p = SourcePoints[i.x];

    p.Position = p.Position;

    float strength = Amount * (AmountFactor == 0
                                   ? 1
                               : (AmountFactor == 1) ? p.FX1
                                                     : p.FX2);

    float sign = Flip > 0.5 ? -1 : 1;
    
    
     switch (OrientationMode) {
        case 0: // Center
            {float4 newRot = qLookAt(normalize(Target - p.Position) * sign, normalize(UpVector));
             float3 forward = qRotateVec3(float3(0, 0, 1), newRot);
             float4 alignment = qFromAngleAxis(PI, forward);
             newRot = qMul(alignment, newRot);
             p.Rotation = normalize(qSlerp(normalize(p.Rotation), normalize(newRot), strength));
            }
        break;
           
        case 1: // Screen
            p.Rotation = normalize(qSlerp(p.Rotation,qFromMatrix3((float3x3)WorldToCamera), strength));
        break;

        case 2: // Billboard to camera    
            float3 up=mul(float3(0,-1,0),(float3x3)CameraToWorld);
            float3 dir=normalize(p.Position-CameraToWorld[3].xyz);
            p.Rotation = normalize(qSlerp(p.Rotation, qLookAt(-dir,up), strength));
        break;

        case 3: // Moving direction
        {

            Point pp = PreviousSourcePoints[i.x];
            float3 moveDir = p.Position - pp.Position;
            
           
            float3 upVector = UpVector;
            moveDir = normalize(moveDir) * sign;
            if (abs(dot(normalize(UpVector),normalize(moveDir))) > .8){
                upVector = float3(1,0,0);
            }  
            float4 newRot = qLookAt(moveDir, normalize(upVector));
            
            // Adjust orientation so Z+ points forward
            float3 forward = qRotateVec3(float3(0, 0, 1), newRot);
            float4 alignment = qFromAngleAxis(PI, forward); // Use PI constant
            newRot = qMul(alignment, newRot);
            strength *= smoothstep(0.0,0.001,length(moveDir));
            p.Rotation = normalize(qSlerp(normalize(ResultPoints[i.x].Rotation), normalize(newRot),  saturate(strength)));
           if (length(moveDir)<.01){
            p.Rotation = ResultPoints[i.x].Rotation;
           }
           // p.Rotation = ResultPoints[i.x].Rotation;
        }
        break;
        
    }

    ResultPoints[i.x] = p;
}
