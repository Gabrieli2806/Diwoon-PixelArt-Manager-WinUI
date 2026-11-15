using DiwoonPixelArtManager.Services;

namespace DiwoonPixelArtManager.Views;

public sealed partial class WidgetsPage : Page
{
    private const int PreviewSize = 192;

    private readonly DisplayPlayer _player = DisplayPlayer.Instance;
    private readonly TimerService _timer = TimerService.Instance;
    private readonly DivoomConnection _connection = DivoomConnection.Instance;
    private readonly SettingsService _settings = SettingsService.Instance;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DispatcherTimer _agentPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private List<Microsoft.UI.Xaml.Media.ImageSource> _agentFrames = [];
    private int _agentFrame;
    private AgentStatus _previewStatus = AgentStatus.Waiting;
    private readonly bool _loaded;

    public WidgetsPage()
    {
        InitializeComponent();
        var s = _settings.Settings;
        AnnounceToggle.IsOn = s.AnnounceTracks;
        TimerMinutesBox.Value = s.TimerMinutes;
        FocusBox.Value = s.FocusMinutes;
        BreakBox.Value = s.BreakMinutes;
        LongBreakBox.Value = s.LongBreakMinutes;
        AgentsToggle.IsOn = s.AgentAlertsEnabled;
        AgentWaitingCheck.IsChecked = s.AgentWaitingEnabled;
        AgentDoneCheck.IsChecked = s.AgentDoneEnabled;
        AgentErrorCheck.IsChecked = s.AgentErrorEnabled;
        ClaudeCheck.IsChecked = !s.AgentsMuted.Contains(AgentKind.Claude);
        CodexCheck.IsChecked = !s.AgentsMuted.Contains(AgentKind.Codex);
        AntigravityCheck.IsChecked = !s.AgentsMuted.Contains(AgentKind.Antigravity);
        _loaded = true;

        _agentPreviewTimer.Tick += (_, _) =>
        {
            if (_agentFrames.Count == 0) return;
            _agentFrame = (_agentFrame + 1) % _agentFrames.Count;
            AgentPreview.Source = _agentFrames[_agentFrame];
        };
        AgentWatcher.Instance.Updated += () => DispatcherQueue.TryEnqueue(UpdateAgentList);
        BuildAgentPreview();

        _player.StateChanged += () => DispatcherQueue.TryEnqueue(UpdateToggles);
        _refresh.Tick += async (_, _) => await RefreshAsync();
        Loaded += (_, _) => { _refresh.Start(); _agentPreviewTimer.Start(); };
        Unloaded += (_, _) => { _refresh.Stop(); _agentPreviewTimer.Stop(); };
        UpdateToggles();
    }

    private async Task RefreshAsync()
    {
        int size = _connection.DisplaySize;

        var info = NowPlayingService.Instance.Current;
        TrackTitle.Text = info?.Title ?? "Nothing playing";
        TrackArtist.Text = info is null
            ? "Play something in Spotify, a browser or any player that shows in the Windows volume panel."
            : $"{info.Artist}{(info.IsPlaying ? "" : " · paused")}";
        var (_, music) = await _player.RenderNowPlayingAsync(size);
        MusicPreview.Source = Preview(music, size);

        int seconds = (int)Math.Ceiling(_timer.Remaining.TotalSeconds);
        TimerText.Text = $"{seconds / 60:00}:{seconds % 60:00}";
        TimerPhaseText.Text = !_timer.IsActive ? "Not running"
            : $"{PhaseName(_timer.Phase)}{(_timer.IsPaused ? " · paused" : "")}{(_timer.IsPomodoro ? $" · {_timer.FocusesDone} focus sessions done" : "")}";
        PauseButton.Content = _timer.IsPaused ? "Resume" : "Pause";
        PauseButton.IsEnabled = StopButton.IsEnabled = _timer.IsActive;
        SkipButton.IsEnabled = _timer.IsActive && _timer.IsPomodoro;
        TimerPreview.Source = Preview(DisplayPlayer.RenderTimer(size).Pixels, size);
    }

    /// <summary>Plays the chosen agent animation in the page, the same frames the display gets.</summary>
    private void BuildAgentPreview()
    {
        int size = _connection.DisplaySize;
        var agent = _previewStatus == AgentStatus.Waiting ? AgentKind.Claude : AgentKind.Codex;
        var art = AgentScreens.Render(agent, _previewStatus, AgentScreens.Message(agent, _previewStatus, "my-project"), size, size <= 16 ? 48 : 59);
        _agentFrames = PixelImageService.Enlarge(art, PreviewSize).Select(f => (Microsoft.UI.Xaml.Media.ImageSource)BitmapFactory.Create(f)).ToList();
        _agentFrame = 0;
        AgentPreview.Source = _agentFrames[0];
    }

