# Keincheck — WPF Stage-A Contracts (neutral seam)

Ground truth for the Stage-A fan-out and Stage B. Every signature below is copied
verbatim from the **built, green** solution (108 tests pass / 1 skip). Code against
these exact symbols. After this refactor `Keincheck.Core` is **framework-FREE** (no
Avalonia, no WPF); all UI-toolkit knowledge lives in the framework adapter packages.

---

## Package layout (after the neutral-seam refactor)

| Assembly                | TFM             | References                                                                 | Role |
|-------------------------|-----------------|----------------------------------------------------------------------------|------|
| **Keincheck.Protocol**  | net8.0          | none (BCL only)                                                            | wire contract |
| **Keincheck.Core**      | net8.0          | ModelContextProtocol (core), → Protocol. **No Avalonia. No WPF.**          | framework-FREE engine: registry, selectors, serializer, the 22 tools, the neutral `IUiAdapter`/`IUiDispatcher`/geometry |
| **Keincheck.Avalonia**  | net8.0          | Avalonia 12.0.4, → Core, → Client                                          | Avalonia impl: `AvaloniaUiAdapter`, `AvaloniaUiDispatcher`, `BindingErrorSink`, `SyntheticInput`, the Avalonia `UseMcpClient` |
| **Keincheck.Client**    | net8.0          | → Core, → Protocol (+ MS.DI). **No Avalonia.**                             | framework-FREE broker client: `BrokerClientHost.Start`, `BrokerClient`, `ClientToolHost`, `McpClientOptions` |
| **Keincheck**           | net8.0          | ModelContextProtocol.AspNetCore, FrameworkReference AspNetCore, → Core, → Avalonia | embedded Kestrel host; `UseMcpServer` injects Avalonia adapter + dispatcher |
| **Keincheck.Wpf**       | net8.0-windows  | `<UseWPF>true</UseWPF>`, `<RollForward>Major</RollForward>`, → Core, → Client | **Stage A scaffold**: `WpfUiAdapter` (stubs), `WpfUiDispatcher` (real), `UseKeincheckClient` |
| **Keincheck.Hub / .Connect** | net8.0     | unchanged                                                                  | framework-agnostic |

- net10 consumers reference net8 libs via `<RollForward>Major</RollForward>`.
- The public call shapes are unchanged: embedded `UseMcpServer` (namespace `Keincheck`),
  Avalonia `UseMcpClient` (now namespace **`Keincheck.Avalonia`** — consumers change the
  `using` and reference `Keincheck.Avalonia`).
- **Namespace shadowing gotcha:** inside namespace `Keincheck.Avalonia` (and any file in
  namespace `Keincheck` that `using`s it), a bare `Avalonia.X` resolves to
  `Keincheck.Avalonia.X`. Qualify framework types as `global::Avalonia.X`.

---

## 1. Neutral geometry (`Keincheck.Core`)

```csharp
namespace Keincheck.Core;

public readonly record struct UiRect(double X, double Y, double Width, double Height)
{
    public double Right  => X + Width;
    public double Bottom => Y + Height;
    public static UiRect Empty => default;
}

public readonly record struct UiPoint(double X, double Y);
public readonly record struct UiVector(double X, double Y);
```

## 2. `IUiDispatcher` (`Keincheck.Core`)

Replaces the old static `UiDispatch`. A DI singleton; the framework package supplies the
implementation (Avalonia: `Dispatcher.UIThread`; WPF: `Application.Current.Dispatcher`).
Implementations MUST execute synchronously when already on the UI thread.

```csharp
namespace Keincheck.Core;

public interface IUiDispatcher
{
    Task<T> Run<T>(Func<T> fn);
    Task    Run(Action action);
    Task<T> RunAsync<T>(Func<Task<T>> fn);
    Task    RunAsync(Func<Task> fn);
}
```

## 3. Neutral `IUiAdapter` (`Keincheck.Core`)

