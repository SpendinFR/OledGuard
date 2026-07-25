using DrawingSystemIcons = System.Drawing.SystemIcons;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsScreen = System.Windows.Forms.Screen;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace OledGuardNeuf;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();

        var application =
            new GuardApplication();

        application.Run();
    }
}

internal sealed class GuardApplication : System.Windows.Application
{
    private readonly List<ScreenEngine> _engines = new();

    private FormsNotifyIcon? _trayIcon;
    private FormsContextMenuStrip? _trayMenu;
    private FormsToolStripMenuItem? _toggleItem;
    private bool _enabled = true;

    public GuardApplication()
    {
        ShutdownMode =
            System.Windows.ShutdownMode.OnExplicitShutdown;

        Startup +=
            OnStartup;

        Exit +=
            OnExit;
    }

    private void OnStartup(
        object sender,
        System.Windows.StartupEventArgs eventArgs)
    {
        foreach (var screen in FormsScreen.AllScreens)
        {
            var engine =
                new ScreenEngine(
                    screen);

            _engines.Add(
                engine);

            engine.Start();
        }

        CreateTrayIcon();
    }

    private void CreateTrayIcon()
    {
        _trayMenu =
            new FormsContextMenuStrip();

        _toggleItem =
            new FormsToolStripMenuItem(
                "Désactiver");

        _toggleItem.Click +=
            (_, _) =>
                Toggle();

        var quitItem =
            new FormsToolStripMenuItem(
                "Quitter");

        quitItem.Click +=
            (_, _) =>
                Shutdown();

        _trayMenu.Items.Add(
            _toggleItem);

        _trayMenu.Items.Add(
            quitItem);

        _trayIcon =
            new FormsNotifyIcon
            {
                Icon =
                    DrawingSystemIcons.Application,
                Text =
                    "OledGuardNeuf — actif",
                ContextMenuStrip =
                    _trayMenu,
                Visible =
                    true
            };

        _trayIcon.DoubleClick +=
            (_, _) =>
                Toggle();
    }

    private void Toggle()
    {
        _enabled =
            !_enabled;

        foreach (var engine in
                 _engines)
        {
            engine.SetEnabled(
                _enabled);
        }

        if (_toggleItem is not null)
        {
            _toggleItem.Text =
                _enabled
                    ? "Désactiver"
                    : "Activer";
        }

        if (_trayIcon is not null)
        {
            _trayIcon.Text =
                _enabled
                    ? "OledGuardNeuf — actif"
                    : "OledGuardNeuf — désactivé";
        }
    }

    private void OnExit(
        object sender,
        System.Windows.ExitEventArgs eventArgs)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible =
                false;

            _trayIcon.Dispose();
            _trayIcon =
                null;
        }

        _trayMenu?.Dispose();
        _trayMenu =
            null;

        _toggleItem =
            null;

        foreach (var engine in
                 _engines)
        {
            engine.Dispose();
        }

        _engines.Clear();
    }
}
