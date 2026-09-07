using System.Drawing;
using System.Drawing.Imaging;
using GdsPreview.Core;

namespace GdsPreview.Renderer;

/// <summary>
/// Integrates polygon edges over the final pixel grid. There is no intermediate
/// cell image, sample-point test, cosmetic outline, or minimum feature width.
/// GDS boundaries are simple contours (including oppositely wound hole contours
/// connected by a retraced bridge). Signed edge areas give their covered area.
/// </summary>
internal sealed class CoverageRasterizer
{
    private readonly int _width;
    private readonly int _height;
    private readonly float[] _red;
    private readonly float[] _green;
    private readonly float[] _blue;
    private readonly double[] _area;
    private readonly double[] _steps;

    public CoverageRasterizer(int width, int height, Color background)
    {
        _width = width;
        _height = height;
        var length = checked(width * height);
        _red = new float[length];
        _green = new float[length];
        _blue = new float[length];
        _area = new double[length];
        _steps = new double[length];
        Array.Fill(_red, background.R);
        Array.Fill(_green, background.G);
        Array.Fill(_blue, background.B);
    }

    public void FillBackground(RectangleF rectangle, Color color)
    {
        for (var y = Math.Max(0, (int)Math.Floor(rectangle.Top)); y < Math.Min(_height, rectangle.Bottom); y++)
        for (var x = Math.Max(0, (int)Math.Floor(rectangle.Left)); x < Math.Min(_width, rectangle.Right); x++)
        {
            var index = y * _width + x;
            _red[index] = color.R;
            _green[index] = color.G;
            _blue[index] = color.B;
        }
    }

    public void FillPolygon(IReadOnlyList<PointD> points, Color color, RectangleF viewport)
    {
        if (points.Count < 3) return;
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

        var previous = points[^1];
        foreach (var point in points)
        {
            AddEdge(previous, point, left, right, top, bottom, clipLeft, clipTop, clipRight, clipBottom);
            previous = point;
        }

        for (var y = top; y < bottom; y++)
        {
            double windingArea = 0;
            var row = y * _width;
            for (var x = left; x < right; x++)
            {
                var index = row + x;
                windingArea += _steps[index];
                var pixelWidth = Math.Min(x + 1, clipRight) - Math.Max(x, clipLeft);
                var coverage = Math.Clamp(Math.Abs(_area[index] + windingArea * pixelWidth), 0, 1);
                var alpha = (float)(coverage * color.A / 255.0);
                // Quantize only when exporting the completed frame. Tiny repeated
                // shapes still contribute even if each is below one 8-bit level.
                _red[index] += (color.R - _red[index]) * alpha;
                _green[index] += (color.G - _green[index]) * alpha;
                _blue[index] += (color.B - _blue[index]) * alpha;
                _area[index] = 0;
                _steps[index] = 0;
            }
        }
    }

    private void AddEdge(PointD from, PointD to, int left, int right, int top, int bottom,
        double clipLeft, double clipTop, double clipRight, double clipBottom)
    {
        if (from.Y == to.Y) return;
        var sign = from.Y < to.Y ? 1 : -1;
        var low = sign > 0 ? from : to;
        var high = sign > 0 ? to : from;
        var startY = Math.Max(Math.Max(low.Y, top), clipTop);
        var endY = Math.Min(Math.Min(high.Y, bottom), clipBottom);
        if (startY >= endY) return;
        var slope = (high.X - low.X) / (high.Y - low.Y);
        for (var y = (int)Math.Floor(startY); y < Math.Ceiling(endY); y++)
        {
            var y0 = Math.Max(y, startY);
            var y1 = Math.Min(y + 1, endY);
            var x0 = low.X + (y0 - low.Y) * slope;
            var x1 = low.X + (y1 - low.Y) * slope;
            var xLow = Math.Min(x0, x1);
            var xHigh = Math.Max(x0, x1);
            var span = xHigh - xLow;
            var signedHeight = (y1 - y0) * sign;
            // Clip before converting to integers (GDS coordinates may be far off-screen).
            var first = (int)Math.Clamp(Math.Floor(xLow), left, right);
            var full = (int)Math.Clamp(Math.Ceiling(xHigh), left, right);
            var row = y * _width;
            for (var x = first; x < full; x++)
            {
                var pixelLeft = Math.Max(x, clipLeft);
                var pixelRight = Math.Min(x + 1, clipRight);
                double average;
                if (span == 0)
                    average = Math.Clamp(pixelRight - xLow, 0, pixelRight - pixelLeft);
                else
                {
                    var a = Math.Clamp((pixelLeft - xLow) / span, 0, 1);
                    var b = Math.Clamp((pixelRight - xLow) / span, 0, 1);
                    average = a * (pixelRight - pixelLeft) +
                        (b - a) * (pixelRight - (xLow + span * (a + b) / 2));
                }
                _area[row + x] += signedHeight * average;
            }
            if (full < right) _steps[row + full] += signedHeight;
        }
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
                    output[x * 4] = (byte)Math.Clamp((int)Math.Round(_blue[index]), 0, 255);
                    output[x * 4 + 1] = (byte)Math.Clamp((int)Math.Round(_green[index]), 0, 255);
                    output[x * 4 + 2] = (byte)Math.Clamp((int)Math.Round(_red[index]), 0, 255);
                    output[x * 4 + 3] = 255;
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
        return bitmap;
    }
}
