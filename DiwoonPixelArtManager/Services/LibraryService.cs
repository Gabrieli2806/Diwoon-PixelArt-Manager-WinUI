using System.IO;
using System.Text.Json;
using DiwoonPixelArtManager.Models;

namespace DiwoonPixelArtManager.Services;

/// <summary>
/// The user's pixel art collection. Only the file paths and their sizes are stored; the files stay where they are.
/// Images added as a folder keep that folder, so the gallery can show them together.
/// </summary>
public sealed class LibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    // Declared after JsonOptions: static fields initialize in order, and the constructor reads files with those options.
    public static LibraryService Instance { get; } = new();

    /// <summary>Images read at the same time when adding; reading is mostly waiting on the disk.</summary>
    private const int ReadParallelism = 6;

    private readonly string _path = Path.Combine(AppPaths.DataFolder, "library.json");
    private readonly List<LibraryItem> _items;
    private readonly HashSet<string> _known = new(StringComparer.OrdinalIgnoreCase);

    // Names given to folders inside the app, by folder path. The folders on disk keep their own names.
    private readonly string _namesPath = Path.Combine(AppPaths.DataFolder, "folder-names.json");
    private readonly Dictionary<string, string> _folderNames;

    public IReadOnlyList<LibraryItem> Items => _items;

    /// <summary>Raised on the calling thread after the library changes (from any page).</summary>
    public event Action? Changed;

    private LibraryService()
    {
        _items = Load();
        foreach (var item in _items) _known.Add(item.Path);
        _folderNames = LoadNames();
    }

    private Dictionary<string, string> LoadNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(_namesPath) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_namesPath), JsonOptions) is { } saved)
                foreach (var (folder, name) in saved) names[folder] = name;
        }
        catch (JsonException ex)
        {
            AppLog.Write("Reading folder names failed", ex);
        }
        return names;
    }

    /// <summary>
    /// Gives a gallery folder a name inside the app only; the folder on disk is not renamed.
    /// An empty name goes back to the folder's own name.
    /// </summary>
    public void RenameFolder(string folder, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name) || name == Path.GetFileName(folder))
            _folderNames.Remove(folder);
        else
            _folderNames[folder] = name;
        AppPaths.WriteAllTextSafely(_namesPath, JsonSerializer.Serialize(_folderNames, JsonOptions));
        Changed?.Invoke();
    }

    private List<LibraryItem> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<LibraryItem>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // A damaged library file starts an empty library instead of blocking the start; keep a copy to inspect.
            AppLog.Write("Reading the library failed", ex);
            try { File.Copy(_path, _path + ".bad", overwrite: true); } catch (IOException) { }
        }
        return [];
    }

    private void Save() => AppPaths.WriteAllTextSafely(_path, JsonSerializer.Serialize(_items, JsonOptions));

    /// <summary>The folders the user added, by name.</summary>
    public IReadOnlyList<LibraryFolder> Folders => _items
        .Where(i => i.Folder is not null)
        .GroupBy(i => i.Folder!, StringComparer.OrdinalIgnoreCase)
        .Select(g => new LibraryFolder(g.Key, g.ToList(), _folderNames.GetValueOrDefault(g.Key)))
        .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public IReadOnlyList<LibraryItem> ItemsIn(string folder) =>
        _items.Where(i => string.Equals(i.Folder, folder, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>
    /// Adds image files, and every supported image inside folders (which become gallery folders; adding a folder
    /// again picks up its new images). Returns how many were added and the files that could not be read.
    /// </summary>
    /// <param name="intoFolder">Gallery folder for the loose files given (the editor saves its drawings into one).</param>
    public async Task<(int Added, IReadOnlyList<string> Failed)> AddAsync(IEnumerable<string> paths, string? intoFolder = null)
    {
        var requested = paths.ToList();
        var known = _known.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = await Task.Run(() => requested
            .SelectMany(p => Directory.Exists(p)
                ? SafeEnumerate(p).Select(file => (File: file, Folder: (string?)Path.GetFullPath(p).TrimEnd('\\', '/')))
                : [(File: p, Folder: intoFolder)])
            .Where(c => PixelImageService.IsSupported(c.File))
            .Select(c => (File: Path.GetFullPath(c.File), c.Folder))
            .DistinctBy(c => c.File, StringComparer.OrdinalIgnoreCase)
            .ToList());

        // Images already in the gallery on their own move into the folder they are now added with.
        int moved = 0;
        var byPath = _items.ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var (file, folder) in candidates.Where(c => c.Folder is not null && known.Contains(c.File)))
        {
            if (byPath.TryGetValue(file, out var existing) && existing.Folder is null)
            {
                existing.Folder = folder;
                moved++;
            }
        }
        candidates = candidates.Where(c => !known.Contains(c.File)).ToList();

        var added = new System.Collections.Concurrent.ConcurrentBag<LibraryItem>();
        var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions { MaxDegreeOfParallelism = ReadParallelism }, async (candidate, _) =>
        {
            try
            {
                var info = await PixelImageService.ReadInfoAsync(candidate.File);
                added.Add(new LibraryItem
                {
                    Path = candidate.File, Folder = candidate.Folder,
                    Width = info.Width, Height = info.Height, FrameCount = info.FrameCount
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failed.Add(candidate.File);
            }
        });

        var fresh = added.Where(i => _known.Add(i.Path)).OrderBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToList();
        if (fresh.Count > 0 || moved > 0)
        {
            _items.AddRange(fresh);
            Save();
            Changed?.Invoke();
        }
        return (fresh.Count, failed.ToList());
    }

    private static IEnumerable<string> SafeEnumerate(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Remove(IEnumerable<LibraryItem> items)
    {
        foreach (var item in items.ToList())
        {
            _items.Remove(item);
            _known.Remove(item.Path);
        }
        Save();
        Changed?.Invoke();
    }

    /// <summary>Removes a folder and its images from the gallery (the files stay on disk).</summary>
    public void RemoveFolder(string folder) => Remove(ItemsIn(folder));
}
