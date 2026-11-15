using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using global::Windows.Graphics.Imaging;
using global::Windows.Storage.Streams;

namespace DiwoonPixelArtManager.Services;

/// <summary>An image converted to the display resolution: one RGB buffer (size × size × 3) per animation frame.</summary>
public sealed record DisplayArt(int Size, IReadOnlyList<byte[]> Frames, IReadOnlyList<int> DelaysMs)
{
    public bool IsAnimated => Frames.Count > 1;
}

/// <summary>Premultiplied BGRA pixels, the format WriteableBitmap expects.</summary>
public sealed record BgraBitmap(int Width, int Height, byte[] Pixels);

public sealed record ImageInfo(int Width, int Height, int FrameCount);

/// <summary>Loads images with the Windows Imaging Component and converts them for the display.</summary>
public static class PixelImageService
{
    public static readonly string[] SupportedExtensions = [".png", ".gif", ".jpg", ".jpeg", ".bmp", ".webp", ".tif", ".tiff"];

    private const int DefaultFrameDelayMs = 100;
    private const int MinFrameDelayMs = 20;

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static async Task<ImageInfo> ReadInfoAsync(string path)
    {
        using var stream = File.OpenRead(path).AsRandomAccessStream();
        var decoder = await CreateDecoderAsync(stream);
        var (width, height) = await CanvasSizeAsync(decoder);
        return new ImageInfo(width, height, (int)decoder.FrameCount);
    }

    public static async Task<DisplayArt> LoadForDisplayAsync(string path, int size, ScalingMode scaling, FitMode fit)
    {
        using var stream = File.OpenRead(path).AsRandomAccessStream();
        return await ConvertForDisplayAsync(stream, size, scaling, fit);
    }

