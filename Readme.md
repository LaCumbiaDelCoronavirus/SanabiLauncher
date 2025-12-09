SanabiLauncher is a fork of the launcher for SS14.

Features include:
- Decent patch-loader compatible with [Marseyloader](https://github.com/ValidHunters/Marseyloader) patches [(example here)](https://github.com/ValidHunters/SubversionExamplePatch)
- - Comes with built-in patches including but not limited to:
- - - HWId spoof patch
    - All-commands-enabled patch
    - Ahelp-menu-popup-disabled patch
- - Support for externally-loaded `.dll` patches (see button to open patch directory somewhere in settings)
  - Suspicious launcher-related assemblies (e.g. patch assemblies) are hidden from the game
- Has actual operational security
- - Account tokens are updated only for individual accounts, and only when they are in use
  - Option to not log-in on launcher start
  - You can use the launcher when logged-out as if you are logged in
  - Option to start the launcher from the login page, and defer *all* web API calls until off of the login page (this option exists if you are paranoid *and know what you're doing*)
  - Allows you to change the default hub API used for the server tabs (however it defaults to the [wizden hub *mirror*](https://cdn.spacestationmultiverse.com/wizden-hub-mirror/))
