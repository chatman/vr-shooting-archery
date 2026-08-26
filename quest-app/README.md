# quest-app — the 10 m hall on a Meta Quest 3

Unity 6000.5.10f1 + Meta XR SDK 205.0.0, on the built-in render pipeline. Consumes
`../output/range_10m/unity/` and builds a standalone Quest 3 APK. The design notes live in
the [repository README](../README.md#quest-3-app-unity); this file is how to run it.

## Prerequisites

- **Unity 6000.5.10f1** with **Android Build Support** (SDK, NDK and OpenJDK sub-modules).
  Other patch releases are fine — Meta XR SDK 205 needs ≥ 6000.0.66f2 — but Unity will want
  to upgrade the project, so expect a reimport.
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
| `2. Set Up URP (Mobile)` | **superseded** — the project is on the built-in pipeline; see below |
| `3. Import Range Model` | applies the importer settings the model's README specifies |
| `4. Build Range Scene` | places the hall and the player, runs the three `Tools ▸ Range` fix-ups |
| `5. Bake Lighting (slow)` | **required** — the hall is black without it |

Items 1–4 also run headlessly in one shot:

```powershell
Unity.exe -batchmode -quit -nographics -projectPath . `
  -buildTarget Android -executeMethod ArcheryRangeSetup.ConfigureAll -logFile setup.log
```

The project renders on the **built-in pipeline**, not URP. A minimal control project reads
crisply under built-in and softly under URP at an identical eye buffer: built-in renders
straight into the XR eye swapchain, while URP goes through an intermediate texture and blits
whenever HDR, MSAA, render scale ≠ 1 or post-processing is on — and this project had MSAA 4
and render scale 1.5, so it always blitted. `ToBuiltIn.Apply` is what moved it across; it
unassigns the pipeline asset *and* remaps every URP shader to its built-in equivalent, which
have to happen together or the hall turns magenta:

```powershell
Unity.exe -batchmode -quit -projectPath . -executeMethod ToBuiltIn.Apply -logFile birp.log
```

Item 5 is deliberately separate: it is slow, and it must not run with `-nographics` on a
machine whose lightmapper needs a graphics context.

## Where the player starts

Firing point 23, at `(2.5, 0, -11.75)`, facing downrange (+Z), with `OVRCameraRig` set to
**Floor Level** tracking origin. Downrange is +Z; the target wall is z = 0 and the glazed
entrance wall is z = −30. To move the spawn, edit `SpawnPosition` in
`Assets/Editor/ArcheryRangeSetup.cs` and re-run `Quest ▸ 4`, or just drag the rig in the
scene.

## Picking up the pistol

The grab model is **Touch Hand Grab**: the grab starts when the fingertips overlap the pistol
and ends when the fingers open past the spread cached at selection. Nothing has to be authored
per object — no recorded `HandGrabPose`, the colliders are the grab surface.

The interactor half is a one-time scene setup and is already in the committed scene. On a
fresh scene, install the **Touch Hand Grab** Building Block (`Meta ▸ Tools ▸ Building Blocks`),
which pulls in the Interactions Rig it depends on, then delete the demo cube it drops in.

The interactable half is two editor steps, in this order, and the second has to be repeated
whenever the first runs:

```powershell
Unity.exe -batchmode -quit -projectPath . -executeMethod AddAirPistol.Apply    -logFile pistol.log
Unity.exe -batchmode -quit -projectPath . -executeMethod PistolTouchGrab.Apply -logFile grab.log
```

`AddAirPistol` builds the pistol on the bench at lane 19 with a rigidbody and a collider.
`PistolTouchGrab` (also `Quest ▸ 10`) puts a `Grabbable` on the root and a `TouchHandGrab`
child carrying a `TouchHandGrabInteractable` that reads the root's collider as its grab
surface. Both halves have to be present — one alone is silent in the headset, which is what
the `[Grab]` lines in the log check for.

The Interactions Rig carries its own hand data sources, `HandVisual`s and controller visuals,
so `RangeHands` no longer attaches an `OVRHandPrefab` or `OVRControllerPrefab` of its own —
it removes them if it finds them, since both sets together draw two hands in one place. It
still sets `OVRProjectConfig.handTrackingSupport` and `OVRManager.handTrackingSupport`, which
the rig does not do.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Pistol does not respond to a grab | Only one half is installed; check the `[Grab]` interactor/interactable counts in the log |
| Pistol falls through the bench when released | `Grabbable` left kinematic; `PistolTouchGrab` sets `throwWhenUnselected`, re-run `Quest ▸ 10` |
| Two hands drawn in the same place | A legacy `OVRHandPrefab` is back under a hand anchor; re-run `RangeHands.Apply`, which strips them |
| `adb devices` says `unauthorized` | The in-headset USB debugging prompt was never accepted |
| Black screen in the headset | Oculus unticked in XR Plug-in Management → Android; run `Quest ▸ 1b` |
| Hall is black but the app runs | Lighting never baked; run `Quest ▸ 5` |
| Glass wall is an opaque grey sheet | `Tools ▸ Range ▸ Fix Materials` did not run |
| Everything is magenta | URP shaders left over on the built-in pipeline; re-run `ToBuiltIn.Apply` |
| App installs but is nowhere | It is under **Library ▸ Unknown Sources** |
| Build fails on missing Android SDK | Preferences → External Tools — tick all three "Unity installed" boxes |
