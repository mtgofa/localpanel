using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace LocalPanel.UI;

/// <summary>
/// Swaps a button's text for a spinning ring while keeping the button's size.
/// Dispose() restores the original content.
/// </summary>
public static class ButtonSpinner
{
    public static IDisposable Start(Button btn)
    {
        var oldContent = btn.Content;
        if (btn.ActualWidth > 0) btn.MinWidth = btn.ActualWidth;
        if (btn.ActualHeight > 0) btn.MinHeight = btn.ActualHeight;
        btn.IsEnabled = false;

        var ring = new Ellipse
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 2.6, 1.8 },
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(),
        };
        ((RotateTransform)ring.RenderTransform).BeginAnimation(
            RotateTransform.AngleProperty,
            new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9))
            {
                RepeatBehavior = RepeatBehavior.Forever
            });
        btn.Content = ring;

        return new Restorer(btn, oldContent);
    }

    private sealed class Restorer(Button btn, object oldContent) : IDisposable
    {
        public void Dispose()
        {
            btn.Content = oldContent;
            // Clear instead of setting true so IsEnabled bindings keep working.
            btn.ClearValue(UIElement.IsEnabledProperty);
        }
    }
}
