using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace Osmium.App.Helpers;

public sealed class HeroCardShadowController
{
    private UIElement? _host;
    private ContainerVisual? _rootVisual;
    private CompositionRoundedRectangleGeometry? _maskGeometry;
    private ShapeVisual? _maskVisual;
    private CompositionVisualSurface? _maskSurface;
    private SpriteVisual? _ambientVisual;
    private DropShadow? _ambientShadow;
    private SpriteVisual? _contactVisual;
    private DropShadow? _contactShadow;

    public void Update(UIElement? host, double width, double height, float cornerRadius, bool isDark)
    {
        if (host == null || width <= 0 || height <= 0)
        {
            return;
        }

        try
        {
            var size = new Vector2((float)width, (float)height);
            var radius = new Vector2(cornerRadius, cornerRadius);

            if (!ReferenceEquals(_host, host) || _rootVisual == null)
            {
                Visual hostVisual = ElementCompositionPreview.GetElementVisual(host);
                Compositor compositor = hostVisual.Compositor;

                _maskGeometry = compositor.CreateRoundedRectangleGeometry();
                _maskGeometry.Size = size;
                _maskGeometry.CornerRadius = radius;

                CompositionSpriteShape maskShape = compositor.CreateSpriteShape(_maskGeometry);
                maskShape.FillBrush = compositor.CreateColorBrush(Colors.White);

                _maskVisual = compositor.CreateShapeVisual();
                _maskVisual.Size = size;
                _maskVisual.Shapes.Add(maskShape);

                _maskSurface = compositor.CreateVisualSurface();
                _maskSurface.SourceVisual = _maskVisual;
                _maskSurface.SourceSize = size;

                CompositionSurfaceBrush maskBrush = compositor.CreateSurfaceBrush(_maskSurface);
                maskBrush.Stretch = CompositionStretch.Fill;

                _ambientShadow = compositor.CreateDropShadow();
                _ambientShadow.Color = Colors.Black;
                _ambientShadow.BlurRadius = 18f;
                _ambientShadow.Offset = new Vector3(0f, 4f, 0f);
                _ambientShadow.Mask = maskBrush;

                _ambientVisual = compositor.CreateSpriteVisual();
                _ambientVisual.Size = size;
                _ambientVisual.Shadow = _ambientShadow;

                _contactShadow = compositor.CreateDropShadow();
                _contactShadow.Color = Colors.Black;
                _contactShadow.BlurRadius = 4f;
                _contactShadow.Offset = new Vector3(0f, 1f, 0f);
                _contactShadow.Mask = maskBrush;

                _contactVisual = compositor.CreateSpriteVisual();
                _contactVisual.Size = size;
                _contactVisual.Shadow = _contactShadow;

                _rootVisual = compositor.CreateContainerVisual();
                _rootVisual.Size = size;
                _rootVisual.Children.InsertAtBottom(_ambientVisual);
                _rootVisual.Children.InsertAtTop(_contactVisual);

                ElementCompositionPreview.SetElementChildVisual(host, _rootVisual);
                _host = host;
            }
            else
            {
                _maskGeometry!.Size = size;
                _maskGeometry.CornerRadius = radius;
                _maskVisual!.Size = size;
                _maskSurface!.SourceSize = size;
                _ambientVisual!.Size = size;
                _contactVisual!.Size = size;
                _rootVisual.Size = size;
            }

            if (_ambientShadow != null)
            {
                _ambientShadow.Opacity = isDark ? 0.22f : 0.085f;
            }

            if (_contactShadow != null && _contactVisual != null)
            {
                _contactVisual.IsVisible = !isDark;
                _contactShadow.Opacity = 0.045f;
            }
        }
        catch
        {
        }
    }
}