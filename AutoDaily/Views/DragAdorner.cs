using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace AutoDaily.Views;

/// <summary>拖曳時跟著滑鼠移動的卡片影像。</summary>
internal sealed class DragAdorner : Adorner
{
    private readonly FrameworkElement _visual;
    private double _x;
    private double _y;

    public DragAdorner(UIElement adornedElement, ImageSource snapshot, Size size) : base(adornedElement)
    {
        IsHitTestVisible = false;
        _visual = new Border
        {
            Child = new Image { Source = snapshot, Width = size.Width, Height = size.Height, Stretch = Stretch.None },
            Opacity = 0.96,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1.02, 1.02),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        AddVisualChild(_visual);
    }

    public void SetPosition(double x, double y)
    {
        _x = x;
        _y = y;
        (Parent as AdornerLayer)?.Update(AdornedElement);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _visual;

    protected override Size MeasureOverride(Size constraint)
    {
        _visual.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return _visual.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _visual.Arrange(new Rect(_visual.DesiredSize));
        return finalSize;
    }

    public override GeneralTransform GetDesiredTransform(GeneralTransform transform)
    {
        var group = new GeneralTransformGroup();
        group.Children.Add(base.GetDesiredTransform(transform));
        group.Children.Add(new TranslateTransform(_x, _y));
        return group;
    }

    /// <summary>把元素畫成點陣圖（用 VisualBrush 避免座標偏移問題）。</summary>
    public static ImageSource Snapshot(FrameworkElement element)
    {
        var dpi = VisualTreeHelper.GetDpi(element);
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth * dpi.DpiScaleX));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var brush = new VisualBrush(element)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
