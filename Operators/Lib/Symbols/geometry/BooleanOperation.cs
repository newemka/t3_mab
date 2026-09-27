#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Numerics;
using Lib.Utils;
using LibTessDotNet;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Geometry;
using T3.Core.Logging;
using T3.Core.Utils;

namespace Lib.geometry;

/// <summary>
/// Boolean-combines closed MeshGeometry solids - union, difference, intersection. Each operand's
/// faces are split along the other solid's face planes, every fragment is classified as inside or
/// outside that solid by a point test, and the openings left by the removed faces are closed with
/// tessellated caps. All parts of one input count as that one solid, so multi-part geometry
/// (fracture chunks, separated letters) needs no merge step.
/// </summary>
/// <remarks>
/// Vertices live in one welded point table and are referenced by id, so a cut vertex exists once and
/// is shared by both faces that meet there. That identity is what keeps the pieces watertight: the
/// halves of a split edge, and the caps chained from them, cannot drift apart because there is only
/// ever one point.
/// </remarks>
[Guid("3f9a1d64-7c25-4b8e-9a13-6e5d84c07b21")]
[ExportDependencies("LibTessDotNet.dll")]
internal sealed class BooleanOperation : Instance<BooleanOperation>, IProgressProvider
{
    [Output(Guid = "b47c0e19-3a86-4d52-8f71-2c9e5b0d4138")]
    public readonly Slot<MeshGeometry> Result = new();

    [Output(Guid = "e13b7d05-6c48-42a9-95f0-8d2c4a71b6e3")]
    public readonly Slot<int> PartCount = new();

    public BooleanOperation()
    {
        Result.UpdateAction = Update;
        PartCount.UpdateAction = Update;
    }

    public bool TryGetProgress(out float progress) => _asyncComputation.TryGetUiProgress(out progress);

    private void Update(EvaluationContext context)
    {
        _operation = (Operations)Operation.GetValue(context).Clamp(0, 2);
        _splitIntoParts = SplitIntoParts.GetValue(context);

        var leftSource = Geometry.GetValue(context);
        var additional = Operands.GetCollectedTypedInputs();
        var rightSources = new List<MeshGeometry>(additional.Count);
        var hash = new HashCode();
        hash.Add(_operation);
        hash.Add(_splitIntoParts);
        hash.Add(leftSource?.Version ?? 0);
        hash.Add(leftSource?.GetHashCode() ?? 0);
        for (var i = 0; i < additional.Count; i++)
        {
            var source = additional[i].GetValue(context);
            rightSources.Add(source);
            if (source == null)
                continue;

            hash.Add(source.Version);
            hash.Add(source.GetHashCode());
        }

        if (leftSource == null || !HasAnythingToCombine(rightSources))
        {
            _asyncComputation.WaitForPending(Result);
            Result.Value = leftSource;
            PartCount.Value = leftSource?.Parts.Length ?? 0;
            return;
        }

        if (Async.GetValue(context))
        {
            var capturedLeft = leftSource;
            var capturedRights = rightSources.ToArray();
            var capturedOperation = _operation;
            var capturedSplit = _splitIntoParts;
            var result = _asyncComputation.Update(context, Result, hash.ToHashCode(),
                                                  token =>
                                                  {
                                                      var target = new MeshGeometry();
                                                      Evaluate(capturedLeft, capturedRights, capturedOperation, capturedSplit, target);
                                                      return target;
                                                  });
            Result.Value = result ?? _output;
            PartCount.Value = result?.Parts.Length ?? 0;
            return;
        }

        _asyncComputation.WaitForPending(Result);
        Evaluate(leftSource, rightSources, _operation, _splitIntoParts, _output);
        Result.Value = _output;
        PartCount.Value = _output.Parts.Length;
    }

