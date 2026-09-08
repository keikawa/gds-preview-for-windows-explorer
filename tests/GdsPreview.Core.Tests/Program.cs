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
        ("single and multiple cells use a white canvas", WhiteCanvas),
        ("theme colours apply to canvas panels and labels", ThemeColors),
        ("preview text is larger and stays inside its labels", ReadablePreviewText),
        ("resolves rotation reflection and arrays", ResolvesRotationReflectionAndArrays),
        ("retains hierarchy when the top cell follows fifty thousand references", RetainsLateTopHierarchy),
        ("rejects reference overflow instead of corrupting hierarchy", RejectsReferenceOverflow),
        ("rejects truncated data", RejectsTruncatedData),
        ("accepts padding after ENDLIB", AcceptsPaddingAfterEndLib),
        ("preserves every vertex in a large polygon", PreservesEveryVertexInLargePolygon),
        ("bounds memory for large flat layout", BoundsMemoryForLargeFlatLayout),
        ("shares the geometry budget across cells", SharesGeometryBudgetAcrossCells),
        ("subpixel coverage follows physical width and phase", RenderingTests.SubpixelCoverage),
        ("polygon coverage matches independent pixel clipping", RenderingTests.PolygonCoverage),
        ("hole bridges preserve empty interiors", RenderingTests.Holes),
        ("hierarchy and flattening have identical coverage", RenderingTests.Hierarchy),
        ("arrays have identical coverage to individual references", RenderingTests.Arrays),
        ("path widths and caps are geometric", RenderingTests.Paths),
        ("sub-quantization geometry accumulates", RenderingTests.TinyGeometryAccumulates),
        ("linear-light colors and translucent layers", RenderingTests.LinearLightColors),
        ("constant-time sRGB lookup preserves the exact quantizer", RenderingTests.ExactSrgbLookup),
        ("layout outlines stay inside true coverage", LayoutStyleTests.InwardOutline),
        ("styled thin lines boost contrast without extending geometry", LayoutStyleTests.ThinCoverage),
        ("outline contrast is bounded and applied after aggregation only", LayoutStyleTests.OutlineContrast),
        ("enclosing fills cannot erase later or earlier outlines", LayoutStyleTests.EnclosingFills),
        ("hole bridges do not create diagonal outlines", LayoutStyleTests.HoleBridge),
        ("clipping and reset do not create phantom outlines", LayoutStyleTests.ClippingAndReset),
        ("real outlines remain visible at viewport edges", LayoutStyleTests.RealEdgesAtViewport),
        ("half-pixel outlines preserve width across pixel phases", LayoutStyleTests.OutlineWidthAndPhase),
        ("styled sub-quantization areas accumulate", LayoutStyleTests.TinyCoverageAccumulates),
        ("streamed rows match independent clipping after reuse", LayoutStyleTests.StreamingRows),
        ("rasterizer allocation excludes full-canvas scratch", LayoutStyleTests.BoundedAllocation),
        ("fixed 256-color palette and RGB identity", LayerPaletteTests.FixedPalette),
        ("stable layer/datatype mapping without short cycles", LayerPaletteTests.StableMapping),
        ("palette retains its original dark-background selection contrast", LayerPaletteTests.CanvasContrast),
        ("light palette darkens uniformly without changing mapping", LayerPaletteTests.LightPalette),
        ("light palette composites correctly on white", LayerPaletteTests.WhiteCanvas),
        ("layer colors do not depend on document or traversal order", LayerPaletteTests.DocumentIndependence)
    ];

    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--performance-review")
        {
            PerformanceReview.Run(args[1]);
            return 0;
        }
        if (args.Length == 2 && args[0] == "--color-review")
        {
            ColorReview.Export(args[1]);
            return 0;
        }
        if ((args.Length == 3 || args.Length == 4) && args[0] == "--verify-preview")
        {
            using var actual = new Bitmap(args[2]);
            var (background, text) = PreviewColors(args.Length == 4 ? args[3] : "light");
            using var expected = HierarchicalBitmapRenderer.Render(GdsParser.ParseFile(args[1]), actual.Width, actual.Height,
                background, text);
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

    private static (Color Background, Color Text) PreviewColors(string mode)
    {
        if (mode == "system")
        {
            if (System.Windows.Forms.SystemInformation.HighContrast)
                return (SystemColors.Window, SystemColors.WindowText);
            mode = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0 ? "dark" : "light";
        }
        return mode switch
        {
            "dark" => (Color.FromArgb(24, 27, 32), Color.FromArgb(220, 228, 238)),
            "high-contrast" => (Color.Black, Color.Yellow),
            "light" => (Color.White, Color.FromArgb(40, 40, 40)),
            _ => throw new ArgumentException("Unknown verification theme.")
        };
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
        True(CountContentPixels(bitmap) > 1_000,
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
        True(CountContentPixels(bitmap, new Rectangle(23, 50, 288, 290)) > 50,
            "The overview did not draw the first design cell.");
        True(CountContentPixels(bitmap, new Rectangle(329, 50, 288, 290)) > 50,
            "The overview did not draw the second design cell.");
    }

    private static void WhiteCanvas()
    {
        using var single = HierarchicalBitmapRenderer.Render(ParseDemo(), 640, 400);
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteMultipleTopCells(stream);
        stream.Position = 0;
        using var multiple = HierarchicalBitmapRenderer.Render(GdsParser.Parse(stream), 640, 400);
        foreach (var image in new[] { single, multiple })
        {
            Equal(Color.White.ToArgb(), image.GetPixel(0, 0).ToArgb());
            Equal(Color.White.ToArgb(), image.GetPixel(4, 40).ToArgb());
            Equal(Color.White.ToArgb(), image.GetPixel(639, 399).ToArgb());
            True(CountContentPixels(image, new Rectangle(10, 372, 600, 22)) > 30,
                "Status text is not readable on white.");
        }
        using var blank = new Bitmap(64, 64);
        Equal(Color.White.ToArgb(), multiple.GetPixel(19, 40).ToArgb()); // Inside the cell panel, outside its geometry viewport.
        using (var graphics = Graphics.FromImage(blank)) graphics.Clear(Color.White);
        Equal(0, CountContentPixels(blank)); // White background must not pass geometry tests.
    }

    private static void ThemeColors()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteMultipleTopCells(stream);
        stream.Position = 0;
        var overview = GdsParser.Parse(stream);
        foreach (var (background, text) in new[] {
            (Color.White, Color.FromArgb(40, 40, 40)),
            (Color.FromArgb(24, 27, 32), Color.FromArgb(220, 228, 238)),
            (Color.Black, Color.Yellow) })
        foreach (var document in new[] { ParseDemo(), overview })
        {
            using var image = HierarchicalBitmapRenderer.Render(document, 640, 400, background, text);
            Equal(background.ToArgb(), image.GetPixel(0, 0).ToArgb());
            Equal(background.ToArgb(), image.GetPixel(4, 40).ToArgb());
            Equal(background.ToArgb(), image.GetPixel(639, 399).ToArgb());
            if (document == overview) Equal(background.ToArgb(), image.GetPixel(19, 40).ToArgb());
            var statusPixels = 0;
            for (var y = 372; y < 394; y++)
            for (var x = 10; x < 610; x++)
            {
                var pixel = image.GetPixel(x, y);
                if (Math.Abs(pixel.R - text.R) + Math.Abs(pixel.G - text.G) + Math.Abs(pixel.B - text.B) < 90)
                    statusPixels++;
            }
            True(statusPixels > 30, "Status text does not use the theme foreground colour.");
        }
    }

    private static void ReadablePreviewText()
    {
        var document = new GdsDocument();
        document.AddCell(new GdsCell(new string('H', 200)));
        using var single = HierarchicalBitmapRenderer.Render(document, 320, 400);
        document.AddCell(new GdsCell(new string('J', 200)));
        using var overview = HierarchicalBitmapRenderer.Render(document, 320, 400);
        static int InkHeight(Bitmap bitmap, Rectangle area)
        {
            var rows = 0;
            for (var y = area.Top; y < area.Bottom; y++)
            {
                for (var x = area.Left; x < area.Right; x++)
                    if (bitmap.GetPixel(x, y).R < 160) { rows++; break; }
            }
            return rows;
        }
        True(InkHeight(overview, new Rectangle(23, 20, 128, 24)) >= 11,
            "Cell labels reverted to the small font.");
        True(InkHeight(single, new Rectangle(18, 364, 284, 30)) >= 11,
            "Status text reverted to the small font.");
        Equal(0, CountContentPixels(overview, new Rectangle(158, 20, 4, 24)));
        Equal(0, CountContentPixels(single, new Rectangle(312, 364, 8, 36)));
        foreach (var size in new[] { new Size(1, 1), new Size(64, 48), new Size(160, 120) })
        {
            using var small = HierarchicalBitmapRenderer.Render(document, size.Width, size.Height);
            Equal(size, small.Size); // Ellipsis/clipping must work even in tiny panes.
        }
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
        True(CountContentPixels(bitmap) > 300,
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
        True(CountContentPixels(bitmap) > 100, "Padded input was not rendered.");
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
            MaximumStoredGeometryElements = 10_000
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
        // Integrate their signal instead (roughly 493 covered pixels before contrast boost).
        double signal = 0;
        static double Luminance(Color c) => .2126 * SrgbColorSpace.Decode(c.R) +
            .7152 * SrgbColorSpace.Decode(c.G) + .0722 * SrgbColorSpace.Decode(c.B);
        var backgroundLuminance = Luminance(Color.White);
        for (var y = 0; y < bitmap.Height - 52; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            signal += backgroundLuminance - Luminance(pixel);
        }
        // Weight the retained geometry by its actual base colour; an HSV-specific
        // fixed maximum channel (242) cannot measure signal for other palettes.
        var palette = LayerPalette.ForBackground(Color.White);
        var expectedSignal = cell.Elements.OfType<GdsPolygon>().Sum(p =>
            backgroundLuminance - Luminance(palette[LayerPalette.IndexFor(p.Layer, p.DataType)])) *
            Math.Pow(444.0 / 19_990 * 10, 2) * LayoutCompositor.OutlineOpacity / 255.0 * 1.5;
        // Each box covers < .05 pixel, so its contrast gain is close to 1.5 even when split across pixels.
        True(signal > expectedSignal * .75 && signal < expectedSignal * 1.05,
            $"Stored geometry has an unexpected integrated signal: {signal}.");
    }

    private static void SharesGeometryBudgetAcrossCells()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteMultipleTopCells(stream); // Eight utility shapes, then one in each design cell.
        foreach (var limit in new[] { 0, 8, 9, 10 })
        {
            stream.Position = 0;
            var document = GdsParser.Parse(stream, options: new GdsParserOptions
            {
                MaximumStoredGeometryElements = limit
            });
            Equal(limit, document.CellsInFileOrder.Sum(cell => cell.Elements.Count));
            Equal(10 - limit, document.CellsInFileOrder.Sum(cell => cell.SkippedElementCount));
            Equal(limit < 10, document.WasSimplified);
            Equal(limit >= 9 ? 1 : 0, document.Cells["DESIGN_A"].Elements.Count);
            Equal(limit >= 10 ? 1 : 0, document.Cells["DESIGN_B"].Elements.Count);
        }
    }

    private static int CountContentPixels(Bitmap bitmap)
        => CountContentPixels(bitmap, new Rectangle(0, 0, bitmap.Width, Math.Max(0, bitmap.Height - 52)));

    private static int CountContentPixels(Bitmap bitmap, Rectangle area)
    {
        var count = 0;
        var clipped = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), area);
        for (var y = clipped.Top; y < clipped.Bottom; y++)
        for (var x = clipped.Left; x < clipped.Right; x++)
        {
            var color = bitmap.GetPixel(x, y);
            if (Math.Min(color.R, Math.Min(color.G, color.B)) < 255) count++;
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
