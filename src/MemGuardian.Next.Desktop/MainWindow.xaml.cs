using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using MediaBrush = System.Windows.Media.Brush;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MemGuardian.Next.Core;
using MemGuardian.Next.Windows;
using WpfButton = System.Windows.Controls.Button;

namespace MemGuardian.Next.Desktop;

public sealed record ProcessRow(string Initial, string ProcessName, string Details, string Cpu,
    string Io, string WorkingSet, string PrivateCommit, string Threads, string Handles, string Activity, MediaBrush ActivityBrush);

[SupportedOSPlatform("windows")]
public partial class MainWindow : Window
{
    private readonly bool _backgroundLaunch;
    private readonly LocalRuntimeStore _store;
    private readonly DesktopPreferencesStore _preferencesStore;
    private readonly IStartupRegistration _startupRegistration;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Drawing.Icon _trayDrawingIcon;
    private GuardianMonitorService? _monitor;
    private DesktopPreferences _preferences;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _refreshTimeoutTimer;
    private HwndSource? _windowSource;
    private WpfButton? _refreshButton;
    private object? _refreshButtonOriginalContent;
    private bool _refreshPending;
    private bool _explicitExit;
    private bool _awaitingShutdown;

    public ObservableCollection<ProcessRow> ProcessRows { get; } = new();

    public MainWindow(bool backgroundLaunch)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("桌面端仅支持 Windows 10/11 x64。");
        InitializeComponent();
        _backgroundLaunch = backgroundLaunch;
        _store = new LocalRuntimeStore();
        _preferencesStore = new DesktopPreferencesStore(_store.DirectoryPath);
        _preferences = _preferencesStore.Load();
        _startupRegistration = new WindowsStartupRegistration();
        DataContext = this;
        LoadSettingsIntoControls();
        UpdateFeedbackText();

