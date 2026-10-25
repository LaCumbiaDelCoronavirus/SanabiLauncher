using System.Reflection;
using HarmonyLib;
using Sanabi.Framework.Game.Managers;
using SS14.Launcher.StealthProbe;

// The "fake game" stealth harness. It loads Sanabi into its OWN process, turns hiding on (unless asked not to),
// then runs the engine's real detection vectors as probes and reports every hidden-assembly identifier any probe
// managed to see. This has to be a separate process because PatchDetectionVectors() hooks process-wide runtime
// methods (AppDomain.GetAssemblies, Type.Assembly, ...) that would corrupt an in-process test host.
//
//   (default)          apply hiding, then probe. Exit 0 iff NOTHING leaked.
//   --expect-visible   skip hiding, then probe. Exit 0 iff the always-present betrayers WERE seen
//                      (the negative control: proves the probes actually detect Sanabi when it isn't hidden).

var expectVisible = args.Contains("--expect-visible");

// Capture genuine references to the Sanabi assembly/type BEFORE any patching, so later probes can call the
// hooked members (GetTypes, Location, Type.Assembly) against the real objects rather than a surrogate.
var sanabiType = typeof(AssemblyHidingManager);
var sanabiAssembly = sanabiType.Assembly;

// Load the Robust.Shared shim and register it so PatchEngineEnumerators can resolve the engine stub types.
var shimAssembly = typeof(Robust.Shared.Reflection.ReflectionManager).Assembly;
AssemblyManager.Assemblies["Robust.Shared"] = shimAssembly;

// Materialise Harmony's in-memory assemblies in both modes by applying one trivial patch.
HarmonyManager.Initialise();
HarmonyManager.Harmony.Patch(
    typeof(HarmonyFrameDecoy).GetMethod(nameof(HarmonyFrameDecoy.NoOp))!,
    postfix: new HarmonyMethod(typeof(HarmonyFrameDecoy).GetMethod(nameof(HarmonyFrameDecoy.NoOpPostfix))!));
HarmonyFrameDecoy.NoOp();

if (!expectVisible)
{
    AssemblyHidingManager.HideBasicAssemblies();
    AssemblyHidingManager.PatchDetectionVectors();
}

// Engine-enumerator stubs, seeded with a hidden assembly (0Harmony), the Sanabi assembly, and a clean one.
var reflectionManager = new Robust.Shared.Reflection.ReflectionManager();
reflectionManager.AssembliesList.AddRange([typeof(object).Assembly, typeof(Harmony).Assembly, sanabiAssembly]);
reflectionManager.TypesList.AddRange([sanabiType, typeof(string)]);

var modLoader = new Robust.Shared.ContentPack.BaseModLoader();
modLoader.Modules.AddRange([typeof(object).Assembly, typeof(Harmony).Assembly, sanabiAssembly]);

// A stack trace captured through a frame whose type name contains "Harmony", for the stack-trace probe.
HarmonyFrameDecoy.Capture(out var environmentTrace, out var exceptionTrace);

var report = new List<(string Probe, List<string> Leaks)>
{
    ("P0 AppDomain.GetAssemblies", Probes.AppDomainAssemblies()),
    ("P1 AssemblyLoadContext", Probes.LoadContexts()),
    ("P2 Assembly.GetTypes/DefinedTypes", Probes.TypesOfHiddenAssembly(sanabiAssembly)),
    ("P3 Assembly.GetType", Probes.TypeLookups(sanabiAssembly)),
    ("P4 GetReferencedAssemblies", Probes.ReferencedAssemblies()),
    ("P5 Assembly.Location", Probes.LocationOfHiddenAssembly(sanabiAssembly)),
    ("P6 Type.Assembly", Probes.TypeAssemblyName(sanabiType)),
    ("P7 StackTrace", Probes.StackTraces(environmentTrace, exceptionTrace)),
    ("P8 Engine enumerators", Probes.EngineEnumerators(reflectionManager, modLoader)),
};

Console.WriteLine(expectVisible
    ? "=== stealth probe: --expect-visible (hiding OFF, probes should FIND Sanabi) ==="
    : "=== stealth probe: hiding ON (probes should find NOTHING) ===");

var totalLeaks = 0;
foreach (var (probe, leaks) in report)
{
    totalLeaks += leaks.Count;
    if (leaks.Count == 0)
        Console.WriteLine($"  [clean] {probe}");
    else
        foreach (var leak in leaks)
            Console.WriteLine($"  [FOUND] {probe}: {leak}");
}

Console.WriteLine($"=== total leaks: {totalLeaks} ===");

if (expectVisible)
{
    // Negative control: the probes must actually detect Sanabi when it is not hidden, otherwise they are
    // worthless (a test that can never go red). We require at least the two statically-loaded betrayers.
    var sawHarmony = report.Any(r => r.Leaks.Any(l => l.Contains("Harmony", StringComparison.Ordinal)));
    var sawSanabi = report.Any(r => r.Leaks.Any(l => l.Contains("Sanabi", StringComparison.Ordinal)));

    if (sawHarmony && sawSanabi)
    {
        Console.WriteLine("negative control OK: probes detected Sanabi when hiding was disabled.");
        return 0;
    }

    Console.Error.WriteLine($"negative control FAILED: probes did not detect Sanabi (harmony={sawHarmony}, sanabi={sawSanabi}). The probes are not proving anything.");
    return 2;
}

// Hide mode: any leak at all is a stealth failure.
if (totalLeaks == 0)
{
    Console.WriteLine("stealth OK: no probe found Sanabi.");
    return 0;
}

Console.Error.WriteLine($"stealth FAILED: {totalLeaks} leak(s) above revealed Sanabi.");
return 1;
