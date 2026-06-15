using System.Linq;
using System.Reflection;
using Keincheck.Core;
using Keincheck.Core.Tools;
using Xunit;

namespace Keincheck.Tests;

/// <summary>
/// Architecture (assertion) tests that prove the neutral seam is genuinely
/// framework-free: the <c>Keincheck.Core</c> assembly — which owns the
/// <see cref="IUiAdapter"/>/<see cref="IUiDispatcher"/> contract, the neutral
/// geometry, the <see cref="ControlRegistry"/>, the <see cref="SelectorChain"/>,
/// the <see cref="PropertyValueSerializer"/>, and all 22 <c>[McpServerTool]</c>
/// modules — must NOT reference any UI toolkit assembly (Avalonia or WPF).
/// </summary>
/// <remarks>
/// These are pure reflection assertions: they inspect assembly metadata only and
/// touch no UI thread, so they do not join the headless collection. If a future
/// edit leaks an Avalonia type back into Core (e.g. a styled <c>Rect</c> on a tool
/// signature, or a stray <c>using Avalonia;</c> that pulls a real reference), the
/// C# compiler adds an <c>Avalonia*</c> entry to Core's referenced-assembly list
/// and these tests go red — guarding the seam against silent regression.
/// </remarks>
public sealed class FrameworkSeamTests
{
    // The framework-free engine assembly, reached via a type we know lives in it.
    private static readonly Assembly CoreAssembly = typeof(IUiAdapter).Assembly;

    /// <summary>
    /// The defining assertion of the refactor: Core references no UI toolkit. We
    /// inspect the assembly's *direct* references (what the compiler recorded) and
    /// fail if any is named for a UI framework. This is the test the assignment
    /// calls out by name.
    /// </summary>
    [Fact]
    public void Core_Assembly_Does_Not_Reference_Avalonia()
    {
        var avaloniaRefs = CoreAssembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null &&
                        a.Name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name!)
            .ToArray();

        Assert.True(
            avaloniaRefs.Length == 0,
            $"Keincheck.Core must be framework-free but references: {string.Join(", ", avaloniaRefs)}");
    }

    /// <summary>
    /// The seam is framework-free in BOTH directions: no WPF (PresentationCore /
    /// PresentationFramework / WindowsBase / System.Windows.*) reference either, so
    /// the same Core powers the Avalonia and (Stage-B) WPF adapters unchanged.
    /// </summary>
    [Fact]
    public void Core_Assembly_Does_Not_Reference_Wpf()
    {
        string[] wpfMarkers =
        {
            "PresentationCore",
            "PresentationFramework",
            "WindowsBase",
            "System.Windows.",
        };

        var wpfRefs = CoreAssembly
            .GetReferencedAssemblies()
            .Where(a => a.Name is not null &&
                        wpfMarkers.Any(m => a.Name.StartsWith(m, StringComparison.OrdinalIgnoreCase)))
            .Select(a => a.Name!)
            .ToArray();

        Assert.True(
            wpfRefs.Length == 0,
            $"Keincheck.Core must be framework-free but references WPF: {string.Join(", ", wpfRefs)}");
    }

    /// <summary>
    /// Pins the seam's surface to a single assembly: the neutral contract
    /// (<see cref="IUiAdapter"/>, <see cref="IUiDispatcher"/>), the geometry structs
    /// (<see cref="UiRect"/>/<see cref="UiPoint"/>/<see cref="UiVector"/>), the
    /// engine (<see cref="ControlRegistry"/>, <see cref="PropertyValueSerializer"/>,
    /// <see cref="McpServerOptions"/>) and a representative tool module all live in
    /// the very assembly asserted framework-free above. Without this, the
    /// no-Avalonia assertion could be vacuously true against the wrong assembly.
    /// </summary>
    [Fact]
    public void Neutral_Spine_Types_All_Live_In_The_FrameworkFree_Core_Assembly()
    {
        Type[] coreTypes =
        {
            typeof(IUiAdapter),
            typeof(IUiDispatcher),
            typeof(UiRect),
            typeof(UiPoint),
            typeof(UiVector),
            typeof(UiAutomationResult),
            typeof(UiAutomationAction),
            typeof(PointerAction),
            typeof(ControlRegistry),
            typeof(PropertyValueSerializer),
            typeof(McpServerOptions),
            typeof(InspectionTools), // one of the 22 [McpServerTool] modules
        };

        foreach (var t in coreTypes)
            Assert.Same(CoreAssembly, t.Assembly);
    }

    /// <summary>
    /// The neutral <see cref="IUiAdapter"/> surface must speak only neutral types:
    /// every parameter and return type across the whole interface comes from the
    /// BCL or from Core itself — never from a UI toolkit. This catches a leak the
    /// referenced-assembly check could miss when a toolkit type sneaks onto a
    /// signature without (yet) pulling a hard assembly reference.
    /// </summary>
    [Fact]
    public void IUiAdapter_Surface_Exposes_Only_Neutral_Types()
    {
        foreach (var method in typeof(IUiAdapter).GetMethods())
        {
            AssertNeutral(method.ReturnType, $"{method.Name} return");
            foreach (var p in method.GetParameters())
                AssertNeutral(p.ParameterType, $"{method.Name}({p.Name})");
        }
    }

    // A type is "neutral" when it is declared in the BCL or in Core (the
    // framework-free assembly). We unwrap by-ref, arrays, and generic arguments
    // (e.g. IEnumerable<object>, out IReadOnlyList<string>) before checking.
    private static void AssertNeutral(Type type, string where)
    {
        if (type.HasElementType) // T[], T&, T*
        {
            AssertNeutral(type.GetElementType()!, where);
            return;
        }

        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments())
                AssertNeutral(arg, where);
            // also validate the open generic's defining assembly below
        }

        if (type.IsGenericParameter)
            return;

        var asm = type.Assembly;
        var name = asm.GetName().Name ?? string.Empty;

        var isBcl =
            name == "System.Private.CoreLib" ||
            name.StartsWith("System.", StringComparison.Ordinal) ||
            name.StartsWith("System", StringComparison.Ordinal) ||
            name == "mscorlib" ||
            name == "netstandard";

        var isCore = ReferenceEquals(asm, CoreAssembly);

        Assert.True(
            isBcl || isCore,
            $"IUiAdapter.{where} exposes non-neutral type {type.FullName} from assembly '{name}'.");
    }
}
