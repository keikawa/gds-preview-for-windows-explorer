using System.Drawing;
using GdsPreview.Core;
using GdsPreview.Renderer;

namespace GdsPreview.Core.Tests;

internal static class LayoutStyleTests
{
    private static readonly RectangleF Canvas = new(0, 0, 64, 64);

    public static void InwardOutline()
    {
        var surface = NewSurface();
        surface.DrawLayoutPolygon(Rectangle(10, 10, 50, 50), Color.White, Canvas);
        using var image = surface.ToBitmap();
        Pixel(image, 10, 30, StyledWhite(1, .5), "half-pixel inward outline");
        Pixel(image, 11, 30, LayoutCompositor.FillOpacity / 255.0, "interior tint");
        Pixel(image, 30, 30, LayoutCompositor.FillOpacity / 255.0, "interior tint");
        Pixel(image, 9, 30, 0, "no outward stroke");
        Pixel(image, 50, 30, 0, "no outward stroke");
        using var again = surface.ToBitmap();
        EqualImages(image, again); // Export must not composite twice into the working buffers.
    }

    public static void ThinCoverage()
    {
        foreach (var width in new[] { .01, .05, .1, .25, .5, 1 })
        foreach (var phase in new[] { 0, .07, .25, .5, .91 })
        foreach (var vertical in new[] { false, true })
        {
            var points = Rectangle(10, 30 + phase, 50, 30 + phase + width);
            if (vertical) points = points.Select(p => new PointD(p.Y, p.X)).ToArray();
            var surface = NewSurface();
            surface.DrawLayoutPolygon(points, Color.White, Canvas);
            using var image = surface.ToBitmap();
            for (var i = 25; i < 36; i++)
            {
                var coverage = Math.Max(0, Math.Min(i + 1, 30 + phase + width) - Math.Max(i, 30 + phase));
                Pixel(image, vertical ? i : 40, vertical ? 40 : i,
                    coverage * LayoutCompositor.OutlineOpacity / 255.0,
                    $"physical width={width}, phase={phase}, vertical={vertical}");
            }
        }
        // Oblique features must not gain pixels outside their analytical support,
        // nor gain more light than a fully opaque true-coverage reference.
        foreach (var angle in new[] { 1, 17, 45, 89 })
        foreach (var clip in new[] { Canvas, new RectangleF(20.2f, 10.3f, 22.4f, 25.7f) })
        {
            var transform = Transform2D.ForReference(new PointD(32.17, 32.31), 1, angle, false);
            var points = Rectangle(-20, -.05, 20, .05).Select(transform.Apply).ToArray();
            var styled = NewSurface();
            var raw = new ReferenceRasterizer(64, 64, Color.Black);
            var thinReference = new ReferenceRasterizer(64, 64, Color.Black);
            styled.DrawLayoutPolygon(points, Color.White, clip);
            raw.FillPolygon(points, Color.White, clip);
            thinReference.FillPolygon(points, Color.FromArgb(LayoutCompositor.OutlineOpacity, Color.White), clip);
            using var actual = styled.ToBitmap();
            using var reference = raw.ToBitmap();
            using var expectedThin = thinReference.ToBitmap();
            EqualImages(actual, expectedThin, 1); // Narrow oblique geometry is not dimmed with the outline width.
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                Require(actual.GetPixel(x, y).R <= reference.GetPixel(x, y).R,
                    $"Expanded oblique feature at ({x}, {y}), angle={angle}.");
        }
    }

    public static void EnclosingFills()
    {
        Bitmap Draw(int count, bool enclosingFirst)
        {
            var surface = NewSurface();
            void Covers()
            {
                for (var i = 0; i < count; i++)
                    surface.DrawLayoutPolygon(Rectangle(-10, -10, 90, 90), Color.Lime, Canvas);
            }
            if (enclosingFirst) Covers();
            surface.DrawLayoutPolygon(Rectangle(8, 20.25, 56, 20.75), Color.Red, Canvas);
            surface.DrawLayoutPolygon(Rectangle(10, 30, 50, 55), Color.Red, Canvas);
            if (!enclosingFirst) Covers();
            return surface.ToBitmap();
        }
        using var once = Draw(1, false);
        foreach (var count in new[] { 1, 8, 64 })
        {
            using var later = Draw(count, false);
            using var earlier = Draw(count, true);
            EqualImages(later, earlier);
            // A covering interior contributes ONLY to fill, never to outline.
            // Both the small box outline and half-pixel line stay unchanged.
            Require(SrgbColorSpace.Decode(later.GetPixel(10, 40).R) >= .5 * LayoutCompositor.OutlineOpacity / 255.0 - .005,
                "Enclosing fills obscured the half-pixel outline contribution.");
            Require(later.GetPixel(30, 20) == once.GetPixel(30, 20), "Enclosing fills obscured a thin line.");
            Pixel(later, 5, 5, LayoutCompositor.FillOpacity / 255.0, "fill opacity cap", green: true);
        }
        // Also check differently coloured overlaps, not just equal-colour covers.
        Bitmap Overlap(bool reverse)
        {
            var surface = NewSurface();
            (PointD[] Points, Color Color)[] shapes =
            [
                (Rectangle(5.1, 7.3, 43.7, 48.2), Color.Cyan),
                (Rectangle(12.6, 16.7, 51.2, 53.4), Color.Magenta),
                (Rectangle(19.8, 23.1, 57.1, 59.2), Color.Yellow)
            ];
            if (reverse) Array.Reverse(shapes);
            foreach (var (points, color) in shapes) surface.DrawLayoutPolygon(points, color, Canvas);
            return surface.ToBitmap();
        }
        using var forward = Overlap(false);
        using var backward = Overlap(true);
        EqualImages(forward, backward, 1); // Float summation may differ below one output level.
    }

