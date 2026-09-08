using System.Numerics;

namespace GdsPreview.Renderer;

/// <summary>
/// Order-independent, bounded fill tint with a separate boundary overlay.
/// Contributions are premultiplied by covered area, not by an artificial width.
/// This is a layout display style, not a physical transparency simulation.
/// </summary>
internal sealed class LayoutCompositor(int length)
{
    internal const int FillOpacity = 32;
    internal const int OutlineOpacity = 240;
    internal const double OutlineWidthPixels = .5;

    private struct Contribution
    {
        internal Vector3 Color;
        internal float Weight;
        internal void Add(Vector3 color, float weight)
        {
            Color += color * weight;
            Weight += weight;
        }

        internal Vector3 Blend(Vector3 background, int opacity, float boost = 0)
        {
            if (Weight <= 0) return background;
            var coverage = Math.Min(Weight, 1);
            var alpha = coverage * (1 + boost * (1 - coverage)) * (opacity / 255f);
            // Averaging colours prevents repeated enclosing fills from washing
            // out the frame. Capping alpha does not discard any geometry.
            return background + (Color / Weight - background) * alpha;
        }
    }

    private readonly Contribution[] _fill = new Contribution[length];
    private readonly Contribution[] _outline = new Contribution[length];

    internal void Add(int index, Vector3 color, double coverage, double outline)
    {
        // Disjoint parts of the original coverage: never paint an edge AND a
        // fill over the same subpixel area of one shape.
        var fill = (float)Math.Max(0, coverage - outline);
        if (fill > 0) _fill[index].Add(color, fill);
        if (outline > 0) _outline[index].Add(color, (float)outline);
    }

    internal void Clear(int index)
    {
        _fill[index] = default;
        _outline[index] = default;
    }

    internal Vector3 Blend(int index, Vector3 background) =>
        _outline[index].Blend(_fill[index].Blend(background, FillOpacity), OutlineOpacity, .5f);
}
