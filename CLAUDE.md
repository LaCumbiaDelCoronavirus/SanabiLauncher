# Contributing

Coding conventions for this repo. Written for coding agents first, humans second — precise over chatty,
examples over prose.

**Quick reference:**
- New fork-owned code → `Sanabi.Framework` (or a `Sanabi`-named file/namespace); prefer it over editing an
  upstream file (see §1).
- Two processes: the launcher UI and the game. Sanabi's patching lives in the **game** process and reaches the
  engine only by reflection/Harmony — see §2 before touching the injection path.
- Follow upstream SS14.Launcher conventions, plus the local C# rules in §3.
- New tests → make them go red before you trust them (see §4).
- The `LauncherInfoModel` JSON endpoint the launcher fetches on startup is documented in §5 — keep the
  embedded fallback in sync with it.
- Config values are CVars; fork ones go in `SanabiCVars`, and game-process ones must also be added to the
  `SanabiConfig` IPC struct (see §6).
- User-facing strings are Fluent `.ftl` files; keep **both** `en-US` and `ru` in sync — those are the only
  locales the fork maintains (see §7).

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

## 2. Runtime architecture: how patching reaches the game

There are **two processes**, and it matters constantly which one you're in:

- **The launcher UI process** (`SS14.Launcher`, an Avalonia app). Logs in, browses/downloads engine + content,
  then spawns the game. Right before spawning it stands up the Sanabi IPC servers in
  [`SS14.Launcher/Models/Connector.cs`](SS14.Launcher/Models/Connector.cs): a struct pipe that hands the game
  its `SanabiConfig` (built from the user's CVars) and a string pipe that receives crash reports back. This
  process is **not** patched and never loads `Sanabi.Framework` into itself.
- **The game process** (`SS14.Loader` → the engine). This is where Sanabi lives, and the only place assembly
  hiding / detection vectors matter — because this is where the engine (and anything it hosts) can enumerate
  the loaded assemblies and notice the fork.

**Game-process startup** ([`SS14.Loader/Program.cs`](SS14.Loader/Program.cs)):
1. Verify the engine zip's Ed25519 signature, then register an `AssemblyLoadContext.Default.Resolving` hook
   that streams the engine DLLs (`Robust.Client`, `Robust.Shared`, …) straight out of the zip.
2. Reflectively invoke `Sanabi.Framework.Bootstrap.SanabiLoaderEntry.InstallEngineHooks(...)` **by name** — so
   `SS14.Loader.dll` keeps no compile-time reference to the fork and no `AssemblyRef` that would betray it (this
   is why the loader looks vanilla; see §1). This reflection call is the one and only bridge from the
   vanilla-looking loader into Sanabi.
3. Hand control to the engine's own `ILoaderEntryPoint.Main`.

[`SanabiLoaderEntry.InstallEngineHooks`](Sanabi.Framework/Bootstrap/SanabiLoaderEntry.cs) is the fork's real
entry point. It receives `SanabiConfig` over the pipe, and when the config's `PatchRunLevel` includes `Engine`:
initializes Harmony, hides the fork assemblies + patches the detection vectors, runs the engine-level patch
entries, and wires `AssemblyManager`. Content-level patch entries fire later — once content assemblies are
loaded — via `AssemblyManager.OnAssembliesFulfilled`.

**The engine (RobustToolbox) is not vendored in this repo.** Everything Sanabi does to it crosses a
reflection/Harmony boundary: resolve engine types and methods by qualified name through
`ReflectionManager.GetTypeByQualifiedName` / `PatchHelpers` / `AccessTools`. Never add a compile-time dependency
on the engine.

### The managers (`Sanabi.Framework/Game/Managers`)

- **`HarmonyManager`** — owns the single Harmony instance (`HarmonyManager.Harmony`); `Initialise()` once.
- **`AssemblyHidingManager`** — the stealth core. `HideBasicAssemblies()` seeds the hidden set
  (`Sanabi.Framework`, `0Harmony`, `SS14.Common`, `MonoMod`, the dynamic-method assemblies);
  `PatchDetectionVectors()` Harmony-patches the runtime enumeration surface (`AppDomain.GetAssemblies`,
  `AssemblyLoadContext`, `Assembly.GetTypes`/`GetType`/`Location`, `Type.Assembly`, stack traces) plus the
  engine's own `ReflectionManager.Assemblies`/`FindAllTypes`. `HideAssembly(...)` adds more at runtime (mods are
  hidden as they load). `ForceEngineView()` is a scoped escape hatch that temporarily *unfilters* those hooks so
  Sanabi's own code can see the real list (used during mod type-discovery); `EngineOverridenCallsites` is an
  allowlist of engine methods that must always see the true, unfiltered list.
