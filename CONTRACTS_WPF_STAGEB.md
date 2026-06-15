# Keincheck — WPF Stage-B Contracts (partial split + sample map)

Hand-off from the **Split Agent (Phase A)** to the parallel Stage-B implementers. The
`WpfUiAdapter` is now a `sealed partial class` spread across one shared file + five
group files; every member still throws `NotImplementedException(NotYet)` so the whole
solution compiles **GREEN**. Each Stage-B implementer owns exactly one group file and
fills in its bodies without touching the others.

Ground truth for signatures stays `CONTRACTS_WPF.md` + `Keincheck.Core/IUiAdapter.cs`.
The reference implementation to mirror (tool-output shapes, projection hooks, automation
provider order, render split, chord grammar) is
`Keincheck.Avalonia/AvaloniaUiAdapter.cs` (+ `SyntheticInput.cs`, `BindingErrorSink.cs`).

---

## 1. Partial-file ownership map (`Keincheck.Wpf/`)

| File | Owns (IUiAdapter members) | Stage-B WPF mapping (System.Windows.*) |
|------|---------------------------|-----------------------------------------|
| **WpfUiAdapter.cs** | ctor + shared fields | `_serializer` (`PropertyValueSerializer`), `_defaultMaxDimension`, the `NotYet` const. Ctor signature `(PropertyValueSerializer? serializer = null, int defaultMaxScreenshotDimension = 2048)` — mirrors `AvaloniaUiAdapter` so DI wiring matches; all-optional so `new WpfUiAdapter()` in `ApplicationClientExtensions` still compiles. |
| **WpfUiAdapter.Topology.cs** | `EnumerateRoots`, `GetTopLevel`, `GetLogicalChildren`, `GetVisualChildren`, `IsControl`, `GetTypeName`, `MatchesType`, `GetName`, `GetTitle`, `GetBounds`, `IsEffectivelyVisible`, `IsEffectivelyEnabled`, `IsActiveWindow` | roots = `Application.Current.Windows`; `GetTopLevel` = `Window.GetWindow(dep)`; logical = `LogicalTreeHelper.GetChildren` (ALL children — consumers filter via `IsControl`); visual = `VisualTreeHelper.GetChildrenCount`/`GetChild` loop; `IsControl` = `is FrameworkElement` (or `Control`); `MatchesType` = walk `GetType().BaseType` simple-name ordinal; `GetName` = `(FrameworkElement).Name`; `GetTitle` = `(Window).Title`; `GetBounds` = `TransformToAncestor(window)` + `RenderSize` (or `VisualTreeHelper.GetDescendantBounds`) → `UiRect`; visible = `(UIElement).IsVisible`; enabled = `(UIElement).IsEnabled`; active = `(Window).IsActive`. |
| **WpfUiAdapter.Properties.cs** | `GetPropertyNames`, `TryReadProperty`, `TryWriteProperty`, `GetDataContext` | names via `DependencyPropertyDescriptor`/`MarkupWriter` or `TypeDescriptor.GetProperties` + `LocalValueEnumerator`; read prefers `DependencyProperty.GetValue` then CLR reflection, projected via the shared `_serializer.ToJsonFriendly(value, renderElement, renderLeaf)` with WPF hooks (element → `"Type#Name"`, WPF value-structs `Thickness`/`Brush`/`Color`/`Rect`/`CornerRadius`/`GridLength`/… → string form); write via `PropertyValueSerializer.TryCoerce` / WPF `TypeConverter` then `SetValue`/CLR set; `GetDataContext` = `(FrameworkElement).DataContext`. |
| **WpfUiAdapter.Visual.cs** | `TryRenderToPng`, `GetRecentBindingErrors` (+ the binding-error `TraceListener` ring buffer) | `RenderTargetBitmap` (96 dpi, `PixelFormats.Pbgra32`) → `PngBitmapEncoder` → `byte[]`, window (whole visual) vs control (subtree) split, clamp to `maxDim` (port `ClampToPixels`); binding errors via `PresentationTraceSources.DataBindingSource` + a custom `TraceListener` ring buffer installed by the adapter — the WPF analog of `BindingErrorSink`; oldest-first, `count <= 0` ⇒ all. |
| **WpfUiAdapter.Automation.cs** | `InvokeAutomation`, `SetFocus`, `GetFocusedElement`, `HitTest` | `UIElementAutomationPeer.CreatePeerForElement(uiElement)`, `GetPattern(PatternInterface.Invoke/Toggle/Value/ExpandCollapse/SelectionItem)`, map to `UiAutomationResult` (mirror the Avalonia `Auto` detect order); `SetFocus` = `(UIElement).Focus()`/`Keyboard.Focus`; `GetFocusedElement` = `Keyboard.FocusedElement`; `HitTest` = `VisualTreeHelper.HitTest(window, point)` → nearest control. |
| **WpfUiAdapter.Input.cs** | `SendPointer`, `SendWheel`, `SendText`, `SendKeys` (+ the Win32 `SendInput` P/Invoke helper) | **hardest group.** No public WPF synthetic pointer → Win32 `SendInput` via P/Invoke for pointer move/down/up/click/double/right + wheel at SCREEN coords (translate window-relative `UiPoint` through `Visual.PointToScreen`); `SendInput`/`keybd_event` (or `TextComposition`/`InputManager`) for text/keys to the focused element; or raise routed `MouseButtonEventArgs`/`MouseWheelEventArgs`/`TextCompositionEventArgs`/`KeyEventArgs` where feasible (mirror `SyntheticInput`). The INPUT/MOUSEINPUT/KEYBDINPUT structs + extern live in this file. **Document the chosen approach + fidelity limits in `risks`.** |

