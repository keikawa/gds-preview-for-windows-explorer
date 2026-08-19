namespace GdsPreview.Sample;

internal static class Program
{
    private static int Main(string[] args)
    {
        var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.GetFullPath("demo.gds");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        using var stream = File.Create(output);
        DemoGdsWriter.Write(stream);
        Console.WriteLine(output);
        return 0;
    }
}
