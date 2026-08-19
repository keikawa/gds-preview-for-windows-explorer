using System.Runtime.InteropServices;
using System.Text;

namespace GdsPreview.Renderer;

internal sealed class SharedPreview : IDisposable
{
    public const int HeaderSize = 1024;
    public const int Magic = 0x56504447; // "GDPV" in little endian.
    private const int FileMapAllAccess = 0x000F001F;
    private const int StatusOffset = 4;
    private const int WidthOffset = 8;
    private const int HeightOffset = 12;
    private const int StrideOffset = 16;
    private const int MessageOffset = 32;
    private const int MessageCharacters = 256;

    private nint _view;
    public int Width { get; }
    public int Height { get; }
    public int Stride => checked(Width * 4);

    public SharedPreview(nint mappingHandle, int width, int height)
    {
        Width = width;
        Height = height;
        var size = checked((nuint)(HeaderSize + Stride * Height));
        _view = MapViewOfFile(mappingHandle, FileMapAllAccess, 0, 0, size);
        if (_view == 0) throw new InvalidOperationException($"MapViewOfFile failed: {Marshal.GetLastWin32Error()}");

        Marshal.WriteInt32(_view, 0, Magic);
        Marshal.WriteInt32(_view, StatusOffset, 0);
        Marshal.WriteInt32(_view, WidthOffset, Width);
        Marshal.WriteInt32(_view, HeightOffset, Height);
        Marshal.WriteInt32(_view, StrideOffset, Stride);
    }

    public unsafe void CopyPixels(IntPtr source, int sourceStride)
    {
        var destination = (byte*)_view + HeaderSize;
        var sourceBase = (byte*)source;
        var rowBytes = checked((nuint)Stride);
        for (var row = 0; row < Height; row++)
        {
            var sourceRow = sourceStride >= 0
                ? sourceBase + row * sourceStride
                : sourceBase + (Height - 1 - row) * -sourceStride;
            Buffer.MemoryCopy(sourceRow, destination + row * Stride, rowBytes, rowBytes);
        }
    }

    public void MarkReady() => Marshal.WriteInt32(_view, StatusOffset, 1);

    public void MarkError(string message)
    {
        var bytes = Encoding.Unicode.GetBytes(message[..Math.Min(message.Length, MessageCharacters - 1)] + "\0");
        Marshal.Copy(bytes, 0, _view + MessageOffset, bytes.Length);
        Marshal.WriteInt32(_view, StatusOffset, 2);
    }

    public void Dispose()
    {
        if (_view == 0) return;
        UnmapViewOfFile(_view);
        _view = 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint mapping, int access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern bool UnmapViewOfFile(nint address);
}
