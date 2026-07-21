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
    private readonly ComboBox _activeHoldSeconds;
    private readonly Slider _briefHoldSeconds;
    private readonly Slider _dimDurationSeconds;
    private readonly Slider _dimSteps;
    private readonly Slider _maximumOpacity;

    private readonly Slider _zonePaddingCells;
    private readonly Slider _motionMergeRadiusCells;
    private readonly Slider _renderMergeGapCells;
    private readonly Slider _minimumMotionCells;
    private readonly Slider _minimumVisibleAreaCells;

    private readonly Slider _mouseRadiusPixels;
    private readonly Slider _geometryRefreshMilliseconds;
    private readonly Slider _samplingMilliseconds;
    private readonly CheckBox _startWithWindows;

    public SettingsWindow(AppSettings settings)
    {
        Title = "OledGuard — Réglages utiles";
        Width = 640;
        Height = 860;
        MinWidth = 540;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        Background = new SolidColorBrush(
            System.Windows.Media.Color.FromRgb(
                245,
                245,
                245));

        var root = new Grid
        {
            Margin = new Thickness(20)
        };
        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });
        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = new GridLength(
                    1,
                    GridUnitType.Star)
            });
        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height = GridLength.Auto
            });

        var heading = new TextBlock
        {
            Text = "Moteur de zones actives",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(
                0,
                0,
                0,
                5)
        };
        Grid.SetRow(heading, 0);
        root.Children.Add(heading);

        var subtitle = new TextBlock
        {
            Text =
                "Ces réglages pilotent réellement le moteur actuel. " +
                "Une zone reste entière tant qu'elle est active, puis " +
                "s'assombrit uniformément par paliers.",
            TextWrapping = TextWrapping.Wrap,
            Foreground =
                System.Windows.Media.Brushes.DimGray,
            Margin = new Thickness(
                0,
                30,
                0,
                10)
        };
        Grid.SetRow(subtitle, 0);
        root.Children.Add(subtitle);

        var form = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Margin = new Thickness(
                0,
                0,
                12,
                0)
        };
        var scroll = new ScrollViewer
        {
            Content = form,
            VerticalScrollBarVisibility =
                ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility =
                ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);

        AddSection(
            form,
            "Durée et assombrissement");

        _activeHoldSeconds = AddCombo(
            form,
            "Maintien d'une zone active après son dernier mouvement",
            new[]
            {
                5,
                10,
                15,
                30,
                45,
                60,
                90,
                120
            },
            Math.Max(
                1,
                settings
                    .MotionZoneRecurringHoldMilliseconds /
                1000),
            "secondes");

        _briefHoldSeconds = AddSlider(
            form,
            "Maintien d'une activité brève",
            0.5,
            10,
            settings.MotionZoneOneShotHoldMilliseconds /
                1000.0,
            "s",
            1);

        _dimDurationSeconds = AddSlider(
            form,
            "Durée de l'assombrissement par paliers",
            0,
            20,
            settings.MotionZoneDimDurationMilliseconds /
                1000.0,
            "s",
            1);

        _dimSteps = AddSlider(
            form,
            "Nombre de paliers d'assombrissement",
            2,
            12,
            settings.MotionZoneDimSteps,
            "paliers");

        _maximumOpacity = AddSlider(
            form,
            "Assombrissement maximal",
            25,
            98,
            settings.MaximumMaskOpacity * 100.0,
            "%");

        form.Children.Add(
            new TextBlock
            {
                Text =
                    "À la fin du maintien, tout le rectangle change " +
                    "d'opacité en même temps. Il ne se rétrécit pas. " +
                    "L'assombrissement final reste limité par le " +
                    "pourcentage ci-dessus.",
                TextWrapping = TextWrapping.Wrap,
                Foreground =
                    System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(
                    0,
                    8,
                    0,
                    4)
            });

        AddSection(
            form,
            "Révélation d'un élément en mouvement");

        _zonePaddingCells = AddSlider(
            form,
            "Marge autour de la zone détectée",
            0,
            6,
            settings.MotionZonePaddingCells,
            "cellules");

        _motionMergeRadiusCells = AddSlider(
            form,
            "Épaississement du mouvement — assemble un logo ou une croix",
            0,
            4,
            settings.MotionZoneMergeRadiusCells,
            "cellules");

        _renderMergeGapCells = AddSlider(
            form,
            "Distance pour réunir deux zones proches",
            0,
            8,
            settings.MotionZoneRenderMergeGapCells,
            "cellules");

        _minimumMotionCells = AddSlider(
            form,
            "Mouvement minimal à accepter",
            1,
            20,
            settings.MotionZoneMinimumMotionCells,
            "cellules");

        _minimumVisibleAreaCells = AddSlider(
            form,
            "Taille minimale d'une zone révélée",
            1,
            50,
            settings.MotionZoneMinimumVisibleAreaCells,
            "cellules");

        form.Children.Add(
            new TextBlock
            {
                Text =
                    "Pour révéler tout un petit logo ou toute une croix, " +
                    "augmente d'abord la marge à 2. Si ses morceaux " +
                    "restent séparés, mets l'épaississement à 2.",
                TextWrapping = TextWrapping.Wrap,
                Foreground =
                    System.Windows.Media.Brushes.DimGray,
                Margin = new Thickness(
                    0,
                    8,
                    0,
                    4)
            });

        AddSection(
            form,
            "Souris");

        _mouseRadiusPixels = AddSlider(
            form,
            "Révélation autour du curseur",
            4,
            96,
            settings.MouseHoverRadiusPixels,
            "px");

        AddSection(
            form,
            "Réactivité");

        _geometryRefreshMilliseconds = AddSlider(
            form,
            "Actualisation de la géométrie active",
            200,
            3000,
            settings.MotionZoneGeometryRefreshMilliseconds,
            "ms");

        _samplingMilliseconds = AddSlider(
            form,
            "Intervalle de détection du mouvement",
            20,
            100,
            settings.MotionZoneSamplingMilliseconds,
            "ms");

        _startWithWindows = new CheckBox
        {
            Content = "Démarrer avec Windows",
            IsChecked = settings.StartWithWindows,
            Margin = new Thickness(
                0,
                16,
                0,
                8)
        };
        form.Children.Add(_startWithWindows);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment =
                HorizontalAlignment.Right,
            Margin = new Thickness(
                0,
                14,
                0,
                0)
        };
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);

        var cancel = new Button
        {
            Content = "Annuler",
            MinWidth = 90,
            Margin = new Thickness(
                0,
                0,
                10,
                0),
            Padding = new Thickness(
                12,
                7,
                12,
                7)
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
            Padding = new Thickness(
                12,
                7,
                12,
                7)
        };
        save.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };
        buttons.Children.Add(save);

        Content = root;
    }

    public AppSettings BuildSettings(
        AppSettings original)
    {
        var updated = original.Clone();

        updated.MotionZoneRecurringHoldMilliseconds =
            (int)_activeHoldSeconds.SelectedItem *
            1000;
        updated.MotionZoneOneShotHoldMilliseconds =
            (int)Math.Round(
                _briefHoldSeconds.Value *
                1000.0);
        updated.MotionZoneDimDurationMilliseconds =
            (int)Math.Round(
                _dimDurationSeconds.Value *
                1000.0);
        updated.MotionZoneDimSteps =
            (int)Math.Round(
                _dimSteps.Value);
        updated.MaximumMaskOpacity =
            _maximumOpacity.Value /
            100.0;

        updated.MotionZonePaddingCells =
            (int)Math.Round(
                _zonePaddingCells.Value);
        updated.MotionZoneMergeRadiusCells =
            (int)Math.Round(
                _motionMergeRadiusCells.Value);
        updated.MotionZoneRenderMergeGapCells =
            (int)Math.Round(
                _renderMergeGapCells.Value);
        updated.MotionZoneMinimumMotionCells =
            (int)Math.Round(
                _minimumMotionCells.Value);
        updated.MotionZoneMinimumVisibleAreaCells =
            (int)Math.Round(
                _minimumVisibleAreaCells.Value);

        updated.MouseHoverRadiusPixels =
            (int)Math.Round(
                _mouseRadiusPixels.Value);
        updated.MotionZoneGeometryRefreshMilliseconds =
            (int)Math.Round(
                _geometryRefreshMilliseconds.Value);
        updated.MotionZoneSamplingMilliseconds =
            (int)Math.Round(
                _samplingMilliseconds.Value);

        updated.StartWithWindows =
            _startWithWindows.IsChecked == true;

        updated.Normalize();
        return updated;
    }

    private static void AddSection(
        StackPanel parent,
        string title)
    {
        if (parent.Children.Count > 0)
        {
            parent.Children.Add(
                new Separator
                {
                    Margin = new Thickness(
                        0,
                        14,
                        0,
                        8)
                });
        }

        parent.Children.Add(
            new TextBlock
            {
                Text = title,
                FontSize = 16,
                FontWeight =
                    FontWeights.SemiBold,
                Margin = new Thickness(
                    0,
                    2,
                    0,
                    5)
            });
    }

    private static ComboBox AddCombo(
        StackPanel parent,
        string label,
        int[] values,
        int selected,
        string suffix)
    {
        parent.Children.Add(
            new TextBlock
            {
                Text = label,
                FontWeight =
                    FontWeights.SemiBold,
                Margin = new Thickness(
                    0,
                    7,
                    0,
                    4)
            });

        var combo = new ComboBox
        {
            Width = 190,
            HorizontalAlignment =
                HorizontalAlignment.Left
        };

        foreach (var value in values)
        {
            combo.Items.Add(value);
        }

        combo.SelectedItem =
            values.Contains(selected)
                ? selected
                : values
                    .OrderBy(
                        value =>
                            Math.Abs(
                                value -
                                selected))
                    .First();

        parent.Children.Add(combo);
        parent.Children.Add(
            new TextBlock
            {
                Text = suffix,
                Foreground =
                    System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(
                    200,
                    -25,
                    0,
                    5)
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
        parent.Children.Add(
            new TextBlock
            {
                Text = label,
                FontWeight =
                    FontWeights.SemiBold,
                Margin = new Thickness(
                    0,
                    7,
                    0,
                    3)
            });

        var panel = new DockPanel();
        var valueText = new TextBlock
        {
            Width = 110,
            TextAlignment =
                TextAlignment.Right,
            VerticalAlignment =
                VerticalAlignment.Center
        };
        DockPanel.SetDock(
            valueText,
            Dock.Right);
        panel.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = minimum,
            Maximum = maximum,
            Value = Math.Clamp(
                value,
                minimum,
                maximum),
            TickFrequency = Math.Max(
                0.1,
                (maximum - minimum) /
                10.0),
            IsSnapToTickEnabled = false,
            Margin = new Thickness(
                0,
                0,
                12,
                0)
        };

        void RefreshValue()
        {
            var rounded = Math.Round(
                slider.Value,
                decimals);
            var formatted =
                rounded.ToString(
                    "F" +
                    decimals);

            valueText.Text =
                string.IsNullOrWhiteSpace(
                    suffix)
                    ? formatted
                    : $"{formatted} {suffix}";
        }

        slider.ValueChanged +=
            (_, _) => RefreshValue();
        RefreshValue();

        panel.Children.Add(slider);
        parent.Children.Add(panel);
        return slider;
    }
}
