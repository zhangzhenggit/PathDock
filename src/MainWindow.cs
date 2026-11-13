using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace PathDock;

public sealed partial class MainWindow : Window
{
    readonly DockStore store;
    readonly bool preview;
    readonly Grid layout = new();
    readonly Border surface = new();
    readonly Grid host = new() { ClipToBounds = true };
    readonly TranslateTransform panelSlide = new();
    readonly Stopwatch slideClock = new();
    Button? edgeButton;
    readonly Border edgePresentation = new();
    EdgeWindow? edgeWindow;
    DialogFollow? dialogFollow;
    OutsideClickMonitor? outsideClickMonitor;
    bool animating, draggingPanel, floatingTransition, waitingForFirstRender, openingHandleCover;
    Point dockPosition, floatingPosition;
    double reveal = 1, transitionFrom = 1, transitionTo = 1;
    double motionVelocity, transitionVelocity;
    TimeSpan lastRenderingTime = TimeSpan.MinValue;
    (int X, int Y, int Width, int Height)? lastTransitionPlacement;
    string? edgeVisualSide;
    readonly record struct DockRegionStamp(int X, int Y, int Width, int Height, int Radius, Rect? Cutout, double Scale, bool Empty = false);
    DockRegionStamp? lastDockRegion;
    int motionFrames;
    readonly StackPanel fixedList = new(), recentList = new();
    readonly TextBlock status = Ui.Text("准备就绪", 11, Ui.Muted);


    readonly DispatcherTimer targetTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer recentTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer outsideHideTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly RecentActivityReader activityReader = new();
    Task<List<RecentActivity>>? activityRead;
    List<ShellSession> availableSessions = new();
    Task<List<ShellSession>>? sessionRead;
    HashSet<string> explorerSeen = new(StringComparer.OrdinalIgnoreCase);
    Task<List<string>>? explorerRead;
    Forms.NotifyIcon? tray;
    TargetWindow? target;
    bool collapsed, dialogOpen, polling, exiting;
    bool performing;
    int menuDepth;
    readonly HashSet<ContextMenu> openMenus = new();
    Button? pin;
    IntPtr handle;
    HwndSource? source;
    bool initialized, renderPending;
    IntPtr previousForeground;
    const double PanelWidth = 300;
    double desiredHeight = 300;
    readonly ScrollViewer folderScroll = new();

