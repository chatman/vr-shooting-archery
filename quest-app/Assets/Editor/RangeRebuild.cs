using UnityEditor;
using UnityEngine;

/// <summary>
/// Rebuilds the scene from the project's own code path, then re-applies the two
/// changes that are genuinely needed.
///
/// The device log shows OVRManager.InitOVRManager() throwing a
/// NullReferenceException, followed by a GPU page fault (kgsl-3d0, write
/// translation fault, UCHE, vk_any) and LCnt=0/1 — no composition layer ever
/// submitted, hence a black display regardless of scene content.
///
/// The rig in the saved scene has been edited repeatedly by ad-hoc scripts, so
/// rather than patch it further this regenerates it from ArcheryRangeSetup, which
/// instantiates a clean OVRCameraRig prefab with a fresh OVRManager.
///
/// Deliberately NOT re-adding the TrackedPoseDriver yet: the first thing to
/// establish is that the app renders anything at all. Head tracking is the next
/// step, once there is a picture to track.
/// </summary>
public static class RangeRebuild
{
    public static void Apply()
    {
        Debug.Log("[Rebuild] 1/3 building scene from ArcheryRangeSetup...");
        ArcheryRangeSetup.BuildRangeScene();

        Debug.Log("[Rebuild] 2/3 simplifying lighting...");
        RangeSimplify.Simplify();

        Debug.Log("[Rebuild] 3/3 standing the player at firing point 23...");
        RangeSpawnCenter.MoveToFiringPoint();

        Debug.Log("[Rebuild] done — fresh OVRCameraRig, simple lighting, no TrackedPoseDriver.");
    }
}
