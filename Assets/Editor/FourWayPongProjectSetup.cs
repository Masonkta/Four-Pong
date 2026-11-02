#if UNITY_EDITOR
using FourWayPong;
using Unity.Netcode;
using PlayEveryWare.EpicOnlineServices;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

[InitializeOnLoad]
public static class FourWayPongProjectSetup
{
    private const string ScenePath = "Assets/FourWayPong.unity";
    private const string PlayerPrefabPath = "Assets/Generated/NetworkPlayer.prefab";
    private const string MatchPrefabPath = "Assets/Generated/PongMatch.prefab";
    private const string SetupKey = "FourWayPong.AutoSetup.v9.EOSRoomCodes.NetworkPrefabFix";

    static FourWayPongProjectSetup()
    {
        EditorApplication.delayCall += EnsureProject;
    }

    [MenuItem("Four-Way Pong/Rebuild Main Scene")]
    public static void RebuildSceneFromMenu()
    {
        EditorPrefs.DeleteKey(SetupKey);
        EnsureProject();
    }

    private static void EnsureProject()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
            return;

        if (!System.IO.File.Exists(ScenePath) || !EditorPrefs.GetBool(SetupKey, false))
        {
            BuildScene();
            EditorPrefs.SetBool(SetupKey, true);
        }

        ConfigurePlayerSettings();
    }

    private static void BuildScene()
    {
        Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        scene.name = "FourWayPong";

        GameObject cameraObject = new GameObject("Main Camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        cameraObject.tag = "MainCamera";
        camera.orthographic = true;
        camera.orthographicSize = 7.25f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.018f, 0.024f, 0.045f);
        cameraObject.transform.position = new Vector3(0f, 0f, -20f);
        cameraObject.AddComponent<AudioListener>();

        GameObject eosRoot = new GameObject("EOSManager");
        eosRoot.AddComponent<EOSManager>();

        GameObject networkRoot = new GameObject("NetworkManager");
        NetworkManager manager = networkRoot.AddComponent<NetworkManager>();
        EOSP2PTransport transport = networkRoot.AddComponent<EOSP2PTransport>();
        manager.NetworkConfig.NetworkTransport = transport;
        manager.NetworkConfig.EnableSceneManagement = true;
        manager.NetworkConfig.ConnectionApproval = true;
        manager.NetworkConfig.TickRate = 30;

        if (!AssetDatabase.IsValidFolder("Assets/Generated"))
            AssetDatabase.CreateFolder("Assets", "Generated");

        GameObject playerTemplate = new GameObject("NetworkPlayer");
        playerTemplate.AddComponent<NetworkObject>();
        GameObject playerPrefab = PrefabUtility.SaveAsPrefabAsset(playerTemplate, PlayerPrefabPath);
        Object.DestroyImmediate(playerTemplate);
        manager.NetworkConfig.PlayerPrefab = playerPrefab;

        GameObject matchTemplate = new GameObject("PongMatch");
        matchTemplate.AddComponent<NetworkObject>();
        matchTemplate.AddComponent<PongMatch>();
        GameObject matchPrefab = PrefabUtility.SaveAsPrefabAsset(matchTemplate, MatchPrefabPath);
        Object.DestroyImmediate(matchTemplate);

        GameObject appObject = new GameObject("PongApplication");
        appObject.AddComponent<PongLocalGame>();
        appObject.AddComponent<EOSRoomService>();
        PongOnlineApp onlineApp = appObject.AddComponent<PongOnlineApp>();

        SerializedObject onlineAppSerialized = new SerializedObject(onlineApp);
        SerializedProperty matchPrefabProperty = onlineAppSerialized.FindProperty("pongMatchPrefab");
        if (matchPrefabProperty != null)
        {
            matchPrefabProperty.objectReferenceValue = matchPrefab;
            onlineAppSerialized.ApplyModifiedPropertiesWithoutUndo();
        }

        EditorSceneManager.SaveScene(scene, ScenePath);
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log("Four-Way Pong scene created at " + ScenePath);
    }

    private static void ConfigurePlayerSettings()
    {
        PlayerSettings.companyName = "Maddox Games";
        PlayerSettings.productName = "Four-Way Pong";
        PlayerSettings.defaultScreenWidth = 1280;
        PlayerSettings.defaultScreenHeight = 720;
        PlayerSettings.resizableWindow = true;
        PlayerSettings.runInBackground = true;

        // Input.GetKey is intentionally used for a simple keyboard-first desktop build.
        // Force Active Input Handling to Both so this works in Unity 6 projects using the new Input System too.
        Object[] settingsAssets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/ProjectSettings.asset");
        if (settingsAssets != null && settingsAssets.Length > 0)
        {
            SerializedObject so = new SerializedObject(settingsAssets[0]);
            SerializedProperty prop = so.FindProperty("activeInputHandler");
            if (prop != null && prop.intValue != 2)
            {
                prop.intValue = 2;
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }
    }
}
#endif
