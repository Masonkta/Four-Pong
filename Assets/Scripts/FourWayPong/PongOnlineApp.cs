using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using Unity.Netcode;
using UnityEngine;

namespace FourWayPong
{
    public sealed class PongOnlineApp : MonoBehaviour
    {
        private enum ScreenState
        {
            Name,
            Main,
            Multiplayer,
            Host,
            Join,
            Settings,
            Lobby,
            OnlineGame,
            LocalGame,
            Battle99Queue,
            Battle99Game
        }

        private ScreenState _screen = ScreenState.Name;
        private string _status = string.Empty;
        private string _joinRoomCode = string.Empty;
        private string _activeRoomCode = string.Empty;
        private string _playerNameDraft = string.Empty;
        private bool _nameSubmittedForSession;
        private bool _isSessionHost;
        private bool _connecting;
        private bool _intentionalShutdown;
        private PongLocalGame _localGame;
        private EOSRoomService _eos;
        private Battle99Service _battle99;
        private Battle99Game _battle99Game;
        private bool _pauseMenuOpen;
        private bool _escapeArmed = true;
        private bool _settingsOpenedFromPause;
        private ScreenState _settingsReturnScreen = ScreenState.Main;
        private Vector2 _battle99QueueScroll;

        [SerializeField] private GameObject pongMatchPrefab;

        private int _rebindingPlayer;
        private bool _rebindingPositive;

        private GUIStyle _title;
        private GUIStyle _subtitle;
        private GUIStyle _heading;
        private GUIStyle _body;
        private GUIStyle _small;
        private GUIStyle _button;
        private GUIStyle _smallButton;
        private GUIStyle _field;
        private GUIStyle _panel;
        private GUIStyle _centerBody;
        private Texture2D _panelTex;
        private Texture2D _buttonTex;
        private Texture2D _buttonHoverTex;
        private Texture2D _fieldTex;

        private void Awake()
        {
            if (Battle99DedicatedServerRuntime.IsDedicatedServerProcess)
                enabled = false;
        }

        private void Start()
        {
            if (Battle99DedicatedServerRuntime.IsDedicatedServerProcess)
                return;
            Application.runInBackground = true;
            PongSettings.Load();
            _playerNameDraft = PongSettings.PlayerName;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null)
            {
                // Run NGO's own network tick fast enough that our 60 Hz motion snapshots
                // and immediate input-change RPCs are not artificially held behind a slower tick.
                manager.NetworkConfig.TickRate = PongConfig.NetcodeTickRate;
                manager.NetworkConfig.EnableTimeResync = true;

                if (pongMatchPrefab == null)
                    Debug.LogError("PongOnlineApp: PongMatch prefab is not assigned. Online hosting/joining will not work until it is assigned.");
            }
            _localGame = GetComponent<PongLocalGame>();
            if (_localGame == null)
                _localGame = gameObject.AddComponent<PongLocalGame>();
            _eos = GetComponent<EOSRoomService>();
            if (_eos == null)
                _eos = gameObject.AddComponent<EOSRoomService>();
            _battle99 = GetComponent<Battle99Service>();
            if (_battle99 == null)
                _battle99 = gameObject.AddComponent<Battle99Service>();
            _battle99Game = GetComponent<Battle99Game>();
            if (_battle99Game == null)
                _battle99Game = gameObject.AddComponent<Battle99Game>();
            HookNetworkCallbacks();
        }

        private void OnDestroy()
        {
            UnhookNetworkCallbacks();
        }

        private void Update()
        {
            HandleEscapeInput();

            if (_screen == ScreenState.Battle99Queue)
            {
                if (_battle99 != null && _battle99.MatchStarted && (_battle99Game == null || !_battle99Game.Active))
                {
                    _battle99Game.StartBattle(_battle99);
                    _screen = ScreenState.Battle99Game;
                    _pauseMenuOpen = false;
                    _status = string.Empty;
                }
                return;
            }

            if (_screen == ScreenState.Battle99Game ||
                (_screen == ScreenState.Settings && _settingsOpenedFromPause && _settingsReturnScreen == ScreenState.Battle99Game))
            {
                return;
            }

            if (_screen == ScreenState.LocalGame ||
                (_screen == ScreenState.Settings && _settingsOpenedFromPause && _settingsReturnScreen == ScreenState.LocalGame))
                return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null)
                return;

            bool normalOnlineContext = _screen == ScreenState.Lobby || _screen == ScreenState.OnlineGame ||
                (_screen == ScreenState.Settings && _settingsOpenedFromPause && _settingsReturnScreen == ScreenState.OnlineGame);

            if (!_isSessionHost && !_connecting && normalOnlineContext &&
                (!manager.IsListening || !manager.IsConnectedClient))
            {
                ReturnToMultiplayerAfterDisconnect();
                return;
            }

