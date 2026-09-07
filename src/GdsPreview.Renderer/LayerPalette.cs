using System.Drawing;

namespace GdsPreview.Renderer;

internal static partial class LayerPalette
{
    internal static int Count => Rgb.Length;
    internal static Color At(int index) => Color.FromArgb(unchecked((int)0xFF000000) | Rgb[index]);

    internal static Color For(int layer, int dataType)
    {
        // Fixed, unsalted integer mixing. Never depend on file/cell identity,
        // enumeration order, other layers, or the process-randomised HashCode.
        // Mix each complete integer before combining; high bits matter too.
        var hash = Mix(unchecked((uint)layer) ^ 0x9E3779B9u);
        hash = Mix(hash ^ Mix(unchecked((uint)dataType) ^ 0x85EBCA6Bu));
        return At((int)(hash & 255));
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
