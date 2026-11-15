namespace DiwoonPixelArtManager.Services;

/// <summary>Square resizing of premultiplied BGRA pixels.</summary>
public static class ImageResizer
{
    /// <summary>
    /// Resizes to <paramref name="size"/> × <paramref name="size"/>. Fit leaves transparent bars, Fill crops, Stretch distorts.
    /// Smooth averages every source pixel under each target pixel; otherwise the nearest pixel is taken.
    /// </summary>
    public static byte[] Resize(byte[] bgra, int width, int height, int size, FitMode fit, bool smooth)
    {
        // Source area that is drawn, and target area it lands on.
        double srcX = 0, srcY = 0, srcW = width, srcH = height;
        double dstX = 0, dstY = 0, dstW = size, dstH = size;
        switch (fit)
        {
            case FitMode.Fit:
                double fitScale = Math.Min((double)size / width, (double)size / height);
                dstW = Math.Max(1, Math.Round(width * fitScale));
                dstH = Math.Max(1, Math.Round(height * fitScale));
                dstX = Math.Floor((size - dstW) / 2);
                dstY = Math.Floor((size - dstH) / 2);
                break;
            case FitMode.Fill:
                double fillScale = Math.Max((double)size / width, (double)size / height);
                srcW = size / fillScale;
                srcH = size / fillScale;
                srcX = (width - srcW) / 2;
                srcY = (height - srcH) / 2;
                break;
        }

        var result = new byte[size * size * 4];
        double stepX = srcW / dstW, stepY = srcH / dstH;
        for (int ty = (int)dstY; ty < (int)(dstY + dstH); ty++)
        {
            double y0 = srcY + (ty - dstY) * stepY;
            for (int tx = (int)dstX; tx < (int)(dstX + dstW); tx++)
            {
                double x0 = srcX + (tx - dstX) * stepX;
                int dst = (ty * size + tx) * 4;
                if (smooth && (stepX > 1 || stepY > 1))
                    Average(bgra, width, height, x0, y0, stepX, stepY, result, dst);
                else
                    Nearest(bgra, width, height, x0 + stepX / 2, y0 + stepY / 2, result, dst);
            }
        }
        return result;
    }

    /// <summary>Drops the alpha channel. Premultiplied pixels already equal the image drawn over black.</summary>
    public static byte[] ToRgbOverBlack(byte[] bgra)
    {
        var rgb = new byte[bgra.Length / 4 * 3];
        for (int i = 0, j = 0; i < bgra.Length; i += 4, j += 3)
        {
            rgb[j] = bgra[i + 2];
            rgb[j + 1] = bgra[i + 1];
            rgb[j + 2] = bgra[i];
        }
        return rgb;
    }

    private static void Nearest(byte[] src, int width, int height, double x, double y, byte[] dst, int dstIndex)
    {
        int sx = Math.Clamp((int)x, 0, width - 1);
        int sy = Math.Clamp((int)y, 0, height - 1);
        Array.Copy(src, (sy * width + sx) * 4, dst, dstIndex, 4);
    }

    private static void Average(byte[] src, int width, int height, double x0, double y0, double w, double h, byte[] dst, int dstIndex)
    {
        int xStart = Math.Clamp((int)x0, 0, width - 1), xEnd = Math.Clamp((int)Math.Ceiling(x0 + w), xStart + 1, width);
        int yStart = Math.Clamp((int)y0, 0, height - 1), yEnd = Math.Clamp((int)Math.Ceiling(y0 + h), yStart + 1, height);
        long b = 0, g = 0, r = 0, a = 0;
        for (int y = yStart; y < yEnd; y++)
        {
            for (int x = xStart; x < xEnd; x++)
            {
                int i = (y * width + x) * 4;
                b += src[i];
                g += src[i + 1];
                r += src[i + 2];
                a += src[i + 3];
            }
        }
        int count = (xEnd - xStart) * (yEnd - yStart);
        dst[dstIndex] = (byte)(b / count);
        dst[dstIndex + 1] = (byte)(g / count);
        dst[dstIndex + 2] = (byte)(r / count);
        dst[dstIndex + 3] = (byte)(a / count);
    }
}
