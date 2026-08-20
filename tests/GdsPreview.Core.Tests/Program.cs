using GdsPreview.Core;
using GdsPreview.Sample;

namespace GdsPreview.Core.Tests;

internal static class Program
{
    private static readonly List<(string Name, Action Test)> Tests =
    [
        ("parses demo library", ParsesDemoLibrary),
        ("selects and expands top cell", SelectsAndExpandsTopCell),
        ("builds overview for multiple top cells", BuildsOverviewForMultipleTopCells),
        ("applies reference transform", AppliesReferenceTransform),
        ("rejects truncated data", RejectsTruncatedData),
        ("accepts padding after ENDLIB", AcceptsPaddingAfterEndLib),
        ("preserves every vertex in a large polygon", PreservesEveryVertexInLargePolygon),
        ("honors primitive safety limit", HonorsPrimitiveSafetyLimit),
        ("bounds memory for large flat layout", BoundsMemoryForLargeFlatLayout)
    ];

    private static int Main()
    {
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
        Equal(3, document.Cells["LEAF"].Elements.Count);
        Equal(3, document.Cells["TOP"].Elements.Count);
        Equal("TOP", document.GetTopCells().Single().Name);
    }

    private static void SelectsAndExpandsTopCell()
    {
        var scene = SceneBuilder.Build(ParseDemo());
        Equal("TOP", scene.CellName);
        Equal(40, scene.Primitives.Count);
        True(!scene.Bounds.IsEmpty, "Scene bounds should not be empty.");
        True(scene.Bounds.Width > 7_000, "Expanded references should contribute to width.");
        True(!scene.WasTruncated, "Demo scene should not be truncated.");
    }

    private static void AppliesReferenceTransform()
    {
        var transform = Transform2D.ForReference(new PointD(10, 20), 2, 90, false);
        var point = transform.Apply(new PointD(3, 0));
        NearlyEqual(10, point.X, 1e-9);
        NearlyEqual(26, point.Y, 1e-9);

        var reflected = Transform2D.ForReference(new PointD(0, 0), 1, 0, true);
        Equal(new PointD(2, -4), reflected.Apply(new PointD(2, 4)));
    }

    private static void BuildsOverviewForMultipleTopCells()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.WriteMultipleTopCells(stream);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        Equal(3, document.GetTopCells().Count);

        var overview = SceneBuilder.Build(document);
        Equal("2 top-level cells", overview.CellName);
        Equal(2, overview.Views.Count);
        Equal("DESIGN_A", overview.Views[0].CellName);
        Equal("DESIGN_B", overview.Views[1].CellName);
        Equal(2, overview.Primitives.Count);

        var explicitMetadata = SceneBuilder.Build(document, "$$$CONTEXT_INFO$$$");
        Equal("$$$CONTEXT_INFO$$$", explicitMetadata.CellName);
        Equal(8, explicitMetadata.Primitives.Count);
        Equal(1, explicitMetadata.Views.Count);
    }

    private static void RejectsTruncatedData()
    {
        using var stream = new MemoryStream([0x00, 0x06, 0x00, 0x02, 0x02]);
        Throws<GdsFormatException>(() => GdsParser.Parse(stream));
    }

    private static void AcceptsPaddingAfterEndLib()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.Write(stream);
        stream.Write(new byte[512]);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        Equal(2, document.Cells.Count);
        Equal("TOP", SceneBuilder.Build(document).CellName);
    }

    private static void HonorsPrimitiveSafetyLimit()
    {
        var scene = SceneBuilder.Build(ParseDemo(), options: new SceneBuildOptions
        {
            MaximumPrimitives = 5,
            MaximumInstances = 100
        });
        Equal(5, scene.Primitives.Count);
        True(scene.WasTruncated, "A limited scene must report truncation.");
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
        DemoGdsWriter.WriteLargeFlat(stream, 100_001);
        stream.Position = 0;
        var document = GdsParser.Parse(stream);
        var cell = document.Cells["TOP"];
        Equal(100_001, cell.SourceElementCount);
        Equal(100_000, cell.Elements.Count);
        Equal(1, cell.SkippedElementCount);
        True(document.WasSimplified, "Large geometry should be marked as simplified.");

        var scene = SceneBuilder.Build(document);
        True(scene.Primitives.Count <= 100_000, "Scene primitive limit was exceeded.");
        True(scene.WasTruncated, "Simplified scene should report truncation.");
        True(scene.Bounds.Width > 19_000, "Skipped geometry bounds should cover the whole layout.");
    }

    private static GdsDocument ParseDemo()
    {
        using var stream = new MemoryStream();
        DemoGdsWriter.Write(stream);
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
