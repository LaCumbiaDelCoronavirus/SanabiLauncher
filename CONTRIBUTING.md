# Contributing

## `LauncherInfoModel` JSON format

The launcher fetches a JSON document from `ConfigConstants.UrlLauncherInfo` (see
[`SS14.Launcher/ConfigConstants.cs`](SS14.Launcher/ConfigConstants.cs)) on startup, which is
deserialized into `LauncherInfoManager.LauncherInfoModel`
(see [`SS14.Launcher/Models/LauncherInfoManager.cs`](SS14.Launcher/Models/LauncherInfoManager.cs)).
This controls things like random home-screen messages, out-of-date detection, asset overrides,
and the changelog shown when the launcher is out of date.

If the endpoint is unreachable or returns invalid data, the launcher falls back to the JSON
embedded in `SanabiGlobal.FallbackLauncherInfoData`
(see [`Sanabi.Framework/Data/SanabiGlobal.cs`](Sanabi.Framework/Data/SanabiGlobal.cs)) — keep that
fallback in sync with whatever shape you add here.

Standard JSON doesn't support comments, so below is an annotated reference showing every field.
A real payload must **not** contain the `//` comments below.

```jsonc
{
  // Per-locale lists of random flavour-text messages shown on the home screen.
  // Only "en-US" is currently read (see LauncherInfoManager.LoadData), but the
  // dictionary shape is future-proofed for other locales.
  // Missing/empty -> no random message is shown.
  "messages": {
    "en-US": [
      "First random message",
      "Second random message"
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

      // Changelog body text for this entry. Supports newlines (\n) and is wrapped/
      // displayed as-is; no other formatting (e.g. markdown) is applied.
      "changes": "- Added a changelog.\n- Fixed some bugs."
    }
  ],

  // Optional. Absolute URL to an image shown at the bottom of the scrollable
  // changelog container. Empty string (or omitted) -> nothing is shown.
  // The downloaded bytes are sniffed by magic number before being decoded, so
  // only recognized image formats are rendered; anything else (or a failed
  // download) is silently skipped. PNG is the primary/expected format; JPEG,
  // GIF, BMP, and WEBP are also supported on a best-effort basis
  // (see MainWindowViewModel.LoadChangelogMedia / IsSupportedImage).
  "changelogMediaUrl": "https://example.com/changelog-media.png"
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