The single framework seam. **Element handles are opaque `object`** the adapter casts to
its own framework type internally; Core never inspects the concrete type. Geometry uses
the neutral structs. Property access is by string name; the adapter owns ALL
framework-value ↔ JSON-friendly conversion (read) and JSON ↔ framework coercion (write).
**Threading:** every member is UI-thread-affine — callers marshal via `IUiDispatcher`.

```csharp
namespace Keincheck.Core;

public interface IUiAdapter
{
    // -- topology --
    IEnumerable<object> EnumerateRoots();
    object? GetTopLevel(object element);
    IEnumerable<object> GetLogicalChildren(object element);   // ALL child elements (incl. non-controls); consumers filter via IsControl
    IEnumerable<object> GetVisualChildren(object element);    // ALL child elements (incl. template parts)

    // -- metadata --
    bool    IsControl(object element);
    string  GetTypeName(object element);
    bool    MatchesType(object element, string typeName);     // runtime type OR any base type name == typeName (ordinal)
    string? GetName(object element);
    string? GetTitle(object element);                         // window title or null
    UiRect  GetBounds(object element);
    bool    IsEffectivelyVisible(object element);
    bool    IsEffectivelyEnabled(object element);
    bool    IsActiveWindow(object element);

    // -- properties --
    IEnumerable<string> GetPropertyNames(object element);     // registered styled/attached property names (may repeat; callers de-dup)
    bool    TryReadProperty(object element, string name, out object? jsonFriendlyValue);   // prefer styled-by-name, then CLR
    bool    TryWriteProperty(object element, string name, JsonElement value, out string error);
    object? GetDataContext(object element);

    // -- render to PNG (UI thread) --
    bool TryRenderToPng(object element, int maxDim, out byte[] png, out string error);     // window => whole visual; control => subtree (+ cropped-TopLevel fallback)

    // -- UI automation --
    UiAutomationResult InvokeAutomation(object element, UiAutomationAction action, string? value);

    // -- focus --
    bool    SetFocus(object element);
    object? GetFocusedElement(object topLevel);

    // -- hit-test --
    object? HitTest(object topLevel, UiPoint point);

    // -- synthetic input (UI thread) --
    object? SendPointer(object topLevel, PointerAction action, UiPoint point);   // returns hit control
    object? SendWheel(object topLevel, UiPoint point, UiVector delta);
    object? SendText(object? target, string text);                              // null target => focused
    bool    SendKeys(object? target, string chords,
                     out IReadOnlyList<string> sentChords, out object? sink, out string error);

    // -- diagnostics --
    IReadOnlyList<string> GetRecentBindingErrors(int count, out bool enabled);   // oldest first; count<=0 => all
}

public enum UiAutomationAction { Auto=0, Invoke, Toggle, SetValue, Expand, Collapse, Select }
public enum PointerAction       { Move=0, Down, Up, Click, DoubleClick, RightClick }

public readonly record struct UiAutomationResult(bool Ok, string? Action, string? State, string? Error)
{
    public static UiAutomationResult Success(string action, string? state = null);
    public static UiAutomationResult Failure(string error);
}
```

### Child-enumeration contract (load-bearing)
`GetLogicalChildren`/`GetVisualChildren` return **every** child element (not only
controls), because the selector walk must traverse THROUGH non-control intermediates to
reach controls beneath them. Only `Visual`-derived children are valid handles.
Consumers that want controls only filter with `IsControl`:
- the selector engine (`SelectorChain`) walks all children, then filters final matches via `IsControl`;
- the tree dumps and `get_text` filter children via `IsControl` (Control-only, matching v1).

### Two hard-won invariants the adapter must preserve (do NOT regress)
1. **Visited-guard:** every merged logical+visual walk carries a shared
   `HashSet<object>(ReferenceEqualityComparer.Instance)` visited set
   (`SelectorChain.Descendants`, `InspectionTools.CollectText`). Overlay/popup/adorner
   cross-links make the merged graph cyclic; without the guard the walk StackOverflows.