    /// <summary>
    /// True when there is nothing to combine. With no right operand the op is a pass-through and
    /// hands the input on untouched - attributes and part table included - instead of rebuilding an
    /// equivalent mesh and dropping whatever the kernel does not carry.
    /// </summary>
    private static bool HasAnythingToCombine(List<MeshGeometry> rightSources)
    {
        for (var i = 0; i < rightSources.Count; i++)
        {
            if (rightSources[i] != null && rightSources[i].FaceCount > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Runs the whole boolean against plain geometries, without an operator instance or an
    /// evaluation context. The op's own Update goes through here too, so the diagnostics exercise
    /// the exact code path that ships rather than a parallel copy of it.
    /// </summary>
    internal static void Evaluate(MeshGeometry leftSource, IReadOnlyList<MeshGeometry> rightSources, Operations operation,
                                  bool splitIntoParts, MeshGeometry target)
    {
        // One weld for the whole operation, created before anything is loaded: the operands' own
        // points are registered in it too, so an id written to the output always names a position.
        var weld = new PointWeld(ExtentOf(leftSource, rightSources));
        var schema = new List<AttributeColumn>();
        var left = Solid.Load(leftSource, schema, weld);
        var solids = new List<Solid>(rightSources.Count);
        foreach (var right in rightSources)
        {
            solids.Add(Solid.Load(right, schema, weld));
        }

        var kernel = new Kernel(weld, schema.Count);
        var current = left.Faces;
        foreach (var solid in solids)
        {
            if (solid.Faces.Count == 0)
                continue;

            var rightTester = new SolidTester(solid);
            var leftTester = new SolidTester(left);
            var pieces = new List<Face>();

            // Coplanar faces are the shared-surface case: two operands meeting in one plane each have
            // a face there. Which of them (if either) is emitted depends on the operation, and getting
            // it wrong doubles a face - which shows up as non-manifold edges, not as a hole.
            var keepLeftCoplanar = new List<Face>();
            var keepRightCoplanar = new List<Face>();

            switch (operation)
            {
                case Operations.Union:
                {
                    // The interface lies inside the union, so neither side may contribute it.
                    kernel.Partition(current, solid.Planes, leftTester, KeepOutside, pieces, keepLeftCoplanar);
                    kernel.Partition(solid.Faces, left.Planes, rightTester, KeepOutside, pieces, keepRightCoplanar);
                    keepLeftCoplanar.Clear();
                    keepRightCoplanar.Clear();
                    break;
                }

                case Operations.Intersection:
                {
                    // The interface is on the result's boundary, so exactly one side contributes it.
                    kernel.Partition(current, solid.Planes, leftTester, KeepInside, pieces, keepLeftCoplanar);
                    kernel.Partition(solid.Faces, left.Planes, rightTester, KeepInside, pieces, null);
                    pieces.AddRange(keepLeftCoplanar);
                    kernel.CapOpenings(pieces);
                    break;
                }

                default:
                {
                    // A minus B: the interface is part of what gets removed, so neither side keeps it,
                    // and the right solid's inside is reversed to face the hole it closes.
                    kernel.Partition(current, solid.Planes, leftTester, KeepOutside, pieces, keepLeftCoplanar);
                    keepLeftCoplanar.Clear();
                    var cutAway = new List<Face>();
                    kernel.Partition(solid.Faces, left.Planes, rightTester, KeepInside, cutAway, keepRightCoplanar);
                    foreach (var face in cutAway)
                    {
                        face.Reversed = !face.Reversed;
                    }

                    pieces.AddRange(cutAway);
                    kernel.CapOpenings(pieces);
                    break;
                }
            }

            current = pieces;
        }

        kernel.Emit(target, current, splitIntoParts);
        WarnIfOpen(target);
    }

    /// <summary>A point test decides a fragment: outside the other solid, or inside it.</summary>
    private static bool KeepOutside(Classification classification) => classification == Classification.Outside;

    /// <summary>
    /// The smallest extent over all operands. One weld serves the whole operation, and its tolerance
    /// has to suit the finest of them - a small cutter must not be swallowed by a large model.
    /// </summary>
    private static float ExtentOf(MeshGeometry left, IReadOnlyList<MeshGeometry> rights)
    {
        var extent = ExtentOf(left);
        for (var i = 0; i < rights.Count; i++)
        {
            var candidate = ExtentOf(rights[i]);
            if (candidate > 0 && (extent <= 0 || candidate < extent))
                extent = candidate;
        }

        return extent;
    }

    private static float ExtentOf(MeshGeometry mesh)
    {
        if (mesh == null || mesh.PointCount == 0)
            return 0;

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var position in mesh.Positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return MathF.Max(max.X - min.X, MathF.Max(max.Y - min.Y, max.Z - min.Z));
    }
    private static bool KeepInside(Classification classification) => classification == Classification.Inside;

    private static void WarnIfOpen(MeshGeometry geometry)
    {
        var topology = new WeldedMeshTopology();
        topology.Measure(geometry, 1e-5f);
        if (topology.BoundaryEdges == 0 && topology.NonManifoldEdges == 0)
            return;

        Log.Warning($"BooleanOperation: the result has {topology.BoundaryEdges} open and {topology.NonManifoldEdges} non-manifold edges. "
                    + "The operands are expected to be closed solids; open or self-intersecting input leaves the cut faces unfilled.");
    }

    internal enum Operations
    {
        Union,
        Difference,
        Intersection,
    }

    private enum Classification
    {
        Outside,
        Inside,
    }

    private readonly MeshGeometry _output = new();
    private readonly AsyncComputation<MeshGeometry> _asyncComputation = new();
    private Operations _operation;
    private bool _splitIntoParts;

    [Input(Guid = "1a7c5e20-9b34-4d61-8f52-70c3e1a8d946")]
    public readonly InputSlot<MeshGeometry> Geometry = new();

    [Input(Guid = "6d2f8a13-4c70-49b5-b8e1-35a7d0c9264f")]
    public readonly MultiInputSlot<MeshGeometry> Operands = new();

    [Input(Guid = "c58b3e71-2d94-4a06-9f38-1b6e7c4d5a29", MappedType = typeof(Operations))]
    public readonly InputSlot<int> Operation = new();

    [Input(Guid = "9e14f7b6-8a02-4d53-b0c9-47e2a5b1c803")]
    public readonly InputSlot<bool> SplitIntoParts = new();

    [Input(Guid = "2b96c4d8-51e3-4f27-a6b0-83d1e95c7a46")]
    public readonly InputSlot<bool> Async = new();

    // ------------------------------------------------------------------ geometry primitives

    /// <summary>An inward half-space of a solid: every point with dot(n, p) - d &gt;= 0 is inside.</summary>
    private readonly record struct HalfSpace(Vector3 Normal, float Offset, float Weight)
    {
        public float Distance(Vector3 point) => Vector3.Dot(Normal, point) - Offset;
    }

    /// <summary>
    /// One corner. <see cref="PointId"/> is its identity in the welded table: two corners cut from
    /// the same place carry the same id, which is what keeps neighbouring faces meeting exactly.
    /// </summary>
    private struct Vert(Vector3 position, Vector3 normal, int pointId)
    {
        public Vector3 Position = position;
        public Vector3 Normal = normal;
        public int PointId = pointId;
        public float[] Attributes = [];
    }

    private sealed class Face(int cornerCount)
    {
        public Vert[] Vertices = new Vert[cornerCount];

        /// <summary>Source face on the input mesh, or -1 when the face was built by capping.</summary>
        public int SourceFace = -1;

        public bool Reversed;

        /// <summary>The plane of the other solid this fragment was split by, when there was one.</summary>
        public HalfSpace CutPlane;

        public int CornerCount => Vertices.Length;
    }

    /// <summary>
    /// An operand: its faces plus the inward half-spaces of those faces, which are the only planes a
    /// boundary crossing can lie in.
    /// </summary>
    private sealed class Solid
    {
        public List<Face> Faces = [];
        public HalfSpace[] Planes = [];

        public static Solid Load(MeshGeometry source, List<AttributeColumn> schema, PointWeld weld)
        {
            var solid = new Solid();
            if (source == null || source.FaceCount == 0)
                return solid;

            var columns = AttributeSchema.Resolve(source, schema);
            var hasNormals = source.Attributes.TryGet<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, out var normals);
            var offsets = source.FaceCornerOffsets;
            var corners = source.CornerPointIndices;
            var faces = new List<Face>(Math.Max(1, source.FaceCount));
            var planes = new List<HalfSpace>(Math.Max(1, source.FaceCount));

            // The input's own points go into the shared weld, so a corner keeps the point identity the
            // source gave it and the output can name a position that actually exists.
            var pointIds = new int[source.PointCount];
            for (var i = 0; i < source.PointCount; i++)
            {
                pointIds[i] = weld.GetOrAddPoint(source.Positions[i]);
            }

            for (var faceIndex = 0; faceIndex < source.FaceCount; faceIndex++)
            {
                var start = offsets[faceIndex];
                var end = offsets[faceIndex + 1];
                if (end - start < 3)
                    continue;

                var face = new Face(end - start) { SourceFace = faceIndex };
                for (var c = start; c < end; c++)
                {
                    var pointIndex = corners[c];
                    var normal = hasNormals ? normals.Values[c] : Vector3.Zero;
                    var attributes = columns == null ? [] : new float[columns.Count];
                    if (columns != null)
                    {
                        for (var column = 0; column < columns.Count; column++)
                        {
                            attributes[column] = columns[column].Values[c];
                        }
                    }

                    face.Vertices[c - start] = new Vert(source.Positions[pointIndex], normal, pointIds[pointIndex])
                    {
                        Attributes = attributes,
                    };
                }

                faces.Add(face);
                planes.Add(HalfSpaceOf(face));
            }

            solid.Faces = faces;
            solid.Planes = planes.ToArray();
            return solid;
        }

        /// <summary>
        /// The inward half-space of a face: north is its outward normal negated. Degenerate faces
        /// yield a zero weight, which callers skip.
        /// </summary>
        public static HalfSpace HalfSpaceOf(Face face)
        {
            var normal = NewellNormal(face);
            var length = normal.Length();
            if (length < 1e-20f)
                return default;

            normal = -normal / length;
            return new HalfSpace(normal, Vector3.Dot(normal, face.Vertices[0].Position), length);
        }

        public static Vector3 NewellNormal(Face face)
        {
            var normal = Vector3.Zero;
            var count = face.CornerCount;
            for (var i = 0; i < count; i++)
            {
                var p0 = face.Vertices[i].Position;
                var p1 = face.Vertices[(i + 1) % count].Position;
                normal += new Vector3((p0.Y - p1.Y) * (p0.Z + p1.Z),
                                      (p0.Z - p1.Z) * (p0.X + p1.X),
                                      (p0.X - p1.X) * (p0.Y + p1.Y));
            }

            return normal;
        }
    }

    /// <summary>Point-in-solid by ray parity, built from a solid's faces.</summary>
    private sealed class SolidTester
    {
        public SolidTester(Solid solid)
        {
            _tester = new MeshInsideTester(ToMesh(solid));
        }

        public bool IsInside(Vector3 point) => _tester.IsInside(point);

        private static MeshGeometry ToMesh(Solid solid)
        {
            var positions = new List<Vector3>();
            var cornerPoints = new List<int>();
            var offsets = new List<int> { 0 };
            foreach (var face in solid.Faces)
            {
                foreach (var vertex in face.Vertices)
                {
                    cornerPoints.Add(positions.Count);
                    positions.Add(vertex.Position);
                }

                offsets.Add(cornerPoints.Count);
            }

            var mesh = new MeshGeometry
            {
                Positions = positions.ToArray(),
                FaceCornerOffsets = offsets.ToArray(),
                CornerPointIndices = cornerPoints.ToArray(),
            };
            mesh.InvalidateTopologyCaches();
            return mesh;
        }

        private readonly MeshInsideTester _tester;
    }

    // ------------------------------------------------------------------ the kernel

    /// <summary>
    /// Splits faces along the other solid's planes, decides each fragment by a point test, and closes
    /// what was cut open. Every new vertex goes through one weld, so the pieces stay stitched together.
    /// </summary>
    private sealed class Kernel
    {
        public Kernel(PointWeld weld, int attributeCount)
        {
            _weld = weld;
            _attributeCount = attributeCount;
            _epsilon = weld.Extent * PlaneToleranceFactor;
        }

        /// <summary>
    /// Splits <paramref name="face"/> along every plane it crosses, then keeps the fragments
    /// <paramref name="keep"/> accepts.
    ///
    /// A fragment lying exactly in the other solid's plane goes to <paramref name="coincident"/>
    /// instead: the two surfaces meet there, and emitting both would double the face and its edge.
    /// Splitting is by plane crossing, not by classification, so a flat face sitting inside the other
    /// solid still gets its cut edges tagged for capping.
    /// </summary>
    public void Partition(List<Face>? source, HalfSpace[] planes, SolidTester tester, Func<Classification, bool> keep, List<Face> target,
                          List<Face>? coincident)
        {
            if (source == null)
                return;

            var beforeKept = target.Count;
            var beforeCoincident = coincident?.Count ?? 0;
            _planesTouched = 0;
            _planesFullyOn = 0;
            foreach (var face in source)
            {
                Partition(face, planes, 0, tester, keep, target, coincident, 0);
            }

            Log.Debug($"BooleanOperation[{KernelVersion}]: partition in={source.Count} kept={target.Count - beforeKept} "
                      + $"coincident={(coincident?.Count ?? 0) - beforeCoincident} planes={planes.Length} epsilon={_epsilon:G4} "
                      + $"touched={_planesTouched} fullyOn={_planesFullyOn}", this);
        }

        private void Partition(Face face, HalfSpace[] planes, int planeIndex, SolidTester tester, Func<Classification, bool> keep,
                               List<Face> target, List<Face>? coincident, int depth)
        {
            if (depth > MaxSplitDepth || face.CornerCount < 3)
                return;

            for (var i = planeIndex; i < planes.Length; i++)
            {
                var plane = planes[i];
                if (plane.Weight <= 0)
                    continue;

                if (LiesInPlane(face, plane))
                {
                    coincident?.Add(face);
                    return;
                }

                var split = SplitByHalfSpace(face, plane);
                if (split.Front == null || split.Back == null)
                    continue;

                // The plane genuinely divides the face: both halves need the rest of the planes.
                Partition(split.Front, planes, i + 1, tester, keep, target, coincident, depth + 1);
                Partition(split.Back, planes, i + 1, tester, keep, target, coincident, depth + 1);
                return;
            }

            if (keep(Classify(face, tester)))
                target.Add(face);
        }

        /// <summary>
        /// Whether the whole face lies in the plane, within the same tolerance the splitter uses.
        /// Every corner has to be on it: a face that merely touches the plane at one vertex - which
        /// two boxes meeting corner to corner do on four of their faces - is not the shared surface.
        /// </summary>
        private bool LiesInPlane(Face face, in HalfSpace plane)
        {
            var onPlane = 0;
            foreach (var vertex in face.Vertices)
            {
                if (MathF.Abs(plane.Distance(vertex.Position)) <= _epsilon)
                    onPlane++;
            }

            if (onPlane > 0)
            {
                _planesTouched++;
                if (onPlane == face.CornerCount)
                {
                    _planesFullyOn++;
                    var first = face.Vertices[0].Position;
                    Log.Debug($"BooleanOperation[{KernelVersion}]:   face({first.X:F4},{first.Y:F4},{first.Z:F4}) lies in "
                              + $"n=({plane.Normal.X:F3},{plane.Normal.Y:F3},{plane.Normal.Z:F3}) d={plane.Offset:F4}; "
                              + $"distances={Describe(face, plane)}", this);
                }
            }

            return onPlane == face.CornerCount;
        }

        /// <summary>Temporary: every corner's distance to the plane, for the face that was declared coplanar.</summary>
        private static string Describe(Face face, in HalfSpace plane)
        {
            var parts = new string[face.CornerCount];
            for (var i = 0; i < face.CornerCount; i++)
            {
                var position = face.Vertices[i].Position;
                parts[i] = $"({position.X:F3},{position.Y:F3},{position.Z:F3})={plane.Distance(position):F4}";
            }

            return string.Join(" ", parts);
        }

        /// <summary>
        /// A fragment's verdict. Its own vertices are tested first; when they disagree (a fragment
        /// straddling the boundary because the other solid is concave), the centroid breaks the tie.
        /// </summary>
        private static Classification Classify(Face face, SolidTester tester)
        {
            var insideVotes = 0;
            foreach (var vertex in face.Vertices)
            {
                if (tester.IsInside(vertex.Position))
                    insideVotes++;
            }

            if (insideVotes == 0)
                return Classification.Outside;

            if (insideVotes == face.CornerCount)
                return Classification.Inside;

            var centroid = Vector3.Zero;
            foreach (var vertex in face.Vertices)
            {
                centroid += vertex.Position;
            }

            return tester.IsInside(centroid / face.CornerCount) ? Classification.Inside : Classification.Outside;
        }

        /// <summary>
        /// Splits a face by a half-space. Returns a null half when the face does not cross the plane,
        /// so a caller can tell "no split" from "an empty fragment".
        /// </summary>
        private Split SplitByHalfSpace(Face face, in HalfSpace plane)
        {
            var count = face.CornerCount;
            var front = new List<Vert>(count + 2);
            var back = new List<Vert>(count + 2);
            var frontAttributes = new List<float>();
            var backAttributes = new List<float>();
            var hasFront = false;
            var hasBack = false;
            for (var i = 0; i < count; i++)
            {
                var current = face.Vertices[i];
                var next = face.Vertices[(i + 1) % count];
                var currentInside = plane.Distance(current.Position) >= -_epsilon;
                var nextInside = plane.Distance(next.Position) >= -_epsilon;
                if (currentInside)
                {
                    hasFront = true;
                }
                else
                {
                    hasBack = true;
                }

                if (currentInside)
                {
                    Append(front, frontAttributes, current);
                }
                else
                {
                    Append(back, backAttributes, current);
                }

                if (currentInside == nextInside)
                    continue;

                // The cut point comes from the shared table, so the face on the other side of this
                // edge lands on the same point id instead of a near-duplicate.
                var crossing = CrossingVertex(current, next, plane);
                Append(front, frontAttributes, crossing);
                Append(back, backAttributes, crossing);
            }

            if (!hasFront || !hasBack)
                return default;

            _ = frontAttributes;
            _ = backAttributes;
            var frontFace = Finish(face, front, plane);
            var backFace = Finish(face, back, plane);
            return new Split(frontFace, backFace);
        }

        private void Append(List<Vert> vertices, List<float> attributes, in Vert vertex)
        {
            vertices.Add(vertex);
            for (var i = 0; i < vertex.Attributes.Length; i++)
            {
                attributes.Add(vertex.Attributes[i]);
            }
        }

        /// <summary>
        /// The point where an edge meets the plane, taken from the weld so both faces sharing the edge
        /// get one point. Its attributes are lerped across the edge.
        /// </summary>
        private Vert CrossingVertex(in Vert a, in Vert b, in HalfSpace plane)
        {
            var aDistance = plane.Distance(a.Position);
            var bDistance = plane.Distance(b.Position);
            var denominator = aDistance - bDistance;
            var t = MathF.Abs(denominator) < 1e-20f ? 0.5f : Math.Clamp(aDistance / denominator, 0f, 1f);
            var position = Vector3.Lerp(a.Position, b.Position, t);
            var normal = Vector3.Lerp(a.Normal, b.Normal, t);
            var lengthSq = normal.LengthSquared();
            if (lengthSq > 1e-10f)
            {
                normal /= MathF.Sqrt(lengthSq);
            }

            var attributes = new float[_attributeCount];
            for (var i = 0; i < _attributeCount; i++)
            {
                var from = i < a.Attributes.Length ? a.Attributes[i] : 0;
                var to = i < b.Attributes.Length ? b.Attributes[i] : 0;
                attributes[i] = from + (to - from) * t;
            }

            return new Vert(position, normal, _weld.GetOrAddPoint(position)) { Attributes = attributes };
        }

        private Face? Finish(Face source, List<Vert> vertices, in HalfSpace plane)
        {
            var face = BuildFace(vertices);
            if (face == null)
                return null;

            face.SourceFace = source.SourceFace;
            face.Reversed = source.Reversed;
            face.CutPlane = plane;
            return face;
        }

        /// <summary>Builds a face from a contour, dropping repeated corners and degenerate results.</summary>
        private Face? BuildFace(List<Vert> vertices)
        {
            var write = 0;
            for (var read = 0; read < vertices.Count; read++)
            {
                var vertex = vertices[read];
                if (write > 0 && vertices[write - 1].PointId == vertex.PointId)
                    continue;

                vertices[write++] = vertex;
            }

            while (write > 1 && vertices[write - 1].PointId == vertices[0].PointId)
            {
                write--;
            }

            if (write < 3)
                return null;

            if (write < vertices.Count)
                vertices.RemoveRange(write, vertices.Count - write);

            var face = new Face(write);
            for (var i = 0; i < write; i++)
            {
                face.Vertices[i] = vertices[i];
            }

            return face;
        }

        /// <summary>
        /// Closes the openings the split left behind. A face that was cut carries the plane it was cut
        /// by; the boundary edges lying in that plane form the cross-section, which is tessellated and
        /// added back with the winding the result needs.
        /// </summary>
        public void CapOpenings(List<Face> faces)
        {
            _boundaryUse.Clear();
            foreach (var face in faces)
            {
                var count = face.CornerCount;
                for (var i = 0; i < count; i++)
                {
                    var a = face.Vertices[i].PointId;
                    var b = face.Vertices[(i + 1) % count].PointId;
                    if (a == b)
                        continue;

                    var key = a < b ? (a, b) : (b, a);
                    _boundaryUse[key] = _boundaryUse.GetValueOrDefault(key) + 1;
                }
            }

            _groups.Clear();
            foreach (var face in faces)
            {
                if (face.CutPlane.Weight <= 0 || face.CornerCount < 3)
                    continue;

                var group = GetGroup(face.CutPlane);
                var count = face.CornerCount;
                for (var i = 0; i < count; i++)
                {
                    var a = face.Vertices[i].PointId;
                    var b = face.Vertices[(i + 1) % count].PointId;
                    var key = a < b ? (a, b) : (b, a);
                    if (_boundaryUse.GetValueOrDefault(key) == 1)
                        group.Edges.Add((a, b));
                }
            }

            foreach (var group in _groups.Values)
            {
                if (group.Edges.Count < 3)
                    continue;

                _caps.Clear();
                FillPlane(group.Plane, group.Edges, _caps);
                faces.AddRange(_caps);
            }
        }

        private Group GetGroup(HalfSpace plane)
        {
            var signature = SignatureOf(plane);
            if (_groups.TryGetValue(signature, out var existing))
                return existing;

            var created = new Group(plane);
            _groups[signature] = created;
            return created;
        }

        /// <summary>
        /// Tessellates one plane's boundary loops. The loops run along the cut, and every vertex is a
        /// point id from the weld, so the triangles meet the surface exactly and no sliver is invented.
        /// </summary>
        private void FillPlane(in HalfSpace plane, List<(int A, int B)> edges, List<Face> faces)
        {
            _loops.Clear();
            ChainLoops(edges);
            if (_loops.Count == 0)
                return;

            var tess = new Tess();
            foreach (var loop in _loops)
            {
                var contour = new ContourVertex[loop.Count];
                for (var i = 0; i < loop.Count; i++)
                {
                    contour[i] = new ContourVertex(ToVec3(_weld.Positions[loop[i]]));
                }

                tess.AddContour(contour, ContourOrientation.Original);
            }

            tess.Tessellate(WindingRule.NonZero, ElementType.Polygons, 3, null, ToVec3(plane.Normal));
            var vertices = tess.Vertices;
            var elements = tess.Elements;
            for (var triangle = 0; triangle < tess.ElementCount; triangle++)
            {
                var i0 = elements[triangle * 3];
                var i1 = elements[triangle * 3 + 1];
                var i2 = elements[triangle * 3 + 2];
                if (i0 == Tess.Undef || i1 == Tess.Undef || i2 == Tess.Undef)
                    continue;

                var p0 = ToVector3(vertices[i0].Position);
                var p1 = ToVector3(vertices[i1].Position);
                var p2 = ToVector3(vertices[i2].Position);
                if (Vector3.Dot(Vector3.Cross(p1 - p0, p2 - p0), plane.Normal) < 0)
                {
                    (p1, p2) = (p2, p1);
                }

                var face = new Face(3) { SourceFace = -1, CutPlane = plane };
                var normal = plane.Normal;
                face.Vertices[0] = new Vert(p0, normal, _weld.GetOrAddPoint(p0)) { Attributes = new float[_attributeCount] };
                face.Vertices[1] = new Vert(p1, normal, _weld.GetOrAddPoint(p1)) { Attributes = new float[_attributeCount] };
                face.Vertices[2] = new Vert(p2, normal, _weld.GetOrAddPoint(p2)) { Attributes = new float[_attributeCount] };
                faces.Add(face);
            }
        }

        /// <summary>
        /// Chains boundary edges into closed loops. Edges that do not close belong to an open input,
        /// which a boolean has no surface to close them with.
        /// </summary>
        private void ChainLoops(List<(int A, int B)> edges)
        {
            _next.Clear();
            _unused.Clear();
            foreach (var edge in edges)
            {
                if (_next.TryAdd(edge.A, edge.B))
                    _unused.Add(edge.A);
            }

            for (var i = 0; i < _unused.Count; i++)
            {
                var start = _unused[i];
                if (!_next.ContainsKey(start))
                    continue;

                var loop = new List<int>();
                var current = start;
                var closed = false;
                for (var guard = 0; guard <= _unused.Count; guard++)
                {
                    if (!_next.Remove(current, out var next))
                        break;

                    loop.Add(current);
                    if (next == start)
                    {
                        closed = true;
                        break;
                    }

                    current = next;
                }

                _next.Remove(start);
                if (closed && loop.Count >= 3)
                    _loops.Add(loop);
            }
        }

        public static long SignatureOf(in HalfSpace plane)
        {
            var n = plane.Normal;
            var d = plane.Offset;
            if (n.X < 0 || (n.X == 0 && (n.Y < 0 || (n.Y == 0 && (n.Z < 0 || (n.Z == 0 && d < 0))))))
            {
                n = -n;
                d = -d;
            }

            var nx = (long)MathF.Round(n.X / SignatureQuantization);
            var ny = (long)MathF.Round(n.Y / SignatureQuantization);
            var nz = (long)MathF.Round(n.Z / SignatureQuantization);
            var offset = (long)MathF.Round(d / SignatureQuantization);
            return HashCode.Combine(nx, ny, nz, offset);
        }

        /// <summary>
        /// Writes the surviving faces out. Vertices are keyed by point id, so the output keeps the
        /// sharing the kernel built and the mesh closes.
        /// </summary>
        public void Emit(MeshGeometry target, IReadOnlyList<Face> faces, bool splitIntoParts)
        {
            var skipped = 0;
            _faceOffsets.Clear();
            _cornerPoints.Clear();
            _faceSourceFaces.Clear();
            _faceCut.Clear();
            _faceOffsets.Add(0);
            foreach (var face in faces)
            {
                if (face.CornerCount < 3)
                {
                    skipped++;
                    continue;
                }

                var start = _cornerPoints.Count;
                for (var i = 0; i < face.CornerCount; i++)
                {
                    _cornerPoints.Add(face.Vertices[i].PointId);
                }

                if (face.Reversed)
                {
                    _cornerPoints.Reverse(start, _cornerPoints.Count - start);
                }

                _faceOffsets.Add(_cornerPoints.Count);
                _faceSourceFaces.Add(face.SourceFace);
                _faceCut.Add(face.CutPlane.Weight > 0 && face.SourceFace < 0 ? 1f : 0f);
            }

            Log.Debug($"BooleanOperation[{KernelVersion}]: emit faces={faces.Count} skipped={skipped} points={_weld.Positions.Count} corners={_cornerPoints.Count}",
                      this);

            target.Positions = _weld.Positions.ToArray();
            target.FaceCornerOffsets = _faceOffsets.ToArray();
            target.CornerPointIndices = _cornerPoints.ToArray();
            target.Attributes.Clear();

            var faceCount = target.FaceCount;
            var isCut = target.Attributes.GetOrCreate<float>(GeometryAttributeNames.IsCut, AttributeDomain.Face, faceCount);
            var selection = target.Attributes.GetOrCreate<float>(GeometryAttributeNames.Selection, AttributeDomain.Face, faceCount);
            for (var face = 0; face < faceCount; face++)
            {
                isCut.Values[face] = _faceCut[face];
                selection.Values[face] = _faceCut[face];
            }

            target.Parts = splitIntoParts ? BuildParts(target) : [];
            target.InvalidateTopologyCaches();
        }

        private GeometryPart[] BuildParts(MeshGeometry geometry)
        {
            var faceCount = geometry.FaceCount;
            if (faceCount == 0)
                return [];

            var parent = new int[geometry.PointCount];
            for (var i = 0; i < parent.Length; i++)
            {
                parent[i] = i;
            }

            _edgeOwner.Clear();
            for (var face = 0; face < faceCount; face++)
            {
                var start = geometry.FaceCornerOffsets[face];
                var end = geometry.FaceCornerOffsets[face + 1];
                for (var c = start; c < end; c++)
                {
                    var a = geometry.CornerPointIndices[c];
                    var b = geometry.CornerPointIndices[c + 1 == end ? start : c + 1];
                    var key = a < b ? (a, b) : (b, a);
                    if (!_edgeOwner.TryGetValue(key, out var other))
                    {
                        _edgeOwner[key] = face;
                        continue;
                    }

                    Union(parent, other, face);
                }
            }

            _order.Clear();
            _byRoot.Clear();
            for (var face = 0; face < faceCount; face++)
            {
                var root = Find(parent, geometry.CornerPointIndices[geometry.FaceCornerOffsets[face]]);
                if (!_byRoot.TryGetValue(root, out var group))
                {
                    group = new List<int>();
                    _byRoot[root] = group;
                    _order.Add(root);
                }

                group.Add(face);
            }

            var parts = new List<GeometryPart>(_order.Count);
            foreach (var root in _order)
            {
                var group = _byRoot[root];
                if (group.Count == 0)
                    continue;

                var pivot = MeshVolumeCentroid.Compute(geometry, group);
                parts.Add(new GeometryPart(group[0], group[^1] - group[0] + 1, pivot, parts.Count, 0));
            }

            return parts.ToArray();
        }

        private static int Find(int[] parent, int element)
        {
            while (parent[element] != element)
            {
                parent[element] = parent[parent[element]];
                element = parent[element];
            }

            return element;
        }

        private static void Union(int[] parent, int a, int b)
        {
            a = Find(parent, a);
            b = Find(parent, b);
            if (a != b)
            {
                parent[a] = b;
            }
        }

        private static Vec3 ToVec3(Vector3 p) => new(p.X, p.Y, p.Z);
        private static Vector3 ToVector3(Vec3 p) => new(p.X, p.Y, p.Z);

        private sealed class Group(HalfSpace plane)
        {
            public readonly HalfSpace Plane = plane;
            public readonly List<(int A, int B)> Edges = [];
        }

        private readonly record struct Split(Face Front, Face Back);

        private readonly PointWeld _weld;
        private readonly int _attributeCount;
        private readonly float _epsilon;
        private int _planesTouched;
        private int _planesFullyOn;
        private readonly List<Face> _caps = [];
        private readonly Dictionary<(int, int), int> _boundaryUse = [];
        private readonly Dictionary<long, Group> _groups = [];
        private readonly List<List<int>> _loops = [];
        private readonly Dictionary<int, int> _next = [];
        private readonly List<int> _unused = [];
        private readonly List<int> _faceOffsets = [];
        private readonly List<int> _cornerPoints = [];
        private readonly List<int> _faceSourceFaces = [];
        private readonly List<float> _faceCut = [];
        private readonly Dictionary<(int, int), int> _edgeOwner = [];
        private readonly Dictionary<int, List<int>> _byRoot = [];
        private readonly List<int> _order = [];
    }

    /// <summary>
    /// The one place points are created. Positions within the weld tolerance map to a single id, so
    /// the same cut point asked for from two different faces always answers with the same id.
    /// </summary>
    private sealed class PointWeld
    {
        public PointWeld(float extent)
        {
            Extent = extent;
            _tolerance = MathF.Max(extent * WeldToleranceFactor, 1e-9f);
            _gridScale = 1f / _tolerance;
            _toleranceSq = _tolerance * _tolerance;
        }

        public float Extent { get; }

        public List<Vector3> Positions { get; } = [];

        public int GetOrAddPoint(Vector3 position)
        {
            var (kx, ky, kz) = Quantize(position);
            for (var dz = -1; dz <= 1; dz++)
            for (var dy = -1; dy <= 1; dy++)
            for (var dx = -1; dx <= 1; dx++)
            {
                if (!_lookup.TryGetValue((kx + dx, ky + dy, kz + dz), out var candidate))
                    continue;

                while (candidate >= 0)
                {
                    if (Vector3.DistanceSquared(Positions[candidate], position) < _toleranceSq)
                        return candidate;

                    candidate = _nextInBucket[candidate];
                }
            }

            var id = Positions.Count;
            Positions.Add(position);
            _nextInBucket.Add(_lookup.TryGetValue((kx, ky, kz), out var head) ? head : -1);
            _lookup[(kx, ky, kz)] = id;
            return id;
        }

        private (int, int, int) Quantize(Vector3 position)
        {
            return ((int)MathF.Floor(position.X * _gridScale),
                    (int)MathF.Floor(position.Y * _gridScale),
                    (int)MathF.Floor(position.Z * _gridScale));
        }

        private readonly float _tolerance;
        private readonly float _toleranceSq;
        private readonly float _gridScale;
        private readonly List<int> _nextInBucket = [];
        private readonly Dictionary<(int, int, int), int> _lookup = [];
    }

    /// <summary>
    /// Corner-domain attributes carried through the kernel. The first operand that has a name fills
    /// the column; later operands append into the same column order, so a cut corner keeps its UVs
    /// and weights from whichever fragment it came from.
    /// </summary>
    private static class AttributeSchema
    {
        public static List<AttributeColumn>? Resolve(MeshGeometry source, List<AttributeColumn> schema)
        {
            var columns = new List<AttributeColumn>();
            foreach (var attribute in source.Attributes)
            {
                if (attribute.Domain != AttributeDomain.Corner)
                    continue;

                var elementSize = ElementSizeOf(attribute);
                if (elementSize == 0)
                    continue;

                if (IndexOf(schema, attribute.Name) < 0)
                {
                    schema.Add(AttributeColumn.Create(attribute, elementSize));
                }

                columns.Add(schema[IndexOf(schema, attribute.Name)]);
            }

            return columns.Count == 0 ? null : columns;
        }

        private static int IndexOf(List<AttributeColumn> schema, string name)
        {
            for (var i = 0; i < schema.Count; i++)
            {
                if (string.Equals(schema[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int ElementSizeOf(GeometryAttribute attribute)
        {
            if (attribute is GeometryAttribute<float>)
                return 1;

            if (attribute is GeometryAttribute<Vector2>)
                return 2;

            if (attribute is GeometryAttribute<Vector3>)
                return 3;

            return attribute is GeometryAttribute<Vector4> ? 4 : 0;
        }
    }

    /// <summary>One column of corner attributes, flattened to a component per value.</summary>
    private sealed class AttributeColumn(string name, int elementSize)
    {
        public readonly string Name = name;
        public readonly int ElementSize = elementSize;
        public readonly List<float> Values = [];

        public static AttributeColumn Create(GeometryAttribute attribute, int elementSize)
        {
            var column = new AttributeColumn(attribute.Name, elementSize);
            for (var i = 0; i < attribute.Count; i++)
            {
                for (var component = 0; component < elementSize; component++)
                {
                    column.Values.Add(Read(attribute, i, component));
                }
            }

            return column;
        }

        private static float Read(GeometryAttribute attribute, int index, int component)
        {
            switch (attribute)
            {
                case GeometryAttribute<float> f:
                    return f.Values[index];
                case GeometryAttribute<Vector2> v2:
                    return component == 0 ? v2.Values[index].X : v2.Values[index].Y;
                case GeometryAttribute<Vector3> v3:
                    return component switch
                    {
                        0 => v3.Values[index].X,
                        1 => v3.Values[index].Y,
                        _ => v3.Values[index].Z,
                    };
                case GeometryAttribute<Vector4> v4:
                    return component switch
                    {
                        0 => v4.Values[index].X,
                        1 => v4.Values[index].Y,
                        2 => v4.Values[index].Z,
                        _ => v4.Values[index].W,
                    };
                default:
                    return 0;
            }
        }
    }

    private const int MaxSplitDepth = 96;
    private const float PlaneToleranceFactor = 1e-6f;
    private const float WeldToleranceFactor = 1e-3f;
    private const float SignatureQuantization = 1e-4f;

    /// <summary>Bumped whenever the kernel's splitting or capping logic changes, so a log line can be told from a stale build.</summary>
    private const int KernelVersion = 4;
}

