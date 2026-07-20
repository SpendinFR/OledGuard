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
    private readonly ComboBox _cellSize;
    private readonly ComboBox _samplesPerCell;
    private readonly Slider _maximumOpacity;
    private readonly Slider _minimumLuminance;
    private readonly Slider _darkenFade;
    private readonly Slider _revealFade;
    private readonly Slider _shortReference;
    private readonly Slider _mediumReference;
    private readonly Slider _longReference;
    private readonly Slider _stableConfirmations;
    private readonly Slider _differenceThreshold;
    private readonly Slider _changedFraction;
    private readonly Slider _majorityPasses;
    private readonly Slider _majorityThreshold;
    private readonly Slider _minimumDimRegion;
    private readonly Slider _maximumBrightHole;
    private readonly Slider _visibleSampling;
    private readonly Slider _maskedSampling;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard — Détection de stabilité";
        Width = 620;
        Height = 820;
        MinWidth = 520;
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
            Text = "Zones statiques propres",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 5)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var subtitle = new TextBlock
        {
            Text = "Chaque sous-zone garde son propre âge ; les régions connectées partagent ensuite une opacité uniforme.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(0, 30, 0, 10)
        };
        Grid.SetRow(subtitle, 0);
        root.Children.Add(subtitle);

        var form = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(0, 0, 12, 0) };
        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        AddSection(form, "Résultat visuel");
        _staticDelay = AddCombo(
            form,
            "Temps sans mouvement avant assombrissement",
            new[] { 5, 15, 30, 60, 120, 180, 300, 600, 900 },
            settings.StaticDelaySeconds,
            "secondes");
        _maximumOpacity = AddSlider(form, "Assombrissement maximum", 25, 98, settings.MaximumMaskOpacity * 100.0, "%");
        _minimumLuminance = AddSlider(form, "Ne pas traiter une région plus sombre que", 0, 100, settings.MinimumLuminanceToDim, "luminance");
        _darkenFade = AddSlider(form, "Durée du fondu vers sombre", 1, 120, settings.DarkenFadeMilliseconds / 1000.0, "s");
        _revealFade = AddSlider(form, "Réapparition après mouvement", 40, 2000, settings.RevealFadeMilliseconds, "ms");

        AddSection(form, "Comparaisons temporelles");
        _shortReference = AddSlider(form, "Référence courte", 1, 15, settings.ShortReferenceSeconds, "s");
        _mediumReference = AddSlider(form, "Référence moyenne", 3, 120, settings.MediumReferenceSeconds, "s");
        _longReference = AddSlider(form, "Référence longue", 10, 600, settings.LongReferenceSeconds, "s");
        _stableConfirmations = AddSlider(form, "Captures stables nécessaires", 1, 12, settings.StableConfirmationSamples, "captures");

        AddSection(form, "Taille et sensibilité");
        _cellSize = AddCombo(
            form,
            "Taille d'un bloc de capture",
            new[] { 32, 40, 48, 64, 80, 96, 128, 160 },
            settings.DetectionCellSizePixels,
            "px");
        _samplesPerCell = AddCombo(
            form,
            "Sous-zones indépendantes par bloc",
            new[] { 2, 3, 4, 5, 6, 8 },
            settings.SamplesPerCell,
            "par côté");
        _differenceThreshold = AddSlider(form, "Différence moyenne minimale", 0.5, 30, settings.DifferenceThreshold, "", 1);
        _changedFraction = AddSlider(form, "Support local minimal d'un mouvement", 1, 100, settings.ChangedSampleFraction * 100.0, "%");

        AddSection(form, "Nettoyage bidirectionnel");
        _majorityPasses = AddSlider(form, "Passages du filtre de majorité", 0, 5, settings.MajorityFilterPasses, "passes");
        _majorityThreshold = AddSlider(form, "Voisins statiques nécessaires sur 9", 5, 8, settings.MajorityDimThreshold, "voisins");
        _minimumDimRegion = AddSlider(form, "Taille minimale d'une zone à assombrir", 1, 100, settings.MinimumDimRegionCells, "cellules");
        _maximumBrightHole = AddSlider(form, "Taille maximale d'un trou clair à combler", 0, 100, settings.MaximumBrightHoleCells, "cellules");

        AddSection(form, "Performance");
        _visibleSampling = AddSlider(form, "Intervalle d'analyse normal", 250, 5000, settings.VisibleSamplingMilliseconds, "ms");
        _maskedSampling = AddSlider(form, "Intervalle quand une zone est sombre", 100, 3000, settings.MaskedSamplingMilliseconds, "ms");
        _startWithWindows = new CheckBox
        {
            Content = "Démarrer avec Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(0, 12, 0, 8)
        };
        form.Children.Add(_startWithWindows);

        form.Children.Add(new TextBlock
        {
            Text = "Ton réglage 128 px / 8 donne environ 16 px de précision réelle : les bords inchangés gardent leur ancienneté même si la fenêtre centrale change. Les régions statiques connectées utilisent exactement la même opacité, sans bandes internes.",
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
        updated.StaticDelaySeconds = (int)_staticDelay.SelectedItem;
        updated.DetectionCellSizePixels = (int)_cellSize.SelectedItem;
        updated.SamplesPerCell = (int)_samplesPerCell.SelectedItem;
        updated.MaximumMaskOpacity = _maximumOpacity.Value / 100.0;
        updated.MinimumLuminanceToDim = (byte)Math.Round(_minimumLuminance.Value);
        updated.DarkenFadeMilliseconds = (int)Math.Round(_darkenFade.Value * 1000.0);
        updated.RevealFadeMilliseconds = (int)Math.Round(_revealFade.Value);
        updated.ShortReferenceSeconds = (int)Math.Round(_shortReference.Value);
        updated.MediumReferenceSeconds = (int)Math.Round(_mediumReference.Value);
        updated.LongReferenceSeconds = (int)Math.Round(_longReference.Value);
        updated.StableConfirmationSamples = (int)Math.Round(_stableConfirmations.Value);
        updated.DifferenceThreshold = _differenceThreshold.Value;
        updated.ChangedSampleFraction = _changedFraction.Value / 100.0;
        updated.MajorityFilterPasses = (int)Math.Round(_majorityPasses.Value);
        updated.MajorityDimThreshold = (int)Math.Round(_majorityThreshold.Value);
        updated.MinimumDimRegionCells = (int)Math.Round(_minimumDimRegion.Value);
        updated.MaximumBrightHoleCells = (int)Math.Round(_maximumBrightHole.Value);
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
            Width = 110,
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
            TickFrequency = Math.Max(0.1, (maximum - minimum) / 10.0),
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 12, 0)
        };

        void RefreshValue()
        {
            var rounded = Math.Round(slider.Value, decimals);
            var formatted = rounded.ToString("F" + decimals);
            valueText.Text = string.IsNullOrWhiteSpace(suffix)
                ? formatted
                : $"{formatted} {suffix}";
        }

        slider.ValueChanged += (_, _) => RefreshValue();
        RefreshValue();
        panel.Children.Add(slider);
        parent.Children.Add(panel);
        return slider;
    }
}
