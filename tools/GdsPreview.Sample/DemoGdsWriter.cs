using System.Buffers.Binary;
using System.Text;

namespace GdsPreview.Sample;

public static class DemoGdsWriter
{
    public static void Write(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Record(stream, 0x00, 0x02, Int16(600));                         // HEADER
        Record(stream, 0x01, 0x02, Dates());                           // BGNLIB
        Record(stream, 0x02, 0x06, Ascii("GDS_PREVIEW_DEMO"));         // LIBNAME
        Record(stream, 0x03, 0x05, Real8(0.001), Real8(1e-9));         // UNITS

        BeginStructure(stream, "LEAF");
        Boundary(stream, 1, 0, [(-400, -300), (400, -300), (400, 300), (-400, 300), (-400, -300)]);
        Path(stream, 2, 0, 90, [(-300, 0), (0, 250), (300, 0)]);
        Text(stream, 3, 0, "LEAF", (0, 0));
        Record(stream, 0x07, 0x00);                                    // ENDSTR

        BeginStructure(stream, "TOP");
        Boundary(stream, 10, 0, [(-1000, -2000), (7000, -2000), (7000, 2000), (-1000, 2000), (-1000, -2000)]);
        SRef(stream, "LEAF", (0, 0), 1.2, 25, true);
        ARef(stream, "LEAF", 4, 3, (2000, -1500), (6000, -1500), (2000, 1500));
        Record(stream, 0x07, 0x00);                                    // ENDSTR
        Record(stream, 0x04, 0x00);                                    // ENDLIB
    }

