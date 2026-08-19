namespace GdsPreview.Core;

public sealed class GdsDocument
{
    public string LibraryName { get; internal set; } = string.Empty;
    public double UserUnitsPerDatabaseUnit { get; internal set; } = 1e-3;
    public double MetersPerDatabaseUnit { get; internal set; } = 1e-9;
    public IList<GdsCell> CellsInFileOrder { get; } = new List<GdsCell>();
    public IReadOnlyDictionary<string, GdsCell> Cells => _cells;
    public bool WasSimplified { get; internal set; }
    public IList<string> Warnings { get; } = new List<string>();

    private readonly Dictionary<string, GdsCell> _cells = new(StringComparer.Ordinal);

    internal void AddCell(GdsCell cell)
    {
        if (string.IsNullOrEmpty(cell.Name))
            throw new GdsFormatException("A structure has no STRNAME record.");
        if (!_cells.TryAdd(cell.Name, cell))
            throw new GdsFormatException($"Duplicate structure name: {cell.Name}");
        CellsInFileOrder.Add(cell);
    }

    public IReadOnlyList<GdsCell> GetTopCells()
    {
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cell in CellsInFileOrder)
        {
            foreach (var element in cell.Elements)
            {
                if (element is GdsReference reference)
                    referenced.Add(reference.CellName);
            }
        }

        var result = CellsInFileOrder.Where(cell => !referenced.Contains(cell.Name)).ToList();
        return result.Count == 0 ? CellsInFileOrder.ToList() : result;
    }
}

public sealed class GdsCell(string name)
{
    public string Name { get; internal set; } = name;
    public IList<GdsElement> Elements { get; } = new List<GdsElement>();
    public BoundsD LocalGeometryBounds { get; internal set; } = BoundsD.Empty;
    public int SourceElementCount { get; internal set; }
    public int SkippedElementCount { get; internal set; }
    internal int StoredGeometryCount { get; set; }
}

public abstract record GdsElement;

public sealed record GdsPolygon(int Layer, int DataType, IReadOnlyList<PointD> Points) : GdsElement;

public sealed record GdsPath(
    int Layer,
    int DataType,
    double Width,
    int PathType,
    IReadOnlyList<PointD> Points) : GdsElement;

public sealed record GdsText(
    int Layer,
    int TextType,
    string Value,
    PointD Origin,
    double Magnification,
    double AngleDegrees,
    bool ReflectXAxis) : GdsElement;

public sealed record GdsReference(
    string CellName,
    PointD Origin,
    double Magnification,
    double AngleDegrees,
    bool ReflectXAxis,
    int Columns = 1,
    int Rows = 1,
    PointD? ColumnPoint = null,
    PointD? RowPoint = null) : GdsElement
{
    public bool IsArray => Columns > 1 || Rows > 1;
}

public sealed class GdsFormatException(string message) : IOException(message);
