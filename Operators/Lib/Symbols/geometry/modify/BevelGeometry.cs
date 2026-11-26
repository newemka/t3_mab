using Lib.Utils;
using System;
using System.Collections.Generic;
using System.Threading;
using T3.Core.Utils;

namespace Lib.geometry.modify;

/// <summary>
/// Edge bevel for Geometry: every face is inset, every edge becomes a profile strip between its two
/// inset faces, and every vertex becomes a patch closing the corner. Faces meeting at less than the
/// flat angle are merged first, so flat edges get no strip. Only the selected edges are inset and
/// stripped; the others stay sharp.
/// </summary>
[Guid("dbaeebea-8d46-416f-9624-867af10d9c07")]
internal sealed class BevelGeometry : Instance<BevelGeometry>, IProgressProvider, IStatusProvider
{
    private enum Methods
    {
        Fast,
        StraightSkeleton,
    }

    /// <summary>Which edges get a bevel; caps and walls are told apart by the IsSide face attribute.</summary>
    private enum EdgeSelections
    {
        All,
        CapEdges,
        WallEdges,
    }

    private readonly record struct Settings(float Width, int Segments, float Roundness, bool FlatShading, bool RoundCorners,
                                            Methods Method, float FlatAngle, EdgeSelections EdgeSelection);

    [Output(Guid = "48342188-0139-4ea4-943d-a5b5d59b7a4a")]
    public readonly Slot<Geometry> Result = new();

    public BevelGeometry()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var original = Geometry.GetValue(context);
        var source = original != null ? _polygons.GetPolygons(original) : null;
        var edgeSelection = (EdgeSelections)Math.Clamp(EdgeSelection.GetValue(context), 0, (int)EdgeSelections.WallEdges);
        _statusMessage = string.Empty;

        List<byte> faceKinds = null;
        if (source != null && edgeSelection != EdgeSelections.All)
        {
            if (!TryCollectFaceKinds(source, out faceKinds))
            {
                _statusMessage = "No IsSide attribute; beveling all edges.";
                edgeSelection = EdgeSelections.All;
            }
        }

        var method = (Methods)Math.Clamp(Method.GetValue(context), 0, (int)Methods.StraightSkeleton);
        if (method == Methods.StraightSkeleton && edgeSelection != EdgeSelections.All)
        {
            _statusMessage = "Edge selection uses the Fast method; beveling the selected edges with it.";
            method = Methods.Fast;
        }

        var settings = new Settings(Width.GetValue(context),
                                    Math.Clamp(Segments.GetValue(context), 1, 16),
                                    Math.Clamp(Roundness.GetValue(context), 0f, 1f),
                                    FlatShading.GetValue(context),
                                    RoundCorners.GetValue(context),
                                    method,
                                    Math.Clamp(FlatAngle.GetValue(context), 0f, 90f),
                                    edgeSelection);

        if (source == null || source.PrimitiveCount == 0 || settings.Width <= 0)
        {
            Result.Value = original;
            return;
        }

        if (Async.GetValue(context))
        {
            var inputsVersion = HashCode.Combine(source.Version, source.GetHashCode(), settings);
            var result = _asyncComputation.Update(context, Result, inputsVersion,
                                                  token =>
                                                  {
                                                      var target = new Geometry();
                                                      Build(target, source, settings, faceKinds, token);
                                                      return target;
                                                  });
            Result.Value = result != null ? _curves.Combine(result, original) : original;
            return;
        }

