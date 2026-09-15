#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using T3.Core.Utils;
using WaterTrans.GlyphLoader;
using WaterTrans.GlyphLoader.Geometry;

namespace Lib.geometry;

/// <summary>
/// Lays out a string with WaterTrans.GlyphLoader and emits glyph outlines as
/// CurveGeometry: one part per glyph, closed contours of cubic beziers, with
/// per-glyph attributes. Variable-font axes and kerning are not supported by
/// GlyphLoader 1.2.2 and are reported via the status provider.
/// </summary>
[Guid("f7a2c9e1-4b63-4d08-9e5a-2c7f8b1d6a34")]
[ExportDependencies("WaterTrans.GlyphLoader.dll")]
internal sealed class TextToCurvesWTG : Instance<TextToCurvesWTG>, IDescriptiveFilename, IStatusProvider
{
    [Output(Guid = "3e8b5f2a-9c14-4d67-a0e3-7b1f6c9a2e85")]
    public readonly Slot<CurveGeometry?> Curves = new();

    [Output(Guid = "a4d2f7c9-1b83-4e56-8f0a-5c9e2b7d1a63")]
    public readonly Slot<int> GlyphCount = new();

    public TextToCurvesWTG()
    {
        _resource = new Resource<LoadedFont>(Path, TryLoadFont, allowDisposal: false);
        _resource.AddDependentSlots(Curves, GlyphCount);
        Curves.UpdateAction = Update;
        GlyphCount.UpdateAction = Update;
    }

    private void Update(EvaluationContext context)
    {
        var text = Text.GetValue(context) ?? string.Empty;
        var size = MathF.Max(Size.GetValue(context), 1e-4f);
        var lineSpacing = LineSpacing.GetValue(context);
        var alignment = (Alignments)Alignment.GetValue(context).Clamp(0, 2);
        var kerning = Kerning.GetValue(context);
        var weight = Weight.GetValue(context);
        var axisTag = Axis.GetValue(context)?.Trim() ?? string.Empty;
        var axisValue = AxisValue.GetValue(context);
        var pivot = Pivot.GetValue(context);
        var maxEdgeLength = MathF.Max(MaxEdgeLength.GetValue(context), 0f);
        var evenSpacing = EvenSpacing.GetValue(context);

        if (!_resource.TryGetValue(context, out var loadedFont))
        {
            _warningMessage = $"Failed loading font {Path.Value}";
            Curves.Value = null;
            GlyphCount.Value = 0;
            return;
        }

        _warningMessage = string.Empty;
        if (text.Length == 0)
        {
            Curves.Value = null;
            GlyphCount.Value = 0;
            return;
        }

        if (weight > 0 || axisTag.Length == 4)
            _warningMessage = "Variable-font axes are not supported by WaterTrans.GlyphLoader.";

        if (kerning)
            _warningMessage = "Kerning is not supported by WaterTrans.GlyphLoader.";

        var tf = loadedFont.Typeface;

        _collector.Begin(text, pivot, maxEdgeLength, evenSpacing);
        try
        {
            LayoutText(tf, text, size, lineSpacing, alignment);
        }
        catch (Exception e)
        {
            _warningMessage = $"Text layout failed: {e.Message}";
            Log.Warning(_warningMessage, this);
            Curves.Value = null;
            GlyphCount.Value = 0;
            return;
        }

        _collector.Finish(_output);
        Curves.Value = _output;
        GlyphCount.Value = _output.Parts.Length;
    }

