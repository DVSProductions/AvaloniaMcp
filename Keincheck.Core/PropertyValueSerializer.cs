using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Keincheck.Core;

/// <summary>
/// Framework-neutral JSON projection helpers shared by the adapters. Reading reduces
/// an arbitrary value to a JSON-friendly representation (primitive, string, or a small
/// depth-limited / cycle-safe dictionary/array); writing coercion is a generic,
/// reflection-driven <see cref="JsonElement"/> → CLR converter (nullable/enum/numeric/
/// <see cref="TypeConverter"/>/static <c>Parse</c>). The adapter owns all
/// framework-type knowledge (which CLR property to read, how to render a framework
/// element or value-type) and delegates only the neutral reduction here.
/// </summary>
public sealed class PropertyValueSerializer
{
    private readonly int _maxDepth;

    /// <param name="maxDepth">
    /// Maximum object graph depth when reducing complex values. Defaults to a
    /// conservative 8; the host passes <see cref="McpServerOptions.MaxSerializationDepth"/>.
    /// </param>
    public PropertyValueSerializer(int maxDepth = 8)
    {
        _maxDepth = Math.Max(1, maxDepth);
    }

    /// <summary>The configured maximum reduction depth.</summary>
    public int MaxDepth => _maxDepth;

    /// <summary>
    /// Reduces <paramref name="value"/> to a JSON-serializable representation using the
    /// configured depth. The optional <paramref name="renderElement"/> hook lets the
    /// adapter project a framework element it recognizes (e.g. a control to
    /// <c>"Type#Name"</c>) before the generic reduction; the optional
    /// <paramref name="renderLeaf"/> hook lets it short-circuit framework value-types
    /// (e.g. Avalonia structs) to their string form.
    /// </summary>
    public object? ToJsonFriendly(
        object? value,
        Func<object, string?>? renderElement = null,
        Func<object, string?>? renderLeaf = null) =>
        Reduce(value, _maxDepth, renderElement, renderLeaf);

