using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace PathDock;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
#if PATHDOCK_DIAGNOSTICS
        if (Array.Exists(args, value => value is "--self-test" or "--motion-test" or "--render-preview" or "--render-settings"))
            return DiagnosticRunner.Run(args);
#endif
        var startup = Array.IndexOf(args, "--startup") >= 0;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PathDock");
        Directory.CreateDirectory(root);
        using var instance = new Mutex(true, "Local\\PathDock.App", out var created);
        if (!created)
        {
            if (!startup) MessageBox.Show("PathDock 已在运行。点击边缘按钮，或按 Ctrl + Alt + 空格展开。", "PathDock");
            return 0;
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteError(root, e.Exception);
            MessageBox.Show("操作未完成：" + e.Exception.Message, "PathDock");
            e.Handled = true;
        };
        try
        {
            var store = new DockStore(root);
            return app.Run(new MainWindow(store, false, startup && !store.State.KeepOpen));
        }
        catch (Exception ex)
        {
            WriteError(root, ex);
            MessageBox.Show(ex.Message, "PathDock 启动失败");
            return 1;
        }
        finally { instance.ReleaseMutex(); }
    }

    static void WriteError(string root, Exception error)
    {
        try { File.AppendAllText(Path.Combine(root, "errors.log"), $"{DateTimeOffset.Now:o} {error}\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
