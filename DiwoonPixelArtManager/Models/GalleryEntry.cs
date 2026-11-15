using System.ComponentModel;
using Microsoft.UI.Xaml.Media;

namespace DiwoonPixelArtManager.Models;

/// <summary>A card in the gallery grid: an image, or a folder of images. The thumbnail arrives after the card is shown.</summary>
public sealed class GalleryEntry : INotifyPropertyChanged
{
    public GalleryEntry(LibraryItem item)
    {
        Item = item;
        Name = item.Name;
        Details = item.Exists ? item.Details : "File not found";
        Tooltip = item.Path;
    }

    public GalleryEntry(LibraryFolder folder)
    {
        Folder = folder;
        Name = folder.Name;
        Details = folder.Items.Count == 1 ? "1 image" : $"{folder.Items.Count} images";
        Tooltip = folder.Path;
        // The folder's cover is its first image.
        Item = folder.Items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).FirstOrDefault();
    }

    /// <summary>The image, or for a folder the image used as its cover (null for an empty folder).</summary>
    public LibraryItem? Item { get; }
    public LibraryFolder? Folder { get; }
    public bool IsFolder => Folder is not null;

    public string Name { get; }
    public string Details { get; }
    public string Tooltip { get; }

    public Visibility AnimatedBadgeVisibility => !IsFolder && Item?.IsAnimated == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FolderBadgeVisibility => IsFolder ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Folders get a second card edge behind them, like a stack of pictures.</summary>
    public Visibility StackVisibility => FolderBadgeVisibility;

    public ImageSource? Thumbnail
    {
        get;
        set
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool ThumbnailRequested { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
