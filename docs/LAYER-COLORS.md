# Stable layer colours

The renderer assigns a base sRGB colour using only `(layer, datatype)`. File names,
cell names, other layers, traversal order and process lifetime do not participate.
PATH and BOUNDARY share the display style described below. The canvas, cell panels,
loading/error states and resize margins share the Windows-derived background colour;
labels use the matching text colour, with subdued panel borders derived from it.
Preview creation reads Windows' **app** light/dark mode (`AppsUseLightTheme`):
white (#FFFFFF) / dark grey (#181B20), with dark / light text. High-contrast mode uses
system window/text colours. Missing or inaccessible theme settings default to light.
Host-provided background/text hints are ignored so legacy light colours cannot override
the Windows app mode. One invisible top-level window, only while a preview is loaded,
receives [Windows setting notifications](https://learn.microsoft.com/windows/win32/winmsg/wm-settingchange)
on the existing UI thread; child and message-only windows do not receive the broadcast.
Colour changes reuse the resize debounce, with identical values ignored. There is
no polling, extra thread or per-geometry theme check. Unload destroys the receiver.
The fixed source table/hash, coverage integration and fill/outline opacities are
unchanged. Light backgrounds (relative luminance >= 0.5) use a gently darkened
variant: decode each source RGB to linear light, multiply all three channels by
0.70, then encode to sRGB. This preserves chromaticity up to byte rounding and
reduces the pale appearance on white; dark backgrounds retain the original RGB.
The two 256-entry tables are prepared once per renderer process, and the background
selects one table before drawing. No colour transform or theme branch is added to
the geometry loop. The same layer/datatype has the same colour across files under
the same background class. There are no colour settings and no claimed 3:1 contrast
ratio on white or arbitrary host backgrounds; subpixel coverage still limits contrast.

## Fill and outlines

The renderer keeps two area-weighted linear-RGB accumulators: one for interiors,
one for inward boundaries. Each averages its contributed colours and uses
`min(total weight, 1) * opacity`. Interior opacity is capped at 32/255, regardless
of how many filled polygons overlap. Boundaries are composited afterwards, with
opacity up to 240/255. A large enclosing interior cannot overwrite a smaller
shape's boundary. Drawing order does not determine which colour wins (apart from
floating-point rounding). This is a layout display style, not physical source-over
transparency, Boolean layer union, or foundry layer-depth semantics. Coincident
outlines still mix colours and are not individually identifiable at one pixel.

For each polygon, four adjacent pixel coverages estimate approximately half-pixel
**inward** boundary bands. Opposite sides add (so a narrow strip does not simply
lose half its brightness), while horizontal/vertical bands overlap at corners.
This gives the half-pixel band area for straight axial edges at different pixel
phases; oblique edges and corners are screen-space estimates, not exact vector
offsets. Fill uses the remainder of the same coverage. No uncovered pixel is lit,
and narrow structures are not promoted to one-pixel-wide strokes.
Hole bridges cancel before boundary extraction. The bands use unclipped coverage
with a one-pixel halo, then their area is bounded by exact viewport-clipped
coverage. Clipping creates no false border, while a real boundary coinciding with
the viewport remains visible. This screen-space
accent is not a physical-width line; separate touching polygons can show seams.

The two accumulators and packed background use 36 bytes **in total** per canvas
pixel (about 66 MiB at 1600 x 1200), independent of cell/layer count. Coverage is
streamed through three reusable rows: there are no full-canvas area, winding or
clipped-coverage scratch planes. Additional scratch scales with canvas width,
height and the largest polygon's vertex count, not the number of instances.
The hierarchy is traversed once. Edges are bucketed by their first row and only
active edges are integrated; fill and boundary reuse the same coverage. Extra
clipping integration is needed only at fractional viewport boundaries. A polygon
at most one pixel across in either axis is entirely covered by the inward bands,
so it uses its coverage directly without redundant neighbour calculations.
No per-layer images, polygon offset library, ordering heuristics, or user settings
are added. Final sRGB quantization uses a 4 KiB lookup and one threshold comparison
per channel, with the same exact output thresholds as the previous binary search.

## Runtime

`LayerPalette.For` mixes each complete integer with a fixed, unsalted 32-bit
avalanche function (MurmurHash3's fmix32 operations), combines them and indexes the
256-entry table with the low eight bits. Arithmetic explicitly wraps. It does not
use `HashCode`, randomness, runtime palette generation or document-dependent
collision resolution. This small fixed-table lookup is used directly: the former
per-document colour dictionary and its 4096-entry limit are removed.

The RGB table and hash are colour-identity data: do not reorder, regenerate with
different parameters, or change the hash casually. Snapshot tests pin both.
This revision intentionally changes the previous HSV colours. Once adopted, the
new mapping is identical across files. Different keys can still collide; even
nonidentical RGB colours may be hard to distinguish, especially in thin features,
overlaps or colour-vision deficiency. This is not a 256-category accessibility
guarantee. Colours do not encode foundry-specific process semantics.
The hash also does **not** enforce perceptual separation between consecutive layer
numbers: adjacent layers can receive similar colours. Cross-file stability alone
does not solve this usability limitation; the outline/overlap repair leaves that
mapping issue open rather than silently changing layer colour identities again.

## Offline selection

The first nine colours are the unmodified [Paul Tol Light scheme](https://sronpersonalpages.nl/~pault/).
The remaining entries use a deterministic, Glasbey-style greedy maximum-minimum
distance selection, not a continuous rainbow scale. See
[Colorcet's description of categorical palettes](https://colorcet.holoviz.org/user_guide/Categorical.html).
This implementation is an adaptation of that selection principle, not Colorcet's
published `glasbey_light` table and not the output of the Python `glasbey` package.

`scripts/generate-layer-palette.mjs` uses Node.js built-ins only. It considers an
sRGB grid with channel levels 0, 8, ..., 248, 255. Additional colours must have:

- full-coverage composited contrast >= 3.15:1 at the original 128/255 selection
  opacity on BOTH #181B20 and #1F232A;
- source relative luminance <= 0.73 to limit very bright fills;
- source Oklab chroma in [0.055, 0.20] to exclude nearly neutral additions and
  very high-chroma fills, without enforcing equal lightness or HSV saturation.

Distances are squared Euclidean [Oklab](https://bottosson.github.io/posts/oklab/)
distances AFTER linear-light compositing at 128/255 opacity. The smaller distance
on the two original dark backgrounds is used. Each new colour maximises its minimum
distance to the selected colours; ascending RGB enumeration resolves ties.
Oklab conversion uses Ottosson's published public-domain 2021 matrices.

The original selection parameters are retained to avoid another colour-identity
change. Historical selection tests retain the 3:1 rendered **boundary** check
on those original dark canvases. Separate white-background tests verify the uniform
linear-light adjustment and exact compositing. Neither the historical contrast numbers
nor the white-background checks guarantee contrast for subpixel edges or overlapping
geometry, or constitute a WCAG claim.
Seed grey is retained deliberately; other neutral candidates are excluded.

Generation is NOT a build dependency and does not run on users' computers:

```powershell
node scripts/generate-layer-palette.mjs --check
node scripts/generate-layer-palette.mjs --print
```

`--check` verifies the checked-in table byte-for-byte (ignoring CRLF). `--print`
prints the generated C# source for an explicitly reviewed replacement. No file
is overwritten automatically. Keep any table change and updated snapshot tests
together, and review its impact on colour identity.

## Validation

Native theme regressions compile the real handler against test-only Windows-query
stubs, in a separate DLL outside the shipping directory. Tests cover opposite host
colour hints before/after preview creation, light/dark transitions notified only
to top-level windows, high contrast, inaccessible settings, and receiver cleanup.
They also count renderer starts: one at creation, only one more for a theme change,
even with repeated notifications. Tests never change the user's Windows settings.
The normal and registered smoke hosts exercise the production DLL's real OS queries.

Normal builds test all 256 colours on white and the historical dark backgrounds, fixed mapping anchors,
layer/datatype progressions with steps 1/8/256, and separate synthetic documents
with different cell/primitive order and more than 4096 style keys. Existing
coverage, hierarchy, equivalent PATH/BOUNDARY and native-host tests still run.
Additional tests cover internal outlines under 1/8/64 enclosing fills, reversed
draw order, thin lines at different pixel phases, hole bridges, fractional
viewport edges, real boundaries aligned with the viewport, half-pixel outline
width at different pixel phases, accumulator reset and sub-quantization contributions.
Row reuse is checked against independent polygon/pixel clipping for concave shapes
and fractional viewports. An allocation-budget test prevents full-canvas scratch
from returning; exact-quantizer tests cover threshold and lookup-bin boundaries.

The optional `--performance-review <baseline-binary-directory>` command compares
median render times and managed allocations against a previous renderer using
synthetic enclosing fills, 100,000 polygons and 100,000 array instances. It writes
no images. Performance is a primary design constraint: remaining under the
six-second safety timeout does not make a slowdown acceptable. Do not trade
preview latency for additional passes, caches or display features without measurement.

The optional `--color-review <directory>` test executable command creates only
synthetic comparison images: original versus gently darkened colours on white,
with identical geometry, fill and inward outlines. It includes narrow lines,
large regions, two/three/four overlapping shapes, both palette and layer-index
swatches, an explicit 1/8/32-enclosing-fill comparison against the old 50%-fill-only
compositor (using the same light palette in that comparison), plus approximate
protanopia/deuteranopia/tritanopia simulations using
the severity-100 matrices of Machado, Oliveira and Fernandes (2009). These are
diagnostic approximations, not universal colour-vision guarantees. Matrix source:
[authors' supplementary data](https://www.inf.ufrgs.br/~oliveira/pubs_files/CVD_Simulation/CVD_Simulation.html),
also reproduced in [colorspacious](https://github.com/njsmith/colorspacious/blob/master/colorspacious/cvd.py).

Never use confidential layouts or their screenshots as checked-in fixtures.
