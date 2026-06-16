using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Keincheck.Core.Tools;

/// <summary>
/// MCP tools that capture PNG screenshots of the live UI and return them as MCP image
/// content (base64 PNG).
/// </summary>
/// <remarks>
/// All framework-specific rendering is performed by <see cref="IUiAdapter"/>
/// (<see cref="IUiAdapter.TryRenderToPng"/>), which runs on the UI thread; the tool
/// bodies only resolve targets (via <see cref="ControlRegistry"/>) and marshal onto
/// the UI thread via <see cref="IUiDispatcher"/>. Each tool returns a
/// <see cref="ContentBlock"/>: an <see cref="ImageContentBlock"/> on success, or a
/// <see cref="TextContentBlock"/> carrying a structured JSON error object on failure
/// (so the convention "never throw raw on a bad handle/selector" holds while still
/// emitting a well-typed MCP content block).
/// </remarks>
[McpServerToolType]
public static class ScreenshotTools
{
    private const string PngMimeType = "image/png";

    /// <summary>
    /// Renders a top-level window to a PNG and returns it as image content.
    /// </summary>
    [McpServerTool, Description(
        "Render a window to a PNG screenshot and return it as image content. " +
        "Optionally target a specific window via a control handle (e.g. \"ctl-1a\") " +
        "or a CSS-ish selector (e.g. \"Window[Name=main]\"); when omitted, the first " +
        "open top-level window is captured. Use scale > 1 to render at a multiple of the " +
        "native size for capturing small windows legibly (clamped 1..16; longest side is " +
        "still bounded by the server's max screenshot dimension).")]
    public static Task<ContentBlock> ScreenshotWindow(
        ControlRegistry registry,
        IUiAdapter ui,
        IUiDispatcher dispatcher,
        McpServerOptions options,
        [Description("Optional control handle or selector identifying a window (or any control inside it). " +
                     "If omitted, the first open top-level window is used.")]
        string? target = null,
        [Description("Render scale multiplier for legible capture of small windows. 1 (default) = native; " +
                     "clamped to 1..16. The longest side is still clamped to the server's max dimension.")]
        double scale = 1)
        => dispatcher.Run(() =>
        {
            // Resolve the TopLevel to render.
            if (!TryResolveTopLevel(registry, ui, target, out var topLevel, out var resolveError))
                return Error(resolveError);

            if (!ui.TryRenderToPng(topLevel!, options.MaxScreenshotDimension, ClampScale(scale), out var png, out var renderError))
                return Error(renderError);

            return Image(png);
        });

    /// <summary>
    /// Renders a single control's subtree to a PNG and returns it as image content.
    /// </summary>
    [McpServerTool, Description(
        "Render a single control (and its descendants) to a PNG screenshot and return " +
        "it as image content. Address the control by handle (e.g. \"ctl-1a\") or by a " +
        "CSS-ish selector (e.g. \"Button[Name=ok]\"); a selector matching multiple " +
        "controls captures the first match in document order. Use scale > 1 to render a " +
        "tiny control at a multiple of its native size so it is legible (clamped 1..16; the " +
        "longest side is still bounded by the server's max screenshot dimension).")]
    public static Task<ContentBlock> ScreenshotControl(
        ControlRegistry registry,
        IUiAdapter ui,
        IUiDispatcher dispatcher,
        McpServerOptions options,
        [Description("Control handle (\"ctl-1a\") or CSS-ish selector (\"Button[Name=ok]\").")]
        string target,
        [Description("Render scale multiplier for legible capture of small controls. 1 (default) = native; " +
                     "clamped to 1..16. The longest side is still clamped to the server's max dimension.")]
        double scale = 1)
        => dispatcher.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(target))
                return Error("A control handle or selector is required.");

            if (!TryResolveControl(registry, ui, target, out var control, out var resolveError))
                return Error(resolveError);

            // The adapter renders the control subtree directly, falling back to a
            // cropped TopLevel render when a direct render is not usable.
            if (!ui.TryRenderToPng(control!, options.MaxScreenshotDimension, ClampScale(scale), out var png, out var renderError))
                return Error(renderError);

            return Image(png);
        });

    // ---- resolution helpers ------------------------------------------------

    private static bool TryResolveControl(
        ControlRegistry registry, IUiAdapter ui, string target, out object? control, out string error)
    {
        // Handle first, then selector (the standard "handle if it resolves, else selector").
        if (registry.TryResolve(target, out control) && control is not null)
        {
            error = string.Empty;
            return true;
        }

        var matches = registry.Query(target, ui);
        if (matches.Count > 0)
        {
            control = matches[0];
            error = string.Empty;
            return true;
        }

        control = null;
        error = $"No control found for handle or selector '{target}'.";
        return false;
    }

    private static bool TryResolveTopLevel(
        ControlRegistry registry, IUiAdapter ui, string? target, out object? topLevel, out string error)
    {
        topLevel = null;
        error = string.Empty;

        if (!string.IsNullOrWhiteSpace(target))
        {
            if (!TryResolveControl(registry, ui, target!, out var control, out error))
                return false;

            // A resolved TopLevel/Window is its own root; otherwise climb to its host.
            topLevel = ui.GetTopLevel(control!) ?? control;
            if (topLevel is null)
            {
                error = $"Control '{Describe(ui, control!)}' is not inside any open window.";
                return false;
            }

            return true;
        }

        // No target: capture the first open top-level window.
        topLevel = ui.EnumerateRoots().FirstOrDefault();
        if (topLevel is null)
        {
            error = "No open top-level window to capture.";
            return false;
        }

        return true;
    }

    // ---- misc --------------------------------------------------------------

    /// <summary>
    /// Clamps a requested render scale into a sane range. 1 (or anything &lt;= 1) keeps the
    /// native-size render; values above 16 are capped so a caller cannot request an
    /// arbitrarily large off-screen surface. NaN/Infinity collapse to 1.
    /// </summary>
    private static double ClampScale(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(scale, 1, 16) : 1;

    private static string Describe(IUiAdapter ui, object control)
    {
        var name = ui.GetName(control);
        var typeName = ui.GetTypeName(control);
        return string.IsNullOrEmpty(name) ? typeName : $"{typeName}#{name}";
    }

    /// <summary>Wraps already-encoded PNG bytes in MCP image content.</summary>
    private static ContentBlock Image(byte[] png) =>
        ImageContentBlock.FromBytes(png, PngMimeType);

    /// <summary>
    /// Produces a structured error as a <see cref="TextContentBlock"/> so the tool's
    /// declared <see cref="ContentBlock"/> return type is honored without throwing.
    /// </summary>
    private static ContentBlock Error(string message) =>
        new TextContentBlock
        {
            Text = JsonSerializer.Serialize(new { ok = false, error = message }),
        };
}
