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
    private readonly ComboBox _activeHold;
    private readonly Slider _briefHold;
    private readonly Slider _dimDuration;
    private readonly Slider _dimSteps;
    private readonly Slider _maximumOpacity;

    private readonly Slider _padding;
    private readonly Slider _minimumMotion;
    private readonly Slider _mergeGap;
    private readonly Slider _sampling;

    private readonly CheckBox _mouseVisual;
    private readonly Slider _mouseRadius;
    private readonly Slider _mouseTrail;

    private readonly CheckBox _startWithWindows;

    public SettingsWindow(
        AppSettings settings)
    {
        Title =
            "OledGuard — Paramètres";
        Width = 640;
        Height = 820;
        MinWidth = 540;
        MinHeight = 620;
        WindowStartupLocation =
            WindowStartupLocation.CenterScreen;
        ResizeMode =
            ResizeMode.CanResize;
        Background =
            new SolidColorBrush(
                Color.FromRgb(
                    245,
                    245,
                    245));

        var root =
            new Grid
            {
                Margin =
                    new Thickness(
                        20)
            };

        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    GridLength.Auto
            });
        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    new GridLength(
                        1,
                        GridUnitType.Star)
            });
        root.RowDefinitions.Add(
            new RowDefinition
            {
                Height =
                    GridLength.Auto
            });

        var heading =
            new TextBlock
            {
                Text =
                    "Protection OLED",
                FontSize = 22,
                FontWeight =
                    FontWeights.SemiBold,
                Margin =
                    new Thickness(
                        0,
                        0,
                        0,
                        5)
            };

        Grid.SetRow(
            heading,
            0);
        root.Children.Add(
            heading);

        var subtitle =
            new TextBlock
            {
                Text =
                    "Réglages simples du moteur réellement utilisé. " +
                    "Les zones restent carrées et stables, puis " +
                    "s'assombrissent entièrement par étapes.",
                TextWrapping =
                    TextWrapping.Wrap,
                Foreground =
                    Brushes.DimGray,
                Margin =
                    new Thickness(
                        0,
                        30,
                        0,
                        10)
            };

        Grid.SetRow(
            subtitle,
            0);
        root.Children.Add(
            subtitle);

        var form =
            new StackPanel
            {
                Orientation =
                    Orientation.Vertical,
                Margin =
                    new Thickness(
                        0,
                        0,
                        12,
                        0)
            };

        var scroll =
            new ScrollViewer
            {
                Content = form,
                VerticalScrollBarVisibility =
                    ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    ScrollBarVisibility.Disabled
            };

        Grid.SetRow(
            scroll,
            1);
        root.Children.Add(
            scroll);

        AddSection(
            form,
            "Comportement");

        _activeHold =
            AddCombo(
                form,
                "Maintien d'une zone animée après son dernier mouvement",
                new[]
                {
                    10,
                    15,
                    30,
                    45,
                    60,
                    90,
                    120
                },
                settings
                    .MotionZoneRecurringHoldMilliseconds /
                1000,
                "secondes");

        _briefHold =
            AddSlider(
                form,
                "Maintien d'un changement bref",
                1,
                10,
                settings
                    .MotionZoneOneShotHoldMilliseconds /
                1000.0,
                "s",
                1);

        _dimDuration =
            AddSlider(
                form,
                "Durée de l'assombrissement complet de la zone",
                0,
                5,
                settings
                    .MotionZoneDimDurationMilliseconds /
                1000.0,
                "s",
                1);

        _dimSteps =
            AddSlider(
                form,
                "Nombre d'étapes d'assombrissement",
                2,
                12,
                settings
                    .MotionZoneDimSteps,
                "étapes");

        _maximumOpacity =
            AddSlider(
                form,
                "Assombrissement maximal",
                40,
                95,
                settings
                    .MaximumMaskOpacity *
                100.0,
                "%");

        AddSection(
            form,
            "Forme et réactivité");

        _padding =
            AddSlider(
                form,
                "Marge propre autour d'une zone détectée",
                0,
                3,
                settings
                    .MotionZonePaddingCells,
                "cellules");

        _minimumMotion =
            AddSlider(
                form,
                "Taille minimale d'un mouvement",
                2,
                20,
                settings
                    .MotionZoneMinimumMotionCells,
                "cellules");

        _mergeGap =
            AddSlider(
                form,
                "Petit espace autorisé dans un même logo ou symbole",
                0,
                3,
                settings
                    .MotionZoneRenderMergeGapCells,
                "cellules");

        _sampling =
            AddSlider(
                form,
                "Réactivité de détection",
                16,
                60,
                settings
                    .MotionZoneSamplingMilliseconds,
                "ms");

        form.Children.Add(
            new TextBlock
            {
                Text =
                    "Valeurs recommandées : marge 1, mouvement 3, " +
                    "espace 1 et détection 20 ms. Les augmenter " +
                    "rend les zones plus larges ou moins sensibles.",
                TextWrapping =
                    TextWrapping.Wrap,
                Foreground =
                    Brushes.DimGray,
                Margin =
                    new Thickness(
                        0,
                        8,
                        0,
                        4)
            });

        AddSection(
            form,
            "Souris");

        _mouseVisual =
            new CheckBox
            {
                Content =
                    new TextBlock
                    {
                        Text =
                            "Halo fluide autour du curseur lorsqu'aucune zone dessous n'est déjà révélée",
                        TextWrapping =
                            TextWrapping.Wrap
                    },
                IsChecked =
                    settings
                        .MouseVisualEnabled,
                Margin =
                    new Thickness(
                        0,
                        8,
                        0,
                        8)
            };

        form.Children.Add(
            _mouseVisual);

        _mouseRadius =
            AddSlider(
                form,
                "Taille du halo",
                8,
                40,
                settings
                    .MouseVisualRadiusPixels,
                "px");

        _mouseTrail =
            AddSlider(
                form,
                "Longueur de la trace fluide",
                0,
                180,
                settings
                    .MouseTrailMilliseconds,
                "ms");

        _startWithWindows =
            new CheckBox
            {
                Content =
                    "Démarrer avec Windows",
                IsChecked =
                    settings
                        .StartWithWindows,
                Margin =
                    new Thickness(
                        0,
                        18,
                        0,
                        8)
            };

        form.Children.Add(
            _startWithWindows);

        var buttons =
            new StackPanel
            {
                Orientation =
                    Orientation.Horizontal,
                HorizontalAlignment =
                    HorizontalAlignment.Right,
                Margin =
                    new Thickness(
                        0,
                        14,
                        0,
                        0)
            };

        Grid.SetRow(
            buttons,
            2);
        root.Children.Add(
            buttons);

        var cancel =
            new Button
            {
                Content =
                    "Annuler",
                MinWidth = 90,
                Margin =
                    new Thickness(
                        0,
                        0,
                        10,
                        0),
                Padding =
                    new Thickness(
                        12,
                        7,
                        12,
                        7)
            };

        cancel.Click +=
            (_, _) =>
            {
                DialogResult = false;
                Close();
            };

        buttons.Children.Add(
            cancel);

        var save =
            new Button
            {
                Content =
                    "Enregistrer",
                MinWidth = 110,
                IsDefault = true,
                Padding =
                    new Thickness(
                        12,
                        7,
                        12,
                        7)
            };

        save.Click +=
            (_, _) =>
            {
                DialogResult = true;
                Close();
            };

        buttons.Children.Add(
            save);
        Content = root;
    }

    public AppSettings BuildSettings(
        AppSettings original)
    {
        var updated =
            original.Clone();

        updated
            .MotionZoneRecurringHoldMilliseconds =
            (int)_activeHold.SelectedItem *
            1000;
        updated
            .MotionZoneOneShotHoldMilliseconds =
            (int)Math.Round(
                _briefHold.Value *
                1000.0);
        updated
            .MotionZoneDimDurationMilliseconds =
            (int)Math.Round(
                _dimDuration.Value *
                1000.0);
        updated.MotionZoneDimSteps =
            (int)Math.Round(
                _dimSteps.Value);
        updated.MaximumMaskOpacity =
            _maximumOpacity.Value /
            100.0;

        updated.MotionZonePaddingCells =
            (int)Math.Round(
                _padding.Value);
        updated.MotionZoneMinimumMotionCells =
            (int)Math.Round(
                _minimumMotion.Value);
        updated.MotionZoneRenderMergeGapCells =
            (int)Math.Round(
                _mergeGap.Value);
        updated.MotionZoneSamplingMilliseconds =
            (int)Math.Round(
                _sampling.Value);

        updated.MouseVisualEnabled =
            _mouseVisual.IsChecked ==
            true;
        updated.MouseVisualRadiusPixels =
            (int)Math.Round(
                _mouseRadius.Value);
        updated.MouseTrailMilliseconds =
            (int)Math.Round(
                _mouseTrail.Value);

        updated.StartWithWindows =
            _startWithWindows.IsChecked ==
            true;

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
                    Margin =
                        new Thickness(
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
                Margin =
                    new Thickness(
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
                TextWrapping =
                    TextWrapping.Wrap,
                Margin =
                    new Thickness(
                        0,
                        7,
                        0,
                        4)
            });

        var combo =
            new ComboBox
            {
                Width = 190,
                HorizontalAlignment =
                    HorizontalAlignment.Left
            };

        foreach (var value in
                 values)
        {
            combo.Items.Add(
                value);
        }

        combo.SelectedItem =
            values.Contains(
                selected)
                ? selected
                : values
                    .OrderBy(
                        value =>
                            Math.Abs(
                                value -
                                selected))
                    .First();

        parent.Children.Add(
            combo);

        parent.Children.Add(
            new TextBlock
            {
                Text = suffix,
                Foreground =
                    Brushes.Gray,
                Margin =
                    new Thickness(
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
                TextWrapping =
                    TextWrapping.Wrap,
                Margin =
                    new Thickness(
                        0,
                        7,
                        0,
                        3)
            });

        var panel =
            new DockPanel();

        var valueText =
            new TextBlock
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
        panel.Children.Add(
            valueText);

        var slider =
            new Slider
            {
                Minimum = minimum,
                Maximum = maximum,
                Value =
                    Math.Clamp(
                        value,
                        minimum,
                        maximum),
                TickFrequency =
                    Math.Max(
                        0.1,
                        (maximum -
                         minimum) /
                        10.0),
                IsSnapToTickEnabled = false,
                Margin =
                    new Thickness(
                        0,
                        0,
                        12,
                        0)
            };

        void RefreshValue()
        {
            var rounded =
                Math.Round(
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
            (_, _) =>
                RefreshValue();

        RefreshValue();
        panel.Children.Add(
            slider);
        parent.Children.Add(
            panel);

        return slider;
    }
}