2. **No `[McpServerTool]` `JsonElement` parameter with a default** — optional JSON params
   are `JsonElement? x = null` (a `JsonElement = default` crashes MapMcp() schema-gen).

---

## 4. `PropertyValueSerializer` (`Keincheck.Core`) — neutral split

Keeps only neutral concerns. The adapter delegates the depth-limited / cycle-safe JSON
reduction here (passing framework-aware projection hooks), and uses the static coercer.

```csharp
namespace Keincheck.Core;

public sealed class PropertyValueSerializer
{
    public PropertyValueSerializer(int maxDepth = 8);
    public int MaxDepth { get; }

    // Reduce a value to JSON-friendly form. renderElement projects a framework element
    // (e.g. control -> "Type#Name"); renderLeaf short-circuits framework value-structs.
    public object? ToJsonFriendly(object? value,
        Func<object,string?>? renderElement = null, Func<object,string?>? renderLeaf = null);

    // Generic, framework-neutral JSON -> CLR coercion (nullable/enum/numeric/TypeConverter/static Parse).
    public static bool TryCoerce(JsonElement value, Type targetType, out object? result, out string error);
}
```

---

## 5. `ControlRegistry` (`Keincheck.Core`) — opaque object handles, adapter-driven

```csharp
namespace Keincheck.Core;

public sealed class ControlRegistry
{
    public string Assign(object element);                    // idempotent "ctl-1a"; weak ref
    public bool   TryResolve(string id, out object? element);
    public IReadOnlyList<object> Query(string selector, IUiAdapter ui, object? scope = null); // never throws; null scope => ui.EnumerateRoots()
}
```

Selector grammar is unchanged (`Type`, `Type[Name=x]`, `#Name`, `[Prop=val]`, `A B`
descendant, `A > B` child; ordinal/case-sensitive; merged logical+visual walk). The
walk is now driven through `IUiAdapter` (`MatchesType` for type, `IsControl` to gate
matches, `GetLogicalChildren`/`GetVisualChildren` for traversal, `GetName`/
`TryReadProperty` for attribute predicates).

---

## 6. Framework-free broker client entry (`Keincheck.Client`)

The per-toolkit glue calls this with its own adapter + dispatcher. No Avalonia/WPF here.

```csharp
namespace Keincheck.Client;

public static class BrokerClientHost
{
    // Builds the tool host over the injected adapter/dispatcher and starts the
    // connect/serve loop. Dispose the handle (graceful, bounded ~2s) on app shutdown.
    public static IDisposable Start(IUiAdapter adapter, IUiDispatcher dispatcher, McpClientOptions options);
}

public sealed class ClientToolHost : IDisposable
{
    public static ClientToolHost Build(IUiAdapter adapter, IUiDispatcher dispatcher,
        Keincheck.Core.McpServerOptions options, params Assembly[] additionalToolAssemblies);
    // ... Describe(), InvokeAsync(name, argsJson, ct), IsToolReadOnly(name) unchanged ...
}

public sealed class BrokerClient : IAsyncDisposable
{
    public static BrokerClient Start(IUiAdapter adapter, IUiDispatcher dispatcher, McpClientOptions options);
    public string ClientId { get; }
}
```

`McpClientOptions` is unchanged (AppId, DisplayName, ReadOnly, PipeName, ConnectTimeout,
HeartbeatInterval, AutoReconnect, `CoreOptions` for screenshot/serialization/binding caps).

---

## 7. Embedded host DI (`Keincheck`, unchanged public surface)

```csharp
namespace Keincheck;
public static class AppBuilderExtensions {
    public static AppBuilder UseMcpServer(this AppBuilder builder, Action<McpServerOptions>? configure = null);
}
```

`McpHost` registers DI singletons available to tool methods as parameters:
`McpServerOptions`, `ControlRegistry`, **`IUiAdapter`** (`AvaloniaUiAdapter`), and
**`IUiDispatcher`** (`AvaloniaUiDispatcher`). Tools now take `IUiDispatcher` instead of
calling the old static `UiDispatch`. Tool discovery scans the **Core** assembly
(`typeof(InspectionTools).Assembly`) + entry assembly.

