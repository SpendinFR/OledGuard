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
using Separator = System.Windows.Controls.Separator;
using Slider = System.Windows.Controls.Slider;
using StackPanel = System.Windows.Controls.StackPanel;
using TextAlignment = System.Windows.TextAlignment;
using TextBlock = System.Windows.Controls.TextBlock;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace OledGuard;

internal sealed class SettingsWindow : Window
{
    private readonly ComboBox _staticDelay;
    private readonly ComboBox _detectionCell;
    private readonly ComboBox _visualCell;
    private readonly ComboBox _samples;
    private readonly Slider _maximumOpacity;
    private readonly Slider _darkenFade;
    private readonly Slider _revealFade;
    private readonly Slider _opacitySteps;
    private readonly Slider _contentPadding;
    private readonly Slider _contentMerge;
    private readonly Slider _minimumComponent;
    private readonly Slider _minimumBlockWidth;
    private readonly Slider _minimumBlockHeight;
    private readonly Slider _contentFeather;
    private readonly Slider _cornerRoundness;
    private readonly Slider _mouseRadius;
    private readonly Slider _mouseHold;
    private readonly CheckBox _mouseStationary;
    private readonly Slider _differenceThreshold;
    private readonly Slider _changedFraction;
    private readonly Slider _strongThreshold;
    private readonly Slider _strongFraction;
    private readonly Slider _weakConfirmation;
    private readonly Slider _visibleSampling;
    private readonly Slider _maskedSampling;
    private readonly Slider _minimumLuminance;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard — Réglages hybrides";
        Width = 640;
        Height = 830;
        MinWidth = 540;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 245));

        var root = new Grid { Margin = new Thickness(20) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = "Pixel trail 0.1 + blocs de contenu 0.5",
            FontSize = 21,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var form = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 12, 0) };
        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        AddSection(form, "Assombrissement");
        _staticDelay = AddCombo(form, "Délai avant assombrissement", new[] { 5, 10, 15, 30, 45, 60, 120, 300 }, settings.StaticDelaySeconds, "secondes");
        _maximumOpacity = AddSlider(form, "Assombrissement maximum", 35, 100, settings.MaximumMaskOpacity * 100.0, "%");
        _darkenFade = AddSlider(form, "Durée du fondu vers sombre", 0.5, 20, settings.DarkenFadeMilliseconds / 1000.0, "s", 1);
        _revealFade = AddSlider(form, "Réapparition", 40, 1500, settings.RevealFadeMilliseconds, "ms");
        _opacitySteps = AddSlider(form, "Nombre de niveaux du dégradé pixel", 4, 64, settings.OpacitySteps, "niveaux");
        _minimumLuminance = AddSlider(form, "Ne pas assombrir sous cette luminance (0 = désactivé)", 0, 80, settings.MinimumLuminanceToDim, "");

        AddSection(form, "Détection et gros blocs");
        _detectionCell = AddCombo(form, "Taille d'une cellule de détection", new[] { 16, 20, 24, 32, 40, 48, 64, 80, 96 }, settings.DetectionCellSizePixels, "px");
        _visualCell = AddCombo(form, "Taille des pixels visuels", new[] { 8, 12, 16, 20, 24, 32, 40, 48 }, settings.VisualCellSizePixels, "px");
        _samples = AddCombo(form, "Échantillons par cellule", new[] { 2, 3, 4, 5, 6 }, settings.SamplesPerCell, "par côté");
        _minimumComponent = AddSlider(form, "Nombre minimum de cellules qui bougent", 1, 12, settings.MinimumActivityComponentCells, "cellules");
        _minimumBlockWidth = AddSlider(form, "Largeur minimale d'un bloc réveillé", 1, 10, settings.MinimumRevealBlockWidthCells, "cellules");
        _minimumBlockHeight = AddSlider(form, "Hauteur minimale d'un bloc réveillé", 1, 10, settings.MinimumRevealBlockHeightCells, "cellules");
        _contentPadding = AddSlider(form, "Marge autour d'un bloc actif", 0, 5, settings.ContentActivationPaddingCells, "cellules");
        _contentMerge = AddSlider(form, "Distance de fusion entre activités", 0, 5, settings.ContentMergeGapCells, "cellules");
        _contentFeather = AddSlider(form, "Dégradé autour des gros blocs", 0, 240, settings.ContentFeatherRadiusPixels, "px");
        _cornerRoundness = AddSlider(form, "Arrondi du dégradé des blocs", 0, 100, settings.ContentCornerRoundnessPercent, "%");

        AddSection(form, "Souris — comportement original");
        _mouseRadius = AddSlider(form, "Rayon du chemin révélé", 20, 500, settings.MouseRevealRadiusPixels, "px");
        _mouseHold = AddSlider(form, "Maintien après passage", 0, 120, settings.MouseRevealHoldMilliseconds / 1000.0, "s");
        _mouseStationary = new CheckBox
        {
            Content = "Maintenir la zone sous une souris immobile",
            IsChecked = settings.MouseRevealWhileStationary,
            Margin = new Thickness(0, 7, 0, 5)
        };
        form.Children.Add(_mouseStationary);

        AddSection(form, "Sensibilité");
        _differenceThreshold = AddSlider(form, "Différence minimale", 0.5, 20, settings.DifferenceThreshold, "", 1);
        _changedFraction = AddSlider(form, "Part minimale d'échantillons modifiés", 1, 100, settings.ChangedSampleFraction * 100.0, "%");
        _strongThreshold = AddSlider(form, "Différence forte", 1, 50, settings.StrongDifferenceThreshold, "", 1);
        _strongFraction = AddSlider(form, "Part d'échantillons fortement modifiés", 1, 100, settings.StrongChangedSampleFraction * 100.0, "%");
        _weakConfirmation = AddSlider(form, "Confirmations d'un changement faible", 1, 6, settings.WeakChangeConfirmationSamples, "captures");

        AddSection(form, "Performance");
        _visibleSampling = AddSlider(form, "Analyse quand l'écran est visible", 250, 5000, settings.VisibleSamplingMilliseconds, "ms");
        _maskedSampling = AddSlider(form, "Analyse quand l'écran est assombri", 100, 2000, settings.MaskedSamplingMilliseconds, "ms");
        _startWithWindows = new CheckBox
        {
            Content = "Démarrer avec Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(0, 12, 0, 8)
        };
        form.Children.Add(_startWithWindows);

        form.Children.Add(new TextBlock
        {
            Text = "Point de départ conseillé : détection 32 px, pixels visuels 16 px, opacité 88 %, blocs minimum 2×2, rayon souris 170 px. Modifie un seul groupe à la fois pour trouver ton réglage.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 12, 0, 12)
        });

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
        updated.StaticDelaySeconds = (int)_staticDelay.SelectedItem;
        updated.DetectionCellSizePixels = (int)_detectionCell.SelectedItem;
        updated.VisualCellSizePixels = (int)_visualCell.SelectedItem;
        updated.SamplesPerCell = (int)_samples.SelectedItem;
        updated.MaximumMaskOpacity = _maximumOpacity.Value / 100.0;
        updated.DarkenFadeMilliseconds = (int)Math.Round(_darkenFade.Value * 1000.0);
        updated.RevealFadeMilliseconds = (int)Math.Round(_revealFade.Value);
        updated.OpacitySteps = (int)Math.Round(_opacitySteps.Value);
        updated.MinimumLuminanceToDim = (byte)Math.Round(_minimumLuminance.Value);
        updated.MinimumActivityComponentCells = (int)Math.Round(_minimumComponent.Value);
        updated.MinimumRevealBlockWidthCells = (int)Math.Round(_minimumBlockWidth.Value);
        updated.MinimumRevealBlockHeightCells = (int)Math.Round(_minimumBlockHeight.Value);
        updated.ContentActivationPaddingCells = (int)Math.Round(_contentPadding.Value);
        updated.ContentMergeGapCells = (int)Math.Round(_contentMerge.Value);
        updated.ContentFeatherRadiusPixels = (int)Math.Round(_contentFeather.Value);
        updated.ContentCornerRoundnessPercent = (int)Math.Round(_cornerRoundness.Value);
        updated.MouseRevealRadiusPixels = (int)Math.Round(_mouseRadius.Value);
        updated.MouseRevealHoldMilliseconds = (int)Math.Round(_mouseHold.Value * 1000.0);
        updated.MouseRevealWhileStationary = _mouseStationary.IsChecked == true;
        updated.DifferenceThreshold = _differenceThreshold.Value;
        updated.ChangedSampleFraction = _changedFraction.Value / 100.0;
        updated.StrongDifferenceThreshold = _strongThreshold.Value;
        updated.StrongChangedSampleFraction = _strongFraction.Value / 100.0;
        updated.WeakChangeConfirmationSamples = (int)Math.Round(_weakConfirmation.Value);
        updated.VisibleSamplingMilliseconds = (int)Math.Round(_visibleSampling.Value);
        updated.MaskedSamplingMilliseconds = (int)Math.Round(_maskedSampling.Value);
        updated.StartWithWindows = _startWithWindows.IsChecked == true;
        updated.Normalize();
        return updated;
    }

    private static void AddSection(StackPanel parent, string title)
    {
        if (parent.Children.Count > 0)
        {
            parent.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 8) });
        }
        parent.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 5)
        });
    }

    private static ComboBox AddCombo(StackPanel parent, string label, int[] values, int selected, string suffix)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 7, 0, 4)
        });
        var combo = new ComboBox { Width = 190, HorizontalAlignment = HorizontalAlignment.Left };
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
            Margin = new Thickness(200, -25, 0, 5)
        });
        return combo;
    }

    private static Slider AddSlider(
        StackPanel parent,
        string label,
        double minimum,
        double maximum,
        double value,
        string suffix,
        int decimals = 0)
    {
        parent.Children.Add(new TextBlock
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 7, 0, 3)
        });
        var panel = new DockPanel();
        var valueText = new TextBlock
        {
            Width = 105,
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
            TickFrequency = Math.Max(0.1, (maximum - minimum) / 20),
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 12, 0)
        };
        void UpdateText() => valueText.Text = $"{Math.Round(slider.Value, decimals)} {suffix}".TrimEnd();
        slider.ValueChanged += (_, _) => UpdateText();
        UpdateText();
        panel.Children.Add(slider);
        parent.Children.Add(panel);
        return slider;
    }
}
