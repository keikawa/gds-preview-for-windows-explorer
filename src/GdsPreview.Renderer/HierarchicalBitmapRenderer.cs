using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using GdsPreview.Core;

namespace GdsPreview.Renderer;

internal static class HierarchicalBitmapRenderer
{
    public static Bitmap Render(GdsDocument document, int width, int height)
    {
        var allTopCells = document.GetTopCells();
        var topCells = allTopCells
            .Where(cell => !cell.Name.Equals("$$$CONTEXT_INFO$$$", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (topCells.Count == 0) topCells = allTopCells.ToList();

        using var renderer = new Renderer(document);
        return renderer.Render(topCells, width, height);
    }

    private sealed class Renderer : IDisposable
    {
        private const long MaximumCachedPixels = 32_000_000;
        private const int MaximumRasterDimension = 4096;

        private readonly GdsDocument _document;
        private readonly Dictionary<string, BoundsD> _bounds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _expandedGeometry = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _referenceCounts = new(StringComparer.Ordinal);
        private readonly Dictionary<RasterKey, CellRaster> _rasters = [];
        private readonly HashSet<string> _renderStack = new(StringComparer.Ordinal);
        private long _cachedPixels;

        public Renderer(GdsDocument document)
        {
            _document = document;
            foreach (var cell in document.CellsInFileOrder)
            {
                foreach (var reference in cell.Elements.OfType<GdsReference>())
                {
                    var count = Math.Max(1L, (long)reference.Columns * reference.Rows);
                    _referenceCounts.TryGetValue(reference.CellName, out var existing);
                    _referenceCounts[reference.CellName] = SaturatingAdd(existing, count);
                }
            }
        }

        public Bitmap Render(IReadOnlyList<GdsCell> topCells, int width, int height)
        {
            if (topCells.Count == 1) return RenderSingle(topCells[0], width, height);
            return RenderOverview(topCells, width, height);
        }

        private Bitmap RenderSingle(GdsCell topCell, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(24, 27, 32));

            const float margin = 18f;
            const float statusHeight = 34f;
            var viewport = new RectangleF(margin, margin,
                Math.Max(1, width - margin * 2),
                Math.Max(1, height - margin * 2 - statusHeight));
            var bounds = DrawCell(graphics, topCell, viewport);
            if (bounds.IsEmpty) bounds = new BoundsD(-1, -1, 1, 1);

            var count = EstimateExpandedGeometry(topCell, []);
            var physicalWidth = FormatLength(bounds.Width * _document.MetersPerDatabaseUnit);
            var physicalHeight = FormatLength(bounds.Height * _document.MetersPerDatabaseUnit);
            var status = $"Cell: {topCell.Name}    {count:N0} elements    {physicalWidth} × {physicalHeight}";
            if (_document.WasSimplified) status += "    simplified";
            DrawStatus(graphics, status, width, height);
            return bitmap;
        }

        private Bitmap RenderOverview(IReadOnlyList<GdsCell> topCells, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.FromArgb(24, 27, 32));

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
            using var labelFont = new Font("Segoe UI", 8f);
            using var labelBrush = new SolidBrush(Color.FromArgb(220, 228, 238));
            using var panelBrush = new SolidBrush(Color.FromArgb(31, 35, 42));
            using var borderPen = new Pen(Color.FromArgb(68, 76, 88));
            using var labelFormat = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                FormatFlags = StringFormatFlags.NoWrap
            };

            long totalGeometry = 0;
            for (var index = 0; index < topCells.Count; index++)
            {
                var column = index % columns;
                var row = index / columns;
                var panel = new RectangleF(content.X + column * (panelWidth + gap),
                    content.Y + row * (panelHeight + gap), panelWidth, panelHeight);
                graphics.FillRectangle(panelBrush, panel);
                graphics.DrawRectangle(borderPen, panel.X, panel.Y, panel.Width, panel.Height);
                graphics.DrawString(topCells[index].Name, labelFont, labelBrush,
                    new RectangleF(panel.X + 5, panel.Y + 2, Math.Max(1, panel.Width - 10), labelHeight),
                    labelFormat);
                var viewport = new RectangleF(panel.X + 5, panel.Y + labelHeight,
                    Math.Max(1, panel.Width - 10), Math.Max(1, panel.Height - labelHeight - 5));
                DrawCell(graphics, topCells[index], viewport);
                totalGeometry = SaturatingAdd(totalGeometry, EstimateExpandedGeometry(topCells[index], []));
            }

            var status = $"{topCells.Count:N0} top-level cells    {totalGeometry:N0} elements";
            if (_document.WasSimplified) status += "    simplified";
            DrawStatus(graphics, status, width, height);
            return bitmap;
        }

