using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OledGuard;

internal sealed class SettingsWindow : Window
{
    private readonly ComboBox _delay;
    private readonly ComboBox _cellSize;
    private readonly Slider _radius;
    private readonly Slider _darkenFade;
    private readonly Slider _revealFade;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard — Paramètres";
        Width = 470;
        Height = 500;
        MinWidth = 430;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Protection OLED dynamique",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 16)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _delay = AddCombo(form, "Délai avant noir", new[] { 5, 15, 30, 60, 120 }, settings.StaticDelaySeconds, "secondes");
        _cellSize = AddCombo(form, "Taille des zones", new[] { 32, 48, 64, 96, 128 }, settings.CellSizePixels, "pixels");
        _radius = AddSlider(form, "Rayon de révélation de la souris", 80, 360, settings.MouseRevealRadiusPixels, "px");
        _revealFade = AddSlider(form, "Fondu vers visible", 40, 500, settings.RevealFadeMilliseconds, "ms");
        _darkenFade = AddSlider(form, "Fondu vers le noir", 200, 2500, settings.DarkenFadeMilliseconds, "ms");

        _startWithWindows = new CheckBox
        {
            Content = "Démarrer avec Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(0, 18, 0, 0),
            FontSize = 14
        };
        form.Children.Add(_startWithWindows);

        var note = new TextBlock
        {
            Text = "30 s est le réglage conseillé. Une zone qui change redevient visible immédiatement. Ctrl+Alt+O active/désactive ; Ctrl+Alt+R révèle tout pendant 10 s.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.DimGray,
            Margin = new Thickness(0, 18, 0, 0)
        };
        form.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var cancel = new Button { Content = "Annuler", MinWidth = 90, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(12, 7, 12, 7) };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(cancel);

        var save = new Button { Content = "Enregistrer", MinWidth = 110, IsDefault = true, Padding = new Thickness(12, 7, 12, 7) };
        save.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(save);

        Content = root;
    }

    public AppSettings BuildSettings(AppSettings original)
    {
        var updated = original.Clone();
        updated.StaticDelaySeconds = (int)_delay.SelectedItem;
        updated.CellSizePixels = (int)_cellSize.SelectedItem;
        updated.MouseRevealRadiusPixels = (int)Math.Round(_radius.Value);
        updated.RevealFadeMilliseconds = (int)Math.Round(_revealFade.Value);
        updated.DarkenFadeMilliseconds = (int)Math.Round(_darkenFade.Value);
        updated.StartWithWindows = _startWithWindows.IsChecked == true;
        updated.Normalize();
        return updated;
    }

    private static ComboBox AddCombo(StackPanel parent, string label, int[] values, int selected, string suffix)
    {
        parent.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 5) });
        var combo = new ComboBox { Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var value in values)
        {
            combo.Items.Add(value);
        }
        combo.SelectedItem = values.Contains(selected) ? selected : values.OrderBy(value => Math.Abs(value - selected)).First();
        parent.Children.Add(combo);
        parent.Children.Add(new TextBlock { Text = suffix, Foreground = Brushes.Gray, Margin = new Thickness(190, -25, 0, 8) });
        return combo;
    }

    private static Slider AddSlider(StackPanel parent, string label, double minimum, double maximum, double value, string suffix)
    {
        var title = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 3) };
        parent.Children.Add(title);
        var panel = new DockPanel();
        var valueText = new TextBlock { Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(valueText, Dock.Right);
        panel.Children.Add(valueText);
        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = value,
            TickFrequency = Math.Max(1, (maximum - minimum) / 10),
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 12, 0)
        };
        slider.ValueChanged += (_, _) => valueText.Text = $"{Math.Round(slider.Value)} {suffix}";
        valueText.Text = $"{Math.Round(value)} {suffix}";
        panel.Children.Add(slider);
        parent.Children.Add(panel);
        return slider;
    }
}
