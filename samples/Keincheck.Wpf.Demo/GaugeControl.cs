using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using System.Windows.Media;

namespace Keincheck.Wpf.Demo;

/// <summary>
/// A small custom-drawn WPF control — the analog of the Avalonia demo's
/// <c>VertexCanvas</c>. It paints a circular gauge by overriding <see cref="OnRender"/>
/// and advances the needle with raw pointer clicks. It deliberately exposes <b>no</b>
/// UI-Automation peer (see <see cref="OnCreateAutomationPeer"/>), so it is the intended
/// target for the synthetic-input fallback path: UI-Automation tools cannot drive it, and
/// a tool must fall back to synthesised pointer input.
/// <para>
/// It also surfaces a couple of plain styled/CLR properties (<see cref="Value"/>,
/// <see cref="LastClickAngle"/>) so the property and wait-for tools have a custom-control
/// target to read and write.
/// </para>
/// </summary>
public sealed class GaugeControl : FrameworkElement
{
    /// <summary>Current gauge value in [0,1]. Styled so tools can set it; re-renders on change.</summary>
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(GaugeControl),
            new FrameworkPropertyMetadata(0.25, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Angle (degrees) of the needle after the most recent click, or -1 if none yet.
    /// A direct, readable side-effect a tool can assert against after a synthetic click.
    /// </summary>
    public static readonly DependencyProperty LastClickAngleProperty =
        DependencyProperty.Register(
            nameof(LastClickAngle), typeof(double), typeof(GaugeControl),
            new FrameworkPropertyMetadata(-1.0));

    private static readonly Brush Backdrop = new SolidColorBrush(Color.FromRgb(0x22, 0x26, 0x33));
    private static readonly Brush DialFill = new SolidColorBrush(Color.FromRgb(0x2D, 0x33, 0x44));
    private static readonly Pen DialPen = new(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)), 1.5);
    private static readonly Pen NeedlePen = new(new SolidColorBrush(Color.FromRgb(0x3D, 0xA9, 0xFC)), 3.0);
    private static readonly Brush HubFill = new SolidColorBrush(Color.FromRgb(0xFF, 0xC1, 0x07));

    static GaugeControl()
    {
        Backdrop.Freeze();
        DialFill.Freeze();
        DialPen.Freeze();
        NeedlePen.Freeze();
        HubFill.Freeze();
    }

    public GaugeControl()
    {
        Focusable = true;
    }

    /// <inheritdoc cref="ValueProperty"/>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <inheritdoc cref="LastClickAngleProperty"/>
    public double LastClickAngle
    {
        get => (double)GetValue(LastClickAngleProperty);
        private set => SetValue(LastClickAngleProperty, value);
    }

    /// <summary>
    /// Return <c>null</c> so this control has no UI-Automation peer. This is the whole
    /// point of the control: it makes <c>automation_action</c> report "no automation peer"
    /// and forces the synthetic-input fallback to drive it. The base signature is
    /// non-nullable; <c>null!</c> is the established idiom for "this element exposes no peer".
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => null!;

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);

        var bounds = new Rect(RenderSize);
        context.DrawRectangle(Backdrop, null, bounds);

        var center = new Point(bounds.Width / 2, bounds.Height / 2);
        var radius = Math.Max(4, Math.Min(bounds.Width, bounds.Height) / 2 - 8);

        // Dial face.
        context.DrawEllipse(DialFill, DialPen, center, radius, radius);

        // Needle: sweep 270 degrees (135° .. 405°) across [0,1].
        var clamped = Math.Clamp(Value, 0.0, 1.0);
        var angle = (135 + clamped * 270) * Math.PI / 180.0;
        var tip = new Point(
            center.X + Math.Cos(angle) * (radius - 6),
            center.Y + Math.Sin(angle) * (radius - 6));
        context.DrawLine(NeedlePen, center, tip);

        // Hub.
        context.DrawEllipse(HubFill, null, center, 4, 4);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);

        // Advance the gauge a notch and record the resulting needle angle so a synthetic
        // click produces an observable, readable side effect.
        Value = Value >= 1.0 ? 0.0 : Math.Round(Value + 0.1, 2);
        LastClickAngle = 135 + Math.Clamp(Value, 0.0, 1.0) * 270;
        Focus();
        e.Handled = true;
    }
}
