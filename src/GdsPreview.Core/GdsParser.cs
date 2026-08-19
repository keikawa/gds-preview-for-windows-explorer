using System.Buffers.Binary;
using System.Text;

namespace GdsPreview.Core;

public sealed class GdsParserOptions
{
    public long MaximumFileBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int MaximumRecords { get; init; } = 10_000_000;
    public int MaximumCells { get; init; } = 100_000;
    public int MaximumStoredGeometryElements { get; init; } = 30_000;
    public int MaximumStoredGeometryElementsPerCell { get; init; } = 4_000;
    public int MaximumStoredPoints { get; init; } = 1_000_000;
    public int MaximumStoredReferences { get; init; } = 50_000;
    public int MaximumStoredTextElements { get; init; } = 500;
    public int MaximumPointsPerElement { get; init; } = 1_024;
}

public static class GdsParser
{
    private enum RecordType : byte
    {
        Header = 0x00,
        BgnLib = 0x01,
        LibName = 0x02,
        Units = 0x03,
        EndLib = 0x04,
        BgnStr = 0x05,
        StrName = 0x06,
        EndStr = 0x07,
        Boundary = 0x08,
        Path = 0x09,
        SRef = 0x0A,
        ARef = 0x0B,
        Text = 0x0C,
        Layer = 0x0D,
        DataType = 0x0E,
        Width = 0x0F,
        Xy = 0x10,
        EndEl = 0x11,
        SName = 0x12,
        ColRow = 0x13,
        TextType = 0x16,
        String = 0x19,
        STrans = 0x1A,
        Mag = 0x1B,
        Angle = 0x1C,
        PathType = 0x21,
        Box = 0x2D,
        BoxType = 0x2E
    }

    private enum ElementKind
    {
        None,
        Boundary,
        Path,
        SRef,
        ARef,
        Text,
        Box
    }

    private sealed class ElementBuilder
    {
        public ElementKind Kind { get; set; }
        public int Layer { get; set; }
        public int DataType { get; set; }
        public int TextType { get; set; }
        public int PathType { get; set; }
        public double Width { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public bool ReflectXAxis { get; set; }
        public double Magnification { get; set; } = 1;
        public double AngleDegrees { get; set; }
        public int Columns { get; set; } = 1;
        public int Rows { get; set; } = 1;
        public List<PointD> Points { get; set; } = [];
        public BoundsD Bounds { get; set; } = BoundsD.Empty;
    }

    public static GdsDocument ParseFile(string path, CancellationToken cancellationToken = default,
        GdsParserOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.SequentialScan);
        return Parse(stream, cancellationToken, options);
    }

