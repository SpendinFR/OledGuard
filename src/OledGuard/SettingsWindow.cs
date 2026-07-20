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
    private readonly Slider _contentFeather;
    private readonly Slider _mousePadding;
    private readonly Slider _mouseFeather;
    private readonly Slider _mouseHold;
    private readonly Slider _darkenFade;
    private readonly Slider _revealFade;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard — Paramètres";
        Width = 530;
        Height = 660;
        MinWidth = 480;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Protection OLED par blocs actifs",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new StackPanel { Orientation = Orientation.Vertical };
        Grid.SetRow(form, 1);
        root.Children.Add(form);

        _delay = AddCombo(form, "Durée visible après une activité", new[] { 5, 15, 30, 60, 120 }, settings.StaticDelaySeconds, "secondes");
        _cellSize = AddCombo(form, "Précision de détection", new[] { 24, 32, 40, 48, 64 }, settings.CellSizePixels, "pixels par cellule");
        _contentFeather = AddSlider(form, "Dégradé autour d'une zone de contenu", 16, 180, settings.ContentFeatherRadiusPixels, "px");
        _mousePadding = AddSlider(form, "Marge rectangulaire autour du trajet souris", 0, 120, settings.MouseRevealRadiusPixels, "px");
        _mouseFeather = AddSlider(form, "Dégradé autour du trajet souris", 16, 180, settings.MouseFeatherRadiusPixels, "px");
        _mouseHold = AddSlider(form, "Durée visible après passage de la souris", 5, 60, settings.MouseRevealHoldMilliseconds / 1000.0, "s");
        _revealFade = AddSlider(form, "Réapparition", 40, 500, settings.RevealFadeMilliseconds, "ms");
        _darkenFade = AddSlider(form, "Retour uniforme au noir", 1, 12, settings.DarkenFadeMilliseconds / 1000.0, "s");

        _startWithWindows = new CheckBox
        {
            Content = "Démarrer avec Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(0, 16, 0, 0),
            FontSize = 14
        };
        form.Children.Add(_startWithWindows);

        var note = new TextBlock
        {
            Text = "Réglage conseillé : 30 s, grille 32 px, dégradé contenu 72 px, marge souris 40 px et dégradé souris 72 px. Les petits clignotements restent minuscules. Un trajet de souris forme un bloc rectangulaire qui s'éteint ensemble. Ctrl+Alt+O active/désactive et Ctrl+Alt+R révèle tout pendant 10 s.",
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
        updated.CellSizePixels = (int)_cellSize.SelectedItem;
        updated.ContentFeatherRadiusPixels = (int)Math.Round(_contentFeather.Value);
        updated.MouseRevealRadiusPixels = (int)Math.Round(_mousePadding.Value);
        updated.MouseFeatherRadiusPixels = (int)Math.Round(_mouseFeather.Value);
        updated.MouseRevealHoldMilliseconds = (int)Math.Round(_mouseHold.Value * 1000.0);
        updated.RevealFadeMilliseconds = (int)Math.Round(_revealFade.Value);
        updated.DarkenFadeMilliseconds = (int)Math.Round(_darkenFade.Value * 1000.0);
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
