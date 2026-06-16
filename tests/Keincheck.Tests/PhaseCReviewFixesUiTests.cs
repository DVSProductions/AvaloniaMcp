using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless;
using Keincheck.Avalonia;
using Keincheck.Core;
using Keincheck.Core.Tools;
using ModelContextProtocol.Protocol;
using System.Text.Json;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// End-to-end seam tests for the Phase-C accessibility review fixes (GitHub issue #1),
/// driven against the real <see cref="AvaloniaUiAdapter"/>/<see cref="AvaloniaUiDispatcher"/>
/// on the shared headless UI thread exactly as the live host wires them, and asserted only
/// through each tool's structured result:
/// <list type="bullet">
/// <item><b>globalBounds</b> — tree/query results now carry the element's box in TOP-LEVEL
/// client DIPs alongside the parent-relative <c>bounds</c>, so a caller need not sum
/// container offsets by hand.</item>
/// <item><b>screenshot scale</b> — <c>screenshot_control</c> accepts a render-scale multiplier
/// for legible capture of tiny controls and still returns a well-formed image content block.</item>
/// <item><b>marks scoping</b> — <c>screenshot_marked</c>/<c>describe_screen</c> targeting a
/// child container scope their numbered marks to that subtree, so sibling/title-bar controls
/// are not marked.</item>
/// </list>
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class PhaseCReviewFixesUiTests
{
    private readonly HeadlessSession _session;

    public PhaseCReviewFixesUiTests(HeadlessSession session) => _session = session;

    // Real adapter/dispatcher, touched only through the neutral seam.
    private static IUiAdapter NewAdapter() => new AvaloniaUiAdapter(new PropertyValueSerializer(8));
    private static IUiDispatcher NewDispatcher() => new AvaloniaUiDispatcher();
    private static McpServerOptions NewOptions() => new() { MaxScreenshotDimension = 512 };

    /// <summary>
    /// Shows the window and drives render-timer ticks so measure -> arrange -> render settle
    /// and every control has a real, non-zero on-screen box (without a layout pass
    /// <c>TryGetBoundsInTopLevel</c> yields nothing and nothing is marked). Render scale pinned
    /// for stable geometry. Mirrors <see cref="SemanticToolsUiTests"/>'s helper.
    /// </summary>
    private static Window ShowAndLayout(Window window)
    {
        window.Show();
        window.SetRenderScaling(1.0);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(2);
        return window;
    }

    /// <summary>
    /// A window whose interactive controls live inside an OUTER container that is itself pushed
    /// down/right by a sibling header and the outer panel's margin. That nesting guarantees the
    /// inner button's TOP-LEVEL box (globalBounds) is offset from its parent-relative
    /// <c>bounds</c>, and gives <see cref="MarkScopeWindow"/> a child container distinct from
    /// the window for marks-scoping. The inner button is named so it is addressable and markable.
    /// </summary>
    private static Window MarkScopeWindow(out Button innerButton, out StackPanel innerContainer, out Button headerButton)
    {
        headerButton = new Button { Name = "Header", Content = "Header" };
        innerButton = new Button { Name = "Inner", Content = "Inner" };
        var innerBox = new TextBox { Name = "InnerInput", Text = "x" };

        innerContainer = new StackPanel { Margin = new global::Avalonia.Thickness(20) };
        innerContainer.Children.Add(innerButton);
        innerContainer.Children.Add(innerBox);

        var outer = new StackPanel { Margin = new global::Avalonia.Thickness(10) };
        outer.Children.Add(headerButton);     // a sibling that must NOT be marked when scoping to innerContainer
        outer.Children.Add(innerContainer);

        return new Window { Title = "Scope Window", Width = 320, Height = 260, Content = outer };
    }

    // ---- globalBounds ------------------------------------------------------

    [Fact]
    public void QueryControls_Emits_GlobalBounds_In_TopLevel_Coords_Distinct_From_Parent_Relative_Bounds()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var window = ShowAndLayout(MarkScopeWindow(out _, out _, out _));
            try
            {
                // Scope the search to this window's top-level and resolve the deeply-nested
                // inner button, which is offset from the window origin by the outer + inner
                // margins and the header above it.
                var handle = registry.Assign(window);
                var result = InspectionTools
                    .QueryControls(registry, ui, dispatcher, "Button[Name=Inner]", scopeHandle: handle)
                    .GetAwaiter().GetResult();
                return JsonSerializer.Serialize(result);
            }
            finally { window.Close(); }
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var controls = root.GetProperty("controls");
        Assert.Equal(1, controls.GetArrayLength());

        var inner = controls[0];
        var bounds = inner.GetProperty("bounds");

        // globalBounds is present and is a real object (not null) for a laid-out child.
        var gb = inner.GetProperty("globalBounds");
        Assert.Equal(JsonValueKind.Object, gb.ValueKind);

        // It has a positive on-screen box.
        Assert.True(gb.GetProperty("width").GetDouble() > 0, "globalBounds width should be positive");
        Assert.True(gb.GetProperty("height").GetDouble() > 0, "globalBounds height should be positive");

        // The global box differs from the parent-relative box: the inner button sits below the
        // header and inside two margined panels, so its TOP-LEVEL Y is strictly greater than its
        // StackPanel-relative Y. (Width/height match; only the origin is accumulated.)
        Assert.True(gb.GetProperty("y").GetDouble() > bounds.GetProperty("y").GetDouble(),
            "globalBounds.y should accumulate parent offsets and exceed the parent-relative bounds.y");
        Assert.True(gb.GetProperty("x").GetDouble() >= bounds.GetProperty("x").GetDouble(),
            "globalBounds.x should be at least the parent-relative bounds.x");
    }

    [Fact]
    public void GetVisualTree_Nodes_Carry_GlobalBounds_For_LaidOut_Children()
    {
        var json = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();

            var window = ShowAndLayout(MarkScopeWindow(out _, out _, out _));
            try
            {
                var handle = registry.Assign(window);
                var result = InspectionTools
                    .GetVisualTree(registry, ui, dispatcher, handle: handle, maxDepth: 16)
                    .GetAwaiter().GetResult();
                return JsonSerializer.Serialize(result);
            }
            finally { window.Close(); }
        });

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());

        // At least one node in the dump exposes a non-null globalBounds with a positive box.
        var withGlobal = 0;
        WalkTree(root.GetProperty("nodes"), n =>
        {
            if (n.TryGetProperty("globalBounds", out var gb) && gb.ValueKind == JsonValueKind.Object &&
                gb.GetProperty("width").GetDouble() > 0 && gb.GetProperty("height").GetDouble() > 0)
                withGlobal++;
        });
        Assert.True(withGlobal >= 1, "expected at least one tree node with a positive globalBounds");
    }

    // ---- screenshot scale --------------------------------------------------

    [Fact]
    public void ScreenshotControl_With_Scale_Returns_Png_Image_Content_And_No_Error()
    {
        // The headless drawing backend encodes an empty surface, so we assert SUCCESS and the
        // image content-block STRUCTURE (PNG mime) rather than the bytes — mirroring the
        // screenshot_marked test note. scale:4 exercises the new clamp + 5-arg render path.
        var mimeType = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var window = ShowAndLayout(MarkScopeWindow(out var inner, out _, out _));
            try
            {
                var handle = registry.Assign(inner);
                var result = ScreenshotTools
                    .ScreenshotControl(registry, ui, dispatcher, options, target: handle, scale: 4)
                    .GetAwaiter().GetResult();

                // Success path returns a single ImageContentBlock (the error path is a
                // TextContentBlock carrying { ok:false, error }).
                var img = Assert.IsType<ImageContentBlock>(result);
                return img.MimeType;
            }
            finally { window.Close(); }
        });

        Assert.Equal("image/png", mimeType);
    }

    [Fact]
    public void ScreenshotControl_Bad_Target_With_Scale_Returns_Structured_Error_Never_Throws()
    {
        var text = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var result = ScreenshotTools
                .ScreenshotControl(registry, ui, dispatcher, options, target: "ctl-nope", scale: 8)
                .GetAwaiter().GetResult();

            // Bad target must not throw even with a scale; it returns the structured-error text block.
            var block = Assert.IsType<TextContentBlock>(result);
            return block.Text;
        });

        using var doc = JsonDocument.Parse(text);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("error").GetString()));
    }

    // ---- marks scoping -----------------------------------------------------

    [Fact]
    public void ScreenshotMarked_Scoped_To_Child_Container_Marks_Only_That_Subtree()
    {
        // Resolve the inner container's bounds and the sibling header's handle up front so the
        // assertion can prove (a) every mark resolves to a control inside the container subtree,
        // and (b) the sibling Header button — markable, but OUTSIDE the target — is never marked.
        var (legendJson, containerBox, headerHandle, innerHandle) = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var window = ShowAndLayout(MarkScopeWindow(out var inner, out var container, out var header));
            try
            {
                var containerHandle = registry.Assign(container);
                var headerH = registry.Assign(header);
                var innerH = registry.Assign(inner);

                // The container's box in top-level coords, to bound-check each mark against.
                ui.TryGetBoundsInTopLevel(container, ui.GetTopLevel(container)!, out var cBox);

                var result = SemanticTools
                    .ScreenshotMarked(registry, ui, dispatcher, options, target: containerHandle)
                    .GetAwaiter().GetResult();

                Assert.False(result.IsError ?? false);
                var legend = Assert.IsType<TextContentBlock>(result.Content[1]);
                return (legend.Text, cBox, headerH, innerH);
            }
            finally { window.Close(); }
        });

        using var doc = JsonDocument.Parse(legendJson);
        var legend = doc.RootElement;
        Assert.True(legend.GetProperty("ok").GetBoolean());
        var marks = legend.GetProperty("marks");

        // The scoped target's interactive child (the inner Button) is marked...
        Assert.True(marks.GetArrayLength() >= 1, "expected at least the inner Button under the target to be marked");

        var markedHandles = marks.EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();

        // ...the inner button IS among the marks...
        Assert.Contains(innerHandle, markedHandles);

        // ...and the sibling Header button (interactive, but OUTSIDE the target container) is NOT.
        Assert.DoesNotContain(headerHandle, markedHandles);

        // Every mark's box lies within the target container's box (allowing a 1px slack for
        // border rounding) — proving marks are scoped to the subtree, not the whole window.
        const double slack = 1.0;
        foreach (var m in marks.EnumerateArray())
        {
            var b = m.GetProperty("bounds");
            var x = b.GetProperty("x").GetDouble();
            var y = b.GetProperty("y").GetDouble();
            var w = b.GetProperty("width").GetDouble();
            var h = b.GetProperty("height").GetDouble();

            Assert.True(x >= containerBox.X - slack,
                $"mark x {x} should be within the container (left {containerBox.X})");
            Assert.True(y >= containerBox.Y - slack,
                $"mark y {y} should be within the container (top {containerBox.Y})");
            Assert.True(x + w <= containerBox.X + containerBox.Width + slack,
                $"mark right {x + w} should be within the container (right {containerBox.X + containerBox.Width})");
            Assert.True(y + h <= containerBox.Y + containerBox.Height + slack,
                $"mark bottom {y + h} should be within the container (bottom {containerBox.Y + containerBox.Height})");
        }
    }

    [Fact]
    public void DescribeScreen_Scoped_To_Child_Container_Marks_Exclude_The_Sibling_Header()
    {
        var (summaryJson, headerHandle, innerHandle) = _session.RunOnUiThread(() =>
        {
            var registry = new ControlRegistry();
            IUiAdapter ui = NewAdapter();
            IUiDispatcher dispatcher = NewDispatcher();
            var options = NewOptions();

            var window = ShowAndLayout(MarkScopeWindow(out var inner, out var container, out var header));
            try
            {
                var containerHandle = registry.Assign(container);
                var headerH = registry.Assign(header);
                var innerH = registry.Assign(inner);

                var result = SemanticTools
                    .DescribeScreen(registry, ui, dispatcher, options, target: containerHandle)
                    .GetAwaiter().GetResult();

                Assert.False(result.IsError ?? false);
                var summary = Assert.IsType<TextContentBlock>(result.Content[1]);
                return (summary.Text, headerH, innerH);
            }
            finally { window.Close(); }
        });

        using var doc = JsonDocument.Parse(summaryJson);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("ok").GetBoolean());

        var markedHandles = root.GetProperty("marks").EnumerateArray()
            .Select(m => m.GetProperty("id").GetString())
            .ToList();

        // describe_screen scopes its marks the same way: the inner button is marked, the sibling
        // header is not. (The bundled semantic summary still describes the whole window.)
        Assert.Contains(innerHandle, markedHandles);
        Assert.DoesNotContain(headerHandle, markedHandles);
    }

    // ---- helpers -----------------------------------------------------------

    /// <summary>Pre-order walk over an InspectionTools tree node array, recursing through "children".</summary>
    private static void WalkTree(JsonElement nodes, System.Action<JsonElement> visit)
    {
        if (nodes.ValueKind != JsonValueKind.Array)
            return;
        foreach (var node in nodes.EnumerateArray())
        {
            if (node.ValueKind != JsonValueKind.Object)
                continue;
            visit(node);
            if (node.TryGetProperty("children", out var children))
                WalkTree(children, visit);
        }
    }
}
