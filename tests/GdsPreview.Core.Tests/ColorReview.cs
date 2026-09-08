using System.Drawing;
using System.Drawing.Imaging;
using GdsPreview.Core;
using GdsPreview.Renderer;

namespace GdsPreview.Core.Tests;

// Optional, synthetic-only visual review. Never shipped or run by the renderer.
internal static class ColorReview
{
    public static void Export(string directory)
    {
        Directory.CreateDirectory(directory);
        using var before = Draw(false);
        using var after = Draw(true);
        using var comparison = new Bitmap(before.Width * 2, before.Height + 36);
        using (var g = Graphics.FromImage(comparison))
        using (var font = new Font("Segoe UI", 12))
        {
            g.Clear(Color.White);
            g.DrawString("White background: original colours", font, Brushes.Black, 10, 5);
            g.DrawString("White background: gently darkened colours", font, Brushes.Black, before.Width + 10, 5);
            g.DrawImageUnscaled(before, 0, 36);
            g.DrawImageUnscaled(after, before.Width, 36);
        }
        Save(comparison, directory, "color-comparison.png");
        // Machado et al. (2009), severity 100. Multiply LINEAR RGB, not sRGB
        // bytes, then clip out-of-gamut results and encode for display. These
        // diagnostic approximations do not certify colour-blind accessibility.
        (string Name, double[] Matrix)[] simulations =
        [
            ("protanopia", [.152286, 1.052583, -.204868, .114503, .786281, .099216, -.003882, -.048116, 1.051998]),
            ("deuteranopia", [.367322, .860646, -.227968, .280085, .672501, .047413, -.011820, .042940, .968881]),
            ("tritanopia", [1.255528, -.076749, -.178779, -.078411, .930809, .147602, .004733, .691367, .303900])
        ];
        foreach (var (name, matrix) in simulations)
        {
            using var image = Simulate(comparison, matrix);
            Save(image, directory, $"color-comparison-{name}.png");
        }
        using var palette = PaletteSheet();
        Save(palette, directory, "palette-256.png");
        using var enclosing = EnclosingComparison();
        Save(enclosing, directory, "enclosing-overlap-comparison.png");
    }

