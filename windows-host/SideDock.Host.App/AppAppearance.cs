using System.Runtime.CompilerServices;
using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.Win32;

namespace SideDock.Host.App;

internal sealed record AppAppearancePalette(
    ElementTheme Theme,
    Windows.UI.Color PageBackground,
    Windows.UI.Color PanelBackground,
    Windows.UI.Color SidebarBackground,
    Windows.UI.Color SubtleBackground,
    Windows.UI.Color Stroke,
    Windows.UI.Color SoftStroke,
    Windows.UI.Color Text,
    Windows.UI.Color Body,
    Windows.UI.Color Muted,
    Windows.UI.Color Disabled,
    Windows.UI.Color Primary,
    Windows.UI.Color PrimaryContrast,
    Windows.UI.Color NavActiveBackground,
    Windows.UI.Color ButtonBackground,
    Windows.UI.Color SuccessSoft,
    Windows.UI.Color SuccessStroke,
    Windows.UI.Color InfoSoft,
    Windows.UI.Color InfoStroke,
    Windows.UI.Color WarningSoft,
    Windows.UI.Color WarningStroke,
    Windows.UI.Color ErrorSoft,
    Windows.UI.Color ErrorStroke,
    Windows.UI.Color PurpleSoft,
    Windows.UI.Color OrangeSoft);

