#include "shared/hash-functions.hlsl"
#include "shared/noise-functions.hlsl"
#include "shared/point.hlsl"
#include "shared/quat-functions.hlsl"
#include "shared/pbr.hlsl"

cbuffer Params : register(b0)
{
    float UvMode;
    float CloseFan; // 0 = open fan, 1 = closed fan
}

StructuredBuffer<Point> Points : t0;

RWStructuredBuffer<PbrVertex> Vertices : u0;
RWStructuredBuffer<int3> TriangleIndices : u1;

[numthreads(80, 1, 1)] void main(uint3 i : SV_DispatchThreadID)
{
    uint triangleCount, stride;
    TriangleIndices.GetDimensions(triangleCount, stride);

    uint vertexCount;
    Vertices.GetDimensions(vertexCount, stride);

    uint id = i.x;

    if (id >= vertexCount)
    {
        return;
    }

    uint pointCount, Stride;
    Points.GetDimensions(pointCount, Stride);
    
    // Triangle fan logic:
    // - 3 points = 1 triangle (0, 1, 2) - automatically closed
    // - 4 points = 2 base triangles (0,1,2), (0,2,3) + optional closing (0,3,1)
    // - 5 points = 3 base triangles (0,1,2), (0,2,3), (0,3,4) + optional closing (0,4,1)
    // - n points = (n-2) base triangles + optional 1 closing triangle
    
    if (pointCount < 3)
    {
        // Not enough points to make a triangle
        return;
    }

    uint baseTriangles = pointCount - 2; // Triangles without closing
    uint trianglesTotal;

    // Calculate which triangle and which vertex within that triangle
    uint vertexId = id % 3; // 0, 1, or 2 for triangle vertices
    
    uint triangleId ;
    
    if (pointCount == 3)
    {
       
        triangleId = id;
        trianglesTotal = 1; // Only one triangle, inherently closed
    }
    else
    {
        // 4+ points: base triangles + optional closing triangle
        trianglesTotal = baseTriangles + (CloseFan > 0.5 ? 1 : 0);
        triangleId = id / 3;
    }
    
    
    
    // Skip if we're beyond valid triangles
    if (triangleId >= trianglesTotal)
    {
        PbrVertex v;
        v.Position = float3(0, 0, 0);
        v.Normal = float3(0, 0, 1);
        v.Tangent = float3(1, 0, 0);
        v.Bitangent = float3(0, 0, 1);
        v.TexCoord = float2(0, 0);
        v.TexCoord2 = float2(0, 0);
        v.Selected = 0;
        v.__padding = float3(0, 0, 0);
        Vertices[id] = v;
        
        // Clear triangle index for this vertex's triangle
        if (vertexId == 0 && triangleId < triangleCount)
        {
            TriangleIndices[triangleId] = int3(0, 0, 0);
        }
        return;
    }

    uint pointIndex;
    
    // Check if this is the closing triangle
    bool isClosingTriangle = (pointCount > 3) && (CloseFan > 0.5) && (triangleId == baseTriangles);
    
    if (isClosingTriangle)
    {
        // Closing triangle: connects last point back to first edge point
        // Triangle pattern: (0, n-1, 1)
        if (vertexId == 0)
        {
            pointIndex = 0; // Center
        }
        else if (vertexId == 1)
        {
            pointIndex = pointCount - 1; // Last point
        }
        else // vertexId == 2
        {
            pointIndex = 1; // First edge point (closes the loop)
        }
    }
    else
    {
        // Regular triangle fan triangles
        // Triangle 0: points 0, 1, 2
        // Triangle 1: points 0, 2, 3
        // Triangle 2: points 0, 3, 4
        // Triangle n: points 0, n+1, n+2
        
        if (vertexId == 0)
        {
            // Always use the center point (point 0)
            pointIndex = 0;
        }
        else if (vertexId == 1)
        {
            // First edge point of this triangle
            pointIndex = 1 + triangleId;
        }
        else // vertexId == 2
        {
            // Second edge point of this triangle
            pointIndex = 2 + triangleId;
        }
    }

    // Ensure we don't go out of bounds
    if (pointIndex >= pointCount)
    {
        PbrVertex v;
        v.Position = float3(0, 0, 0);
        v.Normal = float3(0, 0, 1);
        v.Tangent = float3(1, 0, 0);
        v.Bitangent = float3(0, 0, 1);
        v.TexCoord = float2(0, 0);
        v.TexCoord2 = float2(0, 0);
        v.Selected = 0;
        v.__padding = float3(0, 0, 0);
        Vertices[id] = v;
        
        // Clear triangle index for this vertex's triangle
        if (vertexId == 0 && triangleId < triangleCount)
        {
            TriangleIndices[triangleId] = int3(0, 0, 0);
        }
        return;
    }

    Point p = Points[pointIndex];
    float3 pointPos = p.Position;

    // Calculate UV coordinates based on vertex position in the triangle fan
    float2 uv;
    
    switch ((uint)UvMode)
    {
    case 0:
        // All triangles share the same UVs
        if(vertexId == 0)
        {
            uv = float2(0.5f, 1.0f); // Center point
        }
        else if(vertexId == 1)
        {
            uv = float2(0.067f, 0.250f); // First edge point
        }
        else
        {
            uv = float2(0.933f, 0.250f); // Second edge point
        }
        break;
    case 1:
        // Equilateral triangles in UV space
        if (pointIndex == 0)
        {
            // Center point
            uv.x = 0.5;
            uv.y = 0.5;
        }
        else
        {
            // Edge points arranged in a circle to form equilateral triangles
            // Calculate angle for this edge point (excluding center point 0)
            uint edgePointIndex = pointIndex - 1; // 0, 1, 2, ... for edge points
            uint totalEdgePoints = pointCount - 1; // Total number of edge points
            
            // Angle for this point (in radians)
            float angle = (float)edgePointIndex * 2.0 * 3.14159265359 / (float)totalEdgePoints;
            
            // Place on a circle with radius 0.5 (to fill UV space from 0 to 1)
            uv.x = 0.5 + 0.5 * cos(angle);
            uv.y = 0.5 + 0.5 * sin(angle);
        }
        break;
    default:
        uv = float2(0, 0);
        break;
    }

    // Calculate normal for the triangle (simple approach - all point up)
    float3 normal = float3(0, 0, 1);
    float3 tangent = float3(1, 0, 0);
    float3 bitangent = float3(0, 0, 1);

    // Build the vertex
    PbrVertex v;
    v.Position = pointPos;
    v.Normal = normal;
    v.Tangent = tangent;
    v.Bitangent = bitangent;
    v.TexCoord = uv;
    v.TexCoord2 = float2(0, 0);
    v.Selected = 1;
    v.__padding = float3(0, 0, 0);

    // Write the vertex
    Vertices[id] = v;
    
    // CRITICAL: Write triangle indices
    // Only write the triangle once per triangle (when processing the first vertex)
    if (vertexId == 0 && triangleId < triangleCount)
    {
        // Calculate the actual vertex indices for this triangle
        uint idx0 = triangleId * 3 + 0; // Center point vertex
        uint idx1 = triangleId * 3 + 1; // First edge point vertex
        uint idx2 = triangleId * 3 + 2; // Second edge point vertex
        
        // Write the triangle indices
        TriangleIndices[triangleId] = int3(idx0, idx1, idx2);
    }
}
