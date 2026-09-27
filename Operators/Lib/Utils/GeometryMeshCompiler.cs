#nullable enable
using System;
using System.Numerics;
using T3.Core.DataTypes.Geometry;
using T3.Core.Rendering;
using T3.Core.Utils.Geometry;

namespace Lib.Utils;

/// <summary>
/// Compiles a MeshGeometry into packed PbrVertex/triangle arrays: N-gons are fan
/// triangulated, corner attributes resolved, tangent bases computed. One vertex per
/// corner, so a part's contiguous face range maps to contiguous vertex and triangle
/// ranges - that is what makes the per-part chunk table possible. Buffers are kept
/// between calls; use one compiler per op.
/// </summary>
internal sealed class GeometryMeshCompiler
{
    public PbrVertex[] Vertices { get; private set; } = [];
    public Int3[] Triangles { get; private set; } = [];

    /// <summary>One chunk per part in part order; a single whole-mesh chunk for part-less geometry.</summary>
    public MeshChunkDef[] Chunks { get; private set; } = [];

    private readonly record struct VertexKey(
    int Point,
    int Nx, int Ny, int Nz,
    int Tx, int Ty, int Tz, int Tw,
    int U, int V,
    int R, int G, int B, int A);

    private readonly Dictionary<VertexKey, int> _weldLookup = new();
    private PbrVertex[] _vertexScratch = [];

    private static int Quantize(float v) => (int)MathF.Round(v * 1e5f);
    private static VertexKey MakeKey(int point, Vector3 n, Vector4 t, Vector2 uv, Vector4 c) =>
    new(point,
        Quantize(n.X), Quantize(n.Y), Quantize(n.Z),
        Quantize(t.X), Quantize(t.Y), Quantize(t.Z), Quantize(t.W),
        Quantize(uv.X), Quantize(uv.Y),
        Quantize(c.X), Quantize(c.Y), Quantize(c.Z), Quantize(c.W));

    /// <summary>
    /// Compiles <paramref name="geometry"/>. With <paramref name="relativeToPartPivots"/> the vertex
    /// positions of every part are expressed relative to its pivot, so a chunk draw can place
    /// the part with a point transform.
    /// </summary>
    public void Compile(MeshGeometry geometry, bool relativeToPartPivots)
    {
        var cornerCount = geometry.CornerCount;
        var triangleCount = geometry.GetTriangleCount();
        if (Vertices.Length != cornerCount)
            Vertices = new PbrVertex[cornerCount];
        if (Triangles.Length != triangleCount)
            Triangles = new Int3[triangleCount];

        // Attribute lookups once, outside the loops
        geometry.Attributes.TryGet<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, out var cornerNormals);
        geometry.Attributes.TryGet<Vector4>(GeometryAttributeNames.Tangent, AttributeDomain.Corner, out var cornerTangents);
        geometry.Attributes.TryGet<Vector2>(GeometryAttributeNames.TexCoord, AttributeDomain.Corner, out var cornerUvs);
        geometry.Attributes.TryGet<Vector4>(GeometryAttributeNames.Color, AttributeDomain.Corner, out var cornerColors);

        // Coarser color domains are promoted to corners here: face color, else part color
        GeometryAttribute<Vector4>? faceColors = null;
        GeometryAttribute<Vector4>? partColors = null;
        if (cornerColors == null
            && !geometry.Attributes.TryGet(GeometryAttributeNames.Color, AttributeDomain.Face, out faceColors))
        {
            geometry.Attributes.TryGet(GeometryAttributeNames.Color, AttributeDomain.Part, out partColors);
        }

        if (partColors != null)
            BuildFaceToPartMap(geometry);

        var positions = geometry.Positions;
        var offsets = geometry.FaceCornerOffsets;
        var cornerPoints = geometry.CornerPointIndices;
        var parts = geometry.Parts;
        var partCount = parts.Length > 0 ? parts.Length : 1;

        if (Triangles.Length != triangleCount)
            Triangles = new Int3[triangleCount];
        if (_vertexScratch.Length < geometry.CornerCount)
            _vertexScratch = new PbrVertex[geometry.CornerCount];

        if (Chunks.Length != partCount)
            Chunks = new MeshChunkDef[partCount];

        var vertexCount = 0;
        var triangleIndex = 0;
        var localIndices = new int[16]; // grows as needed per face

