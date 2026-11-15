namespace DiwoonPixelArtManager.Services;

/// <summary>Reduces an RGB frame to an indexed palette. Frames with few colors keep them exactly; others use median cut.</summary>
public static class ColorQuantizer
{
    public static (int[] Palette, byte[] Indices) Quantize(byte[] rgb, int maxColors)
    {
        int pixelCount = rgb.Length / 3;
        var colors = new int[pixelCount];
        for (int i = 0; i < pixelCount; i++)
            colors[i] = (rgb[i * 3] << 16) | (rgb[i * 3 + 1] << 8) | rgb[i * 3 + 2];

        var unique = colors.Distinct().ToArray();
        int[] palette = unique.Length <= maxColors ? unique : MedianCut(unique, maxColors);

        var lookup = new Dictionary<int, byte>();
        var indices = new byte[pixelCount];
        for (int i = 0; i < pixelCount; i++)
        {
            if (!lookup.TryGetValue(colors[i], out byte index))
            {
                index = (byte)Nearest(palette, colors[i]);
                lookup[colors[i]] = index;
            }
            indices[i] = index;
        }
        return (palette, indices);
    }

    private static int[] MedianCut(int[] colors, int maxColors)
    {
        var boxes = new List<int[]> { colors };
        while (boxes.Count < maxColors)
        {
            int[]? widest = null;
            int widestChannel = 0, widestRange = 0;
            foreach (var box in boxes.Where(b => b.Length > 1))
            {
                for (int shift = 0; shift <= 16; shift += 8)
                {
                    int min = 255, max = 0;
                    foreach (int c in box)
                    {
                        int v = (c >> shift) & 0xFF;
                        if (v < min) min = v;
                        if (v > max) max = v;
                    }
                    if (max - min > widestRange)
                    {
                        widestRange = max - min;
                        widest = box;
                        widestChannel = shift;
                    }
                }
            }
            if (widest is null) break;

            var sorted = widest.OrderBy(c => (c >> widestChannel) & 0xFF).ToArray();
            int half = sorted.Length / 2;
            boxes.Remove(widest);
            boxes.Add(sorted[..half]);
            boxes.Add(sorted[half..]);
        }

        return boxes.Select(box =>
        {
            long r = 0, g = 0, b = 0;
            foreach (int c in box)
            {
                r += (c >> 16) & 0xFF;
                g += (c >> 8) & 0xFF;
                b += c & 0xFF;
            }
            return (int)((r / box.Length) << 16 | (g / box.Length) << 8 | (b / box.Length));
        }).ToArray();
    }

    private static int Nearest(int[] palette, int color)
    {
        int r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < palette.Length; i++)
        {
            int dr = r - ((palette[i] >> 16) & 0xFF), dg = g - ((palette[i] >> 8) & 0xFF), db = b - (palette[i] & 0xFF);
            int distance = 2 * dr * dr + 4 * dg * dg + 3 * db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return best;
    }
}