        private BoundsD DrawCell(Graphics graphics, GdsCell cell, RectangleF viewport)
        {
            var bounds = ResolveBounds(cell, []);
            if (bounds.IsEmpty) return bounds;
            var dataWidth = Math.Max(bounds.Width, 1e-12);
            var dataHeight = Math.Max(bounds.Height, 1e-12);
            var scale = Math.Min(viewport.Width / dataWidth, viewport.Height / dataHeight);
            if (!double.IsFinite(scale) || scale <= 0) scale = 1;
            var raster = GetRaster(cell, scale);
            if (raster is null) return bounds;
            try
            {
                var offsetX = viewport.X + (viewport.Width - dataWidth * scale) / 2;
                var offsetY = viewport.Y + (viewport.Height - dataHeight * scale) / 2;
                var destination = new RectangleF(
                    (float)(offsetX + (raster.Bounds.MinX - bounds.MinX) * scale),
                    (float)(offsetY + (bounds.MaxY - raster.Bounds.MaxY) * scale),
                    (float)Math.Max(1, raster.Bounds.Width * scale),
                    (float)Math.Max(1, raster.Bounds.Height * scale));
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                graphics.DrawImage(raster.Bitmap, destination);
            }
            finally
            {
                if (!raster.IsCached) raster.Bitmap.Dispose();
            }
            return bounds;
        }

        private CellRaster? GetRaster(GdsCell cell, double requestedScale)
        {
            if (!_renderStack.Add(cell.Name)) return null;
            try
            {
                var cellBounds = ResolveBounds(cell, []);
                if (cellBounds.IsEmpty) return null;
                var safeScale = Math.Max(requestedScale, 1e-12);
                var rasterBounds = cellBounds.Inflate(1 / safeScale);
                var width = Math.Clamp((int)Math.Ceiling(rasterBounds.Width * safeScale), 1,
                    MaximumRasterDimension);
                var height = Math.Clamp((int)Math.Ceiling(rasterBounds.Height * safeScale), 1,
                    MaximumRasterDimension);
                var key = new RasterKey(cell.Name, width, height);
                if (_rasters.TryGetValue(key, out var known)) return known;

                var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using var graphics = Graphics.FromImage(bitmap);
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.Half;
                graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                var scaleX = width / Math.Max(rasterBounds.Width, 1e-12);
                var scaleY = height / Math.Max(rasterBounds.Height, 1e-12);
                PointF Map(PointD point) => new(
                    (float)((point.X - rasterBounds.MinX) * scaleX),
                    (float)((rasterBounds.MaxY - point.Y) * scaleY));

                var brushes = new Dictionary<(int Layer, int DataType), SolidBrush>();
                var pens = new Dictionary<(int Layer, int DataType), Pen>();
                try
                {
                    foreach (var element in cell.Elements)
                    {
                        switch (element)
                        {
                            case GdsPolygon polygon when polygon.Points.Count >= 3:
                            {
                                var points = MapPoints(polygon.Points, Map);
                                var keyColor = (polygon.Layer, polygon.DataType);
                                if (!brushes.TryGetValue(keyColor, out var brush))
                                {
                                    var color = LayerColor(polygon.Layer, polygon.DataType);
                                    brush = new SolidBrush(Color.FromArgb(82, color));
                                    brushes.Add(keyColor, brush);
                                    pens.Add(keyColor, new Pen(Color.FromArgb(210, color), 1f)
                                    {
                                        LineJoin = LineJoin.Miter
                                    });
                                }
                                graphics.FillPolygon(brush, points, FillMode.Alternate);
                                graphics.DrawPolygon(pens[keyColor], points);
                                break;
                            }
                            case GdsPath path when path.Points.Count >= 2:
                            {
                                var points = MapPoints(path.Points, Map);
                                var color = LayerColor(path.Layer, path.DataType);
                                var pathScale = Math.Sqrt(scaleX * scaleY);
                                var penWidth = path.Width <= 0 ? 1f :
                                    (float)Math.Clamp(path.Width * pathScale, 1, 30);
                                using var pen = new Pen(Color.FromArgb(220, color), penWidth)
                                {
                                    LineJoin = LineJoin.Miter,
                                    StartCap = LineCap.Flat,
                                    EndCap = LineCap.Flat
                                };
                                graphics.DrawLines(pen, points);
                                break;
                            }
                            case GdsReference reference:
                                DrawReference(graphics, reference, scaleX, scaleY, Map);
                                break;
                        }
                    }
                }
                finally
                {
                    foreach (var brush in brushes.Values) brush.Dispose();
                    foreach (var pen in pens.Values) pen.Dispose();
                }

                var pixelCount = (long)width * height;
                _referenceCounts.TryGetValue(cell.Name, out var references);
                var shouldCache = references > 1 && _cachedPixels <= MaximumCachedPixels - pixelCount;
                var result = new CellRaster(bitmap, rasterBounds, shouldCache);
                if (shouldCache)
                {
                    _rasters.Add(key, result);
                    _cachedPixels += pixelCount;
                }
                return result;
            }
            finally
            {
                _renderStack.Remove(cell.Name);
            }
        }

