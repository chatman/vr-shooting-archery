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

The grab model is a **posed Hand Grab**, and it has to be posed. A pose-less grab — Touch
Hand Grab, or a `HandGrabInteractable` with no `HandGrabPose` — leaves the pistol wherever
it happened to be when the fingers closed. Meta's wording: *"If no HandGrabPose is provided,
the grab will be a pose-less grab anchored at the interactable pose."* For a mug that is
fine; for a pistol it is fatal, because there is then no repeatable relationship between
where the hand points and where the barrel points, so the thing can be carried but never
aimed. A posed grab snaps the pistol to the same grip on every pickup, which is what makes
the sights land in the same place relative to the hand.

The interactor half is a one-time scene setup and is already in the committed scene: the
Interactions Rig supplies four `HandGrabInteractor`s (two tracked hands, two controller-driven).

The interactable half is two editor steps, in this order, and the second has to be repeated
whenever the first runs:

```powershell
Unity.exe -batchmode -quit -projectPath . -executeMethod AddAirPistol.Apply   -logFile pistol.log
Unity.exe -batchmode -quit -projectPath . -executeMethod PistolHandGrab.Apply -logFile grab.log
```

`AddAirPistol` (also `Quest ▸ 9b`) builds the pistol on the bench at lane 19. It is shaped to
the ISSF 10 m Air Pistol rules — inside the 420 × 200 × 50 mm box, under 1500 g, heel rest at
90° to the grip — because those rules are what make it hold like a match pistol. The grip is
**raked** 22° off vertical, giving 112° grip-to-bore, and the bore axis rides low over the
grip. An earlier version hung a vertical box under the frame at 90° to the bore, which forces
the wrist into flexion to level the sights and reads in the headset as holding a brick.

`PistolHandGrab` (also `Quest ▸ 10`) puts a `Grabbable` on the root and a `HandGrab` child
carrying a `HandGrabInteractable` with one posed grip per hand. Poses normally come from the
Hand Grab Pose Recorder, which needs Link, Play Mode and a live tracked hand and cannot be
scripted — but a pose is only data, and `HandGrabUtils` exposes all of it, so the pose is
authored instead. The finger curl is lifted from the SDK's own Torch prop, which is held the
same way a grip is; only the wrist placement is ours, and it is derived from the pistol's
geometry rather than typed in, so it survives the grip moving.

Both halves have to be present — one alone is silent in the headset. The `[Grab]` lines in
the log check for that, and report **bore-to-forearm**, the angle between the barrel and the
forearm. Near zero means the barrel lies along the arm and the pistol points where the arm
points; the current build reads 2°. If that number is large, the grip is wired but unaimable.

`Quest ▸ 10b` drops ghost hands into the scene at the authored grips so the hold can be
eyeballed in the editor without a headset; `Quest ▸ 10c` removes them again. They are
editor-only scenery — clear them before building.

### Validating the grip

`Quest ▸ 10d` measures whether the hold would work for a real hand and a real eye, against
adult-median anthropometry. Wiring being correct proves nothing about the hold: a grip can be
perfectly wired and still put the fingers through the frame or the trigger out of reach.

| Check | Current | Why it matters |
|---|---|---|
| grip girth | 147 mm | outside 130–180 mm and the hand cannot close on it |
| grip width | 34 mm | wider than a palm and the fingers cannot meet |
| finger clipping | none | joints inside solid parts show through the metal |
| grip penetration | 8.3 mm | deep penetration means the grip is fatter than the fist's tube |
| trigger reach | 28 mm | further and the index finger cannot find the trigger |
| sight picture | clear | anything the hand puts above the bore blocks the sights |
| arm extension | 570 mm | the pistol must sit where an arm can actually reach |
| wrist deviation | 23°, 0° over floor | see below |
| sight radius | 276 mm | short radius makes wobble unreadable |

**Wrist deviation is judged against a floor, not a constant.** Some wrist extension is forced
on *any* pistol here: the shoulder sits ~150 mm below and ~200 mm outside the eye, so an
extended arm reaching the eye-to-target line has to angle up and inward, and the wrist makes
up the difference. That floor is 23°. The grip adds nothing on top of it, so this is the
geometric optimum for a one-handed sighted hold — not something a better grip could improve.

**The limit worth knowing.** At this range 1° of hand wobble moves the shot **203 mm**, while
the 10 m target's scoring rings span 155 mm. Hand-tracking jitter is comfortably more than 1°,
so *scoring* precisely with tracked hands is not physically available no matter how good the
grip is. The grip is now correct; the aiming precision ceiling is the hardware's. Controllers
are substantially steadier and the rig supports them.

