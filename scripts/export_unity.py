"""Convert the hall to an FBX that Unity imports natively, for Meta Quest 3.

Unity does not read .glb without a package (glTFast or UnityGLTF). FBX it reads out of
the box, so that is what ships; the GLB is copied alongside for anyone who would rather
add glTFast and keep the PBR materials intact.

Blender does the conversion rather than a hand-rolled FBX writer: the format is fiddly
enough that a trusted exporter is worth the dependency, and it doubles as the checker --
the FBX is re-imported into an empty scene afterwards and measured, so a silent unit or
axis change fails here instead of in Unity.

What comes out (output/range_10m/unity/):
    range_10m.fbx        15 meshes, one per material, faces flat-shaded
    Textures/*.png       unpacked from the GLB, referenced relatively by the FBX
    range_10m.glb        the original, for the glTFast route
    lights.json          the 35 ceiling lamps: position + candela, which FBX cannot carry
    RangeSetup.cs        Unity editor menu: URP materials, baked lamps, colliders
    README.md            import settings and Quest 3 notes (hand-written, not generated)

RangeSetup.cs is generated from the GLB's own materials and lights, not hand-written, so
the Unity side cannot drift from the model when the hall is rebuilt.

usage: blender --background --python scripts/export_unity.py -- [OUTDIR]
"""

import json
import os
import shutil
import sys

import bpy

ROOT = "/home/ishan/code/vrshooting"
GLB = os.path.join(ROOT, "output", "range_10m", "range_10m.glb")
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
OUTDIR = argv[0] if argv else os.path.join(ROOT, "output", "range_10m", "unity")

HALL = (42.0, 30.0, 6.0)          # x, downrange, height -- what the round trip must measure


def clean_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    bpy.ops.import_scene.gltf(filepath=path)
    return [o for o in bpy.context.scene.objects if o.type == "MESH"]


def write_textures(outdir):
    """Unpack the GLB's images to disk, named after their material.

    They arrive packed inside the .blend, with an empty filepath, so the FBX exporter's
    "copy textures" mode has nothing to copy from and silently writes none. Saving them
    out first and exporting relative paths is what actually gets textures to Unity.
    Folder is `Textures` because that is the name Unity's importer looks for when it
    resolves a model's texture references.
    """
    texdir = os.path.join(outdir, "Textures")
    os.makedirs(texdir, exist_ok=True)
    written = []
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        for node in mat.node_tree.nodes:
            if node.type == "TEX_IMAGE" and node.image:
                img = node.image
                img.name = mat.name
                img.filepath_raw = os.path.join(texdir, mat.name + ".png")
                img.file_format = "PNG"
                img.save()
                written.append(mat.name + ".png")
    return written


def export_fbx(path):
    # Blender computes "relative" texture paths against the .blend, and with no .blend
    # saved it silently falls back to absolute ones -- which bakes this machine's
    # directory layout into a file meant to be committed and opened elsewhere. Saving a
    # throwaway .blend beside the FBX first is what makes RELATIVE actually relative.
    scratch = os.path.splitext(path)[0] + ".blend"
    bpy.ops.wm.save_as_mainfile(filepath=scratch)
    bpy.ops.file.make_paths_relative()

    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=False,
        object_types={"MESH"},          # lights ride in lights.json instead; see module doc
        apply_unit_scale=True,
        global_scale=1.0,
        apply_scale_options="FBX_SCALE_NONE",
        bake_space_transform=False,     # True quietly rotates meshes relative to their nodes
        axis_forward="-Z",
        axis_up="Y",                    # the Blender -> Unity pair: +Y forward, +Z up
        mesh_smooth_type="FACE",        # the hall is faceted by design, not smooth
        use_triangles=True,
        use_tspace=False,               # Unity recalculates tangents on import
        path_mode="RELATIVE",       # textures already sit in Textures/ beside the FBX
        embed_textures=False,
    )
    os.remove(scratch)


def bounds(objs):
    xs, ys, zs = [], [], []
    for o in objs:
        for corner in o.bound_box:
            v = o.matrix_world @ __import__("mathutils").Vector(corner)
            xs.append(v.x); ys.append(v.y); zs.append(v.z)
    return (max(xs) - min(xs), max(ys) - min(ys), max(zs) - min(zs)), (min(xs), min(ys), min(zs))


def read_gltf(glb_path):
    import struct
    data = open(glb_path, "rb").read()
    off, chunks = 12, {}
    while off < len(data):
        clen, ctype = struct.unpack("<II", data[off:off + 8])
        chunks[ctype] = data[off + 8: off + 8 + clen]
        off += 8 + clen + (-clen % 4)
    return json.loads(chunks[0x4E4F534A])