        for (var partIndex = 0; partIndex < partCount; partIndex++)
        {
            int faceStart, faceEnd;
            var pivot = Vector3.Zero;
            if (parts.Length > 0)
            {
                faceStart = parts[partIndex].FaceStart;
                faceEnd = Math.Min(faceStart + parts[partIndex].FaceCount, geometry.FaceCount);
                pivot = parts[partIndex].Pivot;
            }
            else
            {
                faceStart = 0;
                faceEnd = geometry.FaceCount;
            }

            var partVertexStart = vertexCount;
            var partTriangleStart = triangleIndex;
            _weldLookup.Clear();

            for (var faceIndex = faceStart; faceIndex < faceEnd; faceIndex++)
            {
                var start = offsets[faceIndex];
                var end = offsets[faceIndex + 1];
                var faceCornerCount = end - start;
                if (faceCornerCount < 3)
                    continue;

                // Face normal (unchanged Newell code)
                var faceNormal = Vector3.Zero;
                for (var c = start; c < end; c++)
                {
                    var next = c + 1 == end ? start : c + 1;
                    var p0 = positions[cornerPoints[c]];
                    var p1 = positions[cornerPoints[next]];
                    faceNormal += new Vector3((p0.Y - p1.Y) * (p0.Z + p1.Z),
                                              (p0.Z - p1.Z) * (p0.X + p1.X),
                                              (p0.X - p1.X) * (p0.Y + p1.Y));
                }
                faceNormal = faceNormal.LengthSquared() > 1e-10f ? Vector3.Normalize(faceNormal) : Vector3.UnitY;

                var faceColor = Vector4.One;
                if (faceColors != null)
                    faceColor = faceColors.Values[faceIndex];
                else if (partColors != null)
                    faceColor = _faceToPart[faceIndex] >= 0 ? partColors.Values[_faceToPart[faceIndex]] : Vector4.One;

                // Face-level TBN fallback from the first triangle. Only used when the geometry
                // carries no per-corner Tangent attribute (e.g. geometry not produced by
                // CurvesToGeometry, or with UVs it can actually derive a frame from).
                var faceTangent = Vector3.Zero;
                var faceBitangent = Vector3.Zero;
                var needFaceTbn = cornerTangents == null;
                if (needFaceTbn)
                {
                    var c0 = start;
                    var c1 = start + 1;
                    var c2 = start + 2;
                    MeshUtils.CalcTBNSpace(positions[cornerPoints[c0]], cornerUvs?.Values[c0] ?? Vector2.Zero,
                                           positions[cornerPoints[c1]], cornerUvs?.Values[c1] ?? Vector2.UnitX,
                                           positions[cornerPoints[c2]], cornerUvs?.Values[c2] ?? Vector2.One,
                                           faceNormal, out faceTangent, out faceBitangent);
                    if (faceTangent.LengthSquared() < 1e-10f || float.IsNaN(faceTangent.X))
                    {
                        faceTangent = Vector3.Normalize(Vector3.Cross(faceNormal, Math.Abs(faceNormal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));
                        faceBitangent = Vector3.Cross(faceNormal, faceTangent);
                    }
                }

                if (localIndices.Length < faceCornerCount)
                    localIndices = new int[faceCornerCount];

                for (var i = 0; i < faceCornerCount; i++)
                {
                    var c = start + i;
                    var normal = cornerNormals != null ? cornerNormals.Values[c] : faceNormal;
                    var uv = cornerUvs != null ? cornerUvs.Values[c] : Vector2.Zero;
                    var color = cornerColors != null ? cornerColors.Values[c] : faceColor;
                    var pointIndex = cornerPoints[c];

                    // Resolve this corner's tangent frame.
                    Vector3 tangent;
                    Vector3 bitangent;
                    Vector4 tangent4;
                    if (cornerTangents != null)
                    {
                        var t4 = cornerTangents.Values[c];
                        tangent = new Vector3(t4.X, t4.Y, t4.Z);

                        // Re-orthogonalize against the corner normal so the tangent stays in-plane,
                        // even if the authored tangent and the (possibly smoothed) normal drifted apart.
                        tangent = Vector3.Normalize(tangent - normal * Vector3.Dot(normal, tangent));
                        if (float.IsNaN(tangent.X) || tangent.LengthSquared() < 1e-10f)
                            tangent = Vector3.Normalize(Vector3.Cross(normal, Math.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX));

                        bitangent = Vector3.Cross(normal, tangent) * t4.W;
                        tangent4 = new Vector4(tangent, t4.W);
                    }
                    else
                    {
                        tangent = faceTangent;
                        bitangent = faceBitangent;
                        // Recover a sign relative to the corner normal so the key is stable per corner.
                        var sign = Vector3.Dot(Vector3.Cross(normal, tangent), bitangent) < 0 ? -1f : 1f;
                        tangent4 = new Vector4(tangent, sign);
                    }

                    var key = MakeKey(pointIndex, normal, tangent4, uv, color);
                    if (_weldLookup.TryGetValue(key, out var existing))
                    {
                        localIndices[i] = existing;
                        continue;
                    }

                    var position = positions[pointIndex];
                    if (relativeToPartPivots)
                        position -= pivot;

                    var vi = vertexCount++;
                    if (vi >= _vertexScratch.Length)
                        Array.Resize(ref _vertexScratch, _vertexScratch.Length * 2);
                    _vertexScratch[vi] = new PbrVertex
                    {
                        Position = position,
                        Normal = normal,
                        Texcoord = uv,
                        Texcoord2 = uv,
                        Selection = 1,
                        ColorRgb = new Vector3(color.X, color.Y, color.Z),
                        Tangent = tangent,
                        Bitangent = bitangent,
                    };
                    _weldLookup[key] = vi;
                    localIndices[i] = vi;
                }

                for (var i = 1; i < faceCornerCount - 1; i++)
                {
                    Triangles[triangleIndex++] = new Int3(localIndices[0], localIndices[i], localIndices[i + 1]);
                }
            }

            Chunks[partIndex] = new MeshChunkDef
            {
                StartFaceIndex = partTriangleStart,
                FaceCount = triangleIndex - partTriangleStart,
                StartVertexIndex = partVertexStart,
                VertexCount = vertexCount - partVertexStart,
            };
        }

        if (Vertices.Length != vertexCount)
            Vertices = new PbrVertex[vertexCount];
        Array.Copy(_vertexScratch, Vertices, vertexCount);
    }

