using System;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FourWayPong
{
    [RequireComponent(typeof(NetworkObject))]
    public sealed class PongMatch : NetworkBehaviour
    {
        public static PongMatch Instance { get; private set; }

        public readonly NetworkVariable<bool> MatchRunning = new(false);
        public readonly NetworkVariable<bool> Paused = new(false);
        public readonly NetworkVariable<int> WinnerTeam = new(-1);
        public readonly NetworkVariable<int> StartingLives = new(PongConfig.DefaultStartingLives);
        public readonly NetworkVariable<int> GameMode = new((int)PongGameMode.Classic);
        public readonly NetworkVariable<int> ActivePlayersAtStart = new(0);
        public readonly NetworkVariable<int> ActiveSeatMaskAtStart = new(0);
        public readonly NetworkVariable<int> EliminatedSeatMask = new(0);
        public readonly NetworkVariable<int> DebuffedTeamMask = new(0);

        public readonly NetworkVariable<bool> Ball0Active = new(false);
        public readonly NetworkVariable<Vector2> BallPosition = new(Vector2.zero);
        public readonly NetworkVariable<Vector2> BallVelocity = new(Vector2.zero);
        public readonly NetworkVariable<bool> Ball1Active = new(false);
        public readonly NetworkVariable<Vector2> Ball1Position = new(Vector2.zero);
        public readonly NetworkVariable<Vector2> Ball1Velocity = new(Vector2.zero);
        public readonly NetworkVariable<bool> Ball2Active = new(false);
        public readonly NetworkVariable<Vector2> Ball2Position = new(Vector2.zero);
        public readonly NetworkVariable<Vector2> Ball2Velocity = new(Vector2.zero);
        public readonly NetworkVariable<bool> Ball3Active = new(false);
        public readonly NetworkVariable<Vector2> Ball3Position = new(Vector2.zero);
        public readonly NetworkVariable<Vector2> Ball3Velocity = new(Vector2.zero);
        public readonly NetworkVariable<ulong> LastHitterClient = new(PongConfig.EmptyClient);
        public readonly NetworkVariable<int> BallOwnerPacked = new(0);

        public readonly NetworkVariable<ulong> LeftClient = new(PongConfig.EmptyClient);
        public readonly NetworkVariable<ulong> RightClient = new(PongConfig.EmptyClient);
        public readonly NetworkVariable<ulong> TopClient = new(PongConfig.EmptyClient);
        public readonly NetworkVariable<ulong> BottomClient = new(PongConfig.EmptyClient);

        public readonly NetworkVariable<FixedString128Bytes> LeftName = new(default);
        public readonly NetworkVariable<FixedString128Bytes> RightName = new(default);
        public readonly NetworkVariable<FixedString128Bytes> TopName = new(default);
        public readonly NetworkVariable<FixedString128Bytes> BottomName = new(default);

        public readonly NetworkVariable<int> LeftTeam = new(0);
        public readonly NetworkVariable<int> RightTeam = new(1);
        public readonly NetworkVariable<int> TopTeam = new(2);
        public readonly NetworkVariable<int> BottomTeam = new(3);

        public readonly NetworkVariable<float> LeftPaddle = new(0f);
        public readonly NetworkVariable<float> RightPaddle = new(0f);
        public readonly NetworkVariable<float> TopPaddle = new(0f);
        public readonly NetworkVariable<float> BottomPaddle = new(0f);

        public readonly NetworkVariable<int> LeftLives = new(0);
        public readonly NetworkVariable<int> RightLives = new(0);
        public readonly NetworkVariable<int> TopLives = new(0);
        public readonly NetworkVariable<int> BottomLives = new(0);

        public readonly NetworkVariable<float> LeftPaddleScale = new(1f);
        public readonly NetworkVariable<float> RightPaddleScale = new(1f);
        public readonly NetworkVariable<float> TopPaddleScale = new(1f);
        public readonly NetworkVariable<float> BottomPaddleScale = new(1f);

        public readonly NetworkVariable<ulong> BrickMaskLow = new(0UL);
        public readonly NetworkVariable<uint> BrickMaskHigh = new(0U);

        private readonly float[] _serverInput = new float[4];
        private readonly float[] _serverInputAge = new float[4];
        private readonly uint[] _lastServerInputSequence = new uint[4];
        private readonly float[] _simPaddle = new float[4];
        private readonly Vector2[] _simBallPosition = new Vector2[PongConfig.MaxBalls];
        private readonly Vector2[] _simBallVelocity = new Vector2[PongConfig.MaxBalls];
        private readonly float[] _renderPaddle = new float[4];
        private readonly Vector2[] _renderBallPosition = new Vector2[PongConfig.MaxBalls];
        private readonly float[] _snapshotPaddle = new float[4];
        private readonly Vector2[] _snapshotBallPosition = new Vector2[PongConfig.MaxBalls];
        private readonly Vector2[] _snapshotBallVelocity = new Vector2[PongConfig.MaxBalls];
        private readonly int[] _ballLastHitterSeat = { -1, -1, -1, -1 };
        private readonly int[] _ballAllyPassChain = new int[4];
        private readonly float[] _teamDebuffTimer = new float[4];
        private readonly byte[] _brickPowerups = new byte[PongConfig.BrickColumns * PongConfig.BrickRows];
        private readonly bool[] _powerupActive = new bool[PongConfig.MaxPowerupPickups];
        private readonly byte[] _powerupType = new byte[PongConfig.MaxPowerupPickups];
        private readonly Vector2[] _powerupPosition = new Vector2[PongConfig.MaxPowerupPickups];
        private readonly float[] _powerupArmAt = new float[PongConfig.MaxPowerupPickups];
        private readonly float[] _powerupExpireAt = new float[PongConfig.MaxPowerupPickups];
        private readonly bool[] _powerupVisualActive = new bool[PongConfig.MaxPowerupPickups];
        private readonly float[] _moveOld = new float[4];
        private readonly float[] _moveNext = new float[4];
        private readonly float[] _moveLongHalf = new float[4];

        // Chronological motion history used for server-time interpolation.
        private readonly MotionSnapshot[] _snapshotHistory = new MotionSnapshot[PongConfig.SnapshotHistorySize];
        private int _snapshotHistoryCount;
        private float _snapshotJitter;
        private float _targetInterpolationDelay = PongConfig.SnapshotInterpolationBaseDelay;
        private float _currentInterpolationDelay = PongConfig.SnapshotInterpolationBaseDelay;
        private float _lastSnapshotServerTime = -1f;
        private float _serverSimulationAccumulator;

        private float _serveDelay;
        private float _rallyTime;
        private int _serveDirection = 1;
        private int _serveSeat = -1;
        private float _motionSyncTimer;
        private float _inputSyncTimer;
        private float _sfxSyncTimer;
        private int _pendingSfxMask;
        private uint _inputSequence;
        private uint _motionSnapshotSequence;
        private uint _lastReceivedMotionSequence;
        private float _lastMotionSnapshotReceiveTime;
        private bool _hasMotionSnapshot;
        private int _simBallOwnerPacked;
        private int _snapshotBallOwnerPacked;
        private byte _snapshotBallActiveMask;
        private ulong _simBrickMaskLow;
        private uint _simBrickMaskHigh;
        private ulong _snapshotBrickMaskLow;
        private uint _snapshotBrickMaskHigh;
        private float _lastInputSent = 999f;
        private float _currentLocalInput;
        private int _stopRepeatRemaining;
        private float _stopRepeatTimer;
        private int _remainingBricks;
        private bool _brickResetPending;
        private bool _clientMotionInitialized;
        private float _pingTimer;
        private uint _pingSequence;
        public float LocalPingMs { get; private set; } = -1f;

        private readonly GameObject[] _ballVisuals = new GameObject[PongConfig.MaxBalls];
        private readonly Renderer[] _ballRenderers = new Renderer[PongConfig.MaxBalls];
        private readonly int[] _lastBallTeam = { int.MinValue, int.MinValue, int.MinValue, int.MinValue };
        private readonly GameObject[] _paddleVisuals = new GameObject[4];
        private readonly Renderer[] _paddleRenderers = new Renderer[4];
        private readonly int[] _lastPaddleTeam = { -1, -1, -1, -1 };
        private readonly GameObject[] _wallVisuals = new GameObject[4];
        private readonly GameObject[] _brickVisuals = new GameObject[PongConfig.BrickColumns * PongConfig.BrickRows];
        private readonly GameObject[] _powerupVisuals = new GameObject[PongConfig.MaxPowerupPickups];
        private readonly Renderer[] _powerupRenderers = new Renderer[PongConfig.MaxPowerupPickups];
        private readonly Material[] _teamMaterials = new Material[4];
        private readonly Material[] _powerupMaterials = new Material[4];
        private ulong _lastBrickVisualLow = ulong.MaxValue;
        private uint _lastBrickVisualHigh = uint.MaxValue;
        private int _lastBrickVisualMode = -1;
        private bool _lastBrickVisualShow;
        private int _lastWallMask = int.MinValue;
        private Material _whiteMaterial;
        private Material _dimMaterial;
        private Material _brickMaterial;

        private struct MotionSnapshot : INetworkSerializable
        {
            public uint Sequence;
            public float ServerTime;
            public uint AckLeft;
            public uint AckRight;
            public uint AckTop;
            public uint AckBottom;
            public byte BallActiveMask;
            public float LeftPaddle;
            public float RightPaddle;
            public float TopPaddle;
            public float BottomPaddle;
            public Vector2 Ball0Position;
            public Vector2 Ball0Velocity;
            public Vector2 Ball1Position;
            public Vector2 Ball1Velocity;
            public Vector2 Ball2Position;
            public Vector2 Ball2Velocity;
            public Vector2 Ball3Position;
            public Vector2 Ball3Velocity;
            public int BallOwnerPacked;
            public ulong BrickMaskLow;
            public uint BrickMaskHigh;

            public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
            {
                serializer.SerializeValue(ref Sequence);
                serializer.SerializeValue(ref ServerTime);
                serializer.SerializeValue(ref AckLeft);
                serializer.SerializeValue(ref AckRight);
                serializer.SerializeValue(ref AckTop);
                serializer.SerializeValue(ref AckBottom);
                serializer.SerializeValue(ref BallActiveMask);
                serializer.SerializeValue(ref LeftPaddle);
                serializer.SerializeValue(ref RightPaddle);
                serializer.SerializeValue(ref TopPaddle);
                serializer.SerializeValue(ref BottomPaddle);
                serializer.SerializeValue(ref Ball0Position);
                serializer.SerializeValue(ref Ball0Velocity);
                serializer.SerializeValue(ref Ball1Position);
                serializer.SerializeValue(ref Ball1Velocity);
                serializer.SerializeValue(ref Ball2Position);
                serializer.SerializeValue(ref Ball2Velocity);
                serializer.SerializeValue(ref Ball3Position);
                serializer.SerializeValue(ref Ball3Velocity);
                serializer.SerializeValue(ref BallOwnerPacked);
                serializer.SerializeValue(ref BrickMaskLow);
                serializer.SerializeValue(ref BrickMaskHigh);
            }
        }

        private struct GameplayPopup
        {
            public bool Active;
            public string Text;
            public int Team;
            public float StartTime;
            public float Duration;
            public bool IsScore;
        }

        private readonly GameplayPopup[] _gameplayPopups = new GameplayPopup[8];
        private float _countdownStartTime = -1f;
        private float _countdownPauseStarted = -1f;
        private float _countdownPausedTotal;
        private GUIStyle _gameplayPopupStyle;
        private GUIStyle _countdownStyle;

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        public override void OnNetworkSpawn()
        {
            EnsureVisuals();

            if (IsServer)
            {
                NetworkManager.OnClientConnectedCallback += OnClientConnected;
                NetworkManager.OnClientDisconnectCallback += OnClientDisconnected;
                ResetServerState();

                foreach (ulong clientId in NetworkManager.ConnectedClientsIds)
                    AssignConnectedClient(clientId);
            }
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null && IsServer)
            {
                NetworkManager.OnClientConnectedCallback -= OnClientConnected;
                NetworkManager.OnClientDisconnectCallback -= OnClientDisconnected;
            }

            HideAllVisuals();
        }

        private void Update()
        {
            if (!IsSpawned)
                return;

            float dt = Time.unscaledDeltaTime;
            EnsureVisuals();
            CaptureLocalInput(dt);
            UpdatePing(dt);

            if (IsServer)
            {
                // Keep authoritative gameplay independent from the host's render FPS.
                float fixedDt = 1f / PongConfig.ServerSimulationRate;
                _serverSimulationAccumulator = Mathf.Min(
                    _serverSimulationAccumulator + dt,
                    fixedDt * PongConfig.MaxServerCatchupSteps);

                int steps = 0;
                while (_serverSimulationAccumulator >= fixedDt &&
                       steps < PongConfig.MaxServerCatchupSteps)
                {
                    ServerTick(fixedDt);
                    _serverSimulationAccumulator -= fixedDt;
                    steps++;
                }

                if (MatchRunning.Value)
                {
                    _motionSyncTimer += dt;
                    PublishMotionState();
                }

                FlushQueuedSfx(dt);
            }

            UpdateMotionPresentation(dt);
            UpdateLocalVisuals();
        }

        private void CaptureLocalInput(float dt)
        {
            if (!IsClient || NetworkManager == null || !NetworkManager.IsListening || !MatchRunning.Value || Paused.Value)
            {
                _currentLocalInput = 0f;
                return;
            }

            int seat = FindSeat(NetworkManager.LocalClientId);
            if (seat < 0 || !SeatParticipates(seat))
            {
                _currentLocalInput = 0f;
                return;
            }

            float input;
            if (PongSettings.Player1UsesMouse)
            {
                input = GetMouseInputForSeat(seat);
            }
            else
            {
                input = 0f;
                if (Input.GetKey(PongSettings.Player1Up)) input += 1f;
                if (Input.GetKey(PongSettings.Player1Down)) input -= 1f;
            }

            input = Mathf.Clamp(input, -1f, 1f);
            _currentLocalInput = input;
            PredictLocalPaddle(seat, input, dt);

            if (IsServer)
            {
                _serverInput[seat] = input;
                _serverInputAge[seat] = 0f;
                return;
            }

            _inputSyncTimer += dt;
            _stopRepeatTimer += dt;

            float interval = 1f / PongConfig.InputNetworkRate;
            bool hasPrevious = _lastInputSent >= -1f && _lastInputSent <= 1f;
            bool wasMoving = hasPrevious && Mathf.Abs(_lastInputSent) > 0.001f;
            bool isMoving = Mathf.Abs(input) > 0.001f;
            bool released = wasMoving && !isMoving;
            bool engaged = hasPrevious && !wasMoving && isMoving;
            bool directionChanged = wasMoving && isMoving && Mathf.Sign(input) != Mathf.Sign(_lastInputSent);
            bool firstSend = !hasPrevious;

            // Important input changes should not wait for the normal network heartbeat.
            if (firstSend || released || engaged || directionChanged)
            {
                SendLocalInputToServer(seat, input);
                _inputSyncTimer = 0f;

                if (released)
                {
                    // Repeat a stop packet twice. Input uses unreliable delivery, so one
                    // dropped packet should never leave the authoritative paddle moving.
                    _stopRepeatRemaining = PongConfig.InputStopRepeatCount;
                    _stopRepeatTimer = 0f;
                }
                else
                {
                    _stopRepeatRemaining = 0;
                }

                return;
            }

            if (_stopRepeatRemaining > 0 &&
                _stopRepeatTimer >= PongConfig.InputStopRepeatInterval)
            {
                SendLocalInputToServer(seat, 0f);
                _stopRepeatRemaining--;
                _stopRepeatTimer = 0f;
                return;
            }

            if (_inputSyncTimer >= interval)
            {
                SendLocalInputToServer(seat, input);
                _inputSyncTimer = Mathf.Min(_inputSyncTimer - interval, interval);
            }
        }

        private void SendLocalInputToServer(int seat, float input)
        {
            float reportedPosition = seat >= 0 && seat < 4 ? _renderPaddle[seat] : 0f;
            SubmitInputServerRpc(input, reportedPosition, ++_inputSequence);
            _lastInputSent = input;
        }

        private float GetMouseInputForSeat(int seat)
        {
            bool horizontal = seat >= 2;
            float screenSize = horizontal ? Screen.width : Screen.height;
            float mousePosition = horizontal ? Input.mousePosition.x : Input.mousePosition.y;
            float normalized = screenSize > 1f ? Mathf.Clamp01(mousePosition / screenSize) : 0.5f;
            float paddleLength = GetEffectivePaddleLength(seat);

            float limit = horizontal
                ? PongConfig.HalfWidth - paddleLength * 0.5f - 0.25f
                : PongConfig.HalfHeight - paddleLength * 0.5f - 0.25f;

            float current = IsServer ? _simPaddle[seat] : _renderPaddle[seat];
            float target = Mathf.Lerp(-limit, limit, normalized);
            float difference = target - current;
            if (Mathf.Abs(difference) < 0.04f)
                return 0f;

            return Mathf.Clamp(difference / 0.75f, -1f, 1f);
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        private void SubmitInputServerRpc(float input, float reportedPosition, uint sequence, ServerRpcParams rpcParams = default)
        {
            int seat = FindSeat(rpcParams.Receive.SenderClientId);
            if (seat < 0 || (MatchRunning.Value && !SeatParticipates(seat)))
                return;

            uint previous = _lastServerInputSequence[seat];
            if (previous != 0 && (int)(sequence - previous) <= 0)
                return;

            _lastServerInputSequence[seat] = sequence;
            _serverInput[seat] = Mathf.Clamp(input, -1f, 1f);
            _serverInputAge[seat] = 0f;

            // The client reports the exact position it is already displaying. The server
            // remains authoritative by clamping the value and limiting how far one packet
            // may correct the simulation. This removes the visible client/server tug-of-war
            // while keeping impossible teleports out of the authoritative game state.
            if (MatchRunning.Value && SeatParticipates(seat))
                ApplyClientReportedPaddlePosition(seat, reportedPosition);
        }

        private void ApplyClientReportedPaddlePosition(int seat, float reportedPosition)
        {
            if (seat < 0 || seat > 3)
                return;

            float halfLength = GetEffectivePaddleLength(seat) * 0.5f;
            float axisHalf = seat >= 2 ? PongConfig.HalfWidth : PongConfig.HalfHeight;
            float limit = Mathf.Max(0.1f, axisHalf - halfLength - 0.25f);
            float clamped = Mathf.Clamp(reportedPosition, -limit, limit);
            float correction = Mathf.Clamp(
                clamped - _simPaddle[seat],
                -PongConfig.MaxClientPaddlePositionCorrection,
                PongConfig.MaxClientPaddlePositionCorrection);

            _simPaddle[seat] = Mathf.Clamp(_simPaddle[seat] + correction, -limit, limit);
        }

        private void UpdatePing(float dt)
        {
            if (!IsClient || NetworkManager == null || !NetworkManager.IsListening)
                return;

            if (IsServer)
            {
                LocalPingMs = 0f;
                return;
            }

            _pingTimer += dt;
            if (_pingTimer < 1f)
                return;

            _pingTimer = 0f;
            PingServerRpc(++_pingSequence, Time.realtimeSinceStartup);
        }

        [ServerRpc(RequireOwnership = false, Delivery = RpcDelivery.Unreliable)]
        private void PingServerRpc(uint sequence, float sentAt, ServerRpcParams rpcParams = default)
        {
            ulong[] targets = { rpcParams.Receive.SenderClientId };
            ClientRpcParams clientParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = targets }
            };
            PingReplyClientRpc(sequence, sentAt, clientParams);
        }

        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        private void PingReplyClientRpc(uint sequence, float sentAt, ClientRpcParams clientRpcParams = default)
        {
            if (!IsClient || sequence == 0)
                return;
            float ping = Mathf.Max(0f, (Time.realtimeSinceStartup - sentAt) * 1000f);
            LocalPingMs = LocalPingMs < 0f ? ping : Mathf.Lerp(LocalPingMs, ping, 0.25f);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SetPausedServerRpc(bool paused, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !MatchRunning.Value || WinnerTeam.Value >= 0)
                return;

            if (FindSeat(rpcParams.Receive.SenderClientId) < 0)
                return;

            Paused.Value = paused;
            if (paused)
            {
                for (int seat = 0; seat < 4; seat++)
                {
                    _serverInput[seat] = 0f;
                    _serverInputAge[seat] = 0f;
                }
            }
        }

        [ServerRpc(RequireOwnership = false)]
        public void SetPlayerNameServerRpc(string requestedName, ServerRpcParams rpcParams = default)
        {
            int seat = FindSeat(rpcParams.Receive.SenderClientId);
            if (seat < 0)
                return;

            SetNameForSeat(seat, new FixedString128Bytes(SanitizePlayerName(requestedName)));
        }

        [ServerRpc(RequireOwnership = false)]
        public void RequestSeatServerRpc(int requestedSeat, ServerRpcParams rpcParams = default)
        {
            if (MatchRunning.Value || requestedSeat < 0 || requestedSeat > 3)
                return;

            ulong sender = rpcParams.Receive.SenderClientId;
            int current = FindSeat(sender);
            if (current < 0 || current == requestedSeat)
                return;

            if (GetClientForSeat(requestedSeat) != PongConfig.EmptyClient)
                return;

            int currentTeam = GetTeamForSeat(current);
            int destinationTeam = GetTeamForSeat(requestedSeat);
            FixedString128Bytes currentName = GetNameForSeat(current);
            FixedString128Bytes destinationName = GetNameForSeat(requestedSeat);

            SetClientForSeat(current, PongConfig.EmptyClient);
            SetClientForSeat(requestedSeat, sender);
            SetTeamForSeat(requestedSeat, currentTeam);
            SetTeamForSeat(current, destinationTeam);
            SetNameForSeat(requestedSeat, currentName);
            SetNameForSeat(current, destinationName);
        }

        [ServerRpc(RequireOwnership = false)]
        public void CycleTeamServerRpc(ServerRpcParams rpcParams = default)
        {
            if (MatchRunning.Value)
                return;

            int seat = FindSeat(rpcParams.Receive.SenderClientId);
            if (seat >= 0)
                SetTeamForSeat(seat, (GetTeamForSeat(seat) + 1) % 4);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SetStartingLivesServerRpc(int requestedLives, ServerRpcParams rpcParams = default)
        {
            if (MatchRunning.Value || NetworkManager == null || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
                return;

            StartingLives.Value = Mathf.Clamp(requestedLives, PongConfig.MinStartingLives, PongConfig.MaxStartingLives);
        }

        [ServerRpc(RequireOwnership = false)]
        public void SetGameModeServerRpc(int requestedMode, ServerRpcParams rpcParams = default)
        {
            if (MatchRunning.Value || NetworkManager == null || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
                return;

            GameMode.Value = Mathf.Clamp(requestedMode, (int)PongGameMode.Classic, (int)PongGameMode.BrickWar);
            if ((PongGameMode)GameMode.Value == PongGameMode.Classic)
                ClearBricks();
        }

        [ServerRpc(RequireOwnership = false)]
        public void StartMatchServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer || NetworkManager == null || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
                return;

            int connected = CountAssignedPlayers();
            if (connected < 2 || CountActiveTeams() < 2)
                return;

            StartingLives.Value = Mathf.Clamp(StartingLives.Value, PongConfig.MinStartingLives, PongConfig.MaxStartingLives);
            ActivePlayersAtStart.Value = connected;
            ActiveSeatMaskAtStart.Value = BuildAssignedSeatMask();
            EliminatedSeatMask.Value = 0;
            DebuffedTeamMask.Value = 0;
            WinnerTeam.Value = -1;
            Paused.Value = false;

            for (int team = 0; team < 4; team++)
                _teamDebuffTimer[team] = 0f;

            for (int seat = 0; seat < 4; seat++)
            {
                SetLivesForSeat(seat, GetClientForSeat(seat) == PongConfig.EmptyClient ? 0 : StartingLives.Value);
                SetPaddleScaleForSeat(seat, 1f);
                _serverInput[seat] = 0f;
                _serverInputAge[seat] = 0f;
                _lastServerInputSequence[seat] = 0;
            }

            for (int seat = 0; seat < 4; seat++)
            {
                _simPaddle[seat] = 0f;
                _renderPaddle[seat] = 0f;
                _snapshotPaddle[seat] = 0f;
            }

            _brickResetPending = false;
            if ((PongGameMode)GameMode.Value == PongGameMode.BrickWar)
                ResetBricks();
            else
                ClearBricks();

            MatchRunning.Value = true;
            PublishMotionState(true);
            QueueServe();
        }

        [ServerRpc(RequireOwnership = false)]
        public void ReturnToLobbyServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer || NetworkManager == null || rpcParams.Receive.SenderClientId != NetworkManager.ServerClientId)
                return;

            MatchRunning.Value = false;
            Paused.Value = false;
            WinnerTeam.Value = -1;
            ActivePlayersAtStart.Value = 0;
            ActiveSeatMaskAtStart.Value = 0;
            EliminatedSeatMask.Value = 0;
            DebuffedTeamMask.Value = 0;
            BrickMaskLow.Value = 0UL;
            BrickMaskHigh.Value = 0U;
            LastHitterClient.Value = PongConfig.EmptyClient;
            BallOwnerPacked.Value = 0;
            ClearPowerupPickups();

            for (int seat = 0; seat < 4; seat++)
            {
                SetLivesForSeat(seat, 0);
                SetPaddleScaleForSeat(seat, 1f);
                _serverInput[seat] = 0f;
                _serverInputAge[seat] = 0f;
            }

            ClearAllBalls();
            ClearBricks();
            PublishMotionState(true);
        }

        private void ServerTick(float dt)
        {
            if (!MatchRunning.Value || Paused.Value)
                return;

            UpdateDebuffs(dt);
            ExpireStaleInputs(dt);
            MovePaddles(dt);

            if (_serveDelay > 0f)
            {
                if ((PongGameMode)GameMode.Value == PongGameMode.BrickWar && _serveSeat >= 0 && SeatParticipates(_serveSeat))
                    SetBallPosition(0, GetServeSpawnPosition(_serveSeat));

                _serveDelay -= dt;
                if (_serveDelay <= 0f)
                    LaunchServe();

                return;
            }

            _rallyTime += dt;
            float targetSpeed = PongConfig.BallStartSpeed + _rallyTime * PongConfig.BallAccelerationPerSecond;

            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                if (!GetBallActive(ball))
                    continue;

                Vector2 pos = GetBallPosition(ball);
                Vector2 vel = GetBallVelocity(ball);
                if (vel.sqrMagnitude <= 0.001f)
                    continue;

                vel = vel.normalized * targetSpeed;
                int steps = Mathf.Clamp(
                    Mathf.CeilToInt(targetSpeed * dt / PongConfig.BallSimulationStepDistance),
                    1,
                    PongConfig.MaxBallSimulationSubsteps);
                float stepDt = dt / steps;
                bool removed = false;

                for (int i = 0; i < steps; i++)
                {
                    pos += vel * stepDt;

                    ResolvePaddleCollision(ref pos, ref vel, (int)PaddleSeat.Left, ball);
                    ResolvePaddleCollision(ref pos, ref vel, (int)PaddleSeat.Right, ball);
                    ResolvePaddleCollision(ref pos, ref vel, (int)PaddleSeat.Top, ball);
                    ResolvePaddleCollision(ref pos, ref vel, (int)PaddleSeat.Bottom, ball);

                    if ((PongGameMode)GameMode.Value == PongGameMode.BrickWar)
                    {
                        ResolveBrickCollision(ref pos, ref vel, ball);
                        TryCollectPowerup(ball, pos, vel);
                    }

                    if (HandleBoundsAndGoals(ref pos, ref vel, ball))
                    {
                        removed = true;
                        break;
                    }
                }

                if (!removed && GetBallActive(ball))
                {
                    SetBallPosition(ball, pos);
                    SetBallVelocity(ball, vel);
                }
            }

            if (MatchRunning.Value && CountActiveBalls() == 0 && _serveDelay <= 0f)
                QueueServe();
        }

        private void ExpireStaleInputs(float dt)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                if (!SeatParticipates(seat))
                {
                    _serverInput[seat] = 0f;
                    _serverInputAge[seat] = 0f;
                    continue;
                }

                _serverInputAge[seat] += dt;
                if (_serverInputAge[seat] > PongConfig.InputStaleTimeout)
                    _serverInput[seat] = 0f;
            }
        }

        private void UpdateDebuffs(float dt)
        {
            int mask = DebuffedTeamMask.Value;
            bool changed = false;

            for (int team = 0; team < 4; team++)
            {
                if (_teamDebuffTimer[team] <= 0f)
                    continue;

                _teamDebuffTimer[team] -= dt;
                if (_teamDebuffTimer[team] <= 0f)
                {
                    _teamDebuffTimer[team] = 0f;
                    mask &= ~(1 << team);
                    changed = true;
                }
            }

            if (changed)
                DebuffedTeamMask.Value = mask;
        }

        private void ApplyTeamStallDebuff(int team)
        {
            if (team < 0 || team > 3)
                return;

            _teamDebuffTimer[team] = PongConfig.AllyPassDebuffDuration;
            DebuffedTeamMask.Value |= 1 << team;
        }

        private float GetCurrentPaddleSpeed()
        {
            float currentBallSpeed = PongConfig.BallStartSpeed;
            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                if (GetBallActive(ball))
                    currentBallSpeed = Mathf.Max(currentBallSpeed, GetBallVelocity(ball).magnitude);
            }

            float ballSpeedRatio = currentBallSpeed / PongConfig.BallStartSpeed;
            return PongConfig.PaddleSpeed *
                Mathf.Lerp(1f, ballSpeedRatio, PongConfig.PaddleSpeedScaling);
        }

        private void MovePaddles(float dt)
        {
            float paddleSpeed = GetCurrentPaddleSpeed();

            for (int seat = 0; seat < 4; seat++)
            {
                _moveOld[seat] = _simPaddle[seat];
                _moveLongHalf[seat] = GetEffectivePaddleLength(seat) * 0.5f;
                float axisHalf = seat >= 2 ? PongConfig.HalfWidth : PongConfig.HalfHeight;
                float limit = Mathf.Max(0.1f, axisHalf - _moveLongHalf[seat] - 0.25f);
                float input = SeatParticipates(seat) ? _serverInput[seat] : 0f;
                _moveNext[seat] = Mathf.Clamp(_moveOld[seat] + input * paddleSpeed * dt, -limit, limit);
            }

            bool leftActive = SeatParticipates(0);
            bool rightActive = SeatParticipates(1);
            bool topActive = SeatParticipates(2);
            bool bottomActive = SeatParticipates(3);

            if (topActive && rightActive)
                ResolveCornerBlock(ref _moveNext[2], ref _moveNext[1], _moveOld[2], _moveOld[1], 1f, 1f, _moveLongHalf[2], _moveLongHalf[1]);
            if (topActive && leftActive)
                ResolveCornerBlock(ref _moveNext[2], ref _moveNext[0], _moveOld[2], _moveOld[0], -1f, 1f, _moveLongHalf[2], _moveLongHalf[0]);
            if (bottomActive && rightActive)
                ResolveCornerBlock(ref _moveNext[3], ref _moveNext[1], _moveOld[3], _moveOld[1], 1f, -1f, _moveLongHalf[3], _moveLongHalf[1]);
            if (bottomActive && leftActive)
                ResolveCornerBlock(ref _moveNext[3], ref _moveNext[0], _moveOld[3], _moveOld[0], -1f, -1f, _moveLongHalf[3], _moveLongHalf[0]);

            for (int seat = 0; seat < 4; seat++)
                _simPaddle[seat] = _moveNext[seat];
        }

        private static void ResolveCornerBlock(
            ref float horizontalPaddle,
            ref float verticalPaddle,
            float oldHorizontal,
            float oldVertical,
            float horizontalCornerSign,
            float verticalCornerSign,
            float horizontalLongHalf,
            float verticalLongHalf)
        {
            float shortHalf = PongConfig.PaddleShort * 0.5f;
            float planeX = PongConfig.HalfWidth - PongConfig.PaddleInset;
            float planeY = PongConfig.HalfHeight - PongConfig.PaddleInset;
            float horizontalCornerLimit = planeX - shortHalf - horizontalLongHalf - PongConfig.CornerPaddleGap;
            float verticalCornerLimit = planeY - shortHalf - verticalLongHalf - PongConfig.CornerPaddleGap;

            float newH = horizontalCornerSign * horizontalPaddle;
            float newV = verticalCornerSign * verticalPaddle;
            if (newH <= horizontalCornerLimit || newV <= verticalCornerLimit)
                return;

            float oldH = horizontalCornerSign * oldHorizontal;
            float oldV = verticalCornerSign * oldVertical;
            bool horizontalAlreadyOwnedCorner = oldH > horizontalCornerLimit && oldV <= verticalCornerLimit;
            bool verticalAlreadyOwnedCorner = oldV > verticalCornerLimit && oldH <= horizontalCornerLimit;

            if (horizontalAlreadyOwnedCorner)
                verticalPaddle = verticalCornerSign * verticalCornerLimit;
            else if (verticalAlreadyOwnedCorner)
                horizontalPaddle = horizontalCornerSign * horizontalCornerLimit;
            else
            {
                horizontalPaddle = horizontalCornerSign * horizontalCornerLimit;
                verticalPaddle = verticalCornerSign * verticalCornerLimit;
            }
        }

        private void ResolvePaddleCollision(ref Vector2 pos, ref Vector2 vel, int seat, int ballIndex)
        {
            if (!SeatParticipates(seat))
                return;

            float planeX = PongConfig.HalfWidth - PongConfig.PaddleInset;
            float planeY = PongConfig.HalfHeight - PongConfig.PaddleInset;
            float longHalf = GetEffectivePaddleLength(seat) * 0.5f;
            float shortHalf = PongConfig.PaddleShort * 0.5f;
            bool hit = false;

            if (seat == 0 && vel.x < 0f && pos.x - PongConfig.BallRadius <= -planeX + shortHalf && pos.x > -PongConfig.HalfWidth)
            {
                float offset = pos.y - _simPaddle[0];
                if (Mathf.Abs(offset) <= longHalf + PongConfig.BallRadius)
                {
                    pos.x = -planeX + shortHalf + PongConfig.BallRadius;
                    vel = ReflectVertical(vel, offset / Mathf.Max(0.01f, longHalf), 1f);
                    hit = true;
                }
            }
            else if (seat == 1 && vel.x > 0f && pos.x + PongConfig.BallRadius >= planeX - shortHalf && pos.x < PongConfig.HalfWidth)
            {
                float offset = pos.y - _simPaddle[1];
                if (Mathf.Abs(offset) <= longHalf + PongConfig.BallRadius)
                {
                    pos.x = planeX - shortHalf - PongConfig.BallRadius;
                    vel = ReflectVertical(vel, offset / Mathf.Max(0.01f, longHalf), -1f);
                    hit = true;
                }
            }
            else if (seat == 2 && vel.y > 0f && pos.y + PongConfig.BallRadius >= planeY - shortHalf && pos.y < PongConfig.HalfHeight)
            {
                float offset = pos.x - _simPaddle[2];
                if (Mathf.Abs(offset) <= longHalf + PongConfig.BallRadius)
                {
                    pos.y = planeY - shortHalf - PongConfig.BallRadius;
                    vel = ReflectHorizontal(vel, offset / Mathf.Max(0.01f, longHalf), -1f);
                    hit = true;
                }
            }
            else if (seat == 3 && vel.y < 0f && pos.y - PongConfig.BallRadius <= -planeY + shortHalf && pos.y > -PongConfig.HalfHeight)
            {
                float offset = pos.x - _simPaddle[3];
                if (Mathf.Abs(offset) <= longHalf + PongConfig.BallRadius)
                {
                    pos.y = -planeY + shortHalf + PongConfig.BallRadius;
                    vel = ReflectHorizontal(vel, offset / Mathf.Max(0.01f, longHalf), 1f);
                    hit = true;
                }
            }

            if (!hit)
                return;

            RegisterPaddleHit(ballIndex, seat);
            QueueSfx(0);
        }

        private void RegisterPaddleHit(int ballIndex, int seat)
        {
            int previousSeat = _ballLastHitterSeat[ballIndex];
            int currentTeam = GetTeamForSeat(seat);

            if (previousSeat >= 0)
            {
                int previousTeam = GetTeamForSeat(previousSeat);
                if (previousTeam == currentTeam && previousSeat != seat && CountLivingPlayersOnTeam(currentTeam) >= 2)
                {
                    _ballAllyPassChain[ballIndex]++;
                    if (_ballAllyPassChain[ballIndex] >= PongConfig.AllyPassesBeforeDebuff)
                    {
                        ApplyTeamStallDebuff(currentTeam);
                        _ballAllyPassChain[ballIndex] = 0;
                    }
                }
                else
                {
                    _ballAllyPassChain[ballIndex] = 0;
                }
            }
            else
            {
                _ballAllyPassChain[ballIndex] = 0;
            }

            _ballLastHitterSeat[ballIndex] = seat;
            SetBallOwnerSeat(ballIndex, seat);
        }

        private static Vector2 ReflectVertical(Vector2 current, float hitOffset, float direction)
        {
            float speed = Mathf.Max(current.magnitude, PongConfig.BallStartSpeed);
            Vector2 v = new Vector2(direction, Mathf.Clamp(hitOffset, -0.85f, 0.85f) * 0.9f).normalized * speed;
            if (Mathf.Abs(v.x) < speed * 0.45f) v.x = direction * speed * 0.45f;
            return v.normalized * speed;
        }

        private static Vector2 ReflectHorizontal(Vector2 current, float hitOffset, float direction)
        {
            float speed = Mathf.Max(current.magnitude, PongConfig.BallStartSpeed);
            Vector2 v = new Vector2(Mathf.Clamp(hitOffset, -0.85f, 0.85f) * 0.9f, direction).normalized * speed;
            if (Mathf.Abs(v.y) < speed * 0.45f) v.y = direction * speed * 0.45f;
            return v.normalized * speed;
        }

        private bool HandleBoundsAndGoals(ref Vector2 pos, ref Vector2 vel, int ballIndex)
        {
            if (pos.x < -PongConfig.HalfWidth - PongConfig.BallRadius)
            {
                if (SeatParticipates(0))
                    return ScoreAgainstSeat(0, ballIndex);

                pos.x = -PongConfig.HalfWidth + PongConfig.BallRadius;
                vel.x = Mathf.Abs(vel.x);
                QueueSfx(1);
            }

            if (pos.x > PongConfig.HalfWidth + PongConfig.BallRadius)
            {
                if (SeatParticipates(1))
                    return ScoreAgainstSeat(1, ballIndex);

                pos.x = PongConfig.HalfWidth - PongConfig.BallRadius;
                vel.x = -Mathf.Abs(vel.x);
                QueueSfx(1);
            }

            if (pos.y > PongConfig.HalfHeight + PongConfig.BallRadius)
            {
                if (SeatParticipates(2))
                    return ScoreAgainstSeat(2, ballIndex);

                pos.y = PongConfig.HalfHeight - PongConfig.BallRadius;
                vel.y = -Mathf.Abs(vel.y);
                QueueSfx(1);
            }

            if (pos.y < -PongConfig.HalfHeight - PongConfig.BallRadius)
            {
                if (SeatParticipates(3))
                    return ScoreAgainstSeat(3, ballIndex);

                pos.y = -PongConfig.HalfHeight + PongConfig.BallRadius;
                vel.y = Mathf.Abs(vel.y);
                QueueSfx(1);
            }

            return false;
        }

        private bool ScoreAgainstSeat(int concedingSeat, int ballIndex)
        {
            QueueSfx(2);

            int scorerSeat = ballIndex >= 0 && ballIndex < _ballLastHitterSeat.Length ? _ballLastHitterSeat[ballIndex] : -1;
            if (scorerSeat >= 0 && SeatStartedMatch(scorerSeat))
            {
                string scorerName = GetPlayerNameForSeat(scorerSeat);
                if (string.IsNullOrWhiteSpace(scorerName))
                    scorerName = TeamName(GetTeamForSeat(scorerSeat));

                ShowGameplayPopupClientRpc(
                    scorerName.ToUpperInvariant() + " SCORES!",
                    GetTeamForSeat(scorerSeat),
                    PongConfig.ScorePopupDuration,
                    true);
            }
            else
            {
                ShowGameplayPopupClientRpc("SCORE!", -1, PongConfig.ScorePopupDuration, true);
            }

            int lives = Mathf.Max(0, GetLivesForSeat(concedingSeat) - 1);
            SetLivesForSeat(concedingSeat, lives);
            SetBallActive(ballIndex, false);
            SetBallVelocity(ballIndex, Vector2.zero);

            if (lives <= 0)
            {
                EliminatedSeatMask.Value |= 1 << concedingSeat;
                _serverInput[concedingSeat] = 0f;
            }

            if (EvaluateWinner())
                return true;

            _serveDirection *= -1;
            return true;
        }

        private bool EvaluateWinner()
        {
            bool[] seen = new bool[4];
            int activeTeams = 0;
            int remainingTeam = -1;

            for (int seat = 0; seat < 4; seat++)
            {
                if (!SeatParticipates(seat))
                    continue;

                int team = Mathf.Clamp(GetTeamForSeat(seat), 0, 3);
                if (seen[team])
                    continue;

                seen[team] = true;
                activeTeams++;
                remainingTeam = team;
            }

            if (activeTeams > 1)
                return false;

            if (activeTeams == 1)
            {
                WinnerTeam.Value = remainingTeam;
                MatchRunning.Value = false;
                ClearPowerupPickups();
                ClearAllBalls();
                return true;
            }

            return false;
        }

        private void QueueServe()
        {
            if ((PongGameMode)GameMode.Value == PongGameMode.BrickWar && _brickResetPending)
                ResetBricks();

            ClearPowerupPickups();
            ClearAllBalls();
            ResetBallHitHistory();

            _serveSeat = -1;
            Vector2 spawn = Vector2.zero;
            if ((PongGameMode)GameMode.Value == PongGameMode.BrickWar)
            {
                _serveSeat = ChooseRandomLivingSeat();
                if (_serveSeat >= 0)
                {
                    spawn = GetServeSpawnPosition(_serveSeat);
                    _ballLastHitterSeat[0] = _serveSeat;
                    SetBallOwnerSeat(0, _serveSeat);
                }
            }
            else
            {
                SetBallOwnerSeat(0, -1);
            }

            SetBallActive(0, true);
            SetBallPosition(0, spawn);
            SetBallVelocity(0, Vector2.zero);
            _rallyTime = 0f;
            _serveDelay = PongConfig.ServeDelay;
            StartServeCountdownClientRpc();
            PublishMotionState(true);
        }

        private void LaunchServe()
        {
            if (!MatchRunning.Value)
                return;

            SetBallActive(0, true);

            if ((PongGameMode)GameMode.Value == PongGameMode.BrickWar && _serveSeat >= 0 && SeatParticipates(_serveSeat))
            {
                Vector2 spawn = GetServeSpawnPosition(_serveSeat);
                Vector2 direction = (-spawn).normalized;
                Vector2 tangent = new Vector2(-direction.y, direction.x);
                direction = (direction + tangent * UnityEngine.Random.Range(-0.28f, 0.28f)).normalized;
                SetBallPosition(0, spawn);
                SetBallVelocity(0, direction * PongConfig.BallStartSpeed);
            }
            else
            {
                float x = UnityEngine.Random.Range(0.72f, 1f) * _serveDirection;
                float y = UnityEngine.Random.Range(-0.65f, 0.65f);
                if (Mathf.Abs(y) < 0.22f)
                    y = 0.22f * (UnityEngine.Random.value < 0.5f ? -1f : 1f);

                SetBallPosition(0, Vector2.zero);
                SetBallVelocity(0, new Vector2(x, y).normalized * PongConfig.BallStartSpeed);
            }

            _rallyTime = 0f;
            PublishMotionState(true);
        }

        private int ChooseRandomLivingSeat()
        {
            int count = 0;
            for (int seat = 0; seat < 4; seat++)
                if (SeatParticipates(seat))
                    count++;

            if (count <= 0)
                return -1;

            int pick = UnityEngine.Random.Range(0, count);
            for (int seat = 0; seat < 4; seat++)
            {
                if (!SeatParticipates(seat))
                    continue;
                if (pick-- == 0)
                    return seat;
            }

            return -1;
        }

        private Vector2 GetServeSpawnPosition(int seat)
        {
            float planeX = PongConfig.HalfWidth - PongConfig.PaddleInset;
            float planeY = PongConfig.HalfHeight - PongConfig.PaddleInset;
            float inward = PongConfig.PaddleShort * 0.5f + PongConfig.BallRadius + PongConfig.ServeSpawnGap;

            return seat switch
            {
                0 => new Vector2(-planeX + inward, _simPaddle[0]),
                1 => new Vector2(planeX - inward, _simPaddle[1]),
                2 => new Vector2(_simPaddle[2], planeY - inward),
                3 => new Vector2(_simPaddle[3], -planeY + inward),
                _ => Vector2.zero
            };
        }

        private void ResetBallHitHistory()
        {
            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                _ballLastHitterSeat[i] = -1;
                _ballAllyPassChain[i] = 0;
            }
        }

        private void ResetBricks()
        {
            int brickCount = PongConfig.BrickColumns * PongConfig.BrickRows;
            ulong low = ulong.MaxValue;
            uint high = (1u << (brickCount - 64)) - 1u;

            int centerIndex = (PongConfig.BrickRows / 2) * PongConfig.BrickColumns + (PongConfig.BrickColumns / 2);
            if (centerIndex < 64)
                low &= ~(1UL << centerIndex);
            else
                high &= ~(1u << (centerIndex - 64));

            _simBrickMaskLow = low;
            _simBrickMaskHigh = high;
            BrickMaskLow.Value = low;
            BrickMaskHigh.Value = high;
            _remainingBricks = brickCount - 1;
            _brickResetPending = false;

            for (int i = 0; i < brickCount; i++)
            {
                _brickPowerups[i] = 0;
                if (i == centerIndex)
                    continue;

                if (UnityEngine.Random.value < PongConfig.BrickPowerupChance)
                    _brickPowerups[i] = (byte)UnityEngine.Random.Range(1, 4);
            }
        }

        private void ClearBricks()
        {
            _simBrickMaskLow = 0UL;
            _simBrickMaskHigh = 0U;
            BrickMaskLow.Value = 0UL;
            BrickMaskHigh.Value = 0U;
            _remainingBricks = 0;
            _brickResetPending = false;
            Array.Clear(_brickPowerups, 0, _brickPowerups.Length);
        }

        private void ResolveBrickCollision(ref Vector2 pos, ref Vector2 vel, int ballIndex)
        {
            if (!MatchRunning.Value || (PongGameMode)GameMode.Value != PongGameMode.BrickWar || _remainingBricks <= 0)
                return;

            float stepX = PongConfig.BrickWidth + PongConfig.BrickGap;
            float stepY = PongConfig.BrickHeight + PongConfig.BrickGap;
            float gridOffsetX = (PongConfig.BrickColumns - 1) * 0.5f;
            float gridOffsetY = (PongConfig.BrickRows - 1) * 0.5f;
            int centerColumn = Mathf.RoundToInt(pos.x / stepX + gridOffsetX);
            int centerRow = Mathf.RoundToInt(pos.y / stepY + gridOffsetY);

            int minColumn = Mathf.Max(0, centerColumn - 1);
            int maxColumn = Mathf.Min(PongConfig.BrickColumns - 1, centerColumn + 1);
            int minRow = Mathf.Max(0, centerRow - 1);
            int maxRow = Mathf.Min(PongConfig.BrickRows - 1, centerRow + 1);

            for (int row = minRow; row <= maxRow; row++)
            {
                for (int column = minColumn; column <= maxColumn; column++)
                {
                    int index = row * PongConfig.BrickColumns + column;
                    if (TryResolveBrickAt(index, ref pos, ref vel, ballIndex))
                        return;
                }
            }
        }

        private bool TryResolveBrickAt(int index, ref Vector2 pos, ref Vector2 vel, int ballIndex)
        {
            if (!IsBrickAlive(index))
                return false;

            float halfW = PongConfig.BrickWidth * 0.5f;
            float halfH = PongConfig.BrickHeight * 0.5f;
            Vector2 center = GetBrickCenter(index);
            float dx = pos.x - center.x;
            float dy = pos.y - center.y;
            float overlapX = halfW + PongConfig.BallRadius - Mathf.Abs(dx);
            float overlapY = halfH + PongConfig.BallRadius - Mathf.Abs(dy);
            if (overlapX <= 0f || overlapY <= 0f)
                return false;

            SetBrickAlive(index, false);
            _remainingBricks = Mathf.Max(0, _remainingBricks - 1);
            if (_remainingBricks == 0)
                _brickResetPending = true;

            if (overlapX < overlapY)
            {
                float sign = Mathf.Abs(dx) > 0.0001f ? Mathf.Sign(dx) : (vel.x > 0f ? -1f : 1f);
                pos.x = center.x + sign * (halfW + PongConfig.BallRadius);
                vel.x = -vel.x;
            }
            else
            {
                float sign = Mathf.Abs(dy) > 0.0001f ? Mathf.Sign(dy) : (vel.y > 0f ? -1f : 1f);
                pos.y = center.y + sign * (halfH + PongConfig.BallRadius);
                vel.y = -vel.y;
            }

            QueueSfx(1);
            byte powerup = _brickPowerups[index];
            _brickPowerups[index] = 0;
            if (powerup != 0)
                SpawnPowerupPickup(powerup, GetBrickCenter(index));
            return true;
        }

        private void SpawnPowerupPickup(byte type, Vector2 position)
        {
            int slot = -1;
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                if (!_powerupActive[i])
                {
                    slot = i;
                    break;
                }
            }

            if (slot < 0)
                return;

            _powerupActive[slot] = true;
            _powerupType[slot] = type;
            _powerupPosition[slot] = position;
            _powerupArmAt[slot] = Time.time + PongConfig.PowerupArmDelay;
            _powerupExpireAt[slot] = float.PositiveInfinity;

            SetPowerupVisual(slot, type, position, true);
            ShowPowerupClientRpc(slot, type, position);
        }

        private void UpdatePowerupPickups()
        {
            float now = Time.time;
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                if (_powerupActive[i] && now >= _powerupExpireAt[i])
                    RemovePowerupPickup(i);
            }
        }

        private void TryCollectPowerup(int ballIndex, Vector2 ballPosition, Vector2 ballVelocity)
        {
            float now = Time.time;
            float pickupDistance = PongConfig.PowerupPickupRadius + PongConfig.BallRadius;
            float pickupDistanceSqr = pickupDistance * pickupDistance;

            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                if (!_powerupActive[i] || now < _powerupArmAt[i])
                    continue;

                if ((ballPosition - _powerupPosition[i]).sqrMagnitude > pickupDistanceSqr)
                    continue;

                byte type = _powerupType[i];
                RemovePowerupPickup(i);
                ApplyCollectedPowerup(type, ballIndex, ballPosition, ballVelocity);
                return;
            }
        }

        private void ApplyCollectedPowerup(byte powerup, int ballIndex, Vector2 pos, Vector2 vel)
        {
            int hitterSeat = _ballLastHitterSeat[ballIndex];
            int team = hitterSeat >= 0 ? GetTeamForSeat(hitterSeat) : -1;
            bool applied = false;
            string popupText = string.Empty;

            switch (powerup)
            {
                case 1:
                    applied = DuplicateBall(ballIndex, pos, vel);
                    popupText = "MULTIBALL";
                    break;
                case 2:
                    if (team >= 0)
                    {
                        GrowTeamPaddles(team);
                        applied = true;
                        popupText = "PADDLE EXTEND";
                    }
                    break;
                case 3:
                    if (team >= 0)
                    {
                        ShrinkEnemyPaddles(team);
                        applied = true;
                        popupText = "PADDLE SHRINK";
                    }
                    break;
            }

            if (applied && !string.IsNullOrEmpty(popupText))
                ShowGameplayPopupClientRpc(popupText, team, PongConfig.GameplayPopupDuration, false);
        }

        private void RemovePowerupPickup(int slot)
        {
            if (slot < 0 || slot >= PongConfig.MaxPowerupPickups || !_powerupActive[slot])
                return;

            _powerupActive[slot] = false;
            _powerupType[slot] = 0;
            _powerupArmAt[slot] = 0f;
            _powerupExpireAt[slot] = 0f;
            SetPowerupVisual(slot, 0, Vector2.zero, false);
            HidePowerupClientRpc(slot);
        }

        private void ClearPowerupPickups()
        {
            bool any = false;
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                any |= _powerupActive[i] || _powerupVisualActive[i];
                _powerupActive[i] = false;
                _powerupType[i] = 0;
                _powerupPosition[i] = Vector2.zero;
                _powerupArmAt[i] = 0f;
                _powerupExpireAt[i] = 0f;
                SetPowerupVisual(i, 0, Vector2.zero, false);
            }

            if (IsServer && IsSpawned && any)
                ClearPowerupVisualsClientRpc();
        }

        [ClientRpc]
        private void ShowPowerupClientRpc(int slot, int type, Vector2 position)
        {
            SetPowerupVisual(slot, type, position, true);
        }

        [ClientRpc]
        private void HidePowerupClientRpc(int slot)
        {
            SetPowerupVisual(slot, 0, Vector2.zero, false);
        }

        [ClientRpc]
        private void ClearPowerupVisualsClientRpc()
        {
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
                SetPowerupVisual(i, 0, Vector2.zero, false);
        }

        private bool DuplicateBall(int sourceBall, Vector2 pos, Vector2 vel)
        {
            int targetBall = -1;
            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                if (!GetBallActive(i))
                {
                    targetBall = i;
                    break;
                }
            }

            if (targetBall < 0)
                return false;

            float angle = UnityEngine.Random.value < 0.5f ? -32f : 32f;
            float radians = angle * Mathf.Deg2Rad;
            Vector2 direction = vel.sqrMagnitude > 0.001f ? vel.normalized : Vector2.right;
            Vector2 rotated = new Vector2(
                direction.x * Mathf.Cos(radians) - direction.y * Mathf.Sin(radians),
                direction.x * Mathf.Sin(radians) + direction.y * Mathf.Cos(radians));

            float speed = Mathf.Max(PongConfig.BallStartSpeed, vel.magnitude);
            SetBallActive(targetBall, true);
            SetBallPosition(targetBall, pos + rotated.normalized * (PongConfig.BallRadius * 1.2f));
            SetBallVelocity(targetBall, rotated.normalized * speed);
            _ballLastHitterSeat[targetBall] = _ballLastHitterSeat[sourceBall];
            _ballAllyPassChain[targetBall] = _ballAllyPassChain[sourceBall];
            SetBallOwnerSeat(targetBall, GetBallOwnerSeat(sourceBall));
            return true;
        }

        private void GrowTeamPaddles(int team)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                if (!SeatStartedMatch(seat) || GetTeamForSeat(seat) != team)
                    continue;

                float scale = Mathf.Clamp(GetPaddleScaleForSeat(seat) + PongConfig.AllyGrowAmount, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
                SetPaddleScaleForSeat(seat, scale);
            }
        }

        private void ShrinkEnemyPaddles(int sourceTeam)
        {
            for (int seat = 0; seat < 4; seat++)
            {
                if (!SeatStartedMatch(seat) || GetTeamForSeat(seat) == sourceTeam)
                    continue;

                float scale = Mathf.Clamp(GetPaddleScaleForSeat(seat) - PongConfig.EnemyShrinkAmount, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
                SetPaddleScaleForSeat(seat, scale);
            }
        }

        private static Vector2 GetBrickCenter(int index)
        {
            int column = index % PongConfig.BrickColumns;
            int row = index / PongConfig.BrickColumns;
            float stepX = PongConfig.BrickWidth + PongConfig.BrickGap;
            float stepY = PongConfig.BrickHeight + PongConfig.BrickGap;
            float x = (column - (PongConfig.BrickColumns - 1) * 0.5f) * stepX;
            float y = (row - (PongConfig.BrickRows - 1) * 0.5f) * stepY;
            return new Vector2(x, y);
        }

        public bool IsBrickAlive(int index)
        {
            if (index < 0 || index >= PongConfig.BrickColumns * PongConfig.BrickRows)
                return false;

            ulong low = IsServer ? _simBrickMaskLow : (_hasMotionSnapshot ? _snapshotBrickMaskLow : BrickMaskLow.Value);
            uint high = IsServer ? _simBrickMaskHigh : (_hasMotionSnapshot ? _snapshotBrickMaskHigh : BrickMaskHigh.Value);

            if (index < 64)
                return (low & (1UL << index)) != 0;

            return (high & (1u << (index - 64))) != 0;
        }

        private void SetBrickAlive(int index, bool alive)
        {
            if (index < 0 || index >= PongConfig.BrickColumns * PongConfig.BrickRows)
                return;

            if (index < 64)
            {
                ulong bit = 1UL << index;
                _simBrickMaskLow = alive ? _simBrickMaskLow | bit : _simBrickMaskLow & ~bit;
            }
            else
            {
                uint bit = 1u << (index - 64);
                _simBrickMaskHigh = alive ? _simBrickMaskHigh | bit : _simBrickMaskHigh & ~bit;
            }
        }

        [ClientRpc]
        private void ShowGameplayPopupClientRpc(string text, int team, float duration, bool isScore)
        {
            AddGameplayPopup(text, team, duration, isScore);
        }

        [ClientRpc]
        private void StartServeCountdownClientRpc()
        {
            _countdownStartTime = Time.unscaledTime;
            _countdownPauseStarted = -1f;
            _countdownPausedTotal = 0f;
        }

        private void AddGameplayPopup(string text, int team, float duration, bool isScore)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            int slot = -1;
            float oldest = float.MaxValue;
            int oldestSlot = 0;
            for (int i = 0; i < _gameplayPopups.Length; i++)
            {
                if (!_gameplayPopups[i].Active)
                {
                    slot = i;
                    break;
                }

                if (_gameplayPopups[i].StartTime < oldest)
                {
                    oldest = _gameplayPopups[i].StartTime;
                    oldestSlot = i;
                }
            }

            if (slot < 0)
                slot = oldestSlot;

            _gameplayPopups[slot] = new GameplayPopup
            {
                Active = true,
                Text = text,
                Team = team,
                StartTime = Time.unscaledTime,
                Duration = Mathf.Max(0.25f, duration),
                IsScore = isScore
            };
        }

        private void EnsureOverlayStyles()
        {
            if (_gameplayPopupStyle == null)
            {
                _gameplayPopupStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false
                };
            }

            if (_countdownStyle == null)
            {
                _countdownStyle = new GUIStyle(GUI.skin.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Bold,
                    wordWrap = false
                };
            }
        }

        private void OnGUI()
        {
            if (!IsSpawned)
                return;

            EnsureOverlayStyles();
            DrawServeCountdown();
            DrawGameplayPopups();
        }

        private void DrawServeCountdown()
        {
            if (_countdownStartTime < 0f)
                return;

            float now = Time.unscaledTime;
            if (Paused.Value)
            {
                if (_countdownPauseStarted < 0f)
                    _countdownPauseStarted = now;
            }
            else if (_countdownPauseStarted >= 0f)
            {
                _countdownPausedTotal += now - _countdownPauseStarted;
                _countdownPauseStarted = -1f;
            }

            float effectiveNow = Paused.Value && _countdownPauseStarted >= 0f ? _countdownPauseStarted : now;
            float elapsed = effectiveNow - _countdownStartTime - _countdownPausedTotal;
            if (elapsed >= PongConfig.ServeDelay)
            {
                _countdownStartTime = -1f;
                return;
            }

            float numberDuration = PongConfig.ServeCountdownNumberDuration;
            string text;
            float segmentStart;
            float segmentDuration;

            if (elapsed < numberDuration)
            {
                text = "3";
                segmentStart = 0f;
                segmentDuration = numberDuration;
            }
            else if (elapsed < numberDuration * 2f)
            {
                text = "2";
                segmentStart = numberDuration;
                segmentDuration = numberDuration;
            }
            else if (elapsed < numberDuration * 3f)
            {
                text = "1";
                segmentStart = numberDuration * 2f;
                segmentDuration = numberDuration;
            }
            else
            {
                text = "START";
                segmentStart = numberDuration * 3f;
                segmentDuration = PongConfig.ServeCountdownStartDuration;
            }

            float progress = Mathf.Clamp01((elapsed - segmentStart) / Mathf.Max(0.01f, segmentDuration));
            float alpha = 1f - Mathf.SmoothStep(0f, 1f, progress);
            float scale = Mathf.Lerp(1.18f, 0.88f, progress);
            int fontSize = text == "START"
                ? Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.085f, 42f, 90f))
                : Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.16f, 76f, 170f));

            _countdownStyle.fontSize = fontSize;
            _countdownStyle.normal.textColor = new Color(1f, 1f, 1f, alpha);

            Rect rect = new Rect(0f, Screen.height * 0.31f, Screen.width, Screen.height * 0.30f);
            Matrix4x4 oldMatrix = GUI.matrix;
            Vector2 pivot = rect.center;
            GUIUtility.ScaleAroundPivot(new Vector2(scale, scale), pivot);
            GUI.Label(rect, text, _countdownStyle);
            GUI.matrix = oldMatrix;
        }

        private void DrawGameplayPopups()
        {
            float now = Time.unscaledTime;
            int powerupRow = 0;

            for (int i = 0; i < _gameplayPopups.Length; i++)
            {
                GameplayPopup popup = _gameplayPopups[i];
                if (!popup.Active)
                    continue;

                float progress = (now - popup.StartTime) / Mathf.Max(0.01f, popup.Duration);
                if (progress >= 1f)
                {
                    popup.Active = false;
                    _gameplayPopups[i] = popup;
                    continue;
                }

                float alpha = 1f - Mathf.SmoothStep(0.42f, 1f, progress);
                float intro = Mathf.Clamp01(progress / 0.14f);
                float popScale = Mathf.Lerp(0.72f, 1.12f, intro);
                if (progress > 0.14f)
                    popScale = Mathf.Lerp(1.12f, 1f, Mathf.Clamp01((progress - 0.14f) / 0.16f));

                Color color = popup.Team >= 0 ? TeamColor(popup.Team) : Color.white;
                color.a = alpha;
                _gameplayPopupStyle.normal.textColor = color;
                _gameplayPopupStyle.fontSize = popup.IsScore
                    ? Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.070f, 34f, 68f))
                    : Mathf.RoundToInt(Mathf.Clamp(Screen.height * 0.046f, 24f, 48f));

                float baseY;
                if (popup.IsScore)
                {
                    baseY = Screen.height * 0.23f;
                }
                else
                {
                    baseY = Screen.height * 0.39f + powerupRow * 42f;
                    powerupRow++;
                }

                float rise = progress * (popup.IsScore ? 34f : 48f);
                Rect rect = new Rect(0f, baseY - rise, Screen.width, 90f);
                Matrix4x4 oldMatrix = GUI.matrix;
                GUIUtility.ScaleAroundPivot(new Vector2(popScale, popScale), rect.center);
                GUI.Label(rect, popup.Text, _gameplayPopupStyle);
                GUI.matrix = oldMatrix;
            }
        }

        private void QueueSfx(int soundId)
        {
            if (soundId < 0 || soundId > 2)
                return;

            _pendingSfxMask |= 1 << soundId;

            if (IsHost)
                PlaySfxLocal(1 << soundId);
        }

        private void FlushQueuedSfx(float dt)
        {
            if (_pendingSfxMask == 0)
                return;

            _sfxSyncTimer += dt;
            float interval = 1f / PongConfig.MotionNetworkRate;
            if (_sfxSyncTimer < interval)
                return;

            int mask = _pendingSfxMask;
            _pendingSfxMask = 0;
            _sfxSyncTimer = 0f;
            PlaySfxClientRpc(mask);
        }

        private static void PlaySfxLocal(int soundMask)
        {
            if ((soundMask & (1 << 0)) != 0) PongAudio.PlayPaddle();
            if ((soundMask & (1 << 1)) != 0) PongAudio.PlayWall();
            if ((soundMask & (1 << 2)) != 0) PongAudio.PlayScore();
        }

        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        private void PlaySfxClientRpc(int soundMask)
        {
            PlaySfxLocal(soundMask);
        }

        private void OnClientConnected(ulong clientId)
        {
            AssignConnectedClient(clientId);
        }

        private void AssignConnectedClient(ulong clientId)
        {
            if (FindSeat(clientId) >= 0)
                return;

            if (LeftClient.Value == PongConfig.EmptyClient)
            {
                LeftClient.Value = clientId;
                LeftName.Value = default;
            }
            else if (RightClient.Value == PongConfig.EmptyClient)
            {
                RightClient.Value = clientId;
                RightName.Value = default;
            }
            else if (TopClient.Value == PongConfig.EmptyClient)
            {
                TopClient.Value = clientId;
                TopName.Value = default;
            }
            else if (BottomClient.Value == PongConfig.EmptyClient)
            {
                BottomClient.Value = clientId;
                BottomName.Value = default;
            }
        }

        private void OnClientDisconnected(ulong clientId)
        {
            int seat = FindSeat(clientId);
            if (seat >= 0)
            {
                if (MatchRunning.Value && SeatStartedMatch(seat))
                {
                    SetLivesForSeat(seat, 0);
                    EliminatedSeatMask.Value |= 1 << seat;
                }

                SetClientForSeat(seat, PongConfig.EmptyClient);
                SetNameForSeat(seat, default);
                _serverInput[seat] = 0f;
                _serverInputAge[seat] = 0f;
                _lastServerInputSequence[seat] = 0;
            }

            if (MatchRunning.Value)
                EvaluateWinner();
        }

        private void ResetServerState()
        {
            MatchRunning.Value = false;
            Paused.Value = false;
            WinnerTeam.Value = -1;
            StartingLives.Value = PongConfig.DefaultStartingLives;
            GameMode.Value = (int)PongGameMode.Classic;
            ActivePlayersAtStart.Value = 0;
            ActiveSeatMaskAtStart.Value = 0;
            EliminatedSeatMask.Value = 0;
            DebuffedTeamMask.Value = 0;
            LastHitterClient.Value = PongConfig.EmptyClient;
            BallOwnerPacked.Value = 0;
            LeftClient.Value = RightClient.Value = TopClient.Value = BottomClient.Value = PongConfig.EmptyClient;
            LeftName.Value = RightName.Value = TopName.Value = BottomName.Value = default;
            LeftTeam.Value = 0;
            RightTeam.Value = 1;
            TopTeam.Value = 2;
            BottomTeam.Value = 3;
            LeftPaddle.Value = RightPaddle.Value = TopPaddle.Value = BottomPaddle.Value = 0f;
            LeftLives.Value = RightLives.Value = TopLives.Value = BottomLives.Value = 0;
            LeftPaddleScale.Value = RightPaddleScale.Value = TopPaddleScale.Value = BottomPaddleScale.Value = 1f;

            for (int seat = 0; seat < 4; seat++)
            {
                _serverInput[seat] = 0f;
                _serverInputAge[seat] = 0f;
                _lastServerInputSequence[seat] = 0;
                _simPaddle[seat] = 0f;
                _renderPaddle[seat] = 0f;
            }

            _motionSyncTimer = 0f;
            _serverSimulationAccumulator = 0f;
            _inputSyncTimer = 0f;
            _sfxSyncTimer = 0f;
            _pendingSfxMask = 0;
            _lastInputSent = 999f;
            _currentLocalInput = 0f;
            _stopRepeatRemaining = 0;
            _stopRepeatTimer = 0f;
            _inputSequence = 0;
            _motionSnapshotSequence = 0;
            _lastReceivedMotionSequence = 0;
            _lastMotionSnapshotReceiveTime = 0f;
            _lastSnapshotServerTime = -1f;
            _snapshotHistoryCount = 0;
            _snapshotJitter = 0f;
            _targetInterpolationDelay = PongConfig.SnapshotInterpolationBaseDelay;
            _currentInterpolationDelay = PongConfig.SnapshotInterpolationBaseDelay;
            _hasMotionSnapshot = false;
            _clientMotionInitialized = false;
            _simBallOwnerPacked = 0;
            _snapshotBallOwnerPacked = 0;
            _snapshotBallActiveMask = 0;
            _simBrickMaskLow = 0UL;
            _simBrickMaskHigh = 0U;
            _snapshotBrickMaskLow = 0UL;
            _snapshotBrickMaskHigh = 0U;
            _serveSeat = -1;
            _countdownStartTime = -1f;
            ClearPowerupPickups();
            ClearBricks();
            ClearAllBalls();
            PublishMotionState(true);
            ResetBallHitHistory();
        }

        public int FindSeat(ulong clientId)
        {
            if (clientId == PongConfig.EmptyClient) return -1;
            if (LeftClient.Value == clientId) return 0;
            if (RightClient.Value == clientId) return 1;
            if (TopClient.Value == clientId) return 2;
            if (BottomClient.Value == clientId) return 3;
            return -1;
        }

        public int CountAssignedPlayers()
        {
            int count = 0;
            for (int i = 0; i < 4; i++)
                if (GetClientForSeat(i) != PongConfig.EmptyClient)
                    count++;
            return count;
        }

        public int CountActiveTeams()
        {
            bool[] seen = new bool[4];
            int count = 0;
            for (int seat = 0; seat < 4; seat++)
            {
                if (GetClientForSeat(seat) == PongConfig.EmptyClient)
                    continue;

                int team = Mathf.Clamp(GetTeamForSeat(seat), 0, 3);
                if (seen[team])
                    continue;

                seen[team] = true;
                count++;
            }
            return count;
        }

        private int CountLivingPlayersOnTeam(int team)
        {
            int count = 0;
            for (int seat = 0; seat < 4; seat++)
                if (SeatParticipates(seat) && GetTeamForSeat(seat) == team)
                    count++;
            return count;
        }

        public ulong GetClientForSeat(int seat)
        {
            return seat switch
            {
                0 => LeftClient.Value,
                1 => RightClient.Value,
                2 => TopClient.Value,
                3 => BottomClient.Value,
                _ => PongConfig.EmptyClient
            };
        }

        private void SetClientForSeat(int seat, ulong clientId)
        {
            switch (seat)
            {
                case 0: LeftClient.Value = clientId; break;
                case 1: RightClient.Value = clientId; break;
                case 2: TopClient.Value = clientId; break;
                case 3: BottomClient.Value = clientId; break;
            }
        }

        public string GetPlayerNameForSeat(int seat)
        {
            return GetNameForSeat(seat).ToString();
        }

        private FixedString128Bytes GetNameForSeat(int seat)
        {
            return seat switch
            {
                0 => LeftName.Value,
                1 => RightName.Value,
                2 => TopName.Value,
                3 => BottomName.Value,
                _ => default
            };
        }

        private void SetNameForSeat(int seat, FixedString128Bytes name)
        {
            switch (seat)
            {
                case 0: LeftName.Value = name; break;
                case 1: RightName.Value = name; break;
                case 2: TopName.Value = name; break;
                case 3: BottomName.Value = name; break;
            }
        }

        private static string SanitizePlayerName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Player";

            value = value.Trim();
            string cleaned = string.Empty;
            for (int i = 0; i < value.Length && cleaned.Length < 20; i++)
            {
                char c = value[i];
                if (!char.IsControl(c))
                    cleaned += c;
            }

            return string.IsNullOrWhiteSpace(cleaned) ? "Player" : cleaned;
        }

        public int GetTeamForSeat(int seat)
        {
            return seat switch
            {
                0 => LeftTeam.Value,
                1 => RightTeam.Value,
                2 => TopTeam.Value,
                3 => BottomTeam.Value,
                _ => -1
            };
        }

        private void SetTeamForSeat(int seat, int team)
        {
            switch (seat)
            {
                case 0: LeftTeam.Value = team; break;
                case 1: RightTeam.Value = team; break;
                case 2: TopTeam.Value = team; break;
                case 3: BottomTeam.Value = team; break;
            }
        }

        public int GetLivesForSeat(int seat)
        {
            return seat switch
            {
                0 => LeftLives.Value,
                1 => RightLives.Value,
                2 => TopLives.Value,
                3 => BottomLives.Value,
                _ => 0
            };
        }

        private void SetLivesForSeat(int seat, int value)
        {
            value = Mathf.Max(0, value);
            switch (seat)
            {
                case 0: LeftLives.Value = value; break;
                case 1: RightLives.Value = value; break;
                case 2: TopLives.Value = value; break;
                case 3: BottomLives.Value = value; break;
            }
        }

        public float GetPaddleScaleForSeat(int seat)
        {
            return seat switch
            {
                0 => LeftPaddleScale.Value,
                1 => RightPaddleScale.Value,
                2 => TopPaddleScale.Value,
                3 => BottomPaddleScale.Value,
                _ => 1f
            };
        }

        private void SetPaddleScaleForSeat(int seat, float value)
        {
            value = Mathf.Clamp(value, PongConfig.MinPaddleScale, PongConfig.MaxPaddleScale);
            switch (seat)
            {
                case 0: LeftPaddleScale.Value = value; break;
                case 1: RightPaddleScale.Value = value; break;
                case 2: TopPaddleScale.Value = value; break;
                case 3: BottomPaddleScale.Value = value; break;
            }
        }

        public float GetEffectivePaddleLength(int seat)
        {
            float scale = GetPaddleScaleForSeat(seat);
            int team = GetTeamForSeat(seat);
            if (team >= 0 && (DebuffedTeamMask.Value & (1 << team)) != 0)
                scale *= PongConfig.AllyPassDebuffPaddleScale;
            float baseLength = (seat == 2 || seat == 3)
                ? PongConfig.HorizontalPaddleLong
                : PongConfig.PaddleLong;
            return baseLength * scale;
        }

        private float GetPaddlePosition(int seat)
        {
            if (seat < 0 || seat > 3)
                return 0f;

            return IsServer ? _simPaddle[seat] : GetNetworkPaddlePosition(seat);
        }

        private float GetNetworkPaddlePosition(int seat)
        {
            if (_hasMotionSnapshot && seat >= 0 && seat < 4)
                return _snapshotPaddle[seat];

            return seat switch
            {
                0 => LeftPaddle.Value,
                1 => RightPaddle.Value,
                2 => TopPaddle.Value,
                3 => BottomPaddle.Value,
                _ => 0f
            };
        }

        private int BuildAssignedSeatMask()
        {
            int mask = 0;
            for (int seat = 0; seat < 4; seat++)
                if (GetClientForSeat(seat) != PongConfig.EmptyClient)
                    mask |= 1 << seat;
            return mask;
        }

        public bool SeatStartedMatch(int seat)
        {
            return seat >= 0 && seat < 4 && (ActiveSeatMaskAtStart.Value & (1 << seat)) != 0;
        }

        private bool SeatIsPresent(int seat)
        {
            if (seat < 0 || seat > 3 || GetClientForSeat(seat) == PongConfig.EmptyClient)
                return false;

            bool matchSnapshotActive = ActivePlayersAtStart.Value > 0 && (MatchRunning.Value || WinnerTeam.Value >= 0);
            if (!matchSnapshotActive)
                return true;

            return SeatStartedMatch(seat)
                && (EliminatedSeatMask.Value & (1 << seat)) == 0
                && GetLivesForSeat(seat) > 0;
        }

        public bool SeatParticipates(int seat)
        {
            return SeatIsPresent(seat);
        }

        private int CountActiveBalls()
        {
            int count = 0;
            for (int i = 0; i < PongConfig.MaxBalls; i++)
                if (GetBallActive(i))
                    count++;
            return count;
        }

        public bool GetBallActive(int index)
        {
            if (index < 0 || index >= PongConfig.MaxBalls)
                return false;

            if (!IsServer && _hasMotionSnapshot)
                return (_snapshotBallActiveMask & (1 << index)) != 0;

            return index switch
            {
                0 => Ball0Active.Value,
                1 => Ball1Active.Value,
                2 => Ball2Active.Value,
                3 => Ball3Active.Value,
                _ => false
            };
        }

        public Vector2 GetBallPosition(int index)
        {
            if (index < 0 || index >= PongConfig.MaxBalls)
                return Vector2.zero;

            return IsServer ? _simBallPosition[index] : GetNetworkBallPosition(index);
        }

        public Vector2 GetBallVelocity(int index)
        {
            if (index < 0 || index >= PongConfig.MaxBalls)
                return Vector2.zero;

            return IsServer ? _simBallVelocity[index] : GetNetworkBallVelocity(index);
        }

        private Vector2 GetNetworkBallPosition(int index)
        {
            if (_hasMotionSnapshot && index >= 0 && index < PongConfig.MaxBalls)
                return _snapshotBallPosition[index];

            return index switch
            {
                0 => BallPosition.Value,
                1 => Ball1Position.Value,
                2 => Ball2Position.Value,
                3 => Ball3Position.Value,
                _ => Vector2.zero
            };
        }

        private Vector2 GetNetworkBallVelocity(int index)
        {
            if (_hasMotionSnapshot && index >= 0 && index < PongConfig.MaxBalls)
                return _snapshotBallVelocity[index];

            return index switch
            {
                0 => BallVelocity.Value,
                1 => Ball1Velocity.Value,
                2 => Ball2Velocity.Value,
                3 => Ball3Velocity.Value,
                _ => Vector2.zero
            };
        }

        public int GetBallOwnerSeat(int index)
        {
            if (index < 0 || index >= PongConfig.MaxBalls)
                return -1;

            int packed = IsServer ? _simBallOwnerPacked : (_hasMotionSnapshot ? _snapshotBallOwnerPacked : BallOwnerPacked.Value);
            int code = (packed >> (index * 3)) & 0x7;
            return code == 0 ? -1 : code - 1;
        }

        private void SetBallOwnerSeat(int index, int seat)
        {
            if (index < 0 || index >= PongConfig.MaxBalls)
                return;

            int shift = index * 3;
            int mask = 0x7 << shift;
            int code = seat >= 0 && seat < 4 ? seat + 1 : 0;
            _simBallOwnerPacked = (_simBallOwnerPacked & ~mask) | (code << shift);
        }

        private void SetBallActive(int index, bool value)
        {
            switch (index)
            {
                case 0: Ball0Active.Value = value; break;
                case 1: Ball1Active.Value = value; break;
                case 2: Ball2Active.Value = value; break;
                case 3: Ball3Active.Value = value; break;
            }
        }

        private void SetBallPosition(int index, Vector2 value)
        {
            if (index >= 0 && index < PongConfig.MaxBalls)
                _simBallPosition[index] = value;
        }

        private void SetBallVelocity(int index, Vector2 value)
        {
            if (index >= 0 && index < PongConfig.MaxBalls)
                _simBallVelocity[index] = value;
        }

        private void ClearAllBalls()
        {
            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                SetBallActive(i, false);
                _simBallPosition[i] = Vector2.zero;
                _simBallVelocity[i] = Vector2.zero;
                _renderBallPosition[i] = Vector2.zero;
            }

            _simBallOwnerPacked = 0;
        }

        private void PublishMotionState(bool force = false)
        {
            if (!IsServer)
                return;

            float interval = 1f / PongConfig.MotionNetworkRate;
            if (!force && _motionSyncTimer < interval)
                return;

            if (force)
                _motionSyncTimer = 0f;
            else
                _motionSyncTimer = Mathf.Max(0f, _motionSyncTimer - interval);

            byte activeMask = 0;
            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                if (GetBallActive(ball))
                    activeMask |= (byte)(1 << ball);
            }

            MotionSnapshot snapshot = new MotionSnapshot
            {
                Sequence = ++_motionSnapshotSequence,
                ServerTime = NetworkManager != null ? NetworkManager.ServerTime.TimeAsFloat : Time.unscaledTime,
                AckLeft = _lastServerInputSequence[0],
                AckRight = _lastServerInputSequence[1],
                AckTop = _lastServerInputSequence[2],
                AckBottom = _lastServerInputSequence[3],
                BallActiveMask = activeMask,
                LeftPaddle = _simPaddle[0],
                RightPaddle = _simPaddle[1],
                TopPaddle = _simPaddle[2],
                BottomPaddle = _simPaddle[3],
                Ball0Position = _simBallPosition[0],
                Ball0Velocity = _simBallVelocity[0],
                Ball1Position = _simBallPosition[1],
                Ball1Velocity = _simBallVelocity[1],
                Ball2Position = _simBallPosition[2],
                Ball2Velocity = _simBallVelocity[2],
                Ball3Position = _simBallPosition[3],
                Ball3Velocity = _simBallVelocity[3],
                BallOwnerPacked = _simBallOwnerPacked,
                BrickMaskLow = _simBrickMaskLow,
                BrickMaskHigh = _simBrickMaskHigh
            };

            MotionSnapshotClientRpc(snapshot);
        }

        [ClientRpc(Delivery = RpcDelivery.Unreliable)]
        private void MotionSnapshotClientRpc(MotionSnapshot snapshot)
        {
            if (IsServer)
                return;

            if (_hasMotionSnapshot && (int)(snapshot.Sequence - _lastReceivedMotionSequence) <= 0)
                return;

            float receiveNow = Time.unscaledTime;

            if (_snapshotHistoryCount > 0)
            {
                MotionSnapshot previous = _snapshotHistory[_snapshotHistoryCount - 1];
                float serverDelta = snapshot.ServerTime - previous.ServerTime;
                float arrivalDelta = receiveNow - _lastMotionSnapshotReceiveTime;

                if (serverDelta > 0.001f && arrivalDelta >= 0f)
                {
                    float jitterSample = Mathf.Abs(arrivalDelta - serverDelta);
                    _snapshotJitter = Mathf.Lerp(
                        _snapshotJitter,
                        jitterSample,
                        PongConfig.SnapshotJitterSmoothing);

                    _targetInterpolationDelay = Mathf.Clamp(
                        PongConfig.SnapshotInterpolationBaseDelay +
                        _snapshotJitter * PongConfig.SnapshotJitterMultiplier,
                        PongConfig.SnapshotInterpolationMinDelay,
                        PongConfig.SnapshotInterpolationMaxDelay);
                }
            }
            else
            {
                _snapshotJitter = 0f;
                _targetInterpolationDelay = PongConfig.SnapshotInterpolationBaseDelay;
                _currentInterpolationDelay = PongConfig.SnapshotInterpolationBaseDelay;
            }

            AddSnapshotToHistory(snapshot);

            _lastReceivedMotionSequence = snapshot.Sequence;
            _lastMotionSnapshotReceiveTime = receiveNow;
            _lastSnapshotServerTime = snapshot.ServerTime;
            _hasMotionSnapshot = true;

            _snapshotPaddle[0] = snapshot.LeftPaddle;
            _snapshotPaddle[1] = snapshot.RightPaddle;
            _snapshotPaddle[2] = snapshot.TopPaddle;
            _snapshotPaddle[3] = snapshot.BottomPaddle;
            _snapshotBallPosition[0] = snapshot.Ball0Position;
            _snapshotBallVelocity[0] = snapshot.Ball0Velocity;
            _snapshotBallPosition[1] = snapshot.Ball1Position;
            _snapshotBallVelocity[1] = snapshot.Ball1Velocity;
            _snapshotBallPosition[2] = snapshot.Ball2Position;
            _snapshotBallVelocity[2] = snapshot.Ball2Velocity;
            _snapshotBallPosition[3] = snapshot.Ball3Position;
            _snapshotBallVelocity[3] = snapshot.Ball3Velocity;
            _snapshotBallOwnerPacked = snapshot.BallOwnerPacked;
            _snapshotBallActiveMask = snapshot.BallActiveMask;
            _snapshotBrickMaskLow = snapshot.BrickMaskLow;
            _snapshotBrickMaskHigh = snapshot.BrickMaskHigh;

            if (!_clientMotionInitialized)
            {
                for (int seat = 0; seat < 4; seat++)
                    _renderPaddle[seat] = GetSnapshotPaddle(snapshot, seat);

                for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
                    _renderBallPosition[ball] = GetSnapshotBallPosition(snapshot, ball);

                _clientMotionInitialized = true;
            }
        }

        private void AddSnapshotToHistory(MotionSnapshot snapshot)
        {
            if (_snapshotHistoryCount < _snapshotHistory.Length)
            {
                _snapshotHistory[_snapshotHistoryCount++] = snapshot;
                return;
            }

            for (int i = 1; i < _snapshotHistory.Length; i++)
                _snapshotHistory[i - 1] = _snapshotHistory[i];

            _snapshotHistory[_snapshotHistory.Length - 1] = snapshot;
        }

        private bool TryGetSnapshotPair(
            float renderServerTime,
            out MotionSnapshot from,
            out MotionSnapshot to,
            out float interpolation,
            out float extrapolation)
        {
            from = default;
            to = default;
            interpolation = 0f;
            extrapolation = 0f;

            if (_snapshotHistoryCount <= 0)
                return false;

            MotionSnapshot first = _snapshotHistory[0];
            if (_snapshotHistoryCount == 1 || renderServerTime <= first.ServerTime)
            {
                from = to = first;
                return true;
            }

            for (int i = 1; i < _snapshotHistoryCount; i++)
            {
                MotionSnapshot next = _snapshotHistory[i];
                if (renderServerTime > next.ServerTime)
                    continue;

                from = _snapshotHistory[i - 1];
                to = next;
                float span = Mathf.Max(0.0001f, to.ServerTime - from.ServerTime);
                interpolation = Mathf.Clamp01((renderServerTime - from.ServerTime) / span);
                return true;
            }

            from = to = _snapshotHistory[_snapshotHistoryCount - 1];
            extrapolation = Mathf.Clamp(
                renderServerTime - to.ServerTime,
                0f,
                PongConfig.MaxBallSnapshotExtrapolation);
            return true;
        }

        private static float GetSnapshotPaddle(MotionSnapshot snapshot, int seat)
        {
            return seat switch
            {
                0 => snapshot.LeftPaddle,
                1 => snapshot.RightPaddle,
                2 => snapshot.TopPaddle,
                3 => snapshot.BottomPaddle,
                _ => 0f
            };
        }

        private static uint GetSnapshotAck(MotionSnapshot snapshot, int seat)
        {
            return seat switch
            {
                0 => snapshot.AckLeft,
                1 => snapshot.AckRight,
                2 => snapshot.AckTop,
                3 => snapshot.AckBottom,
                _ => 0u
            };
        }

        private static Vector2 GetSnapshotBallPosition(MotionSnapshot snapshot, int ball)
        {
            return ball switch
            {
                0 => snapshot.Ball0Position,
                1 => snapshot.Ball1Position,
                2 => snapshot.Ball2Position,
                3 => snapshot.Ball3Position,
                _ => Vector2.zero
            };
        }

        private static Vector2 GetSnapshotBallVelocity(MotionSnapshot snapshot, int ball)
        {
            return ball switch
            {
                0 => snapshot.Ball0Velocity,
                1 => snapshot.Ball1Velocity,
                2 => snapshot.Ball2Velocity,
                3 => snapshot.Ball3Velocity,
                _ => Vector2.zero
            };
        }

        private void UpdateMotionPresentation(float dt)
        {
            if (IsServer)
            {
                float renderLead = Mathf.Clamp(
                    _serverSimulationAccumulator,
                    0f,
                    1f / PongConfig.ServerSimulationRate);
                float paddleSpeed = GetCurrentPaddleSpeed();

                for (int seat = 0; seat < 4; seat++)
                {
                    float halfLength = GetEffectivePaddleLength(seat) * 0.5f;
                    float axisHalf = seat >= 2 ? PongConfig.HalfWidth : PongConfig.HalfHeight;
                    float limit = Mathf.Max(0.1f, axisHalf - halfLength - 0.25f);
                    float input = SeatParticipates(seat) ? _serverInput[seat] : 0f;

                    _renderPaddle[seat] = Mathf.Clamp(
                        _simPaddle[seat] + input * paddleSpeed * renderLead,
                        -limit,
                        limit);
                }

                for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
                {
                    _renderBallPosition[ball] = GetBallActive(ball)
                        ? _simBallPosition[ball] + _simBallVelocity[ball] * renderLead
                        : _simBallPosition[ball];
                }

                _clientMotionInitialized = true;
                return;
            }

            if (!_hasMotionSnapshot || _snapshotHistoryCount <= 0)
                return;

            // Adjust the interpolation buffer slowly so a temporary burst of jitter does not
            // itself create a visible time jump.
            _currentInterpolationDelay = Mathf.MoveTowards(
                _currentInterpolationDelay,
                _targetInterpolationDelay,
                PongConfig.SnapshotDelayAdjustSpeed * dt);

            float synchronizedServerTime = NetworkManager != null
                ? NetworkManager.ServerTime.TimeAsFloat
                : _lastSnapshotServerTime + (Time.unscaledTime - _lastMotionSnapshotReceiveTime);

            float renderServerTime = synchronizedServerTime - _currentInterpolationDelay;

            if (!TryGetSnapshotPair(
                    renderServerTime,
                    out MotionSnapshot from,
                    out MotionSnapshot to,
                    out float interpolation,
                    out float extrapolation))
                return;

            int localSeat = NetworkManager != null ? FindSeat(NetworkManager.LocalClientId) : -1;
            MotionSnapshot newest = _snapshotHistory[_snapshotHistoryCount - 1];

            for (int seat = 0; seat < 4; seat++)
            {
                if (seat == localSeat)
                {
                    // Local movement is intentionally presentation-authoritative. The server is
                    // continually validated toward this same predicted position, so normal latency
                    // never produces a visible pull-back. Only a severe divergence can recover it.
                    float authoritative = GetSnapshotPaddle(newest, seat);
                    uint acknowledgedInput = GetSnapshotAck(newest, seat);
                    bool serverCaughtUp = _inputSequence == 0 ||
                        (int)(acknowledgedInput - _inputSequence) >= 0;

                    if (serverCaughtUp &&
                        Mathf.Abs(authoritative - _renderPaddle[seat]) >=
                        PongConfig.LocalEmergencyCorrectionDistance)
                    {
                        _renderPaddle[seat] = authoritative;
                    }

                    continue;
                }

                float a = GetSnapshotPaddle(from, seat);
                float b = GetSnapshotPaddle(to, seat);
                float target = from.Sequence == to.Sequence
                    ? a
                    : Mathf.Lerp(a, b, interpolation);

                float halfLength = GetEffectivePaddleLength(seat) * 0.5f;
                float axisHalf = seat >= 2 ? PongConfig.HalfWidth : PongConfig.HalfHeight;
                float limit = Mathf.Max(0.1f, axisHalf - halfLength - 0.25f);
                _renderPaddle[seat] = Mathf.Clamp(target, -limit, limit);
            }

            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                Vector2 a = GetSnapshotBallPosition(from, ball);
                Vector2 b = GetSnapshotBallPosition(to, ball);

                if (from.Sequence != to.Sequence)
                {
                    _renderBallPosition[ball] = Vector2.Lerp(a, b, interpolation);
                }
                else
                {
                    Vector2 velocity = GetSnapshotBallVelocity(to, ball);
                    _renderBallPosition[ball] = b + velocity * extrapolation;
                }
            }
        }

        private void PredictLocalPaddle(int seat, float input, float dt)
        {
            if (IsServer || seat < 0 || seat > 3)
                return;

            float currentBallSpeed = PongConfig.BallStartSpeed;
            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                if (GetBallActive(ball))
                    currentBallSpeed = Mathf.Max(currentBallSpeed, GetNetworkBallVelocity(ball).magnitude);
            }

            float ratio = currentBallSpeed / PongConfig.BallStartSpeed;
            float speed = PongConfig.PaddleSpeed * Mathf.Lerp(1f, ratio, PongConfig.PaddleSpeedScaling);
            float halfLength = GetEffectivePaddleLength(seat) * 0.5f;
            float axisHalf = seat >= 2 ? PongConfig.HalfWidth : PongConfig.HalfHeight;
            float limit = Mathf.Max(0.1f, axisHalf - halfLength - 0.25f);
            _renderPaddle[seat] = Mathf.Clamp(_renderPaddle[seat] + input * speed * dt, -limit, limit);
        }

        private void EnsureVisuals()
        {
            if (_ballVisuals[0] != null)
                return;

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            if (shader == null) shader = Shader.Find("Sprites/Default");

            _whiteMaterial = new Material(shader) { color = new Color(0.92f, 0.95f, 1f) };
            _dimMaterial = new Material(shader) { color = new Color(0.18f, 0.22f, 0.30f) };
            _brickMaterial = new Material(shader) { color = new Color(0.86f, 0.52f, 0.22f) };

            for (int i = 0; i < 4; i++)
                _teamMaterials[i] = new Material(shader) { color = TeamColor(i) };

            _powerupMaterials[1] = new Material(shader) { color = new Color(0.62f, 0.42f, 1.00f) };
            _powerupMaterials[2] = new Material(shader) { color = new Color(0.35f, 1.00f, 0.55f) };
            _powerupMaterials[3] = new Material(shader) { color = new Color(1.00f, 0.32f, 0.32f) };

            for (int i = 0; i < PongConfig.MaxBalls; i++)
            {
                _ballVisuals[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _ballVisuals[i].name = "Ball Visual " + (i + 1);
                _ballVisuals[i].transform.SetParent(transform, true);
                Destroy(_ballVisuals[i].GetComponent<Collider>());
                _ballVisuals[i].transform.localScale = Vector3.one * PongConfig.BallRadius * 2f;
                _ballRenderers[i] = _ballVisuals[i].GetComponent<Renderer>();
                _ballRenderers[i].sharedMaterial = _whiteMaterial;
            }

            for (int i = 0; i < 4; i++)
            {
                _paddleVisuals[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _paddleVisuals[i].name = ((PaddleSeat)i) + " Paddle Visual";
                _paddleVisuals[i].transform.SetParent(transform, true);
                Destroy(_paddleVisuals[i].GetComponent<Collider>());
                _paddleRenderers[i] = _paddleVisuals[i].GetComponent<Renderer>();
                _paddleRenderers[i].sharedMaterial = _teamMaterials[i];
                _lastPaddleTeam[i] = i;
            }

            for (int i = 0; i < 4; i++)
            {
                _wallVisuals[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _wallVisuals[i].name = "Inactive Boundary " + i;
                _wallVisuals[i].transform.SetParent(transform, true);
                Destroy(_wallVisuals[i].GetComponent<Collider>());
                _wallVisuals[i].GetComponent<Renderer>().sharedMaterial = _dimMaterial;
            }

            for (int i = 0; i < _brickVisuals.Length; i++)
            {
                _brickVisuals[i] = GameObject.CreatePrimitive(PrimitiveType.Cube);
                _brickVisuals[i].name = "Brick " + i;
                _brickVisuals[i].transform.SetParent(transform, true);
                Destroy(_brickVisuals[i].GetComponent<Collider>());
                _brickVisuals[i].GetComponent<Renderer>().sharedMaterial = _brickMaterial;
                Vector2 center = GetBrickCenter(i);
                _brickVisuals[i].transform.position = new Vector3(center.x, center.y, 0.2f);
                _brickVisuals[i].transform.localScale = new Vector3(PongConfig.BrickWidth, PongConfig.BrickHeight, PongConfig.BrickDepth);
            }

            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                _powerupVisuals[i] = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _powerupVisuals[i].name = "Powerup Pickup " + (i + 1);
                _powerupVisuals[i].transform.SetParent(transform, true);
                Destroy(_powerupVisuals[i].GetComponent<Collider>());
                _powerupRenderers[i] = _powerupVisuals[i].GetComponent<Renderer>();
                _powerupVisuals[i].SetActive(false);
            }

            UpdateWallVisuals();
        }

        private void UpdateLocalVisuals()
        {
            if (_ballVisuals[0] == null)
                return;

            bool showMatch = MatchRunning.Value || WinnerTeam.Value >= 0;
            for (int ball = 0; ball < PongConfig.MaxBalls; ball++)
            {
                Vector2 pos = _renderBallPosition[ball];
                _ballVisuals[ball].transform.position = new Vector3(pos.x, pos.y, 0f);
                bool shouldBeActive = GetBallActive(ball) && showMatch;
                if (_ballVisuals[ball].activeSelf != shouldBeActive)
                    _ballVisuals[ball].SetActive(shouldBeActive);

                int ownerSeat = GetBallOwnerSeat(ball);
                int ownerTeam = ownerSeat >= 0 ? GetTeamForSeat(ownerSeat) : -1;
                if (_lastBallTeam[ball] != ownerTeam && _ballRenderers[ball] != null)
                {
                    _lastBallTeam[ball] = ownerTeam;
                    _ballRenderers[ball].sharedMaterial = ownerTeam >= 0 && ownerTeam < 4
                        ? _teamMaterials[ownerTeam]
                        : _whiteMaterial;
                }
            }

            float x = PongConfig.HalfWidth - PongConfig.PaddleInset;
            float y = PongConfig.HalfHeight - PongConfig.PaddleInset;
            _paddleVisuals[0].transform.position = new Vector3(-x, _renderPaddle[0], 0f);
            _paddleVisuals[1].transform.position = new Vector3(x, _renderPaddle[1], 0f);
            _paddleVisuals[2].transform.position = new Vector3(_renderPaddle[2], y, 0f);
            _paddleVisuals[3].transform.position = new Vector3(_renderPaddle[3], -y, 0f);

            float leftLength = GetEffectivePaddleLength(0);
            float rightLength = GetEffectivePaddleLength(1);
            float topLength = GetEffectivePaddleLength(2);
            float bottomLength = GetEffectivePaddleLength(3);
            _paddleVisuals[0].transform.localScale = new Vector3(PongConfig.PaddleShort, leftLength, 0.35f);
            _paddleVisuals[1].transform.localScale = new Vector3(PongConfig.PaddleShort, rightLength, 0.35f);
            _paddleVisuals[2].transform.localScale = new Vector3(topLength, PongConfig.PaddleShort, 0.35f);
            _paddleVisuals[3].transform.localScale = new Vector3(bottomLength, PongConfig.PaddleShort, 0.35f);

            for (int i = 0; i < 4; i++)
            {
                bool active = SeatIsPresent(i);
                if (_paddleVisuals[i].activeSelf != active)
                    _paddleVisuals[i].SetActive(active);

                int team = GetTeamForSeat(i);
                if (_lastPaddleTeam[i] != team && _paddleRenderers[i] != null)
                {
                    _lastPaddleTeam[i] = team;
                    _paddleRenderers[i].sharedMaterial = team >= 0 && team < 4 ? _teamMaterials[team] : _whiteMaterial;
                }
            }

            UpdatePowerupVisualAnimation(showMatch);
            RefreshBrickVisuals(showMatch);
            UpdateWallVisuals();
        }

        private void SetPowerupVisual(int slot, int type, Vector2 position, bool active)
        {
            if (slot < 0 || slot >= PongConfig.MaxPowerupPickups)
                return;

            EnsureVisuals();
            _powerupVisualActive[slot] = active;

            GameObject visual = _powerupVisuals[slot];
            if (visual == null)
                return;

            if (!active)
            {
                if (visual.activeSelf)
                    visual.SetActive(false);
                return;
            }

            visual.transform.position = new Vector3(position.x, position.y, -0.16f);
            visual.transform.localScale = Vector3.one * (PongConfig.PowerupPickupRadius * 2f);
            if (_powerupRenderers[slot] != null)
                _powerupRenderers[slot].sharedMaterial = type >= 1 && type <= 3 ? _powerupMaterials[type] : _whiteMaterial;

            if (!visual.activeSelf)
                visual.SetActive(true);
        }

        private void UpdatePowerupVisualAnimation(bool showMatch)
        {
            for (int i = 0; i < PongConfig.MaxPowerupPickups; i++)
            {
                GameObject visual = _powerupVisuals[i];
                if (visual == null)
                    continue;

                bool active = showMatch && (PongGameMode)GameMode.Value == PongGameMode.BrickWar && _powerupVisualActive[i];
                if (visual.activeSelf != active)
                    visual.SetActive(active);

                if (!active)
                    continue;

                float pulse = 1f + Mathf.Sin(Time.time * 5f + i * 0.37f) * 0.12f;
                float diameter = PongConfig.PowerupPickupRadius * 2f * pulse;
                visual.transform.localScale = Vector3.one * diameter;
                visual.transform.Rotate(0f, 0f, 70f * Time.deltaTime, Space.Self);
            }
        }

        private void RefreshBrickVisuals(bool showMatch)
        {
            bool showBricks = showMatch && (PongGameMode)GameMode.Value == PongGameMode.BrickWar;
            ulong low = IsServer ? _simBrickMaskLow : (_hasMotionSnapshot ? _snapshotBrickMaskLow : BrickMaskLow.Value);
            uint high = IsServer ? _simBrickMaskHigh : (_hasMotionSnapshot ? _snapshotBrickMaskHigh : BrickMaskHigh.Value);
            int mode = GameMode.Value;

            if (_lastBrickVisualLow == low && _lastBrickVisualHigh == high &&
                _lastBrickVisualMode == mode && _lastBrickVisualShow == showBricks)
                return;

            _lastBrickVisualLow = low;
            _lastBrickVisualHigh = high;
            _lastBrickVisualMode = mode;
            _lastBrickVisualShow = showBricks;

            for (int i = 0; i < _brickVisuals.Length; i++)
            {
                bool active = showBricks && IsBrickAlive(i);
                if (_brickVisuals[i].activeSelf != active)
                    _brickVisuals[i].SetActive(active);
            }
        }

        private void UpdateWallVisuals()
        {
            if (_wallVisuals[0] == null)
                return;

            int wallMask = 0;
            for (int i = 0; i < 4; i++)
            {
                bool activeGoal = MatchRunning.Value && SeatParticipates(i);
                if (!activeGoal)
                    wallMask |= 1 << i;
            }

            if (_lastWallMask == wallMask)
                return;

            _lastWallMask = wallMask;
            float t = 0.12f;
            _wallVisuals[0].transform.position = new Vector3(-PongConfig.HalfWidth, 0f, 0.25f);
            _wallVisuals[1].transform.position = new Vector3(PongConfig.HalfWidth, 0f, 0.25f);
            _wallVisuals[2].transform.position = new Vector3(0f, PongConfig.HalfHeight, 0.25f);
            _wallVisuals[3].transform.position = new Vector3(0f, -PongConfig.HalfHeight, 0.25f);
            _wallVisuals[0].transform.localScale = new Vector3(t, PongConfig.HalfHeight * 2f, t);
            _wallVisuals[1].transform.localScale = new Vector3(t, PongConfig.HalfHeight * 2f, t);
            _wallVisuals[2].transform.localScale = new Vector3(PongConfig.HalfWidth * 2f, t, t);
            _wallVisuals[3].transform.localScale = new Vector3(PongConfig.HalfWidth * 2f, t, t);

            for (int i = 0; i < 4; i++)
            {
                bool active = (wallMask & (1 << i)) != 0;
                if (_wallVisuals[i].activeSelf != active)
                    _wallVisuals[i].SetActive(active);
            }
        }

        private void HideAllVisuals()
        {
            for (int i = 0; i < _ballVisuals.Length; i++)
                if (_ballVisuals[i] != null)
                    _ballVisuals[i].SetActive(false);

            for (int i = 0; i < _paddleVisuals.Length; i++)
                if (_paddleVisuals[i] != null)
                    _paddleVisuals[i].SetActive(false);

            for (int i = 0; i < _wallVisuals.Length; i++)
                if (_wallVisuals[i] != null)
                    _wallVisuals[i].SetActive(false);

            for (int i = 0; i < _brickVisuals.Length; i++)
                if (_brickVisuals[i] != null)
                    _brickVisuals[i].SetActive(false);

            for (int i = 0; i < _powerupVisuals.Length; i++)
                if (_powerupVisuals[i] != null)
                    _powerupVisuals[i].SetActive(false);
        }

        public static string TeamName(int team)
        {
            return team switch
            {
                0 => "BLUE",
                1 => "RED",
                2 => "GREEN",
                3 => "ORANGE",
                _ => "WHITE"
            };
        }

        public static Color TeamColor(int team)
        {
            return team switch
            {
                0 => new Color(0.20f, 0.70f, 1.00f),
                1 => new Color(1.00f, 0.36f, 0.48f),
                2 => new Color(0.53f, 0.92f, 0.42f),
                3 => new Color(1.00f, 0.74f, 0.25f),
                _ => Color.white
            };
        }
    }
}
