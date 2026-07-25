using DrawingSystemIcons = System.Drawing.SystemIcons;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsScreen = System.Windows.Forms.Screen;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace OledGuardFresh;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinApi.EnableDpiAwareness();

        var application =
            new FreshApplication();

        application.Run();
    }
}

internal sealed class FreshApplication : System.Windows.Application
{
    private readonly List<FreshEngine> _engines =
        new();

    private FormsNotifyIcon? _trayIcon;
    private FormsContextMenuStrip? _trayMenu;
    private FormsToolStripMenuItem? _toggleItem;
    private bool _enabled = true;

    public FreshApplication()
    {
        ShutdownMode =
            System.Windows.ShutdownMode.OnExplicitShutdown;

        Startup +=
            Start;

        Exit +=
            Stop;
    }

    private void Start(
        object sender,
        System.Windows.StartupEventArgs eventArgs)
    {
        foreach (var screen in
                 FormsScreen.AllScreens)
        {
            var engine =
                new FreshEngine(
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
                    "OledGuardFresh — actif",
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
                    ? "OledGuardFresh — actif"
                    : "OledGuardFresh — désactivé";
        }
    }

    private void Stop(
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
