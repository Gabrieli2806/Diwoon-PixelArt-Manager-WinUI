namespace DiwoonPixelArtManager.Services;

/// <summary>
/// Animated screens for AI agents: a pulsing speech bubble with "?" when one needs you, a check mark drawing itself
/// with sparkles when it finishes, a shaking cross when it fails. The text under the icon scrolls when it is too long.
/// </summary>
public static class AgentScreens
{
    private const int IconSize = 9;
    private const int Cycle = 16;
    private const int FrameMs = 80;

    private static readonly RgbColor White = new(255, 255, 255);
    private static readonly RgbColor DoneGreen = new(61, 220, 132);
    private static readonly RgbColor ErrorRed = new(255, 77, 77);

    private static readonly string[] Bubble =
    [
        ".#######.",
        "#########",
        "#########",
        "#########",
        "#########",
        "#########",
        ".#######.",
        "..##.....",
        ".#......."
    ];

    private static readonly (int X, int Y)[] CheckStroke = [(1, 4), (2, 5), (3, 6), (4, 5), (5, 4), (6, 3), (7, 2)];
    private static readonly (int X, int Y)[] Sparkles = [(0, 0), (8, 1), (0, 8), (8, 7)];

    public static RgbColor AgentColor(AgentKind agent) => agent switch
    {
        AgentKind.Claude => new RgbColor(217, 119, 87),
        AgentKind.Codex => new RgbColor(16, 190, 140),
        _ => new RgbColor(66, 133, 244)
    };

    public static string AgentName(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "Antigravity"
    };

    public static string Message(AgentKind agent, AgentStatus status, string project)
    {
        var what = status switch
        {
            AgentStatus.Waiting => "needs you",
            AgentStatus.Error => "error",
            _ => "done"
        };
        return project.Length > 0 ? $"{AgentName(agent)} {what} - {project}" : $"{AgentName(agent)} {what}";
    }

    public static DisplayArt Render(AgentKind agent, AgentStatus status, string text, int size, int maxFrames)
    {
        int scale = Math.Max(1, size / 32);
        int iconScale = size switch { >= 64 => 4, >= 32 => 2, _ => 1 };
        int iconY = size switch { >= 64 => 4, >= 32 => 2, _ => 0 };
        int iconX = (size - IconSize * iconScale) / 2;
        int textY = iconY + IconSize * iconScale + (size >= 32 ? 3 * scale : 1);

        var accent = status switch
        {
            AgentStatus.Done => DoneGreen,
            AgentStatus.Error => ErrorRed,
            _ => AgentColor(agent)
        };
        var background = new RgbColor((byte)(accent.R / 8), (byte)(accent.G / 8), (byte)(accent.B / 8));
        var textColor = status == AgentStatus.Waiting ? White : AgentColor(agent);

        text = text.ToUpperInvariant();
        int textWidth = PixelFont.Measure(text, scale);
        bool animated = maxFrames >= 8;
        bool scroll = animated && textWidth > size;

        int frameCount;
        int step = scale;
        if (!animated) frameCount = 1;
        else if (scroll)
        {
            int distance = size + textWidth;
            step = Math.Max(scale, (int)Math.Ceiling((double)distance / maxFrames));
            frameCount = Math.Max(Cycle, (int)Math.Ceiling((double)distance / step));
        }
        else frameCount = Cycle;
        frameCount = Math.Min(frameCount, Math.Max(1, maxFrames));

        var frames = new List<byte[]>(frameCount);
        for (int f = 0; f < frameCount; f++)
        {
            var canvas = new PixelCanvas(size);
            canvas.Fill(background);
            int phase = animated ? f % Cycle : Cycle - 1;
            switch (status)
            {
                case AgentStatus.Waiting: DrawWaiting(canvas, accent, iconX, iconY, iconScale, phase, animated); break;
                case AgentStatus.Error: DrawError(canvas, iconX, iconY, iconScale, phase); break;
                default: DrawDone(canvas, iconX, iconY, iconScale, phase); break;
            }
            int textX = scroll ? size - f * step : Math.Max(0, (size - textWidth) / 2);
            canvas.DrawText(scroll || textWidth <= size ? text : PixelFont.Fit(text, size, scale), textX, textY, textColor, scale);
            frames.Add(canvas.Pixels);
        }
        return new DisplayArt(size, frames, Enumerable.Repeat(FrameMs, frames.Count).ToList());
    }

    /// <summary>The bubble breathes (brightness) and bobs one step up and down; the "?" is cut out of it.</summary>
    private static void DrawWaiting(PixelCanvas canvas, RgbColor color, int x, int y, int scale, int phase, bool animated)
    {
        double pulse = animated ? 0.675 + 0.325 * Math.Cos(2 * Math.PI * phase / Cycle) : 1;
        var shade = new RgbColor((byte)(color.R * pulse), (byte)(color.G * pulse), (byte)(color.B * pulse));
        int bob = animated && phase >= Cycle / 2 ? scale : 0;
        canvas.DrawIcon(Bubble, x, y + bob, shade, scale);
        canvas.DrawIcon(PixelFont.Glyph('?'), x + 3 * scale, y + bob + scale, new RgbColor(0, 0, 0), scale);
    }

    /// <summary>The check draws itself over the first frames, then four sparkles twinkle around it.</summary>
    private static void DrawDone(PixelCanvas canvas, int x, int y, int scale, int phase)
    {
        int drawn = Math.Min(CheckStroke.Length, phase + 1);
        for (int i = 0; i < drawn; i++)
            canvas.FillRect(x + CheckStroke[i].X * scale, y + CheckStroke[i].Y * scale, scale, scale, DoneGreen);
        if (drawn < CheckStroke.Length) return;
        for (int i = 0; i < Sparkles.Length; i++)
            if ((phase + i) % 3 != 0)
                canvas.FillRect(x + Sparkles[i].X * scale, y + Sparkles[i].Y * scale, scale, scale, (phase + i) % 2 == 0 ? White : DoneGreen);
    }

    /// <summary>A cross that shakes sideways for the first half of the cycle.</summary>
    private static void DrawError(PixelCanvas canvas, int x, int y, int scale, int phase)
    {
        int shake = phase < Cycle / 2 ? (phase % 2 == 0 ? scale : -scale) : 0;
        for (int i = 1; i <= 7; i++)
        {
            canvas.FillRect(x + shake + i * scale, y + i * scale, scale, scale, ErrorRed);
            canvas.FillRect(x + shake + (8 - i) * scale, y + i * scale, scale, scale, ErrorRed);
        }
    }
}