### Load-bearing invariants to preserve in Stage B
1. **Child enumeration returns ALL children** (not only controls); the selector walk
   traverses THROUGH non-control intermediates. Consumers filter with `IsControl`.
2. **Visited-guard** is enforced by Core (`SelectorChain.Descendants` /
   `InspectionTools.CollectText`) via a shared
   `HashSet<object>(ReferenceEqualityComparer.Instance)`; the adapter must keep returning
   only valid (Visual-derived) handles so the merged logical+visual graph stays walkable.
3. Threading: every member is UI-thread-affine; callers marshal via the injected
   `IUiDispatcher` (`WpfUiDispatcher`, already real). The adapter does NOT re-marshal.
4. `Keincheck.Wpf` references ONLY `Keincheck.Core` + `Keincheck.Client` (+ BCL/WPF) —
   never Avalonia.

### Stage-B wiring TODO (out of Phase A scope)
`ApplicationClientExtensions.UseKeincheckClient` currently builds `new WpfUiAdapter()` and
`new WpfUiDispatcher()` and calls `BrokerClientHost.Start`. When the property/visual groups
land, thread `coreOptions` through like the Avalonia `UseMcpClient`: build the
`PropertyValueSerializer(coreOptions.MaxSerializationDepth)`, pass
`coreOptions.MaxScreenshotDimension`, and install the WPF binding-error sink when
`coreOptions.CaptureBindingErrors`.

---

## 2. WPF sample app — `samples/Keincheck.Wpf.Demo`

Net8.0-windows, `<UseWPF>true</UseWPF>`, `OutputType=WinExe`, `<RollForward>Major</RollForward>`,
references `Keincheck.Wpf` only. Added to `Keincheck.sln` under the `samples` solution
folder. The WPF analog of `samples/Keincheck.Demo`.

- **App.xaml / App.xaml.cs** — `OnStartup` calls
  `this.UseKeincheckClient(o => o.AppId = "wpfdemo")` (disposes the handle on `OnExit`).
  The app registers with the hub as **`wpfdemo`**; it appears in `hub_list_clients`,
  `hub_select_client` targets it, then the 22 tools operate on it (throwing
  `NotImplemented` until the Stage-B adapter bodies land).
- **MainWindow.xaml / .cs** — `DataContext = new MainViewModel()`. Named controls:

  | `x:Name` | Type | Purpose |
  |----------|------|---------|
  | `Root` | `StackPanel` | container |
  | `Heading`, `StatusHeader`, `CountLabel`, `SelectionLabel`, `MessageLabel` | `TextBlock` | labels / bound side-effect readouts |
  | **`Save`** | `Button` | click handler increments `ClickCount` (bindable invoke side-effect) |
  | **`Input`** | `TextBox` | two-way bound to `Name` |
  | `SubscribeCheck` | `CheckBox` | two-way bound to `IsSubscribed` |
  | `ItemsList` | `ListBox` | bound `Items` + two-way `SelectedItem` |
  | **`Gauge`** | `GaugeControl` | **custom-drawn, NO automation peer** — synthetic-input fallback target |

- **GaugeControl.cs** — `FrameworkElement` subclass overriding `OnRender` (draws a dial +
  needle), `OnCreateAutomationPeer() => null!` (no peer → forces synthetic-input
  fallback), with styled `ValueProperty` (`AffectsRender`) + readable `LastClickAngle` so
  property/wait-for tools have a custom target; `OnMouseLeftButtonDown` advances the gauge
  as an observable side-effect. The WPF analog of the Avalonia demo's `VertexCanvas`.
- **MainViewModel.cs** — `INotifyPropertyChanged` with `Name`, `IsSubscribed`,
  `ClickCount`, `StatusMessage`, `ObservableCollection<DemoItem> Items`, `SelectedItem`.
  Mirrors the Avalonia demo's view-model (so `get_data_context` reads the same shape).

---

## 3. Build status

`dotnet build Keincheck.sln` — **succeeded, 0 warnings, 0 errors** with the partial-stub
adapter and the new sample. The Avalonia path and the existing tests are untouched.

### Phase A NOT done (left for Stage B)
- Real `WpfUiAdapter` bodies (this hand-off only split + stubbed them).
- A live WPF e2e test (drive `wpfdemo` through the hub) — add alongside the Avalonia
  e2e once the adapter bodies exist.
- Threading `coreOptions` through `UseKeincheckClient` (see §1 wiring TODO).
