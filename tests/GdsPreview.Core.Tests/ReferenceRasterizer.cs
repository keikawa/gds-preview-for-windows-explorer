using System.Drawing;
using System.Numerics;
using GdsPreview.Core;
using GdsPreview.Renderer;

namespace GdsPreview.Core.Tests;

// Numerical source-over reference, not a shipping rendering mode. Geometry still
// comes from the production coverage integrator. Visual reviews use raw/style
// drawing in separate regions; this helper does not layer style over raw pixels.
internal sealed class ReferenceRasterizer
{
    private readonly CoverageRasterizer _coverage;
    private readonly Vector3[] _pixels;
    private readonly bool[] _raw;
    private readonly int _width, _height;
    private readonly Action<int, double> _paint;
    private Vector3 _source;
    private float _opacity;

    public ReferenceRasterizer(int width, int height, Color background)
    {
        _width = width;
        _height = height;
        _coverage = new(width, height, background);
        _pixels = new Vector3[width * height];
        Array.Fill(_pixels, SrgbColorSpace.DecodeRgb(background.ToArgb()));
        _raw = new bool[_pixels.Length];
        _paint = (index, area) =>
        {
            _pixels[index] += (_source - _pixels[index]) * (float)(area * _opacity);
            _raw[index] = true;
        };
    }

    public void FillPolygon(ReadOnlySpan<PointD> points, Color color, RectangleF viewport)
    {
        _source = SrgbColorSpace.DecodeRgb(color.ToArgb());
        _opacity = color.A / 255f;
        _coverage.VisitCoverage(points, viewport, _paint);
    }
    public void DrawLayoutPolygon(ReadOnlySpan<PointD> points, Color color, RectangleF viewport) =>
        _coverage.DrawLayoutPolygon(points, color, viewport);
    public void FillBackground(RectangleF rectangle, Color color)
    {
        _coverage.FillBackground(rectangle, color);
        for (var y = Math.Max(0, (int)Math.Floor(rectangle.Top)); y < Math.Min(_height, rectangle.Bottom); y++)
        for (var x = Math.Max(0, (int)Math.Floor(rectangle.Left)); x < Math.Min(_width, rectangle.Right); x++)
        {
            _pixels[y * _width + x] = SrgbColorSpace.DecodeRgb(color.ToArgb());
            _raw[y * _width + x] = false;
        }
    }
    public Bitmap ToBitmap()
    {
        var image = _coverage.ToBitmap();
        for (var y = 0; y < _height; y++)
        for (var x = 0; x < _width; x++)
        {
            var index = y * _width + x;
            if (!_raw[index]) continue;
            var c = _pixels[index];
            image.SetPixel(x, y, Color.FromArgb(SrgbColorSpace.Encode(c.X), SrgbColorSpace.Encode(c.Y), SrgbColorSpace.Encode(c.Z)));
        }
        return image;
    }
}
