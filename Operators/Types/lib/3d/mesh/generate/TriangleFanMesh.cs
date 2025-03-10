using System;
using SharpDX.Direct3D11;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.Logging;
using T3.Core.Operator;
using T3.Core.Operator.Attributes;
using T3.Core.Operator.Slots;
using T3.Core.Rendering;
using T3.Core.Resource;
using Buffer = SharpDX.Direct3D11.Buffer;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Point = T3.Core.DataTypes.Point;


namespace T3.Operators.Types.Id_d83ac768_295f_46b8_aff3_3c87098e36f4
{
    public class TriangleFanMesh : Instance<TriangleFanMesh>
    {
        [Output(Guid = "2987f159-17e8-4ec4-826a-3a3e0e899675")]
        public readonly Slot<MeshBuffers> Data = new();

        public TriangleFanMesh()
        {
            Data.UpdateAction = Update;
        }

        private void Update(EvaluationContext context)
        {
            try
            {
                var resourceManager = ResourceManager.Instance();

                var list = DataList.GetValue(context);
                if (list is not StructuredList<Point> pointList)
                {
                    Log.Warning($"{this} requires a structured point list", this);
                    return;
                }

                if (pointList.NumElements < 3)
                {
                    Log.Warning($"Point list needs at least 3 points to create a triangle", this);
                    return;
                }

                Log.Debug($"Number of points in the list: {pointList.NumElements}", this);

                // Extract points from the StructuredList<Point>
                var points = new Vector3[pointList.NumElements];
                for (int i = 0; i < pointList.NumElements; i++)
                {
                    if (i >= pointList.TypedElements.Length)
                    {
                        Log.Error($"Index {i} is out of bounds for TypedElements (length: {pointList.TypedElements.Length})", this);
                        return;
                    }

                    points[i] = pointList.TypedElements[i].Position;
                }

                // Calculate the number of triangles
                int verticesCount = points.Length;
                int triangleCount;

                if (verticesCount == 3)
                {
                    // Special case for exactly 3 points (one triangle)
                    triangleCount = 1;
                }
                else
                {
                    // For more than 3 points, we create a fan with N-1 triangles
                    triangleCount = verticesCount - 1;
                }

                Log.Debug($"Creating {triangleCount} triangles from {verticesCount} points", this);

                // For per-face normals, we need 3 vertices per triangle
                int totalVertices = triangleCount * 3;

                // Resize buffers if needed
                if (_vertexBufferData.Length != totalVertices)
                    _vertexBufferData = new PbrVertex[totalVertices];

                if (_indexBufferData.Length != triangleCount)
                    _indexBufferData = new Int3[triangleCount];

                // Create the triangles
                if (verticesCount == 3)
                {
                    // Special case for exactly 3 points - just one triangle
                    _indexBufferData[0] = new Int3(0, 1, 2);

                    // Compute normal for the triangle
                    var edge1 = points[1] - points[0];
                    var edge2 = points[2] - points[0];
                    var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                    var tangent = Vector3.Normalize(edge1);
                    var binormal = Vector3.Normalize(Vector3.Cross(normal, tangent));

                    // Assign vertices with original UV mapping
                    _vertexBufferData[0] = new PbrVertex
                    {
                        Position = points[0],
                        Normal = normal,
                        Tangent = tangent,
                        Bitangent = binormal,
                        Texcoord = new Vector2(0.5f, 0.5f),
                        Selection = 1,
                    };

                    _vertexBufferData[1] = new PbrVertex
                    {
                        Position = points[1],
                        Normal = normal,
                        Tangent = tangent,
                        Bitangent = binormal,
                        Texcoord = new Vector2(0.0f, 0.0f),
                        Selection = 1,
                    };

                    _vertexBufferData[2] = new PbrVertex
                    {
                        Position = points[2],
                        Normal = normal,
                        Tangent = tangent,
                        Bitangent = binormal,
                        Texcoord = new Vector2(1.0f, 0.0f),
                        Selection = 1,
                    };
                }
                else
                {
                    // For fan triangulation with per-face normals

                    // Precompute UV coordinates for all perimeter points
                    var uvCoordinates = new Vector2[verticesCount];
                    uvCoordinates[0] = new Vector2(0.5f, 0.5f); // Center point

                    for (int i = 1; i < verticesCount; i++)
                    {
                        // Distribute points evenly around a circle for UV mapping
                        float angle = (float)(i - 1) / (verticesCount - 1) * MathF.PI * 2;
                        uvCoordinates[i] = new Vector2(0.5f + 0.5f * MathF.Cos(angle), 0.5f + 0.5f * MathF.Sin(angle));
                    }

                    int vertexIndex = 0;

                    // Create triangles in a fan pattern (excluding the closing triangle)
                    for (int i = 0; i < verticesCount - 2; i++)
                    {
                        // Set index buffer (pointing to unique vertices)
                        _indexBufferData[i] = new Int3(vertexIndex, vertexIndex + 1, vertexIndex + 2);

                        // Compute normal for this specific triangle
                        var p0 = points[0];        // Center point
                        var p1 = points[i + 1];    // Current edge point
                        var p2 = points[i + 2];    // Next edge point

                        var edge1 = p1 - p0;
                        var edge2 = p2 - p0;
                        var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                        var tangent = Vector3.Normalize(edge1);
                        var binormal = Vector3.Normalize(Vector3.Cross(normal, tangent));

                        // Add three vertices for this triangle with the same normal
                        _vertexBufferData[vertexIndex] = new PbrVertex
                        {
                            Position = p0,
                            Normal = normal,
                            Tangent = tangent,
                            Bitangent = binormal,
                            Texcoord = uvCoordinates[0],  // Center
                            Selection = 1,
                        };

                        _vertexBufferData[vertexIndex + 1] = new PbrVertex
                        {
                            Position = p1,
                            Normal = normal,
                            Tangent = tangent,
                            Bitangent = binormal,
                            Texcoord = uvCoordinates[i + 1],
                            Selection = 1,
                        };

                        _vertexBufferData[vertexIndex + 2] = new PbrVertex
                        {
                            Position = p2,
                            Normal = normal,
                            Tangent = tangent,
                            Bitangent = binormal,
                            Texcoord = uvCoordinates[i + 2],
                            Selection = 1,
                        };

                        vertexIndex += 3;
                    }

                    // Add the closing triangle (connects the last point back to the second point)
                    int lastTriangleIndex = verticesCount - 2;
                    _indexBufferData[lastTriangleIndex] = new Int3(vertexIndex, vertexIndex + 1, vertexIndex + 2);

                    // Compute normal for the closing triangle
                    var cp0 = points[0];                 // Center point
                    var cp1 = points[verticesCount - 1]; // Last point
                    var cp2 = points[1];                 // First edge point (after center)

                    var cedge1 = cp1 - cp0;
                    var cedge2 = cp2 - cp0;
                    var cnormal = Vector3.Normalize(Vector3.Cross(cedge1, cedge2));
                    var ctangent = Vector3.Normalize(cedge1);
                    var cbinormal = Vector3.Normalize(Vector3.Cross(cnormal, ctangent));

                    // Add three vertices for the closing triangle
                    _vertexBufferData[vertexIndex] = new PbrVertex
                    {
                        Position = cp0,
                        Normal = cnormal,
                        Tangent = ctangent,
                        Bitangent = cbinormal,
                        Texcoord = uvCoordinates[0],  // Center
                        Selection = 1,
                    };

                    _vertexBufferData[vertexIndex + 1] = new PbrVertex
                    {
                        Position = cp1,
                        Normal = cnormal,
                        Tangent = ctangent,
                        Bitangent = cbinormal,
                        Texcoord = uvCoordinates[verticesCount - 1],  // Last perimeter point
                        Selection = 1,
                    };

                    _vertexBufferData[vertexIndex + 2] = new PbrVertex
                    {
                        Position = cp2,
                        Normal = cnormal,
                        Tangent = ctangent,
                        Bitangent = cbinormal,
                        Texcoord = uvCoordinates[1],  // First perimeter point
                        Selection = 1,
                    };
                }

                // Write Data
                ResourceManager.SetupStructuredBuffer(_vertexBufferData, PbrVertex.Stride * totalVertices, PbrVertex.Stride, ref _vertexBuffer);
                ResourceManager.CreateStructuredBufferSrv(_vertexBuffer, ref _vertexBufferWithViews.Srv);
                ResourceManager.CreateStructuredBufferUav(_vertexBuffer, UnorderedAccessViewBufferFlags.None, ref _vertexBufferWithViews.Uav);
                _vertexBufferWithViews.Buffer = _vertexBuffer;

                const int stride = 3 * 4;
                ResourceManager.SetupStructuredBuffer(_indexBufferData, stride * triangleCount, stride, ref _indexBuffer);
                ResourceManager.CreateStructuredBufferSrv(_indexBuffer, ref _indexBufferWithViews.Srv);
                ResourceManager.CreateStructuredBufferUav(_indexBuffer, UnorderedAccessViewBufferFlags.None, ref _indexBufferWithViews.Uav);
                _indexBufferWithViews.Buffer = _indexBuffer;

                _data.VertexBuffer = _vertexBufferWithViews;
                _data.IndicesBuffer = _indexBufferWithViews;
                Data.Value = _data;
                Data.DirtyFlag.Clear();

                Log.Debug($"Triangle mesh created successfully with {triangleCount} triangles", this);
            }
            catch (Exception e)
            {
                Log.Error("Failed to create triangle mesh: " + e.Message, this);
            }
        }

        private Buffer _vertexBuffer;
        private PbrVertex[] _vertexBufferData = Array.Empty<PbrVertex>();
        private readonly BufferWithViews _vertexBufferWithViews = new();

        private Buffer _indexBuffer;
        private Int3[] _indexBufferData = Array.Empty<Int3>();
        private readonly BufferWithViews _indexBufferWithViews = new();

        private readonly MeshBuffers _data = new();

        [Input(Guid = "6dce5a38-6d1f-48d0-811d-f2d3d39d5acd")]
        public readonly InputSlot<StructuredList> DataList = new();
    }
}