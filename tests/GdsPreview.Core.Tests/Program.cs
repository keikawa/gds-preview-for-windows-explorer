using System.Drawing;
using GdsPreview.Core;
using GdsPreview.Renderer;
using GdsPreview.Sample;

namespace GdsPreview.Core.Tests;

internal static class Program
{
    private static readonly List<(string Name, Action Test)> Tests =
    [
        ("parses demo library", ParsesDemoLibrary),
        ("ignores text for geometry and storage", IgnoresTextForGeometryAndStorage),
        ("far-away text cannot change rendered output", FarAwayTextCannotChangeRenderedOutput),
        ("renders the production hierarchy path", RendersProductionHierarchyPath),
        ("renders multiple design top cells", RendersMultipleDesignTopCells),
        ("resolves rotation reflection and arrays", ResolvesRotationReflectionAndArrays),
        ("retains hierarchy when the top cell follows fifty thousand references", RetainsLateTopHierarchy),
        ("rejects reference overflow instead of corrupting hierarchy", RejectsReferenceOverflow),
        ("rejects truncated data", RejectsTruncatedData),
        ("accepts padding after ENDLIB", AcceptsPaddingAfterEndLib),
        ("preserves every vertex in a large polygon", PreservesEveryVertexInLargePolygon),
        ("bounds memory for large flat layout", BoundsMemoryForLargeFlatLayout),
        ("subpixel coverage follows physical width and phase", RenderingTests.SubpixelCoverage),
        ("polygon coverage matches independent pixel clipping", RenderingTests.PolygonCoverage),
        ("hole bridges preserve empty interiors", RenderingTests.Holes),
        ("hierarchy and flattening have identical coverage", RenderingTests.Hierarchy),
        ("arrays have identical coverage to individual references", RenderingTests.Arrays),
        ("path widths and caps are geometric", RenderingTests.Paths),
        ("sub-quantization geometry accumulates", RenderingTests.TinyGeometryAccumulates)
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--verify-preview")
        {
            using var actual = new Bitmap(args[2]);
            using var expected = HierarchicalBitmapRenderer.Render(GdsParser.ParseFile(args[1]), actual.Width, actual.Height);
            // Native captures do not have meaningful alpha. Compare layout RGB,
            // excluding the status font (whose DPI can differ between test hosts).
            for (var y = 0; y < actual.Height - 52; y++)
            for (var x = 0; x < actual.Width; x++)
                if ((actual.GetPixel(x, y).ToArgb() & 0xffffff) != (expected.GetPixel(x, y).ToArgb() & 0xffffff))
                {
                    Console.Error.WriteLine($"Native preview differs from final-grid rendering at ({x}, {y}).");
                    return 1;
                }
            Console.WriteLine($"PASS  native final-grid pixels ({actual.Width} x {actual.Height})");
            return 0;
        }
        var failures = 0;
        foreach (var (name, test) in Tests)
        {
            try
            {
                test();
                Console.WriteLine($"PASS  {name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL  {name}: {exception.Message}");
            }
        }

        Console.WriteLine($"{Tests.Count - failures}/{Tests.Count} tests passed");
        return failures == 0 ? 0 : 1;
    }

    private static void ParsesDemoLibrary()
    {
        var document = ParseDemo();
        Equal("GDS_PREVIEW_DEMO", document.LibraryName);
        Equal(2, document.Cells.Count);
        NearlyEqual(1e-9, document.MetersPerDatabaseUnit, 1e-18);
        Equal(2, document.Cells["LEAF"].Elements.Count);
        Equal(3, document.Cells["LEAF"].SourceElementCount);
        Equal(3, document.Cells["TOP"].Elements.Count);
        Equal("TOP", document.GetTopCells().Single().Name);
    }

    private static void IgnoresTextForGeometryAndStorage()
    {
        var document = ParseFarAwayText(true);
        var cell = document.Cells["TOP"];
        Equal(2, cell.SourceElementCount);
        Equal(1, cell.Elements.Count);
        True(cell.Elements.Single() is GdsPolygon, "Only drawable geometry should be retained.");
        Equal(new BoundsD(0, 0, 100, 100), cell.LocalGeometryBounds);
        True(!document.WasSimplified, "Unsupported text is intentionally ignored, not simplified geometry.");
    }

    private static void FarAwayTextCannotChangeRenderedOutput()
    {
        using var withoutText = HierarchicalBitmapRenderer.Render(ParseFarAwayText(false), 480, 320);
        using var withText = HierarchicalBitmapRenderer.Render(ParseFarAwayText(true), 480, 320);
        Equal(withoutText.Size, withText.Size);
        for (var y = 0; y < withoutText.Height; y++)
        for (var x = 0; x < withoutText.Width; x++)
        {
            if (withoutText.GetPixel(x, y) != withText.GetPixel(x, y))
                throw new InvalidOperationException($"TEXT changed output at ({x}, {y}).");
        }
    }

    private static void RendersProductionHierarchyPath()
    {
        using var bitmap = HierarchicalBitmapRenderer.Render(ParseDemo(), 640, 480);
        Equal(new Size(640, 480), bitmap.Size);
        True(CountBrightContentPixels(bitmap) > 1_000,
            "The production renderer did not draw the referenced layout.");
    }

    private static void RendersMultipleDesignTopCells()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteMultipleTopCells(stream);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        Equal(3, document.GetTopCells().Count);

