using FormsScreen = System.Windows.Forms.Screen;

namespace OledGuardSimple;

public partial class App : System.Windows.Application
{
    private readonly List<MonitorEngine> _engines = new();
    private HotkeyWindow? _hotkeyWindow;
    private bool _enabled = true;

    protected override void OnStartup(
        System.Windows.StartupEventArgs eventArgs)
    {
        base.OnStartup(eventArgs);

        NativeMethods.TryEnablePerMonitorDpiAwareness();

        foreach (var screen in FormsScreen.AllScreens)
        {
            try
            {
                var engine = new MonitorEngine(screen);
                _engines.Add(engine);
                engine.Start();
            }
            catch
            {
                // Un écran défaillant ne doit pas empêcher les autres de fonctionner.
            }
        }

        _hotkeyWindow = new HotkeyWindow(
            Toggle,
            () => Shutdown());
        _hotkeyWindow.Show();
    }

    private void Toggle()
    {
        _enabled = !_enabled;

        foreach (var engine in _engines)
        {
            engine.SetEnabled(_enabled);
        }
    }

    protected override void OnExit(
        System.Windows.ExitEventArgs eventArgs)
    {
        _hotkeyWindow?.Close();
        _hotkeyWindow = null;

        foreach (var engine in _engines)
        {
            engine.Dispose();
        }

        _engines.Clear();
        base.OnExit(eventArgs);
    }
}