    private static Bitmap Draw(bool styled)
    {
        const int width = 720, height = 860;
        var surface = new ReferenceRasterizer(width, height, Color.White);
        var viewport = new RectangleF(0, 0, width, height);
        surface.FillBackground(new RectangleF(0, 420, width, 440), Color.White);
        void Fill(PointD[] points, int layer, int type = 0)
        {
            var color = styled ? LayerPalette.ForBackground(Color.White)[LayerPalette.IndexFor(layer, type)]
                : LayerPalette.For(layer, type);
            surface.DrawLayoutPolygon(points, color, viewport);
        }
        // All first 32 layer keys, not just the attractive prefix of the palette.
        for (var i = 0; i < 32; i++)
        {
            var x = 12 + i % 16 * 44;
            var y = 38 + i / 16 * 76;
            Fill(Rectangle(x, y, x + 40, y + 47), i);
        }
        // The formerly identical keys 1/9/17; datatype differences alongside.
        int[] layers = [1, 9, 17, 0, 1, 2, 3, 4];
        double[] widths = [.1, .25, .5, 1, 2];
        for (var i = 0; i < layers.Length; i++)
        {
            var x = 12 + i * 88;
            for (var j = 0; j < widths.Length; j++)
                Fill(Rectangle(x, 236.2 + j * 17, x + 78, 236.2 + j * 17 + widths[j]), layers[i]);
            var path = new GdsPath(layers[i], 0, .5, 0,
                [new(x, 374.2), new(x + 25, 340.7), new(x + 55, 371.8), new(x + 78, 341.2)]);
            Fill(PathOutline.Create(path), layers[i]);
        }
        for (var i = 0; i < 8; i++) Fill(Rectangle(12 + i * 88, 448, 94 + i * 88, 488), 1, i);
        for (var group = 0; group < 3; group++)
        for (var i = 0; i < group + 2; i++)
            Fill(Rectangle(12 + group * 236 + i * 29, 546 + i * 25,
                143 + group * 236 + i * 29, 648 + i * 25), layers[i]);
        // Larger adjacent areas, with a narrow different-layer feature on top.
        for (var i = 0; i < 8; i++)
        {
            var x = 12 + i * 88;
            Fill(Rectangle(x, 755, x + 88, 842), layers[i]);
            Fill(Rectangle(x, 790.25, x + 88, 790.75), layers[(i + 1) % layers.Length]);
        }
        var bitmap = surface.ToBitmap();
        using var graphics = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 10);
        void Label(string s, float x, float y) => graphics.DrawString(s, font, Brushes.Black, x, y);
        Label("Layers 0-31 / datatype 0", 12, 10);
        for (var i = 0; i < 32; i++) Label(i.ToString(), 12 + i % 16 * 44, 86 + i / 16 * 76);
        Label("Physical widths 0.1 / 0.25 / 0.5 / 1 / 2 px; oblique 0.5 px", 12, 193);
        for (var i = 0; i < layers.Length; i++) Label($"L{layers[i]}", 12 + i * 88, 212);
        Label("Layer 1 / datatypes 0-7", 12, 421);
        for (var i = 0; i < 8; i++) Label($"D{i}", 12 + i * 88, 490);
        Label("Two / three / four overlaps (identical order in both images)", 12, 520);
        Label("Large adjacent regions + 0.5 px overlaid feature", 12, 726);
        return bitmap;
    }

    private static Bitmap PaletteSheet()
    {
        var surface = new ReferenceRasterizer(960, 992, Color.White);
        var clip = new RectangleF(0, 0, 960, 992);
        for (var i = 0; i < 256; i++)
        {
            var x = i % 16 * 60;
            var y = 32 + i / 16 * 60;
            surface.DrawLayoutPolygon(Rectangle(x + 2, y + 2, x + 58, y + 37),
                LayerPalette.ForBackground(Color.White)[i], clip);
        }
        var bitmap = surface.ToBitmap();
        using var g = Graphics.FromImage(bitmap);
        using var font = new Font("Segoe UI", 9);
        g.DrawString("All 256 palette entries, bounded fill + inward outline on #FFFFFF (not layer order)", font, Brushes.Black, 5, 6);
        for (var i = 0; i < 256; i++)
            g.DrawString(i.ToString(), font, Brushes.Black, i % 16 * 60 + 4, 32 + i / 16 * 60 + 38);
        return bitmap;
    }

    private static Bitmap Simulate(Bitmap source, double[] m)
    {
        var image = new Bitmap(source.Width, source.Height);
        for (var y = 0; y < source.Height; y++)
        for (var x = 0; x < source.Width; x++)
        {
            var c = source.GetPixel(x, y);
            var r = SrgbColorSpace.Decode(c.R);
            var g = SrgbColorSpace.Decode(c.G);
            var b = SrgbColorSpace.Decode(c.B);
            byte Channel(int row) => SrgbColorSpace.Encode((float)Math.Clamp(m[row] * r + m[row + 1] * g + m[row + 2] * b, 0, 1));
            image.SetPixel(x, y, Color.FromArgb(Channel(0), Channel(3), Channel(6)));
        }
        return image;
    }

    private static Bitmap EnclosingComparison()
    {
        var surface = new ReferenceRasterizer(960, 680, Color.White);
        for (var row = 0; row < 2; row++)
        for (var col = 0; col < 3; col++)
        {
            var x = 10 + col * 320;
            var y = 42 + row * 330;
            var clip = new RectangleF(x, y, 300, 290);
            void Fill(PointD[] points, int layer)
            {
                var color = LayerPalette.ForBackground(Color.White)[LayerPalette.IndexFor(layer, 0)];
                if (row == 1) surface.DrawLayoutPolygon(points, color, clip);
                else surface.FillPolygon(points, Color.FromArgb(128, color), clip);
            }
            for (var i = 0; i < 9; i++)
            {
                Fill(Rectangle(x + 32 + i * 27, y + 25, x + 33 + i * 27, y + 260), i);
                Fill(Rectangle(x + 25, y + 40 + i * 24.0, x + 275, y + 40.5 + i * 24.0), i);
            }
            Fill(Rectangle(x + 60, y + 60, x + 200, y + 175), 9);
            // Covers drawn LAST: precisely the failure that a palette change cannot fix.
            for (var i = 0; i < (col == 0 ? 1 : col == 1 ? 8 : 32); i++)
                Fill(Rectangle(x + 5, y + 5, x + 295, y + 285), 1);
        }
        var image = surface.ToBitmap();
        using var g = Graphics.FromImage(image);
        using var font = new Font("Segoe UI", 11);
        g.DrawString("Before: 1 / 8 / 32 enclosing fills drawn after internal structures", font, Brushes.Black, 10, 10);
        g.DrawString("After: same colours and geometry; fill cannot overwrite the separate outlines", font, Brushes.Black, 10, 340);
        return image;
    }

    private static PointD[] Rectangle(double l, double t, double r, double b) =>
        [new(l, t), new(r, t), new(r, b), new(l, b)];
    private static void Save(Bitmap image, string directory, string name)
    {
        var path = Path.Combine(directory, name);
        image.Save(path, ImageFormat.Png);
        Console.WriteLine($"Synthetic colour review: {Path.GetFullPath(path)}");
    }
}
