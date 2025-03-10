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
using T3.Core.Utils.Geometry;

namespace T3.Operators.Types.Id_d83ac768_295f_46b8_aff3_3c87098e36f4
{
    public class TriangleMesh : Instance<TriangleMesh>
    {
        [Output(Guid = "2987f159-17e8-4ec4-826a-3a3e0e899675")]
        public readonly Slot<MeshBuffers> Data = new();

        public TriangleMesh()
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
                    // For more than 3 points, we create a complete fan
                    triangleCount = verticesCount-1;
                }

                Log.Debug($"Creating {triangleCount} triangles from {verticesCount} points", this);

                // Resize buffers if needed
                if (_vertexBufferData.Length != verticesCount)
                    _vertexBufferData = new PbrVertex[verticesCount];

                if (_indexBufferData.Length != triangleCount)
                    _indexBufferData = new Int3[triangleCount];

                if (verticesCount == 3)
                {
                    // Special case for exactly 3 points - just one triangle
                    _indexBufferData[0] = new Int3(0, 1, 2);
                }
                else
                {
                    // Create triangles in a fan pattern
                    for (int i = 0; i < verticesCount - 2; i++)
                    {
                        _indexBufferData[i] = new Int3(0, i + 1, i + 2);
                    }

                    // Add the closing triangle
                    _indexBufferData[verticesCount - 2] = new Int3(0, verticesCount - 1, 1);
                }

                // Calculate normals, tangents, and bitangents
                var edge1 = points[1] - points[0];
                var edge2 = points[2] - points[0];
                var normal = Vector3.Normalize(Vector3.Cross(edge1, edge2));
                var tangent = Vector3.Normalize(edge1);
                var binormal = Vector3.Normalize(Vector3.Cross(normal, tangent));

                // Fill vertex buffer
                for (int i = 0; i < verticesCount; i++)
                {
                    Vector2 texcoord;

                    if (verticesCount == 3)
                    {
                        // For exactly 3 points, use the original UV mapping
                        texcoord = i switch
                        {
                            0 => new Vector2(0.5f, 0.5f),  // First vertex
                            1 => new Vector2(0.0f, 0.0f),  // Second vertex
                            2 => new Vector2(1.0f, 0.0f),  // Third vertex
                            _ => new Vector2(0.0f, 0.0f)   // Default, shouldn't happen
                        };
                    }
                    else
                    {
                        // For more than 3 points
                        if (i == 0)
                        {
                            // Center point
                            texcoord = new Vector2(0.5f, 0.5f);
                        }
                        else
                        {
                            // Distribute points around the edge of a circle
                            float angle = (float)(i - 1) / (verticesCount - 1) * MathF.PI * 2;
                            texcoord = new Vector2(0.5f + 0.5f * MathF.Cos(angle), 0.5f + 0.5f * MathF.Sin(angle));
                        }
                    }

                    _vertexBufferData[i] = new PbrVertex
                    {
                        Position = points[i],
                        Normal = normal,
                        Tangent = tangent,
                        Bitangent = binormal,
                        Texcoord = texcoord,
                        Selection = 1,
                    };
                }

                // Write Data
                ResourceManager.SetupStructuredBuffer(_vertexBufferData, PbrVertex.Stride * verticesCount, PbrVertex.Stride, ref _vertexBuffer);
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