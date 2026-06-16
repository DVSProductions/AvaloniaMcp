using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// Properties group of <see cref="WpfUiAdapter"/>: enumerating registered
/// styled/attached property names, reading a property by name as a JSON-friendly value,
/// coercing + writing a property from a <see cref="JsonElement"/>, and the data context.
/// <para>
/// <b>Stage B map (System.Windows.*):</b>
/// <list type="bullet">
///   <item><see cref="GetPropertyNames"/> → the <c>DependencyProperty</c> set surfaced by
///     <c>DependencyPropertyDescriptor</c> over <c>TypeDescriptor.GetProperties</c> (the
///     WPF analog of Avalonia's <c>AvaloniaPropertyRegistry.GetRegistered</c>).</item>
///   <item><see cref="TryReadProperty"/> → prefer the <c>DependencyProperty</c> by name
///     (<c>GetValue</c>), then CLR reflection; project via the shared
///     <c>PropertyValueSerializer.ToJsonFriendly</c> with WPF hooks (element →
///     <c>"Type#Name"</c>, WPF value-structs such as <c>Thickness</c>/<c>Color</c>/
///     <c>Rect</c>/<c>CornerRadius</c>/<c>GridLength</c> → invariant string form;
///     reference leaves such as <c>Brush</c> fall through to <c>ToString()</c>).</item>
///   <item><see cref="TryWriteProperty"/> → <c>PropertyValueSerializer.TryCoerce</c>
///     (WPF <c>TypeConverter</c> / static <c>Parse</c>) then the CLR setter.</item>
///   <item><see cref="GetDataContext"/> → <c>(FrameworkElement).DataContext</c>.</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// The property gate mirrors the Avalonia adapter: reads/writes require a
/// <see cref="DependencyObject"/> handle (the WPF analog of an Avalonia <c>Control</c>),
/// styled-by-name access is preferred over CLR reflection so the values match the
/// pre-refactor serializer, and the projection is delegated to the shared
/// <see cref="PropertyValueSerializer"/> exactly as <c>AvaloniaUiAdapter.Project</c> does.
/// </remarks>
public sealed partial class WpfUiAdapter
{
    /// <inheritdoc />
    public IEnumerable<string> GetPropertyNames(object element)
    {
        if (element is not DependencyObject dep)
            yield break;

        // The WPF analog of Avalonia's "all registered styled/attached properties for
        // this control": every PropertyDescriptor on the instance that is backed by a
        // DependencyProperty. May repeat across attached owners; Core de-duplicates by
        // name (matching the IUiAdapter contract).
        foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(dep))
        {
            if (DependencyPropertyDescriptor.FromProperty(pd) is not null)
                yield return pd.Name;
        }
    }

    /// <inheritdoc />
    public bool TryReadProperty(object element, string name, out object? jsonFriendlyValue)
    {
        jsonFriendlyValue = null;
        if (element is not DependencyObject dep || string.IsNullOrEmpty(name))
            return false;

        // Prefer the styled/attached DependencyProperty of that name (more values are
        // reachable that way), then fall back to a CLR property read — mirroring the
        // Avalonia adapter's two read paths so values are identical across toolkits.
        if (TryFindDependencyProperty(dep, name) is { } dp)
        {
            try
            {
                jsonFriendlyValue = Project(dep.GetValue(dp));
                return true;
            }
            catch
            {
                jsonFriendlyValue = null;
                return false;
            }
        }

        var clr = FindClrProperty(dep.GetType(), name);
        if (clr is null || !clr.CanRead || clr.GetIndexParameters().Length > 0)
            return false;

        try
        {
            jsonFriendlyValue = Project(clr.GetValue(dep));
            return true;
        }
        catch
        {
            jsonFriendlyValue = null;
            return false;
        }
    }

    /// <inheritdoc />
    public bool TryWriteProperty(object element, string name, JsonElement value, out string error)
    {
        if (element is not DependencyObject dep)
        {
            error = "Target is not a control.";
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            error = "Property name is required.";
            return false;
        }

        var prop = FindClrProperty(dep.GetType(), name);
        if (prop is null)
        {
            error = $"Property '{name}' not found on {dep.GetType().Name}.";
            return false;
        }

        if (!prop.CanWrite || prop.SetMethod is null || !prop.SetMethod.IsPublic)
        {
            error = $"Property '{name}' is not writable.";
            return false;
        }

        if (prop.GetIndexParameters().Length > 0)
        {
            error = $"Property '{name}' is an indexer and cannot be set.";
            return false;
        }

        if (!PropertyValueSerializer.TryCoerce(value, prop.PropertyType, out var coerced, out error))
            return false;

        try
        {
            prop.SetValue(dep, coerced);
            error = string.Empty;
            return true;
        }
        catch (TargetInvocationException tie)
        {
            error = $"Setting '{name}' threw: {tie.InnerException?.Message ?? tie.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Setting '{name}' failed: {ex.Message}";
            return false;
        }
    }

    /// <inheritdoc />
    public object? GetDataContext(object element) => (element as FrameworkElement)?.DataContext;

    /// <summary>
    /// Projects a WPF framework value to a JSON-friendly form via the neutral serializer,
    /// supplying the WPF-specific element + value-type renderers. Mirrors
    /// <c>AvaloniaUiAdapter.Project</c>.
    /// <para>
    /// WPF layout properties default to non-finite doubles far more readily than Avalonia
    /// (e.g. <c>MaxWidth</c>/<c>MaxHeight</c> default to <see cref="double.PositiveInfinity"/>,
    /// and arrange math can yield <see cref="double.NaN"/>). <c>System.Text.Json</c> (used by
    /// the MCP host to serialize the whole tool result) throws on infinity/NaN by default, but
    /// the neutral <see cref="PropertyValueSerializer"/> now guarantees JSON-safe non-finite
    /// values centrally — its <c>Reduce</c> turns any non-finite <c>double</c>/<c>float</c>
    /// (at every nesting level) into its invariant string form ("Infinity"/"-Infinity"/"NaN").
    /// So no adapter-side sanitizing is needed here, and a single odd property can never crash
    /// an entire <c>get_properties</c> dump.
    /// </para>
    /// </summary>
    private object? Project(object? value) =>
        _serializer.ToJsonFriendly(value, RenderElement, RenderLeaf);

    private static string? RenderElement(object value) =>
        value is FrameworkElement fe
            ? $"{fe.GetType().Name}{(string.IsNullOrEmpty(fe.Name) ? "" : "#" + fe.Name)}"
            : null;

    private static string? RenderLeaf(object value)
    {
        var type = value.GetType();
        // Known WPF value-structs (Thickness, Color, Rect, Point, Size, CornerRadius,
        // GridLength, Duration, …) serialize cleanly via their invariant string form,
        // exactly like the Avalonia adapter's struct short-circuit. Reference leaves such
        // as Brush are not value types and fall through to the serializer's ToString()
        // fallback (whose output is JSON-friendly for WPF brushes/colors).
        return type.IsValueType &&
               type.Namespace?.StartsWith("System.Windows", StringComparison.Ordinal) == true
            ? value.ToString()
            : null;
    }

    /// <summary>
    /// Resolves the styled/attached <see cref="DependencyProperty"/> named
    /// <paramref name="name"/> registered (or inherited) on <paramref name="dep"/>'s type,
    /// via the WPF <see cref="DependencyPropertyDescriptor"/> over the instance. Returns
    /// <c>null</c> when no DP of that name is surfaced on the element.
    /// </summary>
    private static DependencyProperty? TryFindDependencyProperty(DependencyObject dep, string name)
    {
        var pd = TypeDescriptor.GetProperties(dep)
            .Cast<PropertyDescriptor>()
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        return pd is null ? null : DependencyPropertyDescriptor.FromProperty(pd)?.DependencyProperty;
    }

    private static PropertyInfo? FindClrProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
}
