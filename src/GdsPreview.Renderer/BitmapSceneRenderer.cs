using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GdsPreview.Core;

namespace GdsPreview.Renderer;

internal static class BitmapSceneRenderer
{
    public static Bitmap Render(GdsScene scene, int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.FromArgb(24, 27, 32));
        DrawScene(graphics, scene, width, height);
        return bitmap;
    }

    private static void DrawScene(Graphics graphics, GdsScene scene, int clientWidth, int clientHeight)
    {
        const float margin = 18f;
        const float statusHeight = 34f;
        var content = new RectangleF(margin, margin,
            Math.Max(1, clientWidth - margin * 2),
            Math.Max(1, clientHeight - margin * 2 - statusHeight));

        if (scene.Views.Count > 1)
            DrawOverview(graphics, scene.Views, content);
        else
            DrawPrimitives(graphics, scene.Primitives, scene.Bounds, content);

        var status = scene.Views.Count > 1
            ? $"{scene.Views.Count:N0} top-level cells    {scene.Primitives.Count:N0} elements"
            : SingleCellStatus(scene);
        if (scene.WasTruncated) status += "    simplified";
        using var statusFont = new Font("Segoe UI", 9f);
        var measured = graphics.MeasureString(status, statusFont);
        var rectangle = new RectangleF(10, clientHeight - 28, Math.Min(clientWidth - 20, measured.Width + 16), 22);
        using var statusBackground = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
        using var statusBrush = new SolidBrush(Color.FromArgb(225, 230, 238));
        graphics.FillRectangle(statusBackground, rectangle);
        graphics.DrawString(status, statusFont, statusBrush, rectangle.X + 8, rectangle.Y + 3);
    }

    private static void DrawOverview(Graphics graphics, IReadOnlyList<GdsSceneView> views, RectangleF content)
    {
        const float gap = 8f;
        const float labelHeight = 20f;
        var columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(views.Count * content.Width / content.Height)));
        var rows = (int)Math.Ceiling(views.Count / (double)columns);
        var panelWidth = Math.Max(1, (content.Width - gap * (columns - 1)) / columns);
        var panelHeight = Math.Max(1, (content.Height - gap * (rows - 1)) / rows);
        using var labelFont = new Font("Segoe UI", 8f);
        using var labelBrush = new SolidBrush(Color.FromArgb(220, 228, 238));
        using var panelBrush = new SolidBrush(Color.FromArgb(31, 35, 42));
        using var borderPen = new Pen(Color.FromArgb(68, 76, 88));
        using var labelFormat = new StringFormat
        {
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        for (var index = 0; index < views.Count; index++)
        {
            var column = index % columns;
            var row = index / columns;
            var panel = new RectangleF(content.X + column * (panelWidth + gap),
                content.Y + row * (panelHeight + gap), panelWidth, panelHeight);
            graphics.FillRectangle(panelBrush, panel);
            graphics.DrawRectangle(borderPen, panel.X, panel.Y, panel.Width, panel.Height);
            graphics.DrawString(views[index].CellName, labelFont, labelBrush,
                new RectangleF(panel.X + 5, panel.Y + 2, Math.Max(1, panel.Width - 10), labelHeight),
                labelFormat);
            var viewport = new RectangleF(panel.X + 5, panel.Y + labelHeight,
                Math.Max(1, panel.Width - 10), Math.Max(1, panel.Height - labelHeight - 5));
            DrawPrimitives(graphics, views[index].Primitives, views[index].Bounds, viewport);
        }
    }

    private static void DrawPrimitives(Graphics graphics, IReadOnlyList<ScenePrimitive> primitives,
        BoundsD bounds, RectangleF viewport)
    {
        var dataWidth = Math.Max(bounds.Width, 1e-12);
        var dataHeight = Math.Max(bounds.Height, 1e-12);
        var scale = Math.Min(viewport.Width / dataWidth, viewport.Height / dataHeight);
        if (!double.IsFinite(scale) || scale <= 0) scale = 1;
        var offsetX = viewport.X + (viewport.Width - dataWidth * scale) / 2;
        var offsetY = viewport.Y + (viewport.Height - dataHeight * scale) / 2;

        PointF Map(PointD point) => new(
            (float)(offsetX + (point.X - bounds.MinX) * scale),
            (float)(offsetY + (bounds.MaxY - point.Y) * scale));

        graphics.SmoothingMode = primitives.Count < 2_000 ? SmoothingMode.AntiAlias : SmoothingMode.None;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;
        foreach (var primitive in primitives)
        {
            var color = LayerColor(primitive.Layer, primitive.DataType);
            switch (primitive)
            {
                case ScenePolygon polygon when polygon.Points.Count >= 3:
                {
                    var points = polygon.Points.Select(Map).ToArray();
                    using var fill = new SolidBrush(Color.FromArgb(82, color));
                    graphics.FillPolygon(fill, points, FillMode.Alternate);
                    if (primitives.Count < 4_000)
                    {
                        using var pen = new Pen(Color.FromArgb(190, color), 1f);
                        graphics.DrawPolygon(pen, points);
                    }
                    break;
                }
                case ScenePath path when path.Points.Count >= 2:
                {
                    var points = path.Points.Select(Map).ToArray();
                    var penWidth = path.Width <= 0 ? 1f : (float)Math.Clamp(path.Width * scale, 1, 30);
                    using var pen = new Pen(Color.FromArgb(220, color), penWidth)
                    {
                        LineJoin = LineJoin.Miter,
                        StartCap = LineCap.Flat,
                        EndCap = LineCap.Flat
                    };
                    graphics.DrawLines(pen, points);
                    break;
                }
                case SceneText text when !string.IsNullOrEmpty(text.Value):
                {
                    using var brush = new SolidBrush(Color.FromArgb(215, color));
                    using var font = new Font("Segoe UI", Math.Clamp((float)(text.Size * scale * 0.15), 6f, 14f));
                    graphics.DrawString(text.Value, font, brush, Map(text.Origin));
                    break;
                }
            }
        }
    }

    private static string SingleCellStatus(GdsScene scene)
    {
        var physicalWidth = FormatLength(scene.Bounds.Width * scene.MetersPerDatabaseUnit);
        var physicalHeight = FormatLength(scene.Bounds.Height * scene.MetersPerDatabaseUnit);
        return $"Cell: {scene.CellName}    {scene.Primitives.Count:N0} elements    {physicalWidth} × {physicalHeight}";
    }

    private static string FormatLength(double meters)
    {
        var absolute = Math.Abs(meters);
        if (absolute >= 1) return $"{meters:0.###} m";
        if (absolute >= 1e-3) return $"{meters * 1e3:0.###} mm";
        if (absolute >= 1e-6) return $"{meters * 1e6:0.###} µm";
        if (absolute >= 1e-9) return $"{meters * 1e9:0.###} nm";
        return $"{meters:0.###e+0} m";
    }

    private static Color LayerColor(int layer, int dataType)
    {
        if (layer < 0) return Color.FromArgb(150, 160, 175);
        var hash = unchecked((uint)(layer * 0x45D9F3B) ^ (uint)(dataType * 0x119DE1F3));
        var hue = hash % 360;
        var chroma = 0.95 * 0.68;
        var x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
        var m = 0.95 - chroma;
        (double r, double g, double b) = hue switch
        {
            < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
