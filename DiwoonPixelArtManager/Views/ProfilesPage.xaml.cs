using DiwoonPixelArtManager.Models;
using DiwoonPixelArtManager.Services;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace DiwoonPixelArtManager.Views;

public sealed partial class ProfilesPage : Page
{
    private static readonly (ProfileScreen Screen, string Label)[] ScreenChoices =
    [
        (ProfileScreen.PcMonitor, "PC monitor"),
        (ProfileScreen.NowPlaying, "Now playing"),
        (ProfileScreen.Timer, "Timer"),
        (ProfileScreen.AppIcon, "The app's icon"),
        (ProfileScreen.Image, "An image…"),
        (ProfileScreen.Folder, "A gallery folder"),
        (ProfileScreen.Clock, "Clock"),
        (ProfileScreen.ScreenOff, "Screen off")
    ];

    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly bool _loaded;

    public ProfilesPage()
    {
        InitializeComponent();
        EnabledToggle.IsOn = _settings.Settings.AppProfilesEnabled;
        _loaded = true;
        _player.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateActive);
        LibraryService.Instance.Changed += BuildList;
        BuildList();
        UpdateActive();
    }

    private List<AppProfile> Profiles => _settings.Settings.AppProfiles;

    private void UpdateActive()
    {
        ActiveText.Text = _player.ActiveProfile is { } active
            ? $"Active now: {active.Name} → {ScreenChoices.First(s => s.Screen == active.Screen).Label}"
            : _settings.Settings.AppProfilesEnabled ? "No profile is active right now" : "Off: the display ignores which app is open";
    }

    private void Enabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.Settings.AppProfilesEnabled = EnabledToggle.IsOn;
        _settings.Save();
        UpdateActive();
    }

    /// <summary>Apps with a window open right now that have no profile yet, plus a way to pick any executable.</summary>
    private void AddAppFlyout_Opening(object? sender, object e)
    {
        AddAppFlyout.Items.Clear();
        var running = DesktopApps.AppWindows()
            .Where(w => !Profiles.Any(p => p.Process.Equals(w.Process, StringComparison.OrdinalIgnoreCase)))
            .GroupBy(w => w.Process, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (running.Count == 0)
            AddAppFlyout.Items.Add(new MenuFlyoutItem { Text = "No other apps with a window are open", IsEnabled = false });
        foreach (var app in running)
        {
            var first = app.First();
            string title = first.Title.Length > 40 ? first.Title[..40] + "…" : first.Title;
            var item = new MenuFlyoutItem { Text = $"{DesktopApps.DisplayName(first.Process, first.ExePath)}  ·  {title}" };
            item.Click += (_, _) => Add(first.Process, first.ExePath);
            AddAppFlyout.Items.Add(item);
        }

        AddAppFlyout.Items.Add(new MenuFlyoutSeparator());
        var browse = new MenuFlyoutItem { Text = "Browse for an app…", Icon = new FontIcon { Glyph = "" } };
        browse.Click += async (_, _) => await BrowseAsync();
        AddAppFlyout.Items.Add(browse);
    }

    private async Task BrowseAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var process = Path.GetFileNameWithoutExtension(file.Path);
        if (Profiles.Any(p => p.Process.Equals(process, StringComparison.OrdinalIgnoreCase)))
        {
            ShowMessage(InfoBarSeverity.Informational, "Already in the list", $"{process} already has a profile.");
            return;
        }
        Add(process, file.Path);
    }

    private void Add(string process, string? exePath)
    {
        Profiles.Add(new AppProfile { Process = process, ExePath = exePath, Name = DesktopApps.DisplayName(process, exePath) });
        Save();
        BuildList();
    }

    private void Save()
    {
        _settings.Save();
        _player.ProfileEdited();
    }

    private void BuildList()
    {
        ProfileList.Children.Clear();
        EmptyState.Visibility = Profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        for (int i = 0; i < Profiles.Count; i++)
            ProfileList.Children.Add(BuildCard(Profiles[i], i));
    }

    private Border BuildCard(AppProfile profile, int index)
    {
        var card = new Border { Style = (Style)Application.Current.Resources["CardStyle"] };
        var layout = new Grid { ColumnSpacing = 12, RowSpacing = 10 };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var icon = new Image { Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Center };
        _ = LoadIconAsync(icon, profile.ExePath);
        layout.Children.Add(icon);

        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = profile.Name, Style = (Style)Application.Current.Resources["CardTitle"], TextTrimming = TextTrimming.CharacterEllipsis });
        names.Children.Add(new TextBlock { Text = profile.Process + ".exe", Style = (Style)Application.Current.Resources["CardDescription"] });
        Grid.SetColumn(names, 1);
        layout.Children.Add(names);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var enabled = new ToggleSwitch { IsOn = profile.Enabled, OnContent = "On", OffContent = "Off", MinWidth = 0 };
        enabled.Toggled += (_, _) => { profile.Enabled = enabled.IsOn; Save(); };
        actions.Children.Add(enabled);
        actions.Children.Add(IconButton("", "Move up", index > 0, () => Move(index, -1)));
        actions.Children.Add(IconButton("", "Move down", index < Profiles.Count - 1, () => Move(index, 1)));
        actions.Children.Add(IconButton("", "Remove", true, () =>
        {
            Profiles.Remove(profile);
            Save();
            BuildList();
        }));
        Grid.SetColumn(actions, 2);
        layout.Children.Add(actions);

        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var trigger = new ComboBox { Header = "When", Width = 200, ItemsSource = new[] { "The app is in front", "The app is running" }, SelectedIndex = (int)profile.Trigger };
        trigger.SelectionChanged += (_, _) => { profile.Trigger = (ProfileTrigger)trigger.SelectedIndex; Save(); };
        options.Children.Add(trigger);

        var screen = new ComboBox { Header = "Show", Width = 200, ItemsSource = ScreenChoices.Select(s => s.Label).ToList() };
        screen.SelectedIndex = Array.FindIndex(ScreenChoices, s => s.Screen == profile.Screen);
        options.Children.Add(screen);

        var folders = LibraryService.Instance.Folders;
        var folder = new ComboBox
        {
            Header = "Folder",
            Width = 220,
            PlaceholderText = folders.Count == 0 ? "Add a folder in the Gallery first" : "Choose a folder",
            ItemsSource = folders.Select(f => $"{f.Name}  ({f.Items.Count})").ToList(),
            SelectedIndex = folders.ToList().FindIndex(f => string.Equals(f.Path, profile.FolderPath, StringComparison.OrdinalIgnoreCase)),
            IsEnabled = folders.Count > 0
        };
        folder.SelectionChanged += (_, _) =>
        {
            if (folder.SelectedIndex < 0) return;
            profile.FolderPath = folders[folder.SelectedIndex].Path;
            Save();
        };

        var image = new Button { Style = (Style)Application.Current.Resources["Action"], VerticalAlignment = VerticalAlignment.Bottom };
        void UpdateImageButton()
        {
            folder.Visibility = profile.Screen == ProfileScreen.Folder ? Visibility.Visible : Visibility.Collapsed;
            image.Visibility = profile.Screen == ProfileScreen.Image ? Visibility.Visible : Visibility.Collapsed;
            image.Content = string.IsNullOrEmpty(profile.ImagePath) ? "Choose image" : Path.GetFileName(profile.ImagePath);
        }
        image.Click += async (_, _) =>
        {
            if (await PickImageAsync() is { } path)
            {
                profile.ImagePath = path;
                Save();
                UpdateImageButton();
            }
        };
        screen.SelectionChanged += async (_, _) =>
        {
            profile.Screen = ScreenChoices[screen.SelectedIndex].Screen;
            if (profile.Screen == ProfileScreen.Image && string.IsNullOrEmpty(profile.ImagePath))
                profile.ImagePath = await PickImageAsync();
            Save();
            UpdateImageButton();
        };
        UpdateImageButton();
        options.Children.Add(image);
        options.Children.Add(folder);

        Grid.SetRow(options, 1);
        Grid.SetColumn(options, 1);
        Grid.SetColumnSpan(options, 2);
        layout.Children.Add(options);

        card.Child = layout;
        return card;
    }

    private static Button IconButton(string glyph, string tip, bool enabled, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = enabled
        };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void Move(int index, int delta)
    {
        var profile = Profiles[index];
        Profiles.RemoveAt(index);
        Profiles.Insert(index + delta, profile);
        Save();
        BuildList();
    }

    /// <summary>Lets the user pick a picture, starting in the app's pixel art folder.</summary>
    private static async Task<string?> PickImageAsync()
    {
        var picker = new FileOpenPicker(App.MainWindow.AppWindow.Id) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var extension in PixelImageService.SupportedExtensions)
            picker.FileTypeFilter.Add(extension);
        return (await picker.PickSingleFileAsync())?.Path;
    }

    private static async Task LoadIconAsync(Image target, string? exePath)
    {
        var bytes = await AppIconService.IconPngAsync(exePath);
        if (bytes is null) return;
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
        }
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        target.Source = bitmap;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