- **`AssemblyManager`** — the dictionary of known assemblies (`Robust.Client`/`Robust.Shared` + content) and the
  run-level fulfillment events that gate `PatchRunLevel.Content`.
- **`AssemblyLoadingManager`** — loads external user mods from `LauncherPaths.SanabiModsPath` (`.dll`, folder, or
  `.zip`), gated on the `LoadExternalMods` CVar. It is deliberately **detached** from the engine `ModLoader`
  (see that file's own docs): it registers a mod's types with the engine `ReflectionManager` and drives the
  `GameShared` lifecycle itself, so mods never enter `BaseModLoader.LoadedModules`.
- **`IpcManager`** — the named-pipe client side of the launcher↔game IPC (`SanabiIpcName` for config,
  `SanabiCrashIpcName` for crash reports).

### The patch-entry model

Fork features are static methods tagged `[PatchEntry(PatchRunLevel.Engine | Content)]`
([`PatchEntryAttribute.cs`](Sanabi.Framework/Game/Patches/PatchEntryAttribute.cs));
`PatchEntryAttributeManager.ProcessRunLevel(level)` discovers and invokes them at the matching stage. An entry
method takes either no args or a single `Dictionary<string, Assembly?>` (it receives `AssemblyManager.Assemblies`).
`Engine` = engine assemblies loaded but the game entry point not yet started; `Content` = everything loaded and
running. The individual Harmony patches themselves live under `Sanabi.Framework/Game/Patches/`.

## 3. C# code style

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

## 4. Testing: prove a test can fail

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

## 5. `LauncherInfoModel` JSON format

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

## 6. CVars (configuration values)

Every persisted setting is a **CVar**: a typed, named, defaulted value defined with
`CVarDef.Create("Name", defaultValue)` ([`SS14.Common/Data/CVarDef.cs`](SS14.Common/Data/CVarDef.cs)).

**Where they live.**
- Upstream CVars → [`SS14.Launcher/Models/Data/CVars.cs`](SS14.Launcher/Models/Data/CVars.cs).
- Fork CVars → the `SanabiCVars` partial class in `Sanabi.Framework/Data/SanabiCVars*.cs`, split across partial
  files by topic (e.g. [`SanabiCVars.cs`](Sanabi.Framework/Data/SanabiCVars.cs) for general stealth/UI toggles,
  [`SanabiCVars.Patching.cs`](Sanabi.Framework/Data/SanabiCVars.Patching.cs) for patch toggles).
- Account-scoped CVars → `SanabiAccountCVars` ([`SanabiAccountCVars.cs`](Sanabi.Framework/Data/SanabiAccountCVars.cs)):
  one value *per account* (e.g. the HWID `SpoofingSeed`), stored under the key `"<guid>::ACCOUNT::<name>"`.

**Registration is reflection-based — just declaring the field is enough.** `DataManager.InitializeCVars()`
([`SS14.Launcher/Models/Data/DataManager.cs`](SS14.Launcher/Models/Data/DataManager.cs)) walks the public static
fields of `CVars` and `SanabiCVars` and registers every `CVarDef` it finds; account CVars are registered per
account via `AssignAccountCVars(guids, typeof(SanabiAccountCVars))`. So a new fork setting is just a new
`public static readonly CVarDef<T>` field in the right class — no extra wiring. Values persist in the launcher's
SQLite config DB. Read/write with `GetCVar` / `SetCVar` (`GetActiveAccountCVarOrDefault` for account CVars); UI
view-models commit through `SetAndCommitCvar`. Default to the safe/off value (patching defaults to off).

**CVars are launcher-UI-process state; the game process never reads them directly.** The settings the game needs
are copied into the blittable `SanabiConfig` struct and sent over IPC
([`SanabiConfig.cs`](Sanabi.Framework/Data/SanabiConfig.cs); see the IPC servers in §2). Therefore, to expose a
CVar *to the game*, do all three:
1. declare the `CVarDef` in `SanabiCVars`;
2. add a matching field to the `SanabiConfig` struct (it must stay an unmanaged, blittable,
   `[StructLayout(Sequential, Pack=1)]` struct — no reference types);
3. map CVar → struct field in `SanabiConfigExtensions.Configure(dataManager)`.

A CVar that only affects the launcher UI (e.g. `StartOnLoginMenu`, `FancyBackground`) needs only step 1.

### Per-account CVars

Some settings need a *separate value per logged-in account* rather than one global value — these live in
[`SanabiAccountCVars`](Sanabi.Framework/Data/SanabiAccountCVars.cs). They are defined exactly like any other
CVar (`CVarDef.Create(...)`), but each account gets its own stored entry, keyed
`"<account-guid>::ACCOUNT::<name>"` (`DataManager.GetAccountCVarIdentifier`).

Current members:
- **`SpoofingSeed`** (`long`, a bit-reinterpreted `ulong`) — the seed the HWID spoofer
  ([`HwidPatch`](Sanabi.Framework/Game/Patches/HwidPatch.cs)) and the spoofed fingerprint derive from. Per-account
  so each account presents its own stable-but-distinct HWID.
- **`ShouldRegenerateSeed`** (`bool`) — when set, a fresh `SpoofingSeed` is generated the next time the account
  becomes active, then cleared.
- **`AuthServers`** (`string`) — the account's auth-server URL set (vertical-bar-separated sets, comma-separated
  fallbacks); a sentinel default (`AuthServersDefaultSerializedNullEscapeValue`) means "unset, use the default".

