namespace DiwoonPixelArtManager.Services;

/// <summary>A message shown on the display for a few seconds.</summary>
public sealed record DisplayAlert(string Text, string[] Icon, RgbColor Color);

/// <summary>Draws the generated screens: PC monitor and alerts, at 16, 32 or 64 LEDs.</summary>
public static class Screens
{
    private static readonly RgbColor Black = new(0, 0, 0);
    private static readonly RgbColor White = new(255, 255, 255);
    private static readonly RgbColor Label = new(179, 157, 255);
    private static readonly RgbColor Track = new(36, 28, 56);
    private static readonly RgbColor Green = new(61, 220, 132);
    private static readonly RgbColor Amber = new(255, 176, 32);
    private static readonly RgbColor Red = new(255, 77, 77);

    public static readonly RgbColor AlertRed = Red;
    public static readonly RgbColor AlertAmber = Amber;
    public static readonly RgbColor AlertGreen = Green;
    public static readonly RgbColor AlertBlue = new(80, 160, 255);

    public static RgbColor TemperatureColor(double celsius) => celsius < 60 ? Green : celsius < 80 ? Amber : Red;
    private static RgbColor LoadColor(double percent) => percent < 50 ? Green : percent < 80 ? Amber : Red;

    /// <summary>CPU and GPU temperature with load bars; 32 and 64 LED displays also get memory and the time.</summary>
    public static byte[] Monitor(SystemSample sample, int size)
    {
        var canvas = new PixelCanvas(size);
        canvas.Fill(Black);
        if (size < 32)
        {
            MonitorRow(canvas, 0, 8, 1, "C", sample.CpuTemperature, sample.CpuLoad);
            MonitorRow(canvas, 8, 8, 1, "G", sample.GpuTemperature, sample.GpuLoad);
            return canvas.Pixels;
        }

        int scale = size / 32;
        int rowHeight = size / 4;
        MonitorRow(canvas, 0, rowHeight, scale, "CPU", sample.CpuTemperature, sample.CpuLoad);
        MonitorRow(canvas, rowHeight, rowHeight, scale, "GPU", sample.GpuTemperature, sample.GpuLoad);
        MonitorRow(canvas, rowHeight * 2, rowHeight, scale, "RAM", null, sample.RamLoad);

        var time = DateTime.Now.ToString("HH:mm");
        canvas.DrawText(time, (size - PixelFont.Measure(time, scale)) / 2, rowHeight * 3 + scale, White, scale);
        return canvas.Pixels;
    }

    /// <summary>One row: label, temperature (or load when there is no temperature) and a load bar under it.</summary>
    private static void MonitorRow(PixelCanvas canvas, int y, int height, int scale, string label, double? temperature, double? load)
    {
        canvas.DrawText(label, 0, y, Label, scale);

        string value;
        RgbColor color;
        if (temperature is { } t)
        {
            value = $"{t:0}°";
            color = TemperatureColor(t);
        }
        else if (load is { } l)
        {
            value = $"{l:0}%";
            color = LoadColor(l);
        }
        else
        {
            value = "--";
            color = Label;
        }
        canvas.DrawText(value, canvas.Size - PixelFont.Measure(value, scale), y, color, scale);

        if (load is { } bar)
            canvas.DrawBar(0, y + height - 2 * scale, canvas.Size, scale, bar, LoadColor(bar), Track);
    }

    /// <summary>
    /// Cover art filling the display (or a note when there is none) with a progress bar along the bottom;
    /// 32 and 64 LED displays also get the title on a dark strip.
    /// </summary>
    public static byte[] NowPlaying(NowPlayingInfo? info, byte[]? coverRgb, int size)
    {
        var canvas = new PixelCanvas(size);
        canvas.Fill(Black);
        int scale = Math.Max(1, size / 32);
        if (coverRgb is not null && coverRgb.Length == canvas.Pixels.Length)
            coverRgb.CopyTo(canvas.Pixels, 0);
        else
        {
            int iconScale = Math.Max(1, size / 16);
            int iconSize = PixelIcons.Size * iconScale;
            canvas.DrawIcon(PixelIcons.Music, (size - iconSize) / 2, (size - iconSize) / 2 - (size >= 32 ? 3 * scale : 1), Label, iconScale);
        }
        if (info is null) return canvas.Pixels;

        int barHeight = scale;
        if (size >= 32)
        {
            int stripHeight = 7 * scale + barHeight;
            Darken(canvas, size - stripHeight, stripHeight);
            var title = PixelFont.Fit(info.Title, size - 2 * scale, scale);
            canvas.DrawText(title, scale, size - stripHeight + scale, White, scale);
        }
        var fill = info.IsPlaying ? White : Amber;
        canvas.DrawBar(0, size - barHeight, size, barHeight, info.Progress * 100, fill, Track);
        return canvas.Pixels;
    }

