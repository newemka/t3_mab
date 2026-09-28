#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.Logging;

namespace Lib.geometry;

/// <summary>
/// Turns a <see cref="MeshGeometry"/> face range into the convex polygon list a mesh operation works
/// on, welding corners through a shared <see cref="PointWeld"/> and reading corner attributes through
/// an <see cref="AttributeSchema"/>.
/// </summary>
/// <remarks>
/// <para>Convexity is the contract every consumer of these polygons relies on, because a splitter is
/// only exact for convex pieces. A face that is planar and convex therefore survives as one N-gon, and
/// anything else is triangulated here - ear clipping for a concave planar face, a fan for a non-planar
/// one - so no consumer ever sees a shape it cannot divide exactly.</para>
///
/// <para>Positions are shifted by <c>origin</c> on the way in. Float32 loses absolute precision as
/// coordinates grow and every tolerance downstream is relative to the extent, so an operation runs in a
/// local frame and adds the offset back on the way out.</para>
///
/// <para>Extracted from [BooleanOperation] so other mesh operators can share one loader instead of each
/// growing their own triangulation and planarity rules.</para>
/// </remarks>
internal static class PolygonLoader
{
    /// <summary>
    /// Loads faces <c>[faceStart, faceStart + faceCount)</c> as convex polygons. A negative
    /// <paramref name="faceCount"/> means "to the end of the mesh".
    /// </summary>
    /// <param name="operandIndex">Recorded on every polygon as its <see cref="Polygon.SourceOperand"/>.</param>
    public static List<Polygon> Load(MeshGeometry source, AttributeSchema schema, PointWeld weld, int operandIndex,
                                     Vector3 origin, int faceStart = 0, int faceCount = -1)
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
    /// every polygon handed to a splitter is convex (and therefore planar, being a triangle).
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
        Log.Warning($"PolygonLoader: could not ear-clip a {count}-corner face; falling back to a triangle fan.");
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

    /// <summary>Longest edge of a face, floored so it can be used as a relative scale.</summary>
    public static float LongestEdge(List<Vert> vertices)
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

    /// <summary>Planarity threshold of a face, relative to its longest edge.</summary>
    private const float PlanarityTolerance = 1e-4f;

    /// <summary>Sine-of-angle slack for calling a corner convex, relative to the face's squared size.</summary>
    private const float ConvexityTolerance = 1e-9f;
}
