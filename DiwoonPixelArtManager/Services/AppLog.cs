using System.IO;

namespace DiwoonPixelArtManager.Services;

/// <summary>Plain-text log in the data folder (app.log), kept small, to diagnose connection and sensor problems.</summary>
public static class AppLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly string LogPath = Path.Combine(AppPaths.DataFolder, "app.log");
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
                // Logging must never break the app.
            }
        }
    }

    public static void Write(string context, Exception ex) => Write($"{context}: {ex.GetType().Name}: {ex.Message}");
}