    private static object? Reduce(
        object? value,
        int depth,
        Func<object, string?>? renderElement,
        Func<object, string?>? renderLeaf)
    {
        if (value is null)
            return null;

        switch (value)
        {
            case string:
            case bool:
            case byte or sbyte or short or ushort or int or uint or long or ulong:
            case decimal: // decimal cannot be non-finite, so it is always JSON-safe as-is
                return value;
            // Non-finite floating-point (Infinity/-Infinity/NaN) is NOT representable in
            // JSON; System.Text.Json throws on it ("Infinity cannot be written as valid
            // JSON"), which crashed get_properties when a styled prop (e.g. MaxWidth =
            // double.PositiveInfinity) reached the host serializer. Project the non-finite
            // value to its invariant-culture string here so EVERY value path and nesting
            // level is covered (Reduce recurses for collections); finite values stay raw
            // numbers. Centralizing the guard here is why neither adapter needs its own.
            case float f:
                return float.IsFinite(f) ? value : f.ToString(CultureInfo.InvariantCulture);
            case double d:
                return double.IsFinite(d) ? value : d.ToString(CultureInfo.InvariantCulture);
        }

        var type = value.GetType();

        if (type.IsEnum)
            return value.ToString();

        // Adapter-owned element projection (e.g. a control → "Type#Name").
        if (renderElement is not null)
        {
            var projected = renderElement(value);
            if (projected is not null)
                return projected;
        }

        if (depth <= 0)
            return value.ToString();

        // Adapter-owned leaf projection (e.g. framework value-structs → invariant string).
        if (renderLeaf is not null)
        {
            var leaf = renderLeaf(value);
            if (leaf is not null)
                return leaf;
        }

        if (value is System.Collections.IEnumerable enumerable and not string)
        {
            var items = new List<object?>();
            var count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= 50) // cap collection size in output
                {
                    items.Add("…(truncated)");
                    break;
                }
                items.Add(Reduce(item, depth - 1, renderElement, renderLeaf));
            }
            return items;
        }

        // Fall back to the type's string representation; ToString() is the safest
        // JSON-friendly projection for opaque reference types.
        return value.ToString();
    }

    /// <summary>
    /// Attempts to coerce a <see cref="JsonElement"/> into <paramref name="targetType"/>.
    /// Honors nullable targets, enums, primitives, and any type with a
    /// <see cref="TypeConverter"/> or a static <c>Parse(string)</c> capable of parsing a
    /// string (e.g. Thickness, Brush, Color, GridLength). Generic and framework-neutral —
    /// the adapter passes its own property types in.
    /// </summary>
    public static bool TryCoerce(JsonElement value, Type targetType, out object? result, out string error)
    {
        result = null;
        error = string.Empty;

        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
                return true; // result stays null
            error = $"Cannot assign null to non-nullable {targetType.Name}.";
            return false;
        }

        try
        {
            // Direct primitive paths.
            if (underlying == typeof(string))
            {
                result = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
                return true;
            }

            if (underlying == typeof(bool))
            {
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    result = value.GetBoolean();
                    return true;
                }
            }
            else if (underlying.IsEnum)
            {
                var raw = value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
                result = Enum.Parse(underlying, raw, ignoreCase: true);
                return true;
            }
            else if (IsNumeric(underlying))
            {
                var raw = value.ValueKind == JsonValueKind.Number
                    ? value.GetRawText()
                    : value.GetString();
                if (raw is not null)
                {
                    result = Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
                    return true;
                }
            }

            var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();

            // TypeConverter path (some types register a System.ComponentModel converter).
            var converter = TypeDescriptor.GetConverter(underlying);
            if (text is not null && converter.CanConvertFrom(typeof(string)))
            {
                try
                {
                    result = converter.ConvertFromInvariantString(text);
                    return true;
                }
                catch
                {
                    // fall through to the Parse path
                }
            }

            // Framework value types (Thickness, Color, GridLength, Point, CornerRadius,
            // Size, etc.) expose a static Parse(string) — and sometimes
            // Parse(string, IFormatProvider) — rather than a ComponentModel converter.
            if (text is not null && TryStaticParse(underlying, text, out result))
                return true;

            // Last resort: assignable-from-string.
            if (underlying.IsAssignableFrom(typeof(string)) && text is not null)
            {
                result = text;
                return true;
            }

            error = $"No conversion from JSON {value.ValueKind} to {underlying.Name}.";
            return false;
        }
        catch (Exception ex)
        {
            error = $"Conversion to {underlying.Name} failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Invokes a public static <c>Parse(string)</c> or <c>Parse(string, IFormatProvider)</c>
    /// on <paramref name="targetType"/> if one exists. Covers framework structs
    /// (Thickness, Color, GridLength, …) that parse from their invariant string form.
    /// </summary>
    private static bool TryStaticParse(Type targetType, string text, out object? result)
    {
        result = null;

        var withProvider = targetType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(IFormatProvider) },
            modifiers: null);
        if (withProvider is not null && withProvider.ReturnType == targetType)
        {
            try
            {
                result = withProvider.Invoke(null, new object?[] { text, CultureInfo.InvariantCulture });
                return true;
            }
            catch
            {
                return false;
            }
        }

        var simple = targetType.GetMethod(
            "Parse",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string) },
            modifiers: null);
        if (simple is not null && simple.ReturnType == targetType)
        {
            try
            {
                result = simple.Invoke(null, new object?[] { text });
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static bool IsNumeric(Type t) =>
        t == typeof(byte) || t == typeof(sbyte) ||
        t == typeof(short) || t == typeof(ushort) ||
        t == typeof(int) || t == typeof(uint) ||
        t == typeof(long) || t == typeof(ulong) ||
        t == typeof(float) || t == typeof(double) || t == typeof(decimal);
}
