using System.Collections.Generic;
using System.Linq;
using Oculus.Interaction;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Makes the air pistol pickable with the tracked hands, using Touch Hand Grab.
///
/// Touch Hand Grab is a collisions-based grab model: selection begins when the fingertips
/// actually overlap the interactable, and release happens when the fingers open past the
/// spread that was cached at selection. That is a closer match to lifting a pistol off a
/// bench than the older HandGrabInteractor, which picks a grab pose by spatial heuristic
/// and needs a recorded HandGrabPose per object to look right. Nothing has to be authored
/// per object here -- the colliders are the grab surface.
///
/// Two halves have to meet for a grab to happen, and having only one is the usual reason
/// nothing responds in the headset:
///
///   Interactor side. Installed by the "Touch Hand Grab" Building Block, which pulls in
///   the Interactions Rig as a dependency and puts a TouchHandGrabInteractor on each hand
///   under OVRCameraRig. This script does not install it -- the block is a one-time scene
///   setup -- but it refuses to run if the interactors are missing, since the result would
///   be a pistol that looks wired and never responds.
///
///   Interactable side, which is this script's job. The pistol gets a Grabbable on the
///   root, pointed at the rigidbody AddAirPistol already made, and a TouchHandGrab child
///   carrying a TouchHandGrabInteractable that reads the root's collider as its grab
///   surface and reports selections back to that Grabbable.
///
/// The shape mirrors the block's own demo cube exactly: Grabbable on the root, interactable
/// on a child, both pointed back at the root's rigidbody and collider.
///
/// Run this after AddAirPistol, and again whenever AddAirPistol re-runs, since that
/// rebuilds the pistol from scratch and takes the grab components with it.
/// </summary>
public static class PistolTouchGrab
{
    const string ScenePath = "Assets/Scenes/Range.unity";
    const string PistolName = "AirPistol_Lane19";
    const string InteractionChild = "TouchHandGrab";

    [MenuItem("Quest/10. Grab the Pistol (Touch Hand Grab)")]
    public static void Apply()
    {
        var scene = EditorSceneManager.OpenScene(ScenePath);

        var pistol = GameObject.Find(PistolName);
        if (pistol == null)
        {
            Debug.LogError($"[Grab] no {PistolName} in the scene. Run AddAirPistol.Apply first.");
            return;
        }

        var body = pistol.GetComponent<Rigidbody>();
        var collider = pistol.GetComponent<Collider>();
        if (body == null || collider == null)
        {
            // Grabbable would drive a rigidbody that is not there, and the interactable
            // would have no grab surface at all.
            Debug.LogError($"[Grab] {PistolName} needs a Rigidbody and a Collider. " +
                           "Re-run AddAirPistol.Apply.");
            return;
        }

        if (!InteractorsPresent()) return;

        ClearPreviousInteraction(pistol);

        var grabbable = pistol.GetComponent<Grabbable>() ?? pistol.AddComponent<Grabbable>();
        grabbable.InjectOptionalRigidbody(body);
        grabbable.InjectOptionalTargetTransform(pistol.transform);
        // Kinematic while held so the hand drives it rather than the solver fighting the
        // hand; thrown with the hand's velocity on release, which is what makes setting it
        // back down on the bench behave.
        grabbable.InjectOptionalKinematicWhileSelected(true);
        grabbable.InjectOptionalThrowWhenUnselected(true);

        var child = new GameObject(InteractionChild);
        child.transform.SetParent(pistol.transform, false);

        var interactable = child.AddComponent<TouchHandGrabInteractable>();
        interactable.InjectOptionalPointableElement(grabbable);
        interactable.InjectAllTouchHandGrabInteractable(collider, new List<Collider> { collider });

        EditorUtility.SetDirty(grabbable);
        EditorUtility.SetDirty(interactable);
        EditorSceneManager.MarkSceneDirty(scene);
        EditorSceneManager.SaveScene(scene, ScenePath);
        AssetDatabase.SaveAssets();

        Verify(pistol);
    }

    /// <summary>
    /// The interactor half comes from the Building Block, not from here. Check it landed
    /// rather than wiring an interactable that nothing will ever talk to.
    /// </summary>
    static bool InteractorsPresent()
    {
        var interactors = Object.FindObjectsByType<TouchHandGrabInteractor>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (interactors.Length > 0) return true;

        Debug.LogError(
            "[Grab] no TouchHandGrabInteractor in the scene. Install the \"Touch Hand Grab\" " +
            "Building Block first (Meta ▸ Tools ▸ Building Blocks), which also brings in the " +
            "Interactions Rig it depends on.");
        return false;
    }

    /// <summary>
    /// Drops the interaction child from a previous run so re-running does not stack a
    /// second interactable on the pistol. The Grabbable on the root is reused rather than
    /// removed -- removing it would break any reference already pointing at it.
    /// </summary>
    static void ClearPreviousInteraction(GameObject pistol)
    {
        var stale = pistol.transform.Cast<Transform>()
            .Where(c => c.GetComponent<TouchHandGrabInteractable>() != null ||
                        c.name == InteractionChild)
            .Select(c => c.gameObject)
            .ToList();

        foreach (var go in stale)
        {
            Debug.Log($"[Grab] removed previous {go.name}");
            Object.DestroyImmediate(go);
        }
    }

    /// <summary>
    /// Report what actually ended up in the scene rather than trusting the wiring above.
    /// Both halves have to be present; either one alone is silent at runtime.
    /// </summary>
    static void Verify(GameObject pistol)
    {
        var grabbable = pistol.GetComponent<Grabbable>();
        var interactable = pistol.GetComponentInChildren<TouchHandGrabInteractable>(true);
        var interactors = Object.FindObjectsByType<TouchHandGrabInteractor>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        Debug.Log($"[Grab] pistol: Grabbable={grabbable != null} " +
                  $"TouchHandGrabInteractable={interactable != null}");
        Debug.Log($"[Grab] interactors in the scene: {interactors.Length} touch hand grab " +
                  $"({string.Join(", ", interactors.Select(i => i.name))})");

        if (interactable != null)
        {
            var so = new SerializedObject(interactable);
            var bounds = so.FindProperty("_boundsCollider").objectReferenceValue;
            var element = so.FindProperty("_pointableElement").objectReferenceValue;
            Debug.Log($"[Grab] interactable wiring: boundsCollider={NameOf(bounds)} " +
                      $"pointableElement={NameOf(element)}");
        }

        bool ok = grabbable != null && interactable != null && interactors.Length > 0;
        Debug.Log(ok
            ? "[Grab] both halves are present -- the pistol should lift off the bench."
            : "[Grab] WARNING: one half is missing; nothing will respond in the headset.");
    }

    static string NameOf(Object o) => o == null ? "<none>" : o.name;
}
