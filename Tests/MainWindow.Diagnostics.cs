using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
namespace PathDock;
public sealed partial class MainWindow
{
    internal async Task<object> CheckMotion()
    {
        var width = Width; var height = Height; var retained = surface.Child; var originalHandle = handle;
        var stable = true; var completions = 0; var beforeFrames = motionFrames;
        var collapsedRegionsCorrect = true; var nativeFramesDisabled = true; var collapsedSamples = 0;
        var independentEdgeVisible = true; var dragVisualCorrect = true;
        var monitorPlacementCorrect = true; var collapsedMeasurementCorrect = true;
        var sizeFailures = new System.Collections.Generic.List<object>();
        async Task Settle()
        {
            var timeout = Stopwatch.StartNew();
            width = Width; height = Height;
            var boundsBefore = Native.WindowBounds(handle);
            while (animating && timeout.ElapsedMilliseconds < 2000)
            {
                var boundsNow = Native.WindowBounds(handle);
                var frameStable = Math.Abs(Width - width) <= 1 && Math.Abs(Height - height) <= 1
                    && boundsNow.Right - boundsNow.Left == boundsBefore.Right - boundsBefore.Left
                    && boundsNow.Bottom - boundsNow.Top == boundsBefore.Bottom - boundsBefore.Top
                    && ReferenceEquals(surface.Child, retained) && handle == originalHandle;
                if (!frameStable && sizeFailures.Count < 8) sizeFailures.Add(new { Width, Height, InitialWidth = width, InitialHeight = height, BeforeWidth = boundsBefore.Right - boundsBefore.Left, BeforeHeight = boundsBefore.Bottom - boundsBefore.Top, NowWidth = boundsNow.Right - boundsNow.Left, NowHeight = boundsNow.Bottom - boundsNow.Top, Scale = Native.WindowScale(handle) });
                stable &= frameStable;
                await Task.Delay(10);
            }
            if (animating) throw new TimeoutException("Animation did not complete.");
            completions++;
        }
        var originalFloating = store.State.FloatingPanel; var originalPanelX = store.State.PanelX; var originalPanelY = store.State.PanelY;
        store.State.FloatingPanel = false;
        var originalSide = store.State.Side;
        var originalHorizontal = store.State.HorizontalRatio; var originalVertical = store.State.VerticalRatio;
        var originalX = store.State.ScreenX; var originalY = store.State.ScreenY;
        var screens = System.Windows.Forms.Screen.AllScreens;
        foreach (var screen in screens)
        foreach (var side in new[] { "right", "left", "top", "bottom" })
        {
            store.State.ScreenX = screen.Bounds.Left + screen.Bounds.Width / 2;
            store.State.ScreenY = screen.Bounds.Top + screen.Bounds.Height / 2;
            var area = screen.WorkingArea;
            var dropX = side == "left" ? area.Left + 1 : side == "right" ? area.Right - 1 : area.Left + area.Width / 2;
            var dropY = side == "top" ? area.Top + 1 : side == "bottom" ? area.Bottom - 1 : area.Top + area.Height / 2;
            DockAt(dropX, dropY);
            monitorPlacementCorrect &= store.State.Side == side;
            var restored = new DockStore(store.Root, false);
            monitorPlacementCorrect &= restored.State.Side == side && restored.State.ScreenX == store.State.ScreenX && restored.State.ScreenY == store.State.ScreenY;
            await Task.Delay(100);
            Render(); await Task.Delay(50);
            var positioned = Native.WindowBounds(handle);
            monitorPlacementCorrect &= Math.Abs(side switch { "left" => positioned.Left - screen.WorkingArea.Left, "top" => positioned.Top - screen.WorkingArea.Top, "bottom" => positioned.Bottom - screen.WorkingArea.Bottom, _ => positioned.Right - screen.WorkingArea.Right }) <= 2;
            for (var i = 0; i < 3; i++)
            {
                Collapse(); await Settle(); stable &= collapsed && reveal == 0;
                if (i == 0) {
                    var expectedHeight = desiredHeight;
                    desiredHeight = 300; // Initial startup value before any visible render.
                    Render();
                    collapsedMeasurementCorrect &= Math.Abs(desiredHeight - expectedHeight) <= 1;
                    var native = Native.WindowBounds(handle); var monitorScale = Native.ScaleAtPoint((int)store.State.ScreenX, (int)store.State.ScreenY);
                    var expectedPixels = Math.Min((int)Math.Round(desiredHeight * monitorScale), Math.Max(100, area.Height - (int)Math.Round(16 * monitorScale)));
                    collapsedMeasurementCorrect &= Math.Abs(native.Bottom - native.Top - expectedPixels) <= 1;
                }
                await Task.Delay(60);
                independentEdgeVisible &= !IsVisible && edgeWindow?.IsPresent == true && edgeWindow.IsAtAnchor;
                if (i == 0 && edgeWindow != null)
                {
                    dragVisualCorrect &= await edgeWindow.CheckDragVisual();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(edgeWindow.ActualWidth * 2), (int)Math.Ceiling(edgeWindow.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                    bitmap.Render(edgeWindow);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var directory = System.IO.Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results"); System.IO.Directory.CreateDirectory(directory);
                    using var file = System.IO.File.Create(System.IO.Path.Combine(directory, "edge-" + side + ".png")); encoder.Save(file);
                }
                var region = Native.DockRegionState(handle); var dpi = VisualTreeHelper.GetDpi(this);
                collapsedRegionsCorrect &= Math.Abs(region.Width - (side is "top" or "bottom" ? 56 : 28) * dpi.DpiScaleX) <= 2 && Math.Abs(region.Height - (side is "top" or "bottom" ? 28 : 56) * dpi.DpiScaleY) <= 2;
                nativeFramesDisabled &= region.NativeFrameDisabled; collapsedSamples++;
                Expand(); await Settle(); stable &= !collapsed && reveal == 1;
            }
        }
        Collapse(); await Task.Delay(60); Expand(); await Settle();
        stable &= !collapsed && reveal == 1 && ReferenceEquals(surface.Child, layout);
        var menu = NewMenu();
        AddAction(menu, "收起", () => { CollapseFromMenu(menu); return Task.CompletedTask; });
        OpenMenu(menu, surface);
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var menuWasTracked = menuDepth > 0;
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Settle();
        var menuCollapseWorks = menuWasTracked && collapsed && !menu.IsOpen && menuDepth == 0;
        var menuState = new { WasTracked = menuWasTracked, Collapsed = collapsed, Open = menu.IsOpen, Depth = menuDepth, Dialog = dialogOpen, Animating = animating, Reveal = reveal };
        Expand(); await Settle();
        var recentLimitCorrect = recentList.Children.Count == Math.Max(1, Math.Min(10, store.State.Folders.Count(f => !f.Pinned && f.LastUsed != null)));
        folderScroll.ScrollToVerticalOffset(100);
        Collapse(); await Settle(); Expand(); await Settle();
        var openingStartsAtTop = folderScroll.VerticalOffset == 0;
        var floatingCorrect = true; var movingCollapseCorrect = true;
        var title = (Grid)layout.Children[0];
        var blankHit = title.InputHitTest(new Point(title.ActualWidth - 2, title.ActualHeight / 2)) as DependencyObject;
        var titleDragCorrect = blankHit != null && !IsTitleButton(blankHit) && IsTitleButton(pin);
        foreach (var screen in screens) {
            var area = screen.WorkingArea; var scale = Native.ScaleAtPoint(area.Left + area.Width / 2, area.Top + area.Height / 2);
            var x = area.Left + (int)(100 * scale); var y = area.Top + (int)(80 * scale);
            Native.PositionDockWindow(handle, x, y, (int)(PanelWidth * scale), (int)(desiredHeight * scale));
            SavePanelPosition(); await Task.Delay(100); Render();
            var positioned = Native.WindowBounds(handle);
            floatingCorrect &= Math.Abs(positioned.Left - x) <= 1 && Math.Abs(positioned.Top - y) <= 1;
            Collapse(); await Task.Delay(60);
            var intermediate = Native.WindowBounds(handle);
            movingCollapseCorrect &= animating && (intermediate.Left != x || intermediate.Top != y)
                && (Math.Abs(intermediate.Left - dockPosition.X) > 1 || Math.Abs(intermediate.Top - dockPosition.Y) > 1);
            await Settle();
            var settledDock = Native.WindowBounds(handle);
            movingCollapseCorrect &= Math.Abs(settledDock.Left - dockPosition.X) <= 1 && Math.Abs(settledDock.Top - dockPosition.Y) <= 1;
            Expand();
            floatingCorrect &= edgeWindow?.IsVisible == false;
            await Settle(); positioned = Native.WindowBounds(handle);
            floatingCorrect &= Math.Abs(positioned.Left - x) <= 1 && Math.Abs(positioned.Top - y) <= 1;
            var restored = new DockStore(store.Root, false);
            floatingCorrect &= restored.State.FloatingPanel && restored.State.PanelX == x && restored.State.PanelY == y;
        }
        store.State.FloatingPanel = originalFloating; store.State.PanelX = originalPanelX; store.State.PanelY = originalPanelY;
        store.State.HorizontalRatio = originalHorizontal; store.State.VerticalRatio = originalVertical; store.State.Side = originalSide; store.State.ScreenX = originalX; store.State.ScreenY = originalY; Place();
        return new { Passed = recentLimitCorrect && openingStartsAtTop && titleDragCorrect && movingCollapseCorrect && floatingCorrect && collapsedMeasurementCorrect && dragVisualCorrect && stable && collapsedRegionsCorrect && nativeFramesDisabled && menuCollapseWorks && independentEdgeVisible && monitorPlacementCorrect, RecentLimitCorrect = recentLimitCorrect, OpeningStartsAtTop = openingStartsAtTop, TitleBlankAreaAndButtonExclusionCorrect = titleDragCorrect, FloatingCollapseMovesContinuously = movingCollapseCorrect, FloatingPanelAndExclusiveVisibilityCorrect = floatingCorrect, CollapsedStartupHeightCorrect = collapsedMeasurementCorrect, DragVisualAndFollowCorrect = dragVisualCorrect, SizeFailures = sizeFailures, MonitorsTested = screens.Length, MonitorPlacementCorrect = monitorPlacementCorrect, IndependentEdgeVisibleAndReceivesPointer = independentEdgeVisible, MenuCollapseWorks = menuCollapseWorks, MenuState = menuState, Transitions = completions, RenderFrames = motionFrames - beforeFrames, StableWindowSizeAndHandleAndContent = stable, CollapsedSamples = collapsedSamples, CollapsedRegionMatchesHandle = collapsedRegionsCorrect, NativeNonClientFrameDisabled = nativeFramesDisabled, SystemFontSize = Ui.ExplorerFontSize, ManualVisualSmoothnessVerified = false };
    }
}
