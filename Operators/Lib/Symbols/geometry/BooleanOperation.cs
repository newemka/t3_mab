#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using Lib.Utils;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Geometry;
using T3.Core.Logging;
using T3.Core.Utils;

namespace Lib.geometry;

/// <summary>
/// Boolean-combines closed MeshGeometry solids - union, difference, intersection.
/// </summary>
/// <remarks>
/// The kernel is a BSP (binary space partition) of the classic Naylor/Thibault form: every operand's
/// faces are inserted into a tree of their own face planes, and the two trees then clip each other's
/// polygons, so a fragment survives only if it is on the requested side of the other solid. There is
/// no separate capping step - the cut surface of the result is made of the other operand's own faces,
/// which is why a closed input pair yields a closed output.
///
/// <para>Coplanar faces are the case that breaks naive implementations. Here they are decided by the
/// facing of the two planes rather than by a point test: two surfaces that meet in one plane keep one
/// copy where they face the same way (so a union does not double the shared face) and cancel where they
/// face opposite (so a difference does not leave a membrane in the opening). Crucially the test is
/// applied per polygon during clipping, not to a whole face against every plane of the other solid, so
/// two solids that merely share a plane - or are apart from each other - are untouched.</para>
///
/// <para>All points go through one weld, so a vertex produced by a cut exists once and both faces that
/// meet there reference the same id. That shared identity is what makes the output watertight under
/// index-based edge counting.</para>
///
/// <para>BSP clipping needs planar polygons; it is exact for convex ones. Faces are therefore kept as
/// N-gons only while they are planar and convex, and are otherwise triangulated on load (ear clipping
/// for a concave planar face, a fan for a non-planar one), so the splitter never sees a shape it cannot
/// divide exactly.</para>
///
/// <para>All parts of one input count as that one solid, so multi-part geometry such as separated
/// letters or fracture chunks needs no merge step. That is right when the mesh declares its parts; a
/// mesh that merely concatenates separated chunks without a part table is still one solid, and an
/// intersection against it also cuts the space between the chunks. SplitOperandsIntoParts recovers
/// the chunks from their connected shells for an intersection, which is the operation whose answer
/// that changes. Solids are expected to be closed; the op warns when the result is not watertight.</para>
/// </remarks>
[Guid("3f9a1d64-7c25-4b8e-9a13-6e5d84c07b21")]
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
        _splitOperands = SplitOperandsIntoParts.GetValue(context);

        var leftSource = Geometry.GetValue(context);

        // Read through GetValues so the multi-input's dirty flag is synced afterwards. Collecting its
        // slots without clearing leaves the op looking changed to every later invalidation walk, and the
        // output window walks once per frame - the op would then never go idle or stop recomputing.
        Operands.GetValues(ref _operandValues, context);
        var rightSources = _operandValues;

        var hash = new HashCode();
        hash.Add(_operation);
        hash.Add(_splitIntoParts);
        hash.Add(_splitOperands);
        hash.Add(leftSource?.Version ?? 0);
        hash.Add(leftSource?.GetHashCode() ?? 0);
        for (var i = 0; i < rightSources.Length; i++)
        {
            var source = rightSources[i];
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
            var capturedRights = (MeshGeometry[])rightSources.Clone();
            var capturedOperation = _operation;
            var capturedSplit = _splitIntoParts;
            var capturedSplitOperands = _splitOperands;
            var result = _asyncComputation.Update(context, Result, hash.ToHashCode(),
                                                  token =>
                                                  {
                                                      var target = new MeshGeometry();
                                                      Evaluate(capturedLeft, capturedRights, capturedOperation, capturedSplit, target,
                                                               capturedSplitOperands, token);
                                                      return target;
                                                  });
            Result.Value = result ?? _output;
            PartCount.Value = result?.Parts.Length ?? 0;
            return;
        }

        _asyncComputation.WaitForPending(Result);
        Evaluate(leftSource, rightSources, _operation, _splitIntoParts, _output, _splitOperands);
        Result.Value = _output;
        PartCount.Value = _output.Parts.Length;
    }

    /// <summary>
    /// True when there is nothing to combine. With no right operand the op is a pass-through and
    /// hands the input on untouched - attributes and part table included - instead of rebuilding an
    /// equivalent mesh and dropping whatever the kernel does not carry.
    /// </summary>
    private static bool HasAnythingToCombine(IReadOnlyList<MeshGeometry> rightSources)
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
    ///
    /// <para>Geometry stays one solid. Operand slots fold left to right, as before - but when one
    /// operand carries a part table, its parts are separate solids rather than one lump: the branch
    /// splits into one per part, each cut on its own. Combining a fracture's cells as a lump would
    /// intersect against their union instead of against each cell.</para>
    ///
    /// <para>With <paramref name="splitOperands"/> an intersection still cuts a mesh that declares no
    /// part table at its connected shells, so its chunks are separate solids too. Without it a mesh of
    /// several disjoint shells is one solid, and an intersection against it is an intersection against
    /// the space between the shells as well - its convex hull rather than its pieces, which shows up as
    /// extra faces and non-manifold edges where two shells meet the same cutter.</para>
    /// </summary>
    internal static void Evaluate(MeshGeometry leftSource, IReadOnlyList<MeshGeometry> rightSources, Operations operation,
                                  bool splitIntoParts, MeshGeometry target, bool splitOperands = false,
                                  CancellationToken cancellationToken = default)
    {
        var extent = ExtentOf(leftSource, rightSources);

        // Float32 loses absolute precision as coordinates grow, and every tolerance below is relative
        // to the extent. Working at the scene's actual position would therefore tighten the tolerances
        // below the noise floor for a model far from the origin, so the kernel runs in a local frame
        // and the offset is added back on the way out.
        var origin = CenterOf(leftSource, rightSources);

        var weld = new PointWeld(extent);
        var schema = AttributeSchema.Resolve(leftSource, rightSources);
        var kernel = new Kernel(weld, schema, extent, origin);

        // One branch is one evolving result solid. A single-part operand folds into every branch; a
        // multi-part operand fans each branch out into one branch per part.
        //
        // Splitting operands at their shells is offered to Intersection only. It is the operation whose
        // answer it corrects: a union or a difference of one multi-shell solid already names every
        // shell in its result, so hull or pieces makes no difference to what is kept - but the hull can
        // keep surfaces that a part-wise pass would drop, so switching it on there would move existing
        // results rather than fix them.
        var splitOperandsHere = splitOperands && operation == Operations.Intersection;
        var leftParts = LoadOperandParts(leftSource, schema, weld, kernel, origin, 0, splitOperandsHere);

        // The fold runs one left part at a time and drives that part through every operand before the
        // next left part starts. Branching multiplies - a part-wide fan-out turns n parts into n
        // branches per operand, and folding the product forward would hold every combination of every
        // operand live at once, which is what floods memory on a 64-part operand. Driving one branch
        // all the way through keeps only that branch's expansions live, so peak polygons scale with
        // one part rather than with the product.
        var results = new List<Branch>();
        var budgetExceeded = false;
        for (var p = 0; p < leftParts.Count; p++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var folded = FoldOperands(new Branch(leftParts[p].Polygons, leftParts[p].Seed), rightSources, schema, weld, kernel,
                                      origin, operation, splitOperandsHere, cancellationToken);
            if (folded == null)
            {
                budgetExceeded = true;
                break;
            }

            results.AddRange(folded);
        }

        // An operation that ran out of budget has no answer to offer: emitting the branches built so
        // far would present a truncated solid as the finished one.
        if (budgetExceeded)
        {
            kernel.Emit(target, [], [], [], operation);
            return;
        }

        var combined = new List<Polygon>();
        var components = new List<int>();
        var partSeeds = new List<int>();

        foreach (var branch in results)
        {
            if (branch.Polygons.Count == 0)
                continue;

            kernel.RepairTJunctions(branch.Polygons);

            if (splitIntoParts)
            {
                var shells = kernel.OrderByComponent(branch.Polygons);
                var shellCount = 0;
                for (var p = 0; p < shells.Length; p++)
                    shellCount = Math.Max(shellCount, shells[p] + 1);

                var baseIndex = partSeeds.Count;
                for (var p = 0; p < branch.Polygons.Count; p++)
                {
                    combined.Add(branch.Polygons[p]);
                    components.Add(baseIndex + shells[p]);
                }

                for (var shell = 0; shell < shellCount; shell++)
                    partSeeds.Add(branch.Seed);
            }
            else
            {
                var partId = partSeeds.Count;
                foreach (var polygon in branch.Polygons)
                {
                    combined.Add(polygon);
                    components.Add(partId);
                }

                partSeeds.Add(branch.Seed);
            }
        }

        kernel.Emit(target, combined, components.ToArray(), partSeeds.ToArray(), operation);

        // Parts are emitted with their own points, so an index-based measure is a per-part measure here
        // and a shared face between two touching parts cannot look non-manifold to it.
        var stats = new MeshGeometryStats();
        stats.Measure(target);
        WarnIfOpen(stats.BoundaryEdges, stats.NonManifoldEdges);
    }

    /// <summary>Face range and seed of one operand part; a mesh without a part table is one whole solid.</summary>
    private static (int FaceStart, int FaceCount, int Seed) PartRange(MeshGeometry mesh, int partIndex)
    {
        if (mesh.Parts.Length == 0)
            return (0, mesh.FaceCount, 0);

        var part = mesh.Parts[partIndex];
        return (part.FaceStart, part.FaceCount, part.SeedIndex);
    }

    /// <summary>One operand solid: its polygons, and the seed of the part it descends from.</summary>
    private readonly record struct OperandPart(List<Polygon> Polygons, int Seed);

    /// <summary>
    /// Drives one starting branch through every right operand and returns the branches that survive,
    /// or null when the fold hit the corner budget.
    ///
    /// <para>It walks a work list rather than a single branch so the fan-out of one operand is
    /// expanded before the next operand is read: only one generation is ever live, instead of the
    /// product of every operand's part count.</para>
    /// </summary>
    private static List<Branch>? FoldOperands(Branch start, IReadOnlyList<MeshGeometry> rightSources,
                                              AttributeSchema schema, PointWeld weld, Kernel kernel, Vector3 origin,
                                              Operations operation, bool splitOperands, CancellationToken cancellationToken)
    {
        var generation = new List<Branch> { start };
        var expansion = new List<Branch>();

        for (var i = 0; i < rightSources.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var right = rightSources[i];
            if (right == null || right.FaceCount == 0)
                continue;

            // A right operand that declares no parts is still one solid per connected shell when the
            // caller asked for that, so a scattered operand cuts one piece at a time instead of being
            // intersected against everything between its pieces.
            var rightParts = LoadOperandParts(right, schema, weld, kernel, origin, i + 1, splitOperands);
            if (rightParts.Count == 0)
                continue;

            if (rightParts.Count == 1)
            {
                var solid = rightParts[0].Polygons;
                for (var b = 0; b < generation.Count; b++)
                {
                    var branch = generation[b];
                    branch.Polygons = kernel.Combine(ClonePolygons(branch.Polygons), ClonePolygons(solid), operation);
                }

                continue;
            }

            expansion.Clear();
            var live = 0;
            for (var b = 0; b < generation.Count; b++)
            {
                var branch = generation[b];
                for (var partIndex = 0; partIndex < rightParts.Count; partIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var part = rightParts[partIndex];
                    if (part.Polygons.Count == 0)
                        continue;

                    var result = kernel.Combine(ClonePolygons(branch.Polygons), ClonePolygons(part.Polygons), operation);
                    if (result.Count == 0)
                        continue;

                    for (var polygon = 0; polygon < result.Count; polygon++)
                        live += result[polygon].CornerCount;

                    expansion.Add(new Branch(result, part.Seed));
                }

                // Parts multiply: every operand part fans every branch, so the live polygon set can
                // grow by a factor per operand. Past the budget the fold stops; the caller drops the
                // partial result rather than emit something that looks finished.
                if (live > MaxLiveCorners)
                {
                    Log.Warning($"BooleanOperation: combining {rightParts.Count} operand parts with {generation.Count} "
                                + $"branches exceeds the budget of {MaxLiveCorners:N0} live corners, so the operation was "
                                + "stopped. Reduce the number of parts, or turn off SplitOperandsIntoParts.");
                    return null;
                }
            }

            // Both lists are local, so swapping them exchanges the values rather than the caller's
            // references - the previous generation becomes the scratch to overwrite next time.
            (generation, expansion) = (expansion, generation);
            expansion.Clear();
        }

        return generation;
    }

    /// <summary>
    /// Loads one operand as the solids the fold will treat separately.
    ///
    /// <para>A declared part table wins: its face ranges are the parts, and a mesh with a single part
    /// is one solid. Without a table the mesh is one solid too, unless <paramref name="split"/> is set
    /// - then it is cut at its connected shells, so chunks that were merely concatenated become
    /// separate solids.</para>
    /// </summary>
    private static List<OperandPart> LoadOperandParts(MeshGeometry mesh, AttributeSchema schema, PointWeld weld, Kernel kernel,
                                                      Vector3 origin, int operandIndex, bool split)
    {
        var result = new List<OperandPart>();

        if (mesh.Parts.Length > 0)
        {
            for (var partIndex = 0; partIndex < mesh.Parts.Length; partIndex++)
            {
                var (faceStart, faceCount, seed) = PartRange(mesh, partIndex);
                var polygons = Solid.Load(mesh, schema, weld, operandIndex, origin, faceStart, faceCount);
                if (polygons.Count > 0)
                    result.Add(new OperandPart(polygons, seed));
            }

            return result;
        }

        var whole = Solid.Load(mesh, schema, weld, operandIndex, origin);
        if (whole.Count == 0)
            return result;

        if (!split)
        {
            result.Add(new OperandPart(whole, 0));
            return result;
        }

        var chunks = kernel.SplitByComponent(whole);
        if (chunks.Count <= 1)
        {
            result.Add(new OperandPart(whole, 0));
            return result;
        }

        // A shell keeps its position in the list as its seed, so an output part can still be traced
        // back to the chunk it came from.
        for (var chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            result.Add(new OperandPart(chunks[chunkIndex], chunkIndex));

        return result;
    }

    /// <summary>An evolving result solid and the operand part it descends from.</summary>
    private sealed class Branch(List<Polygon> polygons, int seed)
    {
        public List<Polygon> Polygons = polygons;
        public readonly int Seed = seed;
    }

    /// <summary>Shallow-copies a polygon list into fresh wrappers that share the immutable vertex attributes.</summary>
    private static List<Polygon> ClonePolygons(List<Polygon> source)
    {
        var clone = new List<Polygon>(source.Count);
        foreach (var polygon in source)
            clone.Add(new Polygon((Vert[])polygon.Vertices.Clone(), polygon.Plane, polygon.SourceOperand));

        return clone;
    }

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

    /// <summary>Centre of the combined bounds of all operands; the kernel's local frame.</summary>
    private static Vector3 CenterOf(MeshGeometry left, IReadOnlyList<MeshGeometry> rights)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var any = false;
        Accumulate(left);
        for (var i = 0; i < rights.Count; i++)
        {
            Accumulate(rights[i]);
        }

        return any ? (min + max) * 0.5f : Vector3.Zero;

        void Accumulate(MeshGeometry mesh)
        {
            if (mesh == null || mesh.PointCount == 0)
                return;

            any = true;
            foreach (var position in mesh.Positions)
            {
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
        }
    }

    private static void WarnIfOpen(int boundaryEdges, int nonManifoldEdges)
    {
        if (boundaryEdges == 0 && nonManifoldEdges == 0)
            return;

        Log.Warning($"BooleanOperation: the result has {boundaryEdges} open and {nonManifoldEdges} non-manifold edges. "
                    + "The operands are expected to be closed solids; an open or self-intersecting input has no inside to cut against. "
                    + "Surfaces that meet within the weld tolerance of each other are the other common cause.");
    }

    internal enum Operations
    {
        Union,
        Difference,
        Intersection,
    }

    private readonly MeshGeometry _output = new();
    private readonly AsyncComputation<MeshGeometry> _asyncComputation = new();

    /// <summary>Reused buffer for the collected operands, so reading them allocates nothing per frame.</summary>
    private MeshGeometry[] _operandValues = [];
    private Operations _operation;
    private bool _splitIntoParts;
    private bool _splitOperands;

    [Input(Guid = "1a7c5e20-9b34-4d61-8f52-70c3e1a8d946")]
    public readonly InputSlot<MeshGeometry> Geometry = new();

    [Input(Guid = "6d2f8a13-4c70-49b5-b8e1-35a7d0c9264f")]
    public readonly MultiInputSlot<MeshGeometry> Operands = new();

    [Input(Guid = "c58b3e71-2d94-4a06-9f38-1b6e7c4d5a29", MappedType = typeof(Operations))]
    public readonly InputSlot<int> Operation = new();

    [Input(Guid = "9e14f7b6-8a02-4d53-b0c9-47e2a5b1c803")]
    public readonly InputSlot<bool> SplitIntoParts = new();

    [Input(Guid = "d41a8c26-5f93-4e70-b2a8-6c17e0d94b53")]
    public readonly InputSlot<bool> SplitOperandsIntoParts = new();

    [Input(Guid = "2b96c4d8-51e3-4f27-a6b0-83d1e95c7a46")]
    public readonly InputSlot<bool> Async = new();

    // ------------------------------------------------------------------ geometry primitives

    /// <summary>An oriented plane; positive distance is the outward side.</summary>
    private readonly record struct HalfSpace(Vector3 Normal, float Offset)
    {
        public float Distance(Vector3 point) => Vector3.Dot(Normal, point) - Offset;

        public HalfSpace Flipped => new(-Normal, -Offset);

        public bool IsValid => Normal.LengthSquared() > 0.5f;
    }

    /// <summary>
    /// One corner. <see cref="PointId"/> is its identity in the welded table: two corners cut from
    /// the same place carry the same id, which is what keeps neighbouring faces meeting exactly.
    /// </summary>
    private struct Vert(Vector3 position, int pointId)
    {
        public Vector3 Position = position;
        public int PointId = pointId;

        /// <summary>Corner attributes flattened to one component per entry, in schema order.</summary>
        public float[] Attributes = [];
    }

    private sealed class Polygon
    {
        public Polygon(Vert[] vertices, in HalfSpace plane, int sourceOperand)
        {
            Vertices = vertices;
            Plane = plane;
            SourceOperand = sourceOperand;
        }

        public Vert[] Vertices;

        public HalfSpace Plane;

        /// <summary>0 for the left input, 1..n for the operands; lets the emitter mark new surfaces.</summary>
        public readonly int SourceOperand;

        public int CornerCount => Vertices.Length;

        /// <summary>Reverses the winding and the plane together, which is what "invert" means for a solid.</summary>
        public void Flip()
        {
            Array.Reverse(Vertices);
            Plane = Plane.Flipped;
        }
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

        /// <summary>Merge radius; also the slack callers use when asking whether a point lies on an edge.</summary>
        public float Tolerance => _tolerance;

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

        /// <summary>Bucket indices are 64 bit: a scene far from the origin would overflow a 32 bit cell index.</summary>
        private (long, long, long) Quantize(Vector3 position)
        {
            return ((long)MathF.Floor(position.X * _gridScale),
                    (long)MathF.Floor(position.Y * _gridScale),
                    (long)MathF.Floor(position.Z * _gridScale));
        }

        private readonly float _tolerance;
        private readonly float _toleranceSq;
        private readonly float _gridScale;
        private readonly List<int> _nextInBucket = [];
        private readonly Dictionary<(long, long, long), int> _lookup = [];
    }

    /// <summary>
    /// Corner-domain attributes carried through the kernel. Every operand is loaded against one
    /// shared schema, so a corner keeps the attributes of whichever fragment it came from and the
    /// emitter writes one typed buffer per name without reconciling columns of different widths.
    ///
    /// <para>Only attributes that <em>every</em> contributing operand carries are kept. An attribute
    /// that one operand has and another does not would have to be invented for the second, and there is
    /// no safe invented value: a zero <c>Normal</c> is unlit and a zero <c>Tangent</c> makes the draw
    /// shader's normalized TBN degenerate, which paints the face black. Dropping the attribute instead
    /// lets every consumer fall back to the value it derives from the geometry, which is exactly what it
    /// does for input that never had the attribute.</para>
    /// </summary>
    private sealed class AttributeSchema
    {
        public readonly List<AttributeColumn> Columns = [];
        public int ComponentCount { get; private set; }

        public static AttributeSchema Resolve(MeshGeometry left, IReadOnlyList<MeshGeometry> rights)
        {
            var schema = new AttributeSchema();
            Collect(left, schema);

            for (var i = 0; i < rights.Count; i++)
            {
                // An operand with no faces contributes nothing to the result, so it must not narrow the
                // schema either.
                var right = rights[i];
                if (right == null || right.FaceCount == 0)
                    continue;

                schema.RetainOnlyIn(right);
            }

            return schema;
        }

        private static void Collect(MeshGeometry source, AttributeSchema schema)
        {
            if (source == null)
                return;

            foreach (var attribute in source.Attributes)
            {
                if (attribute.Domain != AttributeDomain.Corner)
                    continue;

                var elementSize = ElementSizeOf(attribute);
                if (elementSize == 0)
                    continue;

                schema.Register(attribute.Name, elementSize);
            }
        }

        /// <summary>Drops every column <paramref name="source"/> does not carry itself.</summary>
        private void RetainOnlyIn(MeshGeometry source)
        {
            for (var i = Columns.Count - 1; i >= 0; i--)
            {
                var column = Columns[i];
                var found = false;
                foreach (var attribute in source.Attributes)
                {
                    if (attribute.Domain != AttributeDomain.Corner
                        || !string.Equals(attribute.Name, column.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    found = ElementSizeOf(attribute) == column.ElementSize;
                    break;
                }

                if (!found)
                    Columns.RemoveAt(i);
            }

            var offset = 0;
            foreach (var column in Columns)
            {
                column.Offset = offset;
                offset += column.ElementSize;
            }

            ComponentCount = offset;
        }

        /// <summary>Binds each column to the source's own attribute of that name, once per solid.</summary>
        public GeometryAttribute[] ResolveFor(MeshGeometry source)
        {
            var resolved = new GeometryAttribute[Columns.Count];
            for (var i = 0; i < Columns.Count; i++)
            {
                resolved[i] = FindIn(source, Columns[i]);
            }

            return resolved;
        }

        public void ReadCorner(GeometryAttribute[] resolved, int cornerIndex, float[] target)
        {
            for (var i = 0; i < Columns.Count; i++)
            {
                var column = Columns[i];
                var attribute = resolved[i];
                if (attribute == null || ElementSizeOf(attribute) != column.ElementSize)
                {
                    Array.Clear(target, column.Offset, column.ElementSize);
                    continue;
                }

                ReadComponents(attribute, cornerIndex, target, column.Offset);
            }
        }

        private static GeometryAttribute? FindIn(MeshGeometry source, AttributeColumn column)
        {
            if (source == null)
                return null;

            foreach (var attribute in source.Attributes)
            {
                if (attribute.Domain == AttributeDomain.Corner
                    && string.Equals(attribute.Name, column.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute;
                }
            }

            return null;
        }

        private AttributeColumn Register(string name, int elementSize)
        {
            foreach (var column in Columns)
            {
                if (string.Equals(column.Name, name, StringComparison.OrdinalIgnoreCase))
                    return column;
            }

            var created = new AttributeColumn(name, elementSize, ComponentCount);
            Columns.Add(created);
            ComponentCount += elementSize;
            return created;
        }

        private static void ReadComponents(GeometryAttribute attribute, int index, float[] target, int offset)
        {
            switch (attribute)
            {
                case GeometryAttribute<float> f:
                    target[offset] = f.Values[index];
                    break;
                case GeometryAttribute<Vector2> v2:
                    target[offset] = v2.Values[index].X;
                    target[offset + 1] = v2.Values[index].Y;
                    break;
                case GeometryAttribute<Vector3> v3:
                    target[offset] = v3.Values[index].X;
                    target[offset + 1] = v3.Values[index].Y;
                    target[offset + 2] = v3.Values[index].Z;
                    break;
                case GeometryAttribute<Vector4> v4:
                    target[offset] = v4.Values[index].X;
                    target[offset + 1] = v4.Values[index].Y;
                    target[offset + 2] = v4.Values[index].Z;
                    target[offset + 3] = v4.Values[index].W;
                    break;
            }
        }

        private static int ElementSizeOf(GeometryAttribute attribute)
        {
            return attribute switch
            {
                GeometryAttribute<float> => 1,
                GeometryAttribute<Vector2> => 2,
                GeometryAttribute<Vector3> => 3,
                GeometryAttribute<Vector4> => 4,
                _ => 0,
            };
        }
    }

    /// <summary>One column of corner attributes: a name, its width, and where it starts in a corner array.</summary>
    private sealed class AttributeColumn(string name, int elementSize, int offset)
    {
        public readonly string Name = name;
        public readonly int ElementSize = elementSize;
        public int Offset = offset;
    }

    // ------------------------------------------------------------------ loading operands

    /// <summary>
    /// Turns one input mesh into the polygon list the BSP works on. Faces that are planar and convex
    /// survive as N-gons; anything else is triangulated, because the splitter is only exact for
    /// convex polygons.
    /// </summary>
    private static class Solid
    {
        public static List<Polygon> Load(MeshGeometry source, AttributeSchema schema, PointWeld weld, int operandIndex, Vector3 origin,
                                         int faceStart = 0, int faceCount = -1)
        {
            var polygons = new List<Polygon>();
            if (source == null || source.FaceCount == 0)
                return polygons;

            var firstFace = Math.Clamp(faceStart, 0, source.FaceCount);
            var lastFace = faceCount < 0
                               ? source.FaceCount
                               : Math.Clamp(firstFace + faceCount, firstFace, source.FaceCount);

            var resolved = schema.ResolveFor(source);
            var offsets = source.FaceCornerOffsets;
            var corners = source.CornerPointIndices;
            var vertices = new List<Vert>(8);

            for (var faceIndex = firstFace; faceIndex < lastFace; faceIndex++)
            {
                var start = offsets[faceIndex];
                var end = offsets[faceIndex + 1];
                if (end - start < 3)
                    continue;

                vertices.Clear();
                for (var c = start; c < end; c++)
                {
                    var pointIndex = corners[c];
                    var position = source.Positions[pointIndex] - origin;
                    var attributes = schema.ComponentCount == 0 ? [] : new float[schema.ComponentCount];
                    if (attributes.Length > 0)
                        schema.ReadCorner(resolved, c, attributes);

                    vertices.Add(new Vert(position, weld.GetOrAddPoint(position)) { Attributes = attributes });
                }

                if (!TryPlaneOf(vertices, out var plane))
                    continue;

                AddFace(polygons, vertices, plane, operandIndex);
            }

            return polygons;
        }

        /// <summary>
        /// A face that is planar and convex becomes one polygon; otherwise it is cut into triangles so
        /// every polygon handed to the splitter is convex (and therefore planar, being a triangle).
        /// </summary>
        private static void AddFace(List<Polygon> target, List<Vert> vertices, in HalfSpace plane, int operand)
        {
            var scale = LongestEdge(vertices);
            if (vertices.Count == 3 || IsPlanar(vertices, plane, scale))
            {
                if (IsConvex(vertices, plane, scale))
                {
                    target.Add(new Polygon(vertices.ToArray(), plane, operand));
                    return;
                }

                EarClip(target, vertices, plane, operand);
                return;
            }

            // Non-planar: fan from the first corner, which is the same triangulation the compile step uses.
            for (var i = 1; i + 1 < vertices.Count; i++)
            {
                AddTriangle(target, vertices[0], vertices[i], vertices[i + 1], operand);
            }
        }

        private static void EarClip(List<Polygon> target, List<Vert> vertices, in HalfSpace plane, int operand)
        {
            var count = vertices.Count;
            var normal = plane.Normal;

            // A right-handed basis in the face plane, so "counter-clockwise" in 2D means "outward" in 3D.
            var u = Perpendicular(normal);
            var v = Vector3.Cross(normal, u);
            var projected = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var p = vertices[i].Position;
                projected[i] = new Vector2(Vector3.Dot(p, u), Vector3.Dot(p, v));
            }

            var remaining = new List<int>(count);
            for (var i = 0; i < count; i++)
            {
                remaining.Add(i);
            }

            if (SignedArea(projected, remaining) < 0)
                remaining.Reverse();

            var emitted = 0;
            while (remaining.Count > 3 && emitted <= count)
            {
                var clipped = false;
                for (var i = 0; i < remaining.Count; i++)
                {
                    var previous = remaining[(i - 1 + remaining.Count) % remaining.Count];
                    var current = remaining[i];
                    var next = remaining[(i + 1) % remaining.Count];
                    if (!IsConvexCorner(projected[previous], projected[current], projected[next]))
                        continue;

                    if (ContainsAnyPoint(projected, remaining, previous, current, next))
                        continue;

                    AddTriangle(target, vertices[previous], vertices[current], vertices[next], operand);
                    remaining.RemoveAt(i);
                    emitted++;
                    clipped = true;
                    break;
                }

                if (!clipped)
                    break;
            }

            if (remaining.Count == 3)
            {
                AddTriangle(target, vertices[remaining[0]], vertices[remaining[1]], vertices[remaining[2]], operand);
                return;
            }

            // Self-intersecting or otherwise unclippable: a fan is wrong but keeps the face represented.
            Log.Warning($"BooleanOperation: could not ear-clip a {count}-corner face; falling back to a triangle fan.");
            for (var i = 1; i + 1 < count; i++)
            {
                target.Add(new Polygon([vertices[0], vertices[i], vertices[i + 1]], plane, operand));
            }
        }

        private static void AddTriangle(List<Polygon> target, in Vert a, in Vert b, in Vert c, int operand)
        {
            var normal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            var length = normal.Length();
            if (length < 1e-20f)
                return;

            normal /= length;
            var plane = new HalfSpace(normal, Vector3.Dot(normal, a.Position));
            target.Add(new Polygon([a, b, c], plane, operand));
        }

        private static bool IsConvexCorner(Vector2 a, Vector2 b, Vector2 c)
        {
            return (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X) > 0;
        }

        private static bool ContainsAnyPoint(Vector2[] projected, List<int> remaining, int a, int b, int c)
        {
            for (var i = 0; i < remaining.Count; i++)
            {
                var index = remaining[i];
                if (index == a || index == b || index == c)
                    continue;

                if (IsInsideTriangle(projected[index], projected[a], projected[b], projected[c]))
                    return true;
            }

            return false;
        }

        private static bool IsInsideTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
            var d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
            var d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
            var hasNegative = d1 < 0 || d2 < 0 || d3 < 0;
            var hasPositive = d1 > 0 || d2 > 0 || d3 > 0;
            return !(hasNegative && hasPositive);
        }

        private static float SignedArea(Vector2[] projected, List<int> indices)
        {
            var area = 0f;
            for (var i = 0; i < indices.Count; i++)
            {
                var a = projected[indices[i]];
                var b = projected[indices[(i + 1) % indices.Count]];
                area += a.X * b.Y - b.X * a.Y;
            }

            return area * 0.5f;
        }

        private static Vector3 Perpendicular(Vector3 normal)
        {
            var candidate = MathF.Abs(normal.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            return Vector3.Normalize(Vector3.Cross(normal, candidate));
        }

        /// <summary>
        /// Whether the whole face is convex seen from its outward side. Collinear corners are allowed -
        /// they cost a split but never make a face non-convex.
        /// </summary>
        private static bool IsConvex(List<Vert> vertices, in HalfSpace plane, float scale)
        {
            var count = vertices.Count;
            var tolerance = scale * scale * ConvexityTolerance;
            for (var i = 0; i < count; i++)
            {
                var a = vertices[i].Position;
                var b = vertices[(i + 1) % count].Position;
                var c = vertices[(i + 2) % count].Position;
                if (Vector3.Dot(Vector3.Cross(b - a, c - b), plane.Normal) < -tolerance)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Whether every corner lies in the face plane. The threshold is relative to the face itself,
        /// not to the mesh: a slightly warped quad on a displaced model has to be triangulated while a
        /// large flat N-gon must stay one polygon.
        /// </summary>
        private static bool IsPlanar(List<Vert> vertices, in HalfSpace plane, float scale)
        {
            var tolerance = scale * PlanarityTolerance;
            foreach (var vertex in vertices)
            {
                if (MathF.Abs(plane.Distance(vertex.Position)) > tolerance)
                    return false;
            }

            return true;
        }

        private static float LongestEdge(List<Vert> vertices)
        {
            var scale = 0f;
            for (var i = 0; i < vertices.Count; i++)
            {
                var next = vertices[(i + 1) % vertices.Count].Position;
                scale = MathF.Max(scale, Vector3.Distance(vertices[i].Position, next));
            }

            return MathF.Max(scale, 1e-12f);
        }

        /// <summary>Newell's normal, so the plane of an N-gon is the best fit rather than one corner's cross product.</summary>
        public static bool TryPlaneOf(List<Vert> vertices, out HalfSpace plane)
        {
            var normal = Vector3.Zero;
            var count = vertices.Count;
            for (var i = 0; i < count; i++)
            {
                var p0 = vertices[i].Position;
                var p1 = vertices[(i + 1) % count].Position;
                normal += new Vector3((p0.Y - p1.Y) * (p0.Z + p1.Z),
                                      (p0.Z - p1.Z) * (p0.X + p1.X),
                                      (p0.X - p1.X) * (p0.Y + p1.Y));
            }

            var length = normal.Length();
            if (length < 1e-20f)
            {
                plane = default;
                return false;
            }

            normal /= length;
            plane = new HalfSpace(normal, Vector3.Dot(normal, vertices[0].Position));
            return true;
        }
    }

    // ------------------------------------------------------------------ the kernel

    /// <summary>
    /// One node of a BSP tree over a solid's polygons: a splitting plane, the polygons lying in it,
    /// and the subtrees behind and in front of it.
    /// </summary>
    private sealed class BspNode
    {
        public BspNode(float epsilon, PointWeld weld, int attributeCount)
        {
            _epsilon = epsilon;
            _weld = weld;
            _attributeCount = attributeCount;
        }

        public HalfSpace Plane;
        public bool HasPlane;
        public List<Polygon> Polygons = [];
        public BspNode? Front;
        public BspNode? Back;

        private readonly float _epsilon;
        private readonly PointWeld _weld;
        private readonly int _attributeCount;

        private enum Side
        {
            Coplanar = 0,
            Front = 1,
            Back = 2,
            Spanning = 3,
        }

        /// <summary>
        /// Inserts polygons into the tree. Polygons lying in the node's plane stay at the node - both
        /// facings - which is what makes a coincident surface a single entry instead of a split.
        /// </summary>
        public void Build(List<Polygon> polygons)
        {
            var work = new Stack<(BspNode Node, List<Polygon> Polygons)>();
            work.Push((this, polygons));
            while (work.Count > 0)
            {
                var (node, batch) = work.Pop();
                if (batch.Count == 0)
                    continue;

                if (!node.HasPlane)
                {
                    node.Plane = batch[0].Plane;
                    node.HasPlane = node.Plane.IsValid;
                    if (!node.HasPlane)
                        continue;
                }

                var front = new List<Polygon>();
                var back = new List<Polygon>();
                foreach (var polygon in batch)
                {
                    Split(node.Plane, polygon, node.Polygons, node.Polygons, front, back);
                }

                if (front.Count > 0)
                {
                    node.Front ??= new BspNode(_epsilon, _weld, _attributeCount);
                    work.Push((node.Front, front));
                }

                if (back.Count > 0)
                {
                    node.Back ??= new BspNode(_epsilon, _weld, _attributeCount);
                    work.Push((node.Back, back));
                }
            }
        }

        /// <summary>
        /// Removes the parts of <paramref name="polygons"/> that fall inside this tree. A polygon
        /// coplanar with a node's plane survives when it faces the same way as the node and is dropped
        /// when it faces the other way - a surface on the boundary is kept once, an internal membrane
        /// disappears.
        /// </summary>
        public List<Polygon> ClipPolygons(List<Polygon> polygons)
        {
            var result = new List<Polygon>();
            var work = new Stack<(BspNode Node, List<Polygon> Polygons)>();
            work.Push((this, polygons));
            while (work.Count > 0)
            {
                var (node, batch) = work.Pop();
                if (batch.Count == 0)
                    continue;

                if (!node.HasPlane)
                {
                    result.AddRange(batch);
                    continue;
                }

                var front = new List<Polygon>();
                var back = new List<Polygon>();
                foreach (var polygon in batch)
                {
                    Split(node.Plane, polygon, front, back, front, back);
                }

                if (node.Front != null)
                    work.Push((node.Front, front));
                else
                    result.AddRange(front);

                // Without a back child everything behind the plane is inside the solid and is discarded.
                if (node.Back != null)
                    work.Push((node.Back, back));
            }

            return result;
        }

        /// <summary>Clips every polygon in this tree against another tree.</summary>
        public void ClipTo(BspNode other)
        {
            var work = new Stack<BspNode>();
            work.Push(this);
            while (work.Count > 0)
            {
                var node = work.Pop();
                node.Polygons = other.ClipPolygons(node.Polygons);
                if (node.Front != null)
                    work.Push(node.Front);
                if (node.Back != null)
                    work.Push(node.Back);
            }
        }

        /// <summary>Turns the solid inside out: every polygon is wound backwards and every plane swapped.</summary>
        public void Invert()
        {
            var work = new Stack<BspNode>();
            work.Push(this);
            while (work.Count > 0)
            {
                var node = work.Pop();
                foreach (var polygon in node.Polygons)
                {
                    polygon.Flip();
                }

                if (node.HasPlane)
                    node.Plane = node.Plane.Flipped;

                (node.Front, node.Back) = (node.Back, node.Front);
                if (node.Front != null)
                    work.Push(node.Front);
                if (node.Back != null)
                    work.Push(node.Back);
            }
        }

        public List<Polygon> AllPolygons()
        {
            var result = new List<Polygon>();
            var work = new Stack<BspNode>();
            work.Push(this);
            while (work.Count > 0)
            {
                var node = work.Pop();
                result.AddRange(node.Polygons);
                if (node.Front != null)
                    work.Push(node.Front);
                if (node.Back != null)
                    work.Push(node.Back);
            }

            return result;
        }

        /// <summary>
        /// Divides one polygon by one plane. Because every polygon the kernel builds is convex, a
        /// crossing produces exactly two convex pieces and one new vertex per straddled edge.
        /// </summary>
        private void Split(in HalfSpace plane, Polygon polygon, List<Polygon> coplanarFront, List<Polygon> coplanarBack,
                           List<Polygon> front, List<Polygon> back)
        {
            var count = polygon.CornerCount;
            var sides = new Side[count];
            var polygonSide = Side.Coplanar;
            for (var i = 0; i < count; i++)
            {
                var distance = plane.Distance(polygon.Vertices[i].Position);
                var side = distance > _epsilon ? Side.Front
                           : distance < -_epsilon ? Side.Back
                           : Side.Coplanar;
                sides[i] = side;
                polygonSide |= side;
            }

            switch (polygonSide)
            {
                case Side.Front:
                    front.Add(polygon);
                    return;

                case Side.Back:
                    back.Add(polygon);
                    return;

                case Side.Spanning:
                    break;

                default:
                    if (Vector3.Dot(plane.Normal, polygon.Plane.Normal) > 0)
                        coplanarFront.Add(polygon);
                    else
                        coplanarBack.Add(polygon);

                    return;
            }

            var frontVertices = new List<Vert>(count + 1);
            var backVertices = new List<Vert>(count + 1);
            for (var i = 0; i < count; i++)
            {
                var next = (i + 1) % count;
                var current = polygon.Vertices[i];
                var currentSide = sides[i];
                var nextSide = sides[next];

                if (currentSide != Side.Back)
                    frontVertices.Add(current);

                if (currentSide != Side.Front)
                    backVertices.Add(current);

                if ((currentSide | nextSide) != Side.Spanning)
                    continue;

                var crossing = CrossingVertex(current, polygon.Vertices[next], plane);
                frontVertices.Add(crossing);
                backVertices.Add(crossing);
            }

            if (frontVertices.Count >= 3)
            {
                var child = MakeChild(frontVertices, polygon);
                if (child != null)
                    front.Add(child);
            }

            if (backVertices.Count >= 3)
            {
                var child = MakeChild(backVertices, polygon);
                if (child != null)
                    back.Add(child);
            }
        }

        /// <summary>
        /// Builds a split child, dropping corners that repeat the previous one. A cut point welds onto an
        /// existing corner whenever the plane passes within the weld radius of it, and a polygon that
        /// carries the same corner twice folds onto itself - it would double an edge and leave the surface
        /// looking open.
        /// </summary>
        private static Polygon? MakeChild(List<Vert> vertices, Polygon source)
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

            return new Polygon(vertices.ToArray(), source.Plane, source.SourceOperand);
        }

        /// <summary>
        /// The point where an edge meets the plane, taken from the weld so both faces sharing the edge
        /// get one point. Attributes are lerped across the edge.
        /// </summary>
        private Vert CrossingVertex(in Vert a, in Vert b, in HalfSpace plane)
        {
            var aDistance = plane.Distance(a.Position);
            var bDistance = plane.Distance(b.Position);
            var denominator = aDistance - bDistance;
            var t = MathF.Abs(denominator) < 1e-20f ? 0.5f : Math.Clamp(aDistance / denominator, 0f, 1f);
            var position = Vector3.Lerp(a.Position, b.Position, t);
            var attributes = new float[_attributeCount];
            for (var i = 0; i < _attributeCount; i++)
            {
                var from = i < a.Attributes.Length ? a.Attributes[i] : 0;
                var to = i < b.Attributes.Length ? b.Attributes[i] : 0;
                attributes[i] = from + (to - from) * t;
            }

            return new Vert(position, _weld.GetOrAddPoint(position)) { Attributes = attributes };
        }
    }

    /// <summary>
    /// Runs the boolean and writes the surviving polygons into a mesh: polygons come from a BSP
    /// built over each operand, clipped against the other tree, and the union of the two trees'
    /// remaining polygons is the answer.
    /// </summary>
    private sealed class Kernel
    {
        public Kernel(PointWeld weld, AttributeSchema schema, float extent, Vector3 origin)
        {
            _weld = weld;
            _schema = schema;
            _origin = origin;
            _epsilon = MathF.Max(extent * PlaneToleranceFactor, 1e-9f);
            _gridCell = MathF.Max(extent / GridResolution, 1e-9f);
            _gridTolerance = MathF.Max(extent * WeldToleranceFactor, 1e-9f) * TJumpOnEdgeTolerance;
            _gridToleranceSq = _gridTolerance * _gridTolerance;
        }

        /// <summary>
        /// The three operations as tree manipulations. Union keeps the surfaces outside the other
        /// solid, difference keeps the left surface outside and the right surface inside (reversed, so
        /// it faces the cavity it opens), intersection keeps both surfaces inside the other solid.
        /// </summary>
        public List<Polygon> Combine(List<Polygon> left, List<Polygon> right, Operations operation)
        {
            if (right.Count == 0)
                return operation == Operations.Intersection ? [] : left;

            if (left.Count == 0)
                return operation == Operations.Union ? right : [];

            var nodeA = new BspNode(_epsilon, _weld, _schema.ComponentCount);
            var nodeB = new BspNode(_epsilon, _weld, _schema.ComponentCount);
            nodeA.Build(left);
            nodeB.Build(right);

            switch (operation)
            {
                case Operations.Union:
                    nodeA.ClipTo(nodeB);
                    nodeB.ClipTo(nodeA);
                    nodeB.Invert();
                    nodeB.ClipTo(nodeA);
                    nodeB.Invert();
                    break;

                case Operations.Difference:
                    nodeA.Invert();
                    nodeA.ClipTo(nodeB);
                    nodeB.ClipTo(nodeA);
                    nodeB.Invert();
                    nodeB.ClipTo(nodeA);
                    nodeB.Invert();
                    break;

                default:
                    nodeA.Invert();
                    nodeB.ClipTo(nodeA);
                    nodeB.Invert();
                    nodeA.ClipTo(nodeB);
                    nodeB.ClipTo(nodeA);
                    break;
            }

            nodeA.Build(nodeB.AllPolygons());
            if (operation != Operations.Union)
                nodeA.Invert();

            return nodeA.AllPolygons();
        }

        /// <summary>
        /// Restores a shared subdivision along edges the two solids meet on.
        /// <para>The BSP clips every polygon along its own path through the other tree, so two polygons
        /// that share an edge can be cut at different points: one gets a vertex on the shared edge and
        /// the other does not (a T-junction). The surface is still geometrically closed - the vertex lies
        /// exactly on the neighbour's edge - but by point id the edge now looks open. This pass inserts
        /// every point that already lies on a polygon edge into that edge, so both sides name the same
        /// vertices in the same order and the mesh closes combinatorially.</para>
        /// </summary>
        public void RepairTJunctions(List<Polygon> polygons)
        {
            if (polygons.Count == 0)
                return;

            // A split can leave a corner that reverses direction - a zero-width fold where the boundary
            // runs out along an edge and straight back - or a corner that lands on one of the polygon's
            // own non-adjacent edges. Neither carries area, but both duplicate an edge and block the
            // insertion below (the point that would split the edge is the polygon's own corner), so the
            // polygons are folded out into simple ones first.
            CleanUp(polygons);

            _usedPoints.Clear();
            foreach (var polygon in polygons)
            {
                foreach (var vertex in polygon.Vertices)
                    _usedPoints.Add(vertex.PointId);
            }

            if (_usedPoints.Count < 4)
                return;

            var cell = MathF.Max(_weld.Tolerance * 2f, _gridCell);
            _grid.Clear();
            foreach (var pointId in _usedPoints)
            {
                var key = CellOf(_weld.Positions[pointId], cell);
                if (!_grid.TryGetValue(key, out var bucket))
                {
                    bucket = [];
                    _grid[key] = bucket;
                }

                bucket.Add(pointId);
            }

            var tolerance = _gridTolerance;
            var toleranceSq = tolerance * tolerance;

            // How close to an edge's end a point may sit and still be worth inserting. This is the weld
            // radius, not the (larger) on-edge tolerance: further than the weld radius from the end it is
            // a real subdivision of the edge, inside it it simply is that end.
            var endSlack = _weld.Tolerance;
            foreach (var polygon in polygons)
            {
                var count = polygon.CornerCount;
                _seenInPolygon.Clear();
                foreach (var vertex in polygon.Vertices)
                    _seenInPolygon.Add(vertex.PointId);

                _repairedVertices.Clear();
                var changed = false;
                for (var i = 0; i < count; i++)
                {
                    var a = polygon.Vertices[i];
                    var b = polygon.Vertices[(i + 1) % count];
                    _repairedVertices.Add(a);

                    _edgeHits.Clear();
                    CollectPointsOnEdge(a, b, cell, tolerance, toleranceSq, endSlack);
                    if (_edgeHits.Count == 0)
                        continue;

                    changed = true;
                    _edgeHits.Sort(static (x, y) => x.T.CompareTo(y.T));
                    foreach (var hit in _edgeHits)
                    {
                        _repairedVertices.Add(hit.Vertex);
                    }
                }

                if (!changed)
                    continue;

                polygon.Vertices = _repairedVertices.ToArray();
            }
        }

        /// <summary>
        /// Finds every already-used point that lies on the open segment a-b. Points are looked up in a
        /// uniform grid sampled along the segment, so the cost follows the number of cells the edge
        /// crosses rather than the number of points in the mesh.
        /// </summary>
        private void CollectPointsOnEdge(in Vert a, in Vert b, float cell, float tolerance, float toleranceSq, float endSlack)
        {
            var direction = b.Position - a.Position;
            var lengthSq = direction.LengthSquared();
            if (lengthSq < 1e-20f)
                return;

            var length = MathF.Sqrt(lengthSq);
            var steps = Math.Clamp((int)MathF.Ceiling(length / (cell * 0.5f)), 1, MaxEdgeSamples);
            for (var step = 0; step <= steps; step++)
            {
                var sample = a.Position + direction * ((float)step / steps);
                var (cx, cy, cz) = CellOf(sample, cell);
                for (var dz = -1; dz <= 1; dz++)
                for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                {
                    if (!_grid.TryGetValue((cx + dx, cy + dy, cz + dz), out var bucket))
                        continue;

                    foreach (var pointId in bucket)
                    {
                        if (pointId == a.PointId || pointId == b.PointId || _seenInPolygon.Contains(pointId))
                            continue;

                        var position = _weld.Positions[pointId];
                        var t = Vector3.Dot(position - a.Position, direction) / lengthSq;

                        // Keep clear of the ends by the weld radius only. The on-edge tolerance is larger,
                        // and guarding the ends with it would reject exactly the subdivision that matters
                        // here: a point sitting a hair past the last corner of the neighbouring face.
                        var along = t * length;
                        if (along <= endSlack || length - along <= endSlack)
                            continue;

                        if (Vector3.DistanceSquared(a.Position + direction * t, position) > toleranceSq)
                            continue;

                        // Only now is the point really on this edge, so only now may it be claimed:
                        // marking a rejected candidate would hide it from the edge it actually lies on.
                        _seenInPolygon.Add(pointId);
                        var vertex = new Vert(position, pointId) { Attributes = LerpAttributes(a, b, t) };
                        _edgeHits.Add(new EdgeHit(t, vertex));
                    }
                }
            }
        }

        private static float[] LerpAttributes(in Vert a, in Vert b, float t)
        {
            var count = Math.Max(a.Attributes.Length, b.Attributes.Length);
            if (count == 0)
                return [];

            var attributes = new float[count];
            for (var i = 0; i < count; i++)
            {
                var from = i < a.Attributes.Length ? a.Attributes[i] : 0;
                var to = i < b.Attributes.Length ? b.Attributes[i] : 0;
                attributes[i] = from + (to - from) * t;
            }

            return attributes;
        }

        private static (long, long, long) CellOf(Vector3 position, float cell)
        {
            return ((long)MathF.Floor(position.X / cell),
                    (long)MathF.Floor(position.Y / cell),
                    (long)MathF.Floor(position.Z / cell));
        }

        /// <summary>
        /// Rewrites every polygon into one that does not touch itself, dropping the ones that collapse.
        /// A polygon is folded when one of its corners lies on one of its own edges; the cycle is cut
        /// there into the two arcs between the edge's ends, which are the same surface without the
        /// self-overlap.
        /// </summary>
        private void CleanUp(List<Polygon> polygons)
        {
            var pending = new Stack<Polygon>();
            for (var i = polygons.Count - 1; i >= 0; i--)
            {
                pending.Push(polygons[i]);
            }

            polygons.Clear();
            var guard = 0;
            var limit = Math.Max(64, pending.Count * 4);
            while (pending.Count > 0 && guard++ < limit)
            {
                var polygon = pending.Pop();
                if (Simplify(polygon))
                    continue;

                if (TrySplitAtFold(polygon, out var first, out var second))
                {
                    pending.Push(first);
                    if (second != null)
                        pending.Push(second);
                    continue;
                }

                polygons.Add(polygon);
            }

            while (pending.Count > 0)
            {
                polygons.Add(pending.Pop());
            }
        }

        /// <summary>
        /// Cuts a self-touching polygon at a corner that lies on one of its own non-adjacent edges.
        /// Returns false when the polygon is already simple.
        /// </summary>
        private bool TrySplitAtFold(Polygon polygon, out Polygon first, out Polygon? second)
        {
            second = null;
            first = polygon;

            var vertices = polygon.Vertices;
            var count = vertices.Length;
            if (count < 4)
                return false;

            for (var i = 0; i < count; i++)
            {
                var a = vertices[i].Position;
                var b = vertices[(i + 1) % count].Position;
                var edge = b - a;
                var edgeLengthSq = edge.LengthSquared();
                if (edgeLengthSq < 1e-20f)
                    continue;

                var edgeLength = MathF.Sqrt(edgeLengthSq);
                for (var j = 0; j < count; j++)
                {
                    if (j == i || j == (i + 1) % count)
                        continue;

                    var position = vertices[j].Position;
                    var t = Vector3.Dot(position - a, edge) / edgeLengthSq;
                    var along = t * edgeLength;
                    if (along <= _weld.Tolerance || edgeLength - along <= _weld.Tolerance)
                        continue;

                    if (Vector3.DistanceSquared(a + edge * t, position) > _gridToleranceSq)
                        continue;

                    var forward = Arc(vertices, (i + 1) % count, j);
                    var backward = Arc(vertices, j, i);
                    var keepForward = forward.Count >= 3;
                    var keepBackward = backward.Count >= 3;
                    if (!keepForward && !keepBackward)
                        return false;

                    if (keepForward && keepBackward)
                    {
                        first = new Polygon(forward.ToArray(), polygon.Plane, polygon.SourceOperand);
                        second = new Polygon(backward.ToArray(), polygon.Plane, polygon.SourceOperand);
                        return true;
                    }

                    var kept = keepForward ? forward : backward;
                    first = new Polygon(kept.ToArray(), polygon.Plane, polygon.SourceOperand);
                    return true;
                }
            }

            return false;
        }

        /// <summary>Corners from <paramref name="from"/> forward to <paramref name="to"/>, both included.</summary>
        private static List<Vert> Arc(Vert[] vertices, int from, int to)
        {
            var result = new List<Vert>();
            var count = vertices.Length;
            var index = from;
            for (var guard = 0; guard <= count; guard++)
            {
                result.Add(vertices[index]);
                if (index == to)
                    break;

                index = (index + 1) % count;
            }

            return result;
        }

        /// <summary>
        /// Drops repeated corners and zero-width folds from a polygon. Returns true when nothing usable
        /// is left, so the caller can remove it.
        /// </summary>
        private static bool Simplify(Polygon polygon)
        {
            var work = new List<Vert>(polygon.Vertices);
            var changed = false;
            var progress = true;
            while (progress && work.Count >= 3)
            {
                progress = false;
                for (var i = 0; i < work.Count; i++)
                {
                    var previous = work[(i - 1 + work.Count) % work.Count];
                    var current = work[i];
                    var next = work[(i + 1) % work.Count];
                    if (current.PointId == previous.PointId || current.PointId == next.PointId || Reverses(previous, current, next))
                    {
                        work.RemoveAt(i);
                        changed = true;
                        progress = true;
                        break;
                    }
                }
            }

            if (work.Count < 3)
                return true;

            if (changed)
                polygon.Vertices = work.ToArray();

            return false;
        }

        /// <summary>True when the boundary doubles back on itself at <paramref name="b"/>.</summary>
        private static bool Reverses(in Vert a, in Vert b, in Vert c)
        {
            var incoming = b.Position - a.Position;
            var outgoing = c.Position - b.Position;
            var incomingLength = incoming.Length();
            var outgoingLength = outgoing.Length();
            if (incomingLength < 1e-12f || outgoingLength < 1e-12f)
                return true;

            var cosine = Vector3.Dot(incoming / incomingLength, outgoing / outgoingLength);
            return cosine < -1f + 1e-6f;
        }

        private readonly record struct EdgeHit(float T, Vert Vertex);

        /// <summary>
        /// Cuts a polygon list into one list per connected shell, in a deterministic order. Returns a
        /// single chunk when the input is already one shell, so callers can treat "no split happened"
        /// and "one solid" the same way.
        /// </summary>
        public List<List<Polygon>> SplitByComponent(List<Polygon> polygons)
        {
            var chunks = new List<List<Polygon>>();
            var components = ComponentsOf(polygons, _order, _byRoot);
            if (components.Length == 0)
                return chunks;

            for (var c = 0; c < components.Length; c++)
            {
                var component = components[c];
                while (chunks.Count <= component)
                    chunks.Add([]);

                chunks[component].Add(polygons[c]);
            }

            return chunks;
        }

        /// <summary>
        /// Groups polygons into connected shells and reorders the list so each shell is contiguous, as
        /// <see cref="GeometryPart"/> describes a part as a face range. Returns the component index of
        /// every polygon; empty when the result is empty.
        /// </summary>
        public int[] OrderByComponent(List<Polygon> polygons)
        {
            var count = polygons.Count;
            if (count == 0)
                return [];

            _order.Clear();
            _byRoot.Clear();
            var components = ComponentsOf(polygons, _order, _byRoot);

            _reordered.Clear();
            _components.Clear();
            var component = 0;
            foreach (var root in _order)
            {
                foreach (var polygonIndex in _byRoot[root])
                {
                    _reordered.Add(polygons[polygonIndex]);
                    _components.Add(component);
                }

                component++;
            }

            polygons.Clear();
            polygons.AddRange(_reordered);

            // Indexed like the reordered list, which is what the emitter walks.
            return _components.ToArray();
        }

        /// <summary>
        /// The connected shells of a polygon list, by shared welded edges. Two polygons are in one
        /// shell when they share an edge, so separate chunks of one mesh come out as separate
        /// components and a single closed hull comes out as one.
        ///
        /// <para>This is the same grouping <see cref="OrderByComponent"/> uses on a result; it is
        /// exposed separately because an <em>operand</em> needs it too, to recover the parts a mesh
        /// did not declare in its part table. The caller supplies the two grouping maps so a hot
        /// loop can reuse them instead of allocating per call.</para>
        /// </summary>
        public int[] ComponentsOf(List<Polygon> polygons, List<int> rootOrder, Dictionary<int, List<int>> byRoot)
        {
            var count = polygons.Count;
            if (count == 0)
                return [];

            var parent = new int[count];
            for (var i = 0; i < count; i++)
            {
                parent[i] = i;
            }

            _edgeOwner.Clear();
            for (var i = 0; i < count; i++)
            {
                var vertices = polygons[i].Vertices;
                for (var c = 0; c < vertices.Length; c++)
                {
                    var a = vertices[c].PointId;
                    var b = vertices[(c + 1) % vertices.Length].PointId;
                    var key = a < b ? (a, b) : (b, a);
                    if (!_edgeOwner.TryGetValue(key, out var other))
                    {
                        _edgeOwner[key] = i;
                        continue;
                    }

                    Union(parent, other, i);
                }
            }

            rootOrder.Clear();
            byRoot.Clear();
            _componentOfRoot.Clear();
            var components = new int[count];
            for (var i = 0; i < count; i++)
            {
                var root = Find(parent, i);
                if (!_componentOfRoot.TryGetValue(root, out var component))
                {
                    component = rootOrder.Count;
                    rootOrder.Add(root);
                    byRoot[root] = [];
                    _componentOfRoot[root] = component;
                }

                byRoot[root].Add(i);
                components[i] = component;
            }

            return components;
        }

        /// <summary>
        /// Writes the surviving polygons out. Points are compacted to those actually referenced and
        /// vertices are keyed by point id, so the output keeps the sharing the kernel built and the
        /// mesh closes; degenerate polygons left behind by welding are dropped.
        ///
        /// <para>Every part gets its own points. Parts of one result are separate solids, and two that
        /// touch would share a face across parts, which an index-based topology check reads as
        /// non-manifold. A fracture emits its cells the same way.</para>
        /// </summary>
        public void Emit(MeshGeometry target, List<Polygon> polygons, int[] components, int[] partSeeds, Operations operation)
        {
            if (_pointRemap.Length < _weld.Positions.Count)
            {
                _pointRemap = new int[_weld.Positions.Count];
                Array.Fill(_pointRemap, -1);
            }

            _positions.Clear();
            _faceOffsets.Clear();
            _cornerPoints.Clear();
            _cornerAttributes.Clear();
            _faceCut.Clear();
            _faceComponents.Clear();
            _faceOffsets.Add(0);

            var currentComponent = int.MinValue;
            for (var polygonIndex = 0; polygonIndex < polygons.Count; polygonIndex++)
            {
                var component = components[polygonIndex];
                if (component != currentComponent)
                {
                    currentComponent = component;
                    foreach (var remappedPoint in _remappedPoints)
                        _pointRemap[remappedPoint] = -1;

                    _remappedPoints.Clear();
                }

                var polygon = polygons[polygonIndex];
                var faceStart = _cornerPoints.Count;
                for (var i = 0; i < polygon.CornerCount; i++)
                {
                    var vertex = polygon.Vertices[i];
                    var mapped = _pointRemap[vertex.PointId];
                    if (mapped < 0)
                    {
                        mapped = _positions.Count;
                        _pointRemap[vertex.PointId] = mapped;
                        _remappedPoints.Add(vertex.PointId);
                        _positions.Add(_weld.Positions[vertex.PointId] + _origin);
                    }

                    if (_cornerPoints.Count > faceStart && _cornerPoints[^1] == mapped)
                        continue;

                    _cornerPoints.Add(mapped);
                    _cornerAttributes.Add(vertex.Attributes);
                }

                while (_cornerPoints.Count - faceStart > 1 && _cornerPoints[^1] == _cornerPoints[faceStart])
                {
                    _cornerPoints.RemoveAt(_cornerPoints.Count - 1);
                    _cornerAttributes.RemoveAt(_cornerAttributes.Count - 1);
                }

                if (_cornerPoints.Count - faceStart < 3)
                {
                    _cornerPoints.RemoveRange(faceStart, _cornerPoints.Count - faceStart);
                    _cornerAttributes.RemoveRange(faceStart, _cornerAttributes.Count - faceStart);
                    continue;
                }

                _faceOffsets.Add(_cornerPoints.Count);

                // A face that came from an operand rather than from the left input is new surface the
                // operation exposed, which is what a downstream selection wants to colour. A union has no
                // such surface - its interface is gone entirely - so it marks nothing.
                var faceIsCut = operation != Operations.Union && polygon.SourceOperand != 0 ? 1f : 0f;
                _faceCut.Add(faceIsCut);
                _faceComponents.Add(components[polygonIndex]);
            }

            target.Positions = _positions.ToArray();
            target.FaceCornerOffsets = _faceOffsets.ToArray();
            target.CornerPointIndices = _cornerPoints.ToArray();
            target.Attributes.Clear();

            var cornerCount = _cornerPoints.Count;
            if (cornerCount > 0)
            {
                foreach (var column in _schema.Columns)
                {
                    WriteCornerColumn(target, column, cornerCount);
                }
            }

            var faceCount = target.FaceCount;
            var isCut = target.Attributes.GetOrCreate<float>(GeometryAttributeNames.IsCut, AttributeDomain.Face, faceCount);
            var selection = target.Attributes.GetOrCreate<float>(GeometryAttributeNames.Selection, AttributeDomain.Face, faceCount);
            for (var face = 0; face < faceCount; face++)
            {
                isCut.Values[face] = _faceCut[face];
                selection.Values[face] = _faceCut[face];
            }

            target.Parts = components.Length == 0 ? [] : BuildParts(target, partSeeds);
            target.InvalidateTopologyCaches();
        }

        private void WriteCornerColumn(MeshGeometry target, AttributeColumn column, int cornerCount)
        {
            switch (column.ElementSize)
            {
                case 1:
                {
                    var attribute = target.Attributes.GetOrCreate<float>(column.Name, AttributeDomain.Corner, cornerCount);
                    for (var c = 0; c < cornerCount; c++)
                        attribute.Values[c] = _cornerAttributes[c][column.Offset];
                    break;
                }

                case 2:
                {
                    var attribute = target.Attributes.GetOrCreate<Vector2>(column.Name, AttributeDomain.Corner, cornerCount);
                    for (var c = 0; c < cornerCount; c++)
                    {
                        var values = _cornerAttributes[c];
                        attribute.Values[c] = new Vector2(values[column.Offset], values[column.Offset + 1]);
                    }

                    break;
                }

                case 3:
                {
                    var attribute = target.Attributes.GetOrCreate<Vector3>(column.Name, AttributeDomain.Corner, cornerCount);
                    for (var c = 0; c < cornerCount; c++)
                    {
                        var values = _cornerAttributes[c];
                        attribute.Values[c] = new Vector3(values[column.Offset], values[column.Offset + 1], values[column.Offset + 2]);
                    }

                    break;
                }

                default:
                {
                    var attribute = target.Attributes.GetOrCreate<Vector4>(column.Name, AttributeDomain.Corner, cornerCount);
                    for (var c = 0; c < cornerCount; c++)
                    {
                        var values = _cornerAttributes[c];
                        attribute.Values[c] = new Vector4(values[column.Offset], values[column.Offset + 1],
                                                          values[column.Offset + 2], values[column.Offset + 3]);
                    }

                    break;
                }
            }
        }

        /// <summary>
        /// Turns the per-face component index recorded during <see cref="Emit"/> into the part table.
        /// Polygons were ordered by component, so each part is a contiguous face range. A part keeps the
        /// seed index of the operand part it came from, which is what lets a downstream op map a result
        /// piece back to the fracture cell that cut it.
        /// </summary>
        private GeometryPart[] BuildParts(MeshGeometry geometry, int[] partSeeds)
        {
            var faceCount = geometry.FaceCount;
            if (faceCount == 0)
                return [];

            var parts = new List<GeometryPart>();
            var range = new List<int>();
            var face = 0;
            while (face < faceCount && face < _faceComponents.Count)
            {
                var component = _faceComponents[face];
                var start = face;
                range.Clear();
                while (face < faceCount && _faceComponents[face] == component)
                {
                    range.Add(face);
                    face++;
                }

                var seed = component >= 0 && component < partSeeds.Length ? partSeeds[component] : 0;
                var pivot = MeshVolumeCentroid.Compute(geometry, range);
                parts.Add(new GeometryPart(start, face - start, pivot, parts.Count, seed));
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

        private readonly PointWeld _weld;
        private readonly AttributeSchema _schema;
        private readonly Vector3 _origin;
        private readonly float _epsilon;
        private readonly float _gridCell;
        private readonly float _gridTolerance;
        private readonly float _gridToleranceSq;
        private int[] _pointRemap = [];

        /// <summary>The weld points mapped for the part currently being emitted, so the next part can reset them.</summary>
        private readonly List<int> _remappedPoints = [];
        private readonly List<Vector3> _positions = [];
        private readonly List<int> _faceOffsets = [];
        private readonly List<int> _cornerPoints = [];
        private readonly List<float[]> _cornerAttributes = [];
        private readonly List<float> _faceCut = [];
        private readonly List<int> _faceComponents = [];
        private readonly HashSet<int> _usedPoints = [];
        private readonly HashSet<int> _seenInPolygon = [];
        private readonly Dictionary<(long, long, long), List<int>> _grid = [];
        private readonly List<Vert> _repairedVertices = [];
        private readonly List<EdgeHit> _edgeHits = [];
        private readonly List<Polygon> _reordered = [];
        private readonly List<int> _components = [];
        private readonly Dictionary<(int, int), int> _edgeOwner = [];
        private readonly Dictionary<int, int> _componentOfRoot = [];
        private readonly Dictionary<int, List<int>> _byRoot = [];
        private readonly List<int> _order = [];
    }

    /// <summary>Classifier slack, as a fraction of the smallest operand's extent.</summary>
    private const float PlaneToleranceFactor = 1e-6f;

    /// <summary>
    /// Weld radius, as a fraction of extent. Splitting the same edge from two neighbouring faces
    /// computes the crossing point twice with different rounding, so the two results have to land in
    /// one bucket - this is the slack that lets them, and it stays well below any real feature.
    /// </summary>
    private const float WeldToleranceFactor = 4e-6f;

    /// <summary>Planarity threshold of a face, relative to its longest edge.</summary>
    private const float PlanarityTolerance = 1e-4f;

    /// <summary>
    /// Face area, as a fraction of the squared extent, below which a polygon is a sliver. Eight orders of
    /// magnitude under a real face, so it only ever catches the debris of a near-tangent cut.
    /// </summary>

    /// <summary>
    /// How far off an edge a point may sit and still be treated as lying on it during the T-junction
    /// pass. Generous next to the weld radius: the point and the edge were computed independently, so
    /// the perpendicular error is a rounding effect rather than a real gap.
    /// </summary>
    private const float TJumpOnEdgeTolerance = 8f;

    /// <summary>Sine-of-angle slack for calling a corner convex, relative to the face's squared size.</summary>
    private const float ConvexityTolerance = 1e-9f;

    /// <summary>Cells per axis of the uniform grid the T-junction pass indexes points with.</summary>
    private const float GridResolution = 256f;

    /// <summary>Cap on the samples taken along one edge while looking for points lying on it.</summary>
    private const int MaxEdgeSamples = 8192;

    /// <summary>
    /// Corner budget for the fold. Each operand part fans every branch, so the live polygon set grows
    /// by a factor per operand and a many-part operand multiplies out quickly. This is a backstop
    /// against exhausting memory on a pathological composition, not a tuning knob: it sits far above
    /// any result that would be useful to draw, so reaching it means the arrangement is wrong.
    /// </summary>
    private const int MaxLiveCorners = 20_000_000;
}