    public static void WriteLargeFlat(Stream stream, int polygonCount)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (polygonCount < 1) throw new ArgumentOutOfRangeException(nameof(polygonCount));
        Record(stream, 0x00, 0x02, Int16(600));
        Record(stream, 0x01, 0x02, Dates());
        Record(stream, 0x02, 0x06, Ascii("GDS_PREVIEW_LOAD_TEST"));
        Record(stream, 0x03, 0x05, Real8(0.001), Real8(1e-9));
        BeginStructure(stream, "TOP");
        for (var index = 0; index < polygonCount; index++)
        {
            var column = index % 1000;
            var row = index / 1000;
            var x = column * 20;
            var y = row * 20;
            Boundary(stream, (short)(index % 32), 0,
                [(x, y), (x + 10, y), (x + 10, y + 10), (x, y + 10), (x, y)]);
        }
        Record(stream, 0x07, 0x00);
        Record(stream, 0x04, 0x00);
    }

    public static void WriteMultipleTopCells(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Record(stream, 0x00, 0x02, Int16(600));
        Record(stream, 0x01, 0x02, Dates());
        Record(stream, 0x02, 0x06, Ascii("GDS_PREVIEW_MULTI_TOP"));
        Record(stream, 0x03, 0x05, Real8(0.001), Real8(1e-9));

        BeginStructure(stream, "$$$CONTEXT_INFO$$$");
        for (var index = 0; index < 8; index++)
            Boundary(stream, 99, 0, [(index * 20, 0), (index * 20 + 10, 0),
                (index * 20 + 10, 10), (index * 20, 10), (index * 20, 0)]);
        Record(stream, 0x07, 0x00);

        BeginStructure(stream, "DESIGN_A");
        Boundary(stream, 1, 0, [(0, 0), (100, 0), (100, 100), (0, 100), (0, 0)]);
        Record(stream, 0x07, 0x00);

        BeginStructure(stream, "DESIGN_B");
        Boundary(stream, 2, 0, [(0, 0), (200, 0), (200, 50), (0, 50), (0, 0)]);
        Record(stream, 0x07, 0x00);
        Record(stream, 0x04, 0x00);
    }

    public static void WriteHighVertexPolygon(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        Record(stream, 0x00, 0x02, Int16(600));
        Record(stream, 0x01, 0x02, Dates());
        Record(stream, 0x02, 0x06, Ascii("GDS_PREVIEW_HIGH_VERTEX"));
        Record(stream, 0x03, 0x05, Real8(0.001), Real8(1e-9));
        BeginStructure(stream, "TOP");
        var points = new List<(int X, int Y)>();
        for (var x = 0; x < 1_100; x++) points.Add((x, x % 2));
        for (var x = 1_099; x >= 0; x--) points.Add((x, 100 + x % 2));
        points.Add(points[0]);
        Boundary(stream, 1, 0, points);
        Record(stream, 0x07, 0x00);
        Record(stream, 0x04, 0x00);
    }

    private static void BeginStructure(Stream stream, string name)
    {
        Record(stream, 0x05, 0x02, Dates());
        Record(stream, 0x06, 0x06, Ascii(name));
    }

    private static void Boundary(Stream stream, short layer, short dataType,
        IReadOnlyList<(int X, int Y)> points)
    {
        Record(stream, 0x08, 0x00);
        Record(stream, 0x0D, 0x02, Int16(layer));
        Record(stream, 0x0E, 0x02, Int16(dataType));
        Record(stream, 0x10, 0x03, Points(points));
        Record(stream, 0x11, 0x00);
    }

    private static void Path(Stream stream, short layer, short dataType, int width,
        IReadOnlyList<(int X, int Y)> points)
    {
        Record(stream, 0x09, 0x00);
        Record(stream, 0x0D, 0x02, Int16(layer));
        Record(stream, 0x0E, 0x02, Int16(dataType));
        Record(stream, 0x0F, 0x03, Int32(width));
        Record(stream, 0x10, 0x03, Points(points));
        Record(stream, 0x11, 0x00);
    }

    private static void Text(Stream stream, short layer, short textType, string value, (int X, int Y) origin)
    {
        Record(stream, 0x0C, 0x00);
        Record(stream, 0x0D, 0x02, Int16(layer));
        Record(stream, 0x16, 0x02, Int16(textType));
        Record(stream, 0x19, 0x06, Ascii(value));
        Record(stream, 0x10, 0x03, Points([origin]));
        Record(stream, 0x11, 0x00);
    }

    private static void SRef(Stream stream, string name, (int X, int Y) origin,
        double magnification, double angle, bool reflected)
    {
        Record(stream, 0x0A, 0x00);
        Record(stream, 0x12, 0x06, Ascii(name));
        if (reflected) Record(stream, 0x1A, 0x01, UInt16(0x8000));
        Record(stream, 0x1B, 0x05, Real8(magnification));
        Record(stream, 0x1C, 0x05, Real8(angle));
        Record(stream, 0x10, 0x03, Points([origin]));
        Record(stream, 0x11, 0x00);
    }

    private static void ARef(Stream stream, string name, short columns, short rows,
        (int X, int Y) origin, (int X, int Y) columnPoint, (int X, int Y) rowPoint)
    {
        Record(stream, 0x0B, 0x00);
        Record(stream, 0x12, 0x06, Ascii(name));
        Record(stream, 0x13, 0x02, Int16(columns), Int16(rows));
        Record(stream, 0x10, 0x03, Points([origin, columnPoint, rowPoint]));
        Record(stream, 0x11, 0x00);
    }

    private static byte[] Dates()
    {
        var values = new short[]
        {
            2026, 1, 1, 0, 0, 0,
            2026, 1, 1, 0, 0, 0
        };
        return values.SelectMany(Int16).ToArray();
    }

    private static byte[] Points(IReadOnlyList<(int X, int Y)> points) =>
        points.SelectMany(point => Int32(point.X).Concat(Int32(point.Y))).ToArray();

    private static byte[] Ascii(string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        return bytes.Length % 2 == 0 ? bytes : [.. bytes, 0];
    }

    private static byte[] Int16(short value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] UInt16(ushort value)
    {
        var bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Int32(int value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Real8(double value)
    {
        var result = new byte[8];
        if (value == 0) return result;

        var negative = value < 0;
        value = Math.Abs(value);
        var exponent = 64;
        while (value >= 1)
        {
            value /= 16;
            exponent++;
        }
        while (value < 1.0 / 16.0)
        {
            value *= 16;
            exponent--;
        }

        result[0] = (byte)((negative ? 0x80 : 0) | exponent);
        for (var index = 1; index < result.Length; index++)
        {
            value *= 256;
            result[index] = (byte)Math.Floor(value);
            value -= result[index];
        }
        return result;
    }

    private static void Record(Stream stream, byte recordType, byte dataType, params byte[][] payloadParts)
    {
        var payloadLength = payloadParts.Sum(part => part.Length);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(0, 2), checked((ushort)(payloadLength + 4)));
        header[2] = recordType;
        header[3] = dataType;
        stream.Write(header);
        foreach (var part in payloadParts) stream.Write(part);
    }
}
