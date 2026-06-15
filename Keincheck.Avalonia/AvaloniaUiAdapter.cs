using System.IO;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using Keincheck.Core;

namespace Keincheck.Avalonia;

/// <summary>
/// The Avalonia 12 implementation of the framework-neutral <see cref="IUiAdapter"/>.
/// It owns the concrete toolkit calls — root enumeration, tree walks, the
/// <see cref="AvaloniaPropertyRegistry"/>, <see cref="RenderTargetBitmap"/> rendering,
/// UI-Automation peers, synthetic routed input, hit-testing, focus, and the
/// <see cref="BindingErrorSink"/> — and converts between Avalonia's element/geometry
/// types and the neutral <see cref="object"/> handles + <see cref="UiRect"/>/
/// <see cref="UiPoint"/>/<see cref="UiVector"/> structs at the seam.
/// </summary>
/// <remarks>
/// Construct it with the shared <see cref="PropertyValueSerializer"/> (and an optional
/// <see cref="BindingErrorSink"/>) the host registers as DI singletons. All members
/// are UI-thread-affine exactly like the tool bodies they back; the adapter does not
/// re-marshal.
/// </remarks>
public sealed class AvaloniaUiAdapter : IUiAdapter
{
    private readonly PropertyValueSerializer _serializer;
    private readonly BindingErrorSink? _bindingErrors;
    private readonly int _defaultMaxDimension;

    /// <param name="serializer">Shared property serializer (host DI singleton).</param>
    /// <param name="bindingErrors">
    /// Optional binding-error ring buffer. When null, <see cref="GetRecentBindingErrors"/>
    /// falls back to <see cref="BindingErrorSink.Current"/> if one is installed.
    /// </param>
    /// <param name="defaultMaxScreenshotDimension">
    /// Fallback max PNG dimension used only if a caller passes a non-positive value.
    /// </param>
    public AvaloniaUiAdapter(
        PropertyValueSerializer serializer,
        BindingErrorSink? bindingErrors = null,
        int defaultMaxScreenshotDimension = 2048)
    {
        _serializer = serializer ?? throw new ArgumentNullException(nameof(serializer));
        _bindingErrors = bindingErrors;
        _defaultMaxDimension = defaultMaxScreenshotDimension > 0 ? defaultMaxScreenshotDimension : 2048;
    }

    // ---------------------------------------------------------------- topology

    /// <inheritdoc />
    public IEnumerable<object> EnumerateRoots() => EnumerateRootsCore();

    /// <summary>
    /// All open top-level visuals (windows, popups) of the current application. Safe to
    /// call only on the UI thread. (Moved here from the framework-free ControlRegistry.)
    /// </summary>
    internal static IEnumerable<Visual> EnumerateRootsCore()
    {
        var app = Application.Current;
        if (app is null)
            yield break;

        switch (app.ApplicationLifetime)
        {
            case IClassicDesktopStyleApplicationLifetime desktop:
                foreach (var w in desktop.Windows)
                    yield return w;
                break;
            case ISingleViewApplicationLifetime single when single.MainView is { } mv:
                if (TopLevel.GetTopLevel(mv) is { } tl)
                    yield return tl;
                else
                    yield return mv;
                break;
        }
    }

    /// <inheritdoc />
    public object? GetTopLevel(object element) =>
        element is Visual v ? TopLevel.GetTopLevel(v) : null;

    /// <inheritdoc />
    public IEnumerable<object> GetLogicalChildren(object element)
    {
        if (element is not ILogical logical)
            yield break;
        // Yield every logical child element (not just Controls): the selector walk needs
        // to traverse THROUGH non-Control visuals (template internals, adorner relays) to
        // reach controls beneath them. Consumers that want controls only filter via
        // IsControl. Only Visual-derived children are addressable handles.
        foreach (var lc in logical.LogicalChildren)
            if (lc is Visual v)
                yield return v;
    }

    /// <inheritdoc />
    public IEnumerable<object> GetVisualChildren(object element)
    {
        if (element is not Visual visual)
            yield break;
        foreach (var vc in visual.GetVisualChildren())
            yield return vc;
    }

    // ---------------------------------------------------------------- metadata

    /// <inheritdoc />
    public bool IsControl(object element) => element is Control;

    /// <inheritdoc />
    public string GetTypeName(object element) => element.GetType().Name;

