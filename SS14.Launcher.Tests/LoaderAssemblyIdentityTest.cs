#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using NUnit.Framework;

namespace SS14.Launcher.Tests;

/// <summary>
///     The concrete, CI-checkable proof of Workstream A: the fork-built <c>SS14.Loader.dll</c> ships into the game
///     process under the same name as upstream's, so it must look like a vanilla build. This reads the built DLL's
///     manifest straight off disk (no loading of its types) and asserts the two tells are gone:
///     <list type="bullet">
///         <item>its assembly <b>Version</b> matches the upstream-matching stamp in <c>SS14.Loader.csproj</c>, not
///             the fork's <c>Launcher.props</c> version; and</item>
///         <item>it carries <b>no AssemblyRef</b> to <c>Sanabi.Framework</c> or <c>SS14.Common</c> — the reflection
///             bootstrap in <c>Program.cs</c> keeps those out of the manifest.</item>
///     </list>
///     If this goes red after a ref change, see the fallback in the plan: <c>ReferenceOutputAssembly="false"</c> on
///     those two ProjectReferences plus an explicit copy of the DLLs into the loader output.
/// </summary>
[TestFixture]
public sealed class LoaderAssemblyIdentityTest
{
    // Must match <Version> in SS14.Loader/SS14.Loader.csproj (upstream space-wizards/SS14.Launcher release).
    private static readonly Version ExpectedVersion = new(0, 40, 3, 0);

    // AssemblyRefs that would betray the fork and must not appear in the loader manifest.
    private static readonly string[] ForbiddenReferences = ["Sanabi.Framework", "SS14.Common"];

    [Test]
    public void LoaderLooksVanilla()
    {
        var loaderDll = LocateLoaderDll();

        using var stream = File.OpenRead(loaderDll);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var version = metadata.GetAssemblyDefinition().Version;
        Assert.That(version, Is.EqualTo(ExpectedVersion),
            $"SS14.Loader.dll version must match upstream ({ExpectedVersion}); it is a visible game-process tell.");

        var references = metadata.AssemblyReferences
            .Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name))
            .ToList();

        foreach (var forbidden in ForbiddenReferences)
            Assert.That(references, Does.Not.Contain(forbidden),
                $"SS14.Loader.dll must carry no AssemblyRef to '{forbidden}'. References: {string.Join(", ", references)}");
    }

    private static string LocateLoaderDll()
    {
        const string configuration =
#if DEBUG
            "Debug";
#else
            "Release";
#endif

        // The test runs from .../SS14.Launcher.Tests/bin/<cfg>/<tfm>/; reuse <tfm> and walk up to the repo root.
        var baseDirectory = AppContext.BaseDirectory;
        var targetFramework = new DirectoryInfo(baseDirectory.TrimEnd(Path.DirectorySeparatorChar)).Name;

        var directory = new DirectoryInfo(baseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "SS14.Launcher.sln")))
            directory = directory.Parent;

        Assert.That(directory, Is.Not.Null, "Could not find SS14.Launcher.sln above the test output directory.");

        // The loader may build straight to bin/<cfg>/<tfm>/ or to a RID subfolder (bin/<cfg>/<tfm>/<rid>/); both
        // carry the same identity, so search and take the first.
        var loaderBin = Path.Combine(directory!.FullName, "SS14.Loader", "bin", configuration, targetFramework);
        Assert.That(Directory.Exists(loaderBin), Is.True, $"SS14.Loader output not found: {loaderBin}");

        var loaderDll = Directory
            .EnumerateFiles(loaderBin, "SS14.Loader.dll", SearchOption.AllDirectories)
            .FirstOrDefault();

        Assert.That(loaderDll, Is.Not.Null, $"SS14.Loader.dll not built under: {loaderBin}");
        return loaderDll!;
    }
}
