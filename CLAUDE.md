# Contributing

Coding conventions for this repo. Written for coding agents first, humans second — precise over chatty,
examples over prose.

**Quick reference:**
- New fork-owned code → `Sanabi.Framework` (or a `Sanabi`-named file/namespace); prefer it over editing an
  upstream file (see §1).
- Follow upstream SS14.Launcher conventions, plus the local C# rules in §2.
- New tests → make them go red before you trust them (see §3).
- The `LauncherInfoModel` JSON endpoint the launcher fetches on startup is documented in §4 — keep the
  embedded fallback in sync with it.

## 1. Project lineage

SanabiLauncher is a fork of the Space Station 14 launcher:

- [space-wizards/SS14.Launcher](https://github.com/space-wizards/SS14.Launcher) — upstream, vanilla launcher.
  Configured as the `upstream` git remote.
- **SanabiLauncher** — this repo, the modded downstream.

We merge from upstream periodically. PRs mix upstream ports with Sanabi-specific work.

### Where fork code lives

- **Upstream projects** — `SS14.Launcher/`, `SS14.Common/`, `SS14.Loader/`, `Robust.LoaderApi/`,
  `SS14.Launcher.Bootstrap/` and friends hold code that originates upstream.
- **Fork-owned code** — new Sanabi subsystems live in `Sanabi.Framework/` (the mod/patch framework). A file or
  type that is ours is identified by living there, or by a `Sanabi` prefix in its filename, namespace or type
  name.

Prefer adding a new, fork-owned file over editing an upstream one. An upstream file edited in place is a merge
conflict waiting for the next pull; a new `Sanabi.Framework` file merges cleanly. Edit upstream in place only
when you're genuinely changing upstream behaviour (e.g. `SS14.Launcher/HappyEyeballsHttp.cs`).

## 2. C# code style

Follow upstream Space Wizards' C# conventions
([style guide](https://docs.spacestation14.com/en/general-development/codebase-info/style-guide.html),
[conventions](https://docs.spacestation14.com/en/general-development/codebase-info/conventions.html)), plus the
local rules below.

**Cast explicitly, with the target type in parentheses:**
```csharp
var myFloat = (float)GetMyInt();   // do this
float myFloat = GetMyInt();        // not this
```

**Name every optional argument.** An optional parameter is always passed with its name, so the call site says
what the value *means* instead of making the reader go read the signature. Required parameters stay positional:
```csharp
// do this
AssemblyHidingManager.HideAssembly("Harmony", exact: false);
PatchHelpers.TryPatchMethod(target, postfix, HarmonyPatchType.Postfix);

// not this - what is 'false'?
AssemblyHidingManager.HideAssembly("Harmony", false);
```

**Use verbose names**, even where existing code is archaic:
```csharp
TransformSystem transformSystem;   // not 'xform'
SpriteComponent spriteComponent;   // not 'sprite'
```

**`TryGet`/`Resolve`/`Ensure` naming.** `TryGet...` implies a pure lookup: it either finds the thing or it
doesn't, with no side effects either way. If a "`TryGet`" actually creates the thing when it's missing, name
it `Resolve...` or `Ensure...` instead — whichever reads better — not `TryGet...`:
```csharp
// misleading - this creates the entry as a side effect, which "TryGet" doesn't promise
private bool TryGetTemplate(string id, out Template template)

// do this instead
private bool ResolveTemplate(string id, out Template template)
```

**Compare enums with `==`, not `.Equals`.** An enum's `Equals` is inherited from `System.Enum`, a class, and
takes an `object`, so calling it boxes both sides — two heap allocations for what `==` does as a single
integer compare. It reads identically, compiles clean, returns the right answer, and only shows up as garbage.
It matters most where the comparison runs in bulk (a struct used as a dictionary key, whose `Equals`/
`GetHashCode` run on every lookup).
```csharp
return Flags.Equals(other.Flags);   // not this - two boxes per call
return Flags == other.Flags;        // this - an integer compare, no allocation
```
`==`, `!=` and bitwise tests (`(flags & Flag) != 0`) never box.

## 3. Testing: prove a test can fail

A test you have never watched fail is not yet a test — it might pass no matter what the code does. Before you
trust a new test, make it go red on purpose, then green again.

**The recipe.** With the test passing, sabotage the thing under test — invert the condition, neutralize the
feature, return a wrong value, or comment out the fix — run the test, and confirm it fails *for the expected
reason* (the assertion you care about, not a compile error somewhere else). Then revert and confirm it passes.
A test that stays green both ways asserts nothing; a test that fails for the wrong reason is testing the wrong
thing. Keep these break-it experiments local — they are never committed.

**Automate the red path for on/off features.** When a feature can simply be switched off — assembly hiding is
the obvious one — don't rely on remembering to sabotage it by hand. Add a permanent *negative-control* test
that disables the feature and asserts the symptom comes back, so the red path runs on every CI run alongside
the green one. If the negative control ever passes-by-doing-nothing, the whole suite around that feature is
suspect.

**In-repo example.** The stealth harness (`SS14.Launcher.StealthProbe`) runs the game engine's real
assembly-detection vectors against Sanabi and exits non-zero if any of them finds it.
`StealthHarnessTest.HidesSanabiFromAllProbes` is the green path (hiding on → nothing found).
`StealthHarnessTest.DetectsSanabiWhenHidingDisabled` is the negative control: it runs the harness with
`--expect-visible` (hiding off) and asserts the probes *did* find Sanabi — proving they can actually detect a
leak, so a green result in the other test means something. This is also how the harness earned its keep: run
against the live Harmony patches, it surfaced real stealth leaks the logic-only unit tests could not.

## 4. `LauncherInfoModel` JSON format

The launcher fetches a JSON document from `ConfigConstants.UrlLauncherInfo` (see
[`SS14.Launcher/ConfigConstants.cs`](SS14.Launcher/ConfigConstants.cs)) on startup, which is
deserialized into `LauncherInfoManager.LauncherInfoModel`
(see [`SS14.Launcher/Models/LauncherInfoManager.cs`](SS14.Launcher/Models/LauncherInfoManager.cs)).
This controls things like random flavour-text messages shown on the connecting screen, out-of-date
detection, asset overrides, and the changelog shown when the launcher is out of date.

If the endpoint is unreachable or returns invalid data, the launcher falls back to the JSON
embedded in `SanabiGlobal.FallbackLauncherInfoData`
(see [`Sanabi.Framework/Data/SanabiGlobal.cs`](Sanabi.Framework/Data/SanabiGlobal.cs)) — keep that
fallback in sync with whatever shape you add here.

Standard JSON doesn't support comments, so below is an annotated reference showing every field.
A real payload must **not** contain the `//` comments below.

```jsonc
{
  // Per-locale lists of random flavour-text messages shown on the connecting screen
  // (see RandomMessage.xaml.cs / ConnectingOverlay.xaml). Keys are culture names
  // (e.g. "en-US", "ru", "de", matching LocalizationManager.AvailableLanguages).
  // The launcher picks the list matching the currently selected language, walking up
  // parent cultures (e.g. "ru-RU" -> "ru") if there's no exact match, then falling
  // back to "en-US" if that locale has no messages of its own
  // (see LauncherInfoManager.GetMessagesForCurrentCulture). Missing/empty for a given
  // locale (with no "en-US" fallback available either) -> no random message is shown.
  "messages": {
    "en-US": [
      "First random message",
      "Second random message"
    ],
    "ru": [
      "Первое случайное сообщение"
    ]
  },

  // List of launcher version strings (must match ConfigConstants.CurrentLauncherVersion
  // exactly) that are considered up-to-date. If the running launcher's version isn't in
  // this list, the "out of date" overlay is shown (see MainWindowViewModel.OutOfDate).
  "allowedVersions": [
    "SANABI-220-7"
  ],

  // Maps an internal asset name to an override file name to download from
  // ConfigConstants.UrlAssetsBase, replacing it locally. A null value for a
  // key is ignored (no override applied). Can be empty.
  "overrideAssets": {
    "some-asset-name": "override-file-name.dat"
  },

  // Optional. Ordered list of changelog entries, shown top-to-bottom in a scrollable
  // list inside the "out of date" overlay (see MainWindowContent.xaml), each entry
  // getting its own visual version header. Order them newest-first.
  // Missing/null/malformed -> treated as empty (no changelog shown); entries with a
  // blank/missing "version" or a missing "changes" are silently dropped
  // (see LauncherInfoModel.ChangelogEntries).
  "changelog": [
    {
      // Version header text for this entry. Free-form string (not required to be a
      // real launcher version), shown as-is above the entry's changes.
      "version": "SANABI-220-7",

      // Per-locale changelog body text for this entry, keyed by culture name like
      // "messages" above, and resolved the same way (current language -> parent
      // cultures -> "en-US"; see LauncherInfoManager.GetLocalizedChangelog). Entries
      // with no text for the current language or "en-US" are hidden. Re-resolved when
      // the language is switched. Supports newlines (\n) and is wrapped/displayed
      // as-is; no other formatting (e.g. markdown) is applied.
      "changes": {
        "en-US": "- Added a changelog.\n- Fixed some bugs.",
        "ru": "- Добавлен список изменений.\n- Исправлены ошибки."
      }
    },
    {
      // Legacy format: a plain string is still accepted and treated as "en-US" text
      // (so it's shown for every language that has no text of its own).
      "version": "SANABI-220-6",
      "changes": "- Older entry."
    }
  ],

  // Optional. Absolute URL to an image shown at the bottom of the scrollable
  // changelog container. Empty string (or omitted) -> nothing is shown.
  // The downloaded bytes are sniffed by magic number before being decoded, so
  // only recognized image formats are rendered; anything else (or a failed
  // download) is silently skipped. PNG is the primary/expected format; JPEG,
  // GIF, BMP, and WEBP are also supported on a best-effort basis
  // (see MainWindowViewModel.LoadChangelogMedia / IsSupportedImage).
  // Rendering is done via AnimatedImage.Avalonia's AnimatedSource, so an
  // animated GIF (or animated/static WEBP, PNG, etc.) will actually play,
  // not just show its first frame.
  "changelogMediaUrl": "https://example.com/changelog-media.gif"
}
```

### Notes

- All fields except `messages`, `allowedVersions`, and `overrideAssets` are optional and default
  to an empty/blank value when omitted — the launcher must never crash or hard-fail on missing or
  malformed data from this endpoint, only degrade gracefully (skip the affected feature and log a
  warning).
- Bump `ConfigConstants.CurrentLauncherVersion` and add the new value to `allowedVersions`
  together when cutting a new launcher release, otherwise existing users will be shown as
  out-of-date.
