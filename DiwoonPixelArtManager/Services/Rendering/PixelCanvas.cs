namespace DiwoonPixelArtManager.Services;

/// <summary>A square RGB frame (size × size × 3 bytes) with the few drawing operations the generated screens need.</summary>
public sealed class PixelCanvas(int size)
{
    public int Size { get; } = size;
    public byte[] Pixels { get; } = new byte[size * size * 3];

    public void SetPixel(int x, int y, RgbColor color)
    {
        if (x < 0 || y < 0 || x >= Size || y >= Size) return;
        int i = (y * Size + x) * 3;
        Pixels[i] = color.R;
        Pixels[i + 1] = color.G;
        Pixels[i + 2] = color.B;
    }

    public void FillRect(int x, int y, int width, int height, RgbColor color)
    {
        for (int py = y; py < y + height; py++)
            for (int px = x; px < x + width; px++)
                SetPixel(px, py, color);
    }

    public void Fill(RgbColor color) => FillRect(0, 0, Size, Size, color);

    /// <summary>Horizontal bar filled to <paramref name="percent"/>.</summary>
    public void DrawBar(int x, int y, int width, int height, double percent, RgbColor fill, RgbColor track)
    {
        int filled = (int)Math.Round(width * Math.Clamp(percent, 0, 100) / 100);
        FillRect(x, y, width, height, track);
        FillRect(x, y, filled, height, fill);
    }

    /// <summary>Draws text in the 3×5 font, every font pixel drawn as a <paramref name="scale"/>² square. Returns the width used.</summary>
    public int DrawText(string text, int x, int y, RgbColor color, int scale = 1)
    {
        int start = x;
        foreach (char c in text)
        {
            var glyph = PixelFont.Glyph(c);
            DrawPattern(glyph, x, y, color, scale);
            x += (glyph[0].Length + 1) * scale;
        }
        return x - start - scale;
    }

    public void DrawIcon(string[] icon, int x, int y, RgbColor color, int scale = 1) => DrawPattern(icon, x, y, color, scale);

    private void DrawPattern(string[] rows, int x, int y, RgbColor color, int scale)
    {
        for (int row = 0; row < rows.Length; row++)
            for (int col = 0; col < rows[row].Length; col++)
                if (rows[row][col] == '#')
                    FillRect(x + col * scale, y + row * scale, scale, scale, color);
    }
}

/// <summary>Tiny 3×5 pixel font: digits, A–Z (lowercase is drawn as uppercase) and common symbols.</summary>
public static class PixelFont
{
    public const int Height = 5;

