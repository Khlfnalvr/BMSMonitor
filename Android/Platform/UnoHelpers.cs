using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace BMSMonitor.Services;

/// <summary>Uno/Android stand-ins for WinRT imaging APIs Uno does not implement.</summary>
internal static class UnoImaging
{
    /// <summary>BGRA8 pixel buffer (from RenderTargetBitmap) → ImageSource.</summary>
    public static ImageSource FromBgra(IBuffer pixels, int width, int height)
    {
        var bytes = new byte[pixels.Length];
        DataReader.FromBuffer(pixels).ReadBytes(bytes);

        var wb = new WriteableBitmap(width, height);
        System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions
            .CopyTo(bytes, 0, wb.PixelBuffer, 0, Math.Min(bytes.Length, (int)wb.PixelBuffer.Capacity));
        wb.Invalidate();
        return wb;
    }
}

/// <summary>File-system locations and picker helpers for Android.</summary>
internal static class UnoFiles
{
    /// <summary>
    /// User-reachable documents folder: Android/data/&lt;app&gt;/files/Documents
    /// (visible over USB and in file managers); falls back to internal storage.
    /// </summary>
    public static string DocumentsFolder
    {
        get
        {
#if __ANDROID__
            try
            {
                var dir = Android.App.Application.Context
                    .GetExternalFilesDir(Android.OS.Environment.DirectoryDocuments);
                if (dir?.AbsolutePath is { Length: > 0 } p) return p;
            }
            catch { }
#endif
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }
    }

    /// <summary>Copy a picked file into the cache dir and return its real path.</summary>
    public static async Task<string> CopyToCacheAsync(StorageFile file)
    {
        if (!string.IsNullOrEmpty(file.Path) && File.Exists(file.Path))
            return file.Path;

        var dir = Path.Combine(Path.GetTempPath(), "playback");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, file.Name);
        await using var src = await file.OpenStreamForReadAsync();
        await using var dst = File.Create(dest);
        await src.CopyToAsync(dst);
        return dest;
    }
}
