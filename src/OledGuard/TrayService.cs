using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace OledGuard;

internal sealed class TrayService : IDisposable
{
    private readonly ProtectionController _controller;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _enabledItem;
    private readonly Dictionary<int, Forms.ToolStripMenuItem> _delayItems = new();
    private bool _disposed;

    public TrayService(ProtectionController controller)
    {
        _controller = controller;

        var menu = new Forms.ContextMenuStrip();
        _enabledItem = new Forms.ToolStripMenuItem("Protection active")
        {
            CheckOnClick = true,
            Checked = controller.Enabled
        };
        _enabledItem.Click += (_, _) => _controller.SetEnabled(_enabledItem.Checked);
        menu.Items.Add(_enabledItem);

        var reveal = new Forms.ToolStripMenuItem("Révéler tout pendant 10 s");
        reveal.Click += (_, _) => _controller.RevealAll(TimeSpan.FromSeconds(10));
        menu.Items.Add(reveal);

        var delayMenu = new Forms.ToolStripMenuItem("Délai avant noir");
        foreach (var seconds in new[] { 5, 15, 30, 60 })
        {
            var item = new Forms.ToolStripMenuItem($"{seconds} secondes") { CheckOnClick = true };
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
        quit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(quit);

        _notifyIcon = new Forms.NotifyIcon
        {
            Icon = SystemIcons.Shield,
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
                "Windows n'a pas confirmé l'exclusion du masque des captures. Utilisez Windows 10 2004 ou une version plus récente et testez la révélation avant un usage prolongé.",
                Forms.ToolTipIcon.Warning);
        }

        _controller.StateChanged += OnControllerChanged;
        _controller.SettingsChanged += OnControllerChanged;
        RefreshMenu();
    }

    private void OnControllerChanged(object? sender, EventArgs e)
    {
        if (Application.Current.Dispatcher.CheckAccess())
        {
            RefreshMenu();
        }
        else
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(RefreshMenu));
        }
    }

    private void RefreshMenu()
    {
        _enabledItem.Checked = _controller.Enabled;
        foreach (var pair in _delayItems)
        {
            pair.Value.Checked = pair.Key == _controller.Settings.StaticDelaySeconds;
        }

        _notifyIcon.Text = _controller.Enabled
            ? $"OledGuard actif — noir après {_controller.Settings.StaticDelaySeconds} s"
            : "OledGuard désactivé";
    }

    private void ShowSettings()
    {
        Application.Current.Dispatcher.Invoke(() =>
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
    }
}
