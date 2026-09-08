using System.Drawing;

namespace GdsPreview.Renderer;

internal static partial class LayerPalette
{
    private static readonly Color[] DarkColors;
    private static readonly Color[] LightColors;

    static LayerPalette()
    {
        DarkColors = new Color[Rgb.Length];
        LightColors = new Color[Rgb.Length];
        for (var i = 0; i < Rgb.Length; i++)
        {
            DarkColors[i] = Color.FromArgb(unchecked((int)0xFF000000) | Rgb[i]);
            // Uniform linear-light scaling preserves chromaticity (up to byte
            // rounding). Prepare once, not for every polygon or pixel.
            var rgb = SrgbColorSpace.DecodeRgb(Rgb[i]) * .70f;
            LightColors[i] = Color.FromArgb(SrgbColorSpace.Encode(rgb.X),
                SrgbColorSpace.Encode(rgb.Y), SrgbColorSpace.Encode(rgb.Z));
        }
    }

    internal static int Count => Rgb.Length;
    internal static Color At(int index) => DarkColors[index];

    internal static Color[] ForBackground(Color background) =>
        .2126f * SrgbColorSpace.Decode(background.R) + .7152f * SrgbColorSpace.Decode(background.G) +
        .0722f * SrgbColorSpace.Decode(background.B) >= .5f ? LightColors : DarkColors;

    internal static Color For(int layer, int dataType) => At(IndexFor(layer, dataType));

    internal static int IndexFor(int layer, int dataType)
    {
        // Fixed, unsalted integer mixing. Never depend on file/cell identity,
        // enumeration order, other layers, or the process-randomised HashCode.
        // Mix each complete integer before combining; high bits matter too.
        var hash = Mix(unchecked((uint)layer) ^ 0x9E3779B9u);
        hash = Mix(hash ^ Mix(unchecked((uint)dataType) ^ 0x85EBCA6Bu));
        return (int)(hash & 255);
    }

    private static uint Mix(uint value)
    {
        unchecked
        {
            value ^= value >> 16;
            value *= 0x85EBCA6Bu;
            value ^= value >> 13;
            value *= 0xC2B2AE35u;
            return value ^ (value >> 16);
        }
    }
}