    private static void Darken(PixelCanvas canvas, int y, int height)
    {
        for (int i = y * canvas.Size * 3; i < Math.Min(canvas.Pixels.Length, (y + height) * canvas.Size * 3); i++)
            canvas.Pixels[i] = (byte)(canvas.Pixels[i] / 4);
    }

    public static RgbColor PhaseColor(TimerPhase phase) => phase switch
    {
        TimerPhase.Focus => new RgbColor(255, 99, 71),
        TimerPhase.ShortBreak or TimerPhase.LongBreak => Green,
        _ => AlertBlue
    };

    /// <summary>Minutes over seconds in the phase color, with the time left as a bar along the bottom.</summary>
    public static byte[] Timer(TimeSpan remaining, double progress, TimerPhase phase, bool paused, int size)
    {
        var canvas = new PixelCanvas(size);
        canvas.Fill(Black);
        int scale = size >= 32 ? size / 16 : 1;
        int top = size >= 32 ? 2 * scale : 1;
        int second = top + 6 * scale + (size >= 32 ? scale : 1);
        var color = paused ? Label : PhaseColor(phase);

        int totalSeconds = (int)Math.Ceiling(remaining.TotalSeconds);
        string minutes = Math.Min(99, totalSeconds / 60).ToString("00");
        string seconds = (totalSeconds % 60).ToString("00");
        canvas.DrawText(minutes, (size - PixelFont.Measure(minutes, scale)) / 2, top, color, scale);
        canvas.DrawText(seconds, (size - PixelFont.Measure(seconds, scale)) / 2, second, White, scale);

        int barHeight = size >= 32 ? scale : 1;
        canvas.DrawBar(0, size - barHeight, size, barHeight, (1 - progress) * 100, color, Track);
        return canvas.Pixels;
    }

    /// <summary>
    /// Icon on a tinted background with the text under it. Text wider than the display scrolls when the display
    /// accepts at least that many animation frames; otherwise it is cut.
    /// </summary>
    public static DisplayArt Alert(DisplayAlert alert, int size, int maxFrames)
    {
        int scale = Math.Max(1, size / 32);
        int iconScale = size switch { >= 64 => 4, >= 32 => 2, _ => 1 };
        int iconSize = PixelIcons.Size * iconScale;
        int iconY = size switch { >= 64 => 6, >= 32 => 3, _ => 1 };
        int textY = iconY + iconSize + (size >= 32 ? 3 * scale : 2);
        var background = new RgbColor((byte)(alert.Color.R / 7), (byte)(alert.Color.G / 7), (byte)(alert.Color.B / 7));

        string text = alert.Text.ToUpperInvariant();
        int textWidth = PixelFont.Measure(text, scale);

        byte[] Frame(int textX)
        {
            var canvas = new PixelCanvas(size);
            canvas.Fill(background);
            canvas.DrawIcon(alert.Icon, (size - iconSize) / 2, iconY, alert.Color, iconScale);
            canvas.DrawText(text, textX, textY, White, scale);
            return canvas.Pixels;
        }

        if (textWidth <= size || maxFrames < 8)
            return new DisplayArt(size, [Frame(Math.Max(0, (size - textWidth) / 2))], [1000]);

        // Scroll from the right edge until the text has left on the left.
        int distance = size + textWidth;
        int step = Math.Max(scale, (int)Math.Ceiling((double)distance / maxFrames));
        var frames = new List<byte[]>();
        for (int x = size; x > -textWidth; x -= step)
            frames.Add(Frame(x));
        return new DisplayArt(size, frames, Enumerable.Repeat(70, frames.Count).ToList());
    }
}
