#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using PlayEveryWare.EpicOnlineServices;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace FourWayPong.Editor
{
    /// <summary>
    /// Builds Battle 99 as a headless Unity dedicated-server process while temporarily
    /// swapping the PlayEveryWare EOS platform configuration to the server-only client
    /// credentials. The normal player credentials are restored immediately after the
    /// build finishes (success or failure), so regular client builds remain unchanged.
    /// </summary>
    public static class Battle99DedicatedServerBuild
    {
        private const string ServerClientIdKey = "FourWayPong.Battle99.ServerClientId";
        private const string ServerClientSecretKey = "FourWayPong.Battle99.ServerClientSecret";

        public static string ServerClientId
        {
            get => EditorPrefs.GetString(ServerClientIdKey, string.Empty);
            set => EditorPrefs.SetString(ServerClientIdKey, value == null ? string.Empty : value.Trim());
        }

        public static string ServerClientSecret
        {
            get => EditorPrefs.GetString(ServerClientSecretKey, string.Empty);
            set => EditorPrefs.SetString(ServerClientSecretKey, value == null ? string.Empty : value.Trim());
        }

        public static bool HasServerCredentials =>
            !string.IsNullOrWhiteSpace(ServerClientId) &&
            !string.IsNullOrWhiteSpace(ServerClientSecret);

        [MenuItem("Four Way Pong/Battle 99/Server EOS Credentials")]
        public static void OpenCredentialWindow()
        {
            Battle99ServerCredentialsWindow.ShowWindow();
        }

        [MenuItem("Four Way Pong/Build Battle 99 Server/Windows x64")]
        public static void BuildWindowsServer()
        {
            BuildServer(
                BuildTarget.StandaloneWindows64,
                PlatformManager.Platform.Windows,
                Path.Combine("Builds", "Battle99Server", "Windows", "FourWayPongServer.exe"));
        }

        [MenuItem("Four Way Pong/Build Battle 99 Server/Linux x64")]
        public static void BuildLinuxServer()
        {
            BuildServer(
                BuildTarget.StandaloneLinux64,
                PlatformManager.Platform.Linux,
                Path.Combine("Builds", "Battle99Server", "Linux", "FourWayPongServer.x86_64"));
        }

        private static void BuildServer(
            BuildTarget target,
            PlatformManager.Platform eosPlatform,
            string outputPath)
        {
            if (!HasServerCredentials)
            {
                Debug.LogError(
                    "Battle 99 server credentials are not configured. Open " +
                    "Four Way Pong > Battle 99 > Server EOS Credentials first.");
                OpenCredentialWindow();
                return;
            }

            if (!PlatformManager.TryGetConfig(eosPlatform, out PlatformConfig config) || config == null)
            {
                Debug.LogError(
                    "Could not load the PlayEveryWare EOS configuration for " + eosPlatform + ". " +
                    "Open EOS Plugin > EOS Configuration once, save the configuration, then try again.");
                return;
            }

            string[] scenes = EditorBuildSettings.scenes
                .Where(scene => scene.enabled)
                .Select(scene => scene.path)
                .ToArray();

            if (scenes.Length == 0)
            {
                Debug.LogError("Battle 99 server build: no enabled scenes are present in Build Profiles.");
                return;
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            // Preserve the player's normal EOS credentials. The server build gets its own
            // Peer2Peer client credential because Battle 99 uses EOS P2P/relay for gameplay
            // transport in addition to EOS Sessions for discovery.
            EOSClientCredentials originalCredentials = config.clientCredentials;
            bool originalIsServer = config.isServer;

            string encryptionKey = originalCredentials != null &&
                                   EOSClientCredentials.IsEncryptionKeyValid(originalCredentials.EncryptionKey)
                ? originalCredentials.EncryptionKey
                : EOSClientCredentials.GenerateEncryptionKey();

            try
            {
                config.clientCredentials = new EOSClientCredentials(
                    ServerClientId,
                    ServerClientSecret,
                    encryptionKey);
                config.isServer = true;
                config.Write();
                AssetDatabase.Refresh();

                BuildPlayerOptions options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = outputPath,
                    target = target,
                    options = BuildOptions.CleanBuildCache,
                    subtarget = (int)StandaloneBuildSubtarget.Server
                };

                Debug.Log(
                    "Building Battle 99 dedicated server with the separate server EOS client. " +
                    "The normal game-client EOS configuration will be restored automatically afterward.");

                BuildReport report = BuildPipeline.BuildPlayer(options);
                if (report.summary.result == BuildResult.Succeeded)
                {
                    Debug.Log(
                        "Battle 99 dedicated server build complete: " + Path.GetFullPath(outputPath) +
                        "\nServer credentials were included only in this server build. " +
                        "Do not distribute this server build to players.");
                }
                else
                {
                    Debug.LogError(
                        "Battle 99 dedicated server build failed: " +
                        report.summary.result +
                        ". Make sure the Dedicated Server build support module for the selected platform is installed.");
                }
            }
            finally
            {
                // Critical: restore the player build configuration even if Unity's build fails.
                config.clientCredentials = originalCredentials;
                config.isServer = originalIsServer;
                config.Write();
                AssetDatabase.Refresh();
                Debug.Log("Restored normal game-client EOS configuration after Battle 99 server build.");
            }
        }
    }

    public sealed class Battle99ServerCredentialsWindow : EditorWindow
    {
        private string _clientId;
        private string _clientSecret;
        private bool _showSecret;

        public static void ShowWindow()
        {
            Battle99ServerCredentialsWindow window = GetWindow<Battle99ServerCredentialsWindow>(true);
            window.titleContent = new GUIContent("Battle 99 EOS Server");
            window.minSize = new Vector2(560f, 280f);
            window.Show();
        }

        private void OnEnable()
        {
            _clientId = Battle99DedicatedServerBuild.ServerClientId;
            _clientSecret = Battle99DedicatedServerBuild.ServerClientSecret;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("Battle 99 Dedicated Server EOS Client", EditorStyles.boldLabel);
            EditorGUILayout.Space(6f);

            EditorGUILayout.HelpBox(
                "Use the separate EOS Client assigned to your Battle99 Server P2P policy. " +
                "These values are stored in this computer's Unity EditorPrefs, not in your project source files. " +
                "When you build the dedicated server, they are temporarily inserted into the EOS server build and " +
                "the normal player credentials are restored afterward.",
                MessageType.Info);

            EditorGUILayout.Space(8f);
            _clientId = EditorGUILayout.TextField("Server Client ID", _clientId);

            EditorGUILayout.BeginHorizontal();
            if (_showSecret)
                _clientSecret = EditorGUILayout.TextField("Server Client Secret", _clientSecret);
            else
                _clientSecret = EditorGUILayout.PasswordField("Server Client Secret", _clientSecret);
            _showSecret = GUILayout.Toggle(_showSecret, "Show", GUILayout.Width(62f));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10f);
            EditorGUILayout.HelpBox(
                "Do not use a TrustedServer policy for this particular build yet. " +
                "The current Battle 99 transport intentionally uses EOS P2P/relay so you can self-host without router port forwarding. " +
                "That requires a Product User ID, so the server client should use the Peer2Peer policy and User Required should stay enabled.",
                MessageType.Warning);

            GUILayout.FlexibleSpace();
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("Clear Saved Credentials", GUILayout.Height(32f)))
            {
                _clientId = string.Empty;
                _clientSecret = string.Empty;
                Battle99DedicatedServerBuild.ServerClientId = string.Empty;
                Battle99DedicatedServerBuild.ServerClientSecret = string.Empty;
            }

            if (GUILayout.Button("Save", GUILayout.Height(32f)))
            {
                Battle99DedicatedServerBuild.ServerClientId = _clientId;
                Battle99DedicatedServerBuild.ServerClientSecret = _clientSecret;
                Debug.Log("Battle 99 server EOS credentials saved locally in Unity EditorPrefs.");
                Close();
            }

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(10f);
        }
    }
}
#endif
