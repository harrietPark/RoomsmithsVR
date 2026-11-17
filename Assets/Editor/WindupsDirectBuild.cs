using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Windups;

// Inject into the disposable build scene, never the authored scene/prefab or a source file.
public sealed class WindupsDirectBuild : IProcessSceneWithReport
{
    public int callbackOrder => 0;
    public void OnProcessScene(Scene scene, BuildReport report)
    {
        if (report == null) return;
        foreach (var root in scene.GetRootGameObjects())
        foreach (var meeting in root.GetComponentsInChildren<WindupsMeeting>(true))
        {
            if (!meeting.directOpenAI) continue;
            string consent = Path.Combine(Path.GetDirectoryName(Application.dataPath), "LocalSettings/direct-build-consent.txt");
            if (!File.Exists(consent) || File.ReadAllText(consent).Trim() != "ALLOW_KEY_IN_TEST_APK")
                throw new BuildFailedException("Direct OpenAI test build requires local opt-in: LocalSettings/direct-build-consent.txt must contain ALLOW_KEY_IN_TEST_APK. The key will be extractable from this APK. Never publish it publicly.");
            string key = WindupsDirectCredentials.ReadLocalKey();
            if (string.IsNullOrWhiteSpace(key)) throw new BuildFailedException("Direct OpenAI test key is missing from local settings.");
            var credentials = meeting.gameObject.AddComponent<WindupsDirectCredentials>();
            credentials.SetBuildKey(key);
            // F26 (optional): the Meshy key the user asked to handle like the OpenAI one, and this
            // PC's local address so the headset can send print files to the Windups Lab server.
            string meshy = WindupsDirectCredentials.ReadLocalMeshyKey();
            if (!string.IsNullOrWhiteSpace(meshy)) credentials.SetMeshyKey(meshy);
            // F83 (optional): the Meta Model API key (Segment Anything finds a model's parts), handled the same way.
            string meta = WindupsDirectCredentials.ReadLocalMetaKey();
            if (!string.IsNullOrWhiteSpace(meta)) credentials.SetMetaKey(meta);
            string ip = LocalAddress();
            if (ip != null) credentials.SetPcUrl("http://" + ip + ":5177");
        }
    }

    // The PC's address on the local network (Wi-Fi or Ethernet, skipping virtual adapters).
    private static string LocalAddress()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus != OperationalStatus.Up) continue;
                if (adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 && adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                string name = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                if (name.Contains("virtual") || name.Contains("vethernet") || name.Contains("wintun") || name.Contains("tap") || name.Contains("vpn")) continue;
                if (!adapter.GetIPProperties().GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))) continue;
                var v4 = adapter.GetIPProperties().UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork);
                if (v4 != null) return v4.Address.ToString();
            }
        }
        catch { /* no address: the headset keeps print files in its own folder */ }
        return null;
    }
}
