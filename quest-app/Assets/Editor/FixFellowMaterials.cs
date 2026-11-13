using System.IO;
using UnityEditor;
using UnityEngine;

// Extract the FBX's embedded textures and rebind materials (Blender-exported FBX ships
// textures embedded; Unity leaves materials untextured until they're extracted).
public static class FixFellowMaterials
{
    const string Fbx = "Assets/Models/FellowShooter/Shooter.fbx";
    const string TexDir = "Assets/Models/FellowShooter/Textures";
    const string MatDir = "Assets/Models/FellowShooter/Materials";

    public static void Run()
    {
        var imp = (ModelImporter)AssetImporter.GetAtPath(Fbx);
        imp.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
        imp.materialLocation = ModelImporterMaterialLocation.InPrefab;
        AssetDatabase.ImportAsset(Fbx, ImportAssetOptions.ForceSynchronousImport);

        Directory.CreateDirectory(TexDir);
        bool ex = imp.ExtractTextures(TexDir);
        Debug.Log("[FM] ExtractTextures=" + ex);
        AssetDatabase.Refresh();
        AssetDatabase.ImportAsset(Fbx, ImportAssetOptions.ForceSynchronousImport);

        // Extract materials to disk so they are real editable assets bound to the model.
        Directory.CreateDirectory(MatDir);
        foreach (var o in AssetDatabase.LoadAllAssetsAtPath(Fbx))
        {
            var m = o as Material; if (m == null) continue;
            string dst = MatDir + "/" + m.name + ".mat";
            string err = AssetDatabase.ExtractAsset(m, dst);
            if (!string.IsNullOrEmpty(err)) Debug.Log("[FM] extract mat " + m.name + " -> " + err);
        }
        AssetDatabase.WriteImportSettingsIfDirty(Fbx);
        AssetDatabase.ImportAsset(Fbx, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.Refresh();

        int tf = 0;
        if (Directory.Exists(TexDir)) foreach (var f in Directory.GetFiles(TexDir)) if (!f.EndsWith(".meta")) tf++;
        Debug.Log("[FM] extracted texture files=" + tf);

        // Report material -> base map bindings (diagnose any still-white slots).
        foreach (var guid in AssetDatabase.FindAssets("t:Material", new[] { MatDir }))
        {
            var m = AssetDatabase.LoadAssetAtPath<Material>(AssetDatabase.GUIDToAssetPath(guid));
            if (m == null) continue;
            Texture bm = m.HasProperty("_BaseMap") ? m.GetTexture("_BaseMap") : null;
            if (bm == null && m.HasProperty("_MainTex")) bm = m.GetTexture("_MainTex");
            Debug.Log("[FM] mat '" + m.name + "' shader=" + m.shader.name + " baseMap=" + (bm != null ? bm.name : "<NONE>"));
        }
        Debug.Log("[FM] DONE");
    }
}
