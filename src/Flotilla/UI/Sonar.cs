using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Flotilla.UI;

public sealed class Sonar : Canvas
{
    const int Rings = 4;
    const int Trail = 16;
    const double TrailStep = 3.5;
    const int FrameRate = 30;

    static readonly TimeSpan Period = TimeSpan.FromSeconds(6);

    static readonly (double Bearing, double Range)[] Contacts =
    [
        (18, 0.70), (64, 0.45), (118, 0.82), (163, 0.60),
        (201, 0.85), (238, 0.35), (284, 0.74), (332, 0.55),
    ];

    readonly RotateTransform sweep = new();
    readonly List<(UIElement Blip, double Bearing)> blips = [];

    public Sonar()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => Draw();
        IsVisibleChanged += (_, _) => Animate();
    }

    public bool ShowContacts { get; set; } = true;

    void Draw()
    {
        Children.Clear();
        blips.Clear();

        var radius = Math.Min(ActualWidth, ActualHeight) / 2;
        if (radius <= 0) return;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var ink = (Brush)FindResource("Accent");

        for (var i = 1; i <= Rings; i++)
        {
            var r = radius * i / Rings;
            var ring = new Ellipse { Width = r * 2, Height = r * 2, Stroke = ink, StrokeThickness = 1, Opacity = 0.16 };
            SetLeft(ring, center.X - r);
            SetTop(ring, center.Y - r);
            Children.Add(ring);
        }
        Children.Add(new Line { X1 = center.X - radius, X2 = center.X + radius, Y1 = center.Y, Y2 = center.Y, Stroke = ink, Opacity = 0.08 });
        Children.Add(new Line { X1 = center.X, X2 = center.X, Y1 = center.Y - radius, Y2 = center.Y + radius, Stroke = ink, Opacity = 0.08 });

        sweep.CenterX = center.X;
        sweep.CenterY = center.Y;
        var arm = new Canvas { RenderTransform = sweep };
        for (var i = 0; i < Trail; i++)
        {
            arm.Children.Add(new Path
            {
                Data = Slice(center, radius, -(i + 1) * TrailStep, -i * TrailStep),
                Fill = ink,
                Opacity = 0.18 * (1 - (double)i / Trail),
            });
        }
        arm.Children.Add(new Line { X1 = center.X, Y1 = center.Y, X2 = center.X + radius, Y2 = center.Y, Stroke = ink, StrokeThickness = 1.5, Opacity = 0.6 });
        Children.Add(arm);

        if (ShowContacts)
        {
            foreach (var (bearing, range) in Contacts)
            {
                var angle = bearing * Math.PI / 180;
                var blip = Blip(ink);
                SetLeft(blip, center.X + Math.Cos(angle) * range * radius - blip.Width / 2);
                SetTop(blip, center.Y + Math.Sin(angle) * range * radius - blip.Height / 2);
                Children.Add(blip);
                blips.Add((blip, bearing));
            }
        }

        Animate();
    }

    void Animate()
    {
        var moving = IsVisible && SystemParameters.ClientAreaAnimation;

        if (moving)
        {
            var turn = new DoubleAnimation(0, 360, Period) { RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(turn, FrameRate);
            sweep.BeginAnimation(RotateTransform.AngleProperty, turn);
        }
        else
        {
            sweep.BeginAnimation(RotateTransform.AngleProperty, null);
            sweep.Angle = 300;
        }

        foreach (var (blip, bearing) in blips)
        {
            blip.Opacity = moving ? 0 : 0.7;
            blip.BeginAnimation(OpacityProperty, moving ? Echo(bearing) : null);
        }
    }

    static AnimationTimeline Echo(double bearing)
    {
        var echo = new DoubleAnimationUsingKeyFrames
        {
            Duration = Period,
            BeginTime = Period * (bearing / 360),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        echo.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)));
        echo.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromPercent(0.8)));
        echo.KeyFrames.Add(new LinearDoubleKeyFrame(0.1, KeyTime.FromPercent(1)));
        Timeline.SetDesiredFrameRate(echo, FrameRate);
        return echo;
    }

    static FrameworkElement Blip(Brush ink) => new Grid
    {
        Width = 16,
        Height = 16,
        Children =
        {
            new Ellipse { Fill = ink, Opacity = 0.22 },
            new Ellipse { Width = 6, Height = 6, Fill = ink },
        },
    };

    static Geometry Slice(Point center, double radius, double from, double to)
    {
        Point At(double degrees) => new(
            center.X + radius * Math.Cos(degrees * Math.PI / 180),
            center.Y + radius * Math.Sin(degrees * Math.PI / 180));

        var figure = new PathFigure(center,
        [
            new LineSegment(At(from), false),
            new ArcSegment(At(to), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false),
        ], closed: true);

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