    public static void HoleBridge()
    {
        PointD[] points = [new(10, 10), new(50, 10), new(50, 50), new(10, 50), new(10, 10),
            new(20, 20), new(20, 40), new(40, 40), new(40, 20), new(20, 20), new(10, 10)];
        foreach (var reverse in new[] { false, true })
        {
            if (reverse) Array.Reverse(points);
            var surface = NewSurface();
            surface.DrawLayoutPolygon(points, Color.White, Canvas);
            using var image = surface.ToBitmap();
            Pixel(image, 30, 30, 0, "hole remains empty");
            for (var i = 12; i < 18; i++)
                Pixel(image, i, i, LayoutCompositor.FillOpacity / 255.0, "retraced bridge is not an outline");
            Pixel(image, 19, 30, StyledWhite(1, .5), "inward hole outline");
            Pixel(image, 20, 30, 0, "hole outline does not expand into hole");
        }
    }

    public static void ClippingAndReset()
    {
        foreach (var clip in new[] { Canvas, new RectangleF(5.25f, 7.5f, 43.5f, 45.25f) })
        {
            var surface = NewSurface();
            surface.DrawLayoutPolygon(Rectangle(-1e12, -1e12, 1e12, 1e12), Color.White, clip);
            using var image = surface.ToBitmap();
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var coverage = Math.Max(0, Math.Min(x + 1, clip.Right) - Math.Max(x, clip.Left)) *
                    Math.Max(0, Math.Min(y + 1, clip.Bottom) - Math.Max(y, clip.Top));
                Pixel(image, x, y, coverage * LayoutCompositor.FillOpacity / 255.0,
                    "viewport clipping must not make a false outline");
            }
            surface.FillBackground(Canvas, Color.Black);
            using var cleared = surface.ToBitmap();
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++) Pixel(cleared, x, y, 0, "background reset clears style accumulators");
        }
    }

    public static void TinyCoverageAccumulates()
    {
        var surface = NewSurface();
        for (var i = 0; i < 1000; i++)
            surface.DrawLayoutPolygon(Rectangle(5.1, 5.1, 5.11, 5.11), Color.White, Canvas);
        using var image = surface.ToBitmap();
        Pixel(image, 5, 5, .1 * LayoutCompositor.OutlineOpacity / 255.0, "sub-quantization areas accumulate before export");
        Pixel(image, 4, 5, 0, "tiny geometry cannot expand");
    }

    public static void RealEdgesAtViewport()
    {
        foreach (var clip in new[] { Canvas, new RectangleF(5.25f, 7.5f, 43.5f, 45.25f) })
        {
            var surface = NewSurface();
            surface.DrawLayoutPolygon(Rectangle(clip.Left, clip.Top, clip.Right, clip.Bottom), Color.White, clip);
            using var image = surface.ToBitmap();
            var x = (int)Math.Floor(clip.Left);
            var y = (int)Math.Floor(clip.Top);
            Pixel(image, x, 30, StyledWhite(x + 1 - clip.Left, Math.Min(.5, x + 1 - clip.Left)),
                "real left boundary aligned with viewport must remain visible");
            Pixel(image, 30, y, StyledWhite(y + 1 - clip.Top, Math.Min(.5, y + 1 - clip.Top)),
                "real top boundary aligned with viewport must remain visible");
            x = (int)Math.Ceiling(clip.Right) - 1;
            y = (int)Math.Ceiling(clip.Bottom) - 1;
            Pixel(image, x, 30, StyledWhite(clip.Right - x, Math.Min(.5, clip.Right - x)),
                "real right boundary aligned with viewport must remain visible");
            Pixel(image, 30, y, StyledWhite(clip.Bottom - y, Math.Min(.5, clip.Bottom - y)),
                "real bottom boundary aligned with viewport must remain visible");
        }
    }

    public static void OutlineWidthAndPhase()
    {
        // Independent axial reference: exact intersection of each pixel with
        // the two half-pixel inner bands, regardless of pixel alignment.
        foreach (var width in new[] { .1, .5, .75, 1, 1.2, 2, 5 })
        foreach (var phase in new[] { 0, .07, .25, .5, .91 })
        {
            var start = 20 + phase;
            var end = start + width;
            var surface = NewSurface();
            surface.DrawLayoutPolygon(Rectangle(10, start, 50, end), Color.White, Canvas);
            using var image = surface.ToBitmap();
            for (var y = 18; y < 29; y++)
            {
                double Area(double a, double b) => Math.Max(0, Math.Min(y + 1, b) - Math.Max(y, a));
                var coverage = Area(start, end);
                var outline = width <= 1 ? coverage : Area(start, start + .5) + Area(end - .5, end);
                Pixel(image, 30, y, StyledWhite(coverage, outline), $"half-pixel band, width={width}, phase={phase}");
            }
        }
    }

    public static void StreamingRows()
    {
        // Independent polygon/pixel clipping, including negative halo coordinates.
        // Reuse the surface across different shapes and fractional viewports to
        // catch stale ring rows, edge buckets and clipped scratch values.
        var random = new Random(7103);
        var surface = NewSurface();
        for (var trial = 0; trial < 48; trial++)
        {
            var clip = trial % 2 == 0 ? Canvas : new RectangleF(5.25f, 7.3f, 43.5f, 45.2f);
            var points = Enumerable.Range(0, 10).Select(i =>
            {
                var angle = i * Math.PI / 5;
                var radius = (i % 2 == 0 ? 35 : 10) * (.5 + random.NextDouble());
                return new PointD(32 + radius * Math.Cos(angle), 32 + radius * Math.Sin(angle));
            }).ToArray();
            if (trial % 3 == 0) Array.Reverse(points);
            surface.FillBackground(Canvas, Color.Black);
            surface.DrawLayoutPolygon(points, Color.White, clip);
            using var image = surface.ToBitmap();
            double Area(int x, int y) => RenderingTests.PixelArea(points, x, y, x + 1, y + 1);
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var covered = RenderingTests.PixelArea(points, Math.Max(x, clip.Left), Math.Max(y, clip.Top),
                    Math.Min(x + 1, clip.Right), Math.Min(y + 1, clip.Bottom));
                var full = Area(x, y);
                var h = Math.Min(full, Math.Max(0, .5 - Area(x - 1, y)) + Math.Max(0, .5 - Area(x + 1, y)));
                var v = Math.Min(full, Math.Max(0, .5 - Area(x, y - 1)) + Math.Max(0, .5 - Area(x, y + 1)));
                var band = full > 0 ? h + v * (1 - h / full) : 0;
                Pixel(image, x, y, StyledWhite(covered, Math.Clamp(band, 0, covered)), "streamed independent coverage");
            }
        }
    }

    public static void BoundedAllocation()
    {
        var points = Rectangle(0, 0, 2, 2);
        new CoverageRasterizer(8, 8, Color.Black).DrawLayoutPolygon(points, Color.White, new(0, 0, 8, 8));
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        var surface = new CoverageRasterizer(1600, 1200, Color.Black);
        for (var i = 0; i < 1000; i++) surface.DrawLayoutPolygon(points, Color.White, new(0, 0, 1600, 1200));
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        // 4-byte background + two 16-byte weighted colours, plus row/edge scratch.
        Require(allocated < 36L * 1600 * 1200 + 512 * 1024,
            $"Rasterization allocated {allocated} bytes: full-canvas scratch or per-polygon allocation regressed.");
    }

    private static double StyledWhite(double coverage, double outline)
    {
        var fillAlpha = (coverage - outline) * LayoutCompositor.FillOpacity / 255.0;
        var outlineAlpha = outline * LayoutCompositor.OutlineOpacity / 255.0;
        return fillAlpha + (1 - fillAlpha) * outlineAlpha;
    }

    private static CoverageRasterizer NewSurface() => new(64, 64, Color.Black);
    private static PointD[] Rectangle(double l, double t, double r, double b) =>
        [new(l, t), new(r, t), new(r, b), new(l, b)];
    private static void Pixel(Bitmap image, int x, int y, double linear, string message, bool green = false)
    {
        var encoded = (linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055) * 255;
        var actual = green ? image.GetPixel(x, y).G : image.GetPixel(x, y).R;
        Require(Math.Abs(actual - encoded) <= .51, $"{message} at ({x}, {y}): expected {encoded:F3}, actual {actual}.");
    }
    private static void EqualImages(Bitmap a, Bitmap b, int tolerance = 0)
    {
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
        {
            var c = a.GetPixel(x, y);
            var d = b.GetPixel(x, y);
            Require(Math.Abs(c.R - d.R) <= tolerance && Math.Abs(c.G - d.G) <= tolerance &&
                Math.Abs(c.B - d.B) <= tolerance, $"Different composite at ({x}, {y}).");
        }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
