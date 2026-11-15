using System.Runtime.InteropServices.WindowsRuntime;
using DiwoonPixelArtManager.Services;
using Microsoft.UI.Xaml.Media;
using global::Windows.Graphics.Imaging;
using Color = global::Windows.UI.Color;

namespace DiwoonPixelArtManager.Views;

public sealed partial class EditorPage : Page
{
    private const int CanvasViewSize = 512;
    private const int MaxUndo = 100;
    private static readonly TimeSpan LiveInterval = TimeSpan.FromMilliseconds(250);

    // PICO-8, a common 16-color pixel art palette.
    private static readonly string[] Palette =
    [
        "000000", "1D2B53", "7E2553", "008751", "AB5236", "5F574F", "C2C3C7", "FFF1E8",
        "FF004D", "FFA300", "FFEC27", "00E436", "29ADFF", "83769C", "FF77A8", "FFCCAA"
    ];

    public static string SaveFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Diwoon PixelArt");

    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly Stack<byte[]> _undo = new(), _redo = new();
    private readonly DispatcherTimer _liveTimer = new() { Interval = LiveInterval };

    private int _size = 16;
    private byte[] _pixels = new byte[16 * 16 * 3];
    private RgbColor _color = new(0xFF, 0x00, 0x4D);
    private bool _drawing, _erasing, _loaded;
    private (int X, int Y)? _lastCell;

    public EditorPage()
    {
        InitializeComponent();
        foreach (var hex in Palette)
        {
            var swatch = new Button
            {
                Width = 24, Height = 24, Padding = new Thickness(0), CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(ToColor(ParseHex(hex))), Tag = hex
            };
            ToolTipService.SetToolTip(swatch, "#" + hex);
            swatch.Click += (_, _) => SetColor(ParseHex((string)swatch.Tag));
            PaletteGrid.Children.Add(swatch);
        }
        SaveFolderText.Text = $"Saved as PNG in {SaveFolder}";
        _liveTimer.Tick += async (_, _) =>
        {
            _liveTimer.Stop();
            await SendLiveAsync();
        };
        SelectSize(_connection.IsConnected ? _connection.DisplaySize : 16);
        _loaded = true;
        Render();
    }