**On aiming with hand tracking.** Meta's own writeup of putting a gun in *First Steps* is
worth knowing: they deliberately designed the model to stop players using iron sights,
because sighting brings the hands into poses where tracking fails, and they found two-handed
holds far more stable because aim is then pivoted by translation rather than rotation. A 10 m
air pistol is the hard case — one hand, extended, sighted. Controllers track this
considerably better than hands do, and the rig supports both.

### What actually runs in the headset: the fixed-offset grab

The SDK's runtime resolution of those poses never worked on device: on the first frame of
every grab the pistol teleported hundreds of metres away, and six candidate causes were
eliminated without finding it. The **committed scene therefore uses a fixed-offset grab**,
and the posed setup above is the authoring step that feeds it:

```powershell
Unity.exe -batchmode -quit -projectPath . -executeMethod AddAirPistol.Apply       -logFile pistol.log
Unity.exe -batchmode -quit -projectPath . -executeMethod PistolHandGrab.Apply     -logFile grab.log
Unity.exe -batchmode -quit -projectPath . -executeMethod UseFixedOffsetGrab.Apply -logFile fixed.log
```

`UseFixedOffsetGrab` (also `Quest ▸ 11`) harvests each `HandGrabPose`'s transform relative to
the pistol onto a `GrabbableItem` component as the wrist pose in pistol space, then strips
`Grabbable`, `HandGrabInteractable` and the `HandGrab` child — they must go, because the
rig's four `HandGrabInteractor`s are still live and would otherwise keep seizing the pistol.

At runtime `PinchGrab` (self-installing, no scene wiring) reads the joints under
`OVRHandVisualLeft/Right` — `XRHand_Wrist`, `XRHand_IndexTip`, `XRHand_ThumbTip` — and treats
thumb-to-index under 25 mm as a pinch (40 mm to release). A pinch within 0.18 m of the pistol
places it by arithmetic: `rotation = wrist × inverse(wristLocal)`,
`position = wrist − rotation × wristLocalPos`. The grip a shooter feels is the authored one;
only the machinery differs, so the pistol snaps to the same aimable hold on every pickup.
`GrabbableItem` also returns the pistol to the bench if it falls 0.6 m or strays 3 m.
`PistolWatchdog` is diagnostic only and can be removed.

The Interactions Rig carries its own hand data sources, `HandVisual`s and controller visuals,
so `RangeHands` no longer attaches an `OVRHandPrefab` or `OVRControllerPrefab` of its own —
it removes them if it finds them, since both sets together draw two hands in one place. It
still sets `OVRProjectConfig.handTrackingSupport` and `OVRManager.handTrackingSupport`, which
the rig does not do.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Pistol does not respond to a grab | `[Grab] ready` missing from the log: `GrabbableItem` has no `hasGrips` (re-run `Quest ▸ 10` then `Quest ▸ 11`) or the `OVRHandVisual*` joints were not found |
| Pistol vanishes on grab | SDK `Grabbable`/`HandGrabInteractable` are back on the pistol; re-run `Quest ▸ 11` to strip them |
| Pistol is held but cannot be aimed | The grab went pose-less — `posed grips` in the log is under 2, or bore-to-forearm is large; re-run `Quest ▸ 10` |
| Pistol sits beside the hand, not in it | `AddAirPistol` re-ran after `Quest ▸ 10` and took the grip with it; re-run `Quest ▸ 10` |
| Ghost hands in the build | `Quest ▸ 10b` preview was left in the scene; run `Quest ▸ 10c` |
| Pistol falls through the bench when released | `Grabbable` left kinematic; `PistolHandGrab` sets `throwWhenUnselected`, re-run `Quest ▸ 10` |
| Two hands drawn in the same place | A legacy `OVRHandPrefab` is back under a hand anchor; re-run `RangeHands.Apply`, which strips them |
| `adb devices` says `unauthorized` | The in-headset USB debugging prompt was never accepted |
| Black screen in the headset | Oculus unticked in XR Plug-in Management → Android; run `Quest ▸ 1b` |
| Hall is black but the app runs | Lighting never baked; run `Quest ▸ 5` |
| Glass wall is an opaque grey sheet | `Tools ▸ Range ▸ Fix Materials` did not run |
| Everything is magenta | URP shaders left over on the built-in pipeline; re-run `ToBuiltIn.Apply` |
| App installs but is nowhere | It is under **Library ▸ Unknown Sources** |
| Build fails on missing Android SDK | Preferences → External Tools — tick all three "Unity installed" boxes |
