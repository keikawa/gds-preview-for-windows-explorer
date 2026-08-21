using System.Drawing.Imaging;
using GdsPreview.Core;

namespace GdsPreview.Renderer;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        SharedPreview? shared = null;
        try
        {
            var options = ParseArguments(args);
            shared = new SharedPreview(options.MappingHandle, options.Width, options.Height);
            var document = GdsParser.ParseFile(options.FilePath);
            using var bitmap = HierarchicalBitmapRenderer.Render(document, options.Width, options.Height);
            var rectangle = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try { shared.CopyPixels(data.Scan0, data.Stride); }
            finally { bitmap.UnlockBits(data); }
            shared.MarkReady();
            return 0;
        }
        catch (Exception exception)
        {
            shared?.MarkError(FormatError(exception));
            return 1;
        }
        finally
        {
            shared?.Dispose();
        }
    }

    private static Options ParseArguments(string[] args)
    {
        nint mapping = 0;
        var width = 0;
        var height = 0;
        string? file = null;
        for (var index = 0; index + 1 < args.Length; index += 2)
        {
            switch (args[index])
            {
                case "--mapping": mapping = unchecked((nint)ulong.Parse(args[index + 1])); break;
                case "--width": width = int.Parse(args[index + 1]); break;
                case "--height": height = int.Parse(args[index + 1]); break;
                case "--file": file = args[index + 1]; break;
            }
        }
        if (mapping == 0 || width < 1 || height < 1 || string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("Invalid renderer arguments.");
        return new Options(mapping, width, height, file);
    }

    private static string FormatError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "The GDSII file cannot be read due to its permissions.",
        GdsFormatException => $"Invalid GDSII file.\r\n{exception.Message}",
        IOException => $"The GDSII file cannot be read.\r\n{exception.Message}",
        _ => $"Preview rendering failed.\r\n{exception.Message}"
    };

    private sealed record Options(nint MappingHandle, int Width, int Height, string FilePath);
}
