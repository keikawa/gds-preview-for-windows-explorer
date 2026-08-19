namespace GdsPreview.Core;

public sealed class SceneBuildOptions
{
    public int MaximumPrimitives { get; init; } = 8_000;
    public int MaximumInstances { get; init; } = 8_000;
    public int MaximumDepth { get; init; } = 48;
    public int MaximumTextLabels { get; init; } = 200;
    public int MaximumPoints { get; init; } = 300_000;
    public int MaximumOverviewCells { get; init; } = 16;
}

public abstract record ScenePrimitive(int Layer, int DataType);
public sealed record ScenePolygon(int Layer, int DataType, IReadOnlyList<PointD> Points)
    : ScenePrimitive(Layer, DataType);
public sealed record ScenePath(int Layer, int DataType, double Width, IReadOnlyList<PointD> Points)
    : ScenePrimitive(Layer, DataType);
public sealed record SceneText(int Layer, int DataType, string Value, PointD Origin, double Size, double AngleDegrees)
    : ScenePrimitive(Layer, DataType);

public sealed record GdsSceneView(
    string CellName,
    IReadOnlyList<ScenePrimitive> Primitives,
    BoundsD Bounds,
    bool WasTruncated);

public sealed class GdsScene
{
    public required string CellName { get; init; }
    public required IReadOnlyList<ScenePrimitive> Primitives { get; init; }
    public required BoundsD Bounds { get; init; }
    public required double MetersPerDatabaseUnit { get; init; }
    public bool WasTruncated { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<GdsSceneView> Views { get; init; } = [];
}

public static class SceneBuilder
{
    private sealed class BuildState(GdsDocument document, SceneBuildOptions options, CancellationToken token)
    {
        public GdsDocument Document { get; } = document;
        public SceneBuildOptions Options { get; } = options;
        public CancellationToken Token { get; } = token;
        public List<ScenePrimitive> Primitives { get; } = [];
        public HashSet<string> Stack { get; } = new(StringComparer.Ordinal);
        public HashSet<string> WarningSet { get; } = new(StringComparer.Ordinal);
        public int Instances { get; set; }
        public int TextLabels { get; set; }
        public int PointCount { get; set; }
        public bool Truncated { get; set; }
        public bool HardLimitReached { get; set; }

        public bool AtLimit => Primitives.Count >= Options.MaximumPrimitives ||
                               Instances >= Options.MaximumInstances || HardLimitReached;
        public void Warn(string message) => WarningSet.Add(message);

        public bool TryAdd(ScenePrimitive primitive, int pointCount)
        {
            if (Primitives.Count >= Options.MaximumPrimitives ||
                PointCount > Options.MaximumPoints - pointCount)
            {
                Truncated = true;
                HardLimitReached = true;
                Warn("Expanded geometry reached the preview memory limit.");
                return false;
            }

            Primitives.Add(primitive);
            PointCount += pointCount;
            return true;
        }
    }

    public static GdsScene Build(GdsDocument document, string? cellName = null,
        SceneBuildOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new SceneBuildOptions();
        ValidateOptions(options);

        if (!string.IsNullOrEmpty(cellName))
        {
            if (!document.Cells.TryGetValue(cellName, out var named))
                throw new ArgumentException($"Cell '{cellName}' does not exist.", nameof(cellName));
            return BuildSingle(document, named, options, cancellationToken);
        }

        var topCells = document.GetTopCells();
        if (topCells.Count == 0)
            throw new GdsFormatException("No structure can be selected for preview.");

        var designTopCells = topCells.Where(cell => !IsMetadataCell(cell.Name)).ToList();
        if (designTopCells.Count == 0)
            designTopCells = topCells.ToList();
        if (designTopCells.Count == 1)
            return BuildSingle(document, designTopCells[0], options, cancellationToken);

        return BuildOverview(document, designTopCells, options, cancellationToken);
    }

    private static GdsScene BuildSingle(GdsDocument document, GdsCell cell,
        SceneBuildOptions options, CancellationToken cancellationToken)
    {
        var state = new BuildState(document, options, cancellationToken);
        if (document.WasSimplified)
        {
            state.Truncated = true;
            foreach (var warning in document.Warnings) state.Warn(warning);
        }
        VisitCell(cell, Transform2D.Identity, 0, state);

        var bounds = BoundsD.Empty;
        foreach (var primitive in state.Primitives)
        {
            switch (primitive)
            {
                case ScenePolygon polygon:
                    foreach (var point in polygon.Points) bounds = bounds.Include(point);
                    break;
                case ScenePath path:
                    foreach (var point in path.Points) bounds = bounds.Include(point);
                    bounds = bounds.Inflate(path.Width / 2);
                    break;
                case SceneText text:
                    bounds = bounds.Include(text.Origin);
                    break;
            }
        }

        if (bounds.IsEmpty)
            bounds = new BoundsD(-1, -1, 1, 1);

        return new GdsScene
        {
            CellName = cell.Name,
            Primitives = state.Primitives,
            Bounds = bounds,
            MetersPerDatabaseUnit = document.MetersPerDatabaseUnit,
            WasTruncated = state.Truncated,
            Warnings = state.WarningSet.ToList(),
            Views = [new GdsSceneView(cell.Name, state.Primitives, bounds, state.Truncated)]
        };
    }

