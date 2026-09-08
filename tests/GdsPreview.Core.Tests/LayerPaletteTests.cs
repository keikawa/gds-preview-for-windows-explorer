using System.Drawing;
using GdsPreview.Core;
using GdsPreview.Renderer;

namespace GdsPreview.Core.Tests;

internal static class LayerPaletteTests
{
    public static void FixedPalette()
    {
        Require(LayerPalette.Count == 256, "The fixed palette must have 256 entries.");
        var colors = Enumerable.Range(0, LayerPalette.Count).Select(LayerPalette.At).ToArray();
        Require(colors.Select(c => c.ToArgb()).Distinct().Count() == 256, "Duplicate palette entries.");
        int[] seeds = [0x77AADD, 0xEE8866, 0xEEDD88, 0xFFAABB, 0x99DDFF,
            0x44BB99, 0xBBCC33, 0xAAAA00, 0xDDDDDD];
        for (var i = 0; i < seeds.Length; i++)
            Require((colors[i].ToArgb() & 0xffffff) == seeds[i], "The published Tol Light seeds changed.");
        foreach (var color in colors)
            Require(color.A == 255, "Base colours must not carry material opacity.");
        // Snapshot every RGB byte, not just selected keys. Palette order is part
        // of colour identity; intentional changes require an explicit review.
        uint fingerprint = 2166136261;
        foreach (var color in colors)
        foreach (var channel in new[] { color.R, color.G, color.B })
            fingerprint = unchecked((fingerprint ^ channel) * 16777619);
        Require(fingerprint == PaletteFingerprint, "The fixed RGB table changed.");
    }

    private const uint PaletteFingerprint = 0x063DA54E;

    public static void StableMapping()
    {
        (int Layer, int Type, int Rgb)[] anchors =
        [
            (0, 0, 0xE07868), (1, 0, 0xB8E8B8), (9, 0, 0xE87080), (17, 0, 0xD088A0),
            (2, 0, 0xA0D8E0), (1, 1, 0x98B058), (0, 1, 0x28C8FF),
            (256, 0, 0xC8B890), (0, 256, 0xA0D8E0), (65535, 65535, 0xD0A8FF),
            (-1, 0, 0xA0A8FF), (int.MinValue, int.MaxValue, 0x98C808)
        ];
        foreach (var (layer, type, rgb) in anchors.Reverse().Concat(anchors))
            Require((HierarchicalBitmapRenderer.PaletteColor(layer, type).ToArgb() & 0xffffff) == rgb,
                $"Mapping changed for ({layer}, {type}).");

        // Exercise the old periodic failures along each axis, including high
        // bits. Some collisions are expected with a finite palette, not cycles.
        foreach (var step in new[] { 1, 8, 256 })
        foreach (var byLayer in new[] { false, true })
        {
            var mapped = Enumerable.Range(0, 1024).Select(i => byLayer
                ? LayerPalette.For(i * step, 0).ToArgb()
                : LayerPalette.For(0, i * step).ToArgb()).ToArray();
            Require(mapped.Distinct().Count() >= 230, $"Poor palette spread (step {step}).");
            Require(mapped.Zip(mapped.Skip(1)).Count(p => p.First == p.Second) < 16,
                "Excessive adjacent-key collisions.");
        }
        Require(new[] { 1, 9, 17 }.Select(i => LayerPalette.For(i, 0)).Distinct().Count() == 3,
            "The previous eight-layer cycle returned.");
    }

    public static void CanvasContrast()
    {
        foreach (var background in new[] { Color.FromArgb(24, 27, 32), Color.FromArgb(31, 35, 42) })
        foreach (var index in Enumerable.Range(0, LayerPalette.Count))
        {
            var surface = new CoverageRasterizer(4, 4, background);
            surface.DrawLayoutPolygon(Rectangle(1, 1, 3, 3), LayerPalette.At(index),
                new RectangleF(0, 0, 4, 4));
            using var image = surface.ToBitmap();
            var ratio = (Luminance(image.GetPixel(1, 1)) + .05) / (Luminance(background) + .05);
            Require(ratio >= 3, $"Palette entry {index} has rendered contrast {ratio:F4}:1.");
            Require(image.GetPixel(0, 0).ToArgb() == background.ToArgb(), "Colour extended geometry.");
        }
    }

