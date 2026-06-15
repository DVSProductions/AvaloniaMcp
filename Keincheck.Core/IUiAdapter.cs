using System.Text.Json;

namespace Keincheck.Core;

/// <summary>
/// The framework-agnostic seam between the MCP tool set and the live UI toolkit.
/// Every primitive the 22 tools need — enumerating top-levels, walking the
/// logical/visual tree, reading/writing properties, rendering to PNG,
/// UI-Automation invocation, synthetic pointer/key input, hit-testing, focus, and
/// recent binding errors — is expressed here so the tools never call any toolkit
/// directly. The Avalonia implementation lives in <c>Keincheck.Avalonia</c>; a WPF
/// implementation lives in <c>Keincheck.Wpf</c>; a headless/broker backend can supply
/// yet another.
/// </summary>
/// <remarks>
/// <para><b>Element handles.</b> Every element is an opaque <see cref="object"/> the
/// adapter casts internally to its own framework type (an Avalonia <c>Control</c>, a
/// WPF <c>DependencyObject</c>, …). The Core never inspects the concrete type; it only
/// passes handles back to the adapter. This is what keeps Core framework-free.</para>
/// <para><b>Geometry.</b> Bounds/points/vectors are the neutral <see cref="UiRect"/>,
/// <see cref="UiPoint"/>, and <see cref="UiVector"/> structs.</para>
/// <para><b>Threading.</b> Unless explicitly noted, every member touches the live
/// visual tree and therefore MUST be called on the UI thread. Callers marshal via
/// <see cref="IUiDispatcher"/> exactly as the tools do; the adapter does not
/// re-marshal.</para>
/// </remarks>
public interface IUiAdapter
{
    // ---------------------------------------------------------------- topology

    /// <summary>
    /// All open top-level elements of the current application (desktop windows,
    /// popups, or the single-view root). UI-thread only.
    /// </summary>
    IEnumerable<object> EnumerateRoots();

    /// <summary>
    /// The top-level element that hosts <paramref name="element"/>, or <c>null</c> if
    /// it is not attached to one.
    /// </summary>
    object? GetTopLevel(object element);

    /// <summary>
    /// The direct <b>logical</b> children of <paramref name="element"/> that are
    /// themselves controls, in document order.
    /// </summary>
    IEnumerable<object> GetLogicalChildren(object element);

    /// <summary>
    /// The direct <b>visual</b> children of <paramref name="element"/> that are
    /// themselves controls (includes template-generated parts), in document order.
    /// </summary>
    IEnumerable<object> GetVisualChildren(object element);

    // ---------------------------------------------------------------- metadata

    /// <summary>Whether <paramref name="element"/> is a control (a usable, addressable element).</summary>
    bool IsControl(object element);

    /// <summary>The runtime type name of <paramref name="element"/> (e.g. "Button").</summary>
    string GetTypeName(object element);

    /// <summary>
    /// Whether <paramref name="element"/>'s runtime type — or any of its base types —
    /// has the simple name <paramref name="typeName"/> (ordinal). This is the
    /// type-hierarchy predicate the selector grammar uses (so <c>Button</c> matches a
    /// <c>ToggleButton</c>, <c>Control</c> matches any control). The adapter owns the
    /// framework type walk so Core never sees a <see cref="System.Type"/>.
    /// </summary>
    bool MatchesType(object element, string typeName);

    /// <summary>The element's <c>Name</c>, or <c>null</c> when unset/empty.</summary>
    string? GetName(object element);

    /// <summary>The window/popup title if <paramref name="element"/> is a window, else <c>null</c>.</summary>
    string? GetTitle(object element);

    /// <summary>The element's arranged bounds in its parent's coordinate space.</summary>
    UiRect GetBounds(object element);

    /// <summary>Whether the element is effectively visible (and all ancestors are).</summary>
    bool IsEffectivelyVisible(object element);

    /// <summary>Whether the element is effectively enabled (and all ancestors are).</summary>
    bool IsEffectivelyEnabled(object element);

    /// <summary>Whether <paramref name="element"/> is an active window; <c>false</c> for non-window elements.</summary>
    bool IsActiveWindow(object element);

    // -------------------------------------------------------------- properties

    /// <summary>
    /// The names of every styled/attached property registered on
    /// <paramref name="element"/> (the per-framework property set surfaced to
    /// <c>get_properties</c>). May contain duplicates; callers de-duplicate by name.
    /// </summary>
    IEnumerable<string> GetPropertyNames(object element);

    /// <summary>
    /// Reads a styled/CLR property by name as a JSON-friendly value (primitive,
    /// string, or small dictionary/array). Returns <c>false</c> (and a null value)
    /// when missing or unreadable. The adapter owns the framework-value → JSON-friendly
    /// projection.
    /// </summary>
    bool TryReadProperty(object element, string name, out object? jsonFriendlyValue);

    /// <summary>
    /// Coerces and writes <paramref name="value"/> onto the named property. Returns
    /// <c>false</c> with a reason in <paramref name="error"/> on failure; never throws
    /// for ordinary failures. The adapter owns the JSON → framework-value coercion.
    /// </summary>
    bool TryWriteProperty(object element, string name, JsonElement value, out string error);

    /// <summary>
    /// The element's data context (view-model), as an opaque object for the
    /// serializer to project, or <c>null</c> when there is none.
    /// </summary>
    object? GetDataContext(object element);

    // ----------------------------------------------------------------- render