    public static async Task<DisplayArt> LoadForDisplayAsync(byte[] fileBytes, int size, ScalingMode scaling, FitMode fit)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(fileBytes.AsBuffer());
        stream.Seek(0);
        return await ConvertForDisplayAsync(stream, size, scaling, fit);
    }

    /// <summary>Square thumbnail of the first frame; small pixel art is enlarged without blurring.</summary>
    public static async Task<BgraBitmap> ThumbnailAsync(string path, int size)
    {
        using var stream = File.OpenRead(path).AsRandomAccessStream();
        var decoder = await CreateDecoderAsync(stream);
        var (width, height) = await CanvasSizeAsync(decoder);
        var first = (await DecodeFramesAsync(decoder, width, height, firstOnly: true))[0].Pixels;
        bool isPixelArt = Math.Max(width, height) <= size;
        var pixels = ImageResizer.Resize(first, width, height, size, FitMode.Fit, smooth: !isPixelArt);
        return new BgraBitmap(size, size, pixels);
    }

    /// <summary>Every frame of <paramref name="art"/> enlarged by an integer factor so each LED stays a sharp square.</summary>
    public static IReadOnlyList<BgraBitmap> Enlarge(DisplayArt art, int targetSize)
    {
        int scale = Math.Max(1, targetSize / art.Size);
        int outSize = art.Size * scale;
        return art.Frames.Select(rgb =>
        {
            var pixels = new byte[outSize * outSize * 4];
            for (int y = 0; y < outSize; y++)
            {
                for (int x = 0; x < outSize; x++)
                {
                    int src = ((y / scale) * art.Size + x / scale) * 3;
                    int dst = (y * outSize + x) * 4;
                    pixels[dst] = rgb[src + 2];
                    pixels[dst + 1] = rgb[src + 1];
                    pixels[dst + 2] = rgb[src];
                    pixels[dst + 3] = 255;
                }
            }
            return new BgraBitmap(outSize, outSize, pixels);
        }).ToList();
    }

    private static async Task<DisplayArt> ConvertForDisplayAsync(IRandomAccessStream stream, int size, ScalingMode scaling, FitMode fit)
    {
        var decoder = await CreateDecoderAsync(stream);
        var (width, height) = await CanvasSizeAsync(decoder);
        var frames = await DecodeFramesAsync(decoder, width, height, firstOnly: false);

        var rgbFrames = frames
            .Select(f => ImageResizer.ToRgbOverBlack(ImageResizer.Resize(f.Pixels, width, height, size, fit, scaling == ScalingMode.Smooth)))
            .ToList();
        var delays = frames.Select(f => f.DelayMs <= 0 ? DefaultFrameDelayMs : Math.Max(f.DelayMs, MinFrameDelayMs)).ToList();
        return new DisplayArt(size, rgbFrames, delays);
    }

    private static async Task<BitmapDecoder> CreateDecoderAsync(IRandomAccessStream stream)
    {
        try
        {
            return await BitmapDecoder.CreateAsync(stream);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            throw new InvalidDataException("This file is not an image format Windows can read.", ex);
        }
    }

    private static bool IsGif(BitmapDecoder decoder) => decoder.DecoderInformation.CodecId == BitmapDecoder.GifDecoderId;

    /// <summary>The full image size; for GIFs this is the logical screen, which frames may only partly cover.</summary>
    private static async Task<(int Width, int Height)> CanvasSizeAsync(BitmapDecoder decoder)
    {
        if (IsGif(decoder))
        {
            var props = await decoder.BitmapProperties.GetPropertiesAsync(["/logscrdesc/Width", "/logscrdesc/Height"]);
            if (props.TryGetValue("/logscrdesc/Width", out var w) && props.TryGetValue("/logscrdesc/Height", out var h))
                return (Convert.ToInt32(w.Value), Convert.ToInt32(h.Value));
        }
        return ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight);
    }

    private sealed record DecodedFrame(byte[] Pixels, int DelayMs);

    /// <summary>Decodes frames as premultiplied BGRA at the canvas size. GIF frames are composited the way a browser plays them.</summary>
    private static async Task<List<DecodedFrame>> DecodeFramesAsync(BitmapDecoder decoder, int width, int height, bool firstOnly)
    {
        if (!IsGif(decoder))
        {
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
            return [new DecodedFrame(data.DetachPixelData(), 0)];
        }

        var canvas = new byte[width * height * 4];
        var result = new List<DecodedFrame>();
        uint count = firstOnly ? 1 : decoder.FrameCount;
        for (uint i = 0; i < count; i++)
        {
            var frame = await decoder.GetFrameAsync(i);
            var props = await frame.BitmapProperties.GetPropertiesAsync(["/imgdesc/Left", "/imgdesc/Top", "/grctlext/Delay", "/grctlext/Disposal"]);
            int left = ReadInt(props, "/imgdesc/Left");
            int top = ReadInt(props, "/imgdesc/Top");
            int delayMs = ReadInt(props, "/grctlext/Delay") * 10;
            int disposal = ReadInt(props, "/grctlext/Disposal");

            int frameWidth = (int)frame.PixelWidth;
            int frameHeight = (int)frame.PixelHeight;
            var data = await frame.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();

            var before = disposal == 3 ? (byte[])canvas.Clone() : null;
            for (int y = 0; y < frameHeight && top + y < height; y++)
            {
                for (int x = 0; x < frameWidth && left + x < width; x++)
                {
                    int src = (y * frameWidth + x) * 4;
                    if (pixels[src + 3] == 0) continue;
                    Array.Copy(pixels, src, canvas, ((top + y) * width + left + x) * 4, 4);
                }
            }
            result.Add(new DecodedFrame((byte[])canvas.Clone(), delayMs));

            if (disposal == 2)
                ClearRect(canvas, width, height, left, top, frameWidth, frameHeight);
            else if (disposal == 3)
                canvas = before!;
        }
        return result;
    }

    private static int ReadInt(BitmapPropertySet props, string key) =>
        props.TryGetValue(key, out var value) && value.Value is not null ? Convert.ToInt32(value.Value) : 0;

    private static void ClearRect(byte[] canvas, int width, int height, int left, int top, int rectWidth, int rectHeight)
    {
        for (int y = top; y < Math.Min(top + rectHeight, height); y++)
        {
            int start = (y * width + left) * 4;
            int length = Math.Max(0, Math.Min(rectWidth, width - left)) * 4;
            Array.Clear(canvas, start, length);
        }
    }
}

public enum ScalingMode
{
    Pixel,
    Smooth
}

public enum FitMode
{
    Fit,
    Fill,
    Stretch
}
