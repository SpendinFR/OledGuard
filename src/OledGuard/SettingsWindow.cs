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
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using Slider = System.Windows.Controls.Slider;
using StackPanel = System.Windows.Controls.StackPanel;
using TextAlignment = System.Windows.TextAlignment;
using TextBlock = System.Windows.Controls.TextBlock;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OledGuard;

internal sealed class SettingsWindow : Window
{
    private readonly ComboBox _delay;
    private readonly ComboBox _windowReveal;
    private readonly ComboBox _cellSize;
    private readonly ComboBox _minimumComponent;
    private readonly Slider _contentCore;
    private readonly Slider _contentFeather;
    private readonly Slider _gradientSteps;
    private readonly Slider _mouseRadius;
    private readonly Slider _mouseFeather;
    private readonly Slider _mouseHold;
    private readonly Slider _revealFade;
    private readonly Slider _darkenFade;
    private readonly CheckBox _restCycleEnabled;
    private readonly Slider _restInterval;
    private readonly Slider _restDuration;
    private readonly Slider _restStrength;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard V1 — Paramètres";
        Width = 560;
        Height = 790;
        MinWidth = 500;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));

        var root = new Grid { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Zone active + repos pixel",
            FontSize = 23,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 6)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var subtitle = new TextBlock
        {
            Text = "Le bureau reste noir. Seules les zones utiles de la fenêtre au premier plan et la zone de la souris sont révélées.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 34, 0, 12)
        };
        Grid.SetRow(subtitle, 0);
        root.Children.Add(subtitle);

        var form = new StackPanel { Orientation = Orientation.Vertical };
        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        AddSection(form, "Détection et durée");
        _delay = AddCombo(form, "Noir après la dernière activité", new[] { 5, 15, 30, 60, 120 }, settings.StaticDelaySeconds, "secondes");
        _windowReveal = AddCombo(form, "Révélation lors d'un changement de fenêtre", new[] { 2, 3, 5, 8, 12 }, settings.WindowRevealSeconds, "secondes");
        _cellSize = AddCombo(form, "Précision interne", new[] { 16, 20, 24, 28, 32, 40 }, settings.CellSizePixels, "pixels par cellule");
        _minimumComponent = AddCombo(form, "Filtre des micro-animations", new[] { 1, 2, 3, 4, 5 }, settings.MinimumActivityComponentCells, "cellules minimum");

        AddSection(form, "Dégradé carré noir / sombre / net");
        _contentCore = AddSlider(form, "Centre entièrement visible", 0, 4, settings.ContentCoreCells, "cellules");
        _contentFeather = AddSlider(form, "Anneaux jusqu'au noir", 2, 10, settings.ContentFeatherCells, "cellules");
        _gradientSteps = AddSlider(form, "Nombre de niveaux de gris", 3, 12, settings.GradientSteps, "niveaux");
        _revealFade = AddSlider(form, "Réapparition", 40, 500, settings.RevealFadeMilliseconds, "ms");
        _darkenFade = AddSlider(form, "Retour au noir", 0.5, 8, settings.DarkenFadeMilliseconds / 1000.0, "s");

        AddSection(form, "Découverte à la souris");
        _mouseRadius = AddSlider(form, "Carré visible autour du curseur", 16, 160, settings.MouseCoreRadiusPixels, "px");
        _mouseFeather = AddSlider(form, "Dégradé autour de la souris", 1, 10, settings.MouseFeatherCells, "cellules");
        _mouseHold = AddSlider(form, "Maintien après passage", 2, 60, settings.MouseRevealHoldMilliseconds / 1000.0, "s");

        AddSection(form, "Balayage de repos des pixels");
        _restCycleEnabled = new CheckBox
        {
            Content = "Activer le balayage noir périodique dans les zones visibles",
            IsChecked = settings.RestCycleEnabled,
            Margin = new Thickness(0, 5, 0, 4),
            FontSize = 14
        };
        form.Children.Add(_restCycleEnabled);
        _restInterval = AddSlider(form, "Intervalle", 30, 300, settings.RestCycleIntervalSeconds, "s");
        _restDuration = AddSlider(form, "Durée du passage", 2, 15, settings.RestCycleDurationMilliseconds / 1000.0, "s");
        _restStrength = AddSlider(form, "Noir maximal du passage", 40, 100, settings.RestCycleStrength * 100.0, "%");

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
            Text = "Réglages conseillés : 24 px, 30 s, filtre 2, 7 niveaux. Le balayage n'ajoute aucune couleur : il ne fait qu'augmenter temporairement le noir. Cible mémoire : moins de 100 Mo pour un écran 4K, selon le compositeur Windows.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 18, 8, 12)
        };
        form.Children.Add(note);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0)
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
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        buttons.Children.Add(cancel);

        var save = new Button
        {
            Content = "Enregistrer",
            MinWidth = 110,
            IsDefault = true,
            Padding = new Thickness(12, 7, 12, 7)
        };
        save.Click += (_, _) => { DialogResult = true; Close(); };
        buttons.Children.Add(save);

        Content = root;
    }

    public AppSettings BuildSettings(AppSettings original)
    {
        var updated = original.Clone();
        updated.StaticDelaySeconds = (int)_delay.SelectedItem;
        updated.WindowRevealSeconds = (int)_windowReveal.SelectedItem;
        updated.CellSizePixels = (int)_cellSize.SelectedItem;
        updated.MinimumActivityComponentCells = (int)_minimumComponent.SelectedItem;
        updated.ContentCoreCells = (int)Math.Round(_contentCore.Value);
        updated.ContentFeatherCells = (int)Math.Round(_contentFeather.Value);
        updated.GradientSteps = (int)Math.Round(_gradientSteps.Value);
        updated.RevealFadeMilliseconds = (int)Math.Round(_revealFade.Value);
        updated.DarkenFadeMilliseconds = (int)Math.Round(_darkenFade.Value * 1000.0);
        updated.MouseCoreRadiusPixels = (int)Math.Round(_mouseRadius.Value);
        updated.MouseFeatherCells = (int)Math.Round(_mouseFeather.Value);
        updated.MouseRevealHoldMilliseconds = (int)Math.Round(_mouseHold.Value * 1000.0);
        updated.RestCycleEnabled = _restCycleEnabled.IsChecked == true;
        updated.RestCycleIntervalSeconds = (int)Math.Round(_restInterval.Value);
        updated.RestCycleDurationMilliseconds = (int)Math.Round(_restDuration.Value * 1000.0);
        updated.RestCycleStrength = _restStrength.Value / 100.0;
        updated.StartWithWindows = _startWithWindows.IsChecked == true;
        updated.Normalize();
        return updated;
    }

    private static void AddSection(StackPanel parent, string title)
    {
        parent.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 16, 0, 3)
        });
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
            Width = 82,
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
            TickFrequency = Math.Max(0.1, (maximum - minimum) / 10),
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 12, 0)
        };
        slider.ValueChanged += (_, _) => valueText.Text = $"{Math.Round(slider.Value, slider.Value < 10 ? 1 : 0)} {suffix}";
        valueText.Text = $"{Math.Round(slider.Value, slider.Value < 10 ? 1 : 0)} {suffix}";
        panel.Children.Add(slider);
        parent.Children.Add(panel);
        return slider;
    }
}