    /// <summary>
    /// Renders <paramref name="element"/> (and its descendants) to PNG bytes, honoring
    /// <paramref name="maxDim"/> (uniform downscale when larger). Returns <c>false</c>
    /// with a reason in <paramref name="error"/> when the element has no renderable
    /// size or rendering fails. Handles both single controls and whole top-levels.
    /// UI-thread only.
    /// </summary>
    bool TryRenderToPng(object element, int maxDim, out byte[] png, out string error);

    // ------------------------------------------------------------- automation

    /// <summary>
    /// Performs a semantic UI-Automation action on <paramref name="element"/>.
    /// <paramref name="action"/> selects the pattern (<see cref="UiAutomationAction.Auto"/>
    /// auto-detects). <paramref name="value"/> is used by <c>SetValue</c> (and
    /// <c>Auto</c> on a value-capable element). Returns a structured outcome.
    /// </summary>
    UiAutomationResult InvokeAutomation(object element, UiAutomationAction action, string? value);

    // ----------------------------------------------------------------- focus

    /// <summary>
    /// Requests keyboard focus for <paramref name="element"/>. Returns whether the
    /// focus request was accepted.
    /// </summary>
    bool SetFocus(object element);

    /// <summary>
    /// The element currently holding keyboard focus in <paramref name="topLevel"/>'s
    /// focus scope, or <c>null</c> if none / not a control.
    /// </summary>
    object? GetFocusedElement(object topLevel);

    // --------------------------------------------------------------- hit-test

    /// <summary>
    /// The deepest control at <paramref name="point"/> (in <paramref name="topLevel"/>
    /// client coordinates), or <c>null</c> if nothing (or a non-control) is hit.
    /// </summary>
    object? HitTest(object topLevel, UiPoint point);

    // ----------------------------------------------------- synthetic input

    /// <summary>
    /// Raises a synthetic pointer gesture at <paramref name="point"/> in
    /// <paramref name="topLevel"/>, hit-testing the target element. Returns the hit
    /// control, or <c>null</c> if nothing was hit. UI-thread only.
    /// </summary>
    object? SendPointer(object topLevel, PointerAction action, UiPoint point);

    /// <summary>
    /// Raises a synthetic mouse-wheel scroll (notches in <paramref name="delta"/>) at
    /// <paramref name="point"/>. Returns the hit control, or <c>null</c>. UI-thread only.
    /// </summary>
    object? SendWheel(object topLevel, UiPoint point, UiVector delta);

    /// <summary>
    /// Routes literal <paramref name="text"/> as text-input to <paramref name="target"/>
    /// if given (focusing it first), else to the current focused element. Returns the
    /// element that received the text, or <c>null</c> if there was no sink. UI-thread only.
    /// </summary>
    object? SendText(object? target, string text);

    /// <summary>
    /// Sends one or more key chords (e.g. <c>"Ctrl+S"</c>, <c>"Enter"</c>) to
    /// <paramref name="target"/> if given (focusing it first), else to the focused
    /// element. On a parse failure returns <c>false</c> with a reason in
    /// <paramref name="error"/>; on success <paramref name="sentChords"/> lists the
    /// accepted chords and <paramref name="sink"/> is the element that received them.
    /// UI-thread only.
    /// </summary>
    bool SendKeys(object? target, string chords, out IReadOnlyList<string> sentChords, out object? sink, out string error);

    // ------------------------------------------------------- diagnostics

    /// <summary>
    /// Up to <paramref name="count"/> most-recent captured binding errors (oldest
    /// first); <paramref name="count"/> &lt;= 0 returns all buffered. Returns an empty
    /// sequence (and <paramref name="enabled"/> = false) when binding-error capture is
    /// disabled. Thread-safe.
    /// </summary>
    IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled);
}

/// <summary>The semantic UI-Automation action requested via <see cref="IUiAdapter.InvokeAutomation"/>.</summary>
public enum UiAutomationAction
{
    /// <summary>Auto-detect the right pattern from the control's peer.</summary>
    Auto = 0,
    /// <summary>Invoke (push) the control.</summary>
    Invoke,
    /// <summary>Toggle the control.</summary>
    Toggle,
    /// <summary>Set the control's text/value (requires a value).</summary>
    SetValue,
    /// <summary>Expand the control.</summary>
    Expand,
    /// <summary>Collapse the control.</summary>
    Collapse,
    /// <summary>Select the item.</summary>
    Select,
}

/// <summary>The synthetic pointer gesture requested via <see cref="IUiAdapter.SendPointer"/>.</summary>
public enum PointerAction
{
    /// <summary>Move the pointer (no buttons).</summary>
    Move = 0,
    /// <summary>Left button down.</summary>
    Down,
    /// <summary>Left button up.</summary>
    Up,
    /// <summary>Left click (down + up).</summary>
    Click,
    /// <summary>Left double-click.</summary>
    DoubleClick,
    /// <summary>Right click (down + up).</summary>
    RightClick,
}

/// <summary>
/// The structured outcome of <see cref="IUiAdapter.InvokeAutomation"/>: whether it
/// succeeded, the pattern that was used, an optional human-readable state string,
/// and an error message on failure.
/// </summary>
public readonly record struct UiAutomationResult(
    bool Ok,
    string? Action,
    string? State,
    string? Error)
{
    /// <summary>A successful result for <paramref name="action"/> with optional <paramref name="state"/>.</summary>
    public static UiAutomationResult Success(string action, string? state = null) =>
        new(true, action, state, null);

    /// <summary>A failed result carrying <paramref name="error"/>.</summary>
    public static UiAutomationResult Failure(string error) =>
        new(false, null, null, error);
}
