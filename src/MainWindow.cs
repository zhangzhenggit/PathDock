using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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
    EdgeWindow? edgeWindow;
    bool animating;
    double reveal = 1, transitionFrom = 1, transitionTo = 1;
    int motionFrames;
    readonly StackPanel fixedList = new(), recentList = new();
    readonly TextBlock status = Ui.Text("准备就绪", 11, Ui.Muted);


    readonly DispatcherTimer targetTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly DispatcherTimer recentTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };
    readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly Dictionary<string, DateTime> activitySeen = new();
    HashSet<string> explorerSeen = new(StringComparer.OrdinalIgnoreCase);
    Task<List<string>>? explorerRead;
    Forms.NotifyIcon? tray;
    TargetWindow? target;
    bool collapsed, dialogOpen, polling, exiting;
    int menuDepth;
    readonly HashSet<ContextMenu> openMenus = new();
    Button? pin;
    IntPtr handle;
    HwndSource? source;
    bool initialized;
    IntPtr previousForeground;
    const double PanelWidth = 300;
    double desiredHeight = 300;

    public MainWindow(DockStore store, bool preview, bool startCollapsed = false)
    {
        this.store = store; this.preview = preview;
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
            if (collapsed) ShowEdgeWindow();
            if (store.RecoveryNotice != null) Toast(store.RecoveryNotice);
            if (!preview) await CollectRecent();
        };
        targetTimer.Tick += (_, _) => TrackTarget();
        recentTimer.Tick += async (_, _) => { if (!preview) await CollectRecent(); };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); if (!store.State.KeepOpen && !IsMouseOver && !dialogOpen && menuDepth == 0) Collapse(); };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); status.Text = ""; status.Visibility = Visibility.Collapsed; };
        MouseEnter += (_, _) => hideTimer.Stop();
        MouseLeave += (_, _) => { if (!collapsed && !animating) hideTimer.Start(); };
        Deactivated += (_, _) => { Native.NoActivate(this, true); Keyboard.ClearFocus(); if (!collapsed && !store.State.KeepOpen) hideTimer.Start(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Collapse(); e.Handled = true; } };
        AllowDrop = true; DragOver += DragOverFolder; Drop += DropFolder;
        SizeChanged += (_, _) => { if (handle != IntPtr.Zero) { UpdateDockRegion(); if (collapsed && !animating) ShowEdgeWindow(); } };
        Closing += (_, _) => { exiting = true; edgeWindow?.Close(); CompositionTarget.Rendering -= AnimateDock; targetTimer.Stop(); recentTimer.Stop(); hideTimer.Stop(); toastTimer.Stop(); Native.UnregisterHotKey(handle, 1); source?.RemoveHook(WindowMessage); tray?.ContextMenuStrip?.Dispose(); tray?.Icon?.Dispose(); tray?.Dispose(); store.Save(); };
    }
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
        surface.Background = Ui.Canvas; surface.BorderBrush = Ui.Brush("#CED4DC"); surface.BorderThickness = new Thickness(1);
        surface.Child = layout; surface.RenderTransform = panelSlide; host.Background = Ui.Canvas; host.Children.Add(surface); Content = host;
        edgeButton = Ui.Button(new Image { Source = AppIcon.Image, Width = 16, Height = 16 });
        edgeButton.Width = 28; edgeButton.Height = 56; edgeButton.Padding = new Thickness(0); edgeButton.ToolTip = "PathDock";
        edgeButton.VerticalAlignment = VerticalAlignment.Center; edgeButton.HorizontalAlignment = HorizontalAlignment.Right;
        edgeButton.Background = Ui.Brush("#EDF3FA"); edgeButton.Opacity = 0; edgeButton.IsHitTestVisible = false;
        edgeButton.Click += (_, _) => Expand(); host.Children.Add(edgeButton);
        layout.Margin = new Thickness(7, 0, 7, 6);
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new Grid { Height = 36, Background = Ui.Brush("#F3F4F6"), Margin = new Thickness(-7, 0, -7, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(7, 0, 0, 0) };
        brand.Children.Add(new Image { Source = AppIcon.Image, Width = 16, Height = 16, Margin = new Thickness(5, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center });
        brand.Children.Add(Ui.Text("PathDock", Ui.ExplorerFontSize));
        brand.MouseLeftButtonDown += (_, e) => { if (e.ClickCount != 1) return; try { DragMove(); Snap(); } catch { } e.Handled = true; };
        header.Children.Add(brand);
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
        var add = Ui.IconButton("\uE710", "添加文件夹"); add.Click += (_, _) => AddFolder(); tools.Children.Add(add);
        pin = Ui.IconButton("\uE718", "固定面板"); UpdatePin();
        pin.Click += (_, _) => { store.State.KeepOpen = !store.State.KeepOpen; store.Save(); UpdatePin(); };
        tools.Children.Add(pin);
        var more = Ui.IconButton("\uE712", "更多"); more.Click += (_, _) => ShowPanelMenu(more); tools.Children.Add(more);
        Grid.SetColumn(tools, 1); header.Children.Add(tools); layout.Children.Add(header);
        var lists = new StackPanel();
        lists.Children.Add(SectionHeader("固定")); lists.Children.Add(fixedList);
        lists.Children.Add(SectionHeader("最近")); lists.Children.Add(recentList);
        var scroll = new ScrollViewer { Content = lists, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, PanningMode = PanningMode.VerticalOnly };
        Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
        status.Text = ""; status.Visibility = Visibility.Collapsed; status.TextWrapping = TextWrapping.Wrap; status.Margin = new Thickness(5, 5, 5, 2);
        Grid.SetRow(status, 2); layout.Children.Add(status);
        var panelMenu = NewMenu();
        AddAction(panelMenu, "添加文件夹…", () => { AddFolder(); return Task.CompletedTask; });
        AddAction(panelMenu, "导入…", ShowImport); layout.ContextMenu = panelMenu;
    }
    void ShowPanelMenu(FrameworkElement anchor)
    {
        var menu = NewMenu();
        AddAction(menu, "导入文件夹…", ShowImport);
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
        var marker = Ui.Icon(text == "固定" ? "\uE734" : "\uE81C", 12, Ui.Brush(text == "固定" ? "#5A83B3" : "#A8894F"));
        marker.Margin = new Thickness(0, 0, 6, 0); label.Children.Add(marker);
        label.Children.Add(Ui.Text(text, Ui.ExplorerFontSize, Ui.Muted)); grid.Children.Add(label);
        var line = new Border { Height = 1, Background = Ui.Line, Margin = new Thickness(9, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(line, 1); grid.Children.Add(line); return grid;
    }
    void Render()
    {
        fixedList.Children.Clear(); recentList.Children.Clear();
        var fixedItems = store.State.Folders.Where(f => f.Pinned).ToList();
        foreach (var item in fixedItems) fixedList.Children.Add(FolderRow(item));
        if (fixedItems.Count == 0) fixedList.Children.Add(Empty("无项目", ""));
        var recent = store.State.Folders.Where(f => !f.Pinned && f.LastUsed != null).OrderByDescending(f => f.LastUsed).Take(12).ToList();
        foreach (var item in recent) recentList.Children.Add(FolderRow(item));
        if (recent.Count == 0) recentList.Children.Add(Empty("无项目", ""));
        if (!collapsed && !animating)
        {
            var available = new Size(PanelWidth - 16, double.PositiveInfinity);
            fixedList.Measure(available); recentList.Measure(available);
            desiredHeight = Math.Clamp(108 + fixedList.DesiredSize.Height + recentList.DesiredSize.Height, 180, 500);
            Height = desiredHeight;
            if (IsLoaded) Place();
        }
        fixedList.InvalidateMeasure(); recentList.InvalidateMeasure();
        if (fixedList.Parent is FrameworkElement items) items.InvalidateMeasure();
        layout.InvalidateMeasure(); layout.InvalidateArrange();
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
        AddAction(menu, "复制路径", () => { Copy(item.Path); return Task.CompletedTask; }, glyph: "\uE8C8");
        var sessions = Services.Sessions(store.Root);
        if (sessions.Count > 0)
        {
            menu.Items.Add(new Separator());
            var submenu = new MenuItem { Header = "切换 PowerShell 目录", Icon = Ui.Icon("\uE8AB", 14) };
            foreach (var session in sessions)
            {
                var captured = session; var entry = new MenuItem { Header = new TextBlock { Text = $"{session.TitleMarker}  {session.Cwd}", MaxWidth = 300, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = $"选择标题中有 {session.TitleMarker} 的终端。" };
                entry.Click += async (_, _) => { try { await Services.SwitchSession(store.Root, captured, item.Path); store.Visit(item.Path, "PowerShell"); Render(); Toast("已切换所选 PowerShell 会话"); } catch (Exception ex) { Toast(ex.Message, true); } }; submenu.Items.Add(entry);
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
        menu.Closed += (_, _) => { openMenus.RemoveWhere(m => !m.IsOpen); menuDepth = openMenus.Count; if (!IsMouseOver) hideTimer.Start(); };
        return menu;
    }
    void OpenMenu(ContextMenu menu, FrameworkElement anchor) { menu.PlacementTarget = anchor; menu.IsOpen = true; }
    static void AddAction(ContextMenu menu, string name, Func<Task> action, bool enabled = true, string? glyph = null)
    {
        var entry = new MenuItem { Header = name, IsEnabled = enabled, Padding = new Thickness(10, 6, 10, 6) }; if (glyph != null) entry.Icon = Ui.Icon(glyph, 14); entry.Click += async (_, _) => await action(); menu.Items.Add(entry);
    }
    async Task Perform(FolderEntry item, string action, FrameworkElement? anchor, TargetWindow? destination)
    {
        try
        {
            InputObservation? automaticFocus = null;
            if (action == "auto")
            {
                var current = Native.ReadTarget();
                if (current != null) destination = current;
                if (destination?.IsTerminal == true)
                {
                    var session = Services.MatchSession(store.Root, destination);
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
                Clipboard.SetText(item.Path);
                if (now != destination.Handle) Native.SetForegroundWindow(destination.Handle);
                await Task.Delay(90);
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
            if (!exists) throw new DirectoryNotFoundException("目录不存在或暂时无法访问，可在菜单中修改路径。");
            ProcessStartInfo info;
            if (action == "terminal") { info = new("wt.exe") { UseShellExecute = true }; info.ArgumentList.Add("-w"); info.ArgumentList.Add("0"); info.ArgumentList.Add("new-tab"); info.ArgumentList.Add("-d"); info.ArgumentList.Add(item.Path); }
            else { info = new("explorer.exe") { UseShellExecute = true }; info.ArgumentList.Add(item.Path); }
            using var launched = Process.Start(info);
            if (launched == null) throw new IOException("系统未返回启动结果。");
            store.Visit(item.Path, action == "terminal" ? "终端打开请求" : "目录打开请求"); Render(); Toast(action == "terminal" ? "已请求在新终端中打开" : "已请求打开目录");
        }
        catch (TimeoutException ex) { Toast(ex.Message, true); }
        catch (Exception ex) { Toast(ex.Message, true); }
    }
    void Copy(string path) { try { Clipboard.SetText(path); Toast("路径已复制"); } catch (Exception ex) { Toast(ex.Message, true); } }
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
            if (store.State.RecordRecent && store.State.TrackExplorer)
            {
                if (explorerRead == null || explorerRead.IsCompleted) explorerRead = Services.ExplorerFolders();
                var folders = await explorerRead.WaitAsync(TimeSpan.FromSeconds(2));
                foreach (var path in folders) if (!explorerSeen.Contains(path)) changed |= store.Visit(path, "资源管理器 · 发现");
                explorerSeen = new(folders, StringComparer.OrdinalIgnoreCase);
            }
            var directory = Path.Combine(store.Root, "activity");
            if (Directory.Exists(directory)) foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                {
                    try
                    {
                        var stamp = File.GetLastWriteTimeUtc(file); if (activitySeen.TryGetValue(file, out var seen) && seen == stamp) continue; activitySeen[file] = stamp;
                        using var document = JsonDocument.Parse(File.ReadAllText(file)); var root = document.RootElement;
                        var utc = root.GetProperty("Utc").GetDateTimeOffset();
                        var age = DateTimeOffset.UtcNow - utc;
                        if (age >= TimeSpan.Zero && age < TimeSpan.FromDays(7)) changed |= store.Visit(root.GetProperty("Path").GetString()!, "PowerShell", utc);
                    }
                    catch { }
                }
            if (changed && !dialogOpen && menuDepth == 0) Render();
        }
        catch { /* Optional source unavailable: keep existing data and UI responsive. */ }
        finally { polling = false; }
    }
    void EnableEditing() { Native.NoActivate(this, false); Activate(); Native.SetForegroundWindow(handle); }
    void WithDialog(Action show)
    {
        dialogOpen = true; hideTimer.Stop(); EnableEditing();
        try { show(); } finally { dialogOpen = false; Native.NoActivate(this, true); Render(); }
    }
    void AddFolder()
    {
        WithDialog(() =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = "选择要固定的目录", Multiselect = true };
            if (picker.ShowDialog(this) != true) return;
            foreach (var path in picker.FolderNames) store.AddPinned(path); Toast("已添加到固定收藏");
        });
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
    async Task ShowImport()
    {
        dialogOpen = true; EnableEditing(); hideTimer.Stop();
        var dialog = new DockDialog(this, "导入文件夹", "", 480, 520);
        var loading = Ui.Text("正在读取本机目录…", 12, Ui.Muted); dialog.Body.Children.Add(loading);
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var candidates = await Services.ImportCandidates(); if (!dialog.IsVisible) return; dialog.Body.Children.Clear();
                var choices = new List<(CheckBox Box, ImportCandidate Candidate)>();
                foreach (var item in candidates)
                {
                    var text = new StackPanel(); text.Children.Add(Ui.Text(DockStore.DefaultName(item.Path), 13, bold: true)); var detail = Ui.Text(item.Path + "\n" + item.Source, 10, Ui.Muted); detail.TextWrapping = TextWrapping.Wrap; detail.Margin = new Thickness(0, 4, 0, 0); text.Children.Add(detail);
                    var box = new CheckBox { Content = text, Margin = new Thickness(0, 6, 0, 6), VerticalContentAlignment = VerticalAlignment.Center, IsChecked = false, IsEnabled = !store.State.Folders.Any(f => f.Pinned && string.Equals(f.Path, item.Path, StringComparison.OrdinalIgnoreCase)) };
                    if (!box.IsEnabled) box.ToolTip = "已在固定收藏中"; choices.Add((box, item)); dialog.Body.Children.Add(box);
                }
                if (choices.Count == 0) dialog.Body.Children.Add(Ui.Text("没有可导入的目录，可直接添加文件夹。", 12, Ui.Muted));
                var add = Ui.Button("添加所选目录", true); add.Margin = new Thickness(0, 18, 0, 8); add.Click += (_, _) => { var selected = choices.Where(c => c.Box.IsChecked == true).ToList(); foreach (var c in selected) store.AddPinned(c.Candidate.Path); dialog.Close(); Toast($"已添加 {selected.Count} 个目录"); }; dialog.Body.Children.Add(add);
            }
            catch (Exception ex) { loading.Text = "读取失败：" + ex.Message; loading.TextWrapping = TextWrapping.Wrap; }
        };
        try { dialog.ShowDialog(); } finally { dialogOpen = false; Native.NoActivate(this, true); Render(); }
        await Task.CompletedTask;
    }
    void ShowSettings() => WithDialog(() => CreateSettingsDialog().ShowDialog());
    internal DockDialog CreateSettingsDialog()
    {
        var dialog = new DockDialog(this, "设置", "", 360, 370);
        dialog.SizeToContent = SizeToContent.Height;
        dialog.MaxHeight = Math.Max(300, Forms.Screen.FromHandle(handle).WorkingArea.Height / VisualTreeHelper.GetDpi(this).DpiScaleY - 40);
        void Toggle(string label, bool initial, Action<bool> apply)
        {
            var box = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 5, 0, 9), FontSize = Ui.ExplorerFontSize };
            box.Click += (_, _) => { apply(box.IsChecked == true); store.Save(); }; dialog.Body.Children.Add(box);
        }
        Toggle("固定面板", store.State.KeepOpen, v => { store.State.KeepOpen = v; UpdatePin(); });
        var startup = new CheckBox { Content = "开机时启动 PathDock", IsChecked = StartupRegistration.IsEnabled, IsEnabled = !preview, Margin = new Thickness(0, 5, 0, 9), FontSize = Ui.ExplorerFontSize, ToolTip = "登录当前 Windows 账户时启动。" };
        startup.Click += (_, _) =>
        {
            try { StartupRegistration.SetEnabled(startup.IsChecked == true); }
            catch (Exception ex) { startup.IsChecked = StartupRegistration.IsEnabled; Ui.Message(dialog, ex.Message); }
        };
        dialog.Body.Children.Add(startup);
        Toggle("记录最近目录", store.State.RecordRecent, v => store.State.RecordRecent = v);
        Toggle("从资源管理器发现目录", store.State.TrackExplorer, v => store.State.TrackExplorer = v);
        var side = new ComboBox { ItemsSource = new[] { "右侧停靠", "左侧停靠" }, SelectedIndex = store.State.Side == "left" ? 1 : 0, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(5) };
        side.SelectionChanged += (_, _) => { store.State.Side = side.SelectedIndex == 1 ? "left" : "right"; store.Save(); Place(); }; dialog.Body.Children.Add(side);
        var installed = File.Exists(Path.Combine(store.Root, "integration.json"));
        var integrationBody = new StackPanel { Margin = new Thickness(10, 6, 10, 10) };
        var integration = new CheckBox { Content = "允许在当前终端切换目录", IsChecked = installed, FontSize = Ui.ExplorerFontSize, Margin = new Thickness(0, 0, 0, 8), IsEnabled = !preview };
        integration.ToolTip = "备份并追加用户 PowerShell 配置。仅在空提示符下切换；有未提交输入或正在运行命令时不切换。";
        var state = Ui.Text(installed ? "在空闲的 PowerShell 中，点击目录即可切换。" : "启用后，对新打开的 PowerShell 标签生效。", Ui.ExplorerFontSize, Ui.Muted);
        state.TextWrapping = TextWrapping.Wrap;
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
        var titleNote = Ui.Text("空闲时，终端标题会自动显示 [PD:…] 会话标记。", Ui.ExplorerFontSize, Ui.Muted);
        titleNote.TextWrapping = TextWrapping.Wrap; titleNote.Margin = new Thickness(0, 7, 0, 0); integrationBody.Children.Add(titleNote);
        dialog.Body.Children.Add(new GroupBox { Header = "PowerShell", FontSize = Ui.ExplorerFontSize, BorderBrush = Ui.Line, BorderThickness = new Thickness(1), Content = integrationBody, Margin = new Thickness(0, 4, 0, 0) });
        return dialog;
    }
    void UpdatePin() { if (pin != null) { pin.Background = store.State.KeepOpen ? Ui.Brush("#DFEAF8") : Brushes.Transparent; pin.ToolTip = store.State.KeepOpen ? "取消固定面板" : "固定面板"; } }
    void Toast(string message, bool error = false) { if (!error) return; status.Text = message; status.Visibility = Visibility.Visible; status.Foreground = error ? Ui.Brush("#B56847") : Ui.Accent; toastTimer.Stop(); toastTimer.Start(); }
    void DragOverFolder(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    async void DropFolder(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        try { var valid = await Task.Run(() => paths.Where(Directory.Exists).ToArray()).WaitAsync(TimeSpan.FromSeconds(3)); foreach (var path in valid) store.AddPinned(path); if (collapsed) Expand(); Render(); Toast($"已添加 {valid.Length} 个目录"); }
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
        StartTransition(true); Show(); Native.KeepAbove(handle);
    }
    void StartTransition(bool expand)
    {
        hideTimer.Stop();
        var destination = expand ? 1d : 0d;
        if (animating && transitionTo == destination) return;
        collapsed = !expand;
        transitionFrom = reveal; transitionTo = destination;
        surface.IsHitTestVisible = false;
        if (edgeButton != null) edgeButton.IsHitTestVisible = false;
        animating = true; slideClock.Restart();
        CompositionTarget.Rendering -= AnimateDock;
        CompositionTarget.Rendering += AnimateDock;
    }
    void AnimateDock(object? sender, EventArgs e)
    {
        var progress = Math.Clamp(slideClock.Elapsed.TotalMilliseconds / 180, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        reveal = transitionFrom + (transitionTo - transitionFrom) * eased;
        motionFrames++;
        UpdateDockRegion();
        if (progress < 1) return;
        reveal = transitionTo; animating = false;
        CompositionTarget.Rendering -= AnimateDock; slideClock.Stop();
        surface.IsHitTestVisible = !collapsed;
        if (edgeButton != null) edgeButton.IsHitTestVisible = collapsed;
        UpdateDockRegion();
        if (collapsed) ShowEdgeWindow();
        else edgeWindow?.Hide();
        if (!collapsed && !store.State.KeepOpen && !IsMouseOver) hideTimer.Start();
    }
    void ShowEdgeWindow()
    {
        if (exiting || animating || !collapsed) return;
        edgeWindow ??= new EdgeWindow(Expand);
        edgeWindow.ShowAt(handle, store.State.Side == "left");
        Hide();
    }
    void UpdateDockRegion()
    {
        if (handle == IntPtr.Zero || ActualWidth <= 0 || ActualHeight <= 0) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var visibleWidth = 28 + (ActualWidth - 28) * reveal;
        var visibleHeight = 56 + (ActualHeight - 56) * reveal;
        var leftSide = store.State.Side == "left";
        var x = leftSide ? 0 : ActualWidth - visibleWidth;
        var y = (ActualHeight - visibleHeight) / 2;
        panelSlide.X = Math.Round((leftSide ? -1 : 1) * (ActualWidth - visibleWidth) * dpi.DpiScaleX) / dpi.DpiScaleX;
        if (edgeButton != null)
        {
            edgeButton.HorizontalAlignment = leftSide ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            edgeButton.Opacity = Math.Clamp((.22 - reveal) / .22, 0, 1);
        }
        Native.SetVisibleRegion(handle, (int)Math.Round(x * dpi.DpiScaleX), (int)Math.Round(y * dpi.DpiScaleY),
            (int)Math.Ceiling(visibleWidth * dpi.DpiScaleX), (int)Math.Ceiling(visibleHeight * dpi.DpiScaleY),
            (int)Math.Round(8 * dpi.DpiScaleX));
    }
    void Place()
    {
        if (animating) return;
        var point = new System.Drawing.Point((int)store.State.ScreenX, (int)store.State.ScreenY);
        var screen = Forms.Screen.FromPoint(point); var area = screen.WorkingArea;
        var scale = Native.ScaleAtPoint(point.X, point.Y);
        // Native WM_SIZE can arrive before WPF applies the destination DPI.
        // Keep the logical design size independent of those transient values.
        var width = (int)Math.Round(PanelWidth * scale);
        var height = Math.Min((int)Math.Round(desiredHeight * scale), Math.Max(100, area.Height - (int)Math.Round(16 * scale)));
        var x = store.State.Side == "left" ? area.Left : area.Right - width;
        var y = area.Top + (int)Math.Round(Math.Max(0, area.Height - height) * Math.Clamp(store.State.VerticalRatio, 0, 1));
        Native.PositionDockWindow(handle, x, y, width, height);
        UpdateDockRegion();
        if (collapsed && !animating && IsLoaded) ShowEdgeWindow();
    }
    void Snap()
    {
        var screen = Forms.Screen.FromHandle(handle); var center = screen.Bounds; store.State.ScreenX = center.Left + center.Width / 2; store.State.ScreenY = center.Top + center.Height / 2;
        var bounds = Native.WindowBounds(handle);
        store.State.Side = (bounds.Left + bounds.Right) / 2 < center.Left + center.Width / 2 ? "left" : "right";
        var available = screen.WorkingArea.Height - (bounds.Bottom - bounds.Top);
        store.State.VerticalRatio = available <= 0 ? .5 : Math.Clamp((bounds.Top - screen.WorkingArea.Top) / available, 0, 1); store.Save(); Place();
    }
    void CreateTray()
    {
        var icon = AppIcon.TrayIcon();
        tray = new Forms.NotifyIcon { Icon = icon, Text = "PathDock", Visible = true };
        var menu = new Forms.ContextMenuStrip(); menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(() => { Expand(); ShowSettings(); })); menu.Items.Add(new Forms.ToolStripSeparator()); menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Close)); tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(Expand);
    }
}
