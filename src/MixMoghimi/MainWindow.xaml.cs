using MixMoghimi.Models;
using MixMoghimi.Services;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MixMoghimi;

public partial class MainWindow : Window
{
    private readonly DeviceIdentityService _identity = new();
    private readonly MixApiClient _api;
    private readonly SingBoxService _engine = new();
    private readonly ObservableCollection<ServerEntry> _servers = new();
    private readonly DispatcherTimer _displayTimer;
    private readonly Stopwatch _connectionClock = new();
    private readonly Stopwatch _quotaClock = new();

    private TrialSnapshot? _snapshot;
    private ServerEntry? _selectedServer;
    private Guid? _sessionId;
    private long _sequence;
    private CancellationTokenSource? _heartbeatWaitCts;
    private CancellationTokenSource? _connectCts;
    private Task? _heartbeatTask;
    private bool _connecting;
    private bool _connected;
    private bool _disconnecting;
    private bool _closeRequested;
    private bool _allowClose;
    private bool _apiReady;

    public MainWindow()
    {
        InitializeComponent();
        _api = new MixApiClient(_identity);
        _engine.EngineExited += (_, _) =>
        {
            _ = Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_connected && !_disconnecting && !_connecting)
                    await DisconnectAsync("موتور اتصال متوقف شد؛ پراکسی ویندوز بازیابی شد.");
            }));
        };
        ServersList.ItemsSource = _servers;
        _displayTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _displayTimer.Tick += (_, _) => RefreshLiveLabels();
        _displayTimer.Start();
        SourceInitialized += (_, _) => ApplyNativeGlass();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await RefreshBackendAsync(showErrors: true);
    }

    private async Task RefreshBackendAsync(bool showErrors)
    {
        try
        {
            SetTopStatus("در حال اتصال به سرویس ویندوز…", false);
            await _api.EnsureRegisteredAsync();
            _snapshot = await _api.GetSnapshotAsync();
            _quotaClock.Restart();
            ApplySnapshot(_snapshot);
            await RefreshServersAsync();
            _apiReady = true;
            SetTopStatus("سرویس آماده است", true);
            if (_servers.Count == 0)
            {
                GreetingText.Text = "سرویس آماده است؛ هنوز کانفیگ فعالی در پنل ویندوز ثبت نشده.";
                ConnectionHintText.Text = "برای فعال‌شدن اتصال، کانفیگ باید در پنل ویندوز قرار بگیرد.";
            }
        }
        catch (Exception ex)
        {
            _apiReady = false;
            SetTopStatus("ارتباط با سرویس برقرار نشد", false);
            ConnectionStatusText.Text = "در انتظار سرویس";
            ConnectionHintText.Text = "اتصال به سرویس ویندوز برقرار نیست.";
            if (showErrors)
                MessageBox.Show(this, ex.Message, "MIX-MOGHIMI", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async Task RefreshServersAsync()
    {
        IReadOnlyList<ServerEntry> entries = await _api.GetServersAsync();
        string? previousId = _selectedServer?.Id;
        _servers.Clear();
        foreach (ServerEntry entry in entries) _servers.Add(entry);
        if (_servers.Count > 0)
        {
            ServerEntry? preferred = _servers.FirstOrDefault(x => x.Id == previousId) ?? _servers[0];
            ServersList.SelectedItem = preferred;
            EmptyServersPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _selectedServer = null;
            ServersList.SelectedItem = null;
            EmptyServersPanel.Visibility = Visibility.Visible;
            SelectedServerText.Text = "سروری انتخاب نشده";
        }
        ServerSelectionHint.Text = _servers.Count == 0
            ? "لیست سرورهای فعال خالی است."
            : $"{ToFa(_servers.Count.ToString(CultureInfo.InvariantCulture))} سرور فعال";
    }

    private async void RefreshServers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshBackendAsync(showErrors: false);
            SetTopStatus("لیست سرورها بروزرسانی شد", true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "بروزرسانی سرورها", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedServer = ServersList.SelectedItem as ServerEntry;
        if (_selectedServer is not null)
        {
            SelectedServerText.Text = _selectedServer.Title;
            ConnectionHintText.Text = "سرور انتخاب شد؛ آمادهٔ اتصال.";
        }
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_connecting || _disconnecting) return;
        if (_connected)
        {
            await DisconnectAsync("اتصال توسط کاربر قطع شد.");
            return;
        }
        if (_selectedServer is null)
        {
            SetPage("servers");
            MessageBox.Show(this, "ابتدا یک سرور فعال انتخاب کن.", "انتخاب سرور", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _connecting = true;
        _connectCts = new CancellationTokenSource();
        CancellationToken connectToken = _connectCts.Token;
        ConnectButton.IsEnabled = false;
        ConnectionStatusText.Text = "در حال آماده‌سازی اتصال…";
        ConnectionHintText.Text = "در حال بررسی سهمیه و راه‌اندازی موتور";
        SetTopStatus("در حال اتصال…", false);
        bool backendSessionStarted = false;
        Guid session = Guid.NewGuid();
        try
        {
            if (!_apiReady) await RefreshBackendAsync(showErrors: false);
            connectToken.ThrowIfCancellationRequested();
            if (!_apiReady) throw new InvalidOperationException("ارتباط با سرویس MIX برقرار نیست.");

            TrialSnapshot quota = await _api.GetSnapshotAsync(connectToken);
            ApplySnapshot(quota);
            if (!quota.AccessAllowed || quota.TrialRemainingMs <= 0)
                throw new InvalidOperationException("سهمیهٔ سه‌ساعتهٔ این هفته تمام شده است. زمان شارژ بعدی در پنل نمایش داده می‌شود.");

            string protectedConfig = await _api.GetServerConfigAsync(_selectedServer.Id, connectToken);
            ShadowsocksServer server = ShadowsocksUriParser.Parse(protectedConfig);
            // Start and validate the engine before the weekly quota clock starts.
            // The Windows system proxy is left untouched until the server accepts this session.
            await _engine.StartAsync(server, connectToken);
            ConnectionStatusText.Text = "در حال آزمایش مسیر اینترنت…";
            bool reachable = await SingBoxService.TestConnectivityAsync(_engine.MixedPort, connectToken);
            if (!reachable)
                throw new InvalidOperationException("موتور اجرا شد، اما از مسیر این سرور دسترسی اینترنت تأیید نشد. ممکن است سرور خاموش، پورت بسته یا کانفیگ نامعتبر باشد.");

            backendSessionStarted = true; // the request may commit even if its response is lost
            TrialSnapshot started = await _api.StartSessionAsync(session, connectToken);
            ApplySnapshot(started); // align the local countdown with the server's exact session start
            _sessionId = session;
            _sequence = 0;
            WindowsProxyManager.Enable(_engine.MixedPort);
            _connected = true;
            _connectionClock.Restart();
            ConnectButton.IsEnabled = true;
            SetConnectButton(true);
            ConnectionStatusText.Text = "اتصال برقرار است";
            ConnectionHintText.Text = "ترافیک برنامه‌های سازگار با پراکسی ویندوز از این سرور عبور می‌کند.";
            SetTopStatus("متصل • اتصال رمزگذاری‌شده", true);
            GreetingText.Text = "اتصال برقرار است؛ زمان و ترافیک به‌صورت دوره‌ای ثبت می‌شوند.";
            _heartbeatWaitCts = new CancellationTokenSource();
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(session, _heartbeatWaitCts.Token));
            SetPage("home");
        }
        catch (Exception ex)
        {
            try { WindowsProxyManager.Restore(); } catch { }
            try { await _engine.StopAsync(); } catch { }
            if (backendSessionStarted)
            {
                try { await SendFinalHeartbeatAsync(session); } catch { }
            }
            _sessionId = null;
            _connectionClock.Reset();
            SetConnectButton(false);
            ConnectionStatusText.Text = "اتصال برقرار نشد";
            ConnectionHintText.Text = ex.Message;
            SetTopStatus("قطع • آمادهٔ تلاش دوباره", false);
            if (!_closeRequested && ex is not OperationCanceledException)
                MessageBox.Show(this, ex.Message, "خطا در اتصال", MessageBoxButton.OK, MessageBoxImage.Warning);
            try { if (!_closeRequested) await RefreshBackendAsync(showErrors: false); } catch { }
        }
        finally
        {
            _connecting = false;
            _connectCts?.Dispose();
            _connectCts = null;
            ConnectButton.IsEnabled = true;
            if (_closeRequested && !_allowClose) { _allowClose = true; Close(); }
        }
    }

    private async Task HeartbeatLoopAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        int failures = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                // Reuse the same sequence when the response is lost. The server accepts
                // same-sequence cumulative checkpoints without double-counting bytes.
                long nextSequence = Interlocked.Read(ref _sequence) + 1;
                long rx = _engine.DownloadBytes;
                long tx = _engine.UploadBytes;
                try
                {
                    TrialSnapshot status = await _api.SendHeartbeatAsync(sessionId, nextSequence, rx, tx, false);
                    Interlocked.Exchange(ref _sequence, nextSequence);
                    failures = 0;
                    await Dispatcher.InvokeAsync(() => ApplySnapshot(status));
                    if (!status.AccessAllowed || status.TrialRemainingMs <= 0)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(async () => await DisconnectAsync("سهمیهٔ هفتگی تمام شد.")));
                        return;
                    }
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    failures++;
                    if (failures >= 2)
                    {
                        _ = Dispatcher.BeginInvoke(new Action(async () => await DisconnectAsync("ثبت مصرف در سرور پاسخ نداد؛ برای جلوگیری از مصرف ثبت‌نشده اتصال قطع شد.")));
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async Task SendFinalHeartbeatAsync(Guid sessionId)
    {
        long nextSequence = Interlocked.Increment(ref _sequence);
        TrialSnapshot final = await _api.SendHeartbeatAsync(
            sessionId, nextSequence, _engine.DownloadBytes, _engine.UploadBytes, true);
        await Dispatcher.InvokeAsync(() => ApplySnapshot(final));
    }

    private async Task DisconnectAsync(string reason)
    {
        if (_disconnecting) return;
        if (!_connected && _sessionId is null && !_engine.IsRunning) return;
        _disconnecting = true;
        SetTopStatus("در حال قطع اتصال…", false);
        ConnectionStatusText.Text = "در حال بستن امن اتصال…";

        try
        {
            try { WindowsProxyManager.Restore(); } catch { }

            if (_heartbeatWaitCts is not null)
            {
                _heartbeatWaitCts.Cancel();
                if (_heartbeatTask is not null)
                {
                    try { await _heartbeatTask.WaitAsync(TimeSpan.FromSeconds(18)); }
                    catch (TimeoutException) { }
                    catch (OperationCanceledException) { }
                }
                _heartbeatWaitCts.Dispose();
                _heartbeatWaitCts = null;
                _heartbeatTask = null;
            }

            try { await _engine.StopAsync(); } catch { }

            if (_sessionId is Guid session)
            {
                try { await SendFinalHeartbeatAsync(session); }
                catch (Exception ex)
                {
                    ConnectionHintText.Text = "اتصال محلی بسته شد؛ آخرین ثبت مصرف در سرور تأیید نشد: " + ex.Message;
                }
            }
        }
        finally
        {
            _sessionId = null;
            _connected = false;
            _connectionClock.Stop();
            SetConnectButton(false);
            ConnectionStatusText.Text = "اتصال قطع است";
            ConnectionHintText.Text = reason;
            SetTopStatus("آماده برای اتصال", true);
            _disconnecting = false;
            if (_closeRequested && !_allowClose)
            {
                _allowClose = true;
                Close();
            }
        }
    }

    private void ApplySnapshot(TrialSnapshot snapshot)
    {
        _snapshot = snapshot;
        _quotaClock.Restart();
        RefreshLiveLabels();
    }

    private void RefreshLiveLabels()
    {
        if (_snapshot is not null)
        {
            long remaining = Math.Max(0, _snapshot.TrialRemainingMs -
                (_connected ? _quotaClock.ElapsedMilliseconds : 0));
            long used = Math.Clamp(_snapshot.TrialLimitMs - remaining, 0, _snapshot.TrialLimitMs);
            TrialRemainingText.Text = FormatDuration(remaining);
            TrialUsedText.Text = $"مصرف‌شده: {FormatDuration(used)} از ۰۳:۰۰:۰۰";
            TrialProgress.Value = _snapshot.TrialLimitMs <= 0 ? 0 :
                Math.Clamp(used * 100d / _snapshot.TrialLimitMs, 0, 100);
            ResetDateText.Text = _snapshot.ResetAt is DateTimeOffset reset
                ? "شارژ بعدی: " + reset.ToLocalTime().ToString("yyyy/MM/dd  HH:mm", CultureInfo.GetCultureInfo("fa-IR"))
                : "شارژ بعدی: پس از اولین ثبت دستگاه";
            long download = _snapshot.WeekDownloadBytes;
            long upload = _snapshot.WeekUploadBytes;
            DownloadValue.Text = FormatBytes(download);
            UploadValue.Text = FormatBytes(upload);
            TrafficPeriodText.Text = "مجموع مصرف ثبت‌شده در این دورهٔ هفتگی";
        }
        if (_connected)
            SessionDurationText.Text = ToFa(_connectionClock.Elapsed.ToString(@"hh\:mm\:ss"));
        else
            SessionDurationText.Text = "۰۰:۰۰:۰۰";
    }

    private void SetConnectButton(bool connected)
    {
        _connected = connected;
        ConnectButton.Content = null;
        ConnectButton.Background = connected
            ? new SolidColorBrush(Color.FromRgb(255, 99, 130))
            : (Brush)FindResource("ConnectGradient");
        if (ConnectButton.Template.FindName("ActionLabel", ConnectButton) is TextBlock label)
            label.Text = connected ? "قطع اتصال" : "اتصال";
        if (ConnectButton.Template.FindName("Glyph", ConnectButton) is TextBlock glyph)
            glyph.Text = connected ? "■" : "⏻";
    }

    private void SetTopStatus(string text, bool healthy)
    {
        TopStatusText.Text = text;
        // The dot is the first child of the top status stack; tint it without adding UI state.
        TopStatusDot.Fill = healthy ? new SolidColorBrush(Color.FromRgb(67, 214, 160)) : new SolidColorBrush(Color.FromRgb(255, 109, 136));
        TopStatusText.Foreground = healthy ? new SolidColorBrush(Color.FromRgb(174, 190, 217)) :
            new SolidColorBrush(Color.FromRgb(255, 174, 187));
    }

    private void SetPage(string page)
    {
        DashboardView.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        ServersView.Visibility = page == "servers" ? Visibility.Visible : Visibility.Collapsed;
        SupportView.Visibility = page == "support" ? Visibility.Visible : Visibility.Collapsed;
        HomeNav.Background = page == "home" ? new SolidColorBrush(Color.FromArgb(52, 42, 60, 114)) : Brushes.Transparent;
    }

    private void DashboardNav_Click(object sender, RoutedEventArgs e) => SetPage("home");
    private void ServersNav_Click(object sender, RoutedEventArgs e) => SetPage("servers");
    private void SupportNav_Click(object sender, RoutedEventArgs e) => SetPage("support");

    private void SupportTelegram_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://t.me/Moghimi_vpn_bot") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "بازکردن تلگرام", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_closeRequested) return;
        _closeRequested = true;
        if (_connecting) { _connectCts?.Cancel(); return; }
        _ = DisconnectAsync("برنامه بسته شد.");
        if (!_connected && _sessionId is null && !_engine.IsRunning)
        {
            _allowClose = true;
            Close();
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    private static string FormatDuration(long milliseconds)
    {
        TimeSpan span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        long hours = (long)span.TotalHours;
        return ToFa($"{hours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }

    private static string FormatBytes(long bytes)
    {
        double value = Math.Max(0, bytes);
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        string number = unit == 0 ? value.ToString("0", CultureInfo.InvariantCulture) : value.ToString("0.00", CultureInfo.InvariantCulture);
        return ToFa(number) + " " + units[unit];
    }

    private static string ToFa(string value)
    {
        const string western = "0123456789";
        const string persian = "۰۱۲۳۴۵۶۷۸۹";
        var result = new StringBuilder(value.Length);
        foreach (char ch in value) result.Append(ch >= '0' && ch <= '9' ? persian[ch - '0'] : ch);
        return result.ToString();
    }

    private void ApplyNativeGlass()
    {
        try
        {
            IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int backdrop = 3; // DWMSBT_TRANSIENTWINDOW: Windows 11 glass backdrop
            _ = DwmSetWindowAttribute(hwnd, 38, ref backdrop, sizeof(int));
            var margins = new GlassMargins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            if (System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                target.BackgroundColor = Colors.Transparent;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlassMargins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref GlassMargins margins);

    protected override async void OnClosed(EventArgs e)
    {
        _displayTimer.Stop();
        try { await _engine.DisposeAsync(); } catch { }
        _api.Dispose();
        base.OnClosed(e);
    }
}
