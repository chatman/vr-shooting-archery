using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

// One-shot opt-in to Meta's AI Agent Bridge, which gates the MCP bridge shipped in
// com.meta.xr.sdk.core. Enabling it starts the local MCP HTTP server (127.0.0.1:48736)
// and auto-registers it with the configured MCP client, giving us the Building Blocks, Quest
// setup, and compilation tools.
//
// Meta.XR.AI.AgentBridge.Settings is an internal type, so this goes through reflection.
// EnableAndInitialize() is the same entry point Meta's own AI Tools Setup installer uses.
// Safe to delete once the bridge is registered -- the setting persists in EditorPrefs.
public static class MetaAgentBridgeEnable
{
    [MenuItem("Tools/MCP/Enable Meta AI Agent Bridge")]
    public static void Enable()
    {
        var settings = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "meta.xr.ai.agentbridge.editor")
            ?.GetType("Meta.XR.AI.AgentBridge.Settings");

        if (settings == null)
        {
            Debug.LogError("Meta AI Agent Bridge: Settings type not found. Is com.meta.xr.sdk.core installed?");
            return;
        }

        var enableAndInitialize = settings.GetMethod("EnableAndInitialize",
            BindingFlags.Public | BindingFlags.Static);
        if (enableAndInitialize == null)
        {
            Debug.LogError("Meta AI Agent Bridge: EnableAndInitialize() not found; the SDK API has changed.");
            return;
        }

        enableAndInitialize.Invoke(null, null);
        Debug.Log("Meta AI Agent Bridge enabled; MCP HTTP server starting and registering with the MCP client.");
    }
}
