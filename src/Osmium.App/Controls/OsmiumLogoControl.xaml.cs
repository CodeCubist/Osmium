using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Osmium.App.Controls;

public sealed partial class OsmiumLogoControl : UserControl
{
    public static readonly DependencyProperty AccentBrushProperty =
        DependencyProperty.Register(
            nameof(AccentBrush),
            typeof(Brush),
            typeof(OsmiumLogoControl),
            new PropertyMetadata(new SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 205, 255))));

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public OsmiumLogoControl()
    {
        InitializeComponent();
    }
}