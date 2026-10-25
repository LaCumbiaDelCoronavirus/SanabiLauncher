#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using NUnit.Framework;
using Sanabi.Framework.Game.Managers;

namespace SS14.Launcher.Tests;

/// <summary>
///     Tests the assembly-hiding logic that every detection-vector patch delegates to: name matching,
///         the content-filtering transform, stack-trace sanitisation, call-site detection, and the
///         engine-view scope.
/// </summary>
/// <remarks>
///     <see cref="AssemblyHidingManager"/> keeps its hidden-assembly list in static state, so this fixture
///         must NOT be parallelised and every test unhides whatever it hid (see <see cref="TearDown"/>).
///     Applying the Harmony patches themselves (<see cref="AssemblyHidingManager.PatchDetectionVectors"/>)
///         is deliberately not exercised here: it hooks process-wide methods like
///         <c>AppDomain.GetAssemblies</c>, so running it would alter the test host for every later test.
///         What the patches call into is what's tested below.
/// </remarks>
[TestFixture]
[TestOf(typeof(AssemblyHidingManager))]
public sealed class AssemblyHidingManagerTest
{
    private const string TestAssemblyName = "SS14.Launcher.Tests";

    // Dynamic assembly names the manager always hides, regardless of the configured list.
    private const string HarmonyDynamicAssemblyName = "Anonymously Hosted DynamicMethods Assembly";
    private const string HarmonySharedStateAssemblyName = "HarmonySharedState";

    private static readonly BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    /// <summary>Ids hidden during the current test, unhidden again in <see cref="TearDown"/>.</summary>
    private readonly List<string> _hiddenDuringTest = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var identifier in _hiddenDuringTest)
            AssemblyHidingManager.UnhideAssembly(identifier);

        _hiddenDuringTest.Clear();
    }

    private void Hide(string identifier, bool exact)
    {
        AssemblyHidingManager.HideAssembly(identifier, exact: exact);
        _hiddenDuringTest.Add(identifier);
    }

    #region ShouldHideAssembly

    [Test]
    public void ShouldHideAssembly_ExactMatch_OnlyMatchesExactName()
    {
        Hide("SanabiExactAsm", exact: true);

        Assert.Multiple(() =>
        {
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("SanabiExactAsm"), Is.True);
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("SanabiExactAsm.Extra"), Is.False);
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("PrefixSanabiExactAsm"), Is.False);
        });
    }

    [Test]
    public void ShouldHideAssembly_ContainsMatch_MatchesAnySubstring()
    {
        Hide("SanabiFragment", exact: false);

        Assert.Multiple(() =>
        {
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("SanabiFragment"), Is.True);
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("Prefix.SanabiFragment.Suffix"), Is.True);
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("Unrelated"), Is.False);
        });
    }

    [Test]
    public void ShouldHideAssembly_EmptyOrUnknown_IsFalse()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AssemblyHidingManager.ShouldHideAssembly(""), Is.False);
            Assert.That(AssemblyHidingManager.ShouldHideAssembly("DefinitelyNotHidden_1234"), Is.False);
        });
    }

    [Test]
    public void ShouldHideAssembly_DynamicNames_AlwaysHidden()
    {
        // These are hidden by name even though nothing registered them.
        Assert.Multiple(() =>
        {
            Assert.That(AssemblyHidingManager.ShouldHideAssembly(HarmonyDynamicAssemblyName), Is.True);
            Assert.That(AssemblyHidingManager.ShouldHideAssembly(HarmonySharedStateAssemblyName), Is.True);
        });
    }

    [Test]
    public void HideThenUnhide_RoundTrips()
    {
        Assert.That(AssemblyHidingManager.ShouldHideAssembly("SanabiRoundTrip"), Is.False);

        AssemblyHidingManager.HideAssembly("SanabiRoundTrip", exact: true);
        Assert.That(AssemblyHidingManager.ShouldHideAssembly("SanabiRoundTrip"), Is.True);

        AssemblyHidingManager.UnhideAssembly("SanabiRoundTrip");
        Assert.That(AssemblyHidingManager.ShouldHideAssembly("SanabiRoundTrip"), Is.False);
    }

    [Test]
    public void HideAssembly_EmptyIdentifier_Throws()
    {
        Assert.Throws<System.ArgumentException>(() => AssemblyHidingManager.HideAssembly("   "));
    }

    #endregion

    #region DetectionVectorPatcher (the content-filtering transform)

    [Test]
    public void DetectionVectorPatcher_AssemblyNames_DropsHidden()
    {
        Hide("SanabiSecretAsm", exact: true);

        var input = new[]
        {
            new AssemblyName("SanabiSecretAsm"),
            new AssemblyName("Robust.Shared"),
        };

        var result = (AssemblyName[])InvokeDetectionVectorPatcher(input)!;

        Assert.That(result.Select(name => name.Name!), Is.EqualTo(new[] { "Robust.Shared" }));
    }

    // A type from an assembly that is NOT on the test's call stack, so hiding it doesn't trip
    // DetectionVectorPatcher's own call-site guard. (nunit's assembly can't be used: the test runner's
    // frames are nunit, so hiding it would make the guard treat the caller as hidden and skip filtering.)
    private static readonly System.Type HiddenSampleType = typeof(JsonSerializer);
    private static readonly System.Type VisibleSampleType = typeof(string);

    private void HideSampleAssembly()
        => Hide(HiddenSampleType.Assembly.GetName().Name!, exact: true);

    [Test]
    public void DetectionVectorPatcher_TypeArray_DropsTypesFromHiddenAssembly()
    {
        HideSampleAssembly();

        var input = new[] { HiddenSampleType, VisibleSampleType };

        var result = (System.Type[])InvokeDetectionVectorPatcher(input)!;

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain(VisibleSampleType));
            Assert.That(result, Does.Not.Contain(HiddenSampleType));
        });
    }

    [Test]
    public void DetectionVectorPatcher_SingleType_NulledWhenHidden()
    {
        HideSampleAssembly();

        var hiddenResult = InvokeDetectionVectorPatcher(HiddenSampleType);
        var visibleResult = InvokeDetectionVectorPatcher(VisibleSampleType);

        Assert.Multiple(() =>
        {
            Assert.That(hiddenResult, Is.Null);
            Assert.That(visibleResult, Is.EqualTo(VisibleSampleType));
        });
    }

    [Test]
    public void DetectionVectorPatcher_TypeEnumerable_DropsHidden()
    {
        HideSampleAssembly();

        // A non-array IEnumerable<Type> so the dedicated IEnumerable<Type> branch runs.
        IEnumerable<System.Type> input = new List<System.Type> { HiddenSampleType, VisibleSampleType };

        var result = (IEnumerable<System.Type>)InvokeDetectionVectorPatcher(input)!;

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Contain(VisibleSampleType));
            Assert.That(result, Does.Not.Contain(HiddenSampleType));
        });
    }

    [Test]
    public void DetectionVectorPatcher_TypeInfoEnumerable_TakesPrecedenceOverTypeEnumerable()
    {
        // IEnumerable<TypeInfo> is also IEnumerable<Type> via covariance; the TypeInfo branch must win.
        // Passing a non-array keeps it out of the Type[] branch, proving the ordering, not array handling.
        HideSampleAssembly();

        IEnumerable<TypeInfo> input = new List<TypeInfo>
        {
            HiddenSampleType.GetTypeInfo(),
            VisibleSampleType.GetTypeInfo(),
        };

        var result = (IEnumerable<TypeInfo>)InvokeDetectionVectorPatcher(input)!;

        Assert.Multiple(() =>
        {
            Assert.That(result.Select(typeInfo => typeInfo.AsType()), Does.Contain(VisibleSampleType));
            Assert.That(result.Select(typeInfo => typeInfo.AsType()), Does.Not.Contain(HiddenSampleType));
        });
    }

    #endregion

    #region Stack-trace sanitisation

    [Test]
    public void FilterStackTrace_DropsLinesMentioningHiddenAssembly()
    {
        Hide("SanabiStackAsm", exact: false);

        const string input = "   at Game.DoThing()\n   at SanabiStackAsm.Secret()\n   at Game.DoOther()";

        var result = InvokeFilterStackTrace(input);

        Assert.Multiple(() =>
        {
            Assert.That(result, Does.Not.Contain("SanabiStackAsm"));
            Assert.That(result, Does.Contain("Game.DoThing"));
            Assert.That(result, Does.Contain("Game.DoOther"));
        });
    }

    [Test]
    public void FilterStackTrace_DropsDynamicHarmonyLines()
    {
        const string input = "   at Game.DoThing()\n   at " + HarmonySharedStateAssemblyName + ".Emit()";

        var result = InvokeFilterStackTrace(input);

        Assert.That(result, Does.Not.Contain(HarmonySharedStateAssemblyName));
    }

    [Test]
    public void FilterStackTrace_NothingHidden_ReturnsUnchanged()
    {
        const string input = "   at Game.DoThing()\n   at Game.DoOther()";

        var result = InvokeFilterStackTrace(input);

        Assert.That(result, Is.EqualTo(input));
    }

    [Test]
    public void StackLineMentionsHidden_MatchesHiddenAndDynamicNames()
    {
        Hide("SanabiLineAsm", exact: false);

        Assert.Multiple(() =>
        {
            Assert.That(InvokeStackLineMentionsHidden("   at SanabiLineAsm.Foo()"), Is.True);
            Assert.That(InvokeStackLineMentionsHidden("   at " + HarmonyDynamicAssemblyName + ".Bar()"), Is.True);
            Assert.That(InvokeStackLineMentionsHidden("   at Robust.Client.Baz()"), Is.False);
        });
    }

    #endregion

    #region Call-site detection

    // IsCallsiteThroughHiddenAssembly ignores its first two frames, so a nested helper keeps a
    // test-assembly frame in view past that cutoff.
    private static bool CheckCallsite() => AssemblyHidingManager.IsCallsiteThroughHiddenAssembly();

    [Test]
    public void IsCallsiteThroughHiddenAssembly_TrueWhenCallerAssemblyHidden()
    {
        Assert.That(CheckCallsite(), Is.False, "precondition: test assembly not hidden yet");

        Hide(TestAssemblyName, exact: true);

        Assert.That(CheckCallsite(), Is.True);
    }

    [Test]
    public void IsCallsiteThroughHiddenAssembly_FalseUnderEngineView()
    {
        Hide(TestAssemblyName, exact: true);

        using (AssemblyHidingManager.ForceEngineView())
        {
            Assert.That(CheckCallsite(), Is.False, "engine view pretends the caller is external");
        }

        Assert.That(CheckCallsite(), Is.True, "scope restored after dispose");
    }

    [Test]
    public void IsCallsiteThroughAssembly_FindsNamedAssemblyOnStack()
    {
        Assert.Multiple(() =>
        {
            Assert.That(AssemblyHidingManager.IsCallsiteThroughAssembly(TestAssemblyName, exact: true), Is.True);
            Assert.That(AssemblyHidingManager.IsCallsiteThroughAssembly("SS14.Launcher", exact: false), Is.True);
            Assert.That(AssemblyHidingManager.IsCallsiteThroughAssembly("NotOnTheStack_9999", exact: false), Is.False);
            Assert.That(AssemblyHidingManager.IsCallsiteThroughAssembly("", exact: false), Is.False);
        });
    }

    [Test]
    public void IsCallsiteFromGame_FalseFromTestCode()
    {
        // No Robust/Content/OpenDreamShared namespace is on this stack.
        Assert.That(AssemblyHidingManager.IsCallsiteFromGame(), Is.False);
    }

    #endregion

    #region Engine-view scope

    [Test]
    public void ForceEngineView_SetsAndRestoresFlag()
    {
        Assert.That(AssemblyHidingManager.IsEngineViewForced(), Is.False);

        using (AssemblyHidingManager.ForceEngineView())
            Assert.That(AssemblyHidingManager.IsEngineViewForced(), Is.True);

        Assert.That(AssemblyHidingManager.IsEngineViewForced(), Is.False);
    }

    [Test]
    public void ForceEngineView_Nested_RestoresPreviousState()
    {
        using (AssemblyHidingManager.ForceEngineView())
        {
            using (AssemblyHidingManager.ForceEngineView())
                Assert.That(AssemblyHidingManager.IsEngineViewForced(), Is.True);

            // Inner dispose must restore to "true" (the state before the inner scope), not "false".
            Assert.That(AssemblyHidingManager.IsEngineViewForced(), Is.True);
        }

        Assert.That(AssemblyHidingManager.IsEngineViewForced(), Is.False);
    }

    #endregion

    #region Reflection helpers for private members

    private static object? InvokeDetectionVectorPatcher(object result)
    {
        var method = typeof(AssemblyHidingManager).GetMethod("DetectionVectorPatcher", PrivateStatic)!;
        var args = new[] { result };
        method.Invoke(null, args);
        return args[0];
    }

    private static string InvokeFilterStackTrace(string original)
    {
        var method = typeof(AssemblyHidingManager).GetMethod("FilterStackTrace", PrivateStatic)!;
        return (string)method.Invoke(null, [original])!;
    }

    private static bool InvokeStackLineMentionsHidden(string line)
    {
        var method = typeof(AssemblyHidingManager).GetMethod("StackLineMentionsHidden", PrivateStatic)!;
        return (bool)method.Invoke(null, [line])!;
    }

    #endregion
}
