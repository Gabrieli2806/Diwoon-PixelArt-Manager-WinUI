using System.IO;

namespace DiwoonPixelArtManager.Models;

public sealed class LibraryItem
{
    public required string Path { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int FrameCount { get; init; }
    public DateTime AddedAt { get; init; } = DateTime.Now;

    /// <summary>The folder this image came in with (the one the user added), or null for images added one by one.</summary>
    public string? Folder { get; set; }

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public bool IsAnimated => FrameCount > 1;
    public string Details => IsAnimated ? $"{Width}×{Height} · {FrameCount} frames" : $"{Width}×{Height}";
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// A folder the user added: its images are listed together and can be played as one slideshow. It can carry a name
/// of its own inside the app; the folder on disk keeps its name.
/// </summary>
public sealed record LibraryFolder(string Path, IReadOnlyList<LibraryItem> Items, string? CustomName = null)
{
    public string Name => CustomName ?? DiskName;

    public string DiskName => System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : Path;
}
