using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DiwoonPixelArtManager.Services;

public static class BitmapFactory
{
    /// <summary>Creates the UI bitmap. Call on the UI thread.</summary>
    public static WriteableBitmap Create(BgraBitmap source)
    {
        var bitmap = new WriteableBitmap(source.Width, source.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(source.Pixels, 0, source.Pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }
}