        _trayDrawingIcon = CreateTrayIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _trayDrawingIcon,
            Text = "MemGuardian.Next · 内存压力监控",
            Visible = false
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(ShowFromTray));
        Loaded += WindowLoaded;
        Closing += WindowClosing;
        Closed += WindowClosed;
    }

    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_backgroundLaunch) HideToTray();

        try
        {
            if (_preferences.MonitorOnLaunch) await StartMonitoringAsync();
            else SetControllerState(ControllerState.Normal, "监控已暂停");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception)
        {
            SetControllerState(ControllerState.Normal, "监控启动失败");
            ShowToast($"监控未启动：{exception.Message}");
        }
    }

    private void WindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_preferences.RunInBackground && !_explicitExit)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        if (_monitor is not null && !_awaitingShutdown)
        {
            e.Cancel = true;
            _awaitingShutdown = true;
            _ = FinishShutdownAsync();
        }
    }

    private void WindowClosed(object? sender, EventArgs e)
    {
        CompleteRefreshFeedback();
        _windowSource?.RemoveHook(WindowMessageHook);
        _windowSource = null;
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _trayDrawingIcon.Dispose();
        _ = StopMonitoringAsync();
    }

    private async Task FinishShutdownAsync()
    {
        await StopMonitoringAsync();
        Close();
    }

    private void DashboardNavClick(object sender, RoutedEventArgs e) => ShowPage("dashboard");
    private void ProcessesNavClick(object sender, RoutedEventArgs e) => ShowPage("processes");
    private void DiagnosticsNavClick(object sender, RoutedEventArgs e) => ShowPage("diagnostics");
    private void SettingsNavClick(object sender, RoutedEventArgs e) => ShowPage("settings");

    private void ShowPage(string page)
    {
        DashboardScroll.Visibility = page == "dashboard" ? Visibility.Visible : Visibility.Collapsed;
        ProcessesPage.Visibility = page == "processes" ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsPage.Visibility = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        SetNavigationVisual(DashboardNav, page == "dashboard");
        SetNavigationVisual(ProcessesNav, page == "processes");
        SetNavigationVisual(DiagnosticsNav, page == "diagnostics");
        SetNavigationVisual(SettingsNav, page == "settings");
        if (page == "settings") LoadSettingsIntoControls();
    }

    private static void SetNavigationVisual(WpfButton button, bool selected)
    {
        button.Tag = selected ? "selected" : null;
        button.Background = selected
            ? new SolidColorBrush(MediaColor.FromRgb(18, 51, 68))
            : MediaBrushes.Transparent;
        button.Foreground = selected
            ? MediaBrushes.White
            : new SolidColorBrush(MediaColor.FromRgb(169, 188, 208));
    }

    private void RefreshClick(object sender, RoutedEventArgs e)
    {
        var monitor = _monitor;
        if (monitor is null)
        {
            ShowToast("请先在策略与启动中启用监控。");
            return;
        }

        if (_refreshPending) return;

        _refreshPending = true;
        _refreshButton = sender as WpfButton;
        _refreshButtonOriginalContent = _refreshButton?.Content;
        if (_refreshButton is not null)
        {
            _refreshButton.IsEnabled = false;
            _refreshButton.Content = "采样中…";
        }

        DashboardSubtitle.Text = "正在刷新系统与进程指标…";
        ShowToast("采样请求已提交，正在更新指标…");
        try
        {
            monitor.RequestRefresh();
            _refreshTimeoutTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _refreshTimeoutTimer.Tick -= RefreshTimeoutTick;
            _refreshTimeoutTimer.Tick += RefreshTimeoutTick;
            _refreshTimeoutTimer.Start();
        }
        catch (InvalidOperationException exception)
        {
            CompleteRefreshFeedback($"立即采样失败：{exception.Message}");
        }
    }

    private void CompleteRefreshFeedback(string? message = null)
    {
        if (!_refreshPending) return;

        _refreshPending = false;
        _refreshTimeoutTimer?.Stop();
        if (_refreshButton is not null)
        {
            _refreshButton.Content = _refreshButtonOriginalContent;
            _refreshButton.IsEnabled = true;
        }
        _refreshButton = null;
        _refreshButtonOriginalContent = null;
        if (message is not null) ShowToast(message);
    }

    private void RefreshTimeoutTick(object? sender, EventArgs e) =>
        CompleteRefreshFeedback("等待系统采样响应超时；监控仍会按计划继续运行。");

    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    private void WindowStateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        WindowSurface.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
        MaximizeButton.Content = maximized ? "❐" : "□";
    }

    private static IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != WmGetMinMaxInfo || lParam == IntPtr.Zero) return IntPtr.Zero;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return IntPtr.Zero;

        var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo)) return IntPtr.Zero;

        var minMaxInfo = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
        minMaxInfo.MaxPosition = new NativePoint
        {
            X = monitorInfo.Work.Left - monitorInfo.Monitor.Left,
            Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top
        };
        minMaxInfo.MaxSize = new NativePoint
        {
            X = monitorInfo.Work.Right - monitorInfo.Work.Left,
            Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top
        };
        Marshal.StructureToPtr(minMaxInfo, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo monitorInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private void CloseClick(object sender, RoutedEventArgs e) => Close();

    private async void ExitClick(object sender, RoutedEventArgs e)
    {
        _explicitExit = true;
        _preferences = _preferences with { RunInBackground = false };
        try { _preferencesStore.Save(_preferences); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            ShowToast($"退出设置未能保存，但本次仍会正常退出：{exception.Message}");
        }
        await StopMonitoringAsync();
        Close();
    }

    private async void ToggleStartupClick(object sender, RoutedEventArgs e)
    {
        var button = sender as WpfButton;
        var originalContent = button?.Content;
        if (button is not null)
        {
            button.IsEnabled = false;
            button.Content = "正在配置…";
            await Task.Yield();
        }

        try
        {
            var enable = StartupToggle.IsChecked != true;
            var startMinimized = StartMinimizedToggle.IsChecked == true;
            var preferences = _preferences with { StartMinimized = startMinimized };
            _preferencesStore.Save(preferences);
            _startupRegistration.SetEnabled(enable, startMinimized);
            _preferences = preferences;
            StartupToggle.IsChecked = enable;
            SettingsFeedbackText.Text = enable ? "已写入当前用户开机启动项。" : "已移除当前用户开机启动项。";
            ShowToast(SettingsFeedbackText.Text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            ShowToast($"开机启动配置失败：{exception.Message}");
        }
        finally
        {
            if (button is not null)
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }
    }

    private async void SaveSettingsClick(object sender, RoutedEventArgs e)
    {
        var button = sender as WpfButton;
        var originalContent = button?.Content;
        if (button is not null)
        {
            button.IsEnabled = false;
            button.Content = "保存中…";
            SettingsFeedbackText.Text = "正在校验并应用设置…";
            await Task.Yield();
        }

        try
        {
            var options = ReadOptionsFromControls();
            _store.UpdateOptions(options);
            var warning = _store.Save();
            _preferences = new DesktopPreferences
            {
                MonitorOnLaunch = MonitorToggle.IsChecked == true,
                AutomaticReclaim = AutoReclaimToggle.IsChecked == true,
                RunInBackground = BackgroundToggle.IsChecked == true,
                StartMinimized = StartMinimizedToggle.IsChecked == true
            };
            _preferencesStore.Save(_preferences);
            _startupRegistration.SetEnabled(StartupToggle.IsChecked == true, _preferences.StartMinimized);
            LoadSettingsIntoControls();
            if (_preferences.MonitorOnLaunch) await StartMonitoringAsync();
            else await StopMonitoringAsync();
            if (warning is not null) ShowToast(warning);
            else
            {
                SettingsFeedbackText.Text = "设置已校验并保存到当前用户配置目录。";
                ShowToast("策略与启动设置已保存。");
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or IOException or
                                           UnauthorizedAccessException or SecurityException or InvalidOperationException)
        {
            SettingsFeedbackText.Text = $"设置未能应用：{exception.Message}";
            ShowToast(SettingsFeedbackText.Text);
        }
        finally
        {
            if (button is not null)
            {
                button.Content = originalContent;
                button.IsEnabled = true;
            }
        }
    }

    private async Task StartMonitoringAsync()
    {
        if (_monitor?.IsRunning == true)
        {
            _monitor.SetAutomaticReclaim(_preferences.AutomaticReclaim);
            SetControllerState(_monitor.Latest.State, "监控运行中");
            return;
        }

        await StopMonitoringAsync();
        var monitor = new GuardianMonitorService(_store);
        monitor.Updated += MonitorUpdated;
        try
        {
            monitor.Start(_preferences.AutomaticReclaim);
            _monitor = monitor;
            SetControllerState(ControllerState.Watch, "正在采集系统指标");
        }
        catch
        {
            monitor.Updated -= MonitorUpdated;
            await monitor.DisposeAsync();
            throw;
        }
    }

    private async Task StopMonitoringAsync()
    {
        CompleteRefreshFeedback();
        var monitor = _monitor;
        if (monitor is null) return;
        _monitor = null;
        monitor.Updated -= MonitorUpdated;
        await monitor.DisposeAsync();
        SetControllerState(ControllerState.Normal, "监控已暂停");
    }

    private void MonitorUpdated(object? sender, MonitorUpdate update) =>
        Dispatcher.BeginInvoke(new Action(() => ApplyUpdate(update)));

    private void ApplyUpdate(MonitorUpdate update)
    {
        SetControllerState(update.State, update.Diagnosis is null ? "采样中" : "系统指标已更新");
        if (update.Snapshot is null || update.Diagnosis is null)
        {
            DashboardSubtitle.Text = update.Message ?? "等待首轮系统采样。";
            CompleteRefreshFeedback(update.Message ?? "采样已完成，但指标尚不完整。");
            return;
        }

        var memory = update.Snapshot.Memory;
        PhysicalValue.Text = $"{Percent(1 - memory.AvailablePhysicalRatio)} 已用";
        PhysicalDetail.Text = $"可用 {Bytes(memory.AvailablePhysicalBytes)} / {Bytes(memory.TotalPhysicalBytes)}";
        CommitValue.Text = Percent(memory.CommitRatio);
        CommitDetail.Text = $"{Bytes(memory.CommittedBytes)} / {Bytes(memory.CommitLimitBytes)}";
        PagingValue.Text = memory.PageReadsPerSecond is { } reads ? $"{reads:N1} /s" : "不可用";
        PagingDetail.Text = memory.PagesInputPerSecond is { } pages ? $"Pages Input {pages:N1} /秒" : "PDH Pages Input 不可用";
        PoolValue.Text = $"{Bytes(memory.PagedPoolBytes + memory.NonpagedPoolBytes)}";
        PoolDetail.Text = $"Paged {Bytes(memory.PagedPoolBytes)} · Nonpaged {Bytes(memory.NonpagedPoolBytes)}";
        PagefileDetail.Text = memory.PagefileUsagePercent is { } pagefile ? $"Pagefile 使用 {pagefile:N1}%" : "Pagefile 计数器不可用";

        var flagLabels = update.Diagnosis.Flags.Select(FlagLabel).ToArray();
        DiagnosisChips.ItemsSource = flagLabels;
        DiagnosisLevelText.Text = PressureLabel(update.Diagnosis.Level);
        DiagnosisLevelText.Foreground = LevelBrush(update.Diagnosis.Level);
        DiagnosisSummaryText.Text = flagLabels.Length == 0 ? "未发现持续的多维内存压力证据。" : string.Join(" · ", flagLabels);
        EvidenceLevelText.Text = PressureLabel(update.Diagnosis.Level);
        EvidenceLevelText.Foreground = LevelBrush(update.Diagnosis.Level);
        EvidenceStateText.Text = $"控制器状态：{ControllerLabel(update.State)} · 最近更新 {update.UpdatedAt.ToLocalTime():HH:mm:ss}";
        EvidenceItems.ItemsSource = update.Diagnosis.Evidence;
        NoEvidenceText.Visibility = update.Diagnosis.Evidence.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AutoPolicyText.Text = CandidateSummary(update);
        UpdateFeedbackText();

        var history = _store.History.Snapshot().Where(item => item.CapturedAt >= update.UpdatedAt - TimeSpan.FromMinutes(15)).ToArray();
        AvailableTrend.Values = DashboardAvailableTrend.Values = history.Select(item => item.TotalPhysicalBytes == 0 ? 0 : (double)item.AvailablePhysicalBytes / item.TotalPhysicalBytes).ToArray();
        CommitTrend.Values = DashboardCommitTrend.Values = history.Select(item => item.CommitLimitBytes == 0 ? 0 : (double)item.CommittedBytes / item.CommitLimitBytes).ToArray();
        PagingTrend.Values = history.Select(item => item.PageReadsPerSecond ?? 0).ToArray();
        TrendRangeText.Text = history.Length < 2
            ? "样本持续写入本地有界 ring buffer。"
            : $"{history[0].CapturedAt.ToLocalTime():HH:mm} — {history[^1].CapturedAt.ToLocalTime():HH:mm} · {history.Length} 个样本";
        DashboardSubtitle.Text = update.Message ?? $"快照时间 {memory.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss} · 状态机 {ControllerLabel(update.State)}";
        HeaderStateText.Text = ControllerLabel(update.State);
        var processRows = update.Snapshot.Processes.OrderByDescending(process => process.WorkingSetBytes)
            .Take(50).Select(process => ToRow(process, update.Candidates)).ToArray();
        ProcessRows.Clear();
        foreach (var row in processRows) ProcessRows.Add(row);
        EmptyProcessesText.Visibility = ProcessRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ProcessCountText.Text = $"{update.Snapshot.Processes.Count} 个进程 · 按 Working Set 排序";
        CounterWarningText.Text = memory.PageReadsPerSecond is null || memory.PagesInputPerSecond is null
            ? "PDH paging 计数器暂不可用；系统内存与 Commit 指标仍可用于诊断。"
            : string.Empty;
        SidebarUpdateText.Text = update.UpdatedAt.ToLocalTime().ToString("HH:mm:ss 更新 · 15 分钟有界趋势", CultureInfo.CurrentCulture);
        CompleteRefreshFeedback("采样完成，页面指标已更新。");
    }

    private ProcessRow ToRow(ProcessSnapshot process, CandidateSelection? selection)
    {
        var activity = process.IsForeground ? "前台" :
            process.LastUsedAt is null ? "未知" : DateTimeOffset.UtcNow - process.LastUsedAt < TimeSpan.FromMinutes(2) ? "近期活跃" :
            selection?.Candidates.Any(candidate => candidate.Identity == process.Identity) == true ? "候选" :
            process.CanTrim ? "后台" : "受保护";
        var brush = activity switch
        {
            "候选" => (MediaBrush)FindResource("Success"),
            "前台" or "近期活跃" => (MediaBrush)FindResource("Warning"),
            "受保护" => new SolidColorBrush(MediaColor.FromRgb(125, 137, 152)),
            _ => new SolidColorBrush(MediaColor.FromRgb(75, 101, 132))
        };
        var recentUse = process.LastUsedAt is { } lastUsed ? $"最近使用 {lastUsed.ToLocalTime():HH:mm}" : "前台历史未知";
        var details = $"PID {process.Identity.ProcessId} · {(process.SessionId is { } session ? $"Session {session}" : "Session —")} · {OwnerLabel(process)} · {recentUse}";
        return new ProcessRow(process.ProcessName.Length == 0 ? "?" : process.ProcessName[..1].ToUpperInvariant(),
            process.ProcessName, details, process.CpuPercent is { } cpu ? $"{cpu:N1}%" : "—",
            process.IoBytesPerSecond is { } io && double.IsFinite(io) && io >= 0 ? $"{Bytes((ulong)io)}/s" : "—",
            Bytes(process.WorkingSetBytes), Bytes(process.PrivateCommitBytes), process.ThreadCount.ToString("N0"),
            process.HandleCount.ToString("N0"), activity, brush);
    }

    private string CandidateSummary(MonitorUpdate update)
    {
        var selection = update.Candidates;
        if (selection is null) return _preferences.AutomaticReclaim ? "自动回收已启用 · 等待安全候选" : "自动回收关闭 · 当前只监测";
        if (selection.Candidates.Count > 0)
            return _preferences.AutomaticReclaim
                ? $"自动回收已启用 · 安全候选 {selection.Candidates.Count} 个"
                : $"自动回收关闭 · 已评估 {selection.Candidates.Count} 个安全候选";
        if (selection.BlockedBy is { } blocked) return $"{(_preferences.AutomaticReclaim ? "自动回收已启用" : "自动回收关闭")} · {TranslateBlock(blocked)}";
        if (selection.Rejections.FirstOrDefault() is { } rejected)
            return $"本轮未回收 · {rejected.ProcessName}：{TranslateBlock(rejected.Reason)}";
        return "当前没有符合条件的回收候选。";
    }

    private void UpdateFeedbackText()
    {
        var feedback = _store.RecentFeedbacks.LastOrDefault();
        LastFeedbackText.Text = feedback is null
            ? "尚无回收记录；自动回收默认关闭。"
            : $"{feedback.CapturedAt.ToLocalTime():MM-dd HH:mm:ss} · {feedback.TargetProcessName ?? "目标进程"} · {feedback.Summary}";
    }

    private static string TranslateBlock(string reason) => reason switch
    {
        "Pressure/Critical not confirmed" => "等待压力状态确认",
        "Global cooldown active" => "全局冷却中",
        "Backoff active" => "自适应退避中",
        "Foreground process" => "前台程序保护",
        "Used within the protected interval" => "最近使用保护",
        "Not idle long enough" => "闲置时间不足",
        "CPU activity is high or unknown" => "CPU 活跃或指标未知",
        "I/O activity is high or unknown" => "I/O 活跃或指标未知",
        "Process cooldown active" => "该进程仍在冷却期",
        "Denylist" => "denylist 保护",
        "Not owned by current user" => "非当前用户进程",
        "Not in current user session" => "非当前 Session",
        "Foreground-use history unknown" => "前台使用历史未知",
        _ => reason
    };

    private static string OwnerLabel(ProcessSnapshot process) => !process.CanTrim && process.CollectionError is { } error
        ? error.Length > 26 ? "访问受限" : error
        : process.IsCurrentUser ? "当前用户" : "其他用户";

    private void LoadSettingsIntoControls()
    {
        var options = _store.Options;
        MonitorToggle.IsChecked = _preferences.MonitorOnLaunch;
        AutoReclaimToggle.IsChecked = _preferences.AutomaticReclaim;
        BackgroundToggle.IsChecked = _preferences.RunInBackground;
        StartMinimizedToggle.IsChecked = _preferences.StartMinimized;
        try { StartupToggle.IsChecked = _startupRegistration.IsEnabled; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or SecurityException or IOException)
        {
            StartupToggle.IsChecked = false;
            SettingsFeedbackText.Text = $"无法读取当前用户启动项：{exception.Message}";
        }

        AvailablePressureField.Value = Format(options.AvailablePressureRatio * 100);
        AvailableCriticalField.Value = Format(options.AvailableCriticalRatio * 100);
        CommitPressureField.Value = Format(options.CommitPressureRatio * 100);
        CommitCriticalField.Value = Format(options.CommitCriticalRatio * 100);
        SystemIntervalField.Value = Format(options.SystemSampleInterval.TotalSeconds);
        ProcessIntervalField.Value = Format(options.ProcessSampleInterval.TotalSeconds);
        PressureConfirmationField.Value = Format(options.PressureConfirmation.TotalSeconds);
        CriticalConfirmationField.Value = Format(options.CriticalConfirmation.TotalSeconds);
        PageReadsField.Value = Format(options.PageReadsAlertPerSecond);
        PagesInputField.Value = Format(options.PagesInputAlertPerSecond);
        GlobalCooldownField.Value = Format(options.GlobalCooldown.TotalMinutes);
        ProcessCooldownField.Value = Format(options.ProcessCooldown.TotalMinutes);
        IdleMinutesField.Value = Format(options.MinimumIdleTime.TotalMinutes);
        MinimumWorkingSetField.Value = Format(options.MinimumCandidateWorkingSetBytes / 1024d / 1024d);
        MaximumCpuField.Value = Format(options.MaximumCandidateCpuPercent);
        MaximumIoField.Value = Format(options.MaximumCandidateIoBytesPerSecond / 1024d);
        FeedbackDelayField.Value = Format(options.FeedbackDelay.TotalSeconds);
        LeakWindowField.Value = Format(options.LeakWindow.TotalMinutes);
        ProcessLeakGrowthField.Value = Format(options.ProcessLeakGrowthBytes / 1024d / 1024d);
        DriverLeakGrowthField.Value = Format(options.DriverLeakGrowthBytes / 1024d / 1024d);
        DenylistText.Text = string.Join(Environment.NewLine, options.Denylist.OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
    }

    private GuardianOptions ReadOptionsFromControls()
    {
        var current = _store.Options;
        double Number(SettingField field)
        {
            var value = double.Parse(field.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            return double.IsFinite(value) ? value : throw new FormatException($"“{field.Label}”必须是有限数值。");
        }
        var denylist = DenylistText.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(value => value.Length <= 128).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return current with
        {
            AvailablePressureRatio = Number(AvailablePressureField) / 100,
            AvailableCriticalRatio = Number(AvailableCriticalField) / 100,
            CommitPressureRatio = Number(CommitPressureField) / 100,
            CommitCriticalRatio = Number(CommitCriticalField) / 100,
            SystemSampleInterval = TimeSpan.FromSeconds(Number(SystemIntervalField)),
            ProcessSampleInterval = TimeSpan.FromSeconds(Number(ProcessIntervalField)),
            PressureConfirmation = TimeSpan.FromSeconds(Number(PressureConfirmationField)),
            CriticalConfirmation = TimeSpan.FromSeconds(Number(CriticalConfirmationField)),
            PageReadsAlertPerSecond = Number(PageReadsField),
            PagesInputAlertPerSecond = Number(PagesInputField),
            GlobalCooldown = TimeSpan.FromMinutes(Number(GlobalCooldownField)),
            ProcessCooldown = TimeSpan.FromMinutes(Number(ProcessCooldownField)),
            MinimumIdleTime = TimeSpan.FromMinutes(Number(IdleMinutesField)),
            MinimumCandidateWorkingSetBytes = ToBytes(Number(MinimumWorkingSetField)),
            MaximumCandidateCpuPercent = Number(MaximumCpuField),
            MaximumCandidateIoBytesPerSecond = ToBytes(Number(MaximumIoField), 1024),
            FeedbackDelay = TimeSpan.FromSeconds(Number(FeedbackDelayField)),
            LeakWindow = TimeSpan.FromMinutes(Number(LeakWindowField)),
            ProcessLeakGrowthBytes = ToBytes(Number(ProcessLeakGrowthField)),
            DriverLeakGrowthBytes = ToBytes(Number(DriverLeakGrowthField)),
            Denylist = denylist
        };
    }

    private static ulong ToBytes(double value, double multiplier = 1024 * 1024)
    {
        if (!double.IsFinite(value) || value < 0 || value > ulong.MaxValue / multiplier)
            throw new FormatException("数值超出可接受范围。");
        return (ulong)(value * multiplier);
    }

    private void SetControllerState(ControllerState state, string fallback)
    {
        var brush = state switch
        {
            ControllerState.Critical or ControllerState.Backoff => (MediaBrush)FindResource("Danger"),
            ControllerState.Pressure or ControllerState.Cooldown => (MediaBrush)FindResource("Warning"),
            ControllerState.Watch => (MediaBrush)FindResource("Accent"),
            _ => (MediaBrush)FindResource("Success")
        };
        HeaderStateDot.Fill = SidebarStateDot.Fill = brush;
        HeaderStateText.Text = ControllerLabel(state);
        SidebarStateText.Text = fallback;
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { ToastBorder.Visibility = Visibility.Collapsed; _toastTimer.Stop(); };
        _toastTimer.Start();
    }

    private void HideToTray()
    {
        _trayIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private static Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(Drawing.Color.Transparent);
        using var background = new Drawing.SolidBrush(Drawing.Color.FromArgb(51, 120, 246));
        graphics.FillRoundedRectangle(background, new Drawing.Rectangle(1, 1, 30, 30), 8);
        using var font = new Drawing.Font("Segoe UI", 17, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
        using var text = new Drawing.SolidBrush(Drawing.Color.White);
        graphics.DrawString("M", font, text, new Drawing.PointF(5, 5));
        var iconHandle = bitmap.GetHicon();
        try
        {
            using var borrowed = Drawing.Icon.FromHandle(iconHandle);
            return (Drawing.Icon)borrowed.Clone();
        }
        finally
        {
            _ = DestroyIcon(iconHandle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    private static string FlagLabel(DiagnosticFlag flag) => flag switch
    {
        DiagnosticFlag.LowAvailableRam => "可用内存偏低",
        DiagnosticFlag.WorkingSetPressure => "驻留集压力",
        DiagnosticFlag.CommitPressure => "提交量压力",
        DiagnosticFlag.PagingPressure => "页面换入压力",
        DiagnosticFlag.KernelPoolPressure => "内核池压力",
        DiagnosticFlag.PossibleProcessLeak => "疑似进程泄漏",
        DiagnosticFlag.PossibleDriverLeak => "疑似驱动泄漏",
        DiagnosticFlag.PagefileUsageHigh => "页面文件占用高",
        _ => flag.ToString()
    };

    private static string PressureLabel(PressureLevel level) => level switch
    {
        PressureLevel.Critical => "严重压力",
        PressureLevel.Pressure => "内存压力",
        PressureLevel.Watch => "观察中",
        _ => "状态正常"
    };

    private static string ControllerLabel(ControllerState state) => state switch
    {
        ControllerState.Critical => "严重压力",
        ControllerState.Pressure => "压力确认",
        ControllerState.Watch => "观察中",
        ControllerState.Cooldown => "冷却中",
        ControllerState.Backoff => "自适应退避",
        _ => "运行正常"
    };

    private static MediaBrush LevelBrush(PressureLevel level) => level switch
    {
        PressureLevel.Critical => new SolidColorBrush(MediaColor.FromRgb(228, 92, 104)),
        PressureLevel.Pressure => new SolidColorBrush(MediaColor.FromRgb(230, 155, 48)),
        PressureLevel.Watch => new SolidColorBrush(MediaColor.FromRgb(51, 120, 246)),
        _ => new SolidColorBrush(MediaColor.FromRgb(22, 164, 122))
    };

    private static string Bytes(ulong bytes)
    {
        var value = (double)bytes;
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var index = 0;
        while (value >= 1024 && index < units.Length - 1) { value /= 1024; index++; }
        return $"{value:N1} {units[index]}";
    }

    private static string Percent(double? ratio) => ratio is { } value ? $"{value:P1}" : "不可用";
    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

internal static class DrawingExtensions
{
    public static void FillRoundedRectangle(this Drawing.Graphics graphics, Drawing.Brush brush, Drawing.Rectangle bounds, int radius)
    {
        using var path = new Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}