def materials_table(g):
    out = []
    for m in g["materials"]:
        pbr = m.get("pbrMetallicRoughness", {})
        base = pbr.get("baseColorFactor", [1, 1, 1, 1])
        out.append(dict(name=m["name"],
                        color=[round(c, 4) for c in base[:3]],
                        alpha=round(base[3] if len(base) > 3 else 1.0, 3),
                        rough=round(float(pbr.get("roughnessFactor", 1.0)), 3),
                        metal=round(float(pbr.get("metallicFactor", 0.0)), 3),
                        emissive=[round(c, 3) for c in m.get("emissiveFactor", [0, 0, 0])],
                        double=bool(m.get("doubleSided", False)),
                        tex="baseColorTexture" in pbr))
    return out


def lights_json(glb_path, out):
    """Pull the punctual lights straight out of the GLB. FBX has lights, but Unity's FBX
    importer ignores them unless asked and gives them no usable intensity, so they travel
    as data and get rebuilt by RangeLights.cs."""
    g = read_gltf(glb_path)
    defs = g.get("extensions", {}).get("KHR_lights_punctual", {}).get("lights", [])
    lamps = []
    for node in g["nodes"]:
        ref = node.get("extensions", {}).get("KHR_lights_punctual")
        if ref is None:
            continue
        d = defs[ref["light"]]
        x, y, z = node.get("translation", [0, 0, 0])
        # glTF -Z is downrange; Unity +Z is forward. Same flip the FBX gets.
        lamps.append({"x": round(x, 4), "y": round(y, 4), "z": round(-z, 4),
                      "candela": d.get("intensity", 100.0),
                      "color": d.get("color", [1, 1, 1]),
                      "range": d.get("range", 12.0)})
    json.dump({"note": "positions are Unity metres, +Z downrange; intensity is candela",
               "lights": lamps}, open(out, "w"), indent=1)
    return lamps


def main():
    os.makedirs(OUTDIR, exist_ok=True)
    fbx = os.path.join(OUTDIR, "range_10m.fbx")

    clean_scene()
    meshes = import_glb(GLB)
    textures = write_textures(OUTDIR)
    dim, _ = bounds(meshes)
    print("imported {} meshes, glTF frame {:.2f} x {:.2f} x {:.2f} m".format(
        len(meshes), *dim))

    export_fbx(fbx)
    print("wrote {} ({:.2f} MB) + {} textures: {}".format(
        os.path.basename(fbx), os.path.getsize(fbx) / 1e6, len(textures),
        ", ".join(textures)))
    if not textures:
        sys.exit("no textures were written")

    shutil.copy(GLB, os.path.join(OUTDIR, "range_10m.glb"))
    lamps = lights_json(GLB, os.path.join(OUTDIR, "lights.json"))
    print("lights.json: {} lamps at {:g} cd".format(len(lamps), lamps[0]["candela"]))

    mats = materials_table(read_gltf(GLB))
    write_unity_scripts(OUTDIR, mats, lamps)
    print("RangeSetup.cs: {} materials, {} lamps".format(len(mats), len(lamps)))

    # --- check: re-import the FBX we just wrote and measure it ------------------
    clean_scene()
    bpy.ops.import_scene.fbx(filepath=fbx)
    back = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    dim2, lo = bounds(back)
    ok = all(abs(a - b) < 0.02 for a, b in zip(sorted(dim2), sorted(HALL)))
    print("round trip: {} meshes, {:.2f} x {:.2f} x {:.2f} m  [{}]".format(
        len(back), *dim2, "ok" if ok else "FAIL"))
    if not ok or len(back) != len(meshes):
        sys.exit("FBX round trip does not measure {} x {} x {} m".format(*HALL))

    # Orientation, checked rather than assumed: Blender's +Y is Unity's +Z, so the target
    # wall must come back on Y=0 and the glazed wall 30 m along -Y. If that ever flips,
    # lights.json's z negation and the README's coordinates are both wrong.
    by = {o.name.split(".")[0]: o for o in back}
    wall_y = by["wall_wood"].matrix_world.translation.y + by["wall_wood"].location.y * 0
    glass_y = sum((by["glass"].matrix_world @ __import__("mathutils").Vector(c)).y
                  for c in by["glass"].bound_box) / 8.0
    wall_y = sum((by["wall_wood"].matrix_world @ __import__("mathutils").Vector(c)).y
                 for c in by["wall_wood"].bound_box) / 8.0
    print("orientation: target wall y={:+.2f}, glazing y={:+.2f} "
          "-> Unity targets z=0, glass z=-30".format(wall_y, glass_y))
    if abs(wall_y) > 0.05 or abs(glass_y + HALL[1]) > 0.05:
        sys.exit("axis mapping is not the expected Blender/Unity pair")


# ---------------------------------------------------------------- Unity side