    public MainWindow(DockStore store, bool preview, bool startCollapsed = false)
    {
        this.store = store; this.preview = preview;
        store.SaveFailed += error => Dispatcher.BeginInvoke(new Action(() => { if (!exiting) Toast("配置保存失败：" + error.Message, true); }));
        Title = "PathDock"; Width = 300; Height = 300; MinWidth = 0; MinHeight = 0;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; Background = Ui.Canvas;
        Topmost = true; ShowInTaskbar = false; ShowActivated = false; FontFamily = SystemFonts.MessageFontFamily;
        Ui.CrispText(this);
        Build();
        Icon = AppIcon.Image;
        if (startCollapsed)
        {
            collapsed = true; reveal = transitionFrom = transitionTo = 0;
            surface.IsHitTestVisible = false;
            if (edgeButton != null) { edgeButton.Opacity = 1; edgeButton.IsHitTestVisible = true; }
        }
        SourceInitialized += (_, _) =>
        {
            handle = new WindowInteropHelper(this).Handle; source = HwndSource.FromHwnd(handle); source.AddHook(WindowMessage); Native.NoActivate(this, true); Native.ConfigureDockFrame(handle);
            if (!Native.RegisterHotKey(handle, 1, 0x4003, 0x20)) Toast("快捷键已被占用，可点击屏幕边缘展开");
        };
        Loaded += async (_, _) =>
        {
            if (initialized) return; initialized = true;
            Place(); CreateTray(); targetTimer.Start(); recentTimer.Start(); Render();
            if (!preview) outsideClickMonitor = new OutsideClickMonitor((x, y) =>
                Dispatcher.BeginInvoke(new Action(() => OutsideClick(x, y))));
            if (!collapsed) RestartHideTimer();
            if (!preview && store.State.FollowFileDialogs) { try { SetDialogFollow(true); } catch (Exception ex) { Toast(ex.Message, true); } }
            if (collapsed) ShowEdgeWindow();
            if (store.RecoveryNotice != null) Toast(store.RecoveryNotice);
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => {
                if (exiting || animating || islandWindow != null || SystemParameters.HighContrast || (RenderCapability.Tier >> 16) < 2) return;
                try {
                    var edge = EnsureEdgeWindow(); edge.PrepareAt(handle, store.State.Side); _ = edge.CaptureCapsule();
                    islandWindow = new IslandWindow(); islandWindow.Prewarm();
                }
                catch (Exception error) { Trace.WriteLine("PathDock animation prewarm skipped: " + error); }
            }));
            if (!preview) await CollectRecent();
        };
        targetTimer.Tick += (_, _) => { if (!animating) { TrackTarget(); TryRenderPending(); } };
        recentTimer.Tick += async (_, _) => { if (!preview) await CollectRecent(); };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); if (!store.State.KeepOpen && !dialogOpen && menuDepth == 0 && !draggingPanel) Collapse(); };
        outsideHideTimer.Tick += (_, _) => {
            outsideHideTimer.Stop();
            if (CanDismissOutside()) Collapse();
        };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); status.Text = ""; status.Visibility = Visibility.Collapsed; };
        MouseMove += (_, _) => RestartHideTimer();
        PreviewMouseDown += (_, _) => RestartHideTimer();
        PreviewMouseWheel += (_, _) => RestartHideTimer();
        Deactivated += (_, _) => { Native.NoActivate(this, true); Keyboard.ClearFocus(); };
        PreviewKeyDown += (_, e) => { RestartHideTimer(); if (e.Key == Key.Escape) { Collapse(); e.Handled = true; } };
        AllowDrop = true; DragOver += DragOverFolder; Drop += DropFolder;
        SizeChanged += (_, _) => { if (handle != IntPtr.Zero) { UpdateDockRegion(); if (collapsed && !animating) ShowEdgeWindow(); } };
        Closing += (_, _) => { exiting = true; outsideClickMonitor?.Dispose(); dialogFollow?.Dispose(); edgeWindow?.Close(); islandWindow?.Close(); CompositionTarget.Rendering -= AnimateDock; targetTimer.Stop(); recentTimer.Stop(); hideTimer.Stop(); outsideHideTimer.Stop(); toastTimer.Stop(); Native.UnregisterHotKey(handle, 1); source?.RemoveHook(WindowMessage); tray?.ContextMenuStrip?.Dispose(); tray?.Icon?.Dispose(); tray?.Dispose(); store.Save(); };
    }
    void RestartHideTimer()
    {
        outsideHideTimer.Stop();
        hideTimer.Stop();
        if (!collapsed && !animating && !performing && !store.State.KeepOpen && !dialogOpen && menuDepth == 0 && !draggingPanel) hideTimer.Start();
    }
    void OutsideClick(int x, int y)
    {
        if (!CanDismissOutside() || handle == IntPtr.Zero) return;
        var bounds = Native.WindowBounds(handle);
        if (x >= bounds.Left && x < bounds.Right && y >= bounds.Top && y < bounds.Bottom) return;
        hideTimer.Stop(); outsideHideTimer.Stop(); outsideHideTimer.Start();
    }
    bool CanDismissOutside() => !exiting && !collapsed && !animating && !performing && !store.State.KeepOpen && !dialogOpen && menuDepth == 0 && !draggingPanel;
    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (message == 0x0021 && !dialogOpen) { handled = true; return new IntPtr(3); }
        if (message == 0x0312 && wp.ToInt32() == 1) { if (collapsed) Expand(); else Collapse(); handled = true; }
        if (message == 0x001A || message == 0x02E0 || message == 0x007E) Dispatcher.BeginInvoke(new Action(Place));
        return IntPtr.Zero;
    }
    void Build()
    {
        surface.Margin = new Thickness(0); surface.CornerRadius = new CornerRadius(0);
        surface.Background = Ui.PanelBackground; surface.BorderBrush = SystemParameters.HighContrast ? SystemColors.WindowTextBrush : Ui.Brush("#D7DBE2"); surface.BorderThickness = new Thickness(1);
        surface.Child = layout; surface.RenderTransform = panelSlide; host.Background = Ui.PanelBackground; host.Children.Add(surface); Content = host;
        EdgeHandle.ConfigureVisual(edgePresentation);
        edgeButton = Ui.Button(edgePresentation);
        edgeButton.Width = 28; edgeButton.Height = 56; edgeButton.Padding = new Thickness(0); edgeButton.ToolTip = "PathDock";
        edgeButton.VerticalAlignment = VerticalAlignment.Center; edgeButton.HorizontalAlignment = HorizontalAlignment.Right;
        edgeButton.Background = Brushes.Transparent; edgeButton.Opacity = 0; edgeButton.IsHitTestVisible = false;
        edgeButton.Click += (_, _) => Expand(); host.Children.Add(edgeButton);
        layout.Margin = new Thickness(7, 0, 7, 6);
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = 36, Background = Ui.HeaderBackground, Margin = new Thickness(-7, 0, -7, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 0, 0) };
        brand.Children.Add(new Image { Source = AppIcon.Image, Width = 16, Height = 16, Margin = new Thickness(5, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Ui.Text("PathDock", Ui.ExplorerFontSize, Ui.Ink, true));
        header.MouseLeftButtonDown += (_, e) => { if (e.ClickCount != 1 || IsTitleButton(e.OriginalSource as DependencyObject)) return; try { draggingPanel = true; hideTimer.Stop(); DragMove(); } catch (InvalidOperationException) { } finally { draggingPanel = false; SavePanelPosition(); RestartHideTimer(); } e.Handled = true; };
        header.Children.Add(brand);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        pin = Ui.IconButton("\uE718", "固定面板"); UpdatePin();
        pin.Click += (_, _) => { store.State.KeepOpen = !store.State.KeepOpen; store.Save(); UpdatePin(); RestartHideTimer(); };
        tools.Children.Add(pin);
        var more = Ui.IconButton("\uE712", "更多"); more.Click += (_, _) => ShowPanelMenu(more); tools.Children.Add(more);
        Grid.SetColumn(tools, 1); header.Children.Add(tools); layout.Children.Add(header);
        var lists = new StackPanel();
        lists.Children.Add(SectionHeader("固定")); lists.Children.Add(fixedList);
        lists.Children.Add(SectionHeader("最近")); lists.Children.Add(recentList);
        folderScroll.Content = lists; folderScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        folderScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled; folderScroll.PanningMode = PanningMode.VerticalOnly;
        Grid.SetRow(folderScroll, 1); layout.Children.Add(folderScroll);
        status.Text = ""; status.Visibility = Visibility.Collapsed; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(5, 5, 5, 2);
        Grid.SetRow(status, 2); layout.Children.Add(status);
    }
    static bool IsTitleButton(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase) return true;
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }
    void ShowPanelMenu(FrameworkElement anchor)
    {
        var menu = NewMenu();
        AddAction(menu, "点击行为…", () => { ShowModeMenu(anchor); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddAction(menu, "设置…", () => { ShowSettings(); return Task.CompletedTask; });
        AddAction(menu, "收起", () => { CollapseFromMenu(menu); return Task.CompletedTask; });
        menu.Items.Add(new Separator());
        AddAction(menu, "退出", () => { Close(); return Task.CompletedTask; });
        OpenMenu(menu, anchor);
    }
    void CollapseFromMenu(ContextMenu menu)
    {
        menu.IsOpen = false;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(Collapse));
    }
    Grid SectionHeader(string text)
    {
        var grid = new Grid { Height = 27, Margin = new Thickness(5, 2, 5, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition());
        var label = new StackPanel { Orientation = Orientation.Horizontal };
        var marker = Ui.Icon(text == "固定" ? "\uE734" : "\uE81C", 12, SystemParameters.HighContrast ? Ui.Ink : Ui.Brush(text == "固定" ? "#BE852D" : "#168D99"));
        marker.Margin = new Thickness(0, 0, 6, 0); label.Children.Add(marker);
        label.Children.Add(Ui.Text(text, Ui.ExplorerFontSize, Ui.Muted)); grid.Children.Add(label);
        var line = new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(9, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1); grid.Children.Add(line); return grid;
    }
    void TryRenderPending()
    {
        if (renderPending && (collapsed || !IsMouseOver)) Render();
    }
    sealed record RowStamp(FolderEntry Entry, string Name, string Path, bool Pinned);
    void SynchronizeRows(StackPanel panel, System.Collections.Generic.IReadOnlyList<FolderEntry> entries)
    {
        if (entries.Count == 0)
        {
            if (panel.Children.Count != 1 || panel.Children[0] is not Border)
            { panel.Children.Clear(); panel.Children.Add(Empty("无项目", "")); }
            return;
        }
        var existing = panel.Children.OfType<Button>().ToArray();
        var rows = entries.Select(item => existing.FirstOrDefault(row => row.Tag is RowStamp stamp
            && ReferenceEquals(stamp.Entry, item) && stamp.Name == item.Name && stamp.Path == item.Path && stamp.Pinned == item.Pinned) ?? (Button)FolderRow(item)).ToArray();
        foreach (var old in panel.Children.Cast<UIElement>().Where(row => !rows.Contains(row)).ToArray()) panel.Children.Remove(old);
        for (var i = 0; i < rows.Length; i++)
        {
            if (i < panel.Children.Count && ReferenceEquals(panel.Children[i], rows[i])) continue;
            panel.Children.Remove(rows[i]); panel.Children.Insert(i, rows[i]);
        }
    }
    void Render()
    {
        if (animating || draggingPanel || dialogOpen || menuDepth > 0 || Mouse.LeftButton == MouseButtonState.Pressed)
        { renderPending = true; return; }
        renderPending = false;
        var fixedItems = store.State.Folders.Where(f => f.Pinned).ToList();
        SynchronizeRows(fixedList, fixedItems);
        var recent = store.State.Folders.Where(f => !f.Pinned && f.LastUsed != null).OrderByDescending(f => f.LastUsed).Take(10).ToList();
        SynchronizeRows(recentList, recent);
        // A collapsed startup still needs the same content height as an expanded
        // startup. Keep measurement independent of visibility and animation.
        var available = new Size(PanelWidth - 16, double.PositiveInfinity);
        fixedList.Measure(available); recentList.Measure(available);
        desiredHeight = Math.Max(180, 108 + fixedList.DesiredSize.Height + recentList.DesiredSize.Height);
        if (IsLoaded && !animating) Place();
    }
    UIElement Empty(string title, string text) => new Border { Height = 30, Padding = new Thickness(29, 0, 0, 0), Child = Ui.Text(title, 12, Ui.Muted) };
    UIElement FolderRow(FolderEntry item)
    {
        var body = new Grid { MinHeight = 30 }; body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(25) }); body.ColumnDefinitions.Add(new ColumnDefinition());
        body.Children.Add(new Image { Source = Native.FolderIcon(), Width = 16, Height = 16, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 7, 0, 0) });
        var label = Ui.Text(item.Pinned ? item.Name : item.Path, Ui.ExplorerFontSize);
        label.Margin = new Thickness(0, 5, 0, 5); label.VerticalAlignment = VerticalAlignment.Center;
        if (!item.Pinned) { label.TextWrapping = TextWrapping.Wrap; label.TextTrimming = TextTrimming.None; }
        Grid.SetColumn(label, 1); body.Children.Add(label);
        var button = Ui.Button(body); button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(6, 0, 6, 0);
        button.Tag = new RowStamp(item, item.Name, item.Path, item.Pinned);
        button.ToolTip = item.Path; System.Windows.Automation.AutomationProperties.SetName(button, item.Name);
        button.Click += async (_, _) => await Perform(item, store.State.ActionMode, button, target);
        button.ContextMenu = new ContextMenu();
        button.ContextMenuOpening += (_, e) => { e.Handled = true; OpenMenu(BuildFolderMenu(item, target), button); }; return button;
    }
    ContextMenu BuildFolderMenu(FolderEntry item, TargetWindow? destination)
    {
        var menu = NewMenu();
        AddAction(menu, "打开文件夹", async () => await Perform(item, "explorer", null, destination), glyph: "\uE8B7");
        AddAction(menu, "在终端中打开", async () => await Perform(item, "terminal", null, destination), glyph: "\uE756");
        var targetName = destination == null ? "" : System.Text.RegularExpressions.Regex.Replace(string.IsNullOrWhiteSpace(destination.Title) ? destination.ProcessName : destination.Title, @"\s*\[PD:[0-9a-f]+\]", "");
        var pasteHint = new StackPanel { MaxWidth = 260 };
        pasteHint.Children.Add(Ui.Text(destination == null ? "请先选择要输入的窗口。" : "目标：" + targetName, Ui.ExplorerFontSize));
        pasteHint.Children.Add(Ui.Text("仅粘贴路径，不自动回车。", Ui.ExplorerFontSize, Ui.Muted));
        var paste = new MenuItem { Header = "粘贴路径", Icon = Ui.Icon("\uE77F", 14), IsEnabled = destination != null, Padding = new Thickness(10, 6, 10, 6), ToolTip = new ToolTip { Content = pasteHint, MaxWidth = 284 } };
        paste.Click += async (_, _) => await Perform(item, "insert", null, destination); menu.Items.Add(paste);
        AddAction(menu, "复制路径", () => Copy(item.Path), glyph: "\uE8C8");
        var sessions = availableSessions;
        if (sessions.Count > 0)
        {
            menu.Items.Add(new Separator());
            var submenu = new MenuItem { Header = "切换 PowerShell 目录", Icon = Ui.Icon("\uE8AB", 14) };
            foreach (var session in sessions)
            {
                var captured = session; var entry = new MenuItem { Header = new TextBlock { Text = $"{session.TitleMarker}  {session.Cwd}", MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = $"选择标题中有 {session.TitleMarker} 的终端。" };
                entry.Click += async (_, _) => await SwitchSelectedSession(captured, item); submenu.Items.Add(entry);
            }
            menu.Items.Add(submenu);
        }
        menu.Items.Add(new Separator());
        AddAction(menu, item.Pinned ? "取消收藏" : "添加到收藏", () => { item.Pinned = !item.Pinned; if (!item.Pinned && item.LastUsed == null) store.State.Folders.Remove(item); store.Save(); Render(); return Task.CompletedTask; }, glyph: "\uE734");
        AddAction(menu, "编辑…", () => { EditFolder(item); return Task.CompletedTask; }, glyph: "\uE70F");
        if (item.Pinned) { AddAction(menu, "上移", () => { MoveItem(item, -1); return Task.CompletedTask; }); AddAction(menu, "下移", () => { MoveItem(item, 1); return Task.CompletedTask; }); }
        AddAction(menu, "从列表移除", () => { store.Hide(item); Render(); Toast("已移除快捷入口，文件夹不受影响"); return Task.CompletedTask; }, glyph: "\uE711");
        return menu;
    }
    void MoveItem(FolderEntry item, int direction)
    {
        var pinned = store.State.Folders.Where(f => f.Pinned).ToList(); var index = pinned.IndexOf(item); var next = index + direction; if (next < 0 || next >= pinned.Count) return;
        var other = pinned[next]; var a = store.State.Folders.IndexOf(item); var b = store.State.Folders.IndexOf(other); (store.State.Folders[a], store.State.Folders[b]) = (store.State.Folders[b], store.State.Folders[a]); store.Save(); Render();
    }
    ContextMenu NewMenu()
    {
        var menu = new ContextMenu { FontFamily = FontFamily, FontSize = 12, Padding = new Thickness(5) };
        menu.Opened += (_, _) => { openMenus.RemoveWhere(m => !m.IsOpen); openMenus.Add(menu); menuDepth = openMenus.Count; hideTimer.Stop(); };
        menu.Closed += (_, _) => { openMenus.RemoveWhere(m => !m.IsOpen); menuDepth = openMenus.Count; RestartHideTimer(); };
        return menu;
    }
    void OpenMenu(ContextMenu menu, FrameworkElement anchor) { menu.PlacementTarget = anchor; menu.IsOpen = true; }
    static void AddAction(ContextMenu menu, string name, Func<Task> action, bool enabled = true, string? glyph = null)
    {
        var entry = new MenuItem { Header = name, IsEnabled = enabled, Padding = new Thickness(10, 6, 10, 6) }; if (glyph != null) entry.Icon = Ui.Icon(glyph, 14); entry.Click += async (_, _) => await action(); menu.Items.Add(entry);
    }
    async Task Perform(FolderEntry item, string action, FrameworkElement? anchor, TargetWindow? destination)
    {
        if (performing) { Toast("正在处理，请稍候再试"); return; }
        performing = true;
        hideTimer.Stop();
        try
        {
            InputObservation? automaticFocus = null;
            if (action == "auto")
            {
                var current = Native.ReadTarget();
                if (current != null) destination = current;
                if (destination?.IsTerminal == true)
                {
                    var session = await Task.Run(() => Services.MatchSession(store.Root, destination));
                    if (session != null && Native.GetForegroundWindow() == destination.Handle)
                    {
                        await Services.SwitchSession(store.Root, session, item.Path, destination);
                        store.Visit(item.Path, "PowerShell"); Render(); return;
                    }
                    automaticFocus = await AutoTarget.Observe(destination);
                    if (automaticFocus.Kind != InputKind.Codex) { OpenMenu(BuildFolderMenu(item, destination), anchor ?? surface); return; }
                    action = "insert";
                }
                else if (destination != null)
                {
                    automaticFocus = await AutoTarget.Observe(destination);
                    action = automaticFocus.Kind == InputKind.TextBox ? "insert" : "explorer";
                }
                else action = "explorer";
            }
            if (action == "insert")
            {
                if (destination == null) throw new InvalidOperationException("请先选中目标窗口，再点击目录。");
                Native.EnsurePasteAllowed(destination);
                var now = Native.GetForegroundWindow();
                if (now != destination.Handle && now != handle) throw new InvalidOperationException("目标已变化，请重新选择后再插入。");
                await Services.SetClipboardText(item.Path);
                if (exiting) return;
                now = Native.GetForegroundWindow();
                if (now != destination.Handle && now != handle) throw new InvalidOperationException("目标已变化，未插入；路径已复制。");
                if (now != destination.Handle)
                {
                    Native.SetForegroundWindow(destination.Handle);
                    await Task.Delay(90);
                }
                if (automaticFocus != null)
                {
                    var confirmed = await AutoTarget.Observe(destination);
                    if (confirmed.Kind != automaticFocus.Kind || confirmed.FocusId != automaticFocus.FocusId || confirmed.Kind == InputKind.Unknown)
                        throw new InvalidOperationException("输入焦点已变化，未自动插入；路径已复制。");
                }
                Native.Paste(destination);
                Toast("已发送路径粘贴，不回车 · 剪贴板保留该路径"); return;
            }
            var exists = await Task.Run(() => Directory.Exists(item.Path)).WaitAsync(TimeSpan.FromSeconds(3));
            if (exiting) return;
            if (!exists) throw new DirectoryNotFoundException("目录不存在或暂时无法访问，可在菜单中修改路径。");
            ProcessStartInfo info;
            if (action == "terminal") { info = new("wt.exe") { UseShellExecute = true }; info.ArgumentList.Add("-w"); info.ArgumentList.Add("0"); info.ArgumentList.Add("new-tab"); info.ArgumentList.Add("-d"); info.ArgumentList.Add(item.Path); }
            else { info = new("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add(item.Path); }
            await Task.Run(() =>
            {
                using var launched = Process.Start(info);
                if (launched == null) throw new IOException("系统未返回启动结果。");
            });
            store.Visit(item.Path, action == "terminal" ? "终端打开请求" : "目录打开请求"); Render(); Toast(action == "terminal" ? "已请求在新终端中打开" : "已请求打开目录");
        }
        catch (TimeoutException ex) { Toast(ex.Message, true); }
        catch (Exception ex) { Toast(ex.Message, true); }
        finally { performing = false; RestartHideTimer(); }
    }
    async Task SwitchSelectedSession(ShellSession session, FolderEntry item)
    {
        if (performing) { Toast("正在处理，请稍候再试"); return; }
        performing = true; hideTimer.Stop();
        try
        {
            await Services.SwitchSession(store.Root, session, item.Path);
            if (exiting) return;
            store.Visit(item.Path, "PowerShell"); Render(); Toast("已切换所选 PowerShell 会话");
        }
        catch (Exception ex) { Toast(ex.Message, true); }
        finally { performing = false; RestartHideTimer(); }
    }
    async Task Copy(string path)
    {
        if (performing) { Toast("正在处理，请稍候再试"); return; }
        performing = true;
        hideTimer.Stop();
        try { await Services.SetClipboardText(path); Toast("路径已复制"); }
        catch (Exception ex) { Toast(ex.Message, true); }
        finally { performing = false; RestartHideTimer(); }
    }
    void TrackTarget()
    {
        if (dialogOpen || menuDepth > 0) return;
        var currentForeground = Native.GetForegroundWindow();
        if (currentForeground != previousForeground)
        {
            previousForeground = currentForeground;
            if (collapsed && !animating) ShowEdgeWindow();
        }
        var foreground = Native.ReadTarget(); if (foreground != null) target = foreground;
    }
    void ShowModeMenu(FrameworkElement anchor)
    {
        var menu = NewMenu();
        foreach (var option in new[] { ("auto", "自动识别：切换目录 / 插入 / 打开"), ("explorer", "始终用资源管理器打开"), ("terminal", "始终在新终端标签打开"), ("insert", "始终插入路径，不回车") })
        {
            var (mode, label) = option; var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = store.State.ActionMode == mode }; entry.Click += (_, _) => { store.State.ActionMode = mode; store.Save(); TrackTarget(); }; menu.Items.Add(entry);
        }
        OpenMenu(menu, anchor);
    }
    async Task CollectRecent()
    {
        if (polling || exiting || animating) return; polling = true; var changed = false;
        try
        {
            activityRead ??= Task.Run(() => activityReader.Read(store.Root));
            if (sessionRead == null || sessionRead.IsCompleted) sessionRead = Task.Run(() => Services.Sessions(store.Root));
            var discovered = new List<string>();
            if (store.State.RecordRecent)
            {
                if (explorerRead == null || explorerRead.IsCompleted) explorerRead = Services.ExplorerFolders();
                try
                {
                    var folders = await explorerRead.WaitAsync(TimeSpan.FromSeconds(2));
                    discovered = folders.Where(path => !explorerSeen.Contains(path)).ToList();
                    explorerSeen = new(folders, StringComparer.OrdinalIgnoreCase);
                }
                catch { /* A slow Explorer must not prevent collecting shell history. */ }
            }
            var activities = await activityRead.WaitAsync(TimeSpan.FromSeconds(2));
            activityRead = null;
            try { availableSessions = await sessionRead.WaitAsync(TimeSpan.FromMilliseconds(100)); } catch { }
            if (exiting) return;
            using (store.BatchUpdates())
            {
                foreach (var path in discovered) changed |= store.Visit(path, "资源管理器 · 发现");
                foreach (var activity in activities) changed |= store.Visit(activity.Path, "PowerShell", activity.Time);
            }
            if (changed) { renderPending = true; TryRenderPending(); }
        }
        catch { if (activityRead?.IsCompleted == true) activityRead = null; /* Retry optional sources on the next poll. */ }
        finally { polling = false; }
    }
    void EnableEditing() { Native.NoActivate(this, false); Activate(); Native.SetForegroundWindow(handle); }
    void WithDialog(Action show)
    {
        dialogOpen = true; hideTimer.Stop(); EnableEditing();
        try { show(); } finally { dialogOpen = false; Native.NoActivate(this, true); Render(); RestartHideTimer(); }
    }
    void EditFolder(FolderEntry item)
    {
        WithDialog(() =>
        {
            var dialog = new DockDialog(this, "编辑目录", "", height: 310);
            dialog.Body.Children.Add(Ui.Text("显示名称", 12, Ui.Muted)); var name = Ui.Input(item.Name); name.Margin = new Thickness(0, 7, 0, 16); dialog.Body.Children.Add(name);
            dialog.Body.Children.Add(Ui.Text("完整路径", 12, Ui.Muted)); var path = Ui.Input(item.Path); path.Margin = new Thickness(0, 7, 0, 20); dialog.Body.Children.Add(path);
            var save = Ui.Button("保存修改", true); save.Click += (_, _) => { try { var normalized = DockStore.Normalize(path.Text); if (store.State.Folders.Any(f => f.Id != item.Id && string.Equals(f.Path, normalized, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("这个目录已经在列表中。"); item.Path = normalized; item.Name = string.IsNullOrWhiteSpace(name.Text) ? DockStore.DefaultName(normalized) : name.Text.Trim(); store.Save(); dialog.Close(); } catch (Exception ex) { Ui.Message(dialog, ex.Message); } }; dialog.Body.Children.Add(save); dialog.ShowDialog();
        });
    }
    void SetDialogFollow(bool enabled)
    {
        if (enabled) dialogFollow ??= new DialogFollow(action => Dispatcher.BeginInvoke(action));
        else { dialogFollow?.Dispose(); dialogFollow = null; }
    }
    void ShowSettings() => WithDialog(() => CreateSettingsDialog().ShowDialog());
    internal DockDialog CreateSettingsDialog()
    {
        var dialog = new DockDialog(this, "设置", "", 400, 400);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.MaxHeight = Math.Max(300, Forms.Screen.FromHandle(handle).WorkingArea.Height / VisualTreeHelper.GetDpi(this).DpiScaleY - 40);
        void Section(string title, bool separated = false)
        {
            if (separated) dialog.Body.Children.Add(new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(0, 12, 0, 16) });
            var heading = Ui.Text(title, Ui.ExplorerFontSize + 1, Ui.Muted);
            heading.Margin = new Thickness(0, 0, 0, 8); dialog.Body.Children.Add(heading);
        }
        Section("面板与目录");
        void Description(string text)
        {
            var note = Ui.Text(text, Math.Max(10, Ui.ExplorerFontSize - 1), Ui.Muted);
            note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(24, 0, 0, 6);
            dialog.Body.Children.Add(note);
        }
        void Toggle(string label, bool initial, Action<bool> apply, string? description = null)
        {
            var box = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(2, 5, 0, description == null ? 7 : 3), FontSize = Ui.ExplorerFontSize };
            box.Click += (_, _) => { apply(box.IsChecked == true); store.Save(); }; dialog.Body.Children.Add(box);
            if (description != null) Description(description);
        }
        Toggle("固定面板", store.State.KeepOpen, v => { store.State.KeepOpen = v; UpdatePin(); });
        Toggle("记录最近目录", store.State.RecordRecent, v => store.State.RecordRecent = v, "记录资源管理器、PowerShell 和 PathDock 中使用的目录。");
        var follow = new CheckBox { Content = "文件对话框跟随资源管理器", IsChecked = store.State.FollowFileDialogs, IsEnabled = !preview,
            Margin = new Thickness(2, 5, 0, 3), FontSize = Ui.ExplorerFontSize,
            ToolTip = "从文件对话框切换到资源管理器，再返回时，自动进入刚刚浏览的目录。适用于支持的 Windows 标准文件对话框。" };
        follow.Click += (_, _) => {
            var previous = store.State.FollowFileDialogs;
            try { SetDialogFollow(follow.IsChecked == true); store.State.FollowFileDialogs = follow.IsChecked == true; store.Save(); }
            catch (Exception ex) {
                store.State.FollowFileDialogs = previous;
                SetDialogFollow(previous);
                follow.IsChecked = previous;
                Ui.Message(dialog, ex.Message);
            }
        };
        dialog.Body.Children.Add(follow);
        Description("返回文件对话框时，自动切换到刚浏览的目录。");
        var installed = File.Exists(Path.Combine(store.Root, "integration.json"));
        Section("PowerShell", true);
        var integrationBody = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
        var integration = new CheckBox { Content = "在 PowerShell 中切换目录", IsChecked = installed, FontSize = Ui.ExplorerFontSize, Margin = new Thickness(0, 6, 0, 8), IsEnabled = !preview };
        integration.ToolTip = "备份并追加用户 PowerShell 配置。仅在空提示符下切换；有未提交输入或正在运行命令时不切换。";
        var state = Ui.Text(installed ? "在空提示符下点击目录，即可切换当前路径。" : "启用后，在新打开的 PowerShell 中生效。", Ui.ExplorerFontSize, Ui.Muted);
        state.TextWrapping = TextWrapping.Wrap; state.Margin = new Thickness(22, 0, 0, 0);
        integration.Click += async (_, _) =>
        {
            var enable = integration.IsChecked == true; integration.IsEnabled = false;
            try
            {
                await Services.ManageIntegration(store.Root, enable); installed = enable;
                state.Text = enable ? "已启用，新开 PowerShell 标签生效。" : "已停用，重启 PowerShell 后生效。";
            }
            catch (Exception ex) { integration.IsChecked = installed; Ui.Message(dialog, ex.Message); }
            finally { integration.IsEnabled = true; }
        };
        integrationBody.Children.Add(integration); integrationBody.Children.Add(state);
        dialog.Body.Children.Add(integrationBody);
        return dialog;
    }
    void UpdatePin() { if (pin != null) { pin.Background = store.State.KeepOpen ? Ui.PressBackground : Brushes.Transparent; pin.ToolTip = store.State.KeepOpen ? "取消固定面板" : "固定面板"; } }
    void Toast(string message, bool error = false) { if (!error) return; status.Text = message; status.Visibility = Visibility.Visible; status.Foreground = error ? Ui.Brush("#B56847") : Ui.Accent; toastTimer.Stop(); toastTimer.Start(); }
    void DragOverFolder(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    async void DropFolder(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        try { var valid = await Task.Run(() => paths.Where(Directory.Exists).ToArray()).WaitAsync(TimeSpan.FromSeconds(3)); using (store.BatchUpdates()) foreach (var path in valid) store.AddPinned(path); if (collapsed) Expand(); Render(); Toast($"已添加 {valid.Length} 个目录"); }
        catch (Exception ex) { Toast(ex.Message, true); }
    }
    void Collapse()
    {
        // Closed fires after the system menu fade animation. IsOpen already
        // reflects the user's selection, so do not block on that delayed event.
        openMenus.RemoveWhere(menu => !menu.IsOpen); menuDepth = openMenus.Count;
        if (dialogOpen || menuDepth > 0 || collapsed && !animating) return;
        Keyboard.ClearFocus(); Native.NoActivate(this, true); StartTransition(false);
    }
    void Expand()
    {
        if (!collapsed && !animating) return;
        if (!animating) { folderScroll.ScrollToTop(); collapsed = false; Place(); }
        StartTransition(true);
        if (islandActive) return;
        Show();
        if (!openingHandleCover) { edgeWindow?.Hide(); edgeWindow?.ClearTail(); }
        Native.KeepAbove(handle);
        if (openingHandleCover) edgeWindow?.KeepOpeningCover();
    }
    void StartTransition(bool expand, bool? animate = null)
    {
        outsideHideTimer.Stop();
        hideTimer.Stop();
        var destination = expand ? 1d : 0d;
        if (animating && transitionTo == destination) return;
        transitionVelocity = animating ? motionVelocity : 0;
        openingHandleCover = expand && edgeWindow?.IsVisible == true;
        if (openingHandleCover) edgeWindow!.SuspendInput();
        lastRenderingTime = TimeSpan.MinValue;
        lastTransitionPlacement = null;
        collapsed = !expand;
        transitionFrom = reveal; transitionTo = destination;
        surface.IsHitTestVisible = false;
        if (edgeButton != null) edgeButton.IsHitTestVisible = false;
        floatingTransition = store.State.FloatingPanel;
        if (floatingTransition)
        {
            var bounds = Native.WindowBounds(handle); var width = bounds.Right - bounds.Left; var height = bounds.Bottom - bounds.Top;
            var area = Forms.Screen.FromPoint(new System.Drawing.Point((int)store.State.ScreenX, (int)store.State.ScreenY)).WorkingArea;
            var horizontal = store.State.Side is "top" or "bottom";
            dockPosition = new Point(horizontal ? area.Left + Math.Round(Math.Max(0, area.Width - width) * store.State.HorizontalRatio) : store.State.Side == "left" ? area.Left : area.Right - width,
                horizontal ? store.State.Side == "top" ? area.Top : area.Bottom - height : area.Top + Math.Round(Math.Max(0, area.Height - height) * store.State.VerticalRatio));
            floatingPosition = new Point(Math.Clamp(store.State.PanelX, area.Left, Math.Max(area.Left, area.Right - width)), Math.Clamp(store.State.PanelY, area.Top, Math.Max(area.Top, area.Bottom - height)));
        }
        // Render the edge behind the opaque panel before motion. The final handoff
        // then uses an already-painted window rather than showing a fresh HWND.
        if (!expand)
        {
            if (animate ?? SystemParameters.ClientAreaAnimation) EnsureEdgeWindow().WarmBehind(handle, store.State.Side);
            else EnsureEdgeWindow().PrepareAt(handle, store.State.Side);
        }
        CompositionTarget.Rendering -= AnimateDock;
        if (!(animate ?? SystemParameters.ClientAreaAnimation))
        {
            islandWindow?.Cancel(); islandActive = false; islandCovered = false;
            reveal = destination; CompleteTransition(); return;
        }
        if (!BeginIslandTransition()) islandWindow?.Cancel();
        animating = true; waitingForFirstRender = expand && !IsVisible;
        // Showing a hidden HWND must not consume the visible animation's time.
        if (waitingForFirstRender) slideClock.Reset(); else slideClock.Restart();
        if (!islandActive) { PositionTransition(); UpdateDockRegion(); if (floatingTransition) edgeWindow?.TrackBehind(); }
        CompositionTarget.Rendering += AnimateDock;
    }
    void PositionTransition()
    {
        if (!floatingTransition) return;
        var bounds = Native.WindowBounds(handle);
        var x = dockPosition.X + (floatingPosition.X - dockPosition.X) * reveal;
        var y = dockPosition.Y + (floatingPosition.Y - dockPosition.Y) * reveal;
        var placement = ((int)Math.Round(x), (int)Math.Round(y), bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        if (lastTransitionPlacement != placement && Native.PositionDockWindow(handle, placement.Item1, placement.Item2, placement.Item3, placement.Item4))
            lastTransitionPlacement = placement;
    }
    partial void BeginAnimationFrame();
    partial void EndAnimationFrame();
    void AnimateDock(object? sender, EventArgs e)
    {
        if (!animating) return;
        // WPF can raise Rendering again for the same composition frame after a
        // layout invalidation. Only update HWNDs once for that frame.
        if (e is RenderingEventArgs rendering)
        {
            if (rendering.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = rendering.RenderingTime;
        }
        BeginAnimationFrame();
        try
        {
            if (islandActive && !islandCovered)
            {
                if (islandWindow?.HasPresented != true) { slideClock.Restart(); return; }
                islandCovered = true; Hide(); edgeWindow?.Hide(); openingHandleCover = false;
                slideClock.Restart(); waitingForFirstRender = false;
            }
            var firstFrame = waitingForFirstRender;
            if (firstFrame) { waitingForFirstRender = false; slideClock.Restart(); }
            var frame = DockMotion.Sample(transitionFrom, transitionTo, transitionVelocity, firstFrame ? 0 : slideClock.Elapsed.TotalSeconds);
            reveal = frame.Position; motionVelocity = frame.Velocity;
            motionFrames++;
            if (islandActive) PresentIslandFrame();
            else { PositionTransition(); UpdateDockRegion(); if (floatingTransition) edgeWindow?.TrackBehind(); }
            if (!frame.Complete) return;
            // Keep the final panel frame until the replacement has produced content.
            if (collapsed && edgeWindow is { HasRenderedContent: false }) return;
            reveal = transitionTo; CompleteTransition();
        }
        finally { EndAnimationFrame(); }
    }
    void CompleteTransition()
    {
        var finishingIsland = islandActive ? islandWindow : null;
        islandActive = false; islandCovered = false;
        animating = false; waitingForFirstRender = false; openingHandleCover = false;
        motionVelocity = 0;
        CompositionTarget.Rendering -= AnimateDock; slideClock.Stop();
        surface.IsHitTestVisible = !collapsed;
        if (edgeButton != null) edgeButton.IsHitTestVisible = collapsed;
        Place(); // Place already positions and shows the collapsed edge window.
        if (!collapsed) { Show(); edgeWindow?.Hide(); edgeWindow?.ClearTail(); }
        finishingIsland?.CompleteHandoff();
        if (!collapsed) RestartHideTimer();
    }
    EdgeWindow EnsureEdgeWindow() => edgeWindow ??= new EdgeWindow(Expand,
        (x, y) => { store.State.FloatingPanel = false; DockAt(x, y); });
    void ShowEdgeWindow()
    {
        if (exiting || animating || !collapsed) return;
        EnsureEdgeWindow().ShowAt(handle, store.State.Side);
        Hide();
        // Restore the retained main HWND only after it is no longer visible.
        UpdateDockRegion();
    }
    void UpdateDockRegion()
    {
        if (islandActive) return;
        if (handle == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var horizontal = store.State.Side is "top" or "bottom";
        var parked = collapsed && !animating;
        var visibleWidth = parked ? (horizontal ? 56 : 28) : horizontal ? ActualWidth : ActualWidth * reveal;
        var visibleHeight = parked ? (horizontal ? 28 : 56) : horizontal ? ActualHeight * reveal : ActualHeight;
        var x = horizontal ? (ActualWidth - visibleWidth) / 2 : store.State.Side == "left" ? 0 : ActualWidth - visibleWidth;
        var y = horizontal ? store.State.Side == "top" ? 0 : ActualHeight - visibleHeight : (ActualHeight - visibleHeight) / 2;
        panelSlide.X = horizontal ? 0 : Math.Round((store.State.Side == "left" ? -1 : 1) * (ActualWidth - visibleWidth) * dpi.DpiScaleX) / dpi.DpiScaleX;
        panelSlide.Y = horizontal ? Math.Round((store.State.Side == "top" ? -1 : 1) * (ActualHeight - visibleHeight) * dpi.DpiScaleY) / dpi.DpiScaleY : 0;
        var primaryExtent = horizontal ? visibleHeight : visibleWidth;
        var edgeExtent = 28d;
        var edgeReveal = Math.Clamp((edgeExtent - primaryExtent) / edgeExtent, 0, 1);
        if (openingHandleCover && primaryExtent >= edgeExtent)
        {
            openingHandleCover = false; edgeWindow?.Hide(); edgeWindow?.ClearTail();
        }
        else if (openingHandleCover && floatingTransition) edgeWindow?.KeepOpeningCover();
        if (edgeButton != null)
        {
            if (edgeVisualSide != store.State.Side)
            {
                edgeButton.Width = horizontal ? 56 : 28; edgeButton.Height = horizontal ? 28 : 56;
                edgePresentation.Width = edgeButton.Width; edgePresentation.Height = edgeButton.Height;
                EdgeHandle.ApplyDockSide(edgePresentation, store.State.Side);
                edgeButton.HorizontalAlignment = horizontal ? HorizontalAlignment.Center : store.State.Side == "left" ? HorizontalAlignment.Left : HorizontalAlignment.Right;
                edgeButton.VerticalAlignment = horizontal ? store.State.Side == "top" ? VerticalAlignment.Top : VerticalAlignment.Bottom : VerticalAlignment.Center;
                edgeVisualSide = store.State.Side;
            }
            edgeButton.Opacity = parked ? 1 : 0;
            if (collapsed && animating && edgeReveal > 0) edgeWindow?.RevealHandle();
        }
        if (!parked && reveal == 0)
        {
            ApplyDockRegion(new(0, 0, 0, 0, 0, null, 1, true));
            return;
        }
        ApplyDockRegion(new((int)Math.Round(x * dpi.DpiScaleX), (int)Math.Round(y * dpi.DpiScaleY),
            (int)Math.Ceiling(visibleWidth * dpi.DpiScaleX), (int)Math.Ceiling(visibleHeight * dpi.DpiScaleY),
            (int)Math.Round((parked ? 28 : 20) * dpi.DpiScaleX),
            null, dpi.DpiScaleX));
    }
    void ApplyDockRegion(DockRegionStamp region)
    {
        if (lastDockRegion == region) return;
        var applied = region.Empty ? Native.SetEmptyVisibleRegion(handle) : Native.SetVisibleRegion(handle,
            region.X, region.Y, region.Width, region.Height, region.Radius, region.Cutout, region.Scale);
        if (applied) lastDockRegion = region;
    }
    void Place()
    {
        if (animating || draggingPanel) return;
        var point = new System.Drawing.Point((int)store.State.ScreenX, (int)store.State.ScreenY);
        var screen = Forms.Screen.FromPoint(point); var area = screen.WorkingArea;
        var scale = Native.ScaleAtPoint(point.X, point.Y);
        // Native WM_SIZE can arrive before WPF applies the destination DPI.
        // Keep the logical design size independent of those transient values.
        var width = (int)Math.Round(PanelWidth * scale);
        var height = Math.Min((int)Math.Round(desiredHeight * scale), Math.Max(100, area.Height - (int)Math.Round(16 * scale)));
        var horizontal = store.State.Side is "top" or "bottom";
        var x = horizontal ? area.Left + (int)Math.Round(Math.Max(0, area.Width - width) * store.State.HorizontalRatio) : store.State.Side == "left" ? area.Left : area.Right - width;
        var y = area.Top + (int)Math.Round(Math.Max(0, area.Height - height) * Math.Clamp(store.State.VerticalRatio, 0, 1));
        if (horizontal) y = store.State.Side == "top" ? area.Top : area.Bottom - height;
        if (!collapsed && store.State.FloatingPanel)
        {
            x = Math.Clamp((int)Math.Round(store.State.PanelX), area.Left, Math.Max(area.Left, area.Right - width));
            y = Math.Clamp((int)Math.Round(store.State.PanelY), area.Top, Math.Max(area.Top, area.Bottom - height));
        }
        Native.PositionDockWindow(handle, x, y, width, height);
        UpdateDockRegion();
        if (collapsed && !animating && IsLoaded) ShowEdgeWindow();
    }
    void SavePanelPosition()
    {
        var bounds = Native.WindowBounds(handle);
        store.State.FloatingPanel = true; store.State.PanelX = bounds.Left; store.State.PanelY = bounds.Top;
        DockAt((bounds.Left + bounds.Right) / 2, (bounds.Top + bounds.Bottom) / 2);
    }
    void DockAt(int x, int y)
    {
        var screen = Forms.Screen.FromPoint(new System.Drawing.Point(x, y)); var area = screen.WorkingArea;
        store.State.ScreenX = screen.Bounds.Left + screen.Bounds.Width / 2;
        store.State.ScreenY = screen.Bounds.Top + screen.Bounds.Height / 2;
        store.State.Side = NearestSide(area, x, y);
        var scale = Native.ScaleAtPoint(x, y);
        var width = PanelWidth * scale; var height = Math.Min(desiredHeight * scale, area.Height - 16 * scale);
        store.State.HorizontalRatio = Math.Clamp((x - area.Left - width / 2) / Math.Max(1, area.Width - width), 0, 1);
        store.State.VerticalRatio = Math.Clamp((y - area.Top - height / 2) / Math.Max(1, area.Height - height), 0, 1);
        store.Save(); Place();
    }
    internal static string NearestSide(System.Drawing.Rectangle area, int x, int y)
    {
        var distances = new[] { Math.Abs(x - area.Left), Math.Abs(x - area.Right), Math.Abs(y - area.Top), Math.Abs(y - area.Bottom) };
        return new[] { "left", "right", "top", "bottom" }[Array.IndexOf(distances, distances.Min())];
    }
    void CreateTray()
    {
        var icon = AppIcon.TrayIcon();
        tray = new Forms.NotifyIcon { Icon = icon, Text = "PathDock", Visible = true };
        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            Font = System.Drawing.SystemFonts.MenuFont,
            ForeColor = System.Drawing.Color.FromArgb(32, 32, 32),
            Padding = new Forms.Padding(3, 5, 3, 5),
            ShowImageMargin = false,
            ShowCheckMargin = true
        };
        var startup = new Forms.ToolStripMenuItem("开机自启") { Enabled = !preview };
        menu.Opening += (_, _) => startup.Checked = StartupRegistration.IsEnabled;
        startup.Click += (_, _) => Dispatcher.Invoke(() =>
        {
            try { StartupRegistration.SetEnabled(!StartupRegistration.IsEnabled); }
            catch (Exception ex) { Ui.Message(this, ex.Message); }
            finally { startup.Checked = StartupRegistration.IsEnabled; }
        });
        menu.Items.Add(startup);
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { Expand(); ShowSettings(); }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close));
        foreach (Forms.ToolStripItem item in menu.Items)
            if (item is Forms.ToolStripMenuItem) item.Padding = new Forms.Padding(5, 7, 18, 7);
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => Dispatcher.Invoke(Expand);
    }
}
