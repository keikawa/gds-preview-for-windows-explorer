using System.Drawing;
using GdsPreview.Core;

namespace GdsPreview.Renderer;

internal static class HierarchicalBitmapRenderer
{
    // One material opacity for both PATH and BOUNDARY. Coverage, not a cosmetic
    // outline, controls the visibility of subpixel geometry.
    internal const int GeometryOpacity = 128;
    internal static Color PaletteColor(int layer, int dataType) => Renderer.LayerColor(layer, dataType);
    public static Bitmap Render(GdsDocument document, int width, int height)
    {
        var allTopCells = document.GetTopCells();
        var topCells = allTopCells
            .Where(cell => !cell.Name.Equals("$$$CONTEXT_INFO$$$", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (topCells.Count == 0) topCells = allTopCells.ToList();

        var renderer = new Renderer(document);
        return renderer.Render(topCells, width, height);
    }

    internal static BoundsD ResolveBounds(GdsDocument document, GdsCell cell)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(cell);
        var renderer = new Renderer(document);
        return renderer.ResolveBounds(cell, []);
    }

    // Fixed world-to-screen mapping for regression tests: auto-fit must not hide
    // differences between flattened and hierarchical representations.
    internal static Bitmap RenderGeometry(GdsDocument document, GdsCell cell,
        int width, int height, Transform2D transform, bool cacheGeometry = true, bool cullCells = true)
    {
        var renderer = new Renderer(document, cacheGeometry, cullCells);
        var surface = new CoverageRasterizer(width, height, Color.FromArgb(24, 27, 32));
        renderer.DrawDirect(surface, cell, transform, new RectangleF(0, 0, width, height));
        return surface.ToBitmap();
    }

    private sealed class Renderer
    {
        private const int MaximumHierarchyDepth = 512;

        private readonly GdsDocument _document;
        private readonly bool _cacheGeometry;
        private readonly bool _cullCells;
        private readonly Dictionary<string, BoundsD> _bounds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _expandedGeometry = new(StringComparer.Ordinal);
        private readonly HashSet<string> _renderStack = new(StringComparer.Ordinal);
        private readonly Dictionary<(int Layer, int DataType), Color> _colors = [];
        private readonly Dictionary<int, PointD[]> _pointBuffers = [];
        private readonly Dictionary<GdsPath, PointD[]> _pathOutlines = new(ReferenceEqualityComparer.Instance);
        private long _bufferedPoints;
        private long _outlinePoints;

        public Renderer(GdsDocument document, bool cacheGeometry = true, bool cullCells = true)
        {
            _document = document;
            _cacheGeometry = cacheGeometry;
            _cullCells = cullCells;
        }

        public Bitmap Render(IReadOnlyList<GdsCell> topCells, int width, int height)
        {
            if (topCells.Count == 1) return RenderSingle(topCells[0], width, height);
            return RenderOverview(topCells, width, height);
        }

        private Bitmap RenderSingle(GdsCell topCell, int width, int height)
        {
            var surface = new CoverageRasterizer(width, height, Color.FromArgb(24, 27, 32));

            const float margin = 18f;
            const float statusHeight = 34f;
            var viewport = new RectangleF(margin, margin,
                Math.Max(1, width - margin * 2),
                Math.Max(1, height - margin * 2 - statusHeight));
            var bounds = DrawCell(surface, topCell, viewport);
            if (bounds.IsEmpty) bounds = new BoundsD(-1, -1, 1, 1);

            var count = EstimateExpandedGeometry(topCell, []);
            var physicalWidth = FormatLength(bounds.Width * _document.MetersPerDatabaseUnit);
            var physicalHeight = FormatLength(bounds.Height * _document.MetersPerDatabaseUnit);
            var status = $"Cell: {topCell.Name}    {count:N0} elements    {physicalWidth} × {physicalHeight}";
            if (_document.WasSimplified) status += "    simplified";
            var bitmap = surface.ToBitmap();
            using var graphics = Graphics.FromImage(bitmap);
            DrawStatus(graphics, status, width, height);
            return bitmap;
        }

        private Bitmap RenderOverview(IReadOnlyList<GdsCell> topCells, int width, int height)
        {
            var surface = new CoverageRasterizer(width, height, Color.FromArgb(24, 27, 32));

            const float margin = 18f;
            const float statusHeight = 34f;
            const float gap = 8f;
            const float labelHeight = 20f;
            var content = new RectangleF(margin, margin,
                Math.Max(1, width - margin * 2),
                Math.Max(1, height - margin * 2 - statusHeight));
            var columns = Math.Max(1, (int)Math.Ceiling(
                Math.Sqrt(topCells.Count * content.Width / content.Height)));
            var rows = (int)Math.Ceiling(topCells.Count / (double)columns);
            var panelWidth = Math.Max(1, (content.Width - gap * (columns - 1)) / columns);
            var panelHeight = Math.Max(1, (content.Height - gap * (rows - 1)) / rows);
            var panels = new List<RectangleF>();
            long totalGeometry = 0;
            for (var index = 0; index < topCells.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var panel = new RectangleF(content.X + column * (panelWidth + gap),
                    content.Y + row * (panelHeight + gap), panelWidth, panelHeight);
                panels.Add(panel);
                surface.FillBackground(panel, Color.FromArgb(31, 35, 42));
                var viewport = new RectangleF(panel.X + 5, panel.Y + labelHeight,
                    Math.Max(1, panel.Width - 10), Math.Max(1, panel.Height - labelHeight - 5));
                DrawCell(surface, topCells[index], viewport);
                totalGeometry = SaturatingAdd(totalGeometry, EstimateExpandedGeometry(topCells[index], []));
            }

            var status = $"{topCells.Count:N0} top-level cells    {totalGeometry:N0} elements";
            if (_document.WasSimplified) status += "    simplified";
            var bitmap = surface.ToBitmap();
            using var graphics = Graphics.FromImage(bitmap);
            using var labelFont = new Font("Segoe UI", 8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(220, 228, 238));
            using var borderPen = new Pen(Color.FromArgb(68, 76, 88));
            using var labelFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            for (var index = 0; index < panels.Count; index++)
            {
                var panel = panels[index];
                graphics.DrawRectangle(borderPen, panel.X, panel.Y, panel.Width, panel.Height);
                graphics.DrawString(topCells[index].Name, labelFont, labelBrush,
                    new RectangleF(panel.X + 5, panel.Y + 2, Math.Max(1, panel.Width - 10), labelHeight),
                    labelFormat);
            }
            DrawStatus(graphics, status, width, height);
            return bitmap;
        }

        private BoundsD DrawCell(CoverageRasterizer surface, GdsCell cell, RectangleF viewport)
        {
            var bounds = ResolveBounds(cell, []);
            if (bounds.IsEmpty) return bounds;
            var dataWidth = Math.Max(bounds.Width, 1e-12);
            var dataHeight = Math.Max(bounds.Height, 1e-12);
            var scale = Math.Min(viewport.Width / dataWidth, viewport.Height / dataHeight);
            if (!double.IsFinite(scale) || scale <= 0) scale = 1;
            var offsetX = viewport.X + (viewport.Width - dataWidth * scale) / 2;
            var offsetY = viewport.Y + (viewport.Height - dataHeight * scale) / 2;
            var transform = new Transform2D(scale, 0, 0, -scale,
                offsetX - bounds.MinX * scale, offsetY + bounds.MaxY * scale);
            DrawDirect(surface, cell, transform, viewport);
            return bounds;
        }

        public void DrawDirect(CoverageRasterizer surface, GdsCell cell, Transform2D transform,
            RectangleF viewport)
        {
            if (!_renderStack.Add(cell.Name)) return;
            try
            {
                if (_renderStack.Count > MaximumHierarchyDepth)
                    throw new GdsFormatException("The cell hierarchy exceeds the rendering depth limit.");
                var bounds = TransformBounds(ResolveBounds(cell, []), transform);
                if (_cullCells && (bounds.IsEmpty || bounds.MaxX < viewport.Left || bounds.MinX > viewport.Right ||
                    bounds.MaxY < viewport.Top || bounds.MinY > viewport.Bottom)) return;

                foreach (var element in cell.Elements)
                {
                    switch (element)
                    {
                        case GdsPolygon polygon when polygon.Points.Count >= 3:
                            surface.FillPolygon(MapPoints(polygon.Points, transform),
                                GetColor(polygon.Layer, polygon.DataType), viewport);
                            break;
                        case GdsPath path when path.Points.Count >= 2:
                            surface.FillPolygon(MapPoints(GetPathOutline(path), transform),
                                GetColor(path.Layer, path.DataType), viewport);
                            break;
                        case GdsReference reference:
                            if (!_document.Cells.TryGetValue(reference.CellName, out var target)) break;
                            foreach (var origin in ReferenceOrigins(reference))
                            {
                                var child = Transform2D.ForReference(origin, reference.Magnification,
                                    reference.AngleDegrees, reference.ReflectXAxis);
                                DrawDirect(surface, target, transform.Combine(child), viewport);
                            }
                            break;
                    }
                }
            }
            finally { _renderStack.Remove(cell.Name); }
        }

        private Color GetColor(int layer, int dataType)
        {
            var key = (layer, dataType);
            if (_colors.TryGetValue(key, out var color)) return color;
            color = Color.FromArgb(GeometryOpacity, LayerColor(layer, dataType));
            if (_colors.Count < 4096) _colors.Add(key, color);
            return color;
        }

        private PointD[] GetPathOutline(GdsPath path)
        {
            if (_pathOutlines.TryGetValue(path, out var points)) return points;
            points = PathOutline.Create(path);
            // Cache geometry only, never a transformed or rasterized instance.
            if (_cacheGeometry && _outlinePoints + points.Length <= 8_000_000)
            {
                _pathOutlines.Add(path, points);
                _outlinePoints += points.Length;
            }
            return points;
        }

        public BoundsD ResolveBounds(GdsCell cell, HashSet<string> stack)
        {
            if (_bounds.TryGetValue(cell.Name, out var known)) return known;
            if (!stack.Add(cell.Name)) return BoundsD.Empty;
            if (stack.Count > MaximumHierarchyDepth)
                throw new GdsFormatException("The cell hierarchy exceeds the rendering depth limit.");
            var bounds = cell.LocalGeometryBounds;
            // Include miter joins and extended caps as well as parser bounds.
            foreach (var path in cell.Elements.OfType<GdsPath>())
                foreach (var point in GetPathOutline(path)) bounds = bounds.Include(point);
            foreach (var reference in cell.Elements.OfType<GdsReference>())
            {
                if (!_document.Cells.TryGetValue(reference.CellName, out var target)) continue;
                var childBounds = ResolveBounds(target, stack);
                if (childBounds.IsEmpty) continue;
                foreach (var origin in ReferenceExtentOrigins(reference))
                {
                    var transform = Transform2D.ForReference(origin, reference.Magnification,
                        reference.AngleDegrees, reference.ReflectXAxis);
                    bounds = bounds.Include(TransformBounds(childBounds, transform));
                }
            }
            stack.Remove(cell.Name);
            _bounds[cell.Name] = bounds;
            return bounds;
        }

        private long EstimateExpandedGeometry(GdsCell cell, HashSet<string> stack)
        {
            if (_expandedGeometry.TryGetValue(cell.Name, out var known)) return known;
            if (!stack.Add(cell.Name)) return 0;
            long count = cell.Elements.Count(element => element is GdsPolygon or GdsPath);
            foreach (var reference in cell.Elements.OfType<GdsReference>())
            {
                if (!_document.Cells.TryGetValue(reference.CellName, out var target)) continue;
                var multiplier = Math.Max(1L, (long)reference.Columns * reference.Rows);
                count = SaturatingAdd(count, SaturatingMultiply(
                    EstimateExpandedGeometry(target, stack), multiplier));
            }
            stack.Remove(cell.Name);
            _expandedGeometry[cell.Name] = count;
            return count;
        }

        private static IEnumerable<PointD> ReferenceOrigins(GdsReference reference)
        {
            if (!reference.IsArray)
            {
                yield return reference.Origin;
                yield break;
            }
            var columnPoint = reference.ColumnPoint ?? reference.Origin;
            var rowPoint = reference.RowPoint ?? reference.Origin;
            var columnStep = (columnPoint - reference.Origin) / reference.Columns;
            var rowStep = (rowPoint - reference.Origin) / reference.Rows;
            for (var row = 0; row < reference.Rows; row++)
            for (var column = 0; column < reference.Columns; column++)
                yield return reference.Origin + new PointD(
                    columnStep.X * column + rowStep.X * row,
                    columnStep.Y * column + rowStep.Y * row);
        }

        private static IEnumerable<PointD> ReferenceExtentOrigins(GdsReference reference)
        {
            if (!reference.IsArray)
            {
                yield return reference.Origin;
                yield break;
            }
            var columnPoint = reference.ColumnPoint ?? reference.Origin;
            var rowPoint = reference.RowPoint ?? reference.Origin;
            var columnStep = (columnPoint - reference.Origin) / reference.Columns;
            var rowStep = (rowPoint - reference.Origin) / reference.Rows;
            var columns = new[] { 0, reference.Columns - 1 }.Distinct();
            var rows = new[] { 0, reference.Rows - 1 }.Distinct();
            foreach (var row in rows)
            foreach (var column in columns)
                yield return reference.Origin + new PointD(
                    columnStep.X * column + rowStep.X * row,
                    columnStep.Y * column + rowStep.Y * row);
        }

        private static BoundsD TransformBounds(BoundsD bounds, Transform2D transform)
        {
            var result = BoundsD.Empty;
            result = result.Include(transform.Apply(new PointD(bounds.MinX, bounds.MinY)));
            result = result.Include(transform.Apply(new PointD(bounds.MaxX, bounds.MinY)));
            result = result.Include(transform.Apply(new PointD(bounds.MaxX, bounds.MaxY)));
            return result.Include(transform.Apply(new PointD(bounds.MinX, bounds.MaxY)));
        }

        private PointD[] MapPoints(IReadOnlyList<PointD> points, Transform2D transform)
        {
            if (!_pointBuffers.TryGetValue(points.Count, out var result))
            {
                result = new PointD[points.Count];
                if (_cacheGeometry && _bufferedPoints + points.Count <= 1_000_000 && _pointBuffers.Count < 256)
                {
                    _pointBuffers.Add(points.Count, result);
                    _bufferedPoints += points.Count;
                }
            }
            for (var index = 0; index < points.Count; index++)
                result[index] = transform.Apply(points[index]);
            return result;
        }

        private static void DrawStatus(Graphics graphics, string status, int width, int height)
        {
            using var font = new Font("Segoe UI", 9f);
            var measured = graphics.MeasureString(status, font);
            var rectangle = new RectangleF(10, height - 28, Math.Min(width - 20, measured.Width + 16), 22);
            using var background = new SolidBrush(Color.FromArgb(180, 0, 0, 0));
            using var brush = new SolidBrush(Color.FromArgb(225, 230, 238));
            graphics.FillRectangle(background, rectangle);
            graphics.DrawString(status, font, brush, rectangle.X + 8, rectangle.Y + 3);
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

        public static Color LayerColor(int layer, int dataType)
        {
            var hash = unchecked((uint)(layer * 0x45D9F3B) ^ (uint)(dataType * 0x119DE1F3));
            var hue = hash % 360;
            // Preserve the stable layer/datatype hue mapping, but lift dark
            // reds/blues for a dark canvas. Opacity is independent of coverage.
            var chroma = 0.95 * 0.42;
            var x = chroma * (1 - Math.Abs(hue / 60.0 % 2 - 1));
            var m = 0.95 - chroma;
            (double r, double g, double b) = hue switch
            {
                < 60 => (chroma, x, 0d), < 120 => (x, chroma, 0d), < 180 => (0d, chroma, x),
                < 240 => (0d, x, chroma), < 300 => (x, 0d, chroma), _ => (chroma, 0d, x)
            };
            return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        private static long SaturatingMultiply(long left, long right) =>
            left == 0 || right == 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

    }
}
