#nullable enable
using LibTessDotNet;
using Poly2Tri;

using System;

namespace Lib.geometry;

/// <summary>
/// Fills closed curves into a mesh and optionally extrudes them: front cap, back cap
/// and side walls with a rounded or chamfered bevel, in one op because the bevel
/// needs the cap boundary and the walls to agree. One mesh part per curve part
/// (glyph), attributes of the curve parts carried over, so per-character selection
/// and coloring keep working after the fill.
/// </summary>
[Guid("8bf9a286-00f8-4428-b046-8c92608a8cb9")]
[ExportDependencies("LibTessDotNet.dll", "Poly2Tri.dll")]
internal sealed class CurvesToGeometryExp : Instance<CurvesToGeometryExp>
{


    [Output(Guid = "80c53c5b-5ab6-4a97-aa6e-3c2f5640bd88")]
    public readonly Slot<MeshGeometry?> Result = new();

    public CurvesToGeometryExp()
    {
        Result.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var curves = Curves.GetValue(context);
        var depth = MathF.Max(Depth.GetValue(context), 0);
        var bevel = MathF.Max(Bevel.GetValue(context), 0);
        var bevelSegments = Math.Clamp(BevelSegments.GetValue(context), 1, 16);
        var sideLoops = Math.Clamp(SideLoops.GetValue(context), 0, 128);
        // Below this, grid/Poisson point counts scale as 1/spacing^2 and can blow up into a hang
        // or an allocation failure; 0 itself stays valid and means "no cap points".
        const float minCapPointSpacing = 0.01f;
        var capPointSpacing = CapPointSpacing.GetValue(context);
        capPointSpacing = capPointSpacing > 0 ? MathF.Max(capPointSpacing, minCapPointSpacing) : 0;
        var capPointDistribution = CapPointDistribution.GetValue(context);
        var tolerance = MathF.Max(Tolerance.GetValue(context), 1e-5f);
        var front = Front.GetValue(context);
        var back = Back.GetValue(context);
        var sides = Sides.GetValue(context);
        var windingRule = EvenOdd.GetValue(context) ? WindingRule.EvenOdd : WindingRule.NonZero;
        var weight = Weight.GetValue(context);
        var flatShading = FlatShading.GetValue(context);
        var smoothAngle = Math.Clamp(SmoothAngle.GetValue(context), 0f, 180f);

        if (curves == null || curves.ContourCount == 0)
        {
            Result.Value = null;
            return;
        }

        // A bevel that doesn't fit into the depth is halved from both ends
        bevel = depth > 0 ? MathF.Min(bevel, depth * 0.5f) : 0;
        _builder.Begin();

        var parts = curves.Parts;
        var partCount = parts.Length > 0 ? parts.Length : 1;
        for (var partIndex = 0; partIndex < partCount; partIndex++)
        {
            var contourStart = parts.Length > 0 ? parts[partIndex].ContourStart : 0;
            var contourEnd = parts.Length > 0 ? contourStart + parts[partIndex].ContourCount : curves.ContourCount;
            var pivot = parts.Length > 0 ? parts[partIndex].Pivot : Vector3.Zero;
            var id = parts.Length > 0 ? parts[partIndex].Id : 0;
            var seed = parts.Length > 0 ? parts[partIndex].SeedIndex : 0;
            _builder.BuildPart(curves, contourStart, contourEnd, tolerance, depth, bevel, bevelSegments, weight, flatShading, smoothAngle, front, back, sides, windingRule,
                               pivot, id, seed, sideLoops, capPointSpacing, capPointDistribution);
        }

        _builder.Finish(_output, curves);
        Result.Value = _output;
    }

    private readonly MeshBuilder _builder = new();
    private readonly MeshGeometry _output = new();

    /// <summary>
    /// Accumulates faces over all parts. Per part: contours are flattened and oriented
    /// so the solid lies on their left (outers counter-clockwise, holes clockwise, by
    /// nesting parity), inset by the bevel for the caps, tessellated with the fill
    /// rule, and connected by profile rings for bevel and wall faces.
    /// </summary>
    private sealed class MeshBuilder
    {
        public void Begin()
        {
            _positions.Clear();
            _pointLookup.Clear();
            _corners.Clear();
            _normals.Clear();
            _faceOffsets.Clear();
            _faceOffsets.Add(0);
            _isSide.Clear();
            _parts.Clear();
        }