CS_TEMPLATE = """// Generated by scripts/export_unity.py from range_10m.glb -- do not hand-edit; rebuild
// the hall and re-run the exporter instead, so Unity cannot drift from the model.
//
// Put this file anywhere under Assets/Editor/. Three menu items under Tools > Range:
//   Fix Materials (URP)   FBX carries a Phong approximation, so smoothness, metallic,
//                         emission and the two glass materials all arrive wrong. This
//                         restores them from the glTF values.
//   Build Ceiling Lights  FBX cannot carry usable lights; these are the model's own 35
//                         lamps, created as BAKED point lights.
//   Add Colliders         Mesh colliders on the surfaces you can walk into, nothing else.
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class RangeSetup
{
    // Unity's point-light intensity is a unitless multiplier, not photometric candela.
    // This divisor converts; raise it if the bake comes out hot, lower it if flat.
    const float CandelaPerUnit = 1000f;

    struct Mat { public string name; public Color color; public float alpha, rough, metal;
                 public Color emissive; public bool twoSided; }

    static readonly Mat[] Materials = new Mat[] {
__MATERIALS__
    };

    static readonly Vector4[] Lamps = new Vector4[] {   // x, y, z, candela
__LAMPS__
    };

    [MenuItem("Tools/Range/Fix Materials (URP)")]
    static void FixMaterials()
    {
        Shader lit = Shader.Find("Universal Render Pipeline/Lit");
        if (lit == null) { Debug.LogError("URP/Lit not found - is this project on URP?"); return; }

        int touched = 0;
        foreach (Mat m in Materials)
        {
            foreach (string guid in AssetDatabase.FindAssets("t:Material " + m.name))
            {
                var mat = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
                if (mat == null || mat.name != m.name) continue;

                mat.shader = lit;
                Color c = m.color; c.a = m.alpha;
                mat.SetColor("_BaseColor", c);
                mat.SetFloat("_Smoothness", 1f - m.rough);   // glTF roughness is the inverse
                mat.SetFloat("_Metallic", m.metal);
                mat.SetFloat("_Cull", m.twoSided ? 0f : 2f); // 0 = render both faces

                if (m.emissive.maxColorComponent > 0f)
                {
                    mat.EnableKeyword("_EMISSION");
                    mat.SetColor("_EmissionColor", m.emissive);
                    mat.globalIlluminationFlags = MaterialGlobalIlluminationFlags.BakedEmissive;
                }

                if (m.alpha < 1f)
                {
                    // URP transparency is a set of switches, not one property; miss any of
                    // them and the glass renders as an opaque sheet.
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                }
                EditorUtility.SetDirty(mat);
                touched++;
            }
        }
        AssetDatabase.SaveAssets();
        Debug.Log($"Range: set up {touched} materials.");
    }

    [MenuItem("Tools/Range/Build Ceiling Lights")]
    static void BuildLights()
    {
        var root = new GameObject("Range Ceiling Lights");
        Undo.RegisterCreatedObjectUndo(root, "Build Ceiling Lights");
        foreach (Vector4 l in Lamps)
        {
            var go = new GameObject("Lamp");
            go.transform.SetParent(root.transform);
            go.transform.position = new Vector3(l.x, l.y, l.z);
            var light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 12f;
            light.color = new Color(1f, 0.97f, 0.92f);
            light.intensity = l.w / CandelaPerUnit;
            // Baked, not realtime: 35 realtime point lights is not a Quest 3 budget.
            light.lightmapBakeType = LightmapBakeType.Baked;
            light.shadows = LightShadows.None;
            GameObjectUtility.SetStaticEditorFlags(go, StaticEditorFlags.ContributeGI);
        }
        Debug.Log($"Range: built {Lamps.Length} baked lamps. Bake with Window > Rendering > Lighting.");
    }

    [MenuItem("Tools/Range/Add Colliders")]
    static void AddColliders()
    {
        // Everything a player can bump into. The lane signs, target cards and light panels
        // are decoration, and colliders on them would only cost collision checks.
        var solid = new HashSet<string> {
            "floor_green", "floor_black", "wall_wood", "wall_plaster", "ceiling",
            "bench_wood", "chair_seat", "metal_frame", "sius_box", "sius_box_front",
            "glass", "door_glass"
        };
        int n = 0;
        foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
        {
            string baseName = mf.gameObject.name.Split('.')[0];
            if (!solid.Contains(baseName) || mf.GetComponent<MeshCollider>() != null) continue;
            Undo.AddComponent<MeshCollider>(mf.gameObject);
            n++;
        }
        Debug.Log($"Range: added {n} mesh colliders.");
    }
}
"""


def write_unity_scripts(outdir, mats, lamps):
    rows = []
    for m in mats:
        rows.append('        new Mat { name = "%s", color = new Color(%gf, %gf, %gf), '
                    'alpha = %gf, rough = %gf, metal = %gf, '
                    'emissive = new Color(%gf, %gf, %gf), twoSided = %s },'
                    % (m["name"], m["color"][0], m["color"][1], m["color"][2],
                       m["alpha"], m["rough"], m["metal"],
                       m["emissive"][0], m["emissive"][1], m["emissive"][2],
                       "true" if m["double"] else "false"))
    lrows = ["        new Vector4(%gf, %gf, %gf, %gf)," % (l["x"], l["y"], l["z"], l["candela"])
             for l in lamps]
    cs = CS_TEMPLATE.replace("__MATERIALS__", "\n".join(rows))
    cs = cs.replace("__LAMPS__", "\n".join(lrows))
    open(os.path.join(outdir, "RangeSetup.cs"), "w").write(cs)


if __name__ == "__main__":
    main()