    private static GdsScene BuildOverview(GdsDocument document, IReadOnlyList<GdsCell> topCells,
        SceneBuildOptions options, CancellationToken cancellationToken)
    {
        var memo = new Dictionary<string, long>(StringComparer.Ordinal);
        var selectedNames = topCells
            .OrderByDescending(cell => EstimateElementCount(cell, document, memo, []))
            .ThenBy(cell => cell.Name, StringComparer.Ordinal)
            .Take(options.MaximumOverviewCells)
            .Select(cell => cell.Name)
            .ToHashSet(StringComparer.Ordinal);
        var selectedCells = topCells.Where(cell => selectedNames.Contains(cell.Name)).ToList();
        var cellCount = selectedCells.Count;
        var perCellOptions = new SceneBuildOptions
        {
            MaximumPrimitives = Math.Max(1, options.MaximumPrimitives / cellCount),
            MaximumInstances = Math.Max(1, options.MaximumInstances / cellCount),
            MaximumDepth = options.MaximumDepth,
            MaximumTextLabels = Math.Max(1, options.MaximumTextLabels / cellCount),
            MaximumPoints = Math.Max(3, options.MaximumPoints / cellCount),
            MaximumOverviewCells = options.MaximumOverviewCells
        };

        var views = new List<GdsSceneView>(cellCount);
        var primitives = new List<ScenePrimitive>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var combinedBounds = BoundsD.Empty;
        var wasTruncated = topCells.Count > selectedCells.Count;
        if (wasTruncated)
            warnings.Add($"Only {selectedCells.Count} of {topCells.Count} top-level structures are shown.");

        foreach (var cell in selectedCells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scene = BuildSingle(document, cell, perCellOptions, cancellationToken);
            views.Add(scene.Views[0]);
            primitives.AddRange(scene.Primitives);
            combinedBounds = combinedBounds.Include(scene.Bounds);
            wasTruncated |= scene.WasTruncated;
            foreach (var warning in scene.Warnings) warnings.Add(warning);
        }

        return new GdsScene
        {
            CellName = $"{views.Count} top-level cells",
            Primitives = primitives,
            Bounds = combinedBounds.IsEmpty ? new BoundsD(-1, -1, 1, 1) : combinedBounds,
            MetersPerDatabaseUnit = document.MetersPerDatabaseUnit,
            WasTruncated = wasTruncated,
            Warnings = warnings.ToList(),
            Views = views
        };
    }

    private static bool IsMetadataCell(string name) =>
        name.Equals("$$$CONTEXT_INFO$$$", StringComparison.OrdinalIgnoreCase);