internal static class AppAppearance
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly ConditionalWeakTable<DependencyObject, DensityBaseline> DensityBaselines = new();

    private static readonly Windows.UI.Color[] PageRoleColors =
    {
        Rgb(0xF6, 0xF8, 0xFA),
        Rgb(0x0F, 0x14, 0x19)
    };

    private static readonly Windows.UI.Color[] SubtleRoleColors =
    {
        Rgb(0xF8, 0xFA, 0xFC),
        Rgb(0x14, 0x1C, 0x24)
    };

    private static readonly Windows.UI.Color[] SidebarRoleColors =
    {
        Rgb(0xF1, 0xF5, 0xF8),
        Rgb(0x12, 0x19, 0x21)
    };

    private static readonly Windows.UI.Color[] PanelRoleColors =
    {
        Rgb(0xFF, 0xFF, 0xFF),
        Rgb(0x18, 0x20, 0x29),
        Rgb(0x20, 0x2A, 0x35)
    };

    private static readonly Windows.UI.Color[] StrokeRoleColors =
    {
        Rgb(0xD8, 0xDE, 0xE4),
        Rgb(0xD4, 0xDC, 0xE3),
        Rgb(0xE5, 0xE7, 0xEB),
        Rgb(0xE7, 0xEB, 0xEF),
        Rgb(0xCB, 0xD5, 0xDE),
        Rgb(0x34, 0x40, 0x4C),
        Rgb(0x28, 0x32, 0x3D)
    };

    private static readonly Windows.UI.Color[] TextRoleColors =
    {
        Rgb(0x11, 0x18, 0x27),
        Rgb(0x0F, 0x17, 0x2A),
        Rgb(0xF3, 0xF7, 0xFA)
    };

    private static readonly Windows.UI.Color[] BodyRoleColors =
    {
        Rgb(0x30, 0x36, 0x3D),
        Rgb(0x20, 0x25, 0x2B),
        Rgb(0xD6, 0xE0, 0xE8)
    };

    private static readonly Windows.UI.Color[] MutedRoleColors =
    {
        Rgb(0x5B, 0x65, 0x70),
        Rgb(0x6B, 0x72, 0x80),
        Rgb(0x66, 0x71, 0x7D),
        Rgb(0x60, 0x60, 0x60),
        Rgb(0xA3, 0xAA, 0xB2),
        Rgb(0xA5, 0xB1, 0xBC),
        Rgb(0x6F, 0x7B, 0x86)
    };

    private static readonly Windows.UI.Color[] NavActiveRoleColors =
    {
        Rgb(0xE6, 0xEB, 0xEF),
        Rgb(0x1E, 0x34, 0x3C)
    };

    private static readonly Windows.UI.Color[] SuccessSoftRoleColors =
    {
        Rgb(0xF1, 0xFA, 0xEC),
        Rgb(0xF2, 0xFF, 0xF0),
        Rgb(0x10, 0x28, 0x18)
    };

    private static readonly Windows.UI.Color[] InfoSoftRoleColors =
    {
        Rgb(0xEA, 0xF4, 0xFF),
        Rgb(0xEE, 0xF3, 0xFD),
        Rgb(0xEA, 0xF6, 0xFF),
        Rgb(0x13, 0x25, 0x3A)
    };

    private static readonly Windows.UI.Color[] WarningSoftRoleColors =
    {
        Rgb(0xFF, 0xF8, 0xEA),
        Rgb(0xFF, 0xF8, 0xED),
        Rgb(0xFF, 0xF4, 0xE5),
        Rgb(0x2D, 0x24, 0x12)
    };

    private static readonly Windows.UI.Color[] ErrorSoftRoleColors =
    {
        Rgb(0xFD, 0xF2, 0xF2),
        Rgb(0x30, 0x19, 0x1B)
    };

    public static ElementTheme ResolveElementTheme(AppThemeMode mode)
    {
        return mode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => IsSystemLightTheme() ? ElementTheme.Light : ElementTheme.Dark
        };
    }

    public static AppAppearancePalette GetPalette(ElementTheme theme)
    {
        return theme == ElementTheme.Dark ? DarkPalette : LightPalette;
    }

    public static void ApplyPageResources(ResourceDictionary resources, AppAppearancePalette palette)
    {
        SetBrush(resources, "SettingsPageBackgroundBrush", palette.PageBackground);
        SetBrush(resources, "SettingsPanelBrush", palette.PanelBackground);
        SetBrush(resources, "SettingsStrokeBrush", palette.Stroke);
        SetBrush(resources, "SettingsTextBrush", palette.Text);
        SetBrush(resources, "SettingsMutedBrush", palette.Muted);
        SetBrush(resources, "SettingsPrimaryBrush", palette.Primary);

        SetBrush(resources, "SideDockPageBackgroundBrush", palette.PageBackground);
        SetBrush(resources, "SideDockPanelBackgroundBrush", palette.PanelBackground);
        SetBrush(resources, "SideDockStrokeBrush", palette.Stroke);
        SetBrush(resources, "SideDockTextBrush", palette.Text);
        SetBrush(resources, "SideDockMutedTextBrush", palette.Muted);
        SetBrush(resources, "SideDockPrimaryBrush", palette.Primary);

        SetBrush(resources, "AudioPageBackgroundBrush", palette.PageBackground);
        SetBrush(resources, "AudioPanelBrush", palette.PanelBackground);
        SetBrush(resources, "AudioStrokeBrush", palette.Stroke);
        SetBrush(resources, "AudioTextBrush", palette.Text);
        SetBrush(resources, "AudioMutedBrush", palette.Muted);
        SetBrush(resources, "AudioPrimaryBrush", palette.Primary);
        SetBrush(resources, "AudioSoftWarningBrush", palette.WarningSoft);

        SetBrush(resources, "DiagPageBackgroundBrush", palette.PageBackground);
        SetBrush(resources, "DiagCardBackgroundBrush", palette.PanelBackground);
        SetBrush(resources, "DiagStrokeBrush", palette.Stroke);
        SetBrush(resources, "DiagSoftStrokeBrush", palette.SoftStroke);
        SetBrush(resources, "DiagTextBrush", palette.Text);
        SetBrush(resources, "DiagBodyBrush", palette.Body);
        SetBrush(resources, "DiagMutedBrush", palette.Muted);
        SetBrush(resources, "DiagPrimaryBrush", palette.Primary);
        SetBrush(resources, "DiagGreenSoftBrush", palette.SuccessSoft);
        SetBrush(resources, "DiagBlueSoftBrush", palette.InfoSoft);
        SetBrush(resources, "DiagPurpleSoftBrush", palette.PurpleSoft);
        SetBrush(resources, "DiagOrangeSoftBrush", palette.OrangeSoft);
    }

    public static void ApplyPalette(DependencyObject? root, AppAppearancePalette palette)
    {
        if (root is null)
        {
            return;
        }

        ApplyPaletteToObject(root, palette);

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            ApplyPalette(VisualTreeHelper.GetChild(root, index), palette);
        }
    }

    public static void ApplyDensity(DependencyObject? root, AppInterfaceDensity density)
    {
        if (root is null)
        {
            return;
        }

        ApplyDensityToObject(root, density);

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            ApplyDensity(VisualTreeHelper.GetChild(root, index), density);
        }
    }

    public static void SetBrushColor(Brush brush, Windows.UI.Color color)
    {
        if (brush is SolidColorBrush solidColorBrush)
        {
            solidColorBrush.Color = color;
        }
    }

    public static SolidColorBrush Brush(Windows.UI.Color color)
    {
        return new SolidColorBrush(color);
    }

    private static AppAppearancePalette LightPalette { get; } = new(
        ElementTheme.Light,
        Rgb(0xF6, 0xF8, 0xFA),
        Rgb(0xFF, 0xFF, 0xFF),
        Rgb(0xF1, 0xF5, 0xF8),
        Rgb(0xF8, 0xFA, 0xFC),
        Rgb(0xD8, 0xDE, 0xE4),
        Rgb(0xE5, 0xE7, 0xEB),
        Rgb(0x11, 0x18, 0x27),
        Rgb(0x30, 0x36, 0x3D),
        Rgb(0x5B, 0x65, 0x70),
        Rgb(0xA3, 0xAA, 0xB2),
        Rgb(0x08, 0x7C, 0x89),
        Rgb(0xFF, 0xFF, 0xFF),
        Rgb(0xE6, 0xEB, 0xEF),
        Rgb(0xFF, 0xFF, 0xFF),
        Rgb(0xF1, 0xFA, 0xEC),
        Rgb(0x62, 0xB3, 0x60),
        Rgb(0xEA, 0xF4, 0xFF),
        Rgb(0x7B, 0xAE, 0xED),
        Rgb(0xFF, 0xF8, 0xEA),
        Rgb(0xE9, 0xA2, 0x3B),
        Rgb(0xFD, 0xF2, 0xF2),
        Rgb(0xF8, 0x71, 0x71),
        Rgb(0xF0, 0xEE, 0xFF),
        Rgb(0xFF, 0xF4, 0xE5));

    private static AppAppearancePalette DarkPalette { get; } = new(
        ElementTheme.Dark,
        Rgb(0x0F, 0x14, 0x19),
        Rgb(0x18, 0x20, 0x29),
        Rgb(0x12, 0x19, 0x21),
        Rgb(0x14, 0x1C, 0x24),
        Rgb(0x34, 0x40, 0x4C),
        Rgb(0x28, 0x32, 0x3D),
        Rgb(0xF3, 0xF7, 0xFA),
        Rgb(0xD6, 0xE0, 0xE8),
        Rgb(0xA5, 0xB1, 0xBC),
        Rgb(0x6F, 0x7B, 0x86),
        Rgb(0x21, 0xB6, 0xC7),
        Rgb(0x06, 0x10, 0x14),
        Rgb(0x1E, 0x34, 0x3C),
        Rgb(0x20, 0x2A, 0x35),
        Rgb(0x10, 0x28, 0x18),
        Rgb(0x3C, 0x8D, 0x45),
        Rgb(0x13, 0x25, 0x3A),
        Rgb(0x2E, 0x6D, 0xB8),
        Rgb(0x2D, 0x24, 0x12),
        Rgb(0x9E, 0x6C, 0x21),
        Rgb(0x30, 0x19, 0x1B),
        Rgb(0xA5, 0x48, 0x48),
        Rgb(0x21, 0x1C, 0x35),
        Rgb(0x2B, 0x22, 0x14));

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            var value = key?.GetValue("AppsUseLightTheme");
            return value is null || Convert.ToInt32(value, CultureInfo.InvariantCulture) != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void SetBrush(ResourceDictionary resources, string key, Windows.UI.Color color)
    {
        if (resources.TryGetValue(key, out var resource) && resource is SolidColorBrush brush)
        {
            brush.Color = color;
            return;
        }

        resources[key] = new SolidColorBrush(color);
    }

    private static void ApplyPaletteToObject(DependencyObject value, AppAppearancePalette palette)
    {
        if (value is FrameworkElement frameworkElement)
        {
            frameworkElement.RequestedTheme = palette.Theme;
        }

        if (value is Grid grid)
        {
            grid.Background = MapBrush(grid.Background, palette);
        }

        if (value is Border border)
        {
            border.Background = MapBrush(border.Background, palette);
            border.BorderBrush = MapBrush(border.BorderBrush, palette);
        }

        if (value is Control control)
        {
            control.Background = MapBrush(control.Background, palette);
            control.BorderBrush = MapBrush(control.BorderBrush, palette);
            control.Foreground = MapBrush(control.Foreground, palette);
        }

        if (value is TextBlock textBlock)
        {
            textBlock.Foreground = MapBrush(textBlock.Foreground, palette);
        }

        if (value is FontIcon fontIcon)
        {
            fontIcon.Foreground = MapBrush(fontIcon.Foreground, palette);
        }

        if (value is Shape shape)
        {
            shape.Fill = MapBrush(shape.Fill, palette);
            shape.Stroke = MapBrush(shape.Stroke, palette);
        }
    }

    private static Brush MapBrush(Brush brush, AppAppearancePalette palette)
    {
        if (brush is not SolidColorBrush solidColorBrush
            || solidColorBrush.Color.A == 0
            || !TryMapColor(solidColorBrush.Color, palette, out var mappedColor))
        {
            return brush;
        }

        return new SolidColorBrush(mappedColor);
    }

    private static bool TryMapColor(
        Windows.UI.Color color,
        AppAppearancePalette palette,
        out Windows.UI.Color mappedColor)
    {
        if (IsAny(color, PageRoleColors))
        {
            mappedColor = palette.PageBackground;
            return true;
        }

        if (IsAny(color, SubtleRoleColors))
        {
            mappedColor = palette.SubtleBackground;
            return true;
        }

        if (IsAny(color, SidebarRoleColors))
        {
            mappedColor = palette.SidebarBackground;
            return true;
        }

        if (IsAny(color, PanelRoleColors))
        {
            mappedColor = palette.PanelBackground;
            return true;
        }

        if (IsAny(color, StrokeRoleColors))
        {
            mappedColor = palette.Stroke;
            return true;
        }

        if (IsAny(color, TextRoleColors))
        {
            mappedColor = palette.Text;
            return true;
        }

        if (IsAny(color, BodyRoleColors))
        {
            mappedColor = palette.Body;
            return true;
        }

        if (IsAny(color, MutedRoleColors))
        {
            mappedColor = palette.Muted;
            return true;
        }

        if (IsAny(color, NavActiveRoleColors))
        {
            mappedColor = palette.NavActiveBackground;
            return true;
        }

        if (IsAny(color, SuccessSoftRoleColors))
        {
            mappedColor = palette.SuccessSoft;
            return true;
        }

        if (IsAny(color, InfoSoftRoleColors))
        {
            mappedColor = palette.InfoSoft;
            return true;
        }

        if (IsAny(color, WarningSoftRoleColors))
        {
            mappedColor = palette.WarningSoft;
            return true;
        }

        if (IsAny(color, ErrorSoftRoleColors))
        {
            mappedColor = palette.ErrorSoft;
            return true;
        }

        mappedColor = color;
        return false;
    }

    private static void ApplyDensityToObject(DependencyObject value, AppInterfaceDensity density)
    {
        var baseline = DensityBaselines.GetValue(value, CreateDensityBaseline);
        if (value is FrameworkElement frameworkElement)
        {
            ApplyFrameworkElementDensity(frameworkElement, baseline, density);
        }

        if (value is Border border)
        {
            border.Padding = ApplyDensityThickness(baseline.BorderPadding, density);
        }

        if (value is Control control)
        {
            control.Padding = ApplyDensityThickness(baseline.ControlPadding, density);
        }

        if (value is ScrollViewer scrollViewer)
        {
            scrollViewer.Padding = ApplyDensityThickness(baseline.ScrollViewerPadding, density);
        }

        if (value is StackPanel stackPanel)
        {
            stackPanel.Spacing = ApplyDensityLength(baseline.StackPanelSpacing, density, isSpacing: true);
        }

        if (value is Grid grid)
        {
            grid.RowSpacing = ApplyDensityLength(baseline.GridRowSpacing, density, isSpacing: true);
            grid.ColumnSpacing = ApplyDensityLength(baseline.GridColumnSpacing, density, isSpacing: true);
        }
    }

    private static void ApplyFrameworkElementDensity(
        FrameworkElement element,
        DensityBaseline baseline,
        AppInterfaceDensity density)
    {
        if (!double.IsNaN(baseline.Height) && baseline.Height is >= 32 and <= 96 && !IsSmallSquare(element, baseline.Height))
        {
            element.Height = ApplyDensityLength(baseline.Height, density, isSpacing: false);
        }

        if (baseline.MinHeight is >= 40 and <= 96)
        {
            element.MinHeight = ApplyDensityLength(baseline.MinHeight, density, isSpacing: false);
        }
    }

    private static bool IsSmallSquare(FrameworkElement element, double height)
    {
        return !double.IsNaN(element.Width) && Math.Abs(element.Width - height) < 0.1 && height <= 60;
    }

    private static Thickness ApplyDensityThickness(Thickness value, AppInterfaceDensity density)
    {
        if (density == AppInterfaceDensity.Standard)
        {
            return value;
        }

        return new Thickness(
            ApplyDensityLength(value.Left, density, isSpacing: true),
            ApplyDensityLength(value.Top, density, isSpacing: true),
            ApplyDensityLength(value.Right, density, isSpacing: true),
            ApplyDensityLength(value.Bottom, density, isSpacing: true));
    }

    private static double ApplyDensityLength(double value, AppInterfaceDensity density, bool isSpacing)
    {
        if (density == AppInterfaceDensity.Standard || value <= 4)
        {
            return value;
        }

        if (isSpacing)
        {
            return value switch
            {
                <= 8 => Math.Max(4, value - 2),
                <= 14 => value - 3,
                <= 20 => value - 4,
                <= 28 => value - 6,
                _ => Math.Round(value * 0.78)
            };
        }

        return value switch
        {
            <= 36 => Math.Max(32, value - 2),
            <= 44 => value - 6,
            <= 60 => value - 8,
            <= 80 => value - 10,
            _ => Math.Round(value * 0.88)
        };
    }

    private static DensityBaseline CreateDensityBaseline(DependencyObject value)
    {
        return new DensityBaseline(value);
    }

    private static bool IsAny(Windows.UI.Color color, IReadOnlyList<Windows.UI.Color> candidates)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (AreSameColor(color, candidates[index]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AreSameColor(Windows.UI.Color left, Windows.UI.Color right)
    {
        return left.A == right.A && left.R == right.R && left.G == right.G && left.B == right.B;
    }

    private static Windows.UI.Color Rgb(byte red, byte green, byte blue)
    {
        return ColorHelper.FromArgb(255, red, green, blue);
    }

    private sealed class DensityBaseline
    {
        public DensityBaseline(DependencyObject value)
        {
            if (value is FrameworkElement frameworkElement)
            {
                Height = frameworkElement.Height;
                MinHeight = frameworkElement.MinHeight;
            }

            if (value is Border border)
            {
                BorderPadding = border.Padding;
            }

            if (value is Control control)
            {
                ControlPadding = control.Padding;
            }

            if (value is ScrollViewer scrollViewer)
            {
                ScrollViewerPadding = scrollViewer.Padding;
            }

            if (value is StackPanel stackPanel)
            {
                StackPanelSpacing = stackPanel.Spacing;
            }

            if (value is Grid grid)
            {
                GridRowSpacing = grid.RowSpacing;
                GridColumnSpacing = grid.ColumnSpacing;
            }
        }

        public double Height { get; } = double.NaN;

        public double MinHeight { get; }

        public Thickness BorderPadding { get; }

        public Thickness ControlPadding { get; }

        public Thickness ScrollViewerPadding { get; }

        public double StackPanelSpacing { get; }

        public double GridRowSpacing { get; }

        public double GridColumnSpacing { get; }
    }
}
