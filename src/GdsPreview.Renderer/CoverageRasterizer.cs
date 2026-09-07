using System.Drawing;
using System.Drawing.Imaging;
using GdsPreview.Core;

namespace GdsPreview.Renderer;

/// <summary>
/// Analytical polygon coverage on the final grid. Row-local scratch is reused;
/// no full-canvas coverage planes, supersampling, or minimum feature width.
/// </summary>
internal sealed class CoverageRasterizer
{
    private readonly int _width, _height, _stride;
    private readonly int[] _background;
    private readonly int[] _starts;
    private int[] _nextEdge = [];
    private readonly double[] _area, _steps, _clipArea, _clipSteps;
    private readonly double[][] _rows;
    private readonly float[] _clippedRow;
    private LayoutCompositor? _layout;

    public CoverageRasterizer(int width, int height, Color background)
    {
        _width = width;
        _height = height;
        _stride = checked(width + 2);
        _background = new int[checked(width * height)];
        Array.Fill(_background, background.ToArgb());
        _starts = new int[checked(height + 2)];
        _area = new double[_stride];
        _steps = new double[_stride];
        _clipArea = new double[_stride];
        _clipSteps = new double[_stride];
        _rows = [new double[_stride], new double[_stride], new double[_stride]];
        _clippedRow = new float[_stride];
    }

    public void FillBackground(RectangleF rectangle, Color color)
    {
        for (var y = Math.Max(0, (int)Math.Floor(rectangle.Top)); y < Math.Min(_height, rectangle.Bottom); y++)
        for (var x = Math.Max(0, (int)Math.Floor(rectangle.Left)); x < Math.Min(_width, rectangle.Right); x++)
        {
            var index = y * _width + x;
            _background[index] = color.ToArgb();
            _layout?.Clear(index);
        }
    }

    internal void VisitCoverage(ReadOnlySpan<PointD> points, RectangleF viewport, Action<int, double> sink)
        => Rasterize(points, Color.White, viewport, sink);

    public void DrawLayoutPolygon(ReadOnlySpan<PointD> points, Color baseColor, RectangleF viewport)
        => Rasterize(points, baseColor, viewport, null);

