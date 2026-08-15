using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

/// <summary>
/// Entry point for headless APK builds, invoked by deploy.ps1 via
/// -executeMethod QuestBuild.BuildApk.
/// </summary>
public static class QuestBuild
{
    [MenuItem("Quest/3. Build APK")]
    public static void BuildApkFromMenu()
    {
        Build("Builds/vr-shooting-archery.apk");
    }

    public static void BuildApk()
    {
        // Path comes from the command line so the shell script controls output
        // location: -executeMethod QuestBuild.BuildApk -outputPath <path>
        var outputPath = "Builds/vr-shooting-archery.apk";
        var args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "-outputPath")
            {
                outputPath = args[i + 1];
                break;
            }
        }

        var report = Build(outputPath);

        // Batch mode ignores thrown exceptions for exit codes, so set it
        // explicitly or CI cannot tell a failed build from a good one.
        if (report == null || report.summary.result != BuildResult.Succeeded)
        {
            EditorApplication.Exit(1);
        }

        EditorApplication.Exit(0);
    }

    static BuildReport Build(string outputPath)
    {
        var scenes = new List<string>();
        foreach (var scene in EditorBuildSettings.scenes)
        {
            if (scene.enabled) scenes.Add(scene.path);
        }

        if (scenes.Count == 0)
        {
            Debug.LogError("[Quest] No enabled scenes in Build Settings. Run Quest > 2. Create Placeholder Scene first.");
            return null;
        }

        var options = new BuildPlayerOptions
        {
            scenes = scenes.ToArray(),
            locationPathName = outputPath,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = BuildOptions.None,
        };

        var report = BuildPipeline.BuildPlayer(options);
        var summary = report.summary;
        Debug.Log($"[Quest] Build {summary.result}: {summary.totalSize / (1024 * 1024)} MB in {summary.totalTime}.");
        return report;
    }
}
