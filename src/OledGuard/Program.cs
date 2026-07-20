using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace OledGuard;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(initiallyOwned: true, name: @"Local\OledGuard.Singleton", createdNew: out var isFirstInstance);
        if (!isFirstInstance)
        {
            return;
        }

        NativeMethods.TryEnablePerMonitorDpiAwareness();
        RenderOptions.ProcessRenderMode = RenderMode.Default;

        var app = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };

        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();
        var controller = new ProtectionController(settings, settingsStore);
        app.DispatcherUnhandledException += (_, eventArgs) =>
        {
            controller.SetEnabled(false);
            MessageBox.Show(
                $"OledGuard a rencontré une erreur et la protection a été désactivée.\n\n{eventArgs.Exception.Message}",
                "OledGuard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            eventArgs.Handled = true;
            app.Shutdown(-1);
        };
        app.Exit += (_, _) => controller.Dispose();
        controller.Start();

        using var hotkeys = new HotkeyHost(controller);
        using var tray = new TrayService(controller);
        app.Run();
    }
}
