namespace GdsPreview.Core;

public readonly record struct PointD(double X, double Y)
{
    public static PointD operator +(PointD left, PointD right) => new(left.X + right.X, left.Y + right.Y);
    public static PointD operator -(PointD left, PointD right) => new(left.X - right.X, left.Y - right.Y);
    public static PointD operator /(PointD point, double divisor) => new(point.X / divisor, point.Y / divisor);
}

public readonly record struct BoundsD(double MinX, double MinY, double MaxX, double MaxY)
{
    public static BoundsD Empty => new(double.PositiveInfinity, double.PositiveInfinity,
        double.NegativeInfinity, double.NegativeInfinity);

    public bool IsEmpty => MinX > MaxX || MinY > MaxY;
    public double Width => IsEmpty ? 0 : MaxX - MinX;
    public double Height => IsEmpty ? 0 : MaxY - MinY;

    public BoundsD Include(PointD point)
    {
        if (IsEmpty)
            return new BoundsD(point.X, point.Y, point.X, point.Y);

        return new BoundsD(
            Math.Min(MinX, point.X),
            Math.Min(MinY, point.Y),
            Math.Max(MaxX, point.X),
            Math.Max(MaxY, point.Y));
    }

    public BoundsD Include(BoundsD other)
    {
        if (other.IsEmpty) return this;
        if (IsEmpty) return other;
        return new BoundsD(
            Math.Min(MinX, other.MinX),
            Math.Min(MinY, other.MinY),
            Math.Max(MaxX, other.MaxX),
            Math.Max(MaxY, other.MaxY));
    }

    public BoundsD Inflate(double amount) => IsEmpty
        ? this
        : new BoundsD(MinX - amount, MinY - amount, MaxX + amount, MaxY + amount);
}

/// <summary>2D affine transform: x'=A*x+C*y+Tx, y'=B*x+D*y+Ty.</summary>
public readonly record struct Transform2D(double A, double B, double C, double D, double Tx, double Ty)
{
    public static Transform2D Identity => new(1, 0, 0, 1, 0, 0);

    public PointD Apply(PointD point) => new(
        A * point.X + C * point.Y + Tx,
        B * point.X + D * point.Y + Ty);

    public double ScaleEstimate => Math.Sqrt(Math.Abs(A * D - B * C));

    /// <summary>Returns this * child; child is applied first.</summary>
    public Transform2D Combine(Transform2D child) => new(
        A * child.A + C * child.B,
        B * child.A + D * child.B,
        A * child.C + C * child.D,
        B * child.C + D * child.D,
        A * child.Tx + C * child.Ty + Tx,
        B * child.Tx + D * child.Ty + Ty);

    public static Transform2D ForReference(PointD origin, double magnification, double angleDegrees,
        bool reflectXAxis)
    {
        var radians = angleDegrees * Math.PI / 180.0;
        var cos = Math.Cos(radians) * magnification;
        var sin = Math.Sin(radians) * magnification;
        return reflectXAxis
            ? new Transform2D(cos, sin, sin, -cos, origin.X, origin.Y)
            : new Transform2D(cos, sin, -sin, cos, origin.X, origin.Y);
    }
}