        _asyncComputation.WaitForPending(Result);
        Build(_output, source, settings, faceKinds, CancellationToken.None);
        Result.Value = _curves.Combine(_output, original);
    }

    private void Build(Geometry target, Geometry source, Settings settings, List<byte> faceKinds, CancellationToken token)
    {
        CollectBridgeEdges(source);
        List<byte> mergedKinds = null;
        var mesh = settings.FlatAngle > 0
                       ? MergeFlatFaces(source, settings.FlatAngle, faceKinds, out mergedKinds)
                       : source;
        var edgeKinds = mesh == source ? faceKinds : mergedKinds;
        _faceKinds = edgeKinds;
        var offsets = mesh.PrimitiveVertexOffsets;
        var edges = mesh.Edges;
        var segments = settings.Segments;
        PrepareEdgeSelection(edges, settings.EdgeSelection, edgeKinds);

        _outPositions.Clear();
        _outNormals.Clear();
        _outFaceOffsets.Clear();
        _outCorners.Clear();
        _outFaceOffsets.Add(0);

        ComputeFaceNormals(mesh, ref _faceNormals);
        _asyncComputation.ReportProgress(0.05f);

        // --- A: inset point per input corner --------------------------------
        if (_insetPointIds.Length != mesh.VertexCount)
            _insetPointIds = new int[mesh.VertexCount];

        if (settings.Method == Methods.StraightSkeleton && AllInteriorEdgesBeveled(edges))
        {
            InsetBySkeleton(mesh, settings.Width, token);
        }
        else
        {
            InsetFast(mesh, settings.Width);
        }

        WeldSharpEdgeSeams(mesh, settings.Width);

        // --- B: inset faces (same winding as the source) --------------------
        var cornerEdges = edges.CornerEdgeIndices;
        for (var faceIndex = 0; faceIndex < mesh.PrimitiveCount; faceIndex++)
        {
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            for (var c = start; c < end; c++)
            {
                if (TryGetSplitCapCorner(mesh, faceIndex, c, cornerEdges, out var before, out var after))
                {
                    _polygon.Add(before);
                    _polygon.Add(after);
                }
                else
                {
                    _polygon.Add(_insetPointIds[c]);
                }
            }

            AddPolygon(_polygon);
        }

        // --- C: edge strips --------------------------------------------------
        // ring point ids per edge and end vertex, ordered from Face0's inset to Face1's
        var edgeRings = new Dictionary<(int EdgeIndex, int PointId), int[]>();
        _ringsByEnds.Clear();

        for (var edgeIndex = 0; edgeIndex < edges.Edges.Length; edgeIndex++)
        {
            token.ThrowIfCancellationRequested();
            _asyncComputation.ReportProgress(0.15f + 0.55f * edgeIndex / edges.Edges.Length);
            var edge = edges.Edges[edgeIndex];
            if (edge.Face1 < 0)
                continue; // boundary edge - nothing to connect

            var beveled = _edgeBeveled[edgeIndex];
            var ringA = BuildEndRing(mesh, edge, edge.PointA, segments, settings.Roundness, beveled, edgeRings, edgeIndex);
            var ringB = BuildEndRing(mesh, edge, edge.PointB, segments, settings.Roundness, beveled, edgeRings, edgeIndex);
            if (!beveled)
                continue; // sharp edge: its flat rings only close the corner patches

            if (ringA == ringB)
                continue; // the edge collapsed in both inset faces

            // Face0 traverses the edge either A->B or B->A; the strip must run opposite
            // to Face0's inset edge to face outward.
            var startRing = TraversesInOrder(mesh, edge.Face0, edge.PointA, edge.PointB) ? ringA : ringB;
            var endRing = startRing == ringA ? ringB : ringA;

            for (var s = 0; s < segments; s++)
            {
                _polygon.Add(endRing[s]);
                _polygon.Add(startRing[s]);
                _polygon.Add(startRing[s + 1]);
                _polygon.Add(endRing[s + 1]);
                AddPolygon(_polygon);
            }
        }

        // --- D: corner fans --------------------------------------------------
        token.ThrowIfCancellationRequested();
        _asyncComputation.ReportProgress(0.7f);
        BuildCornerFans(mesh, edgeRings, segments, settings.Roundness, settings.RoundCorners);

        // --- E: publish ------------------------------------------------------
        target.Positions = _outPositions.ToArray();
        target.PrimitiveVertexOffsets = _outFaceOffsets.ToArray();
        target.SetAllPrimitiveKinds(PrimitiveKinds.Polygon);
        target.VertexPoints = _outCorners.ToArray();
        target.Parts = [];
        target.Attributes.Clear();
        if (!settings.FlatShading)
        {
            // Without the attribute, the compile step falls back to per-face normals - the hard/faceted look.
            var normals = target.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Vertex, _outCorners.Count);
            for (var c = 0; c < _outCorners.Count; c++)
            {
                normals.Values[c] = _outNormals[_outCorners[c]];
            }
        }

        target.InvalidateTopologyCaches();
    }

    /// <summary>Reads the IsSide face attribute into 1 for walls and 0 for caps; false when it is missing.</summary>
    private static bool TryCollectFaceKinds(Geometry source, out List<byte> faceKinds)
    {
        faceKinds = null;
        if (!source.Attributes.TryGet<float>(GeometryAttributeNames.IsSide, AttributeDomain.Primitive, out var isSide)
            || isSide.Values.Length < source.PrimitiveCount)
            return false;

        faceKinds = new List<byte>(source.PrimitiveCount);
        for (var faceIndex = 0; faceIndex < source.PrimitiveCount; faceIndex++)
            faceKinds.Add(isSide.Values[faceIndex] != 0 ? (byte)1 : (byte)0);

        return true;
    }

    /// <summary>Point pairs of the artificial bridge edges of [Extrude], which never get a bevel.</summary>
    private void CollectBridgeEdges(Geometry source)
    {
        _bridgePointPairs.Clear();
        if (!source.Attributes.TryGet<float>(GeometryAttributeNames.IsBridge, AttributeDomain.Edge, out var isBridge))
            return;

        var edges = source.Edges.Edges;
        var count = Math.Min(edges.Length, isBridge.Values.Length);
        for (var edgeIndex = 0; edgeIndex < count; edgeIndex++)
        {
            if (isBridge.Values[edgeIndex] != 0)
                _bridgePointPairs.Add((edges[edgeIndex].PointA, edges[edgeIndex].PointB));
        }
    }

    /// <summary>
    /// Marks the edges that get a strip and drive the inset. Cap edges are those with at least one cap face
    /// (including the rim between cap and wall), wall edges those between two wall faces.
    /// </summary>
    private void PrepareEdgeSelection(EdgeTopology topology, EdgeSelections selection, List<byte> faceKinds)
    {
        var edgeCount = topology.Edges.Length;
        if (_edgeBeveled.Length < edgeCount)
            _edgeBeveled = new bool[edgeCount];

        for (var edgeIndex = 0; edgeIndex < edgeCount; edgeIndex++)
        {
            var edge = topology.Edges[edgeIndex];
            var isBridge = IsBridgeEdge(edge);
            if (selection == EdgeSelections.All)
            {
                _edgeBeveled[edgeIndex] = !isBridge;
                continue;
            }

            var face0IsWall = faceKinds[edge.Face0] != 0;
            var face1IsWall = edge.Face1 >= 0 ? faceKinds[edge.Face1] != 0 : face0IsWall;
            _edgeBeveled[edgeIndex] = !isBridge && selection switch
                                      {
                                          EdgeSelections.CapEdges  => !(face0IsWall && face1IsWall),
                                          EdgeSelections.WallEdges => face0IsWall && face1IsWall,
                                          _                        => true,
                                      };
        }
    }

    private bool IsBridgeEdge(EdgeTopology.Edge edge)
    {
        foreach (var (a, b) in _bridgePointPairs)
        {
            if (a == edge.PointA && b == edge.PointB || a == edge.PointB && b == edge.PointA)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Merges faces that meet at less than the flat angle into single polygons and drops points that only
    /// split a straight edge, so flat edges get no bevel strip. A region whose outline is not one simple
    /// convex loop (a hole, a pinched or concave corner) keeps its faces.
    /// </summary>
    private Geometry MergeFlatFaces(Geometry source, float flatAngleDegrees, List<byte> faceKinds, out List<byte> mergedKinds)
    {
        mergedKinds = null;
        _mergedFaceKinds.Clear();
        var faceCount = source.PrimitiveCount;
        var offsets = source.PrimitiveVertexOffsets;
        var cornerPoints = source.VertexPoints;
        var positions = source.Positions;
        var edges = source.Edges;
        var minCos = MathF.Cos(flatAngleDegrees * MathF.PI / 180f);
        ComputeFaceNormals(source, ref _faceNormals);

        var parent = new int[faceCount];
        for (var faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            parent[faceIndex] = faceIndex;
        }

        foreach (var edge in edges.Edges)
        {
            if (edge.Face1 >= 0
                && Vector3.Dot(_faceNormals[edge.Face0], _faceNormals[edge.Face1]) >= minCos
                && (faceKinds == null || faceKinds[edge.Face0] == faceKinds[edge.Face1]))
                Union(parent, edge.Face0, edge.Face1);
        }

        // Faces grouped by region (counting sort on the root)
        var regionStart = new int[faceCount + 1];
        for (var faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            regionStart[Find(parent, faceIndex) + 1]++;
        }

        for (var i = 0; i < faceCount; i++)
        {
            regionStart[i + 1] += regionStart[i];
        }

        var members = new int[faceCount];
        var fill = (int[])regionStart.Clone();
        for (var faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            members[fill[Find(parent, faceIndex)]++] = faceIndex;
        }

        var mergedOffsets = new List<int>(faceCount + 1) { 0 };
        var mergedCorners = new List<int>(cornerPoints.Length);
        var regionStates = new byte[faceCount]; // 0 undecided, 1 merged, 2 kept as is
        var nextPoint = new Dictionary<int, int>();
        var loop = new List<int>();
        var changed = false;

        for (var faceIndex = 0; faceIndex < faceCount; faceIndex++)
        {
            var root = Find(parent, faceIndex);
            var regionSize = regionStart[root + 1] - regionStart[root];
            if (regionSize > 1 && regionStates[root] == 0)
            {
                regionStates[root] = TryTraceRegionOutline(source, parent, root, members, regionStart, nextPoint, loop)
                                     && IsConvex(loop, positions, _faceNormals[faceIndex])
                                         ? (byte)1
                                         : (byte)2;
                if (regionStates[root] == 1)
                {
                    mergedCorners.AddRange(loop);
                    mergedOffsets.Add(mergedCorners.Count);
                    changed = true;
                    if (faceKinds != null)
                        _mergedFaceKinds.Add(faceKinds[faceIndex]);
                }
            }

            if (regionSize > 1 && regionStates[root] == 1)
                continue;

            for (var c = offsets[faceIndex]; c < offsets[faceIndex + 1]; c++)
            {
                mergedCorners.Add(cornerPoints[c]);
            }

            mergedOffsets.Add(mergedCorners.Count);
            if (faceKinds != null)
                _mergedFaceKinds.Add(faceKinds[faceIndex]);
        }

        var merged = new Geometry
                         {
                             Positions = positions,
                             PrimitiveVertexOffsets = mergedOffsets.ToArray(),
                             VertexPoints = mergedCorners.ToArray(),
                             Parts = [],
                         };
        merged.SetAllPrimitiveKinds(PrimitiveKinds.Polygon);

        // A point with only two edges that continue each other in a straight line splits nothing visible
        var pointEdgeCounts = new int[positions.Length];
        foreach (var edge in merged.Edges.Edges)
        {
            pointEdgeCounts[edge.PointA]++;
            pointEdgeCounts[edge.PointB]++;
        }

        var reducedOffsets = new List<int>(mergedOffsets.Count) { 0 };
        var reducedCorners = new List<int>(mergedCorners.Count);
        for (var faceIndex = 0; faceIndex < merged.PrimitiveCount; faceIndex++)
        {
            var start = merged.PrimitiveVertexOffsets[faceIndex];
            var end = merged.PrimitiveVertexOffsets[faceIndex + 1];
            var faceStart = reducedCorners.Count;
            for (var c = start; c < end; c++)
            {
                var pointId = merged.VertexPoints[c];
                if (pointEdgeCounts[pointId] == 2)
                {
                    var previous = positions[merged.VertexPoints[c == start ? end - 1 : c - 1]];
                    var next = positions[merged.VertexPoints[c + 1 == end ? start : c + 1]];
                    var p = positions[pointId];
                    var toPoint = p - previous;
                    var fromPoint = next - p;
                    var lengths = toPoint.Length() * fromPoint.Length();
                    if (lengths > 1e-20f && Vector3.Dot(toPoint, fromPoint) >= minCos * lengths)
                        continue;
                }

                reducedCorners.Add(pointId);
            }

            if (reducedCorners.Count - faceStart < 3)
            {
                // Degenerate leftover - keep the face as it was
                reducedCorners.RemoveRange(faceStart, reducedCorners.Count - faceStart);
                for (var c = start; c < end; c++)
                {
                    reducedCorners.Add(merged.VertexPoints[c]);
                }
            }

            changed |= reducedCorners.Count - faceStart != end - start;
            reducedOffsets.Add(reducedCorners.Count);
        }

        if (!changed)
            return source;

        merged.PrimitiveVertexOffsets = reducedOffsets.ToArray();
        merged.VertexPoints = reducedCorners.ToArray();
        merged.SetAllPrimitiveKinds(PrimitiveKinds.Polygon);
        merged.InvalidateTopologyCaches();
        mergedKinds = faceKinds != null ? _mergedFaceKinds : null;
        return merged;
    }

    /// <summary>Chains the outline edges of a flat region into one loop; false if it isn't a single simple loop.</summary>
    private static bool TryTraceRegionOutline(Geometry source, int[] parent, int root, int[] members, int[] regionStart,
                                              Dictionary<int, int> nextPoint, List<int> loop)
    {
        var edges = source.Edges;
        var offsets = source.PrimitiveVertexOffsets;
        var cornerPoints = source.VertexPoints;
        nextPoint.Clear();
        loop.Clear();
        var firstPoint = -1;

        for (var m = regionStart[root]; m < regionStart[root + 1]; m++)
        {
            var faceIndex = members[m];
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            for (var c = start; c < end; c++)
            {
                var edge = edges.Edges[edges.CornerEdgeIndices[c]];
                var otherFace = edge.Face0 == faceIndex ? edge.Face1 : edge.Face0;
                if (otherFace >= 0 && Find(parent, otherFace) == root)
                    continue;

                var from = cornerPoints[c];
                if (!nextPoint.TryAdd(from, cornerPoints[c + 1 == end ? start : c + 1]))
                    return false; // pinched: the outline passes a point twice

                if (firstPoint < 0)
                    firstPoint = from;
            }
        }

        if (firstPoint < 0)
            return false;

        var current = firstPoint;
        do
        {
            loop.Add(current);
            if (!nextPoint.TryGetValue(current, out current) || loop.Count > nextPoint.Count)
                return false;
        }
        while (current != firstPoint);

        return loop.Count == nextPoint.Count && loop.Count >= 3;
    }

    /** Polygons are fan triangulated downstream, so a merged face must not turn the wrong way anywhere (straight runs are fine). */
    private static bool IsConvex(List<int> loop, Vector3[] positions, Vector3 normal)
    {
        for (var i = 0; i < loop.Count; i++)
        {
            var previous = positions[loop[i == 0 ? loop.Count - 1 : i - 1]];
            var p = positions[loop[i]];
            var next = positions[loop[(i + 1) % loop.Count]];
            var toPoint = p - previous;
            var fromPoint = next - p;
            if (Vector3.Dot(Vector3.Cross(toPoint, fromPoint), normal) < -1e-4f * toPoint.Length() * fromPoint.Length())
                return false;
        }

        return true;
    }

    /// <summary>
    /// Moves every corner inward along its miter with a width limited per point: a corner uses at most
    /// <see cref="MaxShareOfDistance"/> of each adjacent edge and of its distance to the other edges of
    /// the face, so short edges and thin faces only narrow the bevel around them.
    /// </summary>
    private void InsetFast(Geometry mesh, float width)
    {
        var positions = mesh.Positions;
        var offsets = mesh.PrimitiveVertexOffsets;
        var cornerPoints = mesh.VertexPoints;
        ComputeCornerVelocities(mesh);

        if (_pointWidths.Length != positions.Length)
            _pointWidths = new float[positions.Length];
        Array.Fill(_pointWidths, width);

        for (var faceIndex = 0; faceIndex < mesh.PrimitiveCount; faceIndex++)
        {
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            for (var c = start; c < end; c++)
            {
                var next = c + 1 == end ? start : c + 1;
                var pointId = cornerPoints[c];
                var p = positions[pointId];
                var edge = positions[cornerPoints[next]] - p;
                var length = edge.Length();
                if (length > 1e-12f)
                {
                    var direction = edge / length;
                    LimitPointWidth(pointId, length, Vector3.Dot(_cornerVelocities[c], direction));
                    LimitPointWidth(cornerPoints[next], length, -Vector3.Dot(_cornerVelocities[next], direction));
                }

                var speed = _cornerVelocities[c].Length();
                for (var other = start; other < end; other++)
                {
                    var otherNext = other + 1 == end ? start : other + 1;
                    if (other == c || otherNext == c)
                        continue;

                    var distance = DistanceToSegment(p, positions[cornerPoints[other]], positions[cornerPoints[otherNext]]);
                    LimitPointWidth(pointId, distance, speed);
                }
            }
        }

        for (var faceIndex = 0; faceIndex < mesh.PrimitiveCount; faceIndex++)
        {
            for (var c = offsets[faceIndex]; c < offsets[faceIndex + 1]; c++)
            {
                var pointId = cornerPoints[c];
                _insetPointIds[c] = AddPoint(positions[pointId] + _cornerVelocities[c] * _pointWidths[pointId], _faceNormals[faceIndex]);
            }
        }
    }

    /** Caps a point's width so moving at <paramref name="speed"/> toward something uses at most its share of the distance. */
    private void LimitPointWidth(int pointId, float distance, float speed)
    {
        if (speed > 1e-6f)
            _pointWidths[pointId] = MathF.Min(_pointWidths[pointId], MaxShareOfDistance * distance / speed);
    }

    /// <summary>
    /// Insets every face with its straight skeleton: all edges move inward at the same speed, and an edge
    /// that shrinks to nothing is removed and its two corners merge into one inset point. A face that
    /// collapses before the full width stops there. Split events are not computed: a concave face stops
    /// short of the width where a concave corner would reach another edge.
    /// </summary>
    private void InsetBySkeleton(Geometry mesh, float width, CancellationToken token)
    {
        var positions = mesh.Positions;
        var offsets = mesh.PrimitiveVertexOffsets;
        var cornerPoints = mesh.VertexPoints;
        ComputeCornerVelocities(mesh);

        for (var faceIndex = 0; faceIndex < mesh.PrimitiveCount; faceIndex++)
        {
            if ((faceIndex & 255) == 0)
                token.ThrowIfCancellationRequested();

            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            var count = end - start;
            var faceNormal = _faceNormals[faceIndex];

            _skeleton.Clear();
            _cornerSlots.Clear(); // a cancelled build can leave the previous face's slots behind
            if (_slotPointIds.Length < count)
                _slotPointIds = new int[count];

            for (var c = start; c < end; c++)
            {
                var previous = c == start ? end - 1 : c - 1;
                var next = c + 1 == end ? start : c + 1;
                var p = positions[cornerPoints[c]];
                _skeleton.Add(new StraightSkeletonInset.Vertex(p,
                                                               _cornerVelocities[c],
                                                               StraightSkeletonInset.EdgeInwardNormal(positions[cornerPoints[previous]], p, faceNormal),
                                                               StraightSkeletonInset.EdgeInwardNormal(p, positions[cornerPoints[next]], faceNormal),
                                                               c - start == 0 ? count - 1 : c - start - 1,
                                                               c - start + 1 == count ? 0 : c - start + 1));
                _cornerSlots.Add(c - start);
            }

            var limit = _skeleton.Limit(width, faceNormal, token);

            // Corners that arrive at the same spot (a face shrunk to a point or ridge) share one point
            var weldDistanceSq = MathF.Max(limit * CollapseWeldFactor, 1e-7f);
            weldDistanceSq *= weldDistanceSq;
            for (var slot = 0; slot < count; slot++)
            {
                var vertex = _skeleton[slot];
                _slotPointIds[slot] = -1;
                if (!vertex.Alive)
                    continue;

                var position = vertex.Origin + vertex.Velocity * limit;
                var previousSlot = _skeleton.ResolveSlot(vertex.Previous);
                if (previousSlot < slot && _slotPointIds[previousSlot] >= 0
                    && Vector3.DistanceSquared(_outPositions[_slotPointIds[previousSlot]], position) < weldDistanceSq)
                {
                    _slotPointIds[slot] = _slotPointIds[previousSlot];
                    continue;
                }

                _slotPointIds[slot] = AddPoint(position, faceNormal);
            }

            for (var local = 0; local < count; local++)
            {
                _insetPointIds[start + local] = _slotPointIds[_skeleton.ResolveSlot(_cornerSlots[local])];
            }
        }
    }

    /// <summary>
    /// Shares the inset points of the two faces along a sharp edge when both land on the same spot, so the
    /// seam closes instead of leaving two coincident open edges.
    /// </summary>
    private void WeldSharpEdgeSeams(Geometry mesh, float width)
    {
        var edges = mesh.Edges;
        var toleranceSq = MathF.Max(width * 1e-3f, 1e-6f);
        toleranceSq *= toleranceSq;

        for (var edgeIndex = 0; edgeIndex < edges.Edges.Length; edgeIndex++)
        {
            var edge = edges.Edges[edgeIndex];
            if (edge.Face1 < 0 || _edgeBeveled[edgeIndex])
                continue;

            WeldSeamEnd(mesh, edge, edge.PointA, toleranceSq);
            WeldSeamEnd(mesh, edge, edge.PointB, toleranceSq);
        }
    }

    private void WeldSeamEnd(Geometry mesh, EdgeTopology.Edge edge, int pointId, float toleranceSq)
    {
        var first = FindCornerInFace(mesh, edge.Face0, pointId);
        var second = FindCornerInFace(mesh, edge.Face1, pointId);
        var firstPoint = _insetPointIds[first];
        var secondPoint = _insetPointIds[second];
        if (firstPoint != secondPoint
            && Vector3.DistanceSquared(_outPositions[firstPoint], _outPositions[secondPoint]) <= toleranceSq)
            _insetPointIds[second] = firstPoint;
    }

    /// <summary>
    /// A cap corner whose two edges stay sharp is cut back to the two wall inset corners where beveled wall
    /// edges meet it, so the wall bevel ends flush against the cap instead of leaving the cap overhanging.
    /// </summary>
    private bool TryGetSplitCapCorner(Geometry mesh, int faceIndex, int corner, int[] cornerEdges, out int before, out int after)
    {
        before = after = -1;
        if (_faceKinds == null || _faceKinds[faceIndex] != 0)
            return false;

        var start = mesh.PrimitiveVertexOffsets[faceIndex];
        var end = mesh.PrimitiveVertexOffsets[faceIndex + 1];
        var previousCorner = corner == start ? end - 1 : corner - 1;
        var nextCorner = corner + 1 == end ? start : corner + 1;
        var previousEdgeIndex = cornerEdges[previousCorner];
        var nextEdgeIndex = cornerEdges[corner];
        if (_edgeBeveled[previousEdgeIndex] || _edgeBeveled[nextEdgeIndex])
            return false;

        var previousEdge = mesh.Edges.Edges[previousEdgeIndex];
        var nextEdge = mesh.Edges.Edges[nextEdgeIndex];
        var previousNeighbor = previousEdge.Face0 == faceIndex ? previousEdge.Face1 : previousEdge.Face0;
        var nextNeighbor = nextEdge.Face0 == faceIndex ? nextEdge.Face1 : nextEdge.Face0;
        if (previousNeighbor < 0 || nextNeighbor < 0 || _faceKinds[previousNeighbor] == 0 || _faceKinds[nextNeighbor] == 0)
            return false;

        var pointId = mesh.VertexPoints[corner];
        before = _insetPointIds[FindCornerInFace(mesh, previousNeighbor, pointId)];
        after = _insetPointIds[FindCornerInFace(mesh, nextNeighbor, pointId)];
        return before != after;
    }

    /// <summary>Points along the bevel profile at one edge end, from Face0's inset point to Face1's.</summary>
    private int[] BuildEndRing(Geometry source, EdgeTopology.Edge edge, int endPointId,
                               int segments, float roundness, bool beveled,
                               Dictionary<(int, int), int[]> edgeRings, int edgeIndex)
    {
        var key = (edgeIndex, endPointId);
        if (edgeRings.TryGetValue(key, out var existing))
            return existing;

        var q0Id = _insetPointIds[FindCornerInFace(source, edge.Face0, endPointId)];
        var q1Id = _insetPointIds[FindCornerInFace(source, edge.Face1, endPointId)];

        // An edge collapsed in both inset faces ends in the same points at both ends - share one ring
        var otherEnd = endPointId == edge.PointA ? edge.PointB : edge.PointA;
        if (edgeRings.TryGetValue((edgeIndex, otherEnd), out var otherRing) && otherRing[0] == q0Id && otherRing[segments] == q1Id)
        {
            edgeRings.Add(key, otherRing);
            return otherRing;
        }

        // A point with only two edges, both between the same two faces, gets no corner patch -
        // both edges must end in the same ring or a gap opens between them
        var endsKey = (endPointId, beveled, Math.Min(q0Id, q1Id), Math.Max(q0Id, q1Id));
        if (_ringsByEnds.TryGetValue(endsKey, out var sharedRing))
        {
            if (sharedRing[0] != q0Id)
            {
                sharedRing = (int[])sharedRing.Clone();
                Array.Reverse(sharedRing);
            }

            edgeRings.Add(key, sharedRing);
            return sharedRing;
        }

        var q0 = _outPositions[q0Id];
        var q1 = _outPositions[q1Id];
        var n0 = _faceNormals[edge.Face0];
        var n1 = _faceNormals[edge.Face1];

        // A sharp edge keeps its two faces on the original line; the flat ring just closes their corner patches
        if (!beveled)
        {
            var straightRing = new int[segments + 1];
            for (var s = 0; s <= segments; s++)
            {
                if (s == 0)
                {
                    straightRing[s] = q0Id;
                    continue;
                }

                if (s == segments)
                {
                    straightRing[s] = q1Id;
                    continue;
                }

                var f = s / (float)segments;
                var blendedNormal = Vector3.Lerp(n0, n1, f);
                var normal = blendedNormal.LengthSquared() > 1e-10f ? Vector3.Normalize(blendedNormal) : n0;
                straightRing[s] = q0Id == q1Id ? q0Id : AddPoint(Vector3.Lerp(q0, q1, f), normal);
            }

            edgeRings.Add(key, straightRing);
            _ringsByEnds.Add(endsKey, straightRing);
            return straightRing;
        }

        // Control point: the original edge, inset along its own direction - the profile
        // bends around it. Projection of the midpoint onto the edge line.
        var edgeStart = source.Positions[edge.PointA];
        var edgeDir = source.Positions[edge.PointB] - edgeStart;
        var edgeLengthSq = edgeDir.LengthSquared();
        var mid = (q0 + q1) * 0.5f;
        var t = edgeLengthSq > 1e-10f ? Vector3.Dot(mid - edgeStart, edgeDir) / edgeLengthSq : 0;
        var control = edgeStart + edgeDir * t;

        var ring = new int[segments + 1];
        ring[0] = q0Id;
        ring[segments] = q1Id;
        for (var s = 1; s < segments; s++)
        {
            var f = (float)s / segments;
            var straight = Vector3.Lerp(q0, q1, f);
            var curvedA = Vector3.Lerp(q0, control, f);
            var curvedB = Vector3.Lerp(control, q1, f);
            var curved = Vector3.Lerp(curvedA, curvedB, f);
            var blended = Vector3.Lerp(n0, n1, f);
            var normal = blended.LengthSquared() > 1e-10f ? Vector3.Normalize(blended) : n0;
            ring[s] = AddPoint(Vector3.Lerp(straight, curved, roundness), normal);
        }

        edgeRings.Add(key, ring);
        _ringsByEnds.Add(endsKey, ring);
        return ring;
    }

    /// <summary>
    /// One patch per source point, closing the hole between the adjacent edge rings. Points joined by an
    /// edge that collapsed in both inset faces share a single patch, as a straight skeleton would.
    /// </summary>
    private void BuildCornerFans(Geometry source, Dictionary<(int, int), int[]> edgeRings, int segments, float roundness, bool roundCorners)
    {
        var edges = source.Edges;
        var positions = source.Positions;

        // adjacency: point -> adjacent interior edges
        var pointEdges = new Dictionary<int, List<int>>();
        var clusterParent = new int[source.PointCount];
        for (var pointId = 0; pointId < clusterParent.Length; pointId++)
        {
            clusterParent[pointId] = pointId;
        }

        for (var edgeIndex = 0; edgeIndex < edges.Edges.Length; edgeIndex++)
        {
            var edge = edges.Edges[edgeIndex];
            if (edge.Face1 < 0)
                continue;

            (pointEdges.TryGetValue(edge.PointA, out var listA) ? listA : pointEdges[edge.PointA] = []).Add(edgeIndex);
            (pointEdges.TryGetValue(edge.PointB, out var listB) ? listB : pointEdges[edge.PointB] = []).Add(edgeIndex);
            if (edgeRings[(edgeIndex, edge.PointA)] == edgeRings[(edgeIndex, edge.PointB)])
                Union(clusterParent, edge.PointA, edge.PointB);
        }

        var clusters = new Dictionary<int, List<(int[] Loop, Vector3 Vertex)>>();
        var loop = new List<int>();
        foreach (var (pointId, adjacentEdges) in pointEdges)
        {
            if (adjacentEdges.Count < 3 || !TryWalkCornerLoop(source, edgeRings, pointId, adjacentEdges, segments, loop))
                continue;

            // A patch that lies flat in an adjacent face is already covered by that face; it only appears
            // where a bevel ends on an edge that stays sharp, e.g. wall edges meeting a sharp cap rim.
            if (IsCoplanarWithAdjacentFace(loop, adjacentEdges, source.Edges))
                continue;

            OrientOutward(loop, positions[pointId]);
            var root = Find(clusterParent, pointId);
            (clusters.TryGetValue(root, out var members) ? members : clusters[root] = []).Add((loop.ToArray(), positions[pointId]));
        }

        var directedEdges = new HashSet<(int, int)>();
        var nextInLoop = new Dictionary<int, int>();
        foreach (var members in clusters.Values)
        {
            if (members.Count > 1 && TryMergeLoops(members, directedEdges, nextInLoop, loop))
            {
                var vertex = Vector3.Zero;
                foreach (var member in members)
                {
                    vertex += member.Vertex;
                }

                vertex /= members.Count;
                OrientOutward(loop, vertex);
                CloseCorner(loop, vertex, segments, roundness, roundCorners);
                continue;
            }

            foreach (var member in members)
            {
                loop.Clear();
                loop.AddRange(member.Loop);
                CloseCorner(loop, member.Vertex, segments, roundness, roundCorners);
            }
        }
    }

    private bool IsCoplanarWithAdjacentFace(List<int> loop, List<int> adjacentEdges, EdgeTopology topology)
    {
        var face = topology.Edges[adjacentEdges[0]].Face0;
        var normal = _faceNormals[face];
        var origin = _outPositions[loop[0]];
        foreach (var id in loop)
        {
            if (MathF.Abs(Vector3.Dot(_outPositions[id] - origin, normal)) > 1e-5f)
                return false;
        }

        return true;
    }

    /// <summary>Walks the fan of faces around a vertex and collects the ring points between them.</summary>
    private static bool TryWalkCornerLoop(Geometry source, Dictionary<(int, int), int[]> edgeRings, int pointId, List<int> adjacentEdges,
                                          int segments, List<int> loop)
    {
        var edges = source.Edges;

        // edge -> its arc -> the far face -> that face's other edge at the vertex
        loop.Clear();
        var currentEdgeIndex = adjacentEdges[0];
        var currentFace = edges.Edges[currentEdgeIndex].Face0;
        for (var i = 0; i < adjacentEdges.Count; i++)
        {
            var edge = edges.Edges[currentEdgeIndex];
            var ring = edgeRings[(currentEdgeIndex, pointId)];

            // The arc runs Face0 -> Face1; append it so we leave on the far side of currentFace.
            var forward = edge.Face0 == currentFace;
            for (var s = 0; s < segments; s++)
            {
                var id = forward ? ring[s] : ring[segments - s];
                if (loop.Count == 0 || loop[^1] != id)
                    loop.Add(id);
            }

            var nextFace = forward ? edge.Face1 : edge.Face0;
            if (!TryFindOtherEdgeAtVertex(source, nextFace, pointId, currentEdgeIndex, out currentEdgeIndex))
                return false;

            currentFace = nextFace;
        }

        while (loop.Count > 1 && loop[^1] == loop[0])
        {
            loop.RemoveAt(loop.Count - 1);
        }

        return loop.Count >= 3;
    }

    /// <summary>
    /// Joins the loops of a corner cluster along the rings they share: neighbouring outward loops run
    /// a shared ring in opposite directions, so those edges cancel and the rest is the merged outline.
    /// </summary>
    private static bool TryMergeLoops(List<(int[] Loop, Vector3 Vertex)> members, HashSet<(int, int)> directedEdges, Dictionary<int, int> nextInLoop,
                                      List<int> result)
    {
        directedEdges.Clear();
        foreach (var (memberLoop, _) in members)
        {
            for (var i = 0; i < memberLoop.Length; i++)
            {
                var from = memberLoop[i];
                var to = memberLoop[(i + 1) % memberLoop.Length];
                if (!directedEdges.Remove((to, from)) && !directedEdges.Add((from, to)))
                    return false;
            }
        }

        nextInLoop.Clear();
        var firstPoint = -1;
        foreach (var (from, to) in directedEdges)
        {
            if (!nextInLoop.TryAdd(from, to))
                return false;

            if (firstPoint < 0)
                firstPoint = from;
        }

        result.Clear();
        if (firstPoint < 0)
            return false;

        var current = firstPoint;
        do
        {
            result.Add(current);
            if (!nextInLoop.TryGetValue(current, out current) || result.Count > nextInLoop.Count)
                return false;
        }
        while (current != firstPoint);

        return result.Count == nextInLoop.Count && result.Count >= 3;
    }

    /** Orients a loop so its polygon normal (Newell) points away from its centroid, toward the vertex it closes. */
    private void OrientOutward(List<int> loop, Vector3 vertex)
    {
        var centroid = Vector3.Zero;
        var loopNormal = Vector3.Zero;
        for (var i = 0; i < loop.Count; i++)
        {
            centroid += _outPositions[loop[i]];
            loopNormal += NewellTerm(_outPositions[loop[i]], _outPositions[loop[(i + 1) % loop.Count]]);
        }

        centroid /= loop.Count;
        if (Vector3.Dot(loopNormal, vertex - centroid) < 0)
            loop.Reverse();
    }

    /** Closes an outward-oriented corner loop with a fan or, for round corners, concentric rings. */
    private void CloseCorner(List<int> loop, Vector3 vertex, int segments, float roundness, bool roundCorners)
    {
        // Fan center: the loop centroid pulled back toward the original vertex, so the
        // patch bulges outward (the centroid itself lies inside the rounded corner).
        var centroid = Vector3.Zero;
        foreach (var id in loop)
        {
            centroid += _outPositions[id];
        }

        centroid /= loop.Count;

        var outwardDir = vertex - centroid;
        var centerNormal = outwardDir.LengthSquared() > 1e-10f ? Vector3.Normalize(outwardDir) : Vector3.UnitY;

        if (!roundCorners || segments == 1)
        {
            var bulge = 1f - 0.25f * roundness;
            var centerId = AddPoint(vertex + (centroid - vertex) * bulge, centerNormal);

            for (var i = 0; i < loop.Count; i++)
            {
                var next = (i + 1) % loop.Count;
                AddTriangle(loop[i], loop[next], centerId);
            }

            return;
        }

        BuildCornerRings(loop, vertex, centerNormal, segments, roundness);
    }

    /// <summary>
    /// Closes a corner with concentric rings carrying the edge profile through the
    /// corner, instead of a single-point fan. The rings are pulled onto a sphere
    /// fitted through the loop points (blended in by roundness), so a fully round
    /// bevel gets the classic rounded-cube corner.
    /// </summary>
    private void BuildCornerRings(List<int> loop, Vector3 vertex, Vector3 outwardNormal, int segments, float roundness)
    {
        // Fit a sphere with its center on the inward axis through the vertex:
        // C = vertex - outwardNormal * d, least-squares over the loop points.
        var sumA = 0f;
        var sumB = 0f;
        var sumAb = 0f;
        var sumBb = 0f;
        foreach (var id in loop)
        {
            var delta = _outPositions[id] - vertex;
            var a = delta.LengthSquared();
            var b = -Vector3.Dot(outwardNormal, delta);
            sumA += a;
            sumB += b;
            sumAb += a * b;
            sumBb += b * b;
        }

        var n = loop.Count;
        var varB = sumBb - sumB * sumB / n;
        var d = varB > 1e-10f ? (sumAb - sumA * sumB / n) / (2f * varB) : 0f;
        var center = vertex - outwardNormal * d;

        var radius = 0f;
        foreach (var id in loop)
        {
            radius += Vector3.Distance(_outPositions[id], center);
        }

        radius /= n;

        // At extreme widths the loop is far from spherical and the fit can overshoot -
        // keep the sphere inside the original vertex so neither pole nor rings spike out.
        if (d > 0)
            radius = MathF.Min(radius, d);

        var pole = center + outwardNormal * radius;
        var poleId = AddPoint(Vector3.Lerp(vertex, pole, roundness), outwardNormal);

        // Rings between the loop (t=0) and the pole (t=1): planar shrink toward the
        // pole, pushed out to the sphere by roundness.
        var previousRing = new int[n];
        var currentRing = new int[n];
        loop.CopyTo(previousRing);
        var polePosition = _outPositions[poleId];

        for (var ringIndex = 1; ringIndex < segments; ringIndex++)
        {
            var t = (float)ringIndex / segments;
            for (var i = 0; i < n; i++)
            {
                var flat = Vector3.Lerp(_outPositions[loop[i]], polePosition, t);
                var fromCenter = flat - center;
                var length = fromCenter.Length();
                var sphereNormal = length > 1e-10f ? fromCenter / length : outwardNormal;
                var position = Vector3.Lerp(flat, center + sphereNormal * radius, roundness);
                var flatNormal = Vector3.Lerp(_outNormals[loop[i]], outwardNormal, t);
                if (flatNormal.LengthSquared() > 1e-10f)
                    flatNormal = Vector3.Normalize(flatNormal);
                var normal = Vector3.Lerp(flatNormal, sphereNormal, roundness);
                if (normal.LengthSquared() > 1e-10f)
                    normal = Vector3.Normalize(normal);
                currentRing[i] = AddPoint(position, normal);
            }

            for (var i = 0; i < n; i++)
            {
                var next = (i + 1) % n;
                _polygon.Add(previousRing[i]);
                _polygon.Add(previousRing[next]);
                _polygon.Add(currentRing[next]);
                _polygon.Add(currentRing[i]);
                AddPolygon(_polygon);
            }

            (previousRing, currentRing) = (currentRing, previousRing);
        }

        for (var i = 0; i < n; i++)
        {
            var next = (i + 1) % n;
            AddTriangle(previousRing[i], previousRing[next], poleId);
        }
    }

    /// <summary>Per corner: how fast it moves when both of its edges move inward at unit speed (in the face plane).</summary>
    private void ComputeCornerVelocities(Geometry mesh)
    {
        var positions = mesh.Positions;
        var offsets = mesh.PrimitiveVertexOffsets;
        var cornerPoints = mesh.VertexPoints;
        var cornerEdges = mesh.Edges.CornerEdgeIndices;
        if (_cornerVelocities.Length != mesh.VertexCount)
            _cornerVelocities = new Vector3[mesh.VertexCount];

        for (var faceIndex = 0; faceIndex < mesh.PrimitiveCount; faceIndex++)
        {
            var faceNormal = _faceNormals[faceIndex];
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            for (var c = start; c < end; c++)
            {
                var previousCorner = c == start ? end - 1 : c - 1;
                var nextCorner = c + 1 == end ? start : c + 1;
                var previous = positions[cornerPoints[previousCorner]];
                var next = positions[cornerPoints[nextCorner]];
                var p = positions[cornerPoints[c]];
                var inwardPrevious = StraightSkeletonInset.EdgeInwardNormal(previous, p, faceNormal);
                var inwardNext = StraightSkeletonInset.EdgeInwardNormal(p, next, faceNormal);
                var previousBeveled = _edgeBeveled[cornerEdges[previousCorner]];
                var nextBeveled = _edgeBeveled[cornerEdges[c]];

                // A corner with one sharp edge slides along it instead of leaving it, so the sharp side stays put
                _cornerVelocities[c] = previousBeveled
                                           ? nextBeveled
                                                 ? StraightSkeletonInset.MiterVelocity(inwardPrevious, inwardNext)
                                                 : SlideVelocity(next - p, inwardPrevious)
                                           : nextBeveled
                                               ? SlideVelocity(previous - p, inwardNext)
                                               : Vector3.Zero;
            }
        }
    }

    /** Moves along an unselected edge until the distance to the beveled edge, along its inward normal, is one. */
    private static Vector3 SlideVelocity(Vector3 alongEdge, Vector3 inwardNormal)
    {
        var length = alongEdge.Length();
        if (length < 1e-12f)
            return Vector3.Zero;

        var direction = alongEdge / length;
        var denominator = Vector3.Dot(direction, inwardNormal);
        return MathF.Abs(denominator) < 1e-5f ? Vector3.Zero : direction / denominator;
    }

    private static void ComputeFaceNormals(Geometry mesh, ref Vector3[] faceNormals)
    {
        var positions = mesh.Positions;
        var offsets = mesh.PrimitiveVertexOffsets;
        var cornerPoints = mesh.VertexPoints;
        if (faceNormals.Length != mesh.PrimitiveCount)
            faceNormals = new Vector3[mesh.PrimitiveCount];

        for (var faceIndex = 0; faceIndex < mesh.PrimitiveCount; faceIndex++)
        {
            var normal = Vector3.Zero;
            var start = offsets[faceIndex];
            var end = offsets[faceIndex + 1];
            for (var c = start; c < end; c++)
            {
                var next = c + 1 == end ? start : c + 1;
                normal += NewellTerm(positions[cornerPoints[c]], positions[cornerPoints[next]]);
            }

            faceNormals[faceIndex] = normal.LengthSquared() > 1e-10f ? Vector3.Normalize(normal) : Vector3.UnitY;
        }
    }

    private static Vector3 NewellTerm(Vector3 p0, Vector3 p1)
    {
        return new Vector3((p0.Y - p1.Y) * (p0.Z + p1.Z),
                           (p0.Z - p1.Z) * (p0.X + p1.X),
                           (p0.X - p1.X) * (p0.Y + p1.Y));
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var lengthSq = ab.LengthSquared();
        var t = lengthSq > 1e-20f ? Math.Clamp(Vector3.Dot(p - a, ab) / lengthSq, 0f, 1f) : 0f;
        return Vector3.Distance(p, a + ab * t);
    }

    private static int Find(int[] parent, int index)
    {
        while (parent[index] != index)
        {
            parent[index] = parent[parent[index]];
            index = parent[index];
        }

        return index;
    }

    private static void Union(int[] parent, int a, int b)
    {
        var rootA = Find(parent, a);
        var rootB = Find(parent, b);
        if (rootA != rootB)
            parent[Math.Max(rootA, rootB)] = Math.Min(rootA, rootB);
    }

    private static bool TraversesInOrder(Geometry source, int faceIndex, int pointA, int pointB)
    {
        var start = source.PrimitiveVertexOffsets[faceIndex];
        var end = source.PrimitiveVertexOffsets[faceIndex + 1];
        for (var c = start; c < end; c++)
        {
            var next = c + 1 == end ? start : c + 1;
            if (source.VertexPoints[c] == pointA && source.VertexPoints[next] == pointB)
                return true;
        }

        return false;
    }

    private static int FindCornerInFace(Geometry source, int faceIndex, int pointId)
    {
        var start = source.PrimitiveVertexOffsets[faceIndex];
        var end = source.PrimitiveVertexOffsets[faceIndex + 1];
        for (var c = start; c < end; c++)
        {
            if (source.VertexPoints[c] == pointId)
                return c;
        }

        return start;
    }

    /// <summary>Finds the face's second edge touching the vertex (each vertex has exactly two per face).</summary>
    private static bool TryFindOtherEdgeAtVertex(Geometry source, int faceIndex, int pointId, int excludeEdgeIndex, out int edgeIndex)
    {
        var edges = source.Edges;
        var start = source.PrimitiveVertexOffsets[faceIndex];
        var end = source.PrimitiveVertexOffsets[faceIndex + 1];
        for (var c = start; c < end; c++)
        {
            var next = c + 1 == end ? start : c + 1;
            if (source.VertexPoints[c] != pointId && source.VertexPoints[next] != pointId)
                continue;

            var candidate = edges.CornerEdgeIndices[c];
            if (candidate == excludeEdgeIndex)
                continue;

            var edge = edges.Edges[candidate];
            if (edge.PointA != pointId && edge.PointB != pointId)
                continue;

            edgeIndex = candidate;
            return edge.Face1 >= 0;
        }

        edgeIndex = -1;
        return false;
    }

    private int AddPoint(Vector3 position, Vector3 normal)
    {
        _outPositions.Add(position);
        _outNormals.Add(normal);
        return _outPositions.Count - 1;
    }

    /** Emits the collected corners as a face, skipping repeats left by collapsed edges, then clears the list. */
    private void AddPolygon(List<int> corners)
    {
        var start = _outCorners.Count;
        foreach (var id in corners)
        {
            if (_outCorners.Count == start || _outCorners[^1] != id)
                _outCorners.Add(id);
        }

        while (_outCorners.Count - start > 1 && _outCorners[^1] == _outCorners[start])
        {
            _outCorners.RemoveAt(_outCorners.Count - 1);
        }

        corners.Clear();
        if (_outCorners.Count - start < 3)
        {
            _outCorners.RemoveRange(start, _outCorners.Count - start);
            return;
        }

        _outFaceOffsets.Add(_outCorners.Count);
    }

    private void AddTriangle(int a, int b, int c)
    {
        _polygon.Add(a);
        _polygon.Add(b);
        _polygon.Add(c);
        AddPolygon(_polygon);
    }

    public bool TryGetProgress(out float progress) => _asyncComputation.TryGetUiProgress(out progress);

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return string.IsNullOrEmpty(_statusMessage) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;
    }

    public string GetStatusMessage() => _statusMessage;

    /** The straight skeleton insets every edge; an edge left sharp would break its assumptions. */
    private bool AllInteriorEdgesBeveled(EdgeTopology topology)
    {
        for (var edgeIndex = 0; edgeIndex < topology.Edges.Length; edgeIndex++)
        {
            if (topology.Edges[edgeIndex].Face1 >= 0 && !_edgeBeveled[edgeIndex])
                return false;
        }

        return true;
    }

    /** Fraction of an edge, or of the distance to another edge of the face, a corner may move across. */
    private const float MaxShareOfDistance = 0.45f;

    /** Skeleton corners closer than this fraction of the width after the inset are welded. */
    private const float CollapseWeldFactor = 1e-3f;

    private readonly Geometry _output = new();
    private readonly AsyncComputation<Geometry> _asyncComputation = new();
    private readonly List<Vector3> _outPositions = [];
    private readonly List<Vector3> _outNormals = [];
    private readonly List<int> _outFaceOffsets = [];
    private readonly List<int> _outCorners = [];
    private readonly List<int> _polygon = [];
    private readonly StraightSkeletonInset _skeleton = new();
    private readonly List<int> _cornerSlots = [];
    private readonly Dictionary<(int PointId, bool Beveled, int LowInsetId, int HighInsetId), int[]> _ringsByEnds = [];
    private readonly List<(int A, int B)> _bridgePointPairs = [];
    private readonly List<byte> _mergedFaceKinds = [];
    private List<byte> _faceKinds;
    private int[] _slotPointIds = new int[64];
    private int[] _insetPointIds = [];
    private bool[] _edgeBeveled = [];
    private Vector3[] _faceNormals = [];
    private Vector3[] _cornerVelocities = [];
    private float[] _pointWidths = [];
    private string _statusMessage = string.Empty;

    [Input(Guid = "46a2c9e2-7b5d-45ce-9955-b1c0b1bb01db")]
    public readonly InputSlot<Geometry> Geometry = new();

    [Input(Guid = "ce40061f-b853-41be-bbef-0db172bbad7f")]
    public readonly InputSlot<float> Width = new();

    [Input(Guid = "5c23fb6d-c4c7-4256-a2d4-3ddd76aed6e4", MappedType = typeof(Methods))]
    public readonly InputSlot<int> Method = new();

    [Input(Guid = "88b2db34-b099-4fa6-8875-743dc40fd367")]
    public readonly InputSlot<float> FlatAngle = new();

    [Input(Guid = "3ccbe233-5a23-46a1-b295-5ff44a3eb56b", MappedType = typeof(EdgeSelections))]
    public readonly InputSlot<int> EdgeSelection = new();

    [Input(Guid = "e9085eac-dc15-4ecf-862d-869e66981418")]
    public readonly InputSlot<int> Segments = new();

    [Input(Guid = "091c932c-a7bb-487e-97c5-093e9838a700")]
    public readonly InputSlot<float> Roundness = new();

    [Input(Guid = "c86a32c2-feeb-4c05-b2c0-cf54076247bb")]
    public readonly InputSlot<bool> FlatShading = new();

    [Input(Guid = "3f8b1c4d-92e6-4a17-b5d0-6a4c8e2f7d19")]
    public readonly InputSlot<bool> RoundCorners = new();

    [Input(Guid = "8d5c2a97-40e1-4b68-93fd-6b0e8c4a2d75")]
    public readonly InputSlot<bool> Async = new();

    /** The op works on the polygons; the curves are appended to its result unchanged. */
    private readonly GeometryPolygonFilter _polygons = new();
    private readonly GeometryCurvePassThrough _curves = new();
}