    /// <inheritdoc />
    public bool MatchesType(object element, string typeName)
    {
        for (var t = element.GetType(); t is not null && t != typeof(object); t = t.BaseType)
        {
            if (string.Equals(t.Name, typeName, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    /// <inheritdoc />
    public string? GetName(object element) =>
        element is Control c && !string.IsNullOrEmpty(c.Name) ? c.Name : null;

    /// <inheritdoc />
    public string? GetTitle(object element) => element is Window w ? w.Title : null;

    /// <inheritdoc />
    public UiRect GetBounds(object element)
    {
        if (element is Control c)
        {
            var b = c.Bounds;
            return new UiRect(b.X, b.Y, b.Width, b.Height);
        }
        return UiRect.Empty;
    }

    /// <inheritdoc />
    public bool IsEffectivelyVisible(object element) => element is Control c && c.IsEffectivelyVisible;

    /// <inheritdoc />
    public bool IsEffectivelyEnabled(object element) => element is Control c && c.IsEffectivelyEnabled;

    /// <inheritdoc />
    public bool IsActiveWindow(object element) => element is WindowBase wb && wb.IsActive;

    // -------------------------------------------------------------- properties

    /// <inheritdoc />
    public IEnumerable<string> GetPropertyNames(object element)
    {
        if (element is not Control control)
            yield break;
        foreach (var prop in AvaloniaPropertyRegistry.Instance.GetRegistered(control))
            yield return prop.Name;
    }

    /// <inheritdoc />
    public bool TryReadProperty(object element, string name, out object? jsonFriendlyValue)
    {
        jsonFriendlyValue = null;
        if (element is not Control control || string.IsNullOrEmpty(name))
            return false;

        // Prefer the styled/attached property of that name (more values are reachable
        // that way), then fall back to a CLR property read — mirroring the v1
        // serializer's two read paths so values are identical to before the refactor.
        var avProp = AvaloniaPropertyRegistry.Instance.GetRegistered(control)
            .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (avProp is not null)
        {
            try
            {
                jsonFriendlyValue = Project(control.GetValue(avProp));
                return true;
            }
            catch
            {
                jsonFriendlyValue = null;
                return false;
            }
        }

        var clr = FindClrProperty(control.GetType(), name);
        if (clr is null || !clr.CanRead || clr.GetIndexParameters().Length > 0)
            return false;

        try
        {
            jsonFriendlyValue = Project(clr.GetValue(control));
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
        if (element is not Control control)
        {
            error = "Target is not a control.";
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            error = "Property name is required.";
            return false;
        }

        var prop = FindClrProperty(control.GetType(), name);
        if (prop is null)
        {
            error = $"Property '{name}' not found on {control.GetType().Name}.";
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
            prop.SetValue(control, coerced);
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
    public object? GetDataContext(object element) => (element as StyledElement)?.DataContext;

    /// <summary>
    /// Projects an Avalonia framework value to a JSON-friendly form via the neutral
    /// serializer, supplying the Avalonia-specific element + value-type renderers.
    /// </summary>
    private object? Project(object? value) =>
        _serializer.ToJsonFriendly(value, RenderElement, RenderLeaf);

    private static string? RenderElement(object value) =>
        value is Control control
            ? $"{control.GetType().Name}{(string.IsNullOrEmpty(control.Name) ? "" : "#" + control.Name)}"
            : null;

    private static string? RenderLeaf(object value)
    {
        var type = value.GetType();
        // Known Avalonia structs serialize cleanly via their invariant string form.
        return type.Namespace?.StartsWith("Avalonia", StringComparison.Ordinal) == true && type.IsValueType
            ? value.ToString()
            : null;
    }

    private static PropertyInfo? FindClrProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    // ----------------------------------------------------------------- render

    /// <inheritdoc />
    public bool TryRenderToPng(object element, int maxDim, out byte[] png, out string error)
    {
        // A Window/TopLevel renders as a whole visual; any other control renders its
        // own subtree (with a cropped-TopLevel fallback). This unifies the v1
        // TryRenderControlToPng / TryRenderVisualToPng split behind one neutral method.
        if (element is TopLevel topLevel)
            return TryRenderVisualToPng(topLevel, maxDim, cropRect: null, out png, out error);
        if (element is Control control)
            return TryRenderControlToPng(control, maxDim, out png, out error);

        png = Array.Empty<byte>();
        error = "Target is not a renderable visual.";
        return false;
    }

    private bool TryRenderControlToPng(Control control, int maxDimension, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;
        var max = maxDimension > 0 ? maxDimension : _defaultMaxDimension;

        var localSize = control.Bounds.Size;
        if (localSize.Width <= 0 || localSize.Height <= 0)
        {
            error = $"Control '{Describe(control)}' has no renderable size " +
                    $"({localSize.Width}x{localSize.Height}); it may not be laid out or visible.";
            return false;
        }

        var (pixelW, pixelH, scale) = ClampToPixels(localSize.Width, localSize.Height, max);

        // Primary path: render the control subtree directly.
        try
        {
            using var rtb = new RenderTargetBitmap(
                new PixelSize(pixelW, pixelH), new Vector(96 * scale, 96 * scale));
            rtb.Render(control);
            png = Encode(rtb);
            return true;
        }
        catch (Exception ex)
        {
            // Fallback: render the whole TopLevel and crop to the control's bounds.
            var topLevel = TopLevel.GetTopLevel(control);
            if (topLevel is null)
            {
                error = $"Direct render of '{Describe(control)}' failed ({ex.Message}) and the control has no TopLevel to crop from.";
                return false;
            }

            if (control.TransformToVisual(topLevel) is not { } toRoot)
            {
                error = $"Direct render of '{Describe(control)}' failed and its bounds could not be mapped to the window.";
                return false;
            }

            var cropRect = new Rect(localSize).TransformToAABB(toRoot);
            return TryRenderVisualToPng(topLevel, max, cropRect, out png, out error);
        }
    }

    private bool TryRenderVisualToPng(Visual visual, int maxDimension, Rect? cropRect, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;
        var max = maxDimension > 0 ? maxDimension : _defaultMaxDimension;

        var fullSize = visual.Bounds.Size;
        if (fullSize.Width <= 0 || fullSize.Height <= 0)
        {
            error = $"The visual has no renderable size ({fullSize.Width}x{fullSize.Height}).";
            return false;
        }

        var (pixelW, pixelH, scale) = ClampToPixels(fullSize.Width, fullSize.Height, max);

        using var rtb = new RenderTargetBitmap(
            new PixelSize(pixelW, pixelH), new Vector(96 * scale, 96 * scale));
        rtb.Render(visual);

        if (cropRect is not { } crop)
        {
            png = Encode(rtb);
            return true;
        }

        var clamped = crop.Intersect(new Rect(fullSize));
        if (clamped.Width <= 0 || clamped.Height <= 0)
        {
            error = "The crop region is empty after clamping to the visual.";
            return false;
        }

        var (cropW, cropH, cropScale) = ClampToPixels(clamped.Width, clamped.Height, max);
        using var cropped = new RenderTargetBitmap(
            new PixelSize(cropW, cropH), new Vector(96 * cropScale, 96 * cropScale));
        using (var ctx = cropped.CreateDrawingContext())
        {
            var dest = new Rect(0, 0, clamped.Width, clamped.Height);
            var src = new Rect(clamped.X, clamped.Y, clamped.Width, clamped.Height);
            ctx.DrawImage(rtb, src, dest);
        }

        png = Encode(cropped);
        return true;
    }

    private static byte[] Encode(RenderTargetBitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms); // PNG encode
        return ms.ToArray();
    }

    private static (int width, int height, double scale) ClampToPixels(
        double dipWidth, double dipHeight, int max)
    {
        var scale = 1.0;
        var largest = Math.Max(dipWidth, dipHeight);
        if (largest > max)
            scale = max / largest;

        var w = Math.Max(1, (int)Math.Round(dipWidth * scale));
        var h = Math.Max(1, (int)Math.Round(dipHeight * scale));
        return (w, h, scale);
    }

    // ------------------------------------------------------------- automation

    /// <inheritdoc />
    public UiAutomationResult InvokeAutomation(object element, UiAutomationAction action, string? value)
    {
        if (element is not Control control)
            return UiAutomationResult.Failure("Target is not a control.");

        var peer = ControlAutomationPeer.CreatePeerForElement(control);
        if (peer is null)
            return UiAutomationResult.Failure("No automation peer is available for this control.");

        try
        {
            return action switch
            {
                UiAutomationAction.Invoke   => DoInvoke(peer),
                UiAutomationAction.Toggle   => DoToggle(peer),
                UiAutomationAction.SetValue => DoSetValue(peer, value),
                UiAutomationAction.Expand   => DoExpand(peer, expand: true),
                UiAutomationAction.Collapse => DoExpand(peer, expand: false),
                UiAutomationAction.Select   => DoSelect(peer),
                _                           => DoAuto(peer, value),
            };
        }
        catch (Exception ex)
        {
            return UiAutomationResult.Failure($"Automation action failed: {ex.Message}");
        }
    }

    private static UiAutomationResult DoAuto(AutomationPeer peer, string? value)
    {
        if (value is not null && peer.GetProvider<IValueProvider>() is { IsReadOnly: false })
            return DoSetValue(peer, value);

        if (peer.GetProvider<IInvokeProvider>() is { } invoke)
        {
            invoke.Invoke();
            return UiAutomationResult.Success("Invoke");
        }

        if (peer.GetProvider<IToggleProvider>() is { } toggle)
        {
            toggle.Toggle();
            return UiAutomationResult.Success("Toggle", toggle.ToggleState.ToString());
        }

        if (peer.GetProvider<IExpandCollapseProvider>() is { } ec)
        {
            var expand = ec.ExpandCollapseState != global::Avalonia.Automation.ExpandCollapseState.Expanded;
            if (expand) ec.Expand(); else ec.Collapse();
            return UiAutomationResult.Success(expand ? "Expand" : "Collapse", ec.ExpandCollapseState.ToString());
        }

        if (peer.GetProvider<ISelectionItemProvider>() is { } sel)
        {
            sel.Select();
            return UiAutomationResult.Success("Select", $"isSelected={sel.IsSelected}");
        }

        if (peer.GetProvider<IValueProvider>() is { IsReadOnly: false } valNoArg)
            return UiAutomationResult.Failure(
                $"Control only supports the Value pattern; provide a 'value' to set (current: {valNoArg.Value}).");

        return UiAutomationResult.Failure(
            "Control's automation peer exposes no actionable pattern (Invoke/Toggle/ExpandCollapse/SelectionItem/Value).");
    }

    private static UiAutomationResult DoInvoke(AutomationPeer peer)
    {
        if (peer.GetProvider<IInvokeProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the Invoke pattern.");
        p.Invoke();
        return UiAutomationResult.Success("Invoke");
    }

    private static UiAutomationResult DoToggle(AutomationPeer peer)
    {
        if (peer.GetProvider<IToggleProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the Toggle pattern.");
        p.Toggle();
        return UiAutomationResult.Success("Toggle", p.ToggleState.ToString());
    }

    private static UiAutomationResult DoSetValue(AutomationPeer peer, string? value)
    {
        if (value is null)
            return UiAutomationResult.Failure("A 'value' string is required for SetValue.");
        if (peer.GetProvider<IValueProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the Value pattern.");
        if (p.IsReadOnly)
            return UiAutomationResult.Failure("Control's Value pattern is read-only.");
        p.SetValue(value);
        return UiAutomationResult.Success("SetValue", p.Value);
    }

    private static UiAutomationResult DoExpand(AutomationPeer peer, bool expand)
    {
        if (peer.GetProvider<IExpandCollapseProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the ExpandCollapse pattern.");
        if (expand) p.Expand(); else p.Collapse();
        return UiAutomationResult.Success(expand ? "Expand" : "Collapse", p.ExpandCollapseState.ToString());
    }

    private static UiAutomationResult DoSelect(AutomationPeer peer)
    {
        if (peer.GetProvider<ISelectionItemProvider>() is not { } p)
            return UiAutomationResult.Failure("Control does not support the SelectionItem pattern.");
        p.Select();
        return UiAutomationResult.Success("Select", $"isSelected={p.IsSelected}");
    }

    // ----------------------------------------------------------------- focus

    /// <inheritdoc />
    public bool SetFocus(object element) => element is Control c && c.Focus();

    /// <inheritdoc />
    public object? GetFocusedElement(object topLevel) =>
        (topLevel as TopLevel)?.FocusManager?.GetFocusedElement() as Control;

    // --------------------------------------------------------------- hit-test

    /// <inheritdoc />
    public object? HitTest(object topLevel, UiPoint point) =>
        (topLevel as TopLevel)?.InputHitTest(new Point(point.X, point.Y)) as Control;

    // ----------------------------------------------------- synthetic input

    /// <inheritdoc />
    public object? SendPointer(object topLevel, PointerAction action, UiPoint point)
    {
        if (topLevel is not TopLevel tl)
            return null;

        var p = new Point(point.X, point.Y);
        var target = SyntheticInput.HitTest(tl, p);
        if (target is null)
            return null;

        var button = action == PointerAction.RightClick ? MouseButton.Right : MouseButton.Left;

        switch (action)
        {
            case PointerAction.Move:
                SyntheticInput.RaiseMove(tl, target, p);
                break;
            case PointerAction.Down:
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 1);
                break;
            case PointerAction.Up:
                SyntheticInput.RaiseReleased(tl, target, p, button);
                break;
            case PointerAction.Click:
            case PointerAction.RightClick:
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 1);
                SyntheticInput.RaiseReleased(tl, target, p, button);
                break;
            case PointerAction.DoubleClick:
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 1);
                SyntheticInput.RaiseReleased(tl, target, p, button);
                SyntheticInput.RaisePressed(tl, target, p, button, clickCount: 2);
                SyntheticInput.RaiseReleased(tl, target, p, button);
                break;
        }

        return target;
    }

    /// <inheritdoc />
    public object? SendWheel(object topLevel, UiPoint point, UiVector delta)
    {
        if (topLevel is not TopLevel tl)
            return null;

        var p = new Point(point.X, point.Y);
        var target = SyntheticInput.HitTest(tl, p);
        if (target is null)
            return null;
        SyntheticInput.RaiseWheel(tl, target, p, new Vector(delta.X, delta.Y));
        return target;
    }

    /// <inheritdoc />
    public object? SendText(object? target, string text)
    {
        (target as InputElement)?.Focus(NavigationMethod.Unspecified, KeyModifiers.None);

        var sink = ResolveInputSink(target as Control);
        if (sink is null)
            return null;

        SyntheticInput.RaiseText(sink, text);
        return sink as Control;
    }

    /// <inheritdoc />
    public bool SendKeys(
        object? target, string chords,
        out IReadOnlyList<string> sentChords, out object? sink, out string error)
    {
        sentChords = Array.Empty<string>();
        sink = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(chords))
        {
            error = "keys was empty.";
            return false;
        }

        var parsed = new List<(Key key, KeyModifiers mods, string raw)>();
        foreach (var token in chords.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!SyntheticInput.TryParseChord(token, out var key, out var mods, out var parseError))
            {
                error = $"could not parse key chord '{token}': {parseError}";
                return false;
            }
            parsed.Add((key, mods, token));
        }

        if (parsed.Count == 0)
        {
            error = "no key chords parsed from input.";
            return false;
        }

        (target as InputElement)?.Focus(NavigationMethod.Unspecified, KeyModifiers.None);

        var element = ResolveInputSink(target as Control);
        if (element is null)
        {
            error = "no focused element to receive keys (focus a control via handle/selector first).";
            return false;
        }

        var sent = new List<string>(parsed.Count);
        foreach (var (key, mods, raw) in parsed)
        {
            SyntheticInput.RaiseKey(element, key, mods, down: true);
            SyntheticInput.RaiseKey(element, key, mods, down: false);
            sent.Add(raw);
        }

        sentChords = sent;
        sink = element as Control;
        return true;
    }

    private static IInputElement? ResolveInputSink(Control? explicitTarget)
    {
        if (explicitTarget is IInputElement ie)
            return ie;

        foreach (var root in EnumerateRootsCore().OfType<TopLevel>())
        {
            var focused = root.FocusManager?.GetFocusedElement();
            if (focused is not null)
                return focused;
        }

        return null;
    }

    // ------------------------------------------------------- diagnostics

    /// <inheritdoc />
    public IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled)
    {
        var sink = _bindingErrors ?? BindingErrorSink.Current;
        if (sink is null)
        {
            enabled = false;
            return Array.Empty<string>();
        }

        enabled = true;
        return sink.Recent(count).ToArray();
    }

    // --------------------------------------------------------------- helpers

    private static string Describe(Control control)
    {
        var name = control.Name;
        var typeName = control.GetType().Name;
        return string.IsNullOrEmpty(name) ? typeName : $"{typeName}#{name}";
    }
}