        private void DrawReference(Graphics graphics, GdsReference reference, double parentScaleX,
            double parentScaleY, Func<PointD, PointF> map)
        {
            if (!_document.Cells.TryGetValue(reference.CellName, out var target)) return;
            var childScale = Math.Sqrt(parentScaleX * parentScaleY) * Math.Abs(reference.Magnification);
            var child = GetRaster(target, childScale);
            if (child is null) return;
            try
            {
                foreach (var origin in ReferenceOrigins(reference))
                {
                    var transform = Transform2D.ForReference(origin, reference.Magnification,
                        reference.AngleDegrees, reference.ReflectXAxis);
                    var destination = new[]
                    {
                        map(transform.Apply(new PointD(child.Bounds.MinX, child.Bounds.MaxY))),
                        map(transform.Apply(new PointD(child.Bounds.MaxX, child.Bounds.MaxY))),
                        map(transform.Apply(new PointD(child.Bounds.MinX, child.Bounds.MinY)))
                    };
                    graphics.DrawImage(child.Bitmap, destination,
                        new RectangleF(0, 0, child.Bitmap.Width, child.Bitmap.Height), GraphicsUnit.Pixel);
                }
            }
            finally
            {
                if (!child.IsCached) child.Bitmap.Dispose();
            }
        }

        private BoundsD ResolveBounds(GdsCell cell, HashSet<string> stack)
        {
            if (_bounds.TryGetValue(cell.Name, out var known)) return known;
            if (!stack.Add(cell.Name)) return BoundsD.Empty;
            var bounds = cell.LocalGeometryBounds;
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

        private static PointF[] MapPoints(IReadOnlyList<PointD> points, Func<PointD, PointF> map)
        {
            var result = new PointF[points.Count];
            for (var index = 0; index < points.Count; index++) result[index] = map(points[index]);
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

        private static Color LayerColor(int layer, int dataType)
        {
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

        private static long SaturatingAdd(long left, long right) =>
            left > long.MaxValue - right ? long.MaxValue : left + right;

        private static long SaturatingMultiply(long left, long right) =>
            left == 0 || right == 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

        public void Dispose()
        {
            foreach (var raster in _rasters.Values) raster.Bitmap.Dispose();
        }

        private readonly record struct RasterKey(string CellName, int Width, int Height);
        private sealed record CellRaster(Bitmap Bitmap, BoundsD Bounds, bool IsCached);
    }
}
