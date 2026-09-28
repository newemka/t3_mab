#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Geometry;

namespace Lib.geometry;

/// <summary>
/// One column of corner attributes: a name, its width, and where it starts in a flattened corner array.
/// </summary>
internal sealed class AttributeColumn(string name, int elementSize, int offset)
{
    public readonly string Name = name;
    public readonly int ElementSize = elementSize;
    public int Offset = offset;
}

/// <summary>
/// The corner attributes a mesh operation carries, resolved to one shared schema for every mesh taking
/// part. A corner then keeps the attributes of whichever fragment it came from, and the result can be
/// written as one typed buffer per name without reconciling columns of different widths.
/// </summary>
/// <remarks>
/// <para>Only attributes that <em>every</em> contributing mesh carries are kept. An attribute that one
/// input has and another does not would have to be invented for the second, and there is no safe
/// invented value: a zero <c>Normal</c> is unlit and a zero <c>Tangent</c> makes the draw shader's
/// normalized TBN degenerate, which paints the face black. Dropping the attribute instead lets every
/// consumer fall back to the value it derives from the geometry, which is exactly what it does for input
/// that never had the attribute.</para>
///
/// <para>Extracted from [BooleanOperation] so other mesh operators share one attribute-negotiation rule.</para>
/// </remarks>
internal sealed class AttributeSchema
{
    public readonly List<AttributeColumn> Columns = [];
    public int ComponentCount { get; private set; }

    /// <summary>The attributes common to every contributing mesh; empty ones do not narrow the schema.</summary>
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

    /// <summary>
    /// Binds each column to the source's own attribute of that name, once per mesh. An entry is null
    /// when this mesh does not carry that attribute at all, which <see cref="ReadCorner"/> handles by
    /// clearing those components.
    /// </summary>
    public GeometryAttribute[] ResolveFor(MeshGeometry source)
    {
        var resolved = new GeometryAttribute[Columns.Count];
        for (var i = 0; i < Columns.Count; i++)
        {
            // FindIn answers null for a mesh that lacks the attribute; the array is read through
            // ReadCorner, which treats null as "no values here" rather than dereferencing it.
            resolved[i] = FindIn(source, Columns[i])!;
        }

        return resolved;
    }

    /// <summary>Reads one corner into <paramref name="target"/>, in schema order, clearing any column this mesh lacks.</summary>
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

    /// <summary>Width in floats of the attribute's element type, or 0 for a type the kernel does not carry.</summary>
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