    private static readonly Dictionary<char, string[]> Glyphs = new()
    {
        ['0'] = ["###", "#.#", "#.#", "#.#", "###"],
        ['1'] = [".#.", "##.", ".#.", ".#.", "###"],
        ['2'] = ["###", "..#", "###", "#..", "###"],
        ['3'] = ["###", "..#", ".##", "..#", "###"],
        ['4'] = ["#.#", "#.#", "###", "..#", "..#"],
        ['5'] = ["###", "#..", "###", "..#", "###"],
        ['6'] = ["###", "#..", "###", "#.#", "###"],
        ['7'] = ["###", "..#", "..#", ".#.", ".#."],
        ['8'] = ["###", "#.#", "###", "#.#", "###"],
        ['9'] = ["###", "#.#", "###", "..#", "###"],
        ['A'] = [".#.", "#.#", "###", "#.#", "#.#"],
        ['B'] = ["##.", "#.#", "##.", "#.#", "##."],
        ['C'] = [".##", "#..", "#..", "#..", ".##"],
        ['D'] = ["##.", "#.#", "#.#", "#.#", "##."],
        ['E'] = ["###", "#..", "##.", "#..", "###"],
        ['F'] = ["###", "#..", "##.", "#..", "#.."],
        ['G'] = [".##", "#..", "#.#", "#.#", ".##"],
        ['H'] = ["#.#", "#.#", "###", "#.#", "#.#"],
        ['I'] = ["###", ".#.", ".#.", ".#.", "###"],
        ['J'] = ["..#", "..#", "..#", "#.#", ".#."],
        ['K'] = ["#.#", "#.#", "##.", "#.#", "#.#"],
        ['L'] = ["#..", "#..", "#..", "#..", "###"],
        ['M'] = ["#.#", "###", "###", "#.#", "#.#"],
        ['N'] = ["##.", "#.#", "#.#", "#.#", "#.#"],
        ['O'] = [".#.", "#.#", "#.#", "#.#", ".#."],
        ['P'] = ["##.", "#.#", "##.", "#..", "#.."],
        ['Q'] = [".#.", "#.#", "#.#", "##.", ".##"],
        ['R'] = ["##.", "#.#", "##.", "#.#", "#.#"],
        ['S'] = [".##", "#..", ".#.", "..#", "##."],
        ['T'] = ["###", ".#.", ".#.", ".#.", ".#."],
        ['U'] = ["#.#", "#.#", "#.#", "#.#", "###"],
        ['V'] = ["#.#", "#.#", "#.#", "#.#", ".#."],
        ['W'] = ["#.#", "#.#", "###", "###", "#.#"],
        ['X'] = ["#.#", "#.#", ".#.", "#.#", "#.#"],
        ['Y'] = ["#.#", "#.#", ".#.", ".#.", ".#."],
        ['Z'] = ["###", "..#", ".#.", "#..", "###"],
        ['%'] = ["#.#", "..#", ".#.", "#..", "#.#"],
        ['°'] = ["##", "##", "..", "..", ".."],
        [':'] = [".", "#", ".", "#", "."],
        ['.'] = [".", ".", ".", ".", "#"],
        [','] = [".", ".", ".", "#", "#"],
        ['\''] = ["#", "#", ".", ".", "."],
        ['!'] = ["#", "#", "#", ".", "#"],
        ['?'] = ["###", "..#", ".#.", "...", ".#."],
        ['-'] = ["...", "...", "###", "...", "..."],
        ['+'] = ["...", ".#.", "###", ".#.", "..."],
        ['/'] = ["..#", "..#", ".#.", "#..", "#.."],
        ['('] = [".#", "#.", "#.", "#.", ".#"],
        [')'] = ["#.", ".#", ".#", ".#", "#."],
        [' '] = ["..", "..", "..", "..", ".."]
    };

    public static string[] Glyph(char c)
    {
        if (Glyphs.TryGetValue(char.ToUpperInvariant(c), out var glyph)) return glyph;
        // Accented letters (á, ñ, ü…) are drawn as their base letter.
        var plain = c.ToString().Normalize(System.Text.NormalizationForm.FormD)[0];
        return Glyphs.TryGetValue(char.ToUpperInvariant(plain), out glyph) ? glyph : Glyphs['?'];
    }

    /// <summary>Cuts <paramref name="text"/> so it fits in <paramref name="width"/> pixels.</summary>
    public static string Fit(string text, int width, int scale = 1)
    {
        while (text.Length > 0 && Measure(text, scale) > width)
            text = text[..^1];
        return text.TrimEnd();
    }

    public static int Measure(string text, int scale = 1) =>
        text.Length == 0 ? 0 : (text.Sum(c => Glyph(c)[0].Length + 1) - 1) * scale;
}

/// <summary>7×7 icons for alerts.</summary>
public static class PixelIcons
{
    public const int Size = 7;

    public static readonly string[] Hot = ["..###..", "..#.#..", "..#.#..", "..###..", ".#####.", ".#####.", "..###.."];
    public static readonly string[] BatteryLow = [".......", "######.", "#....##", "##...##", "#....##", "######.", "......."];
    public static readonly string[] Charging = ["....#..", "...##..", "..##...", ".#####.", "...##..", "..##...", "..#...."];
    public static readonly string[] Bell = ["...#...", "..###..", ".#####.", ".#####.", ".#####.", "#######", "...#..."];
    public static readonly string[] Check = [".......", "......#", ".....#.", "#...#..", ".#.#...", "..#....", "......."];
    public static readonly string[] Cross = ["#.....#", ".#...#.", "..#.#..", "...#...", "..#.#..", ".#...#.", "#.....#"];
    public static readonly string[] Music = ["..#####", "..#...#", "..#...#", "..#...#", "###.###", "###.###", "......."];
    public static readonly string[] Info = ["...#...", ".......", "..##...", "...#...", "...#...", "...#...", "..###.."];

    public static string[] ByName(string? name) => name?.ToLowerInvariant() switch
    {
        "hot" or "temp" or "temperature" => Hot,
        "battery" => BatteryLow,
        "charging" or "power" => Charging,
        "check" or "ok" or "success" => Check,
        "cross" or "error" or "fail" => Cross,
        "info" => Info,
        "music" => Music,
        _ => Bell
    };
}
