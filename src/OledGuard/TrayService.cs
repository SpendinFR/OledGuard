using System.Drawing;
using Forms = System.Windows.Forms;

namespace OledGuard;

internal sealed class TrayService : IDisposable
{
    private readonly ProtectionController _controller;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Dictionary<int, Forms.ToolStripMenuItem> _delayItems = new();
    private readonly Icon _activeIcon;
    private readonly Icon _inactiveIcon;
    private bool _disposed;

    public TrayService(ProtectionController controller)
    {
        _controller = controller;
        _activeIcon = TrayIconFactory.Create(enabled: true);
        _inactiveIcon = TrayIconFactory.Create(enabled: false);

        var menu = new Forms.ContextMenuStrip();
        _enabledItem = new Forms.ToolStripMenuItem("Protection active — Ctrl+Alt+O")
        {
            CheckOnClick = true,
            Checked = controller.Enabled
        };
        _enabledItem.Click += (_, _) => _controller.SetEnabled(_enabledItem.Checked);
        menu.Items.Add(_enabledItem);

        var reveal = new Forms.ToolStripMenuItem("Révéler tout 10 s — Ctrl+Alt+R");
        reveal.Click += (_, _) => _controller.RevealAll(TimeSpan.FromSeconds(10));
        menu.Items.Add(reveal);

        var delayMenu = new Forms.ToolStripMenuItem("Délai avant assombrissement");
        foreach (var seconds in new[] { 5, 15, 30, 60, 120, 300, 600 })
        {
            var label = seconds >= 60 && seconds % 60 == 0 ? $"{seconds / 60} minute(s)" : $"{seconds} secondes";
            var item = new Forms.ToolStripMenuItem(label) { CheckOnClick = true };
            item.Click += (_, _) => _controller.SetDelaySeconds(seconds);
            _delayItems[seconds] = item;
            delayMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(delayMenu);

        var settings = new Forms.ToolStripMenuItem("Paramètres…");
        settings.Click += (_, _) => ShowSettings();
        menu.Items.Add(settings);

        menu.Items.Add(new Forms.ToolStripSeparator());
        var quit = new Forms.ToolStripMenuItem("Quitter");
        quit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        menu.Items.Add(quit);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = controller.Enabled ? _activeIcon : _inactiveIcon,
            Text = "OledGuard",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => _controller.Toggle();

        if (!_controller.CaptureExclusionAvailable)
        {
            _notifyIcon.ShowBalloonTip(
                8000,
                "OledGuard — vérification requise",
                "Windows n'a pas confirmé l'exclusion du masque des captures. Testez qu'une zone assombrie réapparaît bien quand son contenu change.",
                Forms.ToolTipIcon.Warning);
        }

        _controller.StateChanged += OnControllerChanged;
        _controller.SettingsChanged += OnControllerChanged;
        RefreshMenu();
    }

    private void OnControllerChanged(object? sender, EventArgs e)
    {
        if (System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            RefreshMenu();
        }
        else
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(RefreshMenu));
        }
    }

    private void RefreshMenu()
    {
        _enabledItem.Checked = _controller.Enabled;
        foreach (var pair in _delayItems)
        {
            pair.Value.Checked = pair.Key == _controller.Settings.StaticEligibilitySeconds;
        }

        _notifyIcon.Icon = _controller.Enabled ? _activeIcon : _inactiveIcon;
        _notifyIcon.Text = _controller.Enabled
            ? $"OledGuard actif — assombrissement après {_controller.Settings.StaticEligibilitySeconds} s de stabilitÃ©"
            : "OledGuard désactivé";
    }

    private void ShowSettings()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var window = new SettingsWindow(_controller.Settings)
            {
                Topmost = true
            };
            var result = window.ShowDialog();
            if (result == true)
            {
                _controller.ApplySettings(window.BuildSettings(_controller.Settings));
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.StateChanged -= OnControllerChanged;
        _controller.SettingsChanged -= OnControllerChanged;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _activeIcon.Dispose();
        _inactiveIcon.Dispose();
    }
}