            if (manager.IsListening && PongMatch.Instance != null && PongMatch.Instance.IsSpawned)
            {
                TrySubmitPlayerName();

                if (_screen != ScreenState.Settings)
                {
                    if (PongMatch.Instance.MatchRunning.Value || PongMatch.Instance.WinnerTeam.Value >= 0)
                        _screen = ScreenState.OnlineGame;
                    else if (_screen == ScreenState.OnlineGame)
                        _screen = ScreenState.Lobby;
                }
            }
        }

        private void HandleEscapeInput()
        {
            if (!Input.GetKey(KeyCode.Escape))
                _escapeArmed = true;

            if (!Input.GetKeyDown(KeyCode.Escape) || !_escapeArmed || _rebindingPlayer != 0)
                return;

            _escapeArmed = false;

            if (_screen == ScreenState.Settings)
            {
                _rebindingPlayer = 0;
                if (_settingsOpenedFromPause)
                {
                    _screen = _settingsReturnScreen;
                    _settingsOpenedFromPause = false;
                    if (_screen == ScreenState.OnlineGame && PongMatch.Instance != null)
                        _pauseMenuOpen = PongMatch.Instance.Paused.Value;
                    return;
                }

                _screen = ScreenState.Main;
                return;
            }

            if (_screen == ScreenState.LocalGame)
            {
                bool pause = _localGame != null && !_localGame.Paused;
                _localGame?.SetPaused(pause);
                _pauseMenuOpen = pause;
                return;
            }

            if (_screen == ScreenState.OnlineGame)
            {
                PongMatch match = PongMatch.Instance;
                if (match != null && match.IsSpawned && match.MatchRunning.Value && match.WinnerTeam.Value < 0)
                {
                    bool pause = !match.Paused.Value;
                    match.SetPausedServerRpc(pause);
                    _pauseMenuOpen = pause;
                }
                return;
            }

            if (_screen == ScreenState.Battle99Game)
            {
                _pauseMenuOpen = !_pauseMenuOpen;
            }
        }

        private void HookNetworkCallbacks()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null)
                return;

            manager.OnClientConnectedCallback -= OnClientConnected;
            manager.OnClientDisconnectCallback -= OnClientDisconnected;
            manager.OnClientConnectedCallback += OnClientConnected;
            manager.OnClientDisconnectCallback += OnClientDisconnected;
        }

        private void UnhookNetworkCallbacks()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null)
                return;

            manager.OnClientConnectedCallback -= OnClientConnected;
            manager.OnClientDisconnectCallback -= OnClientDisconnected;
        }

        private void OnClientConnected(ulong clientId)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null)
                return;

            if (_screen == ScreenState.Battle99Queue ||
                _screen == ScreenState.Battle99Game ||
                (_screen == ScreenState.Settings && _settingsReturnScreen == ScreenState.Battle99Game))
                return;

            if (clientId == manager.LocalClientId)
            {
                _nameSubmittedForSession = false;
                _connecting = false;
                _screen = ScreenState.Lobby;
                _status = string.Empty;
            }
            else if (_isSessionHost)
            {
                _status = string.Empty;
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_intentionalShutdown)
                return;

            if (_screen == ScreenState.Battle99Queue ||
                _screen == ScreenState.Battle99Game ||
                (_screen == ScreenState.Settings && _settingsReturnScreen == ScreenState.Battle99Game))
                return;

            if (_isSessionHost)
            {
                _status = string.Empty;
                return;
            }

            ReturnToMultiplayerAfterDisconnect();
        }

        private void ReturnToMultiplayerAfterDisconnect()
        {
            _connecting = false;
            _nameSubmittedForSession = false;
            _eos?.LeaveRoom();
            _activeRoomCode = string.Empty;
            _isSessionHost = false;
            _screen = ScreenState.Multiplayer;
            _status = "Host disconnected.";
        }

        private void ConfigureApproval()
        {
            NetworkManager manager = NetworkManager.Singleton;
            manager.NetworkConfig.ConnectionApproval = true;
            manager.ConnectionApprovalCallback = ApprovalCheck;
        }

        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            NetworkManager manager = NetworkManager.Singleton;
            bool matchRunning = PongMatch.Instance != null && PongMatch.Instance.IsSpawned && PongMatch.Instance.MatchRunning.Value;
            bool roomFull = manager != null && manager.ConnectedClientsIds.Count >= PongConfig.MaxPlayers;

            response.Approved = !matchRunning && !roomFull;
            response.CreatePlayerObject = false;
            response.PlayerPrefabHash = null;
            response.Position = null;
            response.Rotation = null;
            response.Pending = false;

            if (matchRunning)
                response.Reason = "The match is already running.";
            else if (roomFull)
                response.Reason = "This lobby already has four players.";
            else
                response.Reason = string.Empty;
        }

        private void StartLocalGame(PongGameMode mode)
        {
            PongAudio.PlayMenu();
            ShutdownIfNeeded();
            _isSessionHost = false;
            _connecting = false;
            _localGame.StartMatch(mode);
            _pauseMenuOpen = false;
            _screen = ScreenState.LocalGame;
            _status = string.Empty;
        }

        private void HostGame()
        {
            if (_connecting || NetworkManager.Singleton == null || _eos == null)
                return;

            if (!_eos.Ready)
            {
                _status = _eos.Status;
                return;
            }

            PongAudio.PlayMenu();
            _localGame.StopMatch();
            ShutdownIfNeeded();
            _eos.LeaveRoom(false);
            _intentionalShutdown = false;
            _isSessionHost = true;
            _connecting = true;
            _nameSubmittedForSession = false;
            _status = string.Empty;

            _eos.CreateRoom((success, error) =>
            {
                if (!success)
                {
                    _connecting = false;
                    _isSessionHost = false;
                    _screen = ScreenState.Host;
                    _status = error;
                    return;
                }

                try
                {
                    NetworkManager manager = NetworkManager.Singleton;
                    ConfigureApproval();
                    EOSP2PTransport transport = manager.GetComponent<EOSP2PTransport>();
                    if (transport == null)
                        throw new InvalidOperationException("EOS P2P transport is missing from NetworkManager.");

                    transport.ServerUserIdToConnectTo = null;
                    manager.NetworkConfig.NetworkTransport = transport;
                    if (!manager.StartHost())
                        throw new InvalidOperationException("Netcode could not start the EOS host.");

                    if (pongMatchPrefab == null)
                        throw new InvalidOperationException("PongMatch prefab has not been assigned.");

                    GameObject matchObject = Instantiate(pongMatchPrefab);
                    NetworkObject matchNetworkObject = matchObject.GetComponent<NetworkObject>();
                    if (matchNetworkObject == null)
                    {
                        Destroy(matchObject);
                        throw new InvalidOperationException("PongMatch prefab does not contain a NetworkObject component.");
                    }

                    matchNetworkObject.Spawn();

                    _activeRoomCode = _eos.CurrentRoomCode;
                    _screen = ScreenState.Lobby;
                    _status = string.Empty;
                    // The host-local client connection callback will clear _connecting.
                }
                catch (Exception ex)
                {
                    _intentionalShutdown = true;
                    ShutdownIfNeeded();
                    _intentionalShutdown = false;
                    _eos.LeaveRoom();
                    _connecting = false;
                    _isSessionHost = false;
                    _screen = ScreenState.Host;
                    _status = "Could not host: " + ex.Message;
                }
            });
        }

        private void JoinGame()
        {
            if (_connecting || NetworkManager.Singleton == null || _eos == null)
                return;

            string code = EOSRoomService.NormalizeRoomCode(_joinRoomCode);
            if (!EOSRoomService.IsValidRoomCode(code))
            {
                _status = "Enter the host's 6-digit room code.";
                return;
            }

            if (!_eos.Ready)
            {
                _status = _eos.Status;
                return;
            }

            PongAudio.PlayMenu();
            _localGame.StopMatch();
            ShutdownIfNeeded();
            _eos.LeaveRoom(false);
            _intentionalShutdown = false;
            _isSessionHost = false;
            _connecting = true;
            _nameSubmittedForSession = false;
            _status = string.Empty;

            _eos.JoinRoom(code, (success, error, hostUser) =>
            {
                if (!success)
                {
                    _connecting = false;
                    _screen = ScreenState.Join;
                    _status = error;
                    return;
                }

                try
                {
                    if (hostUser == null || !hostUser.IsValid())
                        throw new InvalidOperationException("The room did not provide a valid host identity.");

                    NetworkManager manager = NetworkManager.Singleton;
                    ConfigureApproval();
                    EOSP2PTransport transport = manager.GetComponent<EOSP2PTransport>();
                    if (transport == null)
                        throw new InvalidOperationException("EOS P2P transport is missing from NetworkManager.");

                    transport.ServerUserIdToConnectTo = hostUser;
                    manager.NetworkConfig.NetworkTransport = transport;
                    if (!manager.StartClient())
                        throw new InvalidOperationException("Netcode could not start the EOS client.");

                    _activeRoomCode = _eos.CurrentRoomCode;
                    _status = string.Empty;
                }
                catch (Exception ex)
                {
                    _eos.LeaveRoom();
                    _connecting = false;
                    _screen = ScreenState.Join;
                    _status = "Could not connect: " + ex.Message;
                }
            });
        }

        private void LeaveSession()
        {
            PongAudio.PlayMenu();
            ShutdownIfNeeded();
            _eos?.LeaveRoom();
            _activeRoomCode = string.Empty;
            _isSessionHost = false;
            _connecting = false;
            _nameSubmittedForSession = false;
            _pauseMenuOpen = false;
            _screen = ScreenState.Multiplayer;
            _status = string.Empty;
        }

        private void LeaveLocalGame()
        {
            PongAudio.PlayMenu();
            _localGame.StopMatch();
            _pauseMenuOpen = false;
            _screen = ScreenState.Main;
            _status = string.Empty;
        }

        private void StartBattle99Queue()
        {
            if (_battle99 == null || _eos == null || !_eos.Ready)
            {
                _status = _eos != null ? _eos.Status : "Online services unavailable.";
                return;
            }

            PongAudio.PlayMenu();
            ShutdownIfNeeded();
            _eos.LeaveRoom(false);
            _localGame?.StopMatch();
            _battle99Game?.StopBattle();
            _battle99.Leave();
            _pauseMenuOpen = false;
            _status = string.Empty;
            _screen = ScreenState.Battle99Queue;
            _battle99.BeginQueue();
        }

        private void LeaveBattle99()
        {
            PongAudio.PlayMenu();
            _battle99Game?.StopBattle();
            _battle99?.Leave();
            _pauseMenuOpen = false;
            _settingsOpenedFromPause = false;
            _screen = ScreenState.Main;
            _status = string.Empty;
        }

        private void ShutdownIfNeeded()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
                return;

            _intentionalShutdown = true;
            manager.Shutdown();
            _intentionalShutdown = false;
        }

        private void OnGUI()
        {
            BuildStyles();
            HandleRebindEvent();

            if (_screen == ScreenState.Battle99Game)
            {
                _battle99Game?.DrawGameplay();
                DrawPingHud(_battle99Game != null ? _battle99Game.PingMs : -1f);

                if (_pauseMenuOpen)
                    DrawPauseOverlay(ScreenState.Battle99Game);
                else
                    DrawBattle99ResultOverlay();
                return;
            }

            if (_screen == ScreenState.LocalGame)
            {
                DrawLocalHud();
                if (_pauseMenuOpen || (_localGame != null && _localGame.Paused))
                    DrawPauseOverlay(ScreenState.LocalGame);
                return;
            }

            if (_screen == ScreenState.OnlineGame)
            {
                DrawOnlineHud();
                PongMatch onlineMatch = PongMatch.Instance;
                DrawPingHud(onlineMatch != null ? onlineMatch.LocalPingMs : -1f);
                if (onlineMatch != null && onlineMatch.Paused.Value)
                    DrawPauseOverlay(ScreenState.OnlineGame);
                return;
            }

            if (_screen == ScreenState.Settings && _settingsOpenedFromPause && _settingsReturnScreen == ScreenState.Battle99Game)
            {
                _battle99Game?.DrawGameplay();
                DrawPingHud(_battle99Game != null ? _battle99Game.PingMs : -1f);
            }

            float width = Mathf.Min(760f, Screen.width - 36f);
            float height = Mathf.Min(680f, Screen.height - 36f);
            Rect panel = new Rect((Screen.width - width) * 0.5f, (Screen.height - height) * 0.5f, width, height);
            GUI.Box(panel, GUIContent.none, _panel);

            GUILayout.BeginArea(new Rect(panel.x + 24f, panel.y + 20f, panel.width - 48f, panel.height - 40f));
            GUI.Label(new Rect(0, 0, panel.width - 48f, 48f), "FOUR WAY PONG", _title);

            switch (_screen)
            {
                case ScreenState.Name: DrawName(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Main: DrawMain(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Multiplayer: DrawMultiplayer(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Host: DrawHost(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Join: DrawJoin(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Settings: DrawSettings(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Lobby: DrawLobby(panel.width - 48f, panel.height - 40f); break;
                case ScreenState.Battle99Queue: DrawBattle99Queue(panel.width - 48f, panel.height - 40f); break;
            }

            GUILayout.EndArea();

            if (_screen == ScreenState.Lobby && PongMatch.Instance != null)
                DrawPingHud(PongMatch.Instance.LocalPingMs);
        }

        private void DrawName(float width, float height)
        {
            GUI.Label(new Rect(0, 92, width, 32), "PLAYER NAME", _heading);

            float fieldWidth = Mathf.Min(420f, width);
            float x = (width - fieldWidth) * 0.5f;
            _playerNameDraft = GUI.TextField(new Rect(x, 160, fieldWidth, 54), _playerNameDraft ?? string.Empty, 20, _field);

            bool hasName = !string.IsNullOrWhiteSpace(_playerNameDraft);
            GUI.enabled = hasName;
            if (GUI.Button(new Rect(x, 238, fieldWidth, 54), "CONTINUE", _button))
            {
                PongAudio.PlayMenu();
                PongSettings.SetPlayerName(_playerNameDraft);
                _playerNameDraft = PongSettings.PlayerName;
                _screen = ScreenState.Main;
                _status = string.Empty;
            }
            GUI.enabled = true;
        }

        private void TrySubmitPlayerName()
        {
            if (_nameSubmittedForSession)
                return;

            NetworkManager manager = NetworkManager.Singleton;
            PongMatch match = PongMatch.Instance;
            if (manager == null || match == null || !match.IsSpawned || !manager.IsListening)
                return;

            int seat = match.FindSeat(manager.LocalClientId);
            if (seat < 0)
                return;

            string current = match.GetPlayerNameForSeat(seat);
            if (string.Equals(current, PongSettings.PlayerName, StringComparison.Ordinal))
            {
                _nameSubmittedForSession = true;
                return;
            }

            match.SetPlayerNameServerRpc(PongSettings.PlayerName);
            _nameSubmittedForSession = true;
        }

        private void DrawMain(float width, float height)
        {
            GUI.Label(new Rect(0, 54, width, 30), "", _subtitle);

            float buttonW = 390f;
            float x = (width - buttonW) * 0.5f;
            float y = 82f;

            if (GUI.Button(new Rect(x, y, buttonW, 50), "LOCAL CLASSIC", _button))
                StartLocalGame(PongGameMode.Classic);
            y += 58f;

            if (GUI.Button(new Rect(x, y, buttonW, 50), "LOCAL BRICK WAR", _button))
                StartLocalGame(PongGameMode.BrickWar);
            y += 58f;

            if (GUI.Button(new Rect(x, y, buttonW, 50), "MULTIPLAYER", _button))
            {
                PongAudio.PlayMenu();
                _screen = ScreenState.Multiplayer;
                _status = string.Empty;
            }
            y += 58f;

            bool battle99Ready = _eos != null && _eos.Ready && !_eos.Busy;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && battle99Ready;
            if (GUI.Button(new Rect(x, y, buttonW, 50), "BRICK BATTLE 99", _button))
                StartBattle99Queue();
            GUI.enabled = oldEnabled;
            y += 58f;

            if (GUI.Button(new Rect(x, y, buttonW, 50), "SETTINGS", _button))
            {
                PongAudio.PlayMenu();
                _settingsOpenedFromPause = false;
                _screen = ScreenState.Settings;
                _status = string.Empty;
            }
            y += 58f;

            if (GUI.Button(new Rect(x, y, buttonW, 46), "QUIT", _button))
            {
                PongAudio.PlayMenu();
                Application.Quit();
            }
        }

        private void DrawMultiplayer(float width, float height)
        {
            GUI.Label(new Rect(0, 58, width, 32), "MULTIPLAYER", _heading);
            GUI.Label(new Rect(0, 93, width, 46), "", _small);

            float buttonW = 300f;
            float gap = 24f;
            float x = (width - buttonW * 2f - gap) * 0.5f;

            bool onlineReady = _eos != null && _eos.Ready && !_eos.Busy;
            GUI.enabled = onlineReady;
            if (GUI.Button(new Rect(x, 175, buttonW, 100), "HOST", _button))
            {
                PongAudio.PlayMenu();
                _screen = ScreenState.Host;
                _status = string.Empty;
            }

            if (GUI.Button(new Rect(x + buttonW + gap, 175, buttonW, 100), "JOIN", _button))
            {
                PongAudio.PlayMenu();
                _screen = ScreenState.Join;
                _status = "Enter the host's room code.";
            }
            GUI.enabled = true;

            GUI.Label(new Rect(x, 300, buttonW, 76), "", _centerBody);
            GUI.Label(new Rect(x + buttonW + gap, 300, buttonW, 76), "", _centerBody);

            string eosLine = _eos == null || (!_eos.Ready && !_eos.Busy) ? "Online services unavailable." : (_eos.Busy ? "Connecting" : string.Empty);
            GUI.Label(new Rect(0, 410, width, 64), eosLine, _centerBody);

            if (GUI.Button(new Rect(0, height - 54, 150, 38), "BACK", _button))
            {
                PongAudio.PlayMenu();
                _screen = ScreenState.Main;
                _status = string.Empty;
            }

            DrawStatus(width, height);
        }

        private void DrawHost(float width, float height)
        {
            GUI.Label(new Rect(0, 58, width, 30), "HOST", _heading);
            GUI.Label(new Rect(0, 104, width, 72),
                "Create a room and share the code.", _centerBody);

            bool canHost = !_connecting && _eos != null && _eos.Ready && !_eos.Busy;
            GUI.enabled = canHost;
            if (GUI.Button(new Rect((width - 360f) * 0.5f, 215, 360, 64), _connecting ? "CREATING ROOM" : "HOST GAME", _button))
                HostGame();
            GUI.enabled = true;

            GUI.Label(new Rect(0, 315, width, 80),
                "", _small);

            if (_eos == null || (!_eos.Ready && !_eos.Busy))
                GUI.Label(new Rect(0, 420, width, 70), "Online services unavailable.", _centerBody);

            if (GUI.Button(new Rect(0, height - 54, 150, 38), "BACK", _button))
            {
                PongAudio.PlayMenu();
                _screen = ScreenState.Multiplayer;
                _connecting = false;
            }

            DrawStatus(width, height);
        }

        private void DrawJoin(float width, float height)
        {
            GUI.Label(new Rect(0, 58, width, 30), "JOIN", _heading);
            GUI.Label(new Rect(0, 102, width, 24), "ROOM CODE", _small);
            string enteredCode = GUI.TextField(new Rect(0, 130, width, 52), _joinRoomCode, 6, _field);
            _joinRoomCode = FilterRoomCodeInput(enteredCode);
            GUI.Label(new Rect(0, 194, width, 46), "", _small);

            GUI.enabled = !_connecting && _eos != null && _eos.Ready && !_eos.Busy;
            if (GUI.Button(new Rect(0, 266, width, 52), _connecting ? "CONNECTING" : "JOIN GAME", _button))
                JoinGame();
            GUI.enabled = true;

            if (_eos == null || (!_eos.Ready && !_eos.Busy))
                GUI.Label(new Rect(0, 350, width, 64), "Online services unavailable.", _centerBody);

            if (GUI.Button(new Rect(0, height - 54, 150, 38), "BACK", _button))
            {
                PongAudio.PlayMenu();
                _screen = ScreenState.Multiplayer;
                _connecting = false;
            }

            DrawStatus(width, height);
        }

        private void DrawSettings(float width, float height)
        {
            GUI.Label(new Rect(0, 58, width, 30), "SETTINGS", _heading);

            GUI.Label(new Rect(0, 102, 180, 26), "MASTER VOLUME", _body);
            float newVolume = GUI.HorizontalSlider(new Rect(190, 110, width - 310, 22), PongSettings.MasterVolume, 0f, 1f);
            if (Mathf.Abs(newVolume - PongSettings.MasterVolume) > 0.001f)
                PongSettings.SetVolume(newVolume);
            GUI.Label(new Rect(width - 105, 102, 105, 26), Mathf.RoundToInt(PongSettings.MasterVolume * 100f) + "%", _body);

            bool muted = GUI.Toggle(new Rect(0, 146, 180, 30), PongSettings.Muted, "  Mute");
            if (muted != PongSettings.Muted)
            {
                PongSettings.SetMuted(muted);
                if (!muted) PongAudio.PlayMenu();
            }

            bool showPing = GUI.Toggle(new Rect(205, 146, 220, 30), PongSettings.ShowPing, "  Show ping");
            if (showPing != PongSettings.ShowPing)
                PongSettings.SetShowPing(showPing);

            GUI.Label(new Rect(0, 202, width, 28), "CONTROLS", _heading);
            GUI.Label(new Rect(0, 234, width, 44), "", _small);

            float labelW = 120f;
            float colGap = 18f;
            float colW = (width - labelW - colGap) * 0.5f;
            float p1x = labelW;
            float p2x = labelW + colW + colGap;

            GUI.Label(new Rect(p1x, 286, colW, 28), "PLAYER 1", _heading);
            GUI.Label(new Rect(p2x, 286, colW, 28), "PLAYER 2", _heading);

            GUI.Label(new Rect(0, 328, labelW - 8, 38), "MODE", _body);
            DrawControlModeButton(new Rect(p1x, 324, colW, 42), 1, PongSettings.Player1UsesMouse);
            DrawControlModeButton(new Rect(p2x, 324, colW, 42), 2, PongSettings.Player2UsesMouse);

            GUI.Label(new Rect(0, 382, labelW - 8, 38), "UP", _body);
            DrawBindButton(new Rect(p1x, 378, colW, 42), 1, true, PongSettings.Player1Up, !PongSettings.Player1UsesMouse);
            DrawBindButton(new Rect(p2x, 378, colW, 42), 2, true, PongSettings.Player2Up, !PongSettings.Player2UsesMouse);

            GUI.Label(new Rect(0, 436, labelW - 8, 38), "DOWN", _body);
            DrawBindButton(new Rect(p1x, 432, colW, 42), 1, false, PongSettings.Player1Down, !PongSettings.Player1UsesMouse);
            DrawBindButton(new Rect(p2x, 432, colW, 42), 2, false, PongSettings.Player2Down, !PongSettings.Player2UsesMouse);

            if (GUI.Button(new Rect(0, 496, 240, 40), "RESET CONTROLS", _button))
            {
                PongAudio.PlayMenu();
                PongSettings.ResetBindings();
                _rebindingPlayer = 0;
                _status = string.Empty;
            }

            if (_rebindingPlayer != 0)
                GUI.Label(new Rect(260, 499, width - 260, 40), "Press a key. Escape to cancel.", _small);

            if (GUI.Button(new Rect(0, height - 54, 150, 38), "BACK", _button))
            {
                PongAudio.PlayMenu();
                _rebindingPlayer = 0;
                if (_settingsOpenedFromPause)
                {
                    _screen = _settingsReturnScreen;
                    _settingsOpenedFromPause = false;
                    if (_screen == ScreenState.OnlineGame && PongMatch.Instance != null)
                        _pauseMenuOpen = PongMatch.Instance.Paused.Value;
                }
                else
                {
                    _screen = ScreenState.Main;
                }
                _status = string.Empty;
            }

            DrawStatus(width, height);
        }

        private void DrawControlModeButton(Rect rect, int player, bool usesMouse)
        {
            if (GUI.Button(rect, usesMouse ? "MOUSE" : "KEYBOARD", _button))
            {
                PongAudio.PlayMenu();
                PongSettings.ToggleControlMode(player);
                if (_rebindingPlayer == player)
                    _rebindingPlayer = 0;
                _status = string.Empty;
            }
        }

        private void DrawBindButton(Rect rect, int player, bool positive, KeyCode key, bool enabled)
        {
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && enabled;

            bool waiting = _rebindingPlayer == player && _rebindingPositive == positive;
            string label = waiting ? "PRESS A KEY..." : FriendlyKey(key);
            if (GUI.Button(rect, label, _button))
            {
                PongAudio.PlayMenu();
                _rebindingPlayer = player;
                _rebindingPositive = positive;
                _status = string.Empty;
            }

            GUI.enabled = oldEnabled;
        }

        private void HandleRebindEvent()
        {
            if (_rebindingPlayer == 0)
                return;

            Event e = Event.current;
            if (e == null || e.type != EventType.KeyDown)
                return;

            if (e.keyCode == KeyCode.Escape)
            {
                _rebindingPlayer = 0;
                _status = string.Empty;
                e.Use();
                return;
            }

            if (e.keyCode == KeyCode.None)
                return;

            int player = _rebindingPlayer;
            bool positive = _rebindingPositive;
            PongSettings.SetBinding(player, positive, e.keyCode);
            _rebindingPlayer = 0;
            _status = string.Empty;
            PongAudio.PlayMenu();
            e.Use();
        }

        private static string FilterRoomCodeInput(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            char[] filtered = new char[Mathf.Min(6, value.Length)];
            int count = 0;
            for (int i = 0; i < value.Length && count < 6; i++)
            {
                char c = value[i];
                if (c >= '2' && c <= '9')
                    filtered[count++] = c;
            }

            return new string(filtered, 0, count);
        }

        private static string FriendlyKey(KeyCode key)
        {
            return key switch
            {
                KeyCode.UpArrow => "UP ARROW",
                KeyCode.DownArrow => "DOWN ARROW",
                KeyCode.LeftArrow => "LEFT ARROW",
                KeyCode.RightArrow => "RIGHT ARROW",
                KeyCode.Space => "SPACE",
                KeyCode.Return => "ENTER",
                KeyCode.LeftShift => "LEFT SHIFT",
                KeyCode.RightShift => "RIGHT SHIFT",
                _ => key.ToString().ToUpperInvariant()
            };
        }

        private void DrawLobby(float width, float height)
        {
            PongMatch match = PongMatch.Instance;
            NetworkManager manager = NetworkManager.Singleton;

            GUI.Label(new Rect(0, 58, width, 30), "LOBBY", _heading);

            string roomCode = !string.IsNullOrWhiteSpace(_activeRoomCode)
                ? _activeRoomCode
                : (_eos != null ? _eos.CurrentRoomCode : string.Empty);

            GUI.Label(new Rect(0, 94, width, 22), "ROOM CODE", _small);
            GUI.Label(new Rect(0, 114, width - 180, 48), string.IsNullOrWhiteSpace(roomCode) ? "WAITING" : roomCode, _title);

            if (GUI.Button(new Rect(width - 165, 118, 165, 38), "COPY CODE", _button))
            {
                PongAudio.PlayMenu();
                if (!string.IsNullOrWhiteSpace(roomCode))
                {
                    GUIUtility.systemCopyBuffer = roomCode;
                    _status = string.Empty;
                }
            }

            if (match != null && match.IsSpawned)
            {
                PongGameMode currentMode = (PongGameMode)match.GameMode.Value;
                string modeName = currentMode == PongGameMode.BrickWar ? "BRICK WAR" : "CLASSIC";
                GUI.Label(new Rect(0, 166, 210, 30), "MODE: " + modeName, _body);

                if (_isSessionHost && !match.MatchRunning.Value)
                {
                    float modeGap = 8f;
                    float modeWidth = 125f;
                    float modeX = width - modeWidth * 2f - modeGap;
                    bool oldEnabled = GUI.enabled;

                    GUI.enabled = oldEnabled && currentMode != PongGameMode.Classic;
                    if (GUI.Button(new Rect(modeX, 164, modeWidth, 34), "CLASSIC", _smallButton))
                    {
                        PongAudio.PlayMenu();
                        match.SetGameModeServerRpc((int)PongGameMode.Classic);
                    }

                    GUI.enabled = oldEnabled && currentMode != PongGameMode.BrickWar;
                    if (GUI.Button(new Rect(modeX + modeWidth + modeGap, 164, modeWidth, 34), "BRICK WAR", _smallButton))
                    {
                        PongAudio.PlayMenu();
                        match.SetGameModeServerRpc((int)PongGameMode.BrickWar);
                    }

                    GUI.enabled = oldEnabled;
                }

                int currentLives = match.StartingLives.Value;
                GUI.Label(new Rect(0, 208, 150, 28), "STARTING LIVES", _body);

                bool livesEnabled = GUI.enabled;
                GUI.enabled = livesEnabled && _isSessionHost && !match.MatchRunning.Value;
                float sliderValue = GUI.HorizontalSlider(
                    new Rect(160, 216, width - 250, 22),
                    currentLives,
                    PongConfig.MinStartingLives,
                    PongConfig.MaxStartingLives);
                GUI.enabled = livesEnabled;

                int requestedLives = Mathf.RoundToInt(sliderValue);
                GUI.Label(new Rect(width - 78, 208, 78, 28), currentLives.ToString(), _body);

                if (_isSessionHost && !match.MatchRunning.Value && requestedLives != currentLives)
                    match.SetStartingLivesServerRpc(requestedLives);
            }

            float y = 252f;
            string[] seatNames = { "LEFT", "RIGHT", "TOP", "BOTTOM" };

            for (int i = 0; i < 4; i++)
            {
                ulong client = match != null && match.IsSpawned ? match.GetClientForSeat(i) : PongConfig.EmptyClient;
                string playerName = match != null && match.IsSpawned ? match.GetPlayerNameForSeat(i) : string.Empty;
                string who = client == PongConfig.EmptyClient
                    ? "Open"
                    : (!string.IsNullOrWhiteSpace(playerName)
                        ? playerName
                        : (manager != null && client == manager.LocalClientId ? "You" : "Player " + (client + 1)));

                int team = match != null && match.IsSpawned ? match.GetTeamForSeat(i) : i;
                GUI.Label(new Rect(0, y, 90, 30), seatNames[i], _small);
                GUI.Label(new Rect(98, y, width - 98, 30), who + " : " + PongMatch.TeamName(team), _body);
                y += 36f;
            }

            if (match != null && match.IsSpawned && manager != null)
            {
                int mySeat = match.FindSeat(manager.LocalClientId);
                float controlsY = y + 2f;

                if (mySeat >= 0 && !match.MatchRunning.Value)
                {
                    float seatGap = 8f;
                    float seatButtonWidth = (width - seatGap * 3f) * 0.25f;

                    for (int seat = 0; seat < 4; seat++)
                    {
                        bool open = match.GetClientForSeat(seat) == PongConfig.EmptyClient;
                        bool oldEnabled = GUI.enabled;
                        GUI.enabled = oldEnabled && open;

                        if (GUI.Button(
                            new Rect(seat * (seatButtonWidth + seatGap), controlsY, seatButtonWidth, 36),
                            seatNames[seat],
                            _smallButton))
                        {
                            PongAudio.PlayMenu();
                            match.RequestSeatServerRpc(seat);
                        }

                        GUI.enabled = oldEnabled;
                    }

                    controlsY += 44f;

                    if (GUI.Button(new Rect(0, controlsY, width, 36), "CHANGE COLOR", _smallButton))
                    {
                        PongAudio.PlayMenu();
                        match.CycleTeamServerRpc();
                    }

                    controlsY += 44f;
                }

                if (_isSessionHost)
                {
                    bool canStart = match.CountAssignedPlayers() >= 2 && match.CountActiveTeams() >= 2;
                    GUI.enabled = canStart;

                    if (GUI.Button(new Rect(0, controlsY, width, 42), "START MATCH", _button))
                    {
                        PongAudio.PlayMenu();
                        match.StartMatchServerRpc();
                    }

                    GUI.enabled = true;

                    if (!canStart)
                        GUI.Label(new Rect(0, controlsY + 44f, width, 28f), "2 players on different colors required.", _small);
                }
            }

            if (GUI.Button(new Rect(0, height - 54, 150, 38), "LEAVE", _button))
                LeaveSession();

            DrawStatus(width, height);
        }

        private void DrawBattle99Queue(float width, float height)
        {
            GUI.Label(new Rect(0, 58, width, 30), "BRICK BATTLE 99", _heading);

            int count = _battle99 != null ? _battle99.Members.Count : 0;
            GUI.Label(new Rect(0, 94, width, 28), "QUEUE: " + count + " / " + PongConfig.Battle99MaxPlayers, _body);
            GUI.Label(new Rect(0, 124, width, 26), "ALL READY STARTS MATCH    2 TO 99 PLAYERS", _small);

            Rect scrollView = new Rect(0, 154f, width, Mathf.Max(80f, height - 280f));
            float contentHeight = Mathf.Max(scrollView.height - 4f, count * 31f + 4f);
            _battle99QueueScroll = GUI.BeginScrollView(
                scrollView,
                _battle99QueueScroll,
                new Rect(0, 0, width - 20f, contentHeight));

            if (_battle99 != null)
            {
                float y = 2f;
                for (int i = 0; i < _battle99.Members.Count && i < PongConfig.Battle99MaxPlayers; i++)
                {
                    Battle99Member member = _battle99.Members[i];
                    bool isLocal = member.UserId != null && _battle99.LocalUserId != null && Equals(member.UserId, _battle99.LocalUserId);
                    string name = string.IsNullOrWhiteSpace(member.Name) ? "Player" : member.Name;
                    if (isLocal) name += "  YOU";
                    GUI.Label(new Rect(0, y, width - 170f, 28), name, _body);
                    GUI.Label(new Rect(width - 160f, y, 140f, 28), member.Ready ? "READY" : "WAITING", _small);
                    y += 31f;
                }
            }
            GUI.EndScrollView();

            bool canReady = _battle99 != null && _battle99.InQueue && !_battle99.Busy && !_battle99.MatchStarted;
            bool oldEnabled = GUI.enabled;
            GUI.enabled = oldEnabled && canReady;
            string readyLabel = _battle99 != null && _battle99.LocalReady ? "UNREADY" : "READY";
            if (GUI.Button(new Rect(0, height - 112, width, 46), readyLabel, _button))
            {
                PongAudio.PlayMenu();
                _battle99.ToggleReady();
            }
            GUI.enabled = oldEnabled;

            if (GUI.Button(new Rect(0, height - 54, 150, 38), "LEAVE QUEUE", _button))
                LeaveBattle99();

            if (_battle99 != null && !string.IsNullOrWhiteSpace(_battle99.Status))
                GUI.Label(new Rect(170, height - 58, width - 170, 46), _battle99.Status, _small);
        }

        private void DrawPauseOverlay(ScreenState context)
        {
            float w = 430f;
            float h = 310f;
            Rect box = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(box, GUIContent.none, _panel);

            string title = context == ScreenState.Battle99Game ? "PAUSE MENU" : "PAUSED";
            GUI.Label(new Rect(box.x + 20, box.y + 22, w - 40, 48), title, _title);
            if (context == ScreenState.Battle99Game)
                GUI.Label(new Rect(box.x + 30, box.y + 68, w - 60, 36), "BATTLE CONTINUES", _centerBody);
            else if (context == ScreenState.OnlineGame)
                GUI.Label(new Rect(box.x + 30, box.y + 68, w - 60, 36), "MATCH PAUSED FOR EVERYONE", _centerBody);

            if (GUI.Button(new Rect(box.x + 45, box.y + 116, w - 90, 44), "RESUME", _button))
            {
                PongAudio.PlayMenu();
                ResumeFromPause(context);
            }

            if (GUI.Button(new Rect(box.x + 45, box.y + 170, w - 90, 44), "SETTINGS", _button))
            {
                PongAudio.PlayMenu();
                _settingsOpenedFromPause = true;
                _settingsReturnScreen = context;
                _screen = ScreenState.Settings;
                _status = string.Empty;
            }

            if (GUI.Button(new Rect(box.x + 45, box.y + 224, w - 90, 42), "LEAVE GAME", _smallButton))
            {
                if (context == ScreenState.LocalGame) LeaveLocalGame();
                else if (context == ScreenState.OnlineGame) LeaveSession();
                else if (context == ScreenState.Battle99Game) LeaveBattle99();
            }
        }

        private void ResumeFromPause(ScreenState context)
        {
            _pauseMenuOpen = false;
            if (context == ScreenState.LocalGame)
            {
                _localGame?.SetPaused(false);
            }
            else if (context == ScreenState.OnlineGame)
            {
                PongMatch match = PongMatch.Instance;
                if (match != null && match.IsSpawned && match.Paused.Value)
                    match.SetPausedServerRpc(false);
            }
        }

        private void DrawBattle99ResultOverlay()
        {
            if (_battle99Game == null || !_battle99Game.Active)
                return;

            if (_battle99Game.Spectating)
            {
                float y = Screen.height - 58f;
                if (GUI.Button(new Rect(Screen.width * 0.5f - 225f, y, 130f, 40f), "PREVIOUS", _smallButton))
                    _battle99Game.SelectNextSpectated(-1);
                if (GUI.Button(new Rect(Screen.width * 0.5f - 80f, y, 160f, 40f), "NEXT PLAYER", _smallButton))
                    _battle99Game.SelectNextSpectated(1);
                if (GUI.Button(new Rect(Screen.width * 0.5f + 95f, y, 130f, 40f), "LEAVE", _smallButton))
                    LeaveBattle99();
                return;
            }

            if (!_battle99Game.Eliminated && !_battle99Game.Winner)
                return;

            float w = 460f;
            float h = _battle99Game.Winner ? 210f : 270f;
            Rect box = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(box, GUIContent.none, _panel);
            GUI.Label(new Rect(box.x + 25, box.y + 26, w - 50, 50), _battle99Game.Winner ? "YOU WIN" : "YOU LOSE", _title);

            if (!_battle99Game.Winner)
            {
                GUI.Label(new Rect(box.x + 30, box.y + 78, w - 60, 38), "PLACE " + _battle99Game.Placement, _centerBody);
                if (GUI.Button(new Rect(box.x + 40, box.y + 132, w - 80, 44), "SPECTATE", _button))
                {
                    PongAudio.PlayMenu();
                    _battle99Game.BeginSpectate();
                }
                if (GUI.Button(new Rect(box.x + 40, box.y + 188, w - 80, 40), "LEAVE GAME", _smallButton))
                    LeaveBattle99();
            }
            else
            {
                if (GUI.Button(new Rect(box.x + 40, box.y + 116, w - 80, 44), "LEAVE GAME", _button))
                    LeaveBattle99();
            }
        }

        private void DrawPingHud(float pingMs)
        {
            if (!PongSettings.ShowPing)
                return;

            string value = pingMs < 0f ? "PING --" : "PING " + Mathf.RoundToInt(pingMs) + " ms";
            GUI.Label(new Rect(Screen.width - 300f, 14f, 140f, 26f), value, _small);
        }

        private void DrawLocalHud()
        {
            if (_localGame == null || !_localGame.Active)
                return;

            GUI.Label(
                new Rect(20, 16, 680, 34),
                (_localGame.Mode == PongGameMode.BrickWar ? "BRICK WAR" : "CLASSIC") + "    P1 Lives: " + _localGame.Player1Lives + "    P2 Lives: " + _localGame.Player2Lives,
                _body);

            if (GUI.Button(new Rect(Screen.width - 145, 18, 125, 34), "MAIN MENU", _smallButton))
                LeaveLocalGame();

            if (_localGame.Winner != 0)
            {
                float w = 460f;
                float h = 230f;
                Rect box = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
                GUI.Box(box, GUIContent.none, _panel);
                GUI.Label(new Rect(box.x + 25, box.y + 28, w - 50, 48), "PLAYER " + _localGame.Winner + " WINS", _title);

                if (GUI.Button(new Rect(box.x + 35, box.y + 105, w - 70, 44), "REMATCH", _button))
                {
                    PongAudio.PlayMenu();
                    _localGame.RestartMatch();
                }

                if (GUI.Button(new Rect(box.x + 35, box.y + 158, w - 70, 38), "MAIN MENU", _smallButton))
                    LeaveLocalGame();
            }
        }

        private void DrawOnlineHud()
        {
            PongMatch match = PongMatch.Instance;
            if (match == null || !match.IsSpawned)
                return;

            GUI.Label(new Rect(20, 16, Screen.width - 190, 56), ScoreLine(match), _body);

            if (GUI.Button(new Rect(Screen.width - 145, 18, 125, 34), "LEAVE", _smallButton))
                LeaveSession();

            if (match.WinnerTeam.Value >= 0)
            {
                float w = 460f;
                float h = 220f;
                Rect box = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
                GUI.Box(box, GUIContent.none, _panel);
                GUI.Label(new Rect(box.x + 25, box.y + 28, w - 50, 50), PongMatch.TeamName(match.WinnerTeam.Value) + " WINS", _title);

                if (_isSessionHost)
                {
                    if (GUI.Button(new Rect(box.x + 35, box.y + 112, w - 70, 46), "RETURN TO LOBBY", _button))
                    {
                        PongAudio.PlayMenu();
                        match.ReturnToLobbyServerRpc();
                        _screen = ScreenState.Lobby;
                    }
                }
                else
                {
                    GUI.Label(
                        new Rect(box.x + 35, box.y + 112, w - 70, 46),
                        "Waiting for the host to return to the lobby.",
                        _centerBody);
                }
            }
        }

        private string ScoreLine(PongMatch match)
        {
            string[] seatNames = { "L", "R", "T", "B" };
            string line = string.Empty;

            for (int seat = 0; seat < 4; seat++)
            {
                if (!match.SeatStartedMatch(seat))
                    continue;

                string playerName = match.GetPlayerNameForSeat(seat);
                if (string.IsNullOrWhiteSpace(playerName))
                    playerName = seatNames[seat];

                int lives = match.GetLivesForSeat(seat);
                string lifeText = lives > 0 ? lives.ToString() : "OUT";
                string entry = playerName + " " + PongMatch.TeamName(match.GetTeamForSeat(seat)) + ": " + lifeText;

                if (!string.IsNullOrEmpty(line))
                    line += "    ";
                line += entry;
            }

            return line;
        }

        private void DrawStatus(float width, float height)
        {
            if (string.IsNullOrWhiteSpace(_status))
                return;

            GUI.Label(new Rect(170, height - 58, width - 170, 46), _status, _small);
        }

        private void BuildStyles()
        {
            if (_title != null)
                return;

            _panelTex = MakeTex(new Color(0.035f, 0.045f, 0.075f, 0.97f));
            _buttonTex = MakeTex(new Color(0.12f, 0.18f, 0.30f, 1f));
            _buttonHoverTex = MakeTex(new Color(0.18f, 0.28f, 0.46f, 1f));
            _fieldTex = MakeTex(new Color(0.07f, 0.09f, 0.14f, 1f));

            _panel = new GUIStyle(GUI.skin.box) { normal = { background = _panelTex } };
            _title = new GUIStyle(GUI.skin.label)
            {
                fontSize = 32,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = Color.white }
            };
            _subtitle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.62f, 0.72f, 0.92f) }
            };
            _heading = new GUIStyle(GUI.skin.label)
            {
                fontSize = 19,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.75f, 0.84f, 1f) }
            };
            _body = new GUIStyle(GUI.skin.label)
            {
                fontSize = 18,
                normal = { textColor = Color.white }
            };
            _centerBody = new GUIStyle(_body)
            {
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true
            };
            _small = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = new Color(0.68f, 0.74f, 0.84f) }
            };
            _button = new GUIStyle(GUI.skin.button)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                normal = { background = _buttonTex, textColor = Color.white },
                hover = { background = _buttonHoverTex, textColor = Color.white },
                active = { background = _buttonHoverTex, textColor = Color.white }
            };
            _smallButton = new GUIStyle(_button) { fontSize = 13 };
            _field = new GUIStyle(GUI.skin.textField)
            {
                fontSize = 17,
                padding = new RectOffset(14, 14, 10, 8),
                normal = { background = _fieldTex, textColor = Color.white },
                focused = { background = _fieldTex, textColor = Color.white }
            };
        }

        private static Texture2D MakeTex(Color color)
        {
            Texture2D tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }
    }
}
