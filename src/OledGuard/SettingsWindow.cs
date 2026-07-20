using System.Windows;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Dock = System.Windows.Controls.Dock;
using DockPanel = System.Windows.Controls.DockPanel;
using Grid = System.Windows.Controls.Grid;
using GridLength = System.Windows.GridLength;
using GridUnitType = System.Windows.GridUnitType;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Orientation = System.Windows.Controls.Orientation;
using RowDefinition = System.Windows.Controls.RowDefinition;
using Slider = System.Windows.Controls.Slider;
using StackPanel = System.Windows.Controls.StackPanel;
using TextAlignment = System.Windows.TextAlignment;
using TextBlock = System.Windows.Controls.TextBlock;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OledGuard;

internal sealed class SettingsWindow : Window
{
    private readonly ComboBox _delay;
    private readonly ComboBox _cellSize;
    private readonly Slider _staticFade;
    private readonly Slider _staticOpacity;
    private readonly Slider _mouseRadius;
    private readonly Slider _mouseFeather;
    private readonly Slider _mouseHold;
    private readonly Slider _sweepInterval;
    private readonly Slider _sweepOpacity;
    private readonly CheckBox _sweepEnabled;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard — Paramètres V1";
        Width = 540;
        Height = 690;
        MinWidth = 500;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Fenêtre active + protection statique",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _delay = AddCombo(form, "Avant d'assombrir une zone immobile", new[] { 10, 20, 30, 60, 120 }, settings.StaticDelaySeconds, "secondes");
        _staticFade = AddSlider(form, "Durée du fondu statique", 3, 60, settings.StaticFadeSeconds, "s");
        _staticOpacity = AddSlider(form, "Noir maximal des zones statiques", 70, 100, settings.MaximumStaticOpacity * 100.0, "%");
        _cellSize = AddCombo(form, "Précision / consommation", new[] { 24, 32, 40, 48, 64 }, settings.CellSizePixels, "pixels par cellule");
        _mouseRadius = AddSlider(form, "Rayon clair autour de la souris", 24, 160, settings.MouseRevealRadiusPixels, "px");
        _mouseFeather = AddSlider(form, "Dégradé de la traînée souris", 0, 180, settings.MouseRevealFeatherPixels, "px");
        _mouseHold = AddSlider(form, "Durée de la traînée souris", 5, 60, settings.MouseRevealHoldMilliseconds / 1000.0, "s");

        _sweepEnabled = new CheckBox
        {
            Content = "Balayage sombre de repos dans la fenêtre active",
            IsChecked = settings.RestSweepEnabled,
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 14
        };
        form.Children.Add(_sweepEnabled);
        _sweepInterval = AddSlider(form, "Intervalle du balayage", 30, 240, settings.RestSweepIntervalSeconds, "s");
        _sweepOpacity = AddSlider(form, "Intensité du balayage", 5, 50, settings.RestSweepOpacity * 100.0, "%");

        _startWithWindows = new CheckBox
        {
            Content = "Démarrer avec Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(0, 14, 0, 0),
            FontSize = 14
        };
        form.Children.Add(_startWithWindows);

        var note = new TextBlock
        {
            Text = "Le bureau autour de la fenêtre au premier plan devient noir. Dans la fenêtre, les blocs réellement immobiles s'assombrissent ensemble. La souris ouvre une traînée ronde et dégradée, y compris hors de la fenêtre. Le balayage est uniquement sombre : il offre une courte pause aux pixels sans ajouter de lumière. Ctrl+Alt+O active/désactive ; Ctrl+Alt+R révèle tout 10 s.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 16, 0, 0)
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

        var cancel = new Button
        {
            Content = "Annuler",
            MinWidth = 90,
            Margin = new Thickness(0, 0, 10, 0),
            Padding = new Thickness(12, 7, 12, 7)
        };
        cancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };
        buttons.Children.Add(cancel);

        var save = new Button
        {
            Content = "Enregistrer",
            MinWidth = 110,
            IsDefault = true,
            Padding = new Thickness(12, 7, 12, 7)
        };
        save.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(save);

        Content = root;
    }

    public AppSettings BuildSettings(AppSettings original)
    {
        var updated = original.Clone();
        updated.StaticDelaySeconds = (int)_delay.SelectedItem;
        updated.StaticFadeSeconds = (int)Math.Round(_staticFade.Value);
        updated.MaximumStaticOpacity = _staticOpacity.Value / 100.0;
        updated.CellSizePixels = (int)_cellSize.SelectedItem;
        updated.MouseRevealRadiusPixels = (int)Math.Round(_mouseRadius.Value);
        updated.MouseRevealFeatherPixels = (int)Math.Round(_mouseFeather.Value);
        updated.MouseRevealHoldMilliseconds = (int)Math.Round(_mouseHold.Value * 1000.0);
        updated.RestSweepEnabled = _sweepEnabled.IsChecked == true;
        updated.RestSweepIntervalSeconds = (int)Math.Round(_sweepInterval.Value);
        updated.RestSweepOpacity = _sweepOpacity.Value / 100.0;
        updated.StartWithWindows = _startWithWindows.IsChecked == true;
        updated.Normalize();
        return updated;
    }

    private static ComboBox AddCombo(StackPanel parent, string label, int[] values, int selected, string suffix)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 8, 0, 5)
        });

        var combo = new ComboBox
        {
            Width = 190,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        foreach (var value in values)
        {
            combo.Items.Add(value);
        }

        combo.SelectedItem = values.Contains(selected)
            ? selected
            : values.OrderBy(value => Math.Abs(value - selected)).First();

        parent.Children.Add(combo);
        parent.Children.Add(new TextBlock
        {
            Text = suffix,
            Foreground = System.Windows.Media.Brushes.Gray,
            Margin = new Thickness(200, -25, 0, 8)
        });
        return combo;
    }

    private static Slider AddSlider(StackPanel parent, string label, double minimum, double maximum, double value, string suffix)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 9, 0, 3)
        });

        var panel = new DockPanel();
        var valueText = new TextBlock
        {
            Width = 78,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        DockPanel.SetDock(valueText, Dock.Right);
        panel.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(value, minimum, maximum),
            TickFrequency = Math.Max(1, (maximum - minimum) / 10),
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 12, 0)
        };
        slider.ValueChanged += (_, _) => valueText.Text = $"{Math.Round(slider.Value)} {suffix}";
        valueText.Text = $"{Math.Round(slider.Value)} {suffix}";
        panel.Children.Add(slider);
        parent.Children.Add(panel);
        return slider;
    }
}