    private static void ValidateOptions(SceneBuildOptions options)
    {
        if (options.MaximumPrimitives < 1 || options.MaximumInstances < 1 ||
            options.MaximumDepth < 0 || options.MaximumTextLabels < 1 ||
            options.MaximumPoints < 3 || options.MaximumOverviewCells < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Scene limits must be positive.");
    }

    private static long EstimateElementCount(GdsCell cell, GdsDocument document,
        Dictionary<string, long> memo, HashSet<string> stack)
    {
        if (memo.TryGetValue(cell.Name, out var known)) return known;
        if (!stack.Add(cell.Name)) return 0;
        long count = 0;
        foreach (var element in cell.Elements)
        {
            if (element is GdsReference reference && document.Cells.TryGetValue(reference.CellName, out var target))
            {
                var multiplier = Math.Min(1_000_000L, (long)reference.Columns * reference.Rows);
                count = SaturatingAdd(count, SaturatingMultiply(
                    EstimateElementCount(target, document, memo, stack), multiplier));
            }
            else
            {
                count = SaturatingAdd(count, 1);
            }
        }
        stack.Remove(cell.Name);
        memo[cell.Name] = count;
        return count;
    }

    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long SaturatingMultiply(long left, long right) =>
        left == 0 || right == 0 ? 0 : left > long.MaxValue / right ? long.MaxValue : left * right;

    private static void VisitCell(GdsCell cell, Transform2D transform, int depth, BuildState state)
    {
        state.Token.ThrowIfCancellationRequested();
        if (state.AtLimit)
        {
            state.Truncated = true;
            return;
        }
        if (depth > state.Options.MaximumDepth)
        {
            state.Truncated = true;
            state.Warn($"Hierarchy depth exceeded {state.Options.MaximumDepth}.");
            return;
        }
        if (!state.Stack.Add(cell.Name))
        {
            state.Warn($"Cyclic reference involving '{cell.Name}' was skipped.");
            return;
        }

        state.Instances++;
        if (cell.SkippedElementCount > 0 && !cell.LocalGeometryBounds.IsEmpty && !state.AtLimit)
        {
            var bounds = cell.LocalGeometryBounds;
            var outline = new[]
            {
                transform.Apply(new PointD(bounds.MinX, bounds.MinY)),
                transform.Apply(new PointD(bounds.MaxX, bounds.MinY)),
                transform.Apply(new PointD(bounds.MaxX, bounds.MaxY)),
                transform.Apply(new PointD(bounds.MinX, bounds.MaxY))
            };
            state.TryAdd(new ScenePolygon(-1, 0, outline), outline.Length);
        }
        foreach (var element in cell.Elements)
        {
            state.Token.ThrowIfCancellationRequested();
            if (state.AtLimit)
            {
                state.Truncated = true;
                break;
            }

            switch (element)
            {
                case GdsPolygon polygon:
                    var polygonPoints = polygon.Points.Select(transform.Apply).ToArray();
                    state.TryAdd(new ScenePolygon(polygon.Layer, polygon.DataType, polygonPoints),
                        polygonPoints.Length);
                    break;
                case GdsPath path:
                    var pathPoints = path.Points.Select(transform.Apply).ToArray();
                    state.TryAdd(new ScenePath(path.Layer, path.DataType,
                        path.Width * transform.ScaleEstimate,
                        pathPoints), pathPoints.Length);
                    break;
                case GdsText text when state.TextLabels < state.Options.MaximumTextLabels:
                    var textTransform = transform.Combine(Transform2D.ForReference(text.Origin,
                        text.Magnification, text.AngleDegrees, text.ReflectXAxis));
                    state.TryAdd(new SceneText(text.Layer, text.TextType, text.Value,
                        textTransform.Apply(new PointD(0, 0)), Math.Max(1, 100 * textTransform.ScaleEstimate),
                        text.AngleDegrees), 1);
                    state.TextLabels++;
                    break;
                case GdsReference reference:
                    VisitReference(reference, transform, depth + 1, state);
                    break;
            }
        }
        state.Stack.Remove(cell.Name);
    }

    private static void VisitReference(GdsReference reference, Transform2D parent, int depth, BuildState state)
    {
        if (!state.Document.Cells.TryGetValue(reference.CellName, out var target))
        {
            state.Warn($"Missing referenced structure '{reference.CellName}'.");
            return;
        }

        if (!reference.IsArray)
        {
            VisitCell(target, parent.Combine(Transform2D.ForReference(reference.Origin,
                reference.Magnification, reference.AngleDegrees, reference.ReflectXAxis)), depth, state);
            return;
        }

        var columnPoint = reference.ColumnPoint ?? reference.Origin;
        var rowPoint = reference.RowPoint ?? reference.Origin;
        var columnStep = (columnPoint - reference.Origin) / reference.Columns;
        var rowStep = (rowPoint - reference.Origin) / reference.Rows;
        var total = (long)reference.Columns * reference.Rows;
        var remaining = Math.Max(1, state.Options.MaximumInstances - state.Instances);
        var samplingStep = total <= remaining ? 1 : Math.Max(1, (int)Math.Ceiling(Math.Sqrt(total / (double)remaining)));
        if (samplingStep > 1)
        {
            state.Truncated = true;
            state.Warn($"Large array of '{reference.CellName}' was sampled every {samplingStep} rows/columns.");
        }

        foreach (var row in SampleIndices(reference.Rows, samplingStep))
        {
            foreach (var column in SampleIndices(reference.Columns, samplingStep))
            {
                if (state.AtLimit)
                {
                    state.Truncated = true;
                    return;
                }
                var origin = reference.Origin + new PointD(
                    columnStep.X * column + rowStep.X * row,
                    columnStep.Y * column + rowStep.Y * row);
                VisitCell(target, parent.Combine(Transform2D.ForReference(origin,
                    reference.Magnification, reference.AngleDegrees, reference.ReflectXAxis)), depth, state);
            }
        }
    }

    private static IEnumerable<int> SampleIndices(int count, int step)
    {
        var last = -1;
        for (var index = 0; index < count; index += step)
        {
            last = index;
            yield return index;
        }
        if (last != count - 1)
            yield return count - 1;
    }
}