    /// <summary>Opens an image from the gallery, converted to the canvas size.</summary>
    protected override async void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.Parameter is not string path || string.IsNullOrEmpty(path)) return;
        int size = _connection.IsConnected ? _connection.DisplaySize : _size;
        try
        {
            var art = await PixelImageService.LoadForDisplayAsync(path, size, ScalingMode.Pixel, FitMode.Fit);
            PushUndo();
            SelectSize(size);
            _pixels = (byte[])art.Frames[0].Clone();
            NameBox.Text = Path.GetFileNameWithoutExtension(path) + "-edit";
            Changed();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            ShowMessage(InfoBarSeverity.Error, "Cannot open this image", ex.Message);
        }
    }

    private void SelectSize(int size)
    {
        _loaded = false;
        SizeCombo.SelectedItem = SizeCombo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == size.ToString())
            ?? SizeCombo.Items[0];
        _loaded = true;
        if (size != _size)
        {
            _size = size;
            _pixels = new byte[size * size * 3];
        }
    }

    private static RgbColor ParseHex(string hex) =>
        new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));

    private static Color ToColor(RgbColor c) => Color.FromArgb(255, c.R, c.G, c.B);

    private void SetColor(RgbColor color)
    {
        _color = color;
        ColorSwatch.Background = new SolidColorBrush(ToColor(color));
        ColorPickerControl.Color = ToColor(color);
    }

    private void ColorPicker_ColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        _color = new RgbColor(args.NewColor.R, args.NewColor.G, args.NewColor.B);
        ColorSwatch.Background = new SolidColorBrush(args.NewColor);
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        foreach (var tool in new[] { PencilTool, EraserTool, FillTool, PickerTool })
            tool.IsChecked = ReferenceEquals(tool, sender);
    }

    // ---- Drawing ----

    private (int X, int Y)? CellAt(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(CanvasHost).Position;
        int x = (int)(point.X * _size / CanvasViewSize), y = (int)(point.Y * _size / CanvasViewSize);
        return x >= 0 && y >= 0 && x < _size && y < _size ? (x, y) : null;
    }

    private void Canvas_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CellAt(e) is not { } cell) return;
        var props = e.GetCurrentPoint(CanvasHost).Properties;
        _erasing = props.IsRightButtonPressed || EraserTool.IsChecked == true;

        if (PickerTool.IsChecked == true && !props.IsRightButtonPressed)
        {
            SetColor(Get(cell.X, cell.Y));
            return;
        }
        PushUndo();
        if (FillTool.IsChecked == true && !props.IsRightButtonPressed)
        {
            Fill(cell.X, cell.Y, _color);
            Changed();
            return;
        }
        _drawing = true;
        _lastCell = cell;
        CanvasHost.CapturePointer(e.Pointer);
        Set(cell.X, cell.Y, _erasing ? default : _color);
        Changed();
    }

    private void Canvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_drawing || CellAt(e) is not { } cell || cell == _lastCell) return;
        // Fill the gap between pointer events so fast strokes stay continuous.
        var (x0, y0) = _lastCell ?? cell;
        int dx = Math.Abs(cell.X - x0), dy = -Math.Abs(cell.Y - y0);
        int sx = x0 < cell.X ? 1 : -1, sy = y0 < cell.Y ? 1 : -1, error = dx + dy;
        while (true)
        {
            Set(x0, y0, _erasing ? default : _color);
            if (x0 == cell.X && y0 == cell.Y) break;
            int e2 = 2 * error;
            if (e2 >= dy) { error += dy; x0 += sx; }
            if (e2 <= dx) { error += dx; y0 += sy; }
        }
        _lastCell = cell;
        Changed();
    }

    private void Canvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _drawing = false;
        _lastCell = null;
    }

    private RgbColor Get(int x, int y)
    {
        int i = (y * _size + x) * 3;
        return new RgbColor(_pixels[i], _pixels[i + 1], _pixels[i + 2]);
    }

    private void Set(int x, int y, RgbColor color)
    {
        int i = (y * _size + x) * 3;
        _pixels[i] = color.R;
        _pixels[i + 1] = color.G;
        _pixels[i + 2] = color.B;
    }

    private void Fill(int x, int y, RgbColor color)
    {
        var target = Get(x, y);
        if (target == color) return;
        var pending = new Stack<(int, int)>();
        pending.Push((x, y));
        while (pending.Count > 0)
        {
            var (px, py) = pending.Pop();
            if (px < 0 || py < 0 || px >= _size || py >= _size || Get(px, py) != target) continue;
            Set(px, py, color);
            pending.Push((px + 1, py));
            pending.Push((px - 1, py));
            pending.Push((px, py + 1));
            pending.Push((px, py - 1));
        }
    }

    // ---- History ----

    private void PushUndo()
    {
        _undo.Push((byte[])_pixels.Clone());
        _redo.Clear();
        if (_undo.Count > MaxUndo)
        {
            var kept = _undo.Take(MaxUndo).Reverse().ToList();
            _undo.Clear();
            foreach (var state in kept) _undo.Push(state);
        }
    }

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(_pixels);
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(_pixels);
        Restore(_redo.Pop());
    }

    /// <summary>History entries may come from another canvas size.</summary>
    private void Restore(byte[] pixels)
    {
        int size = (int)Math.Sqrt(pixels.Length / 3);
        if (size != _size) SelectSize(size);
        _pixels = pixels;
        Changed();
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void Undo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Undo();
        args.Handled = true;
    }

    private void Redo_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        Redo();
        args.Handled = true;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        PushUndo();
        _pixels = new byte[_size * _size * 3];
        Changed();
    }

    private void Size_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || SizeCombo.SelectedItem is not ComboBoxItem item) return;
        int size = int.Parse((string)item.Tag);
        if (size == _size) return;
        PushUndo();
        // Keep the drawing: scale it to the new size without blurring.
        var old = _pixels;
        int oldSize = _size;
        _size = size;
        _pixels = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
                Array.Copy(old, ((y * oldSize / size) * oldSize + x * oldSize / size) * 3, _pixels, (y * size + x) * 3, 3);
        Changed();
    }

    private void Grid_Toggled(object sender, RoutedEventArgs e) => Render();

    // ---- Output ----

    private void Changed()
    {
        Render();
        if (LiveToggle.IsOn)
        {
            _liveTimer.Stop();
            _liveTimer.Start();
        }
    }

    /// <summary>Draws every cell as a square of the view, with thin grid lines when the grid is on.</summary>
    private void Render()
    {
        int cell = CanvasViewSize / _size;
        int view = cell * _size;
        bool grid = GridToggle.IsOn && cell >= 6;
        var bgra = new byte[view * view * 4];
        for (int y = 0; y < view; y++)
        {
            for (int x = 0; x < view; x++)
            {
                int src = ((y / cell) * _size + x / cell) * 3;
                int dst = (y * view + x) * 4;
                bool line = grid && (x % cell == 0 || y % cell == 0);
                byte r = _pixels[src], g = _pixels[src + 1], b = _pixels[src + 2];
                if (line) { r = (byte)(r / 2 + 20); g = (byte)(g / 2 + 20); b = (byte)(b / 2 + 28); }
                bgra[dst] = b;
                bgra[dst + 1] = g;
                bgra[dst + 2] = r;
                bgra[dst + 3] = 255;
            }
        }
        CanvasImage.Source = BitmapFactory.Create(new BgraBitmap(view, view, bgra));
    }

    private async Task SendLiveAsync()
    {
        if (!_connection.IsConnected) return;
        try
        {
            await _player.ShowImageAsync(CurrentArt(), null);
        }
        catch (InvalidOperationException ex)
        {
            LiveToggle.IsOn = false;
            ShowMessage(InfoBarSeverity.Error, "Could not send", ex.Message);
        }
    }

    /// <summary>The drawing at the display's size (a 16 px drawing on a 32 px display is doubled, and so on).</summary>
    private DisplayArt CurrentArt()
    {
        int target = _connection.DisplaySize;
        if (target == _size) return new DisplayArt(_size, [(byte[])_pixels.Clone()], [1000]);
        var scaled = new byte[target * target * 3];
        for (int y = 0; y < target; y++)
            for (int x = 0; x < target; x++)
                Array.Copy(_pixels, ((y * _size / target) * _size + x * _size / target) * 3, scaled, (y * target + x) * 3, 3);
        return new DisplayArt(target, [scaled], [1000]);
    }

    private async void Live_Toggled(object sender, RoutedEventArgs e)
    {
        if (!LiveToggle.IsOn) return;
        if (!_connection.IsConnected)
        {
            LiveToggle.IsOn = false;
            ShowMessage(InfoBarSeverity.Warning, "No display connected", "Connect your Divoom display on the Device page first.");
            return;
        }
        await SendLiveAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = string.Join("_", NameBox.Text.Trim().Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (name.Length == 0) name = $"pixelart-{DateTime.Now:yyyyMMdd-HHmmss}";

        Directory.CreateDirectory(SaveFolder);
        var path = Path.Combine(SaveFolder, name + ".png");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(SaveFolder, $"{name}-{i}.png");

        try
        {
            await SavePngAsync(path);
            await LibraryService.Instance.AddAsync([path], SaveFolder);
            ShowMessage(InfoBarSeverity.Success, "Saved", $"{Path.GetFileName(path)} is in the gallery, in the \"{Path.GetFileName(SaveFolder)}\" folder.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(InfoBarSeverity.Error, "Could not save", ex.Message);
        }
    }

    private async Task SavePngAsync(string path)
    {
        var bgra = new byte[_size * _size * 4];
        for (int i = 0, j = 0; i < _pixels.Length; i += 3, j += 4)
        {
            bgra[j] = _pixels[i + 2];
            bgra[j + 1] = _pixels[i + 1];
            bgra[j + 2] = _pixels[i];
            bgra[j + 3] = 255;
        }
        using var file = File.Create(path);
        using var stream = file.AsRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)_size, (uint)_size, 96, 96, bgra);
        await encoder.FlushAsync();
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string text)
    {
        Message.Severity = severity;
        Message.Title = title;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
