using System;
using System.Collections;
using UnityEngine;

namespace FourWayPong
{
    public static class Battle99DedicatedServerRuntime
    {
        public static bool IsDedicatedServerProcess { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void DetectServerMode()
        {
#if UNITY_SERVER
            IsDedicatedServerProcess = true;
            return;
#endif
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "-battle99-server", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(args[i], "--battle99-server", StringComparison.OrdinalIgnoreCase))
                {
                    IsDedicatedServerProcess = true;
                    break;
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartServerBootstrap()
        {
            if (!IsDedicatedServerProcess)
                return;

            GameObject root = new GameObject("Battle99DedicatedServerRuntime");
            UnityEngine.Object.DontDestroyOnLoad(root);
            root.AddComponent<Battle99DedicatedServerBootstrap>();
        }
    }

    public sealed class Battle99DedicatedServerBootstrap : MonoBehaviour
    {
        private EOSRoomService _login;
        private Battle99SessionService _sessions;

        private void Start()
        {
            Application.runInBackground = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            _login = gameObject.AddComponent<EOSRoomService>();
            _sessions = gameObject.AddComponent<Battle99SessionService>();
            StartCoroutine(BootWhenReady());
        }

        private IEnumerator BootWhenReady()
        {
            float timeoutAt = Time.realtimeSinceStartup + 30f;
            while (!_login.Ready && Time.realtimeSinceStartup < timeoutAt)
                yield return null;

            if (!_login.Ready)
            {
                Debug.LogError("Battle99 dedicated server could not sign into EOS: " + _login.Status);
                yield break;
            }

            Battle99DedicatedServer server = gameObject.AddComponent<Battle99DedicatedServer>();
            server.Initialize(_sessions);
        }
    }
}
