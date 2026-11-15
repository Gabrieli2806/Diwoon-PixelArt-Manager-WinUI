using System.Runtime.InteropServices;
using DiwoonPixelArtManager.Services;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DiwoonPixelArtManager.Views;

/// <summary>What the header of the tray menu says.</summary>
public sealed record TrayStatus(string Glyph, string Title, string Detail, bool Active);

/// <summary>
/// The notification-area menu: a borderless acrylic window by the pointer, with the display's state on top
/// and the actions under it. Closes as soon as it loses focus, like a menu.
/// </summary>
public sealed partial class TrayMenuWindow : Window
{
    private const double MenuWidth = 300;
    private const int EdgeGap = 8;

    private readonly IntPtr _hwnd;
    private DesktopAcrylicController? _acrylic;
    private SystemBackdropConfiguration? _backdropConfig;

    public TrayMenuWindow(TrayStatus status, IReadOnlyList<TrayMenuItem> items)
    {
        InitializeComponent();
        Title = "Diwoon PixelArt Manager menu";
        _hwnd = WindowNative.GetWindowHandle(this);

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
        Root.Width = MenuWidth;
        Root.RequestedTheme = SettingsService.Instance.Settings.Theme switch
        {
            "Dark" => ElementTheme.Dark,
            "Light" => ElementTheme.Light,
            _ => ElementTheme.Default
        };
        StripFrame(_hwnd);
        SetUpBackdrop();

        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) Close();
        };
        Closed += (_, _) =>
        {
            _acrylic?.Dispose();
            _acrylic = null;
            _backdropConfig = null;
        };

        ShowStatus(status);
        foreach (var item in items)
            Items.Children.Add(item.Text is null ? Separator() : ItemButton(item));
    }

    private void ShowStatus(TrayStatus status)
    {
        var tint = (Brush)Application.Current.Resources[status.Active ? "AccentBrush" : "TextFillColorSecondaryBrush"];
        StatusGlyph.Glyph = status.Glyph;
        StatusGlyph.Foreground = tint;
        StatusHalo.Fill = tint;
        StatusTitle.Text = status.Title;
        StatusDetail.Text = status.Detail;
    }

    private static Border Separator() => new()
    {
        Height = 1,
        Margin = new Thickness(0, 6, 0, 6),
        Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"]
    };

    private Button ItemButton(TrayMenuItem item)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = item.Glyph ?? "", FontSize = 16, Opacity = item.Glyph is null ? 0 : 1 });

        var text = new TextBlock { Text = item.Text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (item.Checked || item.Detail is not null)
        {
            UIElement trailing = item.Checked
                ? new FontIcon { Glyph = "", FontSize = 12, Foreground = (Brush)Application.Current.Resources["AccentBrush"] }
                : new TextBlock
                {
                    Text = item.Detail,
                    VerticalAlignment = VerticalAlignment.Center,
                    Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                    Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
                };
            Grid.SetColumn((FrameworkElement)trailing, 2);
            row.Children.Add(trailing);
        }

        var button = new Button { Style = (Style)Application.Current.Resources["TrayMenuItemStyle"], Content = row, IsEnabled = item.Action is not null };
        button.Click += (_, _) =>
        {
            Close();
            item.Action?.Invoke();
        };
        return button;
    }

    /// <summary>Places the menu by the pointer, above it when there is room, and shows it.</summary>
    public void ShowNear(int cursorX, int cursorY)
    {
        Activate();
        Root.UpdateLayout();
        Root.Measure(new global::Windows.Foundation.Size(MenuWidth, double.PositiveInfinity));

        double scale = Math.Max(1, GetDpiForWindow(_hwnd) / 96.0);
        int width = (int)Math.Ceiling(MenuWidth * scale);
        int height = (int)Math.Ceiling(Math.Max(Root.DesiredSize.Height, 120) * scale);
        AppWindow.Resize(new SizeInt32(width, height));

        var work = DisplayArea.GetFromPoint(new PointInt32(cursorX, cursorY), DisplayAreaFallback.Nearest).WorkArea;
        int x = Math.Clamp(cursorX - width / 2, work.X + EdgeGap, Math.Max(work.X + EdgeGap, work.X + work.Width - width - EdgeGap));
        int y = cursorY - height - EdgeGap;
        if (y < work.Y + EdgeGap) y = Math.Min(cursorY + EdgeGap, work.Y + work.Height - height - EdgeGap);
        AppWindow.Move(new PointInt32(x, Math.Max(y, work.Y + EdgeGap)));
        SetForegroundWindow(_hwnd);
    }

    private void SetUpBackdrop()
    {
        if (!DesktopAcrylicController.IsSupported()) return;
        try
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SettingsService.Instance.Settings.Theme switch
                {
                    "Dark" => SystemBackdropTheme.Dark,
                    "Light" => SystemBackdropTheme.Light,
                    _ => SystemBackdropTheme.Default
                }
            };
            _acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _acrylic.SetSystemBackdropConfiguration(_backdropConfig);
            _acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
        }
        catch (Exception ex)
        {
            AppLog.Write("Tray menu backdrop", ex);
            _acrylic = null;
        }
    }

    /// <summary>
    /// A borderless presenter still leaves the frame styles on the window, and they are drawn as a thin light
    /// rectangle; this removes them and keeps the rounded corners.
    /// </summary>
    private static void StripFrame(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, -16).ToInt64() & ~(0x00C00000L | 0x00040000L | 0x00800000L | 0x00400000L | 0x00080000L);
        SetWindowLongPtr(hwnd, -16, (IntPtr)style);
        long extended = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, (IntPtr)((extended & ~(0x100L | 0x200L | 0x20000L | 0x40000L)) | 0x80L));
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x37);
        int round = 2;
        DwmSetWindowAttribute(hwnd, 33, ref round, 4);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