    /// <summary>
    /// Manual horizontal layout: split by newline, measure each line, align it,
    /// then walk code points and emit one glyph per code point. Kerning is skipped
    /// because GlyphLoader 1.2.2 does not expose it.
    /// </summary>
    private void LayoutText(Typeface tf, string text, float size, float lineSpacing, Alignments alignment)
    {
        var lineHeight = (float)(tf.Height * size) * lineSpacing;
        var baseline = (float)(tf.Baseline * size);

        var lines = text.Split('\n');
        var charIndex = 0;
        var wordIndex = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineWidth = MeasureLine(tf, line, size);

            var startX = alignment switch
            {
                Alignments.Center => -lineWidth * 0.5f,
                Alignments.Right => -lineWidth,
                _ => 0f,
            };

            var penX = startX;
            var baselineY = -lineIndex * lineHeight - baseline;

            var i = 0;
            while (i < line.Length)
            {
                var consumed = DecodeRune(line, i, out var codePoint);
                var glyphIndex = LookupGlyph(tf, codePoint);
                if (glyphIndex == 0)
                {
                    penX += (float)(tf.AdvanceWidths[0] * size);
                    i += consumed;
                    charIndex += consumed;
                    continue;
                }

                var advance = (float)(tf.AdvanceWidths[glyphIndex] * size);
                var geometry = tf.GetGlyphOutline(glyphIndex, size);

                _collector.AddGlyph(
                    geometry,
                    penX, baselineY,
                    codePoint, glyphIndex,
                    charIndex, wordIndex, lineIndex,
                    advance);

                // Count words at whitespace boundaries
                if (char.IsWhiteSpace(line[i]) && (i == 0 || !char.IsWhiteSpace(line[i - 1])))
                    wordIndex++;

                penX += advance;
                i += consumed;
                charIndex += consumed;
            }

            // Newline counts as a word boundary
            wordIndex++;
        }
    }

    private static float MeasureLine(Typeface tf, string line, float size)
    {
        var width = 0f;
        var i = 0;
        while (i < line.Length)
        {
            var consumed = DecodeRune(line, i, out var codePoint);
            var glyphIndex = LookupGlyph(tf, codePoint);
            width += (float)(tf.AdvanceWidths[glyphIndex] * size);
            i += consumed;
        }
        return width;
    }

    private static int DecodeRune(string s, int index, out int codePoint)
    {
        if (char.IsHighSurrogate(s[index]) && index + 1 < s.Length && char.IsLowSurrogate(s[index + 1]))
        {
            codePoint = char.ConvertToUtf32(s[index], s[index + 1]);
            return 2;
        }

        codePoint = s[index];
        return 1;
    }

    private static ushort LookupGlyph(Typeface tf, int codePoint)
    {
        return tf.CharacterToGlyphMap.TryGetValue(codePoint, out var glyph) ? glyph : (ushort)0;
    }

    private bool TryLoadFont(FileResource file, LoadedFont? currentValue, [NotNullWhen(true)] out LoadedFont? newValue,
                             [NotNullWhen(false)] out string? failureReason)
    {
        try
        {
            var stream = File.OpenRead(file.AbsolutePath);
            var typeface = new Typeface(stream);
            newValue = new LoadedFont(typeface, stream);
            failureReason = null;
            return true;
        }
        catch (Exception e)
        {
            failureReason = $"Can't read font {file.AbsolutePath}: {e.Message}";
            Log.Warning(failureReason, this);
            newValue = null;
            return false;
        }
    }

    /// <summary>The stream must stay open because GlyphLoader lazily reads glyph data.</summary>
    private sealed class LoadedFont : IDisposable
    {
        public LoadedFont(Typeface typeface, Stream stream)
        {
            Typeface = typeface;
            _stream = stream;
        }

        public Typeface Typeface { get; }
        private readonly Stream _stream;

        public void Dispose()
        {
            _stream.Dispose();
        }
    }

    private enum Alignments
    {
        Left,
        Center,
        Right,
    }

    /// <summary>
    /// Converts GlyphLoader PathGeometry into the CurveGeometry representation.
    /// The outline is in font design units scaled by the requested size; Y is
    /// flipped so text reads upright in scene space.
    /// </summary>
    private sealed class OutlineCollector
    {
        public void Begin(string text, Vector2 pivot, float maxEdgeLength, bool evenSpacing)
        {
            _pivot = pivot;
            _maxEdgeLength = maxEdgeLength;
            _evenSpacing = evenSpacing;
            _positions.Clear();
            _handlesIn.Clear();
            _handlesOut.Clear();
            _contourOffsets.Clear();
            _contourOffsets.Add(0);
            _contourClosed.Clear();
            _parts.Clear();
            _codePoints.Clear();
            _glyphIds.Clear();
            _charIndices.Clear();
            _wordIndices.Clear();
            _lineIndices.Clear();
            _advances.Clear();
        }

        public void Finish(CurveGeometry target)
        {
            if (_maxEdgeLength > 0 && _positions.Count > 0)
            {
                var contourMap = SubdivideContours(_maxEdgeLength, _evenSpacing);
                for (int i = 0; i < _parts.Count; i++)
                {
                    var p = _parts[i];
                    _parts[i] = new CurvePart(contourMap[p.ContourStart], p.ContourCount,
                                              p.Pivot, p.Id, p.SeedIndex);
                }
            }

            target.Positions = _positions.ToArray();
            target.HandlesIn = _handlesIn.ToArray();
            target.HandlesOut = _handlesOut.ToArray();
            target.ContourOffsets = _contourOffsets.ToArray();
            target.ContourClosed = _contourClosed.ToArray();
            target.Parts = _parts.ToArray();

            var count = _parts.Count;
            target.Attributes.Clear();
            Fill(target.Attributes.GetOrCreate<int>(CurveAttributeNames.CodePoint, AttributeDomain.Part, count).Values, _codePoints);
            Fill(target.Attributes.GetOrCreate<int>(CurveAttributeNames.GlyphId, AttributeDomain.Part, count).Values, _glyphIds);
            Fill(target.Attributes.GetOrCreate<int>(CurveAttributeNames.CharIndex, AttributeDomain.Part, count).Values, _charIndices);
            Fill(target.Attributes.GetOrCreate<int>(CurveAttributeNames.WordIndex, AttributeDomain.Part, count).Values, _wordIndices);
            Fill(target.Attributes.GetOrCreate<int>(CurveAttributeNames.LineIndex, AttributeDomain.Part, count).Values, _lineIndices);
            Fill(target.Attributes.GetOrCreate<float>(CurveAttributeNames.Advance, AttributeDomain.Part, count).Values, _advances);
            target.InvalidateCaches();
        }

        private static void Fill<T>(T[] target, List<T> source)
        {
            for (var i = 0; i < source.Count; i++)
                target[i] = source[i];
        }

        public void AddGlyph(PathGeometry geometry, float penX, float penY,
                             int codePoint, int glyphId,
                             int charIndex, int wordIndex, int lineIndex,
                             float advance)
        {
            var contourStart = _contourOffsets.Count - 1;
            var startPositionCount = _positions.Count;

            // Walk each figure of the glyph
            foreach (var figure in geometry.Figures)
            {
                var figureStart = _positions.Count;

                // Start point (flipped and translated)
                AddAnchor(figure.StartPoint.X + penX, -(figure.StartPoint.Y) + penY);

                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            AddAnchor(line.Point.X + penX, -(line.Point.Y) + penY);
                            break;

                        case BezierSegment bezier:
                            {
                                var last = _positions.Count - 1;
                                _handlesOut[last] = Flip(bezier.Point1.X, bezier.Point1.Y, penX, penY);
                                AddAnchor(bezier.Point3.X + penX, -(bezier.Point3.Y) + penY);
                                _handlesIn[_positions.Count - 1] = Flip(bezier.Point2.X, bezier.Point2.Y, penX, penY);
                                break;
                            }
                        case QuadraticBezierSegment quad:
                            {
                                var last = _positions.Count - 1;
                                var p0 = _positions[last];
                                var c = Flip(quad.Point1.X, quad.Point1.Y, penX, penY);
                                var p1 = Flip(quad.Point2.X, quad.Point2.Y, penX, penY);

                                _handlesOut[last] = p0 + (c - p0) * (2f / 3f);
                                AddAnchor(quad.Point2.X + penX, -(quad.Point2.Y) + penY);
                                _handlesIn[_positions.Count - 1] = p1 + (c - p1) * (2f / 3f);
                                break;
                            }
                        default:
                            Log.Warning($"Unsupported glyph outline segment type: {segment.GetType().Name}");
                            break;
                    }
                }

                var count = _positions.Count - figureStart;
                if (count < 2)
                {
                    _positions.RemoveRange(figureStart, count);
                    _handlesIn.RemoveRange(figureStart, count);
                    _handlesOut.RemoveRange(figureStart, count);
                    continue;
                }

                // Fold an explicit close back to the start into the closed flag
                var first = _positions[figureStart];
                var lastIndex = _positions.Count - 1;
                if (Vector3.DistanceSquared(_positions[lastIndex], first) < 1e-10f)
                {
                    _handlesIn[figureStart] = _handlesIn[lastIndex];
                    _positions.RemoveAt(lastIndex);
                    _handlesIn.RemoveAt(lastIndex);
                    _handlesOut.RemoveAt(lastIndex);
                }

                _contourOffsets.Add(_positions.Count);
                _contourClosed.Add(true);
            }

            var contourCount = _contourOffsets.Count - 1 - contourStart;
            if (contourCount == 0)
                return;

            // Pivot from the glyph bounds, consistent with the SixLabors implementation
            var (minX, minY, maxX, maxY) = ComputeBounds(startPositionCount, _positions.Count);
            var pivotX = minX + (maxX - minX) * (_pivot.X + 1f) * 0.5f;
            var pivotY = minY + (maxY - minY) * (_pivot.Y + 1f) * 0.5f;

            _parts.Add(new CurvePart(contourStart, contourCount, new Vector3(pivotX, pivotY, 0), _parts.Count, charIndex));
            _codePoints.Add(codePoint);
            _glyphIds.Add(glyphId);
            _charIndices.Add(charIndex);
            _wordIndices.Add(wordIndex);
            _lineIndices.Add(lineIndex);
            _advances.Add(advance);
        }

        private (float MinX, float MinY, float MaxX, float MaxY) ComputeBounds(int start, int end)
        {
            var minX = float.MaxValue;
            var minY = float.MaxValue;
            var maxX = float.MinValue;
            var maxY = float.MinValue;

            for (var i = start; i < end; i++)
            {
                var p = _positions[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;

                var hIn = _handlesIn[i];
                if (hIn.X < minX) minX = hIn.X;
                if (hIn.Y < minY) minY = hIn.Y;
                if (hIn.X > maxX) maxX = hIn.X;
                if (hIn.Y > maxY) maxY = hIn.Y;

                var hOut = _handlesOut[i];
                if (hOut.X < minX) minX = hOut.X;
                if (hOut.Y < minY) minY = hOut.Y;
                if (hOut.X > maxX) maxX = hOut.X;
                if (hOut.Y > maxY) maxY = hOut.Y;
            }

            return (minX, minY, maxX, maxY);
        }

        private void AddAnchor(double x, double y, Vector3? handleIn = null)
        {
            var position = new Vector3((float)x, (float)y, 0);
            _positions.Add(position);
            _handlesIn.Add(handleIn ?? position);
            _handlesOut.Add(position);
        }

        private static Vector3 Flip(double x, double y, double penX, double penY) =>
            new((float)(x + penX), (float)(-y + penY), 0);

        /// <summary>Rough length of a cubic from its control polygon and chord.</summary>
        private static float ApproximateCubicLength(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
        {
            float polyLen = Vector3.Distance(p0, p1) + Vector3.Distance(p1, p2) + Vector3.Distance(p2, p3);
            float chordLen = Vector3.Distance(p0, p3);
            return (polyLen + chordLen) * 0.5f;
        }

        /// <summary>De Casteljau split of one cubic at parameter t.</summary>
        private static ((Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3) L,
                        (Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3) R)
            SplitCubicAt((Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3) c, float t)
        {
            var q0 = Vector3.Lerp(c.P0, c.P1, t);
            var q1 = Vector3.Lerp(c.P1, c.P2, t);
            var q2 = Vector3.Lerp(c.P2, c.P3, t);
            var r0 = Vector3.Lerp(q0, q1, t);
            var r1 = Vector3.Lerp(q1, q2, t);
            var s = Vector3.Lerp(r0, r1, t);
            return ((c.P0, q0, r0, s), (s, r1, q2, c.P3));
        }

        /// <summary>Splits a cubic into n equal-parameter sub-cubics (exact, shape-preserving).</summary>
        private static List<(Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3)>
            SplitCubicN(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int n)
        {
            var result = new List<(Vector3, Vector3, Vector3, Vector3)>(n);
            var current = (p0, p1, p2, p3);
            for (int k = 0; k < n; k++)
            {
                int remaining = n - k;
                if (remaining == 1) { result.Add(current); break; }
                var (left, right) = SplitCubicAt(current, 1f / remaining);
                result.Add(left);
                current = right;
            }
            return result;
        }

        private static Vector3 CubicPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
        {
            float u = 1f - t;
            float uu = u * u;
            float tt = t * t;
            return uu * u * p0 + 3f * uu * t * p1 + 3f * u * tt * p2 + tt * t * p3;
        }

        /// <summary>
        /// Splits a cubic into n sub-cubics whose arc-lengths are equal. Achieved by
        /// building a small arc-length lookup table, then inverse-mapping the target
        /// lengths to t values. Exact cubics are preserved by De Casteljau.
        /// </summary>
        private static List<(Vector3 P0, Vector3 P1, Vector3 P2, Vector3 P3)>
            SplitCubicNEvenSpacing(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, int n)
        {
            const int lutSize = 64;
            var lutT = new float[lutSize + 1];
            var lutLen = new float[lutSize + 1];
            var prev = p0;
            lutLen[0] = 0f;
            for (int i = 1; i <= lutSize; i++)
            {
                float t = i / (float)lutSize;
                var pt = CubicPoint(p0, p1, p2, p3, t);
                lutLen[i] = lutLen[i - 1] + Vector3.Distance(prev, pt);
                lutT[i] = t;
                prev = pt;
            }

            float totalLen = lutLen[lutSize];
            if (totalLen <= 1e-9f)
                return SplitCubicN(p0, p1, p2, p3, n); // degenerate; fall back

            // Inverse map: target arc length -> parameter t, by interpolating the LUT
            float TForLen(float target)
            {
                int lo = 0, hi = lutSize;
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) >> 1;
                    if (lutLen[mid] < target) lo = mid; else hi = mid;
                }
                float segLen = lutLen[hi] - lutLen[lo];
                float frac = segLen > 0f ? (target - lutLen[lo]) / segLen : 0f;
                return lutT[lo] + frac * (lutT[hi] - lutT[lo]);
            }

            // Split positions in the ORIGINAL t parameter space
            var tValues = new float[n + 1];
            tValues[0] = 0f;
            tValues[n] = 1f;
            for (int k = 1; k < n; k++)
                tValues[k] = TForLen(totalLen * k / n);

            // Sequentially split: each SplitCubicAt is applied to the *remaining* right part,
            // so the local t is remapped from the original interval [remainingT0, 1].
            var result = new List<(Vector3, Vector3, Vector3, Vector3)>(n);
            var remaining = (P0: p0, P1: p1, P2: p2, P3: p3);
            float remainingT0 = 0f;
            for (int k = 0; k < n; k++)
            {
                if (k == n - 1) { result.Add(remaining); break; }

                float tEnd = tValues[k + 1];
                float localT = (tEnd - remainingT0) / (1f - remainingT0);
                // Guard against numerical extremes
                if (localT <= 0f) localT = 1e-7f;
                if (localT >= 1f) localT = 1f - 1e-7f;

                var (left, right) = SplitCubicAt(remaining, localT);
                result.Add(left);
                remaining = right;
                remainingT0 = tEnd;
            }
            return result;
        }

        private int[] SubdivideContours(float maxEdgeLength, bool evenSpacing)
        {
            var newPositions = new List<Vector3>();
            var newHandlesIn = new List<Vector3>();
            var newHandlesOut = new List<Vector3>();
            var newContourOffsets = new List<int> { 0 };
            var newContourClosed = new List<bool>();

            // Per-anchor, may be rewritten by the segments that touch it
            var updatedHOut = new Vector3[_positions.Count];
            var updatedHIn = new Vector3[_positions.Count];
            for (int i = 0; i < _positions.Count; i++)
            {
                updatedHOut[i] = _handlesOut[i];
                updatedHIn[i] = _handlesIn[i];
            }

            var oldToNew = new int[_contourOffsets.Count - 1];
            int contourCount = _contourOffsets.Count - 1;

            for (int c = 0; c < contourCount; c++)
            {
                oldToNew[c] = newContourOffsets.Count - 1;

                int start = _contourOffsets[c];
                int end = _contourOffsets[c + 1];
                int count = end - start;
                bool closed = _contourClosed[c];

                // Degenerate: copy verbatim
                if (count < 2)
                {
                    for (int i = start; i < end; i++)
                    {
                        newPositions.Add(_positions[i]);
                        newHandlesIn.Add(_handlesIn[i]);
                        newHandlesOut.Add(_handlesOut[i]);
                    }
                    newContourOffsets.Add(newPositions.Count);
                    newContourClosed.Add(closed);
                    continue;
                }

                // For each original segment, decide whether to subdivide and remember the
                // intermediate anchors to insert after anchor `start + i`.
                var intermediates = new Dictionary<int, List<(Vector3 Pos, Vector3 HIn, Vector3 HOut)>>();
                int segmentCount = closed ? count : count - 1;

                for (int i = 0; i < segmentCount; i++)
                {
                    int aIdx = start + i;
                    int bIdx = (i + 1 < count) ? start + i + 1 : start;

                    var P0 = _positions[aIdx];
                    var P1 = _handlesOut[aIdx];
                    var P2 = _handlesIn[bIdx];
                    var P3 = _positions[bIdx];

                    float len = ApproximateCubicLength(P0, P1, P2, P3);
                    if (maxEdgeLength <= 0 || len <= maxEdgeLength) continue;

                    int n = Math.Max(1, (int)Math.Ceiling(len / maxEdgeLength));
                    if (n == 1) continue;

                    var sub = evenSpacing
                           ? SplitCubicNEvenSpacing(P0, P1, P2, P3, n)
                           : SplitCubicN(P0, P1, P2, P3, n);

                    // Rewrite the two shared handles of this segment
                    updatedHOut[aIdx] = sub[0].P1;
                    updatedHIn[bIdx] = sub[n - 1].P2;

                    // Every sub-segment except the last contributes one intermediate anchor
                    var list = new List<(Vector3, Vector3, Vector3)>(n - 1);
                    for (int k = 0; k < n - 1; k++)
                    {
                        list.Add((
                            sub[k].P3,          // position (== sub[k+1].P0)
                            sub[k].P2,          // incoming handle
                            sub[k + 1].P1));    // outgoing handle
                    }
                    intermediates[aIdx] = list;
                }

                // Emit anchors + their trailing intermediates
                for (int i = 0; i < count; i++)
                {
                    int idx = start + i;
                    newPositions.Add(_positions[idx]);
                    newHandlesIn.Add(updatedHIn[idx]);
                    newHandlesOut.Add(updatedHOut[idx]);

                    if (intermediates.TryGetValue(idx, out var list))
                    {
                        foreach (var (p, hi, ho) in list)
                        {
                            newPositions.Add(p);
                            newHandlesIn.Add(hi);
                            newHandlesOut.Add(ho);
                        }
                    }
                }

                newContourOffsets.Add(newPositions.Count);
                newContourClosed.Add(closed);
            }

            // Write back
            _positions.Clear(); _positions.AddRange(newPositions);
            _handlesIn.Clear(); _handlesIn.AddRange(newHandlesIn);
            _handlesOut.Clear(); _handlesOut.AddRange(newHandlesOut);
            _contourOffsets.Clear(); _contourOffsets.AddRange(newContourOffsets);
            _contourClosed.Clear(); _contourClosed.AddRange(newContourClosed);

            return oldToNew;
        }

        private readonly List<Vector3> _positions = [];
        private readonly List<Vector3> _handlesIn = [];
        private readonly List<Vector3> _handlesOut = [];
        private readonly List<int> _contourOffsets = [0];
        private readonly List<bool> _contourClosed = [];
        private readonly List<CurvePart> _parts = [];
        private readonly List<int> _codePoints = [];
        private readonly List<int> _glyphIds = [];
        private readonly List<int> _charIndices = [];
        private readonly List<int> _wordIndices = [];
        private readonly List<int> _lineIndices = [];
        private readonly List<float> _advances = [];
        private Vector2 _pivot;
        private float _maxEdgeLength;
        private bool _evenSpacing;
    }

    public IStatusProvider.StatusLevel GetStatusLevel()
    {
        return string.IsNullOrEmpty(_warningMessage) ? IStatusProvider.StatusLevel.Success : IStatusProvider.StatusLevel.Warning;
    }

    public string GetStatusMessage() => _warningMessage;

    public InputSlot<string> SourcePathSlot => Path;

    private readonly Resource<LoadedFont> _resource;
    private readonly OutlineCollector _collector = new();
    private readonly CurveGeometry _output = new();
    private string _warningMessage = string.Empty;

    [Input(Guid = "1f8a3c5e-7d92-4b06-a4e1-c6b9d2f7a350")]
    public readonly InputSlot<string> Text = new();

    [Input(Guid = "7c2e9a4b-5f61-4d38-b8a0-e3d5c1f9b724")]
    public readonly InputSlot<string> Path = new();

    [Input(Guid = "a5d1f7c3-8b24-4e69-9c0d-2f4a6e8b1d95")]
    public readonly InputSlot<float> Size = new();

    [Input(Guid = "3e6b8d2a-c4f9-4a17-b5e3-8d0c7a2f6e41")]
    public readonly InputSlot<float> LineSpacing = new();

    [Input(Guid = "d9c4a6e2-1b83-4f50-a7e9-5c2d8b3f1a67", MappedType = typeof(Alignments))]
    public readonly InputSlot<int> Alignment = new();

    [Input(Guid = "6b3f1e9d-a2c5-4d84-8e7b-0f9a4c6d2e13")]
    public readonly InputSlot<bool> Kerning = new();

    [Input(Guid = "f4a9c2e7-6d18-4b53-9e0c-a7b1d5f3c826")]
    public readonly InputSlot<float> Weight = new();

    [Input(Guid = "8c5e1d7a-3f92-4a64-b0d8-6e2c9f4a1b57")]
    public readonly InputSlot<string> Axis = new();

    [Input(Guid = "2e7b4f9c-a1d6-4c38-8b5e-d9f0a3c7e614")]
    public readonly InputSlot<float> AxisValue = new();

    [Input(Guid = "4a7f2b9e-8c31-4d56-a0b2-c7e4f9d1a823")]
    public readonly InputSlot<Vector2> Pivot = new();

    [Input(Guid = "85c26820-f36b-4f44-9e78-4b63be33a65d")]
    public readonly InputSlot<float> MaxEdgeLength = new();

    [Input(Guid = "b8d3f5a2-9e41-4c76-8a0b-5d2f7c9e1a34")]
    public readonly InputSlot<bool> EvenSpacing = new();
}