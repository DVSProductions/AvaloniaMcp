using System.Collections.Generic;
using System.Text.Json;
using Keincheck.Avalonia;
using Keincheck.Core;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Proves the neutral seam is not just framework-free on paper but functionally
/// complete: the framework-free engine (<see cref="ControlRegistry"/> +
/// <see cref="SelectorChain"/> + <see cref="PropertyValueSerializer"/>) yields the
/// SAME results when driven purely through the neutral <see cref="IUiAdapter"/>
/// object-handle API as the typed Avalonia path does.
/// </summary>
/// <remarks>
/// <para>
/// The discipline that makes this a real seam test: the adapter is referenced only
/// through the <see cref="IUiAdapter"/> interface, element handles are passed as
/// plain <see cref="object"/>, and the registry/serializer are called with no
/// Avalonia type in sight. The window is still constructed with the shared
/// <c>TestWindowFactory</c>/<c>CyclicGraphFactory</c> helpers (they own the
/// framework-specific construction), but every assertion downstream goes through
/// the neutral contract. This mirrors <c>SpineUiTests</c> deliberately so the two
/// can be diffed: same scenarios, driven via the opaque object API.
/// </para>
/// <para>
/// The concrete <see cref="AvaloniaUiAdapter"/> backs the interface here exactly as
/// the live host wires it; a future <c>WpfUiAdapter</c> dropped behind the same
/// <see cref="IUiAdapter"/> would satisfy these identical assertions unchanged.
/// </para>
/// </remarks>
[Collection(HeadlessCollection.Name)]
public sealed class NeutralSeamUiTests
{
    private readonly HeadlessSession _session;

    public NeutralSeamUiTests(HeadlessSession session) => _session = session;

    // Build the adapter but immediately widen to the neutral interface — nothing in
    // the tests below may touch a concrete Avalonia type.
    private static IUiAdapter NewNeutralAdapter(int maxDepth = 8) =>
        new AvaloniaUiAdapter(new PropertyValueSerializer(maxDepth));

    // ---- selector Query via the object-handle adapter API -----------------

    [Fact]
    public void Neutral_Query_By_Type_And_Name_Finds_Only_The_Save_Button()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewNeutralAdapter();

            // The factory hands back typed controls, but we keep only opaque handles.
            object window = TestWindowFactory.Create(out var saveButton, out var inputBox);
            object saveHandle = saveButton;
            object inputHandle = inputBox;

            IReadOnlyList<object> matches = registry.Query("Button[Name=Save]", ui, window);

            Assert.Single(matches);
            Assert.Same(saveHandle, matches[0]);
            Assert.DoesNotContain(inputHandle, matches);

            // And the neutral metadata the selector relied on is what the adapter
            // reports for that handle — no framework cast needed by the caller.
            Assert.True(ui.IsControl(saveHandle));
            Assert.True(ui.MatchesType(saveHandle, "Button"));
            Assert.Equal(TestWindowFactory.ButtonName, ui.GetName(saveHandle));
        });
    }

    [Fact]
    public void Neutral_Query_By_Name_Sugar_Matches_The_Input_TextBox()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewNeutralAdapter();

            object window = TestWindowFactory.Create(out _, out var inputBox);
            object inputHandle = inputBox;

            IReadOnlyList<object> matches = registry.Query("#Input", ui, window);

            Assert.Single(matches);
            Assert.Same(inputHandle, matches[0]);
        });
    }

    [Fact]
    public void Neutral_Query_Is_Name_Case_Sensitive()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewNeutralAdapter();
            object window = TestWindowFactory.Create(out _, out _);

            // Ordinal/case-sensitive Name matching is part of the neutral contract.
            Assert.Empty(registry.Query("#save", ui, window));
            Assert.Empty(registry.Query("Button[Name=save]", ui, window));
        });
    }

    [Fact]
    public void Neutral_Query_Bad_Selector_Returns_Empty_Never_Throws()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewNeutralAdapter();
            object window = TestWindowFactory.Create(out _, out _);

            Assert.Empty(registry.Query("Button[", ui, window));
            Assert.Empty(registry.Query("   ", ui, window));
        });
    }

    // ---- registry handle round-trip over opaque handles -------------------

    [Fact]
    public void Neutral_Registry_RoundTrips_Opaque_Handles()
    {
        _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            object window = TestWindowFactory.Create(out var saveButton, out _);
            object saveHandle = saveButton;

            var id = registry.Assign(saveHandle);
            Assert.False(string.IsNullOrWhiteSpace(id));
            Assert.Equal(id, registry.Assign(saveHandle)); // idempotent

            Assert.True(registry.TryResolve(id, out var resolved));
            Assert.Same(saveHandle, resolved);

            GC.KeepAlive(window);
        });
    }

    // ---- property read / write round-trip via the neutral adapter ---------

    [Fact]
    public void Neutral_Property_Read_Yields_JsonFriendly_Values()
    {
        _session.RunOnUiThread(() =>
        {
            IUiAdapter ui = NewNeutralAdapter();
            _ = TestWindowFactory.Create(out var saveButton, out _);
            object saveHandle = saveButton;

            // String CLR property -> plain string.
            Assert.True(ui.TryReadProperty(saveHandle, "Name", out var name));
            Assert.Equal(TestWindowFactory.ButtonName, Assert.IsType<string>(name));

            // Styled numeric property -> boxed double, never a framework value-type.
            saveButton.Width = 64;
            Assert.True(ui.TryReadProperty(saveHandle, "Width", out var width));
            Assert.Equal(64d, Assert.IsType<double>(width));
        });
    }

    [Fact]
    public void Neutral_Property_Write_Then_Read_RoundTrips_Numeric()
    {
        _session.RunOnUiThread(() =>
        {
            IUiAdapter ui = NewNeutralAdapter();
            _ = TestWindowFactory.Create(out var saveButton, out _);
            object saveHandle = saveButton;

            var json = JsonDocument.Parse("123").RootElement;
            var ok = ui.TryWriteProperty(saveHandle, "Width", json, out var error);

            Assert.True(ok, error);
            Assert.True(ui.TryReadProperty(saveHandle, "Width", out var read));
            Assert.Equal(123d, Assert.IsType<double>(read));
        });
    }

    [Fact]
    public void Neutral_Property_Write_From_String_RoundTrips_Framework_Struct()
    {
        _session.RunOnUiThread(() =>
        {
            IUiAdapter ui = NewNeutralAdapter();
            _ = TestWindowFactory.Create(out var saveButton, out _);
            object saveHandle = saveButton;

            // The adapter coerces the string into the framework's Thickness on write
            // and projects it back to its invariant string form on read — the caller
            // sees only neutral JSON-friendly values across the whole round-trip.
            var json = JsonDocument.Parse("\"10,5,10,5\"").RootElement;
            var ok = ui.TryWriteProperty(saveHandle, "Margin", json, out var error);

            Assert.True(ok, error);
            Assert.True(ui.TryReadProperty(saveHandle, "Margin", out var read));
            var text = Assert.IsType<string>(read);
            Assert.Contains("10", text);
            Assert.Contains("5", text);
        });
    }

    [Fact]
    public void Neutral_Property_Write_Unknown_Fails_Structured()
    {
        _session.RunOnUiThread(() =>
        {
            IUiAdapter ui = NewNeutralAdapter();
            _ = TestWindowFactory.Create(out var saveButton, out _);
            object saveHandle = saveButton;

            var json = JsonDocument.Parse("1").RootElement;
            var ok = ui.TryWriteProperty(saveHandle, "NoSuchProperty", json, out var error);

            Assert.False(ok);
            Assert.False(string.IsNullOrWhiteSpace(error));
        });
    }

    // ---- visited-guard: cyclic merged graph terminates via the neutral walk

    [Fact]
    public void Neutral_Query_Over_Cyclic_Merged_Graph_Terminates_And_Finds_Target()
    {
        // The merged logical+visual graph built by CyclicGraphFactory is genuinely
        // cyclic (proven by SpineUiTests' guardless control). Driven through the
        // neutral IUiAdapter, the shared visited-guard inside SelectorChain still
        // breaks the cycle: the walk returns promptly with exactly the target.
        var matchCount = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewNeutralAdapter();

            object window = CyclicGraphFactory.Create(out var target);
            object targetHandle = target;

            IReadOnlyList<object> matches = registry.Query("Button[Name=Target]", ui, window);

            Assert.Contains(targetHandle, matches);
            return matches.Count;
        });

        Assert.Equal(1, matchCount);
    }
}
