using System.Numerics;

namespace GdsPreview.Renderer;

/// <summary>Convert display RGB to linear light for coverage and alpha compositing.</summary>
internal static class SrgbColorSpace
{
    private static readonly float[] DecodeTable = Enumerable.Range(0, 256)
        .Select(value => (float)DecodeChannel(value / 255.0)).ToArray();
    private static readonly float[] EncodeThresholds = Enumerable.Range(0, 255)
        .Select(value => (float)DecodeChannel((value + .5) / 255.0)).ToArray();
    private static readonly byte[] EncodeLower = CreateEncodeLower();

    public static float Decode(byte value) => DecodeTable[value];
    public static Vector3 DecodeRgb(int argb) => new(Decode((byte)(argb >> 16)), Decode((byte)(argb >> 8)), Decode((byte)argb));

    // A 1/4096-wide linear interval contains at most one sRGB half-code threshold
    // (the minimum spacing is 1/(255*12.92)). One lookup plus one exact comparison
    // gives the same quantizer as binary search, not an approximate colour LUT.
    public static byte Encode(float linear)
    {
        if (!(linear > 0)) return 0;
        if (linear >= 1) return 255;
        var lower = EncodeLower[(int)(linear * 4096)];
        return lower < 255 && linear >= EncodeThresholds[lower] ? (byte)(lower + 1) : lower;
    }

    private static byte[] CreateEncodeLower()
    {
        var table = new byte[4096];
        var threshold = 0;
        for (var i = 0; i < table.Length; i++)
        {
            while (threshold < 255 && i / 4096f >= EncodeThresholds[threshold]) threshold++;
            table[i] = (byte)threshold;
        }
        return table;
    }

    private static double DecodeChannel(double value) => value <= .04045
        ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
}
