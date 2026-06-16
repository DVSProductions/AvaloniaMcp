using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Keincheck.Avalonia;
using Keincheck.Core;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Smoke tests for the shared spine. These exercise the parts that do not
/// require a running UI thread (options defaults, JSON coercion, ring buffer).
/// Visual-tree tests belong with the module agents and a headless app session.
/// </summary>
public class SpineTests
{
    [Fact]
    public void Options_Have_Expected_Defaults()
    {
        var opts = new McpServerOptions();
        Assert.Equal(3001, opts.Port);
        Assert.True(opts.MaxScreenshotDimension > 0);
        Assert.True(opts.MaxSerializationDepth > 0);
        Assert.True(opts.CaptureBindingErrors);
    }

    [Fact]
    public void PropertyValueSerializer_Coerces_Int_From_Json()
    {
        var json = JsonDocument.Parse("42").RootElement;
        var ok = PropertyValueSerializer.TryCoerce(json, typeof(int), out var value, out var error);
        Assert.True(ok, error);
        Assert.Equal(42, value);
    }

    [Fact]
    public void PropertyValueSerializer_Coerces_Thickness_Via_TypeConverter()
    {
        var json = JsonDocument.Parse("\"10,5,10,5\"").RootElement;
        var ok = PropertyValueSerializer.TryCoerce(json, typeof(global::Avalonia.Thickness), out var value, out var error);
        Assert.True(ok, error);
        Assert.Equal(new global::Avalonia.Thickness(10, 5, 10, 5), value);
    }

    // ---- non-finite floating-point projection (issue #1: get_properties crash) -----
    // System.Text.Json throws "Infinity cannot be written as valid JSON" on a non-finite
    // double/float, which crashed get_properties whenever a styled prop (e.g. MaxWidth =
    // double.PositiveInfinity) reached the host serializer. ToJsonFriendly must project the
    // non-finite value to its invariant string up front so the result is always JSON-safe,
    // while finite values stay raw numbers and survive a real serialization round-trip.

    [Theory]
    [InlineData(double.PositiveInfinity, "Infinity")]
    [InlineData(double.NegativeInfinity, "-Infinity")]
    [InlineData(double.NaN, "NaN")]
    public void Serializer_Projects_NonFinite_Double_To_Invariant_String(double value, string expected)
    {
        var reduced = new PropertyValueSerializer().ToJsonFriendly(value);
        Assert.Equal(expected, Assert.IsType<string>(reduced));

        // And the projection actually serializes (the whole point: no STJ throw).
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(reduced));
    }

    [Theory]
    [InlineData(float.PositiveInfinity, "Infinity")]
    [InlineData(float.NegativeInfinity, "-Infinity")]
    [InlineData(float.NaN, "NaN")]
    public void Serializer_Projects_NonFinite_Float_To_Invariant_String(float value, string expected)
    {
        var reduced = new PropertyValueSerializer().ToJsonFriendly(value);
        Assert.Equal(expected, Assert.IsType<string>(reduced));
        Assert.Equal($"\"{expected}\"", JsonSerializer.Serialize(reduced));
    }

    [Fact]
    public void Serializer_Keeps_Finite_Double_As_Raw_Number()
    {
        var reduced = new PropertyValueSerializer().ToJsonFriendly(3.5d);

        // Stays a real number, not a string — finite values are untouched.
        Assert.Equal(3.5d, Assert.IsType<double>(reduced));
        Assert.Equal("3.5", JsonSerializer.Serialize(reduced));
    }

    [Fact]
    public void Serializer_Sanitizes_NonFinite_Inside_A_Collection_ElementWise()
    {
        // Reduce recurses for IEnumerable, so a non-finite element nested in a list must be
        // projected too — otherwise the host serializer still throws on the inner value.
        var list = new List<double> { 1.0, double.PositiveInfinity, double.NaN, 2.5 };

        var reduced = new PropertyValueSerializer().ToJsonFriendly(list);
        var items = Assert.IsAssignableFrom<IEnumerable<object?>>(reduced).ToList();

        Assert.Equal(1.0d, items[0]);            // finite stays a number
        Assert.Equal("Infinity", items[1]);      // non-finite -> invariant string
        Assert.Equal("NaN", items[2]);
        Assert.Equal(2.5d, items[3]);

        // The whole list round-trips through STJ without throwing.
        Assert.Equal("[1,\"Infinity\",\"NaN\",2.5]", JsonSerializer.Serialize(reduced));
    }

    [Fact]
    public void BindingErrorSink_RingBuffer_Returns_Recent_In_Order()
    {
        var sink = new BindingErrorSink(capacity: 3, inner: null, bindingOnly: true);
        for (var i = 0; i < 5; i++)
            sink.Log(global::Avalonia.Logging.LogEventLevel.Warning, global::Avalonia.Logging.LogArea.Binding, null, "msg {Index}", i);

        var recent = sink.Recent(10).ToArray();
        Assert.Equal(3, recent.Length);          // bounded to capacity
        Assert.Contains("msg 2", recent[0]);     // oldest retained
        Assert.Contains("msg 4", recent[^1]);    // newest
    }
}
