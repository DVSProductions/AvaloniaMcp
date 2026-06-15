using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Input;
using System.Windows.Media;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// Automation + focus + hit-test group of <see cref="WpfUiAdapter"/>: invoking a semantic
/// UI-Automation pattern on an element, requesting/reading keyboard focus, and hit-testing
/// a point to the deepest control.
/// <para>
/// <b>Stage B (System.Windows.*):</b> the WPF analog of the Avalonia adapter's automation
/// block. <see cref="InvokeAutomation"/> creates a peer via
/// <see cref="UIElementAutomationPeer.CreatePeerForElement(UIElement)"/>, resolves the
/// requested pattern through <c>GetPattern(PatternInterface.*)</c>, casts to the matching
/// <c>System.Windows.Automation.Provider.I*Provider</c>, and drives it — mapping the result
/// (and the <see cref="UiAutomationAction.Auto"/> auto-detect order) to
/// <see cref="UiAutomationResult"/> exactly like <c>AvaloniaUiAdapter</c>.
/// <see cref="SetFocus"/> is <c>(UIElement).Focus()</c>; <see cref="GetFocusedElement"/>
/// reads <see cref="Keyboard.FocusedElement"/> scoped to the requested top-level;
/// <see cref="HitTest"/> uses <see cref="VisualTreeHelper.HitTest(Visual, Point)"/> and
/// walks up to the nearest control.
/// </para>
/// </summary>
public sealed partial class WpfUiAdapter
{
    // ------------------------------------------------------------- automation

    /// <inheritdoc />
    public UiAutomationResult InvokeAutomation(object element, UiAutomationAction action, string? value)
    {
        // CreatePeerForElement requires a UIElement; mirror the Avalonia adapter's
        // "Target is not a control." / "No automation peer" failure messages so the
        // automation_action tool output is identical across frameworks.
        if (element is not UIElement uiElement)
            return UiAutomationResult.Failure("Target is not a control.");

        // Controls that deliberately expose no peer (e.g. the demo GaugeControl returning
        // null! from OnCreateAutomationPeer) land here and force the synthetic-input path.
        var peer = UIElementAutomationPeer.CreatePeerForElement(uiElement);
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

    /// <summary>
    /// Resolves an automation pattern provider from a WPF peer. <c>GetPattern</c> returns
    /// the provider as a boxed <see cref="object"/>; cast it to the strongly-typed provider
    /// interface (the WPF equivalent of Avalonia's <c>AutomationPeer.GetProvider&lt;T&gt;</c>).
    /// </summary>
    private static T? GetProvider<T>(AutomationPeer peer, PatternInterface pattern) where T : class =>
        peer.GetPattern(pattern) as T;

    private static UiAutomationResult DoAuto(AutomationPeer peer, string? value)
    {
        // Same precedence as the Avalonia adapter: a supplied value targets a writable
        // Value pattern first, then Invoke > Toggle > ExpandCollapse > SelectionItem, then
        // a value-only control reports it needs an argument, else "no actionable pattern".
        if (value is not null && GetProvider<IValueProvider>(peer, PatternInterface.Value) is { IsReadOnly: false })
            return DoSetValue(peer, value);

        if (GetProvider<IInvokeProvider>(peer, PatternInterface.Invoke) is { } invoke)
        {
            invoke.Invoke();
            return UiAutomationResult.Success("Invoke");
        }

        if (GetProvider<IToggleProvider>(peer, PatternInterface.Toggle) is { } toggle)
        {
            toggle.Toggle();
            return UiAutomationResult.Success("Toggle", toggle.ToggleState.ToString());
        }

        if (GetProvider<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse) is { } ec)
        {
            var expand = ec.ExpandCollapseState != ExpandCollapseState.Expanded;
            if (expand) ec.Expand(); else ec.Collapse();
            return UiAutomationResult.Success(expand ? "Expand" : "Collapse", ec.ExpandCollapseState.ToString());
        }

        if (GetProvider<ISelectionItemProvider>(peer, PatternInterface.SelectionItem) is { } sel)
        {
            sel.Select();
            return UiAutomationResult.Success("Select", $"isSelected={sel.IsSelected}");
        }

        if (GetProvider<IValueProvider>(peer, PatternInterface.Value) is { IsReadOnly: false } valNoArg)
            return UiAutomationResult.Failure(
                $"Control only supports the Value pattern; provide a 'value' to set (current: {valNoArg.Value}).");

        return UiAutomationResult.Failure(
            "Control's automation peer exposes no actionable pattern (Invoke/Toggle/ExpandCollapse/SelectionItem/Value).");
    }