    public static GdsDocument Parse(Stream stream, CancellationToken cancellationToken = default,
        GdsParserOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("The stream must be readable.", nameof(stream));

        options ??= new GdsParserOptions();
        if (stream.CanSeek && stream.Length > options.MaximumFileBytes)
            throw new GdsFormatException($"The file exceeds the {options.MaximumFileBytes:N0}-byte safety limit.");

        var document = new GdsDocument();
        GdsCell? currentCell = null;
        ElementBuilder? element = null;
        var header = new byte[4];
        var recordCount = 0;
        var storedGeometry = 0;
        var storedReferences = 0;
        var storedText = 0;
        var storedPoints = 0;
        var reachedEndLibrary = false;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var headerRead = ReadUpTo(stream, header);
            if (headerRead == 0)
                break;
            if (headerRead != 4)
                throw new GdsFormatException("Truncated GDSII record header.");

            var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
            if (length < 4 || (length & 1) != 0)
                throw new GdsFormatException($"Invalid GDSII record length: {length}.");
            if (++recordCount > options.MaximumRecords)
                throw new GdsFormatException("The record-count safety limit was exceeded.");

            var payload = new byte[length - 4];
            ReadExactly(stream, payload);
            var type = (RecordType)header[2];

            switch (type)
            {
                case RecordType.LibName:
                    document.LibraryName = ReadAscii(payload);
                    break;
                case RecordType.Units:
                    RequireSize(payload, 16, type);
                    document.UserUnitsPerDatabaseUnit = ReadReal8(payload.AsSpan(0, 8));
                    document.MetersPerDatabaseUnit = ReadReal8(payload.AsSpan(8, 8));
                    break;
                case RecordType.EndLib:
                    if (element is not null || currentCell is not null)
                        throw new GdsFormatException("ENDLIB occurred inside a structure or element.");
                    reachedEndLibrary = true;
                    break;
                case RecordType.BgnStr:
                    if (currentCell is not null)
                        throw new GdsFormatException("Nested BGNSTR records are not valid.");
                    currentCell = new GdsCell(string.Empty);
                    break;
                case RecordType.StrName:
                    RequireCell(currentCell, type).Name = ReadAscii(payload);
                    break;
                case RecordType.EndStr:
                    if (element is not null)
                        throw new GdsFormatException("ENDSTR occurred before ENDEL.");
                    document.AddCell(RequireCell(currentCell, type));
                    if (document.CellsInFileOrder.Count > options.MaximumCells)
                        throw new GdsFormatException($"The cell-count safety limit ({options.MaximumCells:N0}) was exceeded.");
                    currentCell = null;
                    break;
                case RecordType.Boundary:
                    element = BeginElement(element, ElementKind.Boundary);
                    break;
                case RecordType.Path:
                    element = BeginElement(element, ElementKind.Path);
                    break;
                case RecordType.SRef:
                    element = BeginElement(element, ElementKind.SRef);
                    break;
                case RecordType.ARef:
                    element = BeginElement(element, ElementKind.ARef);
                    break;
                case RecordType.Text:
                    element = BeginElement(element, ElementKind.Text);
                    break;
                case RecordType.Box:
                    element = BeginElement(element, ElementKind.Box);
                    break;
                case RecordType.Layer:
                    RequireElement(element, type).Layer = ReadInt16(payload, type);
                    break;
                case RecordType.DataType:
                case RecordType.BoxType:
                    RequireElement(element, type).DataType = ReadInt16(payload, type);
                    break;
                case RecordType.TextType:
                    RequireElement(element, type).TextType = ReadInt16(payload, type);
                    break;
                case RecordType.Width:
                    RequireSize(payload, 4, type);
                    RequireElement(element, type).Width = Math.Abs(BinaryPrimitives.ReadInt32BigEndian(payload));
                    break;
                case RecordType.PathType:
                    RequireElement(element, type).PathType = ReadInt16(payload, type);
                    break;
                case RecordType.SName:
                    RequireElement(element, type).Name = ReadAscii(payload);
                    break;
                case RecordType.String:
                    RequireElement(element, type).Text = ReadAscii(payload);
                    break;
                case RecordType.STrans:
                    RequireSize(payload, 2, type);
                    RequireElement(element, type).ReflectXAxis = (BinaryPrimitives.ReadUInt16BigEndian(payload) & 0x8000) != 0;
                    break;
                case RecordType.Mag:
                    RequireSize(payload, 8, type);
                    RequireElement(element, type).Magnification = ReadReal8(payload);
                    break;
                case RecordType.Angle:
                    RequireSize(payload, 8, type);
                    RequireElement(element, type).AngleDegrees = ReadReal8(payload);
                    break;
                case RecordType.ColRow:
                    RequireSize(payload, 4, type);
                    var array = RequireElement(element, type);
                    array.Columns = Math.Max(1, (int)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(0, 2)));
                    array.Rows = Math.Max(1, (int)BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(2, 2)));
                    break;
                case RecordType.Xy:
                    var xyElement = RequireElement(element, type);
                    (xyElement.Points, xyElement.Bounds) = ReadPoints(payload, options.MaximumPointsPerElement);
                    break;
                case RecordType.EndEl:
                    var cell = RequireCell(currentCell, type);
                    var completed = RequireElement(element, type);
                    cell.SourceElementCount++;
                    cell.LocalGeometryBounds = IncludeElementBounds(cell.LocalGeometryBounds, completed);
                    if (ShouldStore(completed, cell, options, ref storedGeometry, ref storedReferences,
                            ref storedText, ref storedPoints))
                    {
                        cell.Elements.Add(BuildElement(completed));
                    }
                    else
                    {
                        cell.SkippedElementCount++;
                        document.WasSimplified = true;
                    }
                    element = null;
                    break;
            }

            if (reachedEndLibrary)
                break;
        }

        if (element is not null || currentCell is not null)
            throw new GdsFormatException("Unexpected end of file inside a structure or element.");
        if (document.CellsInFileOrder.Count == 0)
            throw new GdsFormatException("The file contains no GDSII structures.");
        if (document.WasSimplified)
            document.Warnings.Add("Geometry was sampled to keep Explorer responsive.");
        return document;
    }

    private static bool ShouldStore(ElementBuilder builder, GdsCell cell, GdsParserOptions options,
        ref int storedGeometry, ref int storedReferences, ref int storedText, ref int storedPoints)
    {
        if (builder.Kind is ElementKind.SRef or ElementKind.ARef)
        {
            if (storedReferences >= options.MaximumStoredReferences) return false;
            storedReferences++;
            return true;
        }

        if (builder.Kind == ElementKind.Text)
        {
            if (storedText >= options.MaximumStoredTextElements) return false;
            storedText++;
        }

        if (storedGeometry >= options.MaximumStoredGeometryElements ||
            cell.StoredGeometryCount >= options.MaximumStoredGeometryElementsPerCell ||
            storedPoints > options.MaximumStoredPoints - builder.Points.Count)
            return false;

        storedGeometry++;
        cell.StoredGeometryCount++;
        storedPoints += builder.Points.Count;
        return true;
    }

    private static BoundsD IncludeElementBounds(BoundsD bounds, ElementBuilder builder)
    {
        if (builder.Kind is ElementKind.SRef or ElementKind.ARef) return bounds;
        var elementBounds = builder.Bounds;
        if (builder.Kind == ElementKind.Path && builder.Width > 0)
            elementBounds = elementBounds.Inflate(builder.Width / 2);
        return bounds.Include(elementBounds);
    }

    private static GdsElement BuildElement(ElementBuilder builder)
    {
        return builder.Kind switch
        {
            ElementKind.Boundary or ElementKind.Box => new GdsPolygon(
                builder.Layer, builder.DataType, NormalizePolygon(builder.Points)),
            ElementKind.Path => new GdsPath(builder.Layer, builder.DataType, builder.Width,
                builder.PathType, RequirePointCount(builder.Points, 2, "PATH")),
            ElementKind.Text => new GdsText(builder.Layer, builder.TextType, builder.Text,
                RequirePointCount(builder.Points, 1, "TEXT")[0], builder.Magnification,
                builder.AngleDegrees, builder.ReflectXAxis),
            ElementKind.SRef => new GdsReference(RequireName(builder),
                RequirePointCount(builder.Points, 1, "SREF")[0], builder.Magnification,
                builder.AngleDegrees, builder.ReflectXAxis),
            ElementKind.ARef => BuildArrayReference(builder),
            _ => throw new GdsFormatException("Unknown or missing element type.")
        };
    }

    private static GdsReference BuildArrayReference(ElementBuilder builder)
    {
        var points = RequirePointCount(builder.Points, 3, "AREF");
        return new GdsReference(RequireName(builder), points[0], builder.Magnification,
            builder.AngleDegrees, builder.ReflectXAxis, builder.Columns, builder.Rows,
            points[1], points[2]);
    }

    private static string RequireName(ElementBuilder builder)
    {
        if (string.IsNullOrEmpty(builder.Name))
            throw new GdsFormatException("A reference has no SNAME record.");
        return builder.Name;
    }

    private static IReadOnlyList<PointD> NormalizePolygon(List<PointD> points)
    {
        RequirePointCount(points, 3, "BOUNDARY/BOX");
        if (points.Count > 3 && points[0] == points[^1])
            points.RemoveAt(points.Count - 1);
        return points;
    }

    private static List<PointD> RequirePointCount(List<PointD> points, int count, string elementName)
    {
        if (points.Count < count)
            throw new GdsFormatException($"{elementName} requires at least {count} XY point(s).");
        return points;
    }

    private static ElementBuilder BeginElement(ElementBuilder? current, ElementKind kind)
    {
        if (current is not null)
            throw new GdsFormatException("An element began before the previous ENDEL record.");
        return new ElementBuilder { Kind = kind };
    }

    private static ElementBuilder RequireElement(ElementBuilder? element, RecordType type) => element
        ?? throw new GdsFormatException($"{type} occurred outside an element.");

    private static GdsCell RequireCell(GdsCell? cell, RecordType type) => cell
        ?? throw new GdsFormatException($"{type} occurred outside a structure.");

    private static int ReadInt16(byte[] payload, RecordType type)
    {
        RequireSize(payload, 2, type);
        return BinaryPrimitives.ReadInt16BigEndian(payload);
    }

    private static (List<PointD> Points, BoundsD Bounds) ReadPoints(byte[] payload, int maximumRetainedPoints)
    {
        if (payload.Length == 0 || payload.Length % 8 != 0)
            throw new GdsFormatException("An XY record has an invalid size.");
        var pointCount = payload.Length / 8;
        var stride = Math.Max(1, (int)Math.Ceiling(pointCount / (double)Math.Max(2, maximumRetainedPoints)));
        var points = new List<PointD>(Math.Min(pointCount, maximumRetainedPoints + 2));
        var bounds = BoundsD.Empty;
        PointD lastPoint = default;
        for (var index = 0; index < pointCount; index++)
        {
            var offset = index * 8;
            var x = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset, 4));
            var y = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(offset + 4, 4));
            lastPoint = new PointD(x, y);
            bounds = bounds.Include(lastPoint);
            if (index == 0 || index % stride == 0)
                points.Add(lastPoint);
        }
        if (points.Count == 0 || points[^1] != lastPoint)
            points.Add(lastPoint);
        return (points, bounds);
    }

    internal static double ReadReal8(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8)
            throw new ArgumentException("A GDSII real8 value must contain exactly eight bytes.", nameof(bytes));
        if ((bytes[0] & 0x7F) == 0 && bytes[1..].IndexOfAnyExcept((byte)0) < 0)
            return 0;

        var negative = (bytes[0] & 0x80) != 0;
        var exponent = (bytes[0] & 0x7F) - 64;
        ulong mantissa = 0;
        for (var i = 1; i < 8; i++)
            mantissa = (mantissa << 8) | bytes[i];
        var value = mantissa / 72057594037927936.0 * Math.Pow(16.0, exponent);
        return negative ? -value : value;
    }

    private static string ReadAscii(byte[] payload) => Encoding.ASCII.GetString(payload).TrimEnd('\0');

    private static void RequireSize(byte[] payload, int expected, RecordType type)
    {
        if (payload.Length != expected)
            throw new GdsFormatException($"{type} has size {payload.Length}; expected {expected}.");
    }

    private static int ReadUpTo(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count == 0) break;
            total += count;
        }
        return total;
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var count = stream.Read(buffer, total, buffer.Length - total);
            if (count == 0)
                throw new GdsFormatException("Truncated GDSII record payload.");
            total += count;
        }
    }
}