        public void Finish(MeshGeometry target, CurveGeometry source)
        {
            target.Positions = _positions.ToArray();
            target.CornerPointIndices = _corners.ToArray();
            target.FaceCornerOffsets = _faceOffsets.ToArray();
            target.Parts = _parts.ToArray();
            target.Attributes.Clear();

            var normals = target.Attributes.GetOrCreate<Vector3>(GeometryAttributeNames.Normal, AttributeDomain.Corner, _normals.Count);
            _normals.CopyTo(normals.Values);
            var isSide = target.Attributes.GetOrCreate<float>(GeometryAttributeNames.IsSide, AttributeDomain.Face, _isSide.Count);
            _isSide.CopyTo(isSide.Values);

            // Part attributes of the curves (glyph indices etc.) apply 1:1 to the mesh parts
            foreach (var attribute in source.Attributes)
            {
                if (attribute.Domain != AttributeDomain.Part || attribute.Count != _parts.Count)
                    continue;

                switch (attribute)
                {
                    case GeometryAttribute<float> a:
                        a.Values.CopyTo(target.Attributes.GetOrCreate<float>(a.Name, AttributeDomain.Part, a.Count).Values, 0);
                        break;
                    case GeometryAttribute<int> a:
                        a.Values.CopyTo(target.Attributes.GetOrCreate<int>(a.Name, AttributeDomain.Part, a.Count).Values, 0);
                        break;
                    case GeometryAttribute<Vector4> a:
                        a.Values.CopyTo(target.Attributes.GetOrCreate<Vector4>(a.Name, AttributeDomain.Part, a.Count).Values, 0);
                        break;
                }
            }

            target.InvalidateTopologyCaches();
        }

        public void BuildPart(CurveGeometry curves, int contourStart, int contourEnd, float tolerance, float depth, float bevel, int bevelSegments,
                              float weight, bool flatShading, float smoothAngle, bool front, bool back, bool sides, WindingRule windingRule,
                              Vector3 pivot, int id, int seed, int sideLoops, float capPointSpacing, int capPointDistribution)
        {
            var faceStart = _faceOffsets.Count - 1;

            // Flatten the part's contours into 2D loops
            _loops.Clear();
            for (var contourIndex = contourStart; contourIndex < contourEnd; contourIndex++)
            {
                if (!curves.ContourClosed[contourIndex])
                    continue;

                var loop = new List<Vector2>();
                _scratch.Clear();
                curves.Flatten(contourIndex, tolerance, _scratch);

                foreach (var p in _scratch)
                {
                    if (loop.Count == 0 || Vector2.DistanceSquared(loop[^1], new Vector2(p.X, p.Y)) > 1e-12f)
                        loop.Add(new Vector2(p.X, p.Y));
                }

                if (loop.Count > 2 && Vector2.DistanceSquared(loop[0], loop[^1]) < 1e-12f)
                    loop.RemoveAt(loop.Count - 1);

                if (loop.Count >= 3)
                    _loops.Add(loop);
            }

            if (_loops.Count == 0)
                return;

            // Overlapping contours (bold variable instances, stacked strokes) must become one
            // outline before walls are built, otherwise walls run through the solid. The
            // tessellator resolves them with the fill rule and hands back the boundary loops.
            ResolveOverlaps(windingRule);
            if (_loops.Count == 0)
                return;

            OrientLoops();

            // Faux bold: offset the resolved outline outwards (or inwards for a lighter look).
            // The offset folds at concave corners and can invert counters; a second pass through
            // the fill rule drops those inverted loops, the same trick polygon offsetters use.
            if (MathF.Abs(weight) > 1e-6f)
            {
                OffsetLoops(-weight);
                ResolveOverlaps(WindingRule.NonZero);
                if (_loops.Count == 0)
                    return;

                OrientLoops();
            }

            var hasWalls = sides && depth > 0;
            var profile = BuildProfile(depth, bevel, bevelSegments, sideLoops, hasWalls);
            BuildRings(profile);

            // Caps and walls share the ring points, so the solid closes without a weld
            if (front)
                AddCap(ringIndex: 0, windingRule, flip: false, capPointSpacing, capPointDistribution, seed);

            if (back && depth > 0)
                AddCap(ringIndex: profile.Count - 1, windingRule, flip: true, capPointSpacing, capPointDistribution, seed);

            if (hasWalls)
                AddWalls(profile, flatShading, MathF.Cos(smoothAngle * MathF.PI / 180f));

            var faceCount = _faceOffsets.Count - 1 - faceStart;
            if (faceCount > 0)
                _parts.Add(new GeometryPart(faceStart, faceCount, pivot, id, seed));
        }

        /// <summary>Profile of the extrusion as (inset, z) samples from the front cap to the back cap.</summary>
        private List<(float Inset, float Z)> BuildProfile(float depth, float bevel, int segments, int sideLoops, bool hasWalls)
        {
            _profile.Clear();
            if (!hasWalls)
            {
                _profile.Add((0, 0));
                _profile.Add((0, -depth));
                return _profile;
            }

            if (bevel <= 0)
            {
                var loopCount = sideLoops + 1;
                for (var i = 0; i <= loopCount; i++)
                {
                    var t = i / (float)loopCount;
                    _profile.Add((0, -depth * t));
                }
                return _profile;
            }

            // Quarter circles at both ends: inset shrinks from bevel to 0 while z goes from 0 to -bevel
            for (var i = 0; i <= segments; i++)
            {
                var angle = i / (float)segments * MathF.PI * 0.5f;
                _profile.Add((bevel * (1 - MathF.Sin(angle)), -bevel * (1 - MathF.Cos(angle))));
            }

            // Extra loops are inserted only along the straight section between the two bevels.
            // If the bevels meet, there is no straight section to subdivide.
            var straightStart = -bevel;
            var straightEnd = -depth + bevel;
            var straightLength = MathF.Abs(straightEnd - straightStart);
            if (straightLength > 1e-6f)
            {
                for (var i = 1; i <= sideLoops; i++)
                {
                    var t = i / (float)(sideLoops + 1);
                    _profile.Add((0, straightStart + (straightEnd - straightStart) * t));
                }
            }

            for (var i = segments; i >= 0; i--)
            {
                var angle = i / (float)segments * MathF.PI * 0.5f;
                _profile.Add((bevel * (1 - MathF.Sin(angle)), -depth + bevel * (1 - MathF.Cos(angle))));
            }

            return _profile;
        }