    private void UpdateAgentList()
    {
        AgentList.Children.Clear();
        var sessions = AgentWatcher.Instance.Snapshot.Take(6).ToList();
        if (sessions.Count == 0)
        {
            AgentList.Children.Add(new TextBlock { Text = "No agent active in the last minutes.", Style = (Style)Application.Current.Resources["CardDescription"] });
            return;
        }
        foreach (var session in sessions)
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var color = AgentScreens.AgentColor(session.Agent);
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center,
                Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(global::Windows.UI.Color.FromArgb(255, color.R, color.G, color.B))
            });
            var label = new TextBlock
            {
                Text = $"{AgentScreens.AgentName(session.Agent)} · {(session.Project.Length > 0 ? session.Project : "—")}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            ToolTipService.SetToolTip(label, session.Prompt);
            Grid.SetColumn(label, 1);
            row.Children.Add(label);
            var state = new TextBlock
            {
                Text = session.Status switch
                {
                    AgentStatus.Waiting => "Needs you",
                    AgentStatus.Working => "Working",
                    AgentStatus.Error => "Error",
                    _ => "Done"
                },
                Style = (Style)Application.Current.Resources["CardDescription"],
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 2);
            row.Children.Add(state);
            AgentList.Children.Add(row);
        }
    }

    private void AgentSetting_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var s = _settings.Settings;
        s.AgentAlertsEnabled = AgentsToggle.IsOn;
        s.AgentWaitingEnabled = AgentWaitingCheck.IsChecked == true;
        s.AgentDoneEnabled = AgentDoneCheck.IsChecked == true;
        s.AgentErrorEnabled = AgentErrorCheck.IsChecked == true;
        s.AgentsMuted.Clear();
        if (ClaudeCheck.IsChecked != true) s.AgentsMuted.Add(AgentKind.Claude);
        if (CodexCheck.IsChecked != true) s.AgentsMuted.Add(AgentKind.Codex);
        if (AntigravityCheck.IsChecked != true) s.AgentsMuted.Add(AgentKind.Antigravity);
        _settings.Save();
    }

    private async Task TestAgentAsync(AgentStatus status)
    {
        _previewStatus = status;
        BuildAgentPreview();
        if (!EnsureConnected()) return;
        var agent = status == AgentStatus.Waiting ? AgentKind.Claude : AgentKind.Codex;
        await AgentAlertService.Instance.ShowAsync(agent, status, "my-project");
    }

    private async void TestWaiting_Click(object sender, RoutedEventArgs e) => await TestAgentAsync(AgentStatus.Waiting);
    private async void TestDone_Click(object sender, RoutedEventArgs e) => await TestAgentAsync(AgentStatus.Done);
    private async void TestError_Click(object sender, RoutedEventArgs e) => await TestAgentAsync(AgentStatus.Error);

    private static string PhaseName(TimerPhase phase) => phase switch
    {
        TimerPhase.Focus => "Focus",
        TimerPhase.ShortBreak => "Short break",
        TimerPhase.LongBreak => "Long break",
        _ => "Timer"
    };

    private static Microsoft.UI.Xaml.Media.ImageSource Preview(byte[] pixels, int size) =>
        BitmapFactory.Create(PixelImageService.Enlarge(new DisplayArt(size, [pixels], [1000]), PreviewSize)[0]);

    private void UpdateToggles()
    {
        bool music = _player.Mode == PlayerMode.NowPlaying;
        if (MusicToggle.IsOn != music) MusicToggle.IsOn = music;
    }

    private bool EnsureConnected()
    {
        if (_connection.IsConnected) return true;
        Message.Severity = InfoBarSeverity.Warning;
        Message.Title = "No display connected";
        Message.Message = "Connect your Divoom display on the Device page first.";
        Message.IsOpen = true;
        return false;
    }

    private async void MusicToggle_Toggled(object sender, RoutedEventArgs e)
    {
        bool running = _player.Mode == PlayerMode.NowPlaying;
        if (MusicToggle.IsOn == running) return;
        if (!MusicToggle.IsOn)
        {
            await _player.StopAsync();
            return;
        }
        if (!EnsureConnected())
        {
            MusicToggle.IsOn = false;
            return;
        }
        _player.StartNowPlaying();
    }

    private void Announce_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _settings.Settings.AnnounceTracks = AnnounceToggle.IsOn;
        _settings.Save();
    }

    private void Minutes_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_loaded || double.IsNaN(args.NewValue)) return;
        var s = _settings.Settings;
        if (!double.IsNaN(TimerMinutesBox.Value)) s.TimerMinutes = (int)TimerMinutesBox.Value;
        if (!double.IsNaN(FocusBox.Value)) s.FocusMinutes = (int)FocusBox.Value;
        if (!double.IsNaN(BreakBox.Value)) s.BreakMinutes = (int)BreakBox.Value;
        if (!double.IsNaN(LongBreakBox.Value)) s.LongBreakMinutes = (int)LongBreakBox.Value;
        _settings.Save();
    }

    private void StartTimer_Click(object sender, RoutedEventArgs e)
    {
        _timer.StartTimer(TimeSpan.FromMinutes(Math.Clamp(_settings.Settings.TimerMinutes, 1, 600)));
        ShowTimerOnDisplay();
    }

    private void StartPomodoro_Click(object sender, RoutedEventArgs e)
    {
        _timer.StartPomodoro();
        ShowTimerOnDisplay();
    }

    /// <summary>The timer runs even without a display; it is shown on the display when one is connected.</summary>
    private void ShowTimerOnDisplay()
    {
        if (_connection.IsConnected) _player.StartTimerDisplay();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _timer.TogglePause();

    private void Skip_Click(object sender, RoutedEventArgs e) => _timer.Skip();

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_player.Mode == PlayerMode.Timer) await _player.StopAsync();
    }
}
