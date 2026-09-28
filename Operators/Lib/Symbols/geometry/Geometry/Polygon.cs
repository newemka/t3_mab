#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;

namespace Lib.geometry;

/// <summary>
/// A convex polygon: an ordered ring of corners plus the plane it lies in.
/// </summary>
/// <remarks>
/// Convexity is the contract, because the splitter is only exact for convex pieces. <see cref="PolygonLoader"/>
/// enforces it on load - a face that is planar and convex stays one polygon, and anything else is cut
/// into triangles - so every polygon handed to a splitter can be divided into exactly two convex
/// pieces by one plane.
/// </remarks>
internal sealed class Polygon
{
    public Polygon(Vert[] vertices, in HalfSpace plane, int sourceOperand)
    {
        Vertices = vertices;
        Plane = plane;
        SourceOperand = sourceOperand;
    }

    public Vert[] Vertices;

    public HalfSpace Plane;

    /// <summary>
    /// Which operand this polygon came from: 0 for the left input, 1..n for the operands. Lets a
    /// consumer mark the surfaces an operation exposed rather than those it inherited.
    /// </summary>
    public readonly int SourceOperand;

    public int CornerCount => Vertices.Length;

    /// <summary>Reverses the winding and the plane together, which is what "invert" means for a solid.</summary>
    public void Flip()
    {
        Array.Reverse(Vertices);
        Plane = Plane.Flipped;
    }
}
