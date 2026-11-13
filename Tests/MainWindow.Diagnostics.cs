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
        var coveredPreparationCorrect = true; var warmInvokeRejected = false; var edgeStyleCorrect = true; var firstExpandedFrameCorrect = true;
        var monitorPlacementCorrect = true; var collapsedMeasurementCorrect = true;
        var pointerOccluders = new System.Collections.Generic.HashSet<string>();
        var unchangedGeometrySkipsNativeUpdates = true;
        var sizeFailures = new System.Collections.Generic.List<object>();
        async Task Settle()
        {
            var timeout = Stopwatch.StartNew();
            width = Width; height = Height;
            var boundsBefore = Native.WindowBounds(handle);
            while (animating && timeout.ElapsedMilliseconds < 2000)
            {
                if (collapsed)
                {
                    coveredPreparationCorrect &= islandActive
                        ? islandWindow?.HasSnapshot == true && (islandCovered ? !IsVisible && edgeWindow?.IsVisible == false : IsVisible || openingHandleCover)
                        : edgeWindow?.IsPreparedForHandoff == true;
                    if (!warmInvokeRejected) {
                        try { edgeWindow?.InvokeAccessibleButton(); }
                        catch (System.Windows.Automation.ElementNotEnabledException) { warmInvokeRejected = true; }
                    }
                }
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
            UpdateDockRegion();
            var regionUpdates = Native.VisibleRegionUpdates;
            for (var i = 0; i < 8; i++) UpdateDockRegion();
            unchangedGeometrySkipsNativeUpdates &= Native.VisibleRegionUpdates == regionUpdates;
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
                if (edgeWindow is { IsPresent: false }) pointerOccluders.Add(Native.PointerClassAtCenter(new System.Windows.Interop.WindowInteropHelper(edgeWindow).Handle));
                edgeStyleCorrect &= edgeWindow?.PresentationMatches(edgePresentation) == true
                    && Native.TailContourMatchesEdge(handle, Native.WindowScale(handle));
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
                double? firstReveal = null;
                void CaptureFirstFrame(object? sender, EventArgs args) { firstReveal = reveal; CompositionTarget.Rendering -= CaptureFirstFrame; }
                Expand(); CompositionTarget.Rendering += CaptureFirstFrame;
                await Settle(); CompositionTarget.Rendering -= CaptureFirstFrame;
                firstExpandedFrameCorrect &= !SystemParameters.ClientAreaAnimation || firstReveal == 0;
                stable &= !collapsed && reveal == 1;
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
        var rowReuseCorrect = true;
        var oldFixed = fixedList.Children.Cast<UIElement>().ToArray();
        var oldRecent = recentList.Children.Cast<UIElement>().ToArray();
        Render();
        rowReuseCorrect &= oldFixed.SequenceEqual(fixedList.Children.Cast<UIElement>()) && oldRecent.SequenceEqual(recentList.Children.Cast<UIElement>());
        var entry = store.State.Folders.FirstOrDefault(f => f.Pinned);
        if (entry != null) {
            var name = entry.Name; var rowIndex = store.State.Folders.Where(f => f.Pinned).ToList().IndexOf(entry);
            entry.Name += " test"; Render();
            rowReuseCorrect &= !ReferenceEquals(oldFixed[rowIndex], fixedList.Children[rowIndex]);
            for (var i = 0; i < oldFixed.Length; i++) if (i != rowIndex) rowReuseCorrect &= ReferenceEquals(oldFixed[i], fixedList.Children[i]);
            entry.Name = name; Render();
        }
        var reordered = store.State.Folders.Where(f => !f.Pinned && f.LastUsed != null).OrderByDescending(f => f.LastUsed).Take(10).LastOrDefault();
        if (reordered != null) {
            var prior = reordered.LastUsed;
            var sameRow = recentList.Children.OfType<Button>().Single(row => row.Tag is RowStamp stamp && ReferenceEquals(stamp.Entry, reordered));
            reordered.LastUsed = DateTimeOffset.UtcNow.AddDays(1); Render();
            rowReuseCorrect &= ReferenceEquals(recentList.Children[0], sameRow);
            reordered.LastUsed = prior; Render();
        }
        oldRecent = recentList.Children.Cast<UIElement>().ToArray();
        draggingPanel = true; Render();
        rowReuseCorrect &= renderPending && oldRecent.SequenceEqual(recentList.Children.Cast<UIElement>());
        draggingPanel = false; Render(); rowReuseCorrect &= !renderPending;
        Collapse(); await Settle();
        var accessibleEdgeCorrect = edgeWindow?.HasAccessibleButton == true;
        edgeWindow?.InvokeAccessibleButton();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Settle();
        accessibleEdgeCorrect &= !collapsed && edgeWindow?.IsVisible == false;
        var hiddenRejected = false;
        try { edgeWindow?.InvokeAccessibleButton(); } catch (System.Windows.Automation.ElementNotEnabledException) { hiddenRejected = true; }
        accessibleEdgeCorrect &= hiddenRejected;
        StartTransition(false, false);
        var disabledAnimationCorrect = collapsed && !animating && reveal == 0 && !IsVisible && edgeWindow?.IsPresent == true;
        StartTransition(true, false); Show();
        disabledAnimationCorrect &= !collapsed && !animating && reveal == 1 && surface.IsHitTestVisible && edgeWindow?.IsVisible == false;
        var handoffWaitsForContent = true;
        if (SystemParameters.ClientAreaAnimation && edgeWindow != null) {
            edgeWindow.SetRenderedStateForTest(false);
            Collapse(); await Task.Delay(650);
            handoffWaitsForContent &= animating && collapsed && (islandActive
                ? islandWindow?.IsVisible == true && !IsVisible && reveal == 0
                : IsVisible && edgeWindow.IsPreparedForHandoff);
            edgeWindow.SetRenderedStateForTest(true); await Settle();
            handoffWaitsForContent &= !animating && !IsVisible && edgeWindow.IsPresent;
            Expand(); await Settle();
        }
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
            Collapse();
            var prepareTimeout = Stopwatch.StartNew();
            while (islandActive && !islandCovered && prepareTimeout.ElapsedMilliseconds < 1000) await Task.Delay(10);
            await Task.Delay(60);
            var intermediate = islandActive ? islandWindow!.CardScreenBounds : new Rect(Native.WindowBounds(handle).Left, Native.WindowBounds(handle).Top, Width, Height);
            movingCollapseCorrect &= animating && (intermediate.Left != x || intermediate.Top != y)
                && (Math.Abs(intermediate.Left - dockPosition.X) > 1 || Math.Abs(intermediate.Top - dockPosition.Y) > 1);
            await Settle();
            var settledDock = Native.WindowBounds(handle);
            movingCollapseCorrect &= Math.Abs(settledDock.Left - dockPosition.X) <= 1 && Math.Abs(settledDock.Top - dockPosition.Y) <= 1;
            Expand();
            floatingCorrect &= edgeWindow?.IsVisible == false || openingHandleCover && edgeWindow?.IsOpeningCoverSafe == true;
            await Settle(); positioned = Native.WindowBounds(handle);
            floatingCorrect &= Math.Abs(positioned.Left - x) <= 1 && Math.Abs(positioned.Top - y) <= 1;
            var restored = new DockStore(store.Root, false);
            floatingCorrect &= restored.State.FloatingPanel && restored.State.PanelX == x && restored.State.PanelY == y;
        }
        store.State.FloatingPanel = originalFloating; store.State.PanelX = originalPanelX; store.State.PanelY = originalPanelY;
        store.State.HorizontalRatio = originalHorizontal; store.State.VerticalRatio = originalVertical; store.State.Side = originalSide; store.State.ScreenX = originalX; store.State.ScreenY = originalY; Place();
        var idleFrames = motionFrames;
        AnimateDock(null, EventArgs.Empty);
        var noIdleAnimationUpdates = idleFrames == motionFrames;
        var calmDismissAndAccentCorrect = await CheckCalmDismissAndAccent();
        return new { Passed = calmDismissAndAccentCorrect && unchangedGeometrySkipsNativeUpdates && noIdleAnimationUpdates && handoffWaitsForContent && coveredPreparationCorrect && warmInvokeRejected && edgeStyleCorrect && firstExpandedFrameCorrect && rowReuseCorrect && accessibleEdgeCorrect && disabledAnimationCorrect && recentLimitCorrect && openingStartsAtTop && titleDragCorrect && movingCollapseCorrect && floatingCorrect && collapsedMeasurementCorrect && dragVisualCorrect && stable && collapsedRegionsCorrect && nativeFramesDisabled && menuCollapseWorks && independentEdgeVisible && monitorPlacementCorrect, FirstExpandedFrameCorrect = firstExpandedFrameCorrect, HandoffWaitsForContent = handoffWaitsForContent, PreparedEdgeHandoffCorrect = coveredPreparationCorrect, WarmInvokeRejected = warmInvokeRejected, EdgePresentationMatches = edgeStyleCorrect, RowReuseAndDeferredUpdateCorrect = rowReuseCorrect, AccessibleEdgeInvokeCorrect = accessibleEdgeCorrect, DisabledAnimationCorrect = disabledAnimationCorrect, RecentLimitCorrect = recentLimitCorrect, OpeningStartsAtTop = openingStartsAtTop, TitleBlankAreaAndButtonExclusionCorrect = titleDragCorrect, FloatingCollapseMovesContinuously = movingCollapseCorrect, FloatingPanelAndExclusiveVisibilityCorrect = floatingCorrect, CollapsedStartupHeightCorrect = collapsedMeasurementCorrect, DragVisualAndFollowCorrect = dragVisualCorrect, SizeFailures = sizeFailures, MonitorsTested = screens.Length, MonitorPlacementCorrect = monitorPlacementCorrect, IndependentEdgeVisibleAndReceivesPointer = independentEdgeVisible, MenuCollapseWorks = menuCollapseWorks, MenuState = menuState, Transitions = completions, RenderFrames = motionFrames - beforeFrames, StableWindowSizeAndHandleAndContent = stable, CollapsedSamples = collapsedSamples, CollapsedRegionMatchesHandle = collapsedRegionsCorrect, NativeNonClientFrameDisabled = nativeFramesDisabled, SystemFontSize = Ui.ExplorerFontSize, CalmDismissAndAccentCorrect = calmDismissAndAccentCorrect, UnchangedGeometrySkipsNativeUpdates = unchangedGeometrySkipsNativeUpdates, NoIdleAnimationUpdates = noIdleAnimationUpdates, PointerOccluders = pointerOccluders.ToArray(), AnimationPerformance = AnimationMetrics(), ManualVisualSmoothnessVerified = false };
    }
}
