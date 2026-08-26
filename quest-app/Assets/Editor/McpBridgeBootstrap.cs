using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEngine;

// Keeps the MCP for Unity bridge running so an external MCP client can
// reach this editor. The package ships with auto-start off and HTTP transport on,
// which leaves nothing listening until someone opens the MCP window and clicks Start.
// We pin it to stdio -- the server process is then spawned by the client on demand and
// finds us through the heartbeat file in ~/.unity-mcp.
[InitializeOnLoad]
public static class McpBridgeBootstrap
{
    // EditorPrefKeys in the package is internal, so the literals are repeated here.
    private const string UseHttpTransportKey = "MCPForUnity.UseHttpTransport";
    private const string AutoStartOnLoadKey = "MCPForUnity.AutoStartOnLoad";

    static McpBridgeBootstrap()
    {
        EditorApplication.delayCall += () => Ensure(false);
    }

    [MenuItem("Tools/MCP/Ensure Bridge Running")]
    private static void EnsureFromMenu() => Ensure(true);

    private static async void Ensure(bool verbose)
    {
        EditorPrefs.SetBool(AutoStartOnLoadKey, true);
        EditorConfigurationCache.Instance.SetUseHttpTransport(false);
        EditorPrefs.SetBool(UseHttpTransportKey, false);

        var bridge = MCPServiceLocator.Bridge;
        if (bridge.IsRunning)
        {
            if (verbose) Debug.Log($"MCP bridge already running on port {bridge.CurrentPort}.");
            return;
        }

        bool started = await bridge.StartAsync();
        if (started)
        {
            Debug.Log($"MCP bridge started ({bridge.ActiveMode}) on port {bridge.CurrentPort}.");
        }
        else
        {
            Debug.LogWarning("MCP bridge failed to start. Open Window > MCP For Unity for details.");
        }
    }
}
