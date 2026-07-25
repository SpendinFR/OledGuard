using DrawingSystemIcons = System.Drawing.SystemIcons;
using FormsContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using FormsScreen = System.Windows.Forms.Screen;
using FormsToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;

namespace OledGuardSimple;

public partial class App : System.Windows.Application
{
    private readonly List<DetectionEngine> _engines = new();

    private FormsNotifyIcon? _trayIcon;
    private FormsContextMenuStrip? _trayMenu;
    private FormsToolStripMenuItem? _toggleItem;
    private bool _enabled = true;

    protected override void OnStartup(
        System.Windows.StartupEventArgs eventArgs)
    {
        base.OnStartup(
            eventArgs);

        NativeMethods.TryEnablePerMonitorDpiAwareness();

        foreach (var screen in FormsScreen.AllScreens)
        {
            var engine =
                new DetectionEngine(
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
                    "OledGuardSimple",
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
                    ? "OledGuardSimple — actif"
                    : "OledGuardSimple — désactivé";
        }
    }

    protected override void OnExit(
        System.Windows.ExitEventArgs eventArgs)
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible =
                false;

            _trayIcon.Dispose();
            _trayIcon = null;
        }

        _trayMenu?.Dispose();
        _trayMenu = null;
        _toggleItem = null;

        foreach (var engine in
                 _engines)
        {
            engine.Dispose();
        }

        _engines.Clear();

        base.OnExit(
            eventArgs);
    }
}
