namespace GdsPreview.Renderer;

/// <summary>Convert display RGB to linear light for coverage and alpha compositing.</summary>
internal static class SrgbColorSpace
{
    private static readonly float[] DecodeTable = Enumerable.Range(0, 256)
        .Select(value => (float)DecodeChannel(value / 255.0)).ToArray();
    private static readonly float[] EncodeThresholds = Enumerable.Range(0, 255)
        .Select(value => (float)DecodeChannel((value + .5) / 255.0)).ToArray();

    public static float Decode(byte value) => DecodeTable[value];

    // Quantize only at final output, using sRGB half-code thresholds rather than
    // a coarse linear LUT. At most eight comparisons, with no per-pixel powers.
    public static byte Encode(float linear)
    {
        var low = 0;
        var high = 255;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (linear >= EncodeThresholds[middle]) low = middle + 1;
            else high = middle;
        }
        return (byte)low;
    }

    private static double DecodeChannel(double value) => value <= .04045
        ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
}
