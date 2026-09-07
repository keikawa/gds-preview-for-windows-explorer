using GdsPreview.Core;

namespace GdsPreview.Renderer;

internal static class PathOutline
{
    // Construct physical-width geometry before applying any instance or screen
    // transform. GDI+ pens silently promote subpixel strokes to cosmetic hairlines.
    public static PointD[] Create(GdsPath path)
    {
        if (path.Width <= 0) return [];
        var points = new List<PointD>(path.Points.Count);
        foreach (var point in path.Points)
            if (points.Count == 0 || points[^1] != point) points.Add(point);
        if (points.Count < 2) return [];
        var half = path.Width / 2;
        var normals = new PointD[points.Count - 1];
        for (var i = 0; i < normals.Length; i++)
        {
            var delta = points[i + 1] - points[i];
            var length = Math.Sqrt(delta.X * delta.X + delta.Y * delta.Y);
            normals[i] = new PointD(-delta.Y / length, delta.X / length);
        }
        var left = new List<PointD>(points.Count * 2);
        var right = new List<PointD>(points.Count * 2);
        var start = points[0];
        var end = points[^1];
        if (path.PathType == 2)
        {
            start += new PointD(-normals[0].Y * half, normals[0].X * half);
            end += new PointD(normals[^1].Y * half, -normals[^1].X * half);
        }
        left.Add(Offset(start, normals[0], half));
        right.Add(Offset(start, normals[0], -half));
        for (var i = 1; i < points.Count - 1; i++)
        {
            var before = normals[i - 1];
            var after = normals[i];
            var denominator = 1 + before.X * after.X + before.Y * after.Y;
            var factor = denominator > 1e-12 ? half / denominator : 0;
            var miter = new PointD((before.X + after.X) * factor, (before.Y + after.Y) * factor);
            if (denominator > 1e-12 && miter.X * miter.X + miter.Y * miter.Y <= 100 * half * half)
            {
                left.Add(points[i] + miter);
                right.Add(points[i] - miter);
            }
            else
            {
                left.Add(Offset(points[i], before, half));
                left.Add(Offset(points[i], after, half));
                right.Add(Offset(points[i], before, -half));
                right.Add(Offset(points[i], after, -half));
            }
        }
        left.Add(Offset(end, normals[^1], half));
        right.Add(Offset(end, normals[^1], -half));
        var outline = new List<PointD>(left.Count + right.Count + 256);
        outline.AddRange(left);
        if (path.PathType == 1) AddRoundCap(outline, end, normals[^1], half);
        right.Reverse();
        outline.AddRange(right);
        if (path.PathType == 1)
            AddRoundCap(outline, start, new PointD(-normals[0].X, -normals[0].Y), half);
        return outline.ToArray();
    }

    private static PointD Offset(PointD point, PointD normal, double distance) =>
        new(point.X + normal.X * distance, point.Y + normal.Y * distance);

    private static void AddRoundCap(List<PointD> outline, PointD center, PointD normal, double radius)
    {
        var startAngle = Math.Atan2(normal.Y, normal.X);
        // Sagitta < 0.000076 radii; no screen-space width enlargement.
        const int segments = 128;
        for (var i = 1; i < segments; i++)
        {
            var angle = startAngle - Math.PI * i / segments;
            outline.Add(new PointD(center.X + radius * Math.Cos(angle), center.Y + radius * Math.Sin(angle)));
        }
    }
}