---

## 8. Avalonia package (`Keincheck.Avalonia`)

```csharp
namespace Keincheck.Avalonia;

public sealed class AvaloniaUiAdapter : IUiAdapter {
    public AvaloniaUiAdapter(PropertyValueSerializer serializer,
                             BindingErrorSink? bindingErrors = null,
                             int defaultMaxScreenshotDimension = 2048);
}
public sealed class AvaloniaUiDispatcher : IUiDispatcher { public AvaloniaUiDispatcher(); }
public sealed class BindingErrorSink : Avalonia.Logging.ILogSink { /* Install/Current/Recent/Uninstall — moved from Core */ }

public static class AppBuilderClientExtensions {
    // Avalonia UseMcpClient: builds AvaloniaUiAdapter + AvaloniaUiDispatcher, installs the
    // BindingErrorSink, and calls BrokerClientHost.Start. (Moved here from Keincheck.Client.)
    public static AppBuilder UseMcpClient(this AppBuilder b, Action<McpClientOptions>? configure = null);
}
```

`Keincheck.Avalonia.AvaloniaUiAdapter` owns the moved `EnumerateRoots` application-lifetime
logic (`IClassicDesktopStyleApplicationLifetime.Windows` / single-view root) — it is NOT
in the framework-free `ControlRegistry` any more.

---

## 9. WPF package (`Keincheck.Wpf`) — STAGE A scaffold

Compiles today; real adapter logic is Stage B. Element handles will be WPF
`DependencyObject`/`FrameworkElement`/`Visual`, cast internally.

```csharp
namespace Keincheck.Wpf;

public sealed class WpfUiAdapter : IUiAdapter {
    // Every member: throw new NotImplementedException(...) — Stage B fills these in.
}

public sealed class WpfUiDispatcher : IUiDispatcher {
    // REAL: wraps System.Windows.Application.Current.Dispatcher (sync when on UI thread).
}

public static class ApplicationClientExtensions {
    // WPF client skeleton: builds WpfUiAdapter + WpfUiDispatcher and calls BrokerClientHost.Start.
    public static IDisposable UseKeincheckClient(this System.Windows.Application app, Action<McpClientOptions> configure);
}
```

### Stage B for the WPF agent
Implement `WpfUiAdapter` over WPF, mapping each member to the WPF equivalent:
- `EnumerateRoots` → `Application.Current.Windows`; `GetTopLevel` → `Window.GetWindow`.
- `GetLogicalChildren` → `LogicalTreeHelper.GetChildren`; `GetVisualChildren` →
  `VisualTreeHelper.GetChild(...)` loop. Return ALL children (filter with `IsControl` in
  consumers); keep the shared visited-guard intact.
- `IsControl` → `is FrameworkElement` (or `Control`); `MatchesType` → walk `GetType().BaseType`.
- `GetBounds` → `VisualTreeHelper.GetDescendantBounds` / `TransformToAncestor`, mapped to `UiRect`.
- `GetPropertyNames`/`TryReadProperty`/`TryWriteProperty` → `DependencyProperty` registry
  + CLR reflection; reuse `PropertyValueSerializer.ToJsonFriendly`/`TryCoerce` with WPF
  projection hooks (WPF element → `"Type#Name"`, WPF value-structs → `ToString()`).
- `TryRenderToPng` → `RenderTargetBitmap` + `PngBitmapEncoder` (window vs element split).
- `InvokeAutomation` → `System.Windows.Automation.Peers.*` providers.
- Synthetic input → routed `MouseButtonEventArgs`/`MouseWheelEventArgs`/`TextComposition`/
  `KeyEventArgs` raised on the hit-tested element (mirror `Keincheck.Avalonia.SyntheticInput`).
- `GetRecentBindingErrors` → a WPF `PresentationTraceSources.DataBindingSource` trace sink.

Do not change the `IUiAdapter`/`IUiDispatcher`/geometry signatures; add members only via
the Foundation agent.
