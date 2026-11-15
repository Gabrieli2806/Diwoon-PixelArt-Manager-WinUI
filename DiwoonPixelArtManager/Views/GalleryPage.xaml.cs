using System.Collections.ObjectModel;
using DiwoonPixelArtManager.Models;
using DiwoonPixelArtManager.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.Windows.Storage.Pickers;
using global::Windows.ApplicationModel.DataTransfer;

namespace DiwoonPixelArtManager.Views;

public sealed partial class GalleryPage : Page
{
    private const int ThumbnailSize = 128;
    private const int PreviewSize = 256;
    private const string RootName = "Gallery";

    private readonly LibraryService _library = LibraryService.Instance;
    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly DisplayPlayer _player = DisplayPlayer.Instance;

    private readonly ObservableCollection<GalleryEntry> _visible = [];
    private readonly ObservableCollection<string> _crumbs = [RootName];
    private readonly DispatcherTimer _animationTimer = new();

    // Thumbnails are decoded only for cards that come into view, a few at a time, and kept for when they come back.
    private readonly Dictionary<string, ImageSource> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _thumbnailSlots = new(4);

    private string? _folder;
    private DisplayArt? _previewArt;
    private IReadOnlyList<ImageSource> _previewFrames = [];
    private int _previewFrame;
    private int _previewVersion;