        private void OffsetLoops(float inset)
        {
            foreach (var loop in _loops)
            {
                _scratch2.Clear();
                for (var i = 0; i < loop.Count; i++)
                    _scratch2.Add(InsetPoint(loop, i, inset));

                loop.Clear();
                loop.AddRange(_scratch2);
            }
        }

        private void ResolveOverlaps(WindingRule windingRule)
        {
            var tess = new Tess();
            foreach (var loop in _loops)
            {
                var contour = new ContourVertex[loop.Count];
                for (var i = 0; i < loop.Count; i++)
                {
                    contour[i] = new ContourVertex(new Vec3(loop[i].X, loop[i].Y, 0));
                }

                tess.AddContour(contour, ContourOrientation.Original);
            }

            tess.Tessellate(windingRule, ElementType.BoundaryContours, 0, null, new Vec3(0, 0, 1));
            _loops.Clear();
            var vertices = tess.Vertices;
            var elements = tess.Elements;
            for (var e = 0; e < tess.ElementCount; e++)
            {
                var start = elements[e * 2];
                var count = elements[e * 2 + 1];
                if (count < 3)
                    continue;

                var loop = new List<Vector2>(count);
                for (var i = 0; i < count; i++)
                {
                    var v = vertices[start + i].Position;
                    var point = new Vector2(v.X, v.Y);
                    if (loop.Count == 0 || Vector2.DistanceSquared(loop[^1], point) > 1e-12f)
                        loop.Add(point);
                }

                if (loop.Count > 2 && Vector2.DistanceSquared(loop[0], loop[^1]) < 1e-12f)
                    loop.RemoveAt(loop.Count - 1);

                if (loop.Count >= 3)
                    _loops.Add(loop);
            }
        }

        /// <summary>Orients each loop so the solid is on its left: nesting depth even = outer (CCW), odd = hole (CW).</summary>
        private void OrientLoops()
        {
            for (var i = 0; i < _loops.Count; i++)
            {
                var loop = _loops[i];
                var probe = loop[0];
                var depth = 0;
                for (var j = 0; j < _loops.Count; j++)
                {
                    if (j != i && Contains(_loops[j], probe))
                        depth++;
                }

                var ccw = SignedArea(loop) > 0;
                var wantCcw = depth % 2 == 0;
                if (ccw != wantCcw)
                    loop.Reverse();
            }
        }

        private static float SignedArea(List<Vector2> loop)
        {
            var area = 0f;
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                area += a.X * b.Y - b.X * a.Y;
            }

            return area * 0.5f;
        }

