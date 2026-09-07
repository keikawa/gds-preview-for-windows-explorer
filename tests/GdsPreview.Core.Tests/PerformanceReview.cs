using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.Loader;
using GdsPreview.Core;
using GdsPreview.Renderer;

namespace GdsPreview.Core.Tests;

// Optional synthetic-only benchmark, never included in the installed renderer.
internal static class PerformanceReview
{
    public static void Run(string baselineDirectory)
    {
        var context = new AssemblyLoadContext("render-baseline", isCollectible: true);
        context.Resolving += (_, name) => name.Name == "GdsPreview.Core" ? typeof(GdsDocument).Assembly : null;
        var assembly = context.LoadFromAssemblyPath(Path.Combine(Path.GetFullPath(baselineDirectory), "GdsPreview.Renderer.dll"));
        var baseline = assembly.GetType("GdsPreview.Renderer.HierarchicalBitmapRenderer")!
            .GetMethod("Render", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<Func<GdsDocument, int, int, Bitmap>>();
        foreach (var (name, document) in Cases())
        {
            var old = Measure(baseline, document);
            var current = Measure(HierarchicalBitmapRenderer.Render, document);
            Console.WriteLine($"{name}: baseline {old.Ms:F1} ms / {old.MiB:F1} MiB allocated; current {current.Ms:F1} ms / {current.MiB:F1} MiB allocated");
        }
        context.Unload();
    }

    private static (double Ms, double MiB) Measure(Func<GdsDocument, int, int, Bitmap> render, GdsDocument document)
    {
        using (render(document, 64, 64)) { }
        var times = new double[3];
        var allocations = new double[3];
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            var allocated = GC.GetAllocatedBytesForCurrentThread();
            var start = Stopwatch.GetTimestamp();
            using var image = render(document, 1600, 1200);
            times[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            allocations[i] = (GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0;
        }
        Array.Sort(times);
        Array.Sort(allocations);
        return (times[1], allocations[1]);
    }

    private static IEnumerable<(string, GdsDocument)> Cases()
    {
        var enclosing = new GdsCell("TOP");
        for (var i = 0; i < 128; i++) Add(enclosing, i, 0, 0, 1600, 1200);
        yield return ("128 enclosing fills", Document(enclosing));
        var flat = new GdsCell("TOP");
        for (var i = 0; i < 100_000; i++)
            Add(flat, i % 32, i % 1000 * 20, i / 1000 * 20, 10, 10);
        yield return ("100,000 subpixel polygons", Document(flat));
        var leaf = new GdsCell("LEAF");
        Add(leaf, 1, 0, 0, 10, 10);
        var array = new GdsCell("TOP");
        array.Elements.Add(new GdsReference("LEAF", new(0, 0), 1, 0, false,
            1000, 100, new PointD(20_000, 0), new PointD(0, 2000)));
        yield return ("100,000 array instances", Document(leaf, array));
    }
    private static GdsDocument Document(params GdsCell[] cells)
    {
        var document = new GdsDocument();
        foreach (var cell in cells) document.AddCell(cell);
        return document;
    }
    private static void Add(GdsCell cell, int layer, double x, double y, double w, double h)
    {
        PointD[] points = [new(x, y), new(x + w, y), new(x + w, y + h), new(x, y + h)];
        cell.Elements.Add(new GdsPolygon(layer, 0, points));
        foreach (var p in points) cell.LocalGeometryBounds = cell.LocalGeometryBounds.Include(p);
    }
}
