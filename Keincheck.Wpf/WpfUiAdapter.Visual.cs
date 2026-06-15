using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Keincheck.Core;

namespace Keincheck.Wpf;

/// <summary>
/// Visual + diagnostics group of <see cref="WpfUiAdapter"/>: rendering an element subtree
/// (or a whole window) to PNG bytes, and surfacing recent WPF binding errors captured by
/// a trace sink.
/// <para>
/// <b>Stage B map (System.Windows.*):</b>
/// <list type="bullet">
///   <item><see cref="TryRenderToPng"/> → <c>System.Windows.Media.Imaging.RenderTargetBitmap</c> (96 dpi, <c>PixelFormats.Pbgra32</c>) → <c>PngBitmapEncoder</c> → <c>byte[]</c>; window (whole visual) vs control (subtree); clamp to <c>maxDim</c> like the Avalonia adapter's <c>ClampToPixels</c>.</item>
///   <item><see cref="GetRecentBindingErrors"/> → capture <c>PresentationTraceSources.DataBindingSource</c> via a custom <see cref="WpfBindingErrorSink"/> <c>TraceListener</c> ring buffer (installed by the adapter, mirroring <c>Keincheck.Avalonia.BindingErrorSink</c>); return oldest-first, <c>count &lt;= 0</c> ⇒ all.</item>
/// </list>
/// The binding-error <c>TraceListener</c> and its ring buffer (<see cref="WpfBindingErrorSink"/>)
/// live in this file — the WPF analog of <c>Keincheck.Avalonia.BindingErrorSink</c>.
/// </para>
/// </summary>
public sealed partial class WpfUiAdapter
{
    // ----------------------------------------------------------------- render

    /// <inheritdoc />
    public bool TryRenderToPng(object element, int maxDim, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;

        // A Window renders as a whole visual; any other FrameworkElement renders its own
        // subtree (with a window-crop fallback). This mirrors the Avalonia adapter's
        // TopLevel-vs-Control split behind one neutral method.
        if (element is Window window)
            return TryRenderWindowToPng(window, maxDim, out png, out error);
        if (element is FrameworkElement fe)
            return TryRenderControlToPng(fe, maxDim, out png, out error);

        error = "Target is not a renderable visual.";
        return false;
    }

    /// <summary>
    /// Renders a live <see cref="Window"/> to PNG. WPF's <c>RenderTargetBitmap.Render</c>
    /// applied to the <see cref="Window"/> object itself produces a BLANK bitmap — the
    /// Window's own visual sits at the HWND/airspace boundary and its retained content is
    /// hosted by its first visual child. So we render the window's content visual child
    /// (the client-area root, e.g. the AdornerDecorator/Border WPF inserts), which contains
    /// the actual on-screen UI. Falls back to the window itself if it has no visual child.
    /// </summary>
    private bool TryRenderWindowToPng(Window window, int maxDim, out byte[] png, out string error)
    {
        EnsureArranged(window);
        var contentRoot = WindowContentVisual(window);
        if (contentRoot is not null)
            return TryRenderControlToPng(contentRoot, maxDim, out png, out error);

        // No content child (degenerate/empty window): render the window visual directly.
        return TryRenderVisualToPng(window, maxDim, cropRect: null, out png, out error);
    }

    /// <summary>
    /// Returns the window's content-hosting visual child — the first
    /// <see cref="FrameworkElement"/> under the <see cref="Window"/> in the visual tree
    /// (WPF wraps window content in a Border/AdornerDecorator). This is the visual that
    /// <see cref="RenderTargetBitmap"/> can actually rasterize, unlike the Window itself.
    /// </summary>
    private static FrameworkElement? WindowContentVisual(Window window)
    {
        if (VisualTreeHelper.GetChildrenCount(window) > 0 &&
            VisualTreeHelper.GetChild(window, 0) is FrameworkElement child)
            return child;
        // Fall back to the logical Content if it is itself a FrameworkElement.
        return window.Content as FrameworkElement;
    }

    private bool TryRenderControlToPng(FrameworkElement element, int maxDimension, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;
        var max = maxDimension > 0 ? maxDimension : _defaultMaxDimension;

        EnsureArranged(element);

        var size = element.RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            error = $"Control '{Describe(element)}' has no renderable size " +
                    $"({size.Width}x{size.Height}); it may not be laid out or visible.";
            return false;
        }

        var (pixelW, pixelH, scale) = ClampToPixels(size.Width, size.Height, max);

        // Primary path: render the element subtree directly into a DPI-scaled RTB. WPF
        // renders a FrameworkElement's own visual subtree from its (0,0) origin, so no
        // extra offset is needed; the DPI bakes in the downscale (mirrors Avalonia).
        try
        {
            return EncodeVerified(RenderVisual(element, pixelW, pixelH, scale, cropPixels: null), out png, out error);
        }
        catch (Exception ex)
        {
            // Fallback: render the whole window and crop to the element's bounds in
            // window coordinates (mirrors the Avalonia TransformToVisual crop path).
            var window = Window.GetWindow(element);
            if (window is null)
            {
                error = $"Direct render of '{Describe(element)}' failed ({ex.Message}) and the control has no Window to crop from.";
                return false;
            }

            GeneralTransform toRoot;
            try
            {
                toRoot = element.TransformToAncestor(window);
            }
            catch (Exception mapEx)
            {
                error = $"Direct render of '{Describe(element)}' failed and its bounds could not be mapped to the window ({mapEx.Message}).";
                return false;
            }

            var cropRect = toRoot.TransformBounds(new Rect(size));
            return TryRenderVisualToPng(window, max, cropRect, out png, out error);
        }
    }

    private bool TryRenderVisualToPng(Window window, int maxDimension, Rect? cropRect, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;
        var max = maxDimension > 0 ? maxDimension : _defaultMaxDimension;

        EnsureArranged(window);

        var fullSize = window.RenderSize;
        if (fullSize.Width <= 0 || fullSize.Height <= 0)
        {
            error = $"The visual has no renderable size ({fullSize.Width}x{fullSize.Height}).";
            return false;
        }

        if (cropRect is not { } crop)
        {
            var (pw, ph, s) = ClampToPixels(fullSize.Width, fullSize.Height, max);
            return EncodeVerified(RenderVisual(window, pw, ph, s, cropPixels: null), out png, out error);
        }

        var clamped = Rect.Intersect(crop, new Rect(fullSize));
        if (clamped.IsEmpty || clamped.Width <= 0 || clamped.Height <= 0)
        {
            error = "The crop region is empty after clamping to the visual.";
            return false;
        }

        var (cropW, cropH, cropScale) = ClampToPixels(clamped.Width, clamped.Height, max);

        // Render the WHOLE window at the crop's scale, then cut out the crop region in
        // pixels. The crop rect is in the window's DIP space; at cropScale a DIP maps to
        // cropScale device pixels, so the pixel-space crop origin/size are the DIP values
        // times the scale, clamped to the rendered bitmap.
        var cropPx = new Int32Rect(
            (int)Math.Round(clamped.X * cropScale),
            (int)Math.Round(clamped.Y * cropScale),
            cropW,
            cropH);
        return EncodeVerified(RenderVisual(window, 0, 0, cropScale, cropPx), out png, out error);
    }

    /// <summary>
    /// Renders <paramref name="source"/> (a live or detached visual) DIRECTLY into a
    /// <see cref="RenderTargetBitmap"/> whose DPI encodes the uniform <paramref name="scale"/>
    /// (96·scale), exactly like the Avalonia adapter bakes scale into the RTB's DPI vector.
    /// When <paramref name="cropPixels"/> is supplied the full render is cut to that pixel
    /// rectangle via a <see cref="CroppedBitmap"/> (a static, freezable image — never a live
    /// VisualBrush, which renders blank for an already-parented window). Returns the rendered
    /// <see cref="BitmapSource"/>; encoding + non-blank verification happen in <see cref="EncodeVerified"/>.
    /// </summary>
    private static BitmapSource RenderVisual(Visual source, int pixelW, int pixelH, double scale, Int32Rect? cropPixels)
    {
        // Bake the scale into DPI (96·scale) and render the visual itself. RenderTargetBitmap
        // renders the retained visual tree of a live window correctly; wrapping the live
        // window in a VisualBrush does not (it produces a blank bitmap), which was the bug.
        var dpi = 96.0 * scale;

        if (cropPixels is { } cp)
        {
            // Full-window render at the crop scale, sized to the window's own bounds.
            var fe = source as FrameworkElement;
            var fullW = (int)Math.Max(1, Math.Round((fe?.RenderSize.Width ?? 0) * scale));
            var fullH = (int)Math.Max(1, Math.Round((fe?.RenderSize.Height ?? 0) * scale));
            var fullRtb = new RenderTargetBitmap(fullW, fullH, dpi, dpi, PixelFormats.Pbgra32);
            fullRtb.Render(source);
            fullRtb.Freeze();

            // Clamp the crop to the rendered bounds so an off-by-one never throws.
            var cx = Math.Max(0, Math.Min(cp.X, fullW - 1));
            var cy = Math.Max(0, Math.Min(cp.Y, fullH - 1));
            var cw = Math.Max(1, Math.Min(cp.Width, fullW - cx));
            var ch = Math.Max(1, Math.Min(cp.Height, fullH - cy));
            return new CroppedBitmap(fullRtb, new Int32Rect(cx, cy, cw, ch));
        }

        var rtb = new RenderTargetBitmap(pixelW, pixelH, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(source);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// PNG-encodes a rendered bitmap, but first VERIFIES it is not fully blank
    /// (every pixel transparent). WPF's <see cref="RenderTargetBitmap"/> silently produces an
    /// all-transparent bitmap when the process is stuck on the Tier-0 software rasterizer with
    /// no working MIL render target (headless / no-GPU-access / certain RDP &amp; service
    /// sessions). Returning a deceptively "valid" blank PNG would let a caller believe a
    /// capture succeeded when it shows nothing; instead we fail with an explicit, actionable
    /// error so the limitation is visible rather than silent. A genuinely rendered UI always
    /// has at least one non-transparent pixel.
    /// </summary>
    private static bool EncodeVerified(BitmapSource rendered, out byte[] png, out string error)
    {
        png = Array.Empty<byte>();
        error = string.Empty;

        if (IsFullyTransparent(rendered))
        {
            error =
                "Render produced a fully transparent bitmap. WPF's RenderTargetBitmap is not " +
                $"functional in this process (RenderCapability.Tier={RenderCapability.Tier >> 16}); " +
                "this happens when WPF falls back to the Tier-0 software rasterizer with no working " +
                "MIL render target (headless / no GPU access / certain remote or service sessions). " +
                "The visual tree, properties, automation and synthetic-input tools are unaffected.";
            return false;
        }

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        png = ms.ToArray();
        return true;
    }

    /// <summary>
    /// True if every pixel of <paramref name="source"/> is fully transparent (alpha == 0).
    /// Copies the Pbgra32 pixels once and scans the alpha byte of each.
    /// </summary>
    private static bool IsFullyTransparent(BitmapSource source)
    {
        // Force Bgra32 so the alpha byte is at a known offset (index 3 of each 4-byte pixel).
        var bmp = source.Format == PixelFormats.Pbgra32 || source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        var w = bmp.PixelWidth;
        var h = bmp.PixelHeight;
        if (w <= 0 || h <= 0)
            return true;

        var stride = w * 4;
        var pixels = new byte[h * stride];
        bmp.CopyPixels(pixels, stride, 0);
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0)
                return false;
        return true;
    }

    /// <summary>
    /// Forces a measure + arrange pass so the element has a non-zero <c>RenderSize</c>
    /// even if it was just created or is detached from a live visual tree. No-op for an
    /// already-arranged element with a valid layout.
    /// </summary>
    private static void EnsureArranged(FrameworkElement element)
    {
        // If WPF already produced a render size, the element is laid out — leave it alone
        // so we never disturb a live window's real arrangement.
        if (element.RenderSize is { Width: > 0, Height: > 0 })
            return;

        var desired = element.DesiredSize;
        if (desired.Width <= 0 || desired.Height <= 0)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            desired = element.DesiredSize;
        }

        if (desired.Width > 0 && desired.Height > 0)
            element.Arrange(new Rect(desired));

        element.UpdateLayout();
    }

    /// <summary>
    /// Computes pixel dimensions + uniform downscale for <paramref name="dipWidth"/>×
    /// <paramref name="dipHeight"/> DIPs, clamping the largest side to <paramref name="max"/>.
    /// Ported verbatim from the Avalonia adapter's <c>ClampToPixels</c> so PNG sizes match.
    /// </summary>
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

    private static string Describe(FrameworkElement element)
    {
        var name = element.Name;
        var typeName = element.GetType().Name;
        return string.IsNullOrEmpty(name) ? typeName : $"{typeName}#{name}";
    }

    // ------------------------------------------------------------- diagnostics

    /// <inheritdoc />
    public IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled)
    {
        // Mirror the Avalonia adapter: prefer the sink the adapter installed, else fall
        // back to any process-wide sink another component installed. Lazily install one on
        // first read so get_binding_errors works even when the host wiring hasn't yet been
        // taught to opt in (the WPF UseKeincheckClient currently builds new WpfUiAdapter()).
        var sink = WpfBindingErrorSink.Current ?? WpfBindingErrorSink.Install();
        if (sink is null)
        {
            enabled = false;
            return Array.Empty<string>();
        }

        enabled = true;
        return sink.Recent(count).ToArray();
    }
}

/// <summary>
/// A <see cref="TraceListener"/> that captures WPF data-binding trace messages
/// (<c>PresentationTraceSources.DataBindingSource</c>) into a bounded in-memory ring
/// buffer. The WPF analog of <c>Keincheck.Avalonia.BindingErrorSink</c>: install once at
/// startup (or lazily on first read) with <see cref="Install"/>, which raises the source's
/// switch to <c>Warning</c> so binding failures are emitted, and registers this listener.
/// Thread-safe.
/// </summary>
public sealed class WpfBindingErrorSink : TraceListener
{
    private readonly object _gate = new();
    private readonly string[] _ring;
    private int _head;
    private int _count;
    // WPF emits one binding failure as several Write/WriteLine fragments terminated by a
    // WriteLine; coalesce fragments until the line is flushed so each ring entry is one
    // complete message (matching the Avalonia sink's one-line-per-event shape).
    private readonly System.Text.StringBuilder _pending = new();

    /// <param name="capacity">Ring buffer size (number of retained messages).</param>
    public WpfBindingErrorSink(int capacity = 256)
    {
        _ring = new string[Math.Max(1, capacity)];
    }

    /// <summary>The currently-installed sink, if installation happened via <see cref="Install"/>.</summary>
    public static WpfBindingErrorSink? Current { get; private set; }

    /// <summary>
    /// Installs a sink as a listener on <c>PresentationTraceSources.DataBindingSource</c>,
    /// raising the source switch to <c>Warning</c> so binding errors are emitted. Idempotent:
    /// returns the existing <see cref="Current"/> sink if one is already installed.
    /// </summary>
    public static WpfBindingErrorSink Install(int capacity = 256)
    {
        lock (InstallGate)
        {
            if (Current is { } existing)
                return existing;

            // Ensure the trace source actually emits binding diagnostics. Without this the
            // DataBindingSource switch defaults to Off and no messages reach the listener.
            PresentationTraceSources.Refresh();
            var source = PresentationTraceSources.DataBindingSource;
            source.Switch.Level = SourceLevels.Warning;

            var sink = new WpfBindingErrorSink(capacity);
            source.Listeners.Add(sink);
            // Don't let the binding source also spam the default trace output noise; the
            // adapter is the consumer now. (Leave any pre-existing DefaultTraceListener.)
            Current = sink;
            return sink;
        }
    }

    private static readonly object InstallGate = new();

    /// <summary>
    /// Removes this listener from <c>PresentationTraceSources.DataBindingSource</c>,
    /// undoing <see cref="Install"/>. Safe to call even if this sink is not current.
    /// </summary>
    public void Uninstall()
    {
        lock (InstallGate)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            if (ReferenceEquals(Current, this))
                Current = null;
        }
    }

    /// <inheritdoc />
    public override void Write(string? message)
    {
        if (message is null)
            return;
        lock (_gate)
            _pending.Append(message);
    }

    /// <inheritdoc />
    public override void WriteLine(string? message)
    {
        lock (_gate)
        {
            if (message is not null)
                _pending.Append(message);
            Flush(_pending.ToString());
            _pending.Clear();
        }
    }

    /// <summary>
    /// Returns up to <paramref name="n"/> most-recent captured messages, oldest first.
    /// Pass a non-positive <paramref name="n"/> to get all buffered.
    /// </summary>
    public IEnumerable<string> Recent(int n)
    {
        lock (_gate)
        {
            var take = n <= 0 ? _count : Math.Min(n, _count);
            var result = new string[take];
            // Stored oldest..newest across a circular buffer; newest at (head-1). Take the
            // last `take`, oldest first.
            var start = (_head - take + _ring.Length * 2) % _ring.Length;
            for (var i = 0; i < take; i++)
                result[i] = _ring[(start + i) % _ring.Length];
            return result;
        }
    }

    /// <summary>Clears the ring buffer.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
            _pending.Clear();
        }
    }

    // _gate is already held by callers.
    private void Flush(string raw)
    {
        var rendered = raw.Trim();
        if (rendered.Length == 0)
            return;

        var line = string.Create(CultureInfo.InvariantCulture,
            $"{DateTime.UtcNow:O} [Warning] Binding: {rendered}");

        _ring[_head] = line;
        _head = (_head + 1) % _ring.Length;
        if (_count < _ring.Length)
            _count++;
    }
}