        private static bool Contains(List<Vector2> loop, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = loop.Count - 1; i < loop.Count; j = i++)
            {
                var a = loop[i];
                var b = loop[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) &&
                    p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>Loop vertex moved inwards (to the left of the travel direction) by the inset, mitered with a clamp.</summary>
        private static Vector2 InsetPoint(List<Vector2> loop, int index, float inset)
        {
            if (inset == 0)
                return loop[index];

            var count = loop.Count;
            var previous = loop[(index - 1 + count) % count];
            var current = loop[index];
            var next = loop[(index + 1) % count];
            var d0 = Vector2.Normalize(current - previous);
            var d1 = Vector2.Normalize(next - current);
            var n0 = new Vector2(-d0.Y, d0.X); // left normals point into the solid
            var n1 = new Vector2(-d1.Y, d1.X);
            var bisector = n0 + n1;
            var lengthSq = bisector.LengthSquared();
            if (lengthSq < 1e-8f)
                return current + n0 * inset;

            // Miter length grows at sharp corners; clamp to twice the inset to avoid spikes
            var miter = bisector / lengthSq * 2f;
            if (miter.LengthSquared() > 4f)
                miter = Vector2.Normalize(miter) * 2f;

            return current + miter * inset;
        }

        /// <summary>One point per loop vertex per profile ring; loop l, ring r, vertex i at _loopRings[l][r * count + i].</summary>
        private void BuildRings(List<(float Inset, float Z)> profile)
        {
            _loopRings.Clear();

            foreach (var loop in _loops)
            {
                var count = loop.Count;
                var ids = new int[profile.Count * count];

                for (var r = 0; r < profile.Count; r++)
                {
                    var (inset, z) = profile[r];
                    for (var i = 0; i < count; i++)
                    {
                        var p = InsetPoint(loop, i, inset);
                        var pointId = AddPoint(new Vector3(p.X, p.Y, z));
                        ids[r * count + i] = pointId;
                    }
                }

                _loopRings.Add(ids);
            }
        }

        private void AddCap(int ringIndex, WindingRule windingRule, bool flip, float capPointSpacing, int capPointDistribution, int seed)
        {
            float capZ = _profile[ringIndex].Z;
            Vector3 capNormal = flip ? -Vector3.UnitZ : Vector3.UnitZ;

            // Classify loops into outers/holes by nesting parity and collect outer indices,
            // sorted by area so a hole is matched to its smallest enclosing outer first.
            var loopCount = _loops.Count;
            _capDepths.Clear();
            for (var i = 0; i < loopCount; i++)
                _capDepths.Add(GetDepth(_loops[i], _loops));

            _capOuterIndices.Clear();
            for (var i = 0; i < loopCount; i++)
            {
                if (_capDepths[i] % 2 == 0)
                    _capOuterIndices.Add(i);
            }

            // Simple insertion sort by area - loop counts per cap are small (glyph/shape holes)
            for (var i = 1; i < _capOuterIndices.Count; i++)
            {
                var idx = _capOuterIndices[i];
                var area = MathF.Abs(SignedArea(_loops[idx]));
                var j = i - 1;
                while (j >= 0 && MathF.Abs(SignedArea(_loops[_capOuterIndices[j]])) > area)
                {
                    _capOuterIndices[j + 1] = _capOuterIndices[j];
                    j--;
                }
                _capOuterIndices[j + 1] = idx;
            }

            _capAssignedHoles.Clear();

            // Process each outer polygon with its direct holes
            foreach (var outerIdx in _capOuterIndices)
            {
                var outerLoop = _loops[outerIdx];

                _capHoleIndices.Clear();
                for (var i = 0; i < loopCount; i++)
                {
                    if (_capDepths[i] % 2 == 0 || _capAssignedHoles.Contains(i))
                        continue;

                    if (Contains(outerLoop, _loops[i][0]))
                        _capHoleIndices.Add(i);
                }

                foreach (var holeIdx in _capHoleIndices)
                    _capAssignedHoles.Add(holeIdx);

                // Build the polygon and map boundary points to IDs
                _capBoundaryPointToId.Clear();
                _capOuterPoints.Clear();
                var outerRing = _loopRings[outerIdx];
                for (var i = 0; i < outerLoop.Count; i++)
                {
                    int pointId = outerRing[ringIndex * outerLoop.Count + i];
                    var pp = new PolygonPoint(outerLoop[i].X, outerLoop[i].Y);
                    _capBoundaryPointToId[pp] = pointId;
                    _capOuterPoints.Add(pp);
                }

                var polygon = new Polygon(_capOuterPoints);

                _capHoleLoops.Clear();
                foreach (var holeIdx in _capHoleIndices)
                {
                    var holeLoop = _loops[holeIdx];
                    var holeRing = _loopRings[holeIdx];
                    _capHolePoints.Clear();
                    for (var i = 0; i < holeLoop.Count; i++)
                    {
                        int pointId = holeRing[ringIndex * holeLoop.Count + i];
                        var pp = new PolygonPoint(holeLoop[i].X, holeLoop[i].Y);
                        _capBoundaryPointToId[pp] = pointId;
                        _capHolePoints.Add(pp);
                    }
                    polygon.AddHole(new Polygon(_capHolePoints));
                    _capHoleLoops.Add(holeLoop);
                }

                // Add distributed Steiner points to make the cap denser and more suitable
                // for later deformation. Poly2Tri keeps these interior points and uses them
                // when building the constrained Delaunay triangulation.
                if (capPointSpacing > 0)
                {
                    var interiorSeed = seed ^ outerIdx;
                    _capGeneratedPoints.Clear();
                    if (capPointDistribution == (int)CapPointDistributionMode.Grid)
                        GenerateGridPoints(outerLoop, _capHoleLoops, capPointSpacing, interiorSeed, _capGeneratedPoints);
                    else
                        GeneratePoissonPoints(outerLoop, _capHoleLoops, capPointSpacing, interiorSeed, _capGeneratedPoints);

                    foreach (var p in _capGeneratedPoints)
                        polygon.AddSteinerPoint(new PolygonPoint(p.X, p.Y));
                }

                // Triangulate
                P2T.Triangulate(polygon);

                // Helper to get point ID
                int GetPointId(Point2D pt)
                {
                    if (pt is PolygonPoint pp && _capBoundaryPointToId.TryGetValue(pp, out int id))
                        return id;

                    // Steiner point – add it to the mesh
                    var pos = new Vector3((float)pt.X, (float)pt.Y, capZ);
                    return AddPoint(pos);
                }

                // Add triangles
                foreach (var tri in polygon.Triangles)
                {
                    var p0 = GetPointId(tri.Points[0]);
                    var p1 = GetPointId(tri.Points[1]);
                    var p2 = GetPointId(tri.Points[2]);

                    var v0 = _positions[p0];
                    var v1 = _positions[p1];
                    var v2 = _positions[p2];
                    var normal = Vector3.Cross(v1 - v0, v2 - v0);
                    if (Vector3.Dot(normal, capNormal) < 0)
                        (p1, p2) = (p2, p1);

                    AddFace(p0, p1, p2, capNormal, isSide: false);
                }
            }
        }

        /// <summary>
        /// Rebuilds the reusable background grid (cell size spacing/sqrt2, one point slot per cell,
        /// same scheme Bridson relies on) and seeds it with the outer and hole boundary vertices.
        /// Used by both point generators so "stay away from the boundary" becomes the same O(1)
        /// amortized neighbor lookup as "stay away from other samples", instead of a per-candidate
        /// distance-to-polyline scan against every boundary segment. The trade-off: boundary
        /// proximity is only accurate to within about one cell (~spacing), which is fine for this
        /// margin check but wouldn't be for anything needing an exact distance.
        /// </summary>
        private void PrepareBoundaryGrid(List<Vector2> outer, List<List<Vector2>> holes, float spacing, out Vector2 min, out float cellSize, out int width, out int height)
        {
            // Local copies: out params can't be captured by the SeedLoop local function below.
            var gridMin = outer[0];
            var max = outer[0];
            for (var i = 1; i < outer.Count; i++)
            {
                gridMin = Vector2.Min(gridMin, outer[i]);
                max = Vector2.Max(max, outer[i]);
            }

            var gridCellSize = spacing / MathF.Sqrt(2f);
            var gridWidth = Math.Max(1, (int)MathF.Ceiling((max.X - gridMin.X) / gridCellSize) + 1);
            var gridHeight = Math.Max(1, (int)MathF.Ceiling((max.Y - gridMin.Y) / gridCellSize) + 1);

            var cellCount = gridWidth * gridHeight;
            if (_poissonGridCells.Length < cellCount)
                _poissonGridCells = new int[cellCount];
            Array.Fill(_poissonGridCells, -1, 0, cellCount);

            _poissonGridPoints.Clear();

            SeedLoop(outer);
            foreach (var hole in holes)
                SeedLoop(hole);

            min = gridMin;
            cellSize = gridCellSize;
            width = gridWidth;
            height = gridHeight;

            void SeedLoop(List<Vector2> loop)
            {
                for (var i = 0; i < loop.Count; i++)
                {
                    var p = loop[i];
                    var gx = Math.Clamp((int)MathF.Floor((p.X - gridMin.X) / gridCellSize), 0, gridWidth - 1);
                    var gy = Math.Clamp((int)MathF.Floor((p.Y - gridMin.Y) / gridCellSize), 0, gridHeight - 1);
                    var cell = gy * gridWidth + gx;
                    if (_poissonGridCells[cell] >= 0)
                        continue; // a boundary vertex already represents this cell - good enough for a margin check

                    _poissonGridCells[cell] = _poissonGridPoints.Count;
                    _poissonGridPoints.Add(p);
                }
            }
        }

        /// <summary>True if any point already in the background grid lies within sqrt(minDistSq) of p.</summary>
        private bool HasNearbyPoint(Vector2 p, float minDistSq, Vector2 min, float cellSize, int width, int height)
        {
            const int range = 2;
            var gx = (int)MathF.Floor((p.X - min.X) / cellSize);
            var gy = (int)MathF.Floor((p.Y - min.Y) / cellSize);

            for (var y = Math.Max(0, gy - range); y <= Math.Min(height - 1, gy + range); y++)
            {
                for (var x = Math.Max(0, gx - range); x <= Math.Min(width - 1, gx + range); x++)
                {
                    var index = _poissonGridCells[y * width + x];
                    if (index < 0)
                        continue;

                    if (Vector2.DistanceSquared(_poissonGridPoints[index], p) < minDistSq)
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Generates Bridson Poisson-disc samples inside an outer loop and outside its holes.
        /// The polygon boundary remains constrained by Poly2Tri; samples are interior Steiner points.
        /// </summary>
        private void GeneratePoissonPoints(List<Vector2> outer, List<List<Vector2>> holes, float spacing, int seed, List<Vector2> output)
        {
            if (spacing <= 0 || outer.Count < 3)
                return;

            PrepareBoundaryGrid(outer, holes, spacing, out var min, out var cellSize, out var width, out var height);

            // Boundary vertices seed the disc growth too, so interior points fill in evenly
            // right up to the constrained edge instead of only starting from one random point.
            _poissonActive.Clear();
            for (var i = 0; i < _poissonGridPoints.Count; i++)
                _poissonActive.Add(_poissonGridPoints[i]);

            var radiusSq = spacing * spacing;
            var rng = new SeededRandom(seed);

            bool IsValid(Vector2 p)
            {
                if (!Contains(outer, p))
                    return false;

                foreach (var hole in holes)
                {
                    if (Contains(hole, p))
                        return false;
                }

                return !HasNearbyPoint(p, radiusSq, min, cellSize, width, height);
            }

            void AddSample(Vector2 p)
            {
                var index = _poissonGridPoints.Count;
                _poissonGridPoints.Add(p);
                _poissonActive.Add(p);
                output.Add(p);

                var gx = Math.Clamp((int)MathF.Floor((p.X - min.X) / cellSize), 0, width - 1);
                var gy = Math.Clamp((int)MathF.Floor((p.Y - min.Y) / cellSize), 0, height - 1);
                var cell = gy * width + gx;
                if (_poissonGridCells[cell] < 0)
                    _poissonGridCells[cell] = index;
            }

            // Bridson: each active point gets several candidates in the annulus [spacing, 2 * spacing].
            const int CandidatesPerPoint = 24;
            while (_poissonActive.Count > 0)
            {
                var activeIndex = rng.NextInt(_poissonActive.Count);
                var center = _poissonActive[activeIndex];
                var accepted = false;

                for (var attempt = 0; attempt < CandidatesPerPoint; attempt++)
                {
                    var angle = rng.NextFloat() * MathF.PI * 2f;
                    var radius = spacing * (1f + rng.NextFloat());
                    var candidate = center + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * radius;

                    if (!IsValid(candidate))
                        continue;

                    AddSample(candidate);
                    accepted = true;
                    break;
                }

                if (!accepted)
                    _poissonActive.RemoveAt(activeIndex);
            }
        }

        /// <summary>
        /// Places points on a regular lattice inside the outer loop and outside its holes, spaced
        /// one `spacing` apart. Cheaper and more uniform than Poisson-disc, at the cost of a visibly
        /// grid-like point layout - useful when regularity matters more than organic distribution.
        /// A small jitter is applied to each point: font stems are very often perfectly vertical or
        /// horizontal, and a perfectly regular lattice tends to land points exactly on those edges,
        /// or produce long rows sharing an identical Y - both of which this triangulator's sweep-line
        /// algorithm chokes on ("doesn't contain edge p1-p2"). The jitter is cheap here because, unlike
        /// the Poisson generator, this method makes one pass with no candidate-rejection loop, so an
        /// exact edge-distance check (rather than the coarser vertex-grid approximation) is affordable.
        /// </summary>
        private void GenerateGridPoints(List<Vector2> outer, List<List<Vector2>> holes, float spacing, int seed, List<Vector2> output)
        {
            if (spacing <= 0 || outer.Count < 3)
                return;

            var min = outer[0];
            var max = outer[0];
            for (var i = 1; i < outer.Count; i++)
            {
                min = Vector2.Min(min, outer[i]);
                max = Vector2.Max(max, outer[i]);
            }

            var marginSq = spacing * spacing * 0.25f; // keep lattice points off the boundary without over-thinning it
            var jitter = spacing * 0.00f; // TODO: Allow the user to tweak the jitter
            var rng = new SeededRandom(seed);

            var columns = Math.Max(1, (int)MathF.Floor((max.X - min.X) / spacing));
            var rows = Math.Max(1, (int)MathF.Floor((max.Y - min.Y) / spacing));

            for (var row = 0; row <= rows; row++)
            {
                var y = min.Y + row * spacing;
                for (var col = 0; col <= columns; col++)
                {
                    var p = new Vector2(min.X + col * spacing, y);
                    p.X += (rng.NextFloat() - 0.5f) * jitter;
                    p.Y += (rng.NextFloat() - 0.5f) * jitter;

                    if (!Contains(outer, p) || NearAnyEdge(outer, p, marginSq))
                        continue;

                    var rejected = false;
                    foreach (var hole in holes)
                    {
                        if (Contains(hole, p) || NearAnyEdge(hole, p, marginSq))
                        {
                            rejected = true;
                            break;
                        }
                    }

                    if (rejected)
                        continue;

                    output.Add(p);
                }
            }
        }

        /// <summary>True if p lies within sqrt(minDistSq) of any edge of loop (not just its vertices).</summary>
        private static bool NearAnyEdge(List<Vector2> loop, Vector2 p, float minDistSq)
        {
            for (var i = 0; i < loop.Count; i++)
            {
                var a = loop[i];
                var b = loop[(i + 1) % loop.Count];
                var ab = b - a;
                var lengthSq = ab.LengthSquared();
                var t = lengthSq > 1e-12f ? Math.Clamp(Vector2.Dot(p - a, ab) / lengthSq, 0f, 1f) : 0f;
                var closest = a + ab * t;
                if (Vector2.DistanceSquared(p, closest) < minDistSq)
                    return true;
            }

            return false;
        }

        /// <summary>Small allocation-free xorshift64* RNG - avoids a heap-allocated System.Random per cap.</summary>
        private struct SeededRandom
        {
            private ulong _state;

            public SeededRandom(int seed)
            {
                _state = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL + 1UL;
            }

            private ulong Next()
            {
                _state ^= _state >> 12;
                _state ^= _state << 25;
                _state ^= _state >> 27;
                return _state * 0x2545F4914F6CDD1DUL;
            }

            public float NextFloat() => (Next() >> 40) * (1f / (1 << 24));

            public int NextInt(int maxExclusive) => (int)(Next() % (ulong)maxExclusive);
        }

        private static int GetDepth(List<Vector2> loop, List<List<Vector2>> allLoops)
        {
            var probe = loop[0];
            int depth = 0;
            for (int j = 0; j < allLoops.Count; j++)
            {
                if (allLoops[j] != loop && Contains(allLoops[j], probe))
                    depth++;
            }
            return depth;
        }


        private void AddWalls(List<(float Inset, float Z)> profile, bool flatShading, float smoothCos)
        {
            for (var l = 0; l < _loops.Count; l++)
            {
                var loop = _loops[l];
                var ids = _loopRings[l];
                var count = loop.Count;
                var ringCount = profile.Count;
                for (var r = 0; r < ringCount - 1; r++)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var next = (i + 1) % count;
                        var a = ids[r * count + i];
                        var b = ids[r * count + next];
                        var c = ids[(r + 1) * count + next];
                        var d = ids[(r + 1) * count + i];

                        // The solid is on the left of the travel direction and z decreases along the
                        // profile, so the outward-facing winding is a -> d -> c -> b
                        var pa = _positions[a];
                        var pb = _positions[b];
                        var pd = _positions[d];
                        var normal = Vector3.Cross(pd - pa, pb - pa);
                        if (normal.LengthSquared() < 1e-14f)
                            continue;

                        normal = Vector3.Normalize(normal);
                        // Smooth shading follows the outline (hard at sharp corners) and the bevel profile
                        if (flatShading)
                        {
                            AddFace(a, d, c, b, normal, isSide: true);
                        }
                        else
                        {
                            AddQuadSmooth(a, d, c, b, loop, i, next, profile, r, ringCount, smoothCos);
                        }
                    }
                }
            }
        }

        /// <summary>Quad a (ring r, i) -> d (ring r+1, i) -> c (ring r+1, next) -> b (ring r, next) with smooth normals from the profile slope.</summary>
        private void AddQuadSmooth(int a, int d, int c, int b, List<Vector2> loop, int i, int next, List<(float Inset, float Z)> profile, int r,
                                   int ringCount, float smoothCos)
        {
            // Outline normals at both ends of segment i; a corner sharper than the smooth angle keeps the segment's own normal
            var startOutward = CornerOutward(loop, i, segment: i, smoothCos);
            var endOutward = CornerOutward(loop, next, segment: i, smoothCos);
            _corners.Add(a); _normals.Add(ProfileNormal(startOutward, profile, r, ringCount));
            _corners.Add(d); _normals.Add(ProfileNormal(startOutward, profile, r + 1, ringCount));
            _corners.Add(c); _normals.Add(ProfileNormal(endOutward, profile, r + 1, ringCount));
            _corners.Add(b); _normals.Add(ProfileNormal(endOutward, profile, r, ringCount));
            _faceOffsets.Add(_corners.Count);
            _isSide.Add(1);
        }

        /// <summary>Outward 2D normal of the outline at vertex index as seen from the given segment (index to index+1, or the one
        /// before). Averaged with the neighbour segment when the corner is flatter than the smooth angle, otherwise the segment's
        /// own normal so sharp corners shade hard.</summary>
        private static Vector2 CornerOutward(List<Vector2> loop, int index, int segment, float smoothCos)
        {
            var count = loop.Count;
            var previous = loop[(index - 1 + count) % count];
            var current = loop[index];
            var next = loop[(index + 1) % count];
            var d0 = Vector2.Normalize(current - previous);
            var d1 = Vector2.Normalize(next - current);
            var own = segment == index ? d1 : d0;
            var ownOutward = new Vector2(own.Y, -own.X);
            if (Vector2.Dot(d0, d1) < smoothCos)
                return ownOutward;

            var outward = Vector2.Normalize(new Vector2(d0.Y, -d0.X) + new Vector2(d1.Y, -d1.X));
            return float.IsNaN(outward.X) ? ownOutward : outward;
        }

        private static Vector3 ProfileNormal(Vector2 outward, List<(float Inset, float Z)> profile, int r, int ringCount)
        {
            // Slope of the profile around ring r: average of the neighbouring profile segments
            var before = Math.Max(r - 1, 0);
            var after = Math.Min(r + 1, ringCount - 1);
            var dInset = profile[after].Inset - profile[before].Inset;
            var dZ = profile[after].Z - profile[before].Z;
            // Tangent along the profile is (-dInset, dZ) in (outward, z); the normal is perpendicular, pointing outward/forward
            var tangent = Vector2.Normalize(new Vector2(-dInset, dZ));
            var normal2 = new Vector2(tangent.Y, -tangent.X);
            if (normal2.X < 0)
                normal2 = -normal2;

            return Vector3.Normalize(new Vector3(outward.X * normal2.X, outward.Y * normal2.X, normal2.Y));
        }

        private int AddPoint(Vector3 position)
        {
            // Use a global lookup with rounded integer keys for robust deduplication
            var key = (
                (long)MathF.Round(position.X * CoordinateScale),
                (long)MathF.Round(position.Y * CoordinateScale),
                (long)MathF.Round(position.Z * CoordinateScale)
            );

            if (_pointLookup.TryGetValue(key, out int index))
                return index;

            index = _positions.Count;
            _positions.Add(position);
            _pointLookup[key] = index;
            return index;
        }

        private void AddFace(int a, int b, int c, Vector3 normal, bool isSide)
        {
            _corners.Add(a); _normals.Add(normal);
            _corners.Add(b); _normals.Add(normal);
            _corners.Add(c); _normals.Add(normal);
            _faceOffsets.Add(_corners.Count);
            _isSide.Add(isSide ? 1 : 0);
        }

        private void AddFace(int a, int b, int c, int d, Vector3 normal, bool isSide)
        {
            _corners.Add(a); _normals.Add(normal);
            _corners.Add(b); _normals.Add(normal);
            _corners.Add(c); _normals.Add(normal);
            _corners.Add(d); _normals.Add(normal);
            _faceOffsets.Add(_corners.Count);
            _isSide.Add(isSide ? 1 : 0);
        }

        private const float CoordinateScale = 1e6f;

        // Reusable scratch buffers for AddCap, so it doesn't allocate a dictionary/hashset/lists on every call.
        private readonly List<int> _capDepths = [];
        private readonly List<int> _capOuterIndices = [];
        private readonly List<int> _capHoleIndices = [];
        private readonly HashSet<int> _capAssignedHoles = [];
        private readonly Dictionary<PolygonPoint, int> _capBoundaryPointToId = new();
        private readonly List<PolygonPoint> _capOuterPoints = [];
        private readonly List<PolygonPoint> _capHolePoints = [];
        private readonly List<List<Vector2>> _capHoleLoops = [];
        private readonly List<Vector2> _capGeneratedPoints = [];

        // Reusable background grid for Poisson-disc / grid cap-point generation (see PrepareBoundaryGrid).
        private int[] _poissonGridCells = [];
        private readonly List<Vector2> _poissonGridPoints = [];
        private readonly List<Vector2> _poissonActive = [];

        private readonly List<List<Vector2>> _loops = [];
        private readonly List<Vector3> _scratch = [];
        private readonly List<Vector2> _scratch2 = [];
        private readonly List<(float Inset, float Z)> _profile = [];
        private readonly List<int[]> _loopRings = [];
        private readonly Dictionary<(long X, long Y, long Z), int> _pointLookup = new();
        private readonly List<Vector3> _positions = [];
        private readonly List<int> _corners = [];
        private readonly List<Vector3> _normals = [];
        private readonly List<int> _faceOffsets = [];
        private readonly List<float> _isSide = [];
        private readonly List<GeometryPart> _parts = [];
    }

    [Input(Guid = "ef97def6-b575-4328-94f4-efed5d3fba8d")]
    public readonly InputSlot<CurveGeometry> Curves = new();

    [Input(Guid = "ac693c5a-8f70-452e-aaad-658f164fd435")]
    public readonly InputSlot<float> Depth = new();

    [Input(Guid = "9f288f63-246f-4fd1-aafa-df53f4928f09")]
    public readonly InputSlot<float> Bevel = new();

    [Input(Guid = "f8b5a3d9-d50c-4edb-92c4-dd5a75cb1188")]
    public readonly InputSlot<int> BevelSegments = new();

    [Input(Guid = "558ea40a-6f10-4492-8397-0797330828d7")]
    public readonly InputSlot<float> Weight = new();

    [Input(Guid = "92deeaa6-8a68-4825-b5a5-433f6f7b5860")]
    public readonly InputSlot<bool> FlatShading = new();

    [Input(Guid = "a44581dc-7662-4cf7-8ef9-6b701a76afb7")]
    public readonly InputSlot<float> SmoothAngle = new();

    [Input(Guid = "a7577b2c-e136-43d2-a97c-7b5e3936e093")]
    public readonly InputSlot<float> Tolerance = new();

    [Input(Guid = "899737c5-4808-4a51-a324-57eae064a992")]
    public readonly InputSlot<bool> Front = new();

    [Input(Guid = "49a4f679-5972-4908-88e8-ad6df75cd136")]
    public readonly InputSlot<bool> Back = new();

    [Input(Guid = "6e6a20b5-3186-47e2-9988-5cdebf94144c")]
    public readonly InputSlot<bool> Sides = new();

    [Input(Guid = "3d7b7c7a-8e11-4b9a-8b7e-3e8a4a6f2b91")]
    public readonly InputSlot<int> SideLoops = new();

    [Input(Guid = "5e4a8c13-2a6f-4a9c-9b17-6d5e2f0a4c31")]
    public readonly InputSlot<float> CapPointSpacing = new();

    [Input(Guid = "ef46d435-efac-4ea7-981a-638ee0bdd784", MappedType = typeof(CapPointDistributionMode))]
    public readonly InputSlot<int> CapPointDistribution = new();

    [Input(Guid = "ada0ec23-23a8-41f3-bd13-cc072504bb76")]
    public readonly InputSlot<bool> EvenOdd = new();

    private enum CapPointDistributionMode
    {
        PoissonDisc,
        Grid,
    }
}