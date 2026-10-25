using System.Diagnostics;
using System.Reflection;
using Sanabi.Framework.Data;
using Sanabi.Framework.Game.Managers;
using Sanabi.Framework.Game.Patches;
using Sanabi.Framework.Patching;

namespace Sanabi.Framework.Bootstrap;

/// <summary>
///     The single reflection entry point SS14.Loader uses to drive Sanabi. The loader invokes this by name so
///         that SS14.Loader.dll carries NO compile-time reference to Sanabi.Framework — and therefore no
///         <c>AssemblyRef</c> that would betray the fork to anything inspecting the loader assembly. Every
///         parameter and return type here is BCL only, so the reflection call site needs no Sanabi types.
/// </summary>
public static class SanabiLoaderEntry
{
    /// <summary>
    ///     Installs Sanabi's engine-stage hooks. Call once, after the engine assemblies are loaded and before
    ///         the engine's own entry point runs. The returned handle tears down the subscriptions on dispose.
    /// </summary>
    /// <param name="clientAssembly">The loaded <c>Robust.Client</c> assembly.</param>
    /// <param name="sharedAssembly">The loaded <c>Robust.Shared</c> assembly.</param>
    /// <param name="robustClientExecutable">Path to the engine executable (stored for later use).</param>
    public static IDisposable InstallEngineHooks(Assembly clientAssembly, Assembly sharedAssembly, string robustClientExecutable)
    {
        LazySanabiConfig.RobustClientExecutable = robustClientExecutable;

        AssemblyManager.Assemblies["Robust.Client"] = clientAssembly;
        AssemblyManager.Assemblies["Robust.Shared"] = sharedAssembly;

        // Wait until properly connected: receive the per-process config from the launcher over the pipe.
        SanabiConfig.ProcessConfig = IpcManager.RunObjectPipeClient(IpcManager.SanabiIpcName, SanabiJsonContext.Default.SanabiConfig);

        if (SanabiConfig.ProcessConfig.WaitForDebugger)
        {
            Console.WriteLine("Waiting for debugger to attach...");
            while (!Debugger.IsAttached)
                Thread.Sleep(100);

            Console.WriteLine("Debugger attached!");
        }

        var handle = new EngineHookHandle();

        if (SanabiConfig.ProcessConfig.PatchRunLevel.HasFlag(PatchRunLevel.Engine) &&
            AssemblyManager.TryGetAssembly("Robust.Client", out _))
        {
            HarmonyManager.Initialise();

            var modloader = ReflectionManager.GetTypeByQualifiedName("Robust.Shared.ContentPack.ModLoader", except: true);
            PatchHelpers.PatchPrefixFalse(modloader.GetMethod("SetUseLoadContext")!);

            // Hide assemblies, then patch the detection vectors.
            AssemblyHidingManager.HideBasicAssemblies();
            AssemblyHidingManager.PatchDetectionVectors();

            PatchEntryAttributeManager.ProcessRunLevel(PatchRunLevel.Engine);
            AssemblyManager.OnAssembliesFulfilled += handle.ContentRunLevelAct;

            // Idk why this is after everything and it really shouldnt be but shit breaks if its not after everything. HEEELP
            AssemblyManager.QueryAssemblies();
            AssemblyManager.Subscribe();
        }

        return handle;
    }

    /// <summary>
    ///     Best-effort delivery of an unhandled game exception to the launcher, if it is open and listening.
    ///         Never throws; a failure here must not mask the original crash.
    /// </summary>
    public static void ReportCrash(string? stage, Exception ex)
    {
        try
        {
            var report = $"Crash at {DateTime.Now:yyyy-MM-dd HH:mm:ss}{(stage is { } ? $" during stage '{stage}'" : "")}\n\n{ex}";
            Console.WriteLine(report);
            IpcManager.RunStringPipeClient(IpcManager.SanabiCrashIpcName, report);
        }
        catch (Exception reportEx)
        {
            Console.WriteLine($"Failed to report crash to launcher: {reportEx}");
        }
    }

    private sealed class EngineHookHandle : IDisposable
    {
        public void ContentRunLevelAct()
        {
            if (!SanabiConfig.ProcessConfig.PatchRunLevel.HasFlag(PatchRunLevel.Content))
                return;

            PatchEntryAttributeManager.ProcessRunLevel(PatchRunLevel.Content);
        }

        public void Dispose()
        {
            AssemblyManager.Unsubscribe();
            AssemblyManager.OnAssembliesFulfilled -= ContentRunLevelAct;
        }
    }
}