    public GalleryPage()
    {
        InitializeComponent();
        ArtGrid.ItemsSource = _visible;
        Breadcrumb.ItemsSource = _crumbs;
        _animationTimer.Tick += AnimationTimer_Tick;

        _settings.SettingsChanged += RefreshPreview;
        _connection.ConnectionChanged += () => DispatcherQueue.TryEnqueue(RefreshPreview);
        _player.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateSlideshowButton);
        _library.Changed += Refresh;
        UpdateSlideshowButton();
        Refresh();
    }

    private GalleryEntry? SelectedEntry => ArtGrid.SelectedItems.Count == 1 ? ArtGrid.SelectedItem as GalleryEntry : null;

    // ---------------------------------------------------------------- Listing

    /// <summary>
    /// Fills the grid: folders first, then loose images (top level), or one folder's images, or everything that
    /// matches the search across all folders.
    /// </summary>
    private void Refresh()
    {
        var query = SearchBox.Text.Trim();
        var folders = _library.Folders;
        if (_folder is not null && !folders.Any(f => string.Equals(f.Path, _folder, StringComparison.OrdinalIgnoreCase)))
            _folder = null;

        IEnumerable<GalleryEntry> entries;
        if (query.Length > 0)
        {
            entries = folders.Where(f => f.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).Select(f => new GalleryEntry(f))
                .Concat(_library.Items.Where(i => i.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => new GalleryEntry(i)));
        }
        else if (_folder is not null)
        {
            entries = _library.ItemsIn(_folder).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => new GalleryEntry(i));
        }
        else
        {
            entries = folders.Select(f => new GalleryEntry(f))
                .Concat(_library.Items.Where(i => i.Folder is null).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => new GalleryEntry(i)));
        }

        _visible.Clear();
        foreach (var entry in entries)
        {
            if (entry.Item is not null && _thumbnails.TryGetValue(entry.Item.Path, out var cached)) entry.Thumbnail = cached;
            _visible.Add(entry);
        }

        var current = folders.FirstOrDefault(f => string.Equals(f.Path, _folder, StringComparison.OrdinalIgnoreCase));
        _crumbs.Clear();
        _crumbs.Add(RootName);
        if (current is not null) _crumbs.Add(current.Name);
        BackButton.Visibility = current is null ? Visibility.Collapsed : Visibility.Visible;

        int images = _library.Items.Count;
        CountText.Text = query.Length > 0 ? $"{_visible.Count} results for \"{query}\""
            : current is not null ? $"{Plural(current.Items.Count, "image")} · {current.Path}"
            : $"{Plural(folders.Count, "folder")} · {Plural(images, "image")}";

        bool empty = _visible.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = query.Length > 0 ? "Nothing found" : "Your gallery is empty";
        EmptyText.Text = query.Length > 0 ? "Try another name." : "Add images or a folder, or drop them here. Folders stay grouped.";
        RefreshPreview();
    }

    private static string Plural(int count, string word) => count == 1 ? $"1 {word}" : $"{count} {word}s";

    private void OpenFolder(string? folder)
    {
        _folder = folder;
        SearchBox.Text = "";
        Refresh();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => OpenFolder(null);

    private void Breadcrumb_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Index == 0) OpenFolder(null);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void ArtGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not GalleryEntry entry || entry.ThumbnailRequested || entry.Item is null) return;
        entry.ThumbnailRequested = true;
        if (entry.Thumbnail is null) _ = LoadThumbnailAsync(entry, entry.Item);
    }

    private async Task LoadThumbnailAsync(GalleryEntry entry, LibraryItem item)
    {
        if (_thumbnails.TryGetValue(item.Path, out var cached))
        {
            entry.Thumbnail = cached;
            return;
        }
        await _thumbnailSlots.WaitAsync();
        try
        {
            if (!File.Exists(item.Path)) return;
            var pixels = await Task.Run(() => PixelImageService.ThumbnailAsync(item.Path, ThumbnailSize));
            var bitmap = BitmapFactory.Create(pixels);
            _thumbnails[item.Path] = bitmap;
            entry.Thumbnail = bitmap;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // The file changed or became unreadable after it was added; the card keeps its empty thumbnail.
        }
        finally
        {
            _thumbnailSlots.Release();
        }
    }

    // ---------------------------------------------------------------- Adding and removing

    private async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        ShowMessage(InfoBarSeverity.Informational, "Adding…", "Reading the images.");
        var (added, failed) = await _library.AddAsync(paths);

        if (failed.Count > 0)
            ShowMessage(InfoBarSeverity.Warning, "Some files were skipped",
                $"{failed.Count} file(s) could not be read as images: {string.Join(", ", failed.Take(3).Select(Path.GetFileName))}");
        else if (added == 0)
            ShowMessage(InfoBarSeverity.Informational, "Nothing new", "Those images are already in the gallery.");
        else
            ShowMessage(InfoBarSeverity.Success, "Added", $"{Plural(added, "image")} added.");
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in PixelImageService.SupportedExtensions)
            picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        await AddPathsAsync(files.Select(f => f.Path).ToList());
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(App.MainWindow.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            await AddPathsAsync([folder.Path]);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        var selected = ArtGrid.SelectedItems.Cast<GalleryEntry>().ToList();
        int folders = 0, images = 0;
        foreach (var folder in selected.Where(s => s.IsFolder))
        {
            images += folder.Folder!.Items.Count;
            folders++;
            _library.RemoveFolder(folder.Folder.Path);
        }
        var loose = selected.Where(s => !s.IsFolder && s.Item is not null).Select(s => s.Item!).ToList();
        if (loose.Count > 0)
        {
            images += loose.Count;
            _library.Remove(loose);
        }
        ShowMessage(InfoBarSeverity.Informational, "Removed from the gallery",
            (folders > 0 ? $"{Plural(folders, "folder")}, " : "") + $"{Plural(images, "image")}. The files are still on your disk.");
    }

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "Add to gallery";
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await AddPathsAsync(items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList());
    }

    // ---------------------------------------------------------------- Selection and preview

    private void ArtGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RemoveButton.IsEnabled = ArtGrid.SelectedItems.Count > 0;
        RefreshPreview();
    }

    private async void ArtGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.IsFolder) OpenFolder(entry.Folder!.Path);
        else await SendAsync();
    }

    /// <summary>Converts the selected image (or a folder's cover) to the display size and shows it, animated when it moves.</summary>
    private async void RefreshPreview()
    {
        int version = ++_previewVersion;
        _animationTimer.Stop();
        _previewArt = null;
        _previewFrames = [];
        PreviewImage.Source = null;

        var entry = SelectedEntry;
        bool folder = entry?.IsFolder == true;
        SendText.Text = folder ? "Play folder on display" : "Send to display";
        SendIcon.Glyph = folder ? "" : "";
        EditText.Text = folder ? "Open folder" : "Edit a copy";
        EditIcon.Glyph = folder ? "" : "";
        SendButton.IsEnabled = entry is not null;
        EditButton.IsEnabled = entry is not null;
        RenameButton.Visibility = folder ? Visibility.Visible : Visibility.Collapsed;

        int size = _connection.DisplaySize;
        string settingsText = $"{size}×{size} · {_settings.Settings.Scaling} scaling · {_settings.Settings.Fit}";
        PreviewName.Text = entry?.Name ?? (ArtGrid.SelectedItems.Count > 1 ? $"{ArtGrid.SelectedItems.Count} selected" : "");
        PreviewHint.Visibility = entry?.Item is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewDetails.Text = entry is null ? settingsText : $"{entry.Details}  →  {settingsText}";
        if (entry?.Item is not { } item) return;

        DisplayArt art;
        IReadOnlyList<BgraBitmap> frames;
        try
        {
            var settings = _settings.Settings;
            art = await Task.Run(() => PixelImageService.LoadForDisplayAsync(item.Path, size, settings.Scaling, settings.Fit));
            frames = await Task.Run(() => PixelImageService.Enlarge(art, PreviewSize));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (version == _previewVersion && !folder)
            {
                SendButton.IsEnabled = false;
                ShowMessage(InfoBarSeverity.Error, "Cannot open this image", ex.Message);
            }
            return;
        }

        if (version != _previewVersion) return;
        _previewArt = art;
        _previewFrames = frames.Select(BitmapFactory.Create).ToList();
        _previewFrame = 0;
        PreviewImage.Source = _previewFrames[0];
        if (art.IsAnimated)
        {
            _animationTimer.Interval = TimeSpan.FromMilliseconds(art.DelaysMs[0]);
            _animationTimer.Start();
        }
    }

    private void AnimationTimer_Tick(object? sender, object e)
    {
        if (_previewArt is null || _previewFrames.Count == 0) return;
        _previewFrame = (_previewFrame + 1) % _previewFrames.Count;
        PreviewImage.Source = _previewFrames[_previewFrame];
        _animationTimer.Interval = TimeSpan.FromMilliseconds(_previewArt.DelaysMs[_previewFrame]);
    }

    // ---------------------------------------------------------------- Actions

    /// <summary>Right-click on a card: the same actions as the side panel, for that card.</summary>
    private void ArtGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not GalleryEntry entry) return;
        if (!ArtGrid.SelectedItems.Contains(entry))
        {
            ArtGrid.SelectedItems.Clear();
            ArtGrid.SelectedItem = entry;
        }

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        if (entry.IsFolder)
        {
            Add("Open", "", () => OpenFolder(entry.Folder!.Path));
            Add("Play on display", "", () => StartSlideshow(entry.Folder!.Items, entry.Name));
            Add("Rename…", "", () => _ = RenameAsync(entry.Folder!));
        }
        else if (entry.Item is { } image)
        {
            Add("Send to display", "", () => _ = SendAsync());
            Add("Edit a copy", "", () => (App.MainWindow.Content as MainPage)?.ShowEditor(image.Path));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        Add("Remove from gallery", "", () => Remove_Click(this, new RoutedEventArgs()));
        menu.ShowAt(ArtGrid, e.GetPosition(ArtGrid));
        e.Handled = true;
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is { IsFolder: true } entry) await RenameAsync(entry.Folder!);
    }

    /// <summary>Asks for a new name for a gallery folder. Only the app uses it; the folder on disk is not renamed.</summary>
    private async Task RenameAsync(LibraryFolder folder)
    {
        var box = new TextBox { Text = folder.Name, PlaceholderText = folder.DiskName };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(box);
        content.Children.Add(new TextBlock
        {
            Text = $"Only this app uses the new name. The folder on disk stays \"{folder.DiskName}\".",
            Style = (Style)Application.Current.Resources["CardDescription"]
        });

        var dialog = new ContentDialog
        {
            Title = "Rename folder",
            Content = content,
            PrimaryButtonText = "Rename",
            SecondaryButtonText = folder.CustomName is null ? "" : "Use folder's own name",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary) _library.RenameFolder(folder.Path, box.Text);
        else if (result == ContentDialogResult.Secondary) _library.RenameFolder(folder.Path, null);
    }

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is { IsFolder: true } entry) StartSlideshow(entry.Folder!.Items, entry.Name);
        else await SendAsync();
    }

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedEntry is not { } entry) return;
        if (entry.IsFolder) OpenFolder(entry.Folder!.Path);
        else if (entry.Item is not null) (App.MainWindow.Content as MainPage)?.ShowEditor(entry.Item.Path);
    }

    private async Task SendAsync()
    {
        if (_previewArt is null || SelectedEntry?.Item is not { } item) return;
        if (!_connection.IsConnected)
        {
            ShowNotConnected();
            return;
        }

        SendButton.IsEnabled = false;
        SendProgress.Visibility = Visibility.Visible;
        try
        {
            bool complete = await _player.ShowImageAsync(_previewArt, item.Path);
            if (complete)
                ShowMessage(InfoBarSeverity.Success, "Sent", $"{item.Name} is on the display.");
            else
                ShowMessage(InfoBarSeverity.Warning, "Sent as a still image",
                    "This display does not play animations sent by the app, so the first frame was sent.");
        }
        catch (InvalidOperationException ex)
        {
            ShowMessage(InfoBarSeverity.Error, "Could not send", ex.Message);
        }
        finally
        {
            SendButton.IsEnabled = SelectedEntry is not null;
            SendProgress.Visibility = Visibility.Collapsed;
        }
    }

    private async void Slideshow_Click(object sender, RoutedEventArgs e)
    {
        if (_player.Mode == PlayerMode.Slideshow)
        {
            await _player.StopAsync();
            return;
        }

        // Selected images and folders, or else everything in the current view (the whole gallery at the top level).
        var selected = ArtGrid.SelectedItems.Cast<GalleryEntry>().ToList();
        IEnumerable<LibraryItem> items = selected.Count > 1
            ? selected.SelectMany(s => s.IsFolder ? s.Folder!.Items : new[] { s.Item! })
            : _folder is not null ? _library.ItemsIn(_folder) : _library.Items;
        StartSlideshow(items.ToList(), _folder is not null ? _crumbs[^1] : "the gallery");
    }

    private void StartSlideshow(IReadOnlyList<LibraryItem> items, string source)
    {
        if (!_connection.IsConnected)
        {
            ShowNotConnected();
            return;
        }
        var paths = items.Where(i => i.Exists).OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).Select(i => i.Path).ToList();
        if (paths.Count < 2)
        {
            ShowMessage(InfoBarSeverity.Informational, "Not enough images", "A slideshow needs at least two images.");
            return;
        }
        _player.StartSlideshow(paths);
        ShowMessage(InfoBarSeverity.Success, "Slideshow started",
            $"{Plural(paths.Count, "image")} from {source}, a new one every {_settings.Settings.SlideshowSeconds} s. Change the interval in Settings.");
    }

    private void UpdateSlideshowButton()
    {
        bool running = _player.Mode == PlayerMode.Slideshow;
        SlideshowText.Text = running ? "Stop slideshow" : "Slideshow";
        SlideshowIcon.Glyph = running ? "" : "";
    }

    private void ShowNotConnected()
    {
        var openDevice = new Button { Content = "Connect a display" };
        openDevice.Click += (_, _) => (App.MainWindow.Content as MainPage)?.ShowDevicePage();
        ShowMessage(InfoBarSeverity.Warning, "No display connected", "Connect your Divoom display first.", openDevice);
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text, ButtonBase? action = null)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.ActionButton = action;
        Message.IsOpen = true;
    }
}
