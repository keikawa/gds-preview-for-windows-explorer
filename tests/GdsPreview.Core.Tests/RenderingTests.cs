using System.Drawing;
using GdsPreview.Core;
using GdsPreview.Renderer;

namespace GdsPreview.Core.Tests;

internal static class RenderingTests
{
    public static void SubpixelCoverage()
    {
        foreach (var width in new[] { .01, .05, .1, .25, .5, 1, 2 })
        foreach (var phase in new[] { 0, .07, .25, .5, .91 })
        foreach (var vertical in new[] { false, true })
        {
            var points = Rectangle(10, 30 + phase, 110, 30 + phase + width);
            if (vertical) points = points.Select(p => new PointD(p.Y, p.X)).ToArray();
            var surface = new CoverageRasterizer(128, 128, Color.Black);
            surface.FillPolygon(points, Color.White, new RectangleF(0, 0, 128, 128));
            using var bitmap = surface.ToBitmap();
            double integrated = 0;
            for (var i = 25; i < 36; i++)
                integrated += (vertical ? bitmap.GetPixel(i, 60) : bitmap.GetPixel(60, i)).R / 255.0;
            Near(width, integrated, 2.0 / 255, $"width={width}, phase={phase}, vertical={vertical}");
        }
    }

    public static void PolygonCoverage()
    {
        // Independent reference: Sutherland-Hodgman pixel clipping + shoelace area.
        // Covers a concave contour, both windings, oblique edges, clipping, and far coordinates.
        PointD[] polygon = [new(10.13, 3.7), new(41.3, 17.9), new(27, 24.1),
            new(51.7, 37.8), new(24.1, 40.2), new(4.8, 27.2)];
        foreach (var reverse in new[] { false, true })
        foreach (var offset in new[] { new PointD(0, 0), new PointD(-26.1, -19.3) })
        {
            var points = polygon.Select(p => p + offset).ToArray();
            if (reverse) Array.Reverse(points);
            var surface = new CoverageRasterizer(64, 64, Color.Black);
            var clip = new RectangleF(.2f, .3f, 55.4f, 51.6f);
            surface.FillPolygon(points, Color.White, clip);
            using var bitmap = surface.ToBitmap();
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
            {
                var expected = PixelArea(points, Math.Max(x, clip.Left), Math.Max(y, clip.Top),
                    Math.Min(x + 1, clip.Right), Math.Min(y + 1, clip.Bottom));
                Near(expected, bitmap.GetPixel(x, y).R / 255.0, .51 / 255, $"pixel ({x}, {y})");
            }
        }
        var huge = new CoverageRasterizer(64, 64, Color.Black);
        huge.FillPolygon(Rectangle(-1e12, -1e12, 1e12, 1e12), Color.White, new RectangleF(0, 0, 64, 64));
        using var full = huge.ToBitmap();
        Near(255, full.GetPixel(32, 32).R, 0, "far-off-screen clipping");

        foreach (var width in new[] { .01, .05, .1, .5 })
        foreach (var angle in new[] { 1, 17, 45, 89 })
        {
            var transform = Transform2D.ForReference(new PointD(32.17, 32.31), 1, angle, false);
            var points = Rectangle(-20, -width / 2, 20, width / 2).Select(transform.Apply).ToArray();
            var surface = new CoverageRasterizer(64, 64, Color.Black);
            surface.FillPolygon(points, Color.White, new RectangleF(0, 0, 64, 64));
            using var bitmap = surface.ToBitmap();
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                Near(PixelArea(points, x, y, x + 1, y + 1), bitmap.GetPixel(x, y).R / 255.0,
                    .51 / 255, $"oblique width={width}, angle={angle}, pixel ({x}, {y})");
        }
    }

    public static void Holes()
    {
        PointD[] points = [new(10, 10), new(50, 10), new(50, 50), new(10, 50), new(10, 10),
            new(20, 20), new(20, 40), new(40, 40), new(40, 20), new(20, 20), new(10, 10)];
        var surface = new CoverageRasterizer(64, 64, Color.Black);
        surface.FillPolygon(points, Color.White, new RectangleF(0, 0, 64, 64));
        using var image = surface.ToBitmap();
        Near(0, image.GetPixel(30, 30).R, 0, "hole center");
        Near(255, image.GetPixel(15, 15).R, 0, "retraced bridge must not cut the fill");
        Near(255, image.GetPixel(45, 30).R, 0, "outside ring");
    }

