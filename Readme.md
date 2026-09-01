SanabiLauncher is a fork of the launcher for SS14.

# Installation Guide

1. [Head to the installation link](https://github.com/LaCumbiaDelCoronavirus/SanabiLauncher/releases/latest)
2. Download .zip corresponding to your system
3. Extract downloaded .zip somewhere convenient - you will need to access the executable in it. Create a desktop shortcut or something.
4. Open the executable that is extracted. You are done.

# Development

TODO:
- [ ] API for mods to evade screengrabbing measures
- [x] Test whether windowed-borderless mode actually works on linux
- - Tested: it doesn't work
  - **LINUX GODS**: Try downloading SDL3 library (`sudo apt install libsdl3-dev`, etc.) *into* the launcher directory OR making a symlink to an existing instance of SDL3 in the launcher folder. Launcher folder may be where you installed it, maybe there but `/bin`, who knows.

Features include:
- Patchloader, with mod template here https://github.com/LaCumbiaDelCoronavirus/TemplateSanabiMod
- Compatible with [Marseyloader](https://github.com/ValidHunters/Marseyloader) patches [(example here)](https://github.com/ValidHunters/SubversionExamplePatch)
- - Comes with built-in patches including but not limited to:
- - - HWId spoof patch
    - Patch to replace RT's built-in fullscreen mode with a borderless-windowed mode. Will crash your game on any OS other than windows/linux.
- - Support for externally-loaded mod assemblies (`.dll`s, see button to open patch directory somewhere in Sanabi tab), can be selectively loaded (maximum of 64 mods in directory)
  - Support for externally-loaded resources
  - Support for all of that, compressed into a `.zip`
  - Suspicious launcher-related assemblies (e.g. patch assemblies) are hidden from the game
- Gives you access to tent for camouflage against drones
- - Account tokens are updated only for individual accounts, and only when they are in use
  - You can use the launcher when logged-out as if you are logged in
  - Option to start the launcher from the login page if you don't want to fetch statuses of servers on favourites tab and hubserver for whatever reason
  - Allows you to change the default hub API used for the server tabs (however it defaults to the [wizden hub *mirror*](https://github.com/LaCumbiaDelCoronavirus/SanabiLauncher/blob/9d340ad0998191e7b3b7f21a19bca162e6679af9/SS14.Launcher/ConfigConstants.cs#L43-L49))
  - Per-account settings; you can have settings be different values for specific accounts
  - - Account seed:
    - - When the HWId spoofing patch is enabled and active, the randomly generated HWId uses this seed to stay the same if the seed is the same.
      - Also used for spoofing of the launcher fingerprint; the unique header which the launcher sends in every HTTP request, that can be used as a vector of detection.
  - Options to disable aforementioned launcher fingerprint
- UserData virtualisation (AdminInfoSystem GEEEEEEEEEG)

Info for mod development:
- Mod with resources and optional DLL loaded into the game
- - Folder with
  - - optional `.dll` inside, being the loaded assembly, this is not required so if not found then nothing will happen
    - Folder named `Resources` inside, being the resources mounted to the game
- Packed mod with resources and optional DLL loaded into the game
- - Exactly the same as the above type of mod, but is its contents compressed into a `.zip`.
  - 45MiB size limit
- Standalone DLLs loaded into the game
- - Simply a `.dll` added to the mods folder
  - Only loads a mod assembly into the game

Info for mod-ASSEMBLY (mods with `.dll`s) development:
- Entrypoint
- - You must have a static class with no namespace in your main assembly. This is your entry-point class.
- The entry class must have one of the following names, otherwise it won't be recognised:
- - PatchEntry
    - ModEntry
    - MarseyEntry
- The loader recognises two different optional main entry-point methods on the entry class:
- - 1. Static method named `Entry` that takes a parameter of type `Dictionary<string, Assembly>`; this dictionary will be populated with every loaded game-assembly known by the launcher thus far, with the string being the assembly's name. This will normally include:
    - - - Content.Client
        - Content.Shared
        - Robust.Client
        - Robust.Shared
    - 2. Static method named `Entry` that takes a parameter of type `string`; this is the disk-path where data for mods will be stored. If you want to use this, you should ensure that a folder for your mod data exists.
    - 3. Static method named `Entry` that has the parameters of the above two combined, with the first parameter being the assembly-dictionary and second being the path-string.
    - 4. Static method named `Entry` that takes no parameters
- The loader recognises one more optional method, that being a static method named `AfterEntitySystemsLoaded` on the entry class that takes no parameters. This method is called immediately after every one of the game's `EntitySystem`s have finished loading. This is necessary as you can't resolve any entity systems before this point, otherwise an exception is thrown.
- The mod assembly is loaded like any other game assembly.
