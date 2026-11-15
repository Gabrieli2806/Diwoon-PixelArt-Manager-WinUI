using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace DiwoonPixelArtManager.Services;

/// <summary>An app's icon as image bytes, read from its executable through the Windows shell. Cached per executable.</summary>
public static class AppIconService
{
    private const uint IconSize = 256;
    private static readonly Dictionary<string, byte[]?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static async Task<byte[]?> IconPngAsync(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        lock (Cache)
            if (Cache.TryGetValue(exePath, out var known)) return known;

        byte[]? bytes = null;
        try
        {
            if (File.Exists(exePath))
            {
                var file = await StorageFile.GetFileFromPathAsync(exePath);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, IconSize, ThumbnailOptions.UseCurrentScale);
                if (thumbnail is not null && thumbnail.Size > 0)
                {
                    var buffer = new Windows.Storage.Streams.Buffer((uint)thumbnail.Size);
                    await thumbnail.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
                    bytes = new byte[buffer.Length];
                    using var reader = DataReader.FromBuffer(buffer);
                    reader.ReadBytes(bytes);
                }
            }
        }
        catch (Exception ex)
        {
            // Protected or removed executables have no readable icon; the list shows a placeholder.
            AppLog.Write($"Icon of {exePath} could not be read", ex);
        }

        lock (Cache)
            Cache[exePath] = bytes;
        return bytes;
    }
}