    private void BuildChunks(MeshGeometry geometry, bool relativeToPartPivots)
    {
        var parts = geometry.Parts;
        var offsets = geometry.FaceCornerOffsets;
        var vertices = Vertices;

        if (parts.Length == 0)
        {
            if (Chunks.Length != 1)
                Chunks = new MeshChunkDef[1];

            Chunks[0] = new MeshChunkDef
            {
                StartFaceIndex = 0,
                FaceCount = Triangles.Length,
                StartVertexIndex = 0,
                VertexCount = vertices.Length,
            };
            return;
        }

        if (Chunks.Length != parts.Length)
            Chunks = new MeshChunkDef[parts.Length];

        var triangleStart = 0;
        for (var partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            var part = parts[partIndex];
            var faceEnd = Math.Min(part.FaceStart + part.FaceCount, geometry.FaceCount);
            var cornerStart = offsets[part.FaceStart];
            var cornerEnd = offsets[faceEnd];

            var triangleCount = 0;
            for (var faceIndex = part.FaceStart; faceIndex < faceEnd; faceIndex++)
            {
                var corners = offsets[faceIndex + 1] - offsets[faceIndex];
                if (corners >= 3)
                    triangleCount += corners - 2;
            }

            if (relativeToPartPivots)
            {
                var pivot = part.Pivot;
                for (var c = cornerStart; c < cornerEnd; c++)
                {
                    vertices[c].Position -= pivot;
                }
            }

            Chunks[partIndex] = new MeshChunkDef
            {
                StartFaceIndex = triangleStart,
                FaceCount = triangleCount,
                StartVertexIndex = cornerStart,
                VertexCount = cornerEnd - cornerStart,
            };
            triangleStart += triangleCount;
        }
    }

    private void BuildFaceToPartMap(MeshGeometry geometry)
    {
        if (_faceToPart.Length != geometry.FaceCount)
            _faceToPart = new int[geometry.FaceCount];
        Array.Fill(_faceToPart, -1);

        var parts = geometry.Parts;
        for (var partIndex = 0; partIndex < parts.Length; partIndex++)
        {
            var part = parts[partIndex];
            var end = Math.Min(part.FaceStart + part.FaceCount, geometry.FaceCount);
            for (var faceIndex = part.FaceStart; faceIndex < end; faceIndex++)
            {
                _faceToPart[faceIndex] = partIndex;
            }
        }
    }

    private int[] _faceToPart = [];
}
