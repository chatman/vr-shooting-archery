# quest-app — the 10 m hall on a Meta Quest 3

Unity 6000.0.81f1 + Meta XR SDK 205.0.0. Consumes `../output/range_10m/unity/` and builds
a standalone Quest 3 APK. The design notes live in the [repository README](../README.md#quest-3-app-unity);
this file is how to run it.

## Prerequisites

- **Unity 6000.0.81f1** with **Android Build Support** (SDK, NDK and OpenJDK sub-modules).
  Other 6000.0.x patch releases are fine — Meta XR SDK 205 needs ≥ 6000.0.66f2 — but Unity
  will want to upgrade the project, so expect a reimport.
- **adb** on `PATH` (`winget install Google.PlatformTools`).
- A Quest 3 in Developer Mode.

`deploy.ps1` finds Unity itself from `ProjectVersion.txt`, checking the usual Hub install
locations. Override with `$env:UNITY_EDITOR` if yours lives somewhere else.

## Build and run

```powershell
./deploy.ps1              # build, install, launch
./deploy.ps1 -SkipBuild   # reinstall the last APK
./deploy.ps1 -NoLaunch    # install without starting it
```

First IL2CPP build is 5–6 minutes; later ones are faster. The app installs as
`com.ishanxr.vrshootingarchery` and appears under **Library ▸ Unknown Sources**.

## Rebuilding from a clean clone

`Library/` is not tracked, so the first open takes 10–20 minutes while Unity resolves the
Meta XR SDK and reimports. Then run the **Quest** menu in order:

| Menu item | What it does |
|---|---|
| `1. Configure Player Settings` | Android, IL2CPP, ARM64, Vulkan, min SDK 32, Single Pass Instanced |
| `1b. Enable Oculus XR Loader` | the step whose omission gives you a black screen |
| `2. Set Up URP (Mobile)` | creates and assigns the pipeline asset `RangeSetup` needs |
| `3. Import Range Model` | applies the importer settings the model's README specifies |
| `4. Build Range Scene` | places the hall and the player, runs the three `Tools ▸ Range` fix-ups |
| `5. Bake Lighting (slow)` | **required** — the hall is black without it |

Items 1–4 also run headlessly in one shot:

```powershell
Unity.exe -batchmode -quit -nographics -projectPath . `
  -buildTarget Android -executeMethod ArcheryRangeSetup.ConfigureAll -logFile setup.log
```

Item 5 is deliberately separate: it is slow, and it must not run with `-nographics` on a
machine whose lightmapper needs a graphics context.

## Where the player starts

Firing point 23, at `(2.5, 0, -11.75)`, facing downrange (+Z), with `OVRCameraRig` set to
**Floor Level** tracking origin. Downrange is +Z; the target wall is z = 0 and the glazed
entrance wall is z = −30. To move the spawn, edit `SpawnPosition` in
`Assets/Editor/ArcheryRangeSetup.cs` and re-run `Quest ▸ 4`, or just drag the rig in the
scene.

## Troubleshooting

| Symptom | Cause |
|---|---|
| `adb devices` says `unauthorized` | The in-headset USB debugging prompt was never accepted |
| Black screen in the headset | Oculus unticked in XR Plug-in Management → Android; run `Quest ▸ 1b` |
| Hall is black but the app runs | Lighting never baked; run `Quest ▸ 5` |
| Glass wall is an opaque grey sheet | `Tools ▸ Range ▸ Fix Materials (URP)` did not run, or the project is not on URP |
| Everything is magenta | Project is not on URP; run `Quest ▸ 2` |
| App installs but is nowhere | It is under **Library ▸ Unknown Sources** |
| Build fails on missing Android SDK | Preferences → External Tools — tick all three "Unity installed" boxes |