        using var bitmap = HierarchicalBitmapRenderer.Render(document, 640, 400);
        True(CountBrightPixels(bitmap, new Rectangle(23, 50, 288, 290)) > 50,
            "The overview did not draw the first design cell.");
        True(CountBrightPixels(bitmap, new Rectangle(329, 50, 288, 290)) > 50,
            "The overview did not draw the second design cell.");
    }

    private static void ResolvesRotationReflectionAndArrays()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteReferenceTransforms(stream);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);

        Equal(new BoundsD(920, 2000, 1000, 2200),
            HierarchicalBitmapRenderer.ResolveBounds(document, document.Cells["ROTATED"]));
        Equal(new BoundsD(-1000, -2000, -920, -1800),
            HierarchicalBitmapRenderer.ResolveBounds(document, document.Cells["REFLECTED"]));
        Equal(new BoundsD(100, 200, 400, 340),
            HierarchicalBitmapRenderer.ResolveBounds(document, document.Cells["ARRAY"]));

        using var bitmap = HierarchicalBitmapRenderer.Render(document, 720, 420);
        True(CountBrightContentPixels(bitmap) > 300,
            "Transformed references were not drawn by the production renderer.");
    }

    private static void RejectsTruncatedData()
    {
        using var stream = new MemoryStream([0x00, 0x06, 0x00, 0x02, 0x02]);
        Throws<GdsFormatException>(() => GdsParser.Parse(stream));
    }

    private static void RetainsLateTopHierarchy()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteLateTopAfterManyReferences(stream, 50_001);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        Equal("TOP", document.GetTopCells().Single().Name);
        Equal(1, document.Cells["TOP"].Elements.Count);
        True(document.Cells["TOP"].Elements.Single() is GdsReference,
            "The late top-cell reference was not retained.");
        True(!document.WasSimplified, "Complete hierarchy must not be reported as simplified.");
    }

    private static void RejectsReferenceOverflow()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteLateTopAfterManyReferences(stream, 2);
        stream.Position = 0;
        Throws<GdsFormatException>(() => GdsParser.Parse(stream, options: new GdsParserOptions
        {
            MaximumStoredReferences = 2
        }));
    }

    private static void AcceptsPaddingAfterEndLib()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.Write(stream);
        stream.Write(new byte[512]);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        Equal(2, document.Cells.Count);
        using var bitmap = HierarchicalBitmapRenderer.Render(document, 320, 240);
        True(CountBrightContentPixels(bitmap) > 100, "Padded input was not rendered.");
    }

    private static void PreservesEveryVertexInLargePolygon()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteHighVertexPolygon(stream);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        var polygon = (GdsPolygon)document.Cells["TOP"].Elements.Single();
        Equal(2_200, polygon.Points.Count);
        Equal(new PointD(1_023, 1), polygon.Points[1_023]);
        Equal(new PointD(1_024, 0), polygon.Points[1_024]);
        Equal(new PointD(0, 100), polygon.Points[^1]);
    }

    private static void BoundsMemoryForLargeFlatLayout()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteLargeFlat(stream, 10_001);
        stream.Position = 0;
        var document = GdsParser.Parse(stream, options: new GdsParserOptions
        {
            MaximumStoredGeometryElements = 10_000,
            MaximumStoredGeometryElementsPerCell = 10_000
        });
        var cell = document.Cells["TOP"];
        Equal(10_001, cell.SourceElementCount);
        Equal(10_000, cell.Elements.Count);
        Equal(1, cell.SkippedElementCount);
        True(document.WasSimplified, "Large geometry should be marked as simplified.");
        True(cell.LocalGeometryBounds.Width > 19_000,
            "Skipped geometry must remain represented in the fitted bounds.");

        using var bitmap = HierarchicalBitmapRenderer.Render(document, 480, 320);
        // These 10x10 DBU boxes are only ~0.222 pixels wide at this fit scale.
        // A bright-pixel threshold tested the old one-pixel outlines, not the data.
        // Integrate their signal instead (roughly 493 covered pixels).
        long signal = 0;
        for (var y = 0; y < bitmap.Height - 52; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            signal += Math.Max(pixel.R - 24, Math.Max(pixel.G - 27, pixel.B - 32));
        }
        var expectedSignal = 10_000 * Math.Pow(444.0 / 19_990 * 10, 2) *
            HierarchicalBitmapRenderer.GeometryOpacity / 255.0 * (242 - 27);
        True(signal > expectedSignal * .75 && signal < expectedSignal * 1.05,
            $"Stored geometry has an unexpected integrated signal: {signal}.");
    }

    private static int CountBrightContentPixels(Bitmap bitmap)
        => CountBrightPixels(bitmap, new Rectangle(0, 0, bitmap.Width, Math.Max(0, bitmap.Height - 52)));

    private static int CountBrightPixels(Bitmap bitmap, Rectangle area)
    {
        var count = 0;
        var clipped = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), area);
        for (var y = clipped.Top; y < clipped.Bottom; y++)
        for (var x = clipped.Left; x < clipped.Right; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (Math.Max(color.R, Math.Max(color.G, color.B)) >= 90) count++;
        }
        return count;
    }

    private static GdsDocument ParseDemo()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.Write(stream);
        stream.Position = 0;
        return GdsParser.Parse(stream);
    }

    private static GdsDocument ParseFarAwayText(bool includeText)
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteFarAwayText(stream, includeText);
        stream.Position = 0;
        return GdsParser.Parse(stream);
    }

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}, got {actual}.");
    }

    private static void NearlyEqual(double expected, double actual, double tolerance)
    {
        if (Math.Abs(expected - actual) > tolerance)
            throw new InvalidOperationException($"Expected {expected:R}, got {actual:R}.");
    }

    private static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<TException>(Action action) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
    }
}