    public static void LightPalette()
    {
        var light = LayerPalette.ForBackground(Color.White);
        var dark = LayerPalette.ForBackground(Color.FromArgb(24, 27, 32));
        Require(ReferenceEquals(light, LayerPalette.ForBackground(Color.FromArgb(240, 240, 240))),
            "Light backgrounds must reuse the same palette without allocating.");
        Require(light.Select(c => c.ToArgb()).Distinct().Count() == 256, "Light palette entries collided.");
        for (var i = 0; i < 256; i++)
        {
            Require(dark[i] == LayerPalette.At(i), "Dark palette changed.");
            Require(Math.Abs(Luminance(light[i]) - Luminance(dark[i]) * .70) < .004,
                "Light palette must uniformly scale linear light, not shift individual hues.");
            foreach (var (source, actual) in new[] { (dark[i].R, light[i].R), (dark[i].G, light[i].G), (dark[i].B, light[i].B) })
            {
                var linear = Decode(source) * .70;
                var encoded = (linear <= .0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - .055) * 255;
                Require(Math.Abs(actual - encoded) <= .51, "Non-uniform colour darkening.");
            }
        }
        foreach (var layer in new[] { 0, 1, 2, 9, 17, 256, 65535 })
            Require(dark[LayerPalette.IndexFor(layer, 0)] == LayerPalette.For(layer, 0),
                "Theme selection must not alter the layer-to-index mapping.");
    }

    public static void WhiteCanvas()
    {
        foreach (var index in Enumerable.Range(0, LayerPalette.Count))
        {
            var color = LayerPalette.ForBackground(Color.White)[index];
            var surface = new CoverageRasterizer(8, 8, Color.White);
            surface.DrawLayoutPolygon(Rectangle(1, 1, 7, 7), color, new(0, 0, 8, 8));
            using var image = surface.ToBitmap();
            Require(image.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "White background changed.");
            foreach (var (x, y, coverage, outline) in new[] { (1, 3, 1d, .5), (3, 3, 1d, 0d) })
            {
                var fillAlpha = (coverage - outline) * LayoutCompositor.FillOpacity / 255.0;
                var outlineAlpha = (outline + .5 * outline * (1 - outline)) * LayoutCompositor.OutlineOpacity / 255.0;
                var alpha = fillAlpha + (1 - fillAlpha) * outlineAlpha;
                var actual = image.GetPixel(x, y);
                foreach (var (source, channel) in new[] { (color.R, actual.R), (color.G, actual.G), (color.B, actual.B) })
                {
                    var expected = 1 + (Decode(source) - 1) * alpha;
                    var encoded = (expected <= .0031308 ? expected * 12.92 : 1.055 * Math.Pow(expected, 1 / 2.4) - .055) * 255;
                    Require(Math.Abs(channel - encoded) <= .51, $"White compositing changed palette entry {index}.");
                }
            }
        }
    }

    public static void DocumentIndependence()
    {
        // Synthetic documents only. Different names, cell order, extra types,
        // primitive order, hierarchy and exceeding the former cache limit must not recolour.
        var rectangle = Rectangle(10.25, 10.25, 40.75, 40.75);
        var single = Cell("FIRST", new GdsPolygon(1, 0, rectangle));
        var a = new GdsDocument();
        a.AddCell(single);
        using var expected = HierarchicalBitmapRenderer.RenderGeometry(a, single, 64, 64, Transform2D.Identity);
        foreach (var reverse in new[] { false, true })
        {
            var others = Enumerable.Range(0, 4100).Select(i => (GdsElement)
                new GdsPolygon(i, 7, Rectangle(70, 70, 80, 80))).ToList();
            others.Insert(reverse ? 0 : others.Count, new GdsPolygon(1, 0, rectangle));
            var leaf = Cell("RENAMED", others.ToArray());
            var top = Cell("SECOND", new GdsReference(leaf.Name, new PointD(0, 0), 1, 0, false));
            var b = new GdsDocument();
            if (reverse) { b.AddCell(top); b.AddCell(leaf); }
            else { b.AddCell(leaf); b.AddCell(top); }
            using var actual = HierarchicalBitmapRenderer.RenderGeometry(b, top, 64, 64, Transform2D.Identity);
            for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                Require(expected.GetPixel(x, y) == actual.GetPixel(x, y),
                    $"Document or element order changed the pixel at ({x}, {y}).");
        }
    }

    private static GdsCell Cell(string name, params GdsElement[] elements)
    {
        var cell = new GdsCell(name);
        foreach (var element in elements)
        {
            cell.Elements.Add(element);
            if (element is GdsPolygon polygon)
                foreach (var point in polygon.Points) cell.LocalGeometryBounds = cell.LocalGeometryBounds.Include(point);
        }
        return cell;
    }

    private static PointD[] Rectangle(double l, double t, double r, double b) =>
        [new(l, t), new(r, t), new(r, b), new(l, b)];
    private static double Decode(byte v) => v / 255.0 <= .04045
        ? v / 255.0 / 12.92 : Math.Pow((v / 255.0 + .055) / 1.055, 2.4);
    private static double Luminance(Color c) => .2126 * Decode(c.R) + .7152 * Decode(c.G) + .0722 * Decode(c.B);
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
