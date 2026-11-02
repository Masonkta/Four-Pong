using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FourWayPong
{
    /// <summary>
    /// Authoritative Battle 99 match coordinator. It does not simulate every Breakout
    /// ball; clients simulate their own board. The server owns queue membership/ready,
    /// roster lock, attack routing, elimination placement, and preview fan-out.
    /// </summary>
    public sealed class Battle99DedicatedServer : MonoBehaviour
    {
        private sealed class PlayerState
        {
            public ulong ClientId;
            public ProductUserId UserId;
            public string Name;
            public byte Slot;
            public bool Ready;
            public bool Alive = true;
            public bool HasSnapshot;
            public Battle99NetSnapshot Snapshot;
        }

        private const float PreviewBroadcastInterval = 0.25f;
        private const int PreviewBatchSize = 14;

        private readonly Dictionary<ulong, PlayerState> _playersByClient = new Dictionary<ulong, PlayerState>();
        private readonly Dictionary<string, PlayerState> _playersByProduct = new Dictionary<string, PlayerState>();
        private readonly List<PlayerState> _orderedScratch = new List<PlayerState>();
        private readonly List<PlayerState> _targetScratch = new List<PlayerState>();

        private NetworkManager _manager;
        private EOSP2PTransport _transport;
        private Battle99SessionService _sessionService;
        private bool _matchStarted;
        private bool _queueLocked;
        private bool _advertised;
        private bool _winnerDeclared;
        private int _matchPlayerCount;
        private byte _nextSlot;
        private float _previewTimer;

        public string Status { get; private set; } = "Starting dedicated server...";

        public void Initialize(Battle99SessionService sessionService)
        {
            _sessionService = sessionService;
            StartNetworkServer();
        }

        private void StartNetworkServer()
        {
            _manager = NetworkManager.Singleton;
            if (_manager == null)
            {
                Status = "NetworkManager is missing.";
                Debug.LogError("Battle99DedicatedServer: NetworkManager is missing from the scene.");
                return;
            }

            _transport = _manager.GetComponent<EOSP2PTransport>();
            if (_transport == null)
            {
                Status = "EOSP2PTransport is missing.";
                Debug.LogError("Battle99DedicatedServer: EOSP2PTransport is missing from NetworkManager.");
                return;
            }

            if (_manager.IsListening)
                _manager.Shutdown();

            _manager.NetworkConfig.TickRate = PongConfig.NetcodeTickRate;
            _manager.NetworkConfig.EnableTimeResync = true;
            _manager.NetworkConfig.ConnectionApproval = true;
            _manager.ConnectionApprovalCallback = ApprovalCheck;
            _transport.ServerUserIdToConnectTo = null;
            _manager.NetworkConfig.NetworkTransport = _transport;

            _manager.OnClientDisconnectCallback -= OnClientDisconnected;
            _manager.OnClientDisconnectCallback += OnClientDisconnected;

            // Netcode creates CustomMessagingManager as part of StartServer().
            // Registering named handlers before StartServer leaves it null and crashes
            // dedicated-server startup in headless builds.
            if (!_manager.StartServer())
            {
                Status = "NGO server failed to start.";
                Debug.LogError("Battle99DedicatedServer: NetworkManager.StartServer failed.");
                return;
            }

            if (!RegisterHandlers())
            {
                Status = "NGO messaging failed to initialize.";
                Debug.LogError("Battle99DedicatedServer: CustomMessagingManager was unavailable after StartServer.");
                _manager.Shutdown();
                return;
            }

            Status = "Advertising Battle 99 server...";
            _sessionService.AdvertiseDedicatedServer(PongConfig.Battle99MaxPlayers, OnAdvertised);
        }

        private void ApprovalCheck(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
        {
            int connected = _manager != null ? _manager.ConnectedClientsIds.Count : _playersByClient.Count;
            bool full = connected >= PongConfig.Battle99MaxPlayers;
            response.Approved = !_queueLocked && !full;
            response.CreatePlayerObject = false;
            response.PlayerPrefabHash = null;
            response.Position = null;
            response.Rotation = null;
            response.Pending = false;
            response.Reason = _queueLocked ? "This Battle 99 match already started." :
                full ? "Battle 99 server is full." : string.Empty;
        }

        private void OnAdvertised(bool success, string error)
        {
            _advertised = success;
            Status = success ? "Battle 99 dedicated server available." : error;
            if (success)
            {
                ProductUserId serverUser = PlayEveryWare.EpicOnlineServices.EOSManager.Instance.GetProductUserId();
                Debug.Log("Battle99 dedicated server ready. Session=" + _sessionService.ServerSessionId +
                    " PUID=" + (serverUser != null ? serverUser.ToString() : "none"));
            }
            else
            {
                Debug.LogError("Battle99DedicatedServer: " + error);
            }
        }

        private bool RegisterHandlers()
        {
            if (_manager == null || _manager.CustomMessagingManager == null)
                return false;

            CustomMessagingManager messaging = _manager.CustomMessagingManager;
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Hello, OnHello);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.ReadyState, OnReadyState);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Snapshot, OnSnapshot);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.AttackRequest, OnAttackRequest);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Ping, OnPing);
            return true;
        }

        private void UnregisterHandlers()
        {
            if (_manager == null || _manager.CustomMessagingManager == null)
                return;

            CustomMessagingManager messaging = _manager.CustomMessagingManager;
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Hello);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.ReadyState);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Snapshot);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.AttackRequest);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Ping);
        }

        private void OnHello(ulong senderClientId, FastBufferReader reader)
        {
            string requestedSession = Battle99DedicatedProtocol.ReadString(ref reader);
            string playerName = Battle99DedicatedProtocol.ReadString(ref reader);

            if (_queueLocked)
            {
                Disconnect(senderClientId, "This Battle 99 match already started.");
                return;
            }

            if (!string.IsNullOrEmpty(_sessionService.ServerSessionId) &&
                !string.Equals(requestedSession, _sessionService.ServerSessionId, StringComparison.Ordinal))
            {
                Disconnect(senderClientId, "Connected to the wrong Battle 99 server.");
                return;
            }

            if (!_transport.TryGetProductUserId(senderClientId, out ProductUserId productUserId) ||
                productUserId == null || !productUserId.IsValid())
            {
                Disconnect(senderClientId, "Could not identify EOS user.");
                return;
            }

            string id = productUserId.ToString();
            if (!_playersByProduct.TryGetValue(id, out PlayerState state))
            {
                if (_playersByClient.Count >= PongConfig.Battle99MaxPlayers)
                {
                    Disconnect(senderClientId, "Battle 99 server is full.");
                    return;
                }

                state = new PlayerState
                {
                    ClientId = senderClientId,
                    UserId = productUserId,
                    Name = SanitizeName(playerName),
                    Slot = AllocateSlot(),
                    Ready = false,
                    Alive = true
                };
                _playersByProduct[id] = state;
                _sessionService.RegisterServerPlayer(productUserId);
            }
            else
            {
                if (_matchStarted)
                {
                    Disconnect(senderClientId, "Reconnect is not available after the match starts.");
                    return;
                }
                _playersByClient.Remove(state.ClientId);
                state.ClientId = senderClientId;
                state.Name = SanitizeName(playerName);
            }

            _playersByClient[senderClientId] = state;
            Status = "Battle 99 queue " + _playersByClient.Count + " / " + PongConfig.Battle99MaxPlayers;
            SendFullQueueState(senderClientId);
            BroadcastMemberUpsert(state, senderClientId);
        }

        private static string SanitizeName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "Player";
            value = value.Trim();
            return value.Length > 20 ? value.Substring(0, 20) : value;
        }

        private byte AllocateSlot()
        {
            for (int attempt = 0; attempt < 255; attempt++)
            {
                byte slot = _nextSlot++;
                bool used = false;
                foreach (PlayerState state in _playersByProduct.Values)
                {
                    if (state.Slot == slot)
                    {
                        used = true;
                        break;
                    }
                }
                if (!used)
                    return slot;
            }
            return 0;
        }

        private void OnReadyState(ulong senderClientId, FastBufferReader reader)
        {
            if (_queueLocked || !_playersByClient.TryGetValue(senderClientId, out PlayerState state))
                return;

            reader.ReadValueSafe(out bool ready);
            state.Ready = ready;
            BroadcastReadyState(state);

            if (AllCurrentPlayersReady())
                StartMatch();
        }

        private bool AllCurrentPlayersReady()
        {
            if (_playersByClient.Count < PongConfig.Battle99MinPlayers)
                return false;

            foreach (PlayerState state in _playersByClient.Values)
                if (!state.Ready) return false;
            return true;
        }

        private void StartMatch()
        {
            if (_matchStarted || !AllCurrentPlayersReady())
                return;

            _queueLocked = true;
            _matchStarted = true;
            _winnerDeclared = false;
            _matchPlayerCount = _playersByClient.Count;
            _sessionService.SetDedicatedServerState(true);

            foreach (PlayerState state in _playersByClient.Values)
            {
                state.Alive = true;
                using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
                writer.WriteValueSafe((ushort)_matchPlayerCount);
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.Start,
                    state.ClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }

            Status = "Battle 99 match running with " + _matchPlayerCount + " players.";
        }

        private void SendFullQueueState(ulong clientId)
        {
            BuildOrderedPlayers();
            FastBufferWriter writer = new FastBufferWriter(32768, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((ushort)_orderedScratch.Count);
                for (int i = 0; i < _orderedScratch.Count; i++)
                {
                    PlayerState state = _orderedScratch[i];
                    WriteMember(ref writer, state);
                }

                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.QueueState,
                    clientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void BroadcastMemberUpsert(PlayerState state, ulong exceptClientId)
        {
            FastBufferWriter writer = new FastBufferWriter(384, Allocator.Temp);
            try
            {
                WriteMember(ref writer, state);
                foreach (PlayerState target in _playersByClient.Values)
                {
                    if (target.ClientId == exceptClientId)
                        continue;
                    _manager.CustomMessagingManager.SendNamedMessage(
                        Battle99DedicatedProtocol.QueueMember,
                        target.ClientId,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void BroadcastReadyState(PlayerState state)
        {
            using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe(state.Slot);
            writer.WriteValueSafe(state.Ready);
            foreach (PlayerState target in _playersByClient.Values)
            {
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.ReadyState,
                    target.ClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
        }

        private void BroadcastMemberRemoved(byte slot)
        {
            using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe(slot);
            foreach (PlayerState target in _playersByClient.Values)
            {
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.QueueRemove,
                    target.ClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
        }

        private static void WriteMember(ref FastBufferWriter writer, PlayerState state)
        {
            writer.WriteValueSafe(state.Slot);
            Battle99DedicatedProtocol.WriteString(ref writer, state.UserId != null ? state.UserId.ToString() : string.Empty);
            Battle99DedicatedProtocol.WriteString(ref writer, state.Name);
            writer.WriteValueSafe(state.Ready);
        }

        private void BuildOrderedPlayers()
        {
            _orderedScratch.Clear();
            foreach (PlayerState state in _playersByClient.Values)
                _orderedScratch.Add(state);
            _orderedScratch.Sort((a, b) => a.Slot.CompareTo(b.Slot));
        }

        private void Update()
        {
            if (!_advertised || !_matchStarted)
                return;

            _previewTimer += Time.unscaledDeltaTime;
            if (_previewTimer < PreviewBroadcastInterval)
                return;
            _previewTimer = 0f;
            BroadcastPreviewBatches();
        }

        private void OnSnapshot(ulong senderClientId, FastBufferReader reader)
        {
            if (!_matchStarted || !_playersByClient.TryGetValue(senderClientId, out PlayerState state))
                return;

            Battle99DedicatedProtocol.ReadSnapshot(ref reader, out Battle99NetSnapshot snapshot);
            if (state.HasSnapshot && (int)(snapshot.Sequence - state.Snapshot.Sequence) <= 0)
                return;

            bool wasAlive = state.Alive;
            state.Snapshot = snapshot;
            state.HasSnapshot = true;
            state.Alive = snapshot.Alive;

            if (wasAlive && !state.Alive)
                HandleElimination(state);
        }

        private void BroadcastPreviewBatches()
        {
            BuildOrderedPlayers();
            int sourceCount = _orderedScratch.Count;

            for (int start = 0; start < sourceCount; start += PreviewBatchSize)
            {
                int count = Mathf.Min(PreviewBatchSize, sourceCount - start);
                FastBufferWriter writer = new FastBufferWriter(2048, Allocator.Temp);
                try
                {
                    writer.WriteValueSafe((byte)count);

                    for (int i = 0; i < count; i++)
                    {
                        PlayerState source = _orderedScratch[start + i];
                        writer.WriteValueSafe(source.Slot);
                        Battle99NetSnapshot snapshot = source.HasSnapshot ? source.Snapshot : default;
                        snapshot.Alive = source.Alive;
                        Battle99DedicatedProtocol.WriteSnapshot(ref writer, snapshot);
                    }

                    foreach (PlayerState target in _playersByClient.Values)
                    {
                        _manager.CustomMessagingManager.SendNamedMessage(
                            Battle99DedicatedProtocol.Snapshot,
                            target.ClientId,
                            writer,
                            NetworkDelivery.Unreliable);
                    }
                }
                finally
                {
                    writer.Dispose();
                }
            }
        }

        private void BroadcastSingleSnapshot(PlayerState source)
        {
            FastBufferWriter writer = new FastBufferWriter(256, Allocator.Temp);
            try
            {
                writer.WriteValueSafe((byte)1);
                writer.WriteValueSafe(source.Slot);
                Battle99NetSnapshot snapshot = source.HasSnapshot ? source.Snapshot : default;
                snapshot.Alive = source.Alive;
                Battle99DedicatedProtocol.WriteSnapshot(ref writer, snapshot);

                foreach (PlayerState target in _playersByClient.Values)
                {
                    _manager.CustomMessagingManager.SendNamedMessage(
                        Battle99DedicatedProtocol.Snapshot,
                        target.ClientId,
                        writer,
                        NetworkDelivery.Reliable);
                }
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void OnAttackRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!_matchStarted || !_playersByClient.TryGetValue(senderClientId, out PlayerState attacker) || !attacker.Alive)
                return;

            reader.ReadValueSafe(out byte requestedRows);
            int rows = Mathf.Clamp((int)requestedRows, 1, 2);

            _targetScratch.Clear();
            foreach (PlayerState state in _playersByClient.Values)
            {
                if (state.ClientId != senderClientId && state.Alive)
                    _targetScratch.Add(state);
            }
            if (_targetScratch.Count == 0)
                return;

            PlayerState target = _targetScratch[UnityEngine.Random.Range(0, _targetScratch.Count)];
            FastBufferWriter writer = new FastBufferWriter(160, Allocator.Temp);
            try
            {
                Battle99DedicatedProtocol.WriteString(ref writer, attacker.UserId != null ? attacker.UserId.ToString() : string.Empty);
                writer.WriteValueSafe((byte)rows);
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.Attack,
                    target.ClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void HandleElimination(PlayerState eliminated)
        {
            int alive = CountAlivePlayers();
            int placement = Mathf.Clamp(alive + 1, 2, Mathf.Max(2, _matchPlayerCount));
            SendPlacement(eliminated.ClientId, placement);

            if (alive == 1 && !_winnerDeclared)
            {
                _winnerDeclared = true;
                foreach (PlayerState state in _playersByClient.Values)
                {
                    if (state.Alive)
                    {
                        SendPlacement(state.ClientId, 1);
                        break;
                    }
                }
            }
        }

        private int CountAlivePlayers()
        {
            int count = 0;
            foreach (PlayerState state in _playersByClient.Values)
                if (state.Alive) count++;
            return count;
        }

        private void SendPlacement(ulong clientId, int placement)
        {
            using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe((ushort)Mathf.Clamp(placement, 1, ushort.MaxValue));
            _manager.CustomMessagingManager.SendNamedMessage(
                Battle99DedicatedProtocol.Placement,
                clientId,
                writer,
                NetworkDelivery.Reliable);
        }

        private void OnPing(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out uint sequence);
            using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe(sequence);
            _manager.CustomMessagingManager.SendNamedMessage(
                Battle99DedicatedProtocol.Pong,
                senderClientId,
                writer,
                NetworkDelivery.Unreliable);
        }

        private void SendServerStatus(ulong clientId, string text)
        {
            FastBufferWriter writer = new FastBufferWriter(256, Allocator.Temp);
            try
            {
                Battle99DedicatedProtocol.WriteString(ref writer, text);
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.ServerStatus,
                    clientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        private void Disconnect(ulong clientId, string reason)
        {
            SendServerStatus(clientId, reason);
            _manager.DisconnectClient(clientId);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (!_playersByClient.TryGetValue(clientId, out PlayerState state))
                return;

            _playersByClient.Remove(clientId);
            if (state.UserId != null)
            {
                _playersByProduct.Remove(state.UserId.ToString());
                _sessionService.UnregisterServerPlayer(state.UserId);
            }

            if (_matchStarted && state.Alive)
            {
                state.Alive = false;
                state.Snapshot.Alive = false;
                state.HasSnapshot = true;
                BroadcastSingleSnapshot(state);
                HandleElimination(state);
            }

            if (!_matchStarted)
            {
                BroadcastMemberRemoved(state.Slot);
                Status = _playersByClient.Count == 0
                    ? "Battle 99 dedicated server available."
                    : "Battle 99 queue " + _playersByClient.Count + " / " + PongConfig.Battle99MaxPlayers;
            }

            if (_playersByClient.Count == 0)
                ResetForNextMatch();
        }

        private void ResetForNextMatch()
        {
            _playersByClient.Clear();
            _playersByProduct.Clear();
            _matchStarted = false;
            _queueLocked = false;
            _winnerDeclared = false;
            _matchPlayerCount = 0;
            _nextSlot = 0;
            _previewTimer = 0f;
            _sessionService.SetDedicatedServerState(false);
            Status = "Battle 99 dedicated server available.";
        }

        private void OnDestroy()
        {
            if (_manager != null)
            {
                _manager.OnClientDisconnectCallback -= OnClientDisconnected;
                UnregisterHandlers();
            }
            _sessionService?.DestroyDedicatedServerSession();
        }
    }
}