    public static void Hierarchy()
    {
        // Long, close-spaced and bent narrow structures, including a curved outline.
        var curve = Enumerable.Range(0, 81).Select(i =>
        {
            var angle = i * Math.PI / 160;
            return new PointD(12 + 8 * Math.Cos(angle), 12 + 8 * Math.Sin(angle));
        }).ToArray();
        var leaf = Cell("LEAF", new GdsPolygon(1, 0, Rectangle(0, 0, 30, .1)),
            new GdsPolygon(2, 0, Rectangle(0, .6, 30, .7)),
            new GdsPath(3, 0, .13, 0, [new(2, 2), new(24, 2), new(26, 7)]),
            new GdsPath(4, 0, .09, 0, curve));
        var middle = Cell("MID", new GdsReference(leaf.Name, new PointD(1.7, -2.3), 1.2, 19, true));
        var top = Cell("TOP");
        foreach (var shift in new[] { new PointD(20.07, 30.13), new PointD(60.41, 65.79) })
            top.Elements.Add(new GdsReference(middle.Name, shift, .83, 27, true));
        var document = Document(leaf, middle, top);
        var flat = Cell("FLAT");
        Flatten(document, top, Transform2D.Identity, flat);
        document.AddCell(flat);
        foreach (var scale in new[] { .7, 1, 1.17 })
        foreach (var phase in new[] { 0, .13, .5, .91 })
        {
            var transform = new Transform2D(scale, 0, 0, scale, phase, phase);
            using var hierarchy = HierarchicalBitmapRenderer.RenderGeometry(document, top, 128, 96, transform);
            using var flattened = HierarchicalBitmapRenderer.RenderGeometry(document, flat, 128, 96, transform);
            EqualImages(hierarchy, flattened);
            using var uncached = HierarchicalBitmapRenderer.RenderGeometry(document, top, 128, 96, transform, false, false);
            EqualImages(hierarchy, uncached);
        }

        var distant = Cell("DISTANT", new GdsPolygon(1, 0, Rectangle(1_000_000_000, 1_000_000_000,
            1_000_100_000, 1_000_000_100)));
        var near = Cell("NEAR", new GdsPolygon(1, 0, Rectangle(0, 0, 100_000, 100)));
        var relocated = Cell("RELOCATED", new GdsReference(distant.Name,
            new PointD(-1_000_000_000, -1_000_000_000), 1, 0, false));
        var largeDocument = Document(distant, near, relocated);
        var projection = new Transform2D(.001, 0, 0, .001, 10.13, 30.27);
        using var localImage = HierarchicalBitmapRenderer.RenderGeometry(largeDocument, near, 128, 96, projection);
        using var distantImage = HierarchicalBitmapRenderer.RenderGeometry(largeDocument, relocated, 128, 96, projection);
        EqualImages(localImage, distantImage);
    }

    public static void Arrays()
    {
        var leaf = Cell("LEAF", new GdsPolygon(1, 0, Rectangle(0, 0, 18, .09)));
        var array = Cell("ARRAY", new GdsReference(leaf.Name, new PointD(10.2, 10.7), 1.3, 22, true,
            3, 4, new PointD(61.5, 11.9), new PointD(12.6, 63.1)));
        var singles = Cell("SINGLES");
        for (var row = 0; row < 4; row++)
        for (var col = 0; col < 3; col++)
            singles.Elements.Add(new GdsReference(leaf.Name,
                new PointD(10.2 + col * (61.5 - 10.2) / 3 + row * (12.6 - 10.2) / 4,
                    10.7 + col * (11.9 - 10.7) / 3 + row * (63.1 - 10.7) / 4), 1.3, 22, true));
        var document = Document(leaf, array, singles);
        using var a = HierarchicalBitmapRenderer.RenderGeometry(document, array, 128, 96, Transform2D.Identity);
        using var b = HierarchicalBitmapRenderer.RenderGeometry(document, singles, 128, 96, Transform2D.Identity);
        EqualImages(a, b);
    }

    public static void Paths()
    {
        foreach (var width in new[] { 0, .03, .1, .5, 2, 40 })
        {
            var path = Cell("PATH", new GdsPath(1, 0, width, 0, [new(10, 48), new(110, 48)]));
            var polygon = Cell("POLYGON", new GdsPolygon(1, 0, Rectangle(10, 48 - width / 2, 110, 48 + width / 2)));
            var document = Document(path, polygon);
            using var a = HierarchicalBitmapRenderer.RenderGeometry(document, path, 128, 96, Transform2D.Identity);
            using var b = HierarchicalBitmapRenderer.RenderGeometry(document, polygon, 128, 96, Transform2D.Identity);
            EqualImages(a, b);
        }
        foreach (var type in new[] { 0, 1, 2 })
        {
            var outline = PathOutline.Create(new GdsPath(1, 0, 4, type, [new(10, 20), new(40, 20)]));
            var area = Math.Abs(SignedArea(outline));
            var expected = 120 + (type == 1 ? Math.PI * 4 : type == 2 ? 16 : 0);
            Near(expected, area, .002, $"path cap {type}");
        }
    }