**Lifecycle.** Unlike global CVars (registered once in `InitializeCVars`), per-account entries are materialized
lazily: when an account becomes active, `DataManager.BeforeActiveAccountUpdate` calls
`AssignAccountCVars([userId], typeof(SanabiAccountCVars), overwrite: false)` to create that account's entries if
they don't already exist (`overwrite: false` keeps any previously-saved values), then reloads them from SQLite.

**Access** ([`DataManager.cs`](SS14.Launcher/Models/Data/DataManager.cs)):
- `GetActiveAccountCVarOrDefault(def)` — the active account's value, or the CVar default if there's no active
  account or no stored entry. This is what `SanabiConfig.Configure` uses (e.g. for `SpoofingSeed`).
- `GetAccountCVar(def, guid)` / `GetAccountCVarOrDefault(def, guid?)` — a specific account's value.
- `SetAccountCVar(def, guid, value)` / `TrySetActiveAccountCVar(def, value)` — write (persisted to SQLite under
  the account-scoped key).

**Adding a per-account CVar:** declare the field in `SanabiAccountCVars` — it is assigned automatically for each
account as it activates (no `InitializeCVars` change needed, since that only registers the global `CVars` and
`SanabiCVars`). If the game process needs it per-account, read it via `GetActiveAccountCVarOrDefault` inside
`SanabiConfig.Configure` and carry it in the `SanabiConfig` struct, exactly as `SpoofingSeed` does.

## 7. Localization

User-facing strings are [Fluent](https://projectfluent.org/) `.ftl` files under
`SS14.Launcher/Assets/Locale/<culture>/`, embedded as Avalonia resources (`avares://`) and loaded by
[`LocalizationManager`](SS14.Launcher/Localization/LocalizationManager.cs) via the Linguini library. Localization
is a **launcher-UI-process** concern only — the game process has nothing to do with it.

**How a culture is assembled.** `LoadCulture(culture)` builds a fresh Fluent bundle by first loading the
`en-US` files as the base layer, then layering the selected culture's files on top with
`AddResourceOverriding` (walking up parent cultures, e.g. `ru-RU` → `ru`). So **`en-US` is always the fallback**:
any key missing in the active locale falls back to the `en-US` text, and a key missing everywhere makes
`GetString` return the key string itself. The active culture is chosen at startup from the `CVars.Language`
CVar, or the matched system culture if unset; `SwitchToLanguage` rebuilds the bundle and fires `LanguageSwitched`
so views re-resolve their strings. `AvailableLanguages` is the language-picker list.

**Two `.ftl` files per maintained locale:**
- `text.ftl` — upstream strings (synced from Weblate; don't hand-edit for fork features).
- `sanabi.ftl` — **fork-added strings go here.**

**Always keep `en-US` and `ru` in sync — they are the only locales the fork maintains.** Both carry a
`sanabi.ftl`; every other culture directory has only the upstream `text.ftl` (and mostly from Weblate), so fork
strings simply don't exist there and fall back to `en-US`. When you add or rename a user-facing string:
1. add the key to **both** `Assets/Locale/en-US/sanabi.ftl` and `Assets/Locale/ru/sanabi.ftl`;
2. don't bother with the other locales — leave them to fall back. Adding a half-translated key to only one of
   `en-US`/`ru` is the bug to avoid.

(Weblate stores sub-tag codes with an underscore — `zh_Hans`, `pt_BR` — which `LocalizationManager` maps back to
the hyphenated culture name; keep fork locale folders named to match what the loader expects.)