    private static UiAutomationResult DoInvoke(AutomationPeer peer)
    {
        if (GetProvider<IInvokeProvider>(peer, PatternInterface.Invoke) is not { } p)
            return UiAutomationResult.Failure("Control does not support the Invoke pattern.");
        p.Invoke();
        return UiAutomationResult.Success("Invoke");
    }

    private static UiAutomationResult DoToggle(AutomationPeer peer)
    {
        if (GetProvider<IToggleProvider>(peer, PatternInterface.Toggle) is not { } p)
            return UiAutomationResult.Failure("Control does not support the Toggle pattern.");
        p.Toggle();
        return UiAutomationResult.Success("Toggle", p.ToggleState.ToString());
    }

    private static UiAutomationResult DoSetValue(AutomationPeer peer, string? value)
    {
        if (value is null)
            return UiAutomationResult.Failure("A 'value' string is required for SetValue.");
        if (GetProvider<IValueProvider>(peer, PatternInterface.Value) is not { } p)
            return UiAutomationResult.Failure("Control does not support the Value pattern.");
        if (p.IsReadOnly)
            return UiAutomationResult.Failure("Control's Value pattern is read-only.");
        p.SetValue(value);
        return UiAutomationResult.Success("SetValue", p.Value);
    }

    private static UiAutomationResult DoExpand(AutomationPeer peer, bool expand)
    {
        if (GetProvider<IExpandCollapseProvider>(peer, PatternInterface.ExpandCollapse) is not { } p)
            return UiAutomationResult.Failure("Control does not support the ExpandCollapse pattern.");
        if (expand) p.Expand(); else p.Collapse();
        return UiAutomationResult.Success(expand ? "Expand" : "Collapse", p.ExpandCollapseState.ToString());
    }

    private static UiAutomationResult DoSelect(AutomationPeer peer)
    {
        if (GetProvider<ISelectionItemProvider>(peer, PatternInterface.SelectionItem) is not { } p)
            return UiAutomationResult.Failure("Control does not support the SelectionItem pattern.");
        p.Select();
        return UiAutomationResult.Success("Select", $"isSelected={p.IsSelected}");
    }

    // ----------------------------------------------------------------- focus

    /// <inheritdoc />
    public bool SetFocus(object element) => element is UIElement ui && ui.Focus();

    /// <inheritdoc />
    public object? GetFocusedElement(object topLevel)
    {
        // WPF keyboard focus is application-global; scope it to the requested top-level so
        // the result mirrors Avalonia's per-TopLevel FocusManager query. Keyboard focus
        // tracks the keyboard input device; Keyboard.FocusedElement is its current target.
        if (Keyboard.FocusedElement is not DependencyObject focused)
            return null;

        // Restrict to a FrameworkElement (the IsControl boundary) so the focused handle is
        // addressable by the inspection tools (registry.Assign / GetName / GetBounds).
        if (focused is not FrameworkElement fe)
            return null;

        // Only return the focused element if it actually lives in the requested window;
        // otherwise the caller's top-level had nothing focused (Avalonia returns null too).
        if (topLevel is Window window && Window.GetWindow(fe) is { } owner && !ReferenceEquals(owner, window))
            return null;

        return fe;
    }

    // --------------------------------------------------------------- hit-test

    /// <inheritdoc />
    public object? HitTest(object topLevel, UiPoint point)
    {
        if (topLevel is not Visual root)
            return null;

        var result = VisualTreeHelper.HitTest(root, new Point(point.X, point.Y));
        if (result?.VisualHit is not DependencyObject hit)
            return null;

        // VisualTreeHelper hits the deepest visual (often a template part / drawing primitive);
        // walk up the visual tree to the nearest control, mirroring the Avalonia adapter which
        // returns the InputHitTest result only when it is a control.
        return NearestControl(hit);
    }

    /// <summary>
    /// Walks up the visual tree from <paramref name="origin"/> to the first
    /// <see cref="FrameworkElement"/> (the <c>IsControl</c> boundary), so hit-test /
    /// focus results are addressable handles. Returns <c>null</c> if none is found.
    /// </summary>
    private static FrameworkElement? NearestControl(DependencyObject origin)
    {
        for (var node = origin; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement fe)
                return fe;
        }
        return null;
    }
}
