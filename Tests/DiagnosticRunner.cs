using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading.Tasks;

namespace PathDock;

internal static class DiagnosticRunner
{

    [STAThread]
    public static int Run(string[] args)
    {
        if (args.Length > 0 && args[0] == "--self-test") return SelfTest.Run(args.Length > 1 ? args[1] : "self-test.json");
        var captureIndex = Array.IndexOf(args, "--render-preview");
        var settingsCapture = Array.IndexOf(args, "--render-settings") >= 0;
        if (settingsCapture) captureIndex = Array.IndexOf(args, "--render-settings");
        var capture = captureIndex >= 0 && args.Length > captureIndex + 1 ? Path.GetFullPath(args[captureIndex + 1]) : null;
        var motionIndex = Array.IndexOf(args, "--motion-test");
        var motion = motionIndex >= 0 && args.Length > motionIndex + 1 ? Path.GetFullPath(args[motionIndex + 1]) : null;
        var transient = capture != null || motion != null;
        var preview = Array.IndexOf(args, "--preview") >= 0 || transient;
        var startup = Array.IndexOf(args, "--startup") >= 0;
        var actualRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathDock");
        var root = transient ? Path.Combine(Path.GetTempPath(), "PathDock-Render-" + Guid.NewGuid().ToString("N")) : preview ? Path.Combine(Path.GetTempPath(), "PathDock-Preview") : actualRoot;
        Directory.CreateDirectory(root);
        if (transient && File.Exists(Path.Combine(actualRoot, "settings.json"))) File.Copy(Path.Combine(actualRoot, "settings.json"), Path.Combine(root, "settings.json"));
        if (transient && File.Exists(Path.Combine(actualRoot, "integration.json"))) File.Copy(Path.Combine(actualRoot, "integration.json"), Path.Combine(root, "integration.json"));
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) => {
            File.AppendAllText(Path.Combine(root, "errors.log"), $"{DateTimeOffset.Now:o} {e.Exception}\n");
            e.Handled = true; app.Shutdown(1);
        };
        try
        {
            var store = new DockStore(root); if (transient) store.State.KeepOpen = true;
            var window = new MainWindow(store, preview, startup && !store.State.KeepOpen);
            if (motion != null) window.Loaded += async (_, _) => {
                await Task.Delay(800);
                var result = await window.CheckMotion();
                File.WriteAllText(motion, System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                if (System.Text.Json.JsonSerializer.SerializeToElement(result).GetProperty("Passed").GetBoolean()) window.Close();
                else app.Shutdown(1);
            };
            if (capture != null) window.Loaded += async (_, _) => {
                await Task.Delay(800); window.UpdateLayout();
                Window rendered = window;
                if (settingsCapture)
                {
                    rendered = window.CreateSettingsDialog(); rendered.ShowActivated = false; rendered.IsHitTestVisible = false;
                    rendered.Show(); await Task.Delay(300); rendered.UpdateLayout();
                }
                var visual = (FrameworkElement)rendered.Content;
                foreach (var panel in FindPanels(visual)) panel.InvalidateMeasure();
                visual.InvalidateMeasure(); visual.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * 2), (int)Math.Ceiling(visual.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                Directory.CreateDirectory(Path.GetDirectoryName(capture)!); using (var file = File.Create(capture)) encoder.Save(file);
                window.Close();
            };
            return app.Run(window);
        }
        catch (Exception ex) { File.AppendAllText(Path.Combine(root, "errors.log"), ex.ToString()); return 1; }
        finally { if (transient && Path.GetFileName(root).StartsWith("PathDock-Render-")) Directory.Delete(root, true); }
    }
    static System.Collections.Generic.IEnumerable<System.Windows.Controls.Panel> FindPanels(DependencyObject root)
    {
        if (root is System.Windows.Controls.Panel panel) yield return panel;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var nested in FindPanels(VisualTreeHelper.GetChild(root, i))) yield return nested;
    }
}