    public static void TinyGeometryAccumulates()
    {
        var surface = new CoverageRasterizer(16, 16, Color.Black);
        var tiny = Rectangle(5.1, 5.1, 5.11, 5.11);
        for (var i = 0; i < 1000; i++) surface.FillPolygon(tiny, Color.White, new RectangleF(0, 0, 16, 16));
        using var image = surface.ToBitmap();
        Near(255 * (1 - Math.Pow(1 - .0001, 1000)), image.GetPixel(5, 5).R, .51, "accumulated small area");
    }

    private static PointD[] Rectangle(double left, double top, double right, double bottom) =>
        [new(left, top), new(right, top), new(right, bottom), new(left, bottom)];

    private static GdsCell Cell(string name, params GdsElement[] elements)
    {
        var cell = new GdsCell(name);
        foreach (var element in elements) Add(cell, element);
        return cell;
    }

    private static void Add(GdsCell cell, GdsElement element)
    {
        cell.Elements.Add(element);
        var points = element switch { GdsPolygon p => p.Points, GdsPath p => p.Points, _ => Array.Empty<PointD>() };
        var bounds = BoundsD.Empty;
        foreach (var point in points) bounds = bounds.Include(point);
        if (element is GdsPath path) bounds = bounds.Inflate(path.Width / 2);
        cell.LocalGeometryBounds = cell.LocalGeometryBounds.Include(bounds);
    }

    private static GdsDocument Document(params GdsCell[] cells)
    {
        var document = new GdsDocument();
        foreach (var cell in cells) document.AddCell(cell);
        return document;
    }

    private static void Flatten(GdsDocument document, GdsCell source, Transform2D transform, GdsCell destination)
    {
        foreach (var element in source.Elements)
        {
            switch (element)
            {
                case GdsPolygon p: Add(destination, p with { Points = p.Points.Select(transform.Apply).ToArray() }); break;
                case GdsPath p: Add(destination, p with { Points = p.Points.Select(transform.Apply).ToArray(), Width = p.Width * transform.ScaleEstimate }); break;
                case GdsReference r:
                    Flatten(document, document.Cells[r.CellName], transform.Combine(
                        Transform2D.ForReference(r.Origin, r.Magnification, r.AngleDegrees, r.ReflectXAxis)), destination);
                    break;
            }
        }
    }

    private static void EqualImages(Bitmap a, Bitmap b)
    {
        for (var y = 0; y < a.Height; y++)
        for (var x = 0; x < a.Width; x++)
            if (a.GetPixel(x, y) != b.GetPixel(x, y))
                throw new InvalidOperationException($"Images differ at ({x}, {y}): {a.GetPixel(x, y)} vs {b.GetPixel(x, y)}.");
    }

    private static double PixelArea(IReadOnlyList<PointD> input, double left, double top, double right, double bottom)
    {
        if (left >= right || top >= bottom) return 0;
        var p = Clip(input, v => v.X - left);
        p = Clip(p, v => right - v.X);
        p = Clip(p, v => v.Y - top);
        p = Clip(p, v => bottom - v.Y);
        return Math.Abs(SignedArea(p));
    }

    private static List<PointD> Clip(IReadOnlyList<PointD> input, Func<PointD, double> distance)
    {
        var result = new List<PointD>();
        if (input.Count == 0) return result;
        var previous = input[^1];
        var previousDistance = distance(previous);
        foreach (var current in input)
        {
            var currentDistance = distance(current);
            if ((previousDistance < 0) != (currentDistance < 0))
            {
                var t = previousDistance / (previousDistance - currentDistance);
                result.Add(new PointD(previous.X + (current.X - previous.X) * t, previous.Y + (current.Y - previous.Y) * t));
            }
            if (currentDistance >= 0) result.Add(current);
            previous = current;
            previousDistance = currentDistance;
        }
        return result;
    }

    private static double SignedArea(IReadOnlyList<PointD> points)
    {
        if (points.Count == 0) return 0;
        double area = 0;
        var previous = points[^1];
        foreach (var point in points)
        {
            area += previous.X * point.Y - previous.Y * point.X;
            previous = point;
        }
        return area / 2;
    }

    private static void Near(double expected, double actual, double tolerance, string context)
    {
        if (!double.IsFinite(actual) || Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"{context}: expected {expected:R}, got {actual:R}.");
    }
}