    private void Rasterize(ReadOnlySpan<PointD> points, Color color, RectangleF viewport, Action<int, double>? sink)
    {
        if (points.Length < 3) return;
        var bounds = BoundsD.Empty;
        foreach (var point in points)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y))
                throw new GdsFormatException("A transformed coordinate is not finite.");
            bounds = bounds.Include(point);
        }
        var clipLeft = Math.Max(0, viewport.Left);
        var clipTop = Math.Max(0, viewport.Top);
        var clipRight = Math.Min(_width, viewport.Right);
        var clipBottom = Math.Min(_height, viewport.Bottom);
        var minX = Math.Max(clipLeft, bounds.MinX);
        var minY = Math.Max(clipTop, bounds.MinY);
        var maxX = Math.Min(clipRight, bounds.MaxX);
        var maxY = Math.Min(clipBottom, bounds.MaxY);
        if (minX >= maxX || minY >= maxY) return;
        var left = (int)Math.Floor(minX);
        var right = (int)Math.Ceiling(maxX);
        var top = (int)Math.Floor(minY);
        var bottom = (int)Math.Ceiling(maxY);
        var rgb = SrgbColorSpace.DecodeRgb(color.ToArgb());
        var layout = sink is null;
        if (layout) _layout ??= new LayoutCompositor(_background.Length);

        // With an extent <= twice the inward-band width, opposite bands cover
        // all geometry. Their neighbour calculation is redundant, not a different
        // rendering style. This avoids halo work for millions of tiny shapes.
        var onlyOutline = layout && (bounds.Width <= 2 * LayoutCompositor.OutlineWidthPixels ||
            bounds.Height <= 2 * LayoutCompositor.OutlineWidthPixels);
        var halo = layout && !onlyOutline;
        var scanLeft = left - (halo ? 1 : 0);
        var scanRight = right + (halo ? 1 : 0);
        var scanTop = top - (halo ? 1 : 0);
        var scanBottom = bottom + (halo ? 1 : 0);
        var fractionalX = clipLeft > left || clipRight < right;

        // Bucket edges by their first row. One reusable integer per input vertex;
        // active edges link through the same array, without sorting or copies.
        if (_nextEdge.Length < points.Length)
            Array.Resize(ref _nextEdge, Math.Max(points.Length, _nextEdge.Length * 2));
        Array.Fill(_starts, -1, scanTop + 1, scanBottom - scanTop);
        var previous = points[^1];
        for (var i = 0; i < points.Length; i++)
        {
            var point = points[i];
            var startY = Math.Max(Math.Min(previous.Y, point.Y), scanTop);
            var endY = Math.Min(Math.Max(previous.Y, point.Y), scanBottom);
            if (startY < endY)
            {
                var row = (int)Math.Floor(startY) + 1;
                _nextEdge[i] = _starts[row];
                _starts[row] = i;
            }
            previous = point;
        }

        var active = -1;
        for (var y = scanTop; y < scanBottom; y++)
        {
            for (var edge = _starts[y + 1]; edge >= 0;)
            {
                var next = _nextEdge[edge];
                _nextEdge[edge] = active;
                active = edge;
                edge = next;
            }
            // Full pixels reuse halo coverage directly. Extra integration is
            // needed only where a fractional viewport boundary clips a pixel.
            var clipped = halo && y >= top && y < bottom &&
                (fractionalX || clipTop > y || clipBottom < y + 1);
            var slot = (y + 1) % 3;
            var previousEdge = -1;
            for (var edge = active; edge >= 0;)
            {
                var next = _nextEdge[edge];
                var from = edge == 0 ? points[^1] : points[edge - 1];
                var to = points[edge];
                var sign = from.Y < to.Y ? 1 : -1;
                var low = sign > 0 ? from : to;
                var high = sign > 0 ? to : from;
                var slope = (high.X - low.X) / (high.Y - low.Y);
                AddRowEdge(low, high, slope, sign, y, scanLeft, scanRight,
                    halo ? scanLeft : clipLeft, halo ? scanTop : clipTop,
                    halo ? scanRight : clipRight, halo ? scanBottom : clipBottom, _area, _steps);
                if (clipped)
                    AddRowEdge(low, high, slope, sign, y, left, right,
                        clipLeft, clipTop, clipRight, clipBottom, _clipArea, _clipSteps);
                if (high.Y <= y + 1)
                {
                    if (previousEdge < 0) active = next;
                    else _nextEdge[previousEdge] = next;
                }
                else previousEdge = edge;
                edge = next;
            }

            var currentRow = _rows[slot];
            var middleSlot = (slot + 2) % 3;
            var middle = _rows[middleSlot];
            var above = _rows[(slot + 1) % 3];
            var emitPrevious = halo && y > top && y <= bottom;
            var previousClipped = fractionalX || clipTop > y - 1 || clipBottom < y;
            double winding = 0, clippedWinding = 0;
            for (var x = scanLeft; x < scanRight; x++)
            {
                var column = x + 1;
                winding += _steps[column];
                var pixelWidth = halo ? 1 : Math.Min(x + 1, clipRight) - Math.Max(x, clipLeft);
                var coverage = Math.Clamp(Math.Abs(_area[column] + winding * pixelWidth), 0, 1);
                _area[column] = 0;
                _steps[column] = 0;
                if (!halo)
                {
                    if (coverage <= 0) continue;
                    var index = y * _width + x;
                    if (onlyOutline) _layout!.Add(index, rgb, (float)coverage, (float)coverage);
                    else sink!(index, coverage);
                    continue;
                }
                currentRow[column] = coverage;
                if (x < left || x >= right) continue;
                var covered = previousClipped ? _clippedRow[column] : (float)middle[column];
                if (emitPrevious && covered > 0)
                {
                    var fullCoverage = middle[column];
                    var thickness = LayoutCompositor.OutlineWidthPixels;
                    var horizontal = Math.Min(fullCoverage, Math.Max(0, thickness - middle[column - 1]) +
                        Math.Max(0, thickness - middle[column + 1]));
                    var vertical = Math.Min(fullCoverage, Math.Max(0, thickness - above[column]) +
                        Math.Max(0, thickness - coverage));
                    var band = fullCoverage > 0 ? horizontal + vertical * (1 - horizontal / fullCoverage) : 0;
                    _layout!.Add((y - 1) * _width + x, rgb, covered, Math.Clamp(band, 0, covered));
                }
                // Consume the previous clipped pixel before replacing it: this
                // mask needs one row, not a second three-row neighbourhood.
                if (!clipped) continue;
                clippedWinding += _clipSteps[column];
                var w = Math.Min(x + 1, clipRight) - Math.Max(x, clipLeft);
                _clippedRow[column] = (float)Math.Clamp(Math.Abs(_clipArea[column] + clippedWinding * w), 0, 1);
                _clipArea[column] = 0;
                _clipSteps[column] = 0;
            }
        }
    }

    private static void AddRowEdge(PointD low, PointD high, double slope, int sign, int y,
        int left, int right, double clipLeft, double clipTop, double clipRight, double clipBottom,
        double[] area, double[] steps)
    {
        var y0 = Math.Max(Math.Max(y, low.Y), clipTop);
        var y1 = Math.Min(Math.Min(y + 1, high.Y), clipBottom);
        if (y0 >= y1) return;
        var x0 = low.X + (y0 - low.Y) * slope;
        var x1 = low.X + (y1 - low.Y) * slope;
        var xLow = Math.Min(x0, x1);
        var xHigh = Math.Max(x0, x1);
        var span = xHigh - xLow;
        var signedHeight = (y1 - y0) * sign;
        var first = (int)Math.Clamp(Math.Floor(xLow), left, right);
        var full = (int)Math.Clamp(Math.Ceiling(xHigh), left, right);
        for (var x = first; x < full; x++)
        {
            var pixelLeft = Math.Max(x, clipLeft);
            var pixelRight = Math.Min(x + 1, clipRight);
            double average;
            if (span == 0) average = Math.Clamp(pixelRight - xLow, 0, pixelRight - pixelLeft);
            else
            {
                var a = Math.Clamp((pixelLeft - xLow) / span, 0, 1);
                var b = Math.Clamp((pixelRight - xLow) / span, 0, 1);
                average = a * (pixelRight - pixelLeft) +
                    (b - a) * (pixelRight - (xLow + span * (a + b) / 2));
            }
            area[x + 1] += signedHeight * average;
        }
        if (full < right) steps[full + 1] += signedHeight;
    }

    public unsafe Bitmap ToBitmap()
    {
        var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, _width, _height),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < _height; y++)
            {
                var output = (byte*)data.Scan0 + y * data.Stride;
                for (var x = 0; x < _width; x++)
                {
                    var index = y * _width + x;
                    var rgb = SrgbColorSpace.DecodeRgb(_background[index]);
                    if (_layout is not null) rgb = _layout.Blend(index, rgb);
                    output[x * 4] = SrgbColorSpace.Encode(rgb.Z);
                    output[x * 4 + 1] = SrgbColorSpace.Encode(rgb.Y);
                    output[x * 4 + 2] = SrgbColorSpace.Encode(rgb.X);
                    output[x * 4 + 3] = 255;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}
