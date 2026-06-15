namespace Keincheck.Core;

/// <summary>
/// A framework-neutral axis-aligned rectangle in device-independent pixels. Mirrors
/// the shape of Avalonia's <c>Rect</c> / WPF's <c>Rect</c> without depending on
/// either toolkit. The adapter converts the framework's own rectangle type to/from
/// this neutral form at the seam.
/// </summary>
public readonly record struct UiRect(double X, double Y, double Width, double Height)
{
    /// <summary>The right edge (<see cref="X"/> + <see cref="Width"/>).</summary>
    public double Right => X + Width;

    /// <summary>The bottom edge (<see cref="Y"/> + <see cref="Height"/>).</summary>
    public double Bottom => Y + Height;

    /// <summary>An empty rectangle at the origin.</summary>
    public static UiRect Empty => default;
}

/// <summary>
/// A framework-neutral 2-D point in device-independent pixels. Mirrors the shape of
/// Avalonia's <c>Point</c> / WPF's <c>Point</c>.
/// </summary>
public readonly record struct UiPoint(double X, double Y);

/// <summary>
/// A framework-neutral 2-D vector (used for pointer-wheel deltas). Mirrors the shape
/// of Avalonia's <c>Vector</c> / WPF's <c>Vector</c>.
/// </summary>
public readonly record struct UiVector(double X, double Y);
