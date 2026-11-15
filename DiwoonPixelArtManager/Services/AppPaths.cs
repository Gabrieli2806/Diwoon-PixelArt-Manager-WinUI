using System.IO;

namespace DiwoonPixelArtManager.Services;

public static class AppPaths
{
    public static string DataFolder { get; } = CreateDataFolder();

    private static string CreateDataFolder()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiwoonPixelArtManager");
        Directory.CreateDirectory(folder);
        return folder;
    }

    /// <summary>Writes through a temporary file so a crash never leaves a half-written file behind.</summary>
    public static void WriteAllTextSafely(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
