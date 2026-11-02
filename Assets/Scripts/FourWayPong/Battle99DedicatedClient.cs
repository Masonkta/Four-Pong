using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FourWayPong
{
    /// <summary>
    /// Battle 99 client link to a dedicated Unity server.
    /// The local Breakout board remains client-simulated for responsiveness while
    /// queue state, roster lock, attacks, placements, and opponent previews pass through the server.
    /// </summary>
    public sealed class Battle99DedicatedClient : MonoBehaviour, IBattle99NetworkLink
    {
        private readonly Dictionary<string, Battle99RemoteState> _remoteStates = new Dictionary<string, Battle99RemoteState>();
        private readonly Dictionary<string, ProductUserId> _rosterUsers = new Dictionary<string, ProductUserId>();
        private readonly List<ProductUserId> _aliveScratch = new List<ProductUserId>();
        private readonly List<Battle99Member> _queueMembers = new List<Battle99Member>();
        private readonly Dictionary<byte, ProductUserId> _slotUsers = new Dictionary<byte, ProductUserId>();

        private NetworkManager _manager;
        private ProductUserId _localUser;
        private ProductUserId _serverUser;
        private string _serverSessionId;
        private string _playerName;
        private bool _handlersRegistered;
        private bool _connectRequested;
        private bool _connected;
        private bool _matchReady;
        private Action<bool, string> _connectCallback;
        private uint _pingSequence;
        private uint _awaitingPing;
        private float _pingSentAt;
        private float _pingTimer;

        public event Action<ProductUserId, int> AttackReceived;
        public event Action<int> LocalPlacementReceived;
        public event Action<IReadOnlyList<Battle99Member>> QueueUpdated;

        public float PingMs { get; private set; } = -1f;
        public bool Ready => _matchReady;
        public bool Connected => _connected;
        public bool MatchReady => _matchReady;
        public string Status { get; private set; } = string.Empty;
        public IReadOnlyList<Battle99Member> QueueMembers => _queueMembers;

        public void ConnectToServer(
            ProductUserId serverUser,
            string serverSessionId,
            string playerName,
            IReadOnlyList<Battle99Member> roster,
            Action<bool, string> callback)
        {
            if (_connectRequested || _connected)
            {
                callback?.Invoke(_connected, _connected ? string.Empty : "Dedicated server connection is already in progress.");
                return;
            }

            _manager = NetworkManager.Singleton;
            _localUser = GetLocalUser();
            if (_manager == null || _localUser == null || !_localUser.IsValid() || serverUser == null || !serverUser.IsValid())
            {
                callback?.Invoke(false, "Dedicated server connection cannot start yet.");
                return;
            }

            if (_manager.IsListening)
            {
                callback?.Invoke(false, "Another network session is still active.");
                return;
            }

            EOSP2PTransport transport = _manager.GetComponent<EOSP2PTransport>();
            if (transport == null)
            {
                callback?.Invoke(false, "EOS P2P transport is missing from NetworkManager.");
                return;
            }

            _serverUser = serverUser;
            _serverSessionId = serverSessionId ?? string.Empty;
            _playerName = string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName;
            _connectCallback = callback;
            _connectRequested = true;
            _matchReady = false;
            _connected = false;
            _queueMembers.Clear();
            _remoteStates.Clear();
            _rosterUsers.Clear();
            _slotUsers.Clear();
            PingMs = -1f;
            Status = "Connecting to dedicated server...";

            _manager.NetworkConfig.TickRate = PongConfig.NetcodeTickRate;
            _manager.NetworkConfig.EnableTimeResync = true;
            _manager.NetworkConfig.ConnectionApproval = false;
            _manager.ConnectionApprovalCallback = null;
            transport.ServerUserIdToConnectTo = serverUser;
            _manager.NetworkConfig.NetworkTransport = transport;

            _manager.OnClientConnectedCallback -= OnClientConnected;
            _manager.OnClientDisconnectCallback -= OnClientDisconnected;
            _manager.OnClientConnectedCallback += OnClientConnected;
            _manager.OnClientDisconnectCallback += OnClientDisconnected;

            // Netcode creates CustomMessagingManager during StartClient().
            // Register the named messages only after startup succeeds.
            if (!_manager.StartClient())
            {
                FailConnect("Netcode could not start the dedicated-server client.");
                return;
            }

            if (!RegisterHandlers())
            {
                _manager.Shutdown();
                FailConnect("Netcode messaging did not initialize for the dedicated-server client.");
            }
        }

        private ProductUserId GetLocalUser()
        {
            if (PlayEveryWare.EpicOnlineServices.EOSManager.Instance == null)
                return null;
            return PlayEveryWare.EpicOnlineServices.EOSManager.Instance.GetProductUserId();
        }

        private void OnClientConnected(ulong clientId)
        {
            if (_manager == null || clientId != _manager.LocalClientId || !_connectRequested)
                return;

            _connected = true;
            Status = "Connected. Waiting for players.";
            SendHello();

            Action<bool, string> callback = _connectCallback;
            _connectCallback = null;
            callback?.Invoke(true, string.Empty);
        }

        private void OnClientDisconnected(ulong clientId)
        {
            if (_manager == null || clientId != _manager.LocalClientId)
                return;

            bool wasConnecting = _connectRequested && !_connected;
            _connected = false;
            _matchReady = false;
            _connectRequested = false;
            Status = "Dedicated server disconnected.";

            if (wasConnecting)
            {
                Action<bool, string> callback = _connectCallback;
                _connectCallback = null;
                callback?.Invoke(false, Status);
            }
        }

        private void SendHello()
        {
            if (!_connected || _manager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(384, Allocator.Temp);
            try
            {
                Battle99DedicatedProtocol.WriteString(ref writer, _serverSessionId);
                Battle99DedicatedProtocol.WriteString(ref writer, _playerName);
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.Hello,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.Reliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        public void SetReady(bool ready)
        {
            if (!_connected || _manager == null || _matchReady)
                return;

            using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe(ready);
            _manager.CustomMessagingManager.SendNamedMessage(
                Battle99DedicatedProtocol.ReadyState,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }

        private void Update()
        {
            if (!_connected || _manager == null || !_manager.IsConnectedClient)
                return;

            _pingTimer += Time.unscaledDeltaTime;
            if (_pingTimer < 1f)
                return;

            _pingTimer = 0f;
            _awaitingPing = ++_pingSequence;
            _pingSentAt = Time.realtimeSinceStartup;
            using FastBufferWriter writer = new FastBufferWriter(16, Allocator.Temp);
            writer.WriteValueSafe(_awaitingPing);
            _manager.CustomMessagingManager.SendNamedMessage(
                Battle99DedicatedProtocol.Ping,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Unreliable);
        }

        private bool RegisterHandlers()
        {
            if (_handlersRegistered)
                return true;
            if (_manager == null || _manager.CustomMessagingManager == null)
                return false;

            CustomMessagingManager messaging = _manager.CustomMessagingManager;
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.QueueState, OnQueueStateMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.QueueMember, OnQueueMemberMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.QueueRemove, OnQueueRemoveMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.ReadyState, OnReadyBroadcastMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Start, OnStartMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Snapshot, OnSnapshotMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Attack, OnAttackMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Placement, OnPlacementMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.Pong, OnPongMessage);
            messaging.RegisterNamedMessageHandler(Battle99DedicatedProtocol.ServerStatus, OnServerStatusMessage);
            _handlersRegistered = true;
            return true;
        }

        private void UnregisterHandlers()
        {
            if (!_handlersRegistered || _manager == null)
                return;

            CustomMessagingManager messaging = _manager.CustomMessagingManager;
            if (messaging == null)
            {
                _handlersRegistered = false;
                return;
            }
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.QueueState);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.QueueMember);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.QueueRemove);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.ReadyState);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Start);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Snapshot);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Attack);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Placement);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.Pong);
            messaging.UnregisterNamedMessageHandler(Battle99DedicatedProtocol.ServerStatus);
            _handlersRegistered = false;
        }

        private void OnQueueStateMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort count);
            _queueMembers.Clear();
            _rosterUsers.Clear();
            _slotUsers.Clear();

            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out byte slot);
                string idText = Battle99DedicatedProtocol.ReadString(ref reader);
                string name = Battle99DedicatedProtocol.ReadString(ref reader);
                reader.ReadValueSafe(out bool ready);
                UpsertQueueMember(slot, idText, name, ready, false);
            }

            QueueUpdated?.Invoke(_queueMembers);
        }

        private void OnQueueMemberMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte slot);
            string idText = Battle99DedicatedProtocol.ReadString(ref reader);
            string name = Battle99DedicatedProtocol.ReadString(ref reader);
            reader.ReadValueSafe(out bool ready);
            UpsertQueueMember(slot, idText, name, ready, true);
        }

        private void OnReadyBroadcastMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte slot);
            reader.ReadValueSafe(out bool ready);
            if (!_slotUsers.TryGetValue(slot, out ProductUserId user) || user == null || !user.IsValid())
                return;

            string id = user.ToString();
            for (int i = 0; i < _queueMembers.Count; i++)
            {
                if (_queueMembers[i].UserId != null && _queueMembers[i].UserId.ToString() == id)
                {
                    _queueMembers[i].Ready = ready;
                    QueueUpdated?.Invoke(_queueMembers);
                    return;
                }
            }
        }

        private void OnQueueRemoveMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte slot);
            if (!_slotUsers.TryGetValue(slot, out ProductUserId user) || user == null)
                return;

            string id = user.ToString();
            _slotUsers.Remove(slot);
            _rosterUsers.Remove(id);
            _remoteStates.Remove(id);
            for (int i = _queueMembers.Count - 1; i >= 0; i--)
                if (_queueMembers[i].UserId != null && _queueMembers[i].UserId.ToString() == id)
                    _queueMembers.RemoveAt(i);

            QueueUpdated?.Invoke(_queueMembers);
        }

        private void UpsertQueueMember(byte slot, string idText, string name, bool ready, bool notify)
        {
            ProductUserId user = ProductUserId.FromString(idText);
            if (user == null || !user.IsValid())
                return;

            _slotUsers[slot] = user;
            string localId = _localUser != null && _localUser.IsValid() ? _localUser.ToString() : string.Empty;
            bool found = false;
            for (int i = 0; i < _queueMembers.Count; i++)
            {
                if (_queueMembers[i].UserId != null && _queueMembers[i].UserId.ToString() == idText)
                {
                    _queueMembers[i].Name = string.IsNullOrWhiteSpace(name) ? "Player" : name;
                    _queueMembers[i].Ready = ready;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                _queueMembers.Add(new Battle99Member
                {
                    UserId = user,
                    Name = string.IsNullOrWhiteSpace(name) ? "Player" : name,
                    Ready = ready
                });
            }

            if (idText != localId)
            {
                _rosterUsers[idText] = user;
                if (!_remoteStates.ContainsKey(idText))
                    _remoteStates[idText] = new Battle99RemoteState { UserId = user, Alive = true };
            }

            if (notify)
                QueueUpdated?.Invoke(_queueMembers);
        }

        private void OnStartMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort playerCount);
            _matchReady = playerCount >= PongConfig.Battle99MinPlayers;
            Status = _matchReady ? "Match ready." : "Waiting for players.";
        }

        private void OnSnapshotMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out byte count);
            string localId = _localUser != null && _localUser.IsValid() ? _localUser.ToString() : string.Empty;

            for (int i = 0; i < count; i++)
            {
                reader.ReadValueSafe(out byte slot);
                Battle99DedicatedProtocol.ReadSnapshot(ref reader, out Battle99NetSnapshot snapshot);

                if (!_slotUsers.TryGetValue(slot, out ProductUserId user) || user == null || !user.IsValid())
                    continue;

                string productId = user.ToString();
                if (productId == localId)
                    continue;

                if (!_remoteStates.TryGetValue(productId, out Battle99RemoteState state))
                {
                    state = new Battle99RemoteState { UserId = user };
                    _remoteStates[productId] = state;
                }

                if (!state.HasSnapshot || (int)(snapshot.Sequence - state.Snapshot.Sequence) > 0)
                {
                    state.Snapshot = snapshot;
                    state.HasSnapshot = true;
                    state.Alive = snapshot.Alive;
                    state.LastReceiveTime = Time.unscaledTime;
                }
            }
        }

        private void OnAttackMessage(ulong senderClientId, FastBufferReader reader)
        {
            string attackerId = Battle99DedicatedProtocol.ReadString(ref reader);
            reader.ReadValueSafe(out byte rows);
            ProductUserId attacker = string.IsNullOrEmpty(attackerId) ? null : ProductUserId.FromString(attackerId);
            AttackReceived?.Invoke(attacker, Mathf.Clamp((int)rows, 1, 8));
        }

        private void OnPlacementMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort placement);
            LocalPlacementReceived?.Invoke(Mathf.Max(1, (int)placement));
        }

        private void OnPongMessage(ulong senderClientId, FastBufferReader reader)
        {
            reader.ReadValueSafe(out uint sequence);
            if (sequence != _awaitingPing)
                return;
            PingMs = Mathf.Max(0f, (Time.realtimeSinceStartup - _pingSentAt) * 1000f);
        }

        private void OnServerStatusMessage(ulong senderClientId, FastBufferReader reader)
        {
            Status = Battle99DedicatedProtocol.ReadString(ref reader);
        }

        private void FailConnect(string error)
        {
            _connectRequested = false;
            _connected = false;
            _matchReady = false;
            Status = error;
            Action<bool, string> callback = _connectCallback;
            _connectCallback = null;
            callback?.Invoke(false, error);
        }

        public void StartPeer(ProductUserId localUser, ProductUserId coordinator, IReadOnlyList<Battle99Member> roster)
        {
            _localUser = localUser;
            _serverUser = coordinator;
            SetRoster(roster);
        }

        public void StopPeer()
        {
            _matchReady = false;
            _connectRequested = false;
            _connected = false;
            _remoteStates.Clear();
            _rosterUsers.Clear();
            _queueMembers.Clear();
            _slotUsers.Clear();
            PingMs = -1f;

            if (_manager != null)
            {
                _manager.OnClientConnectedCallback -= OnClientConnected;
                _manager.OnClientDisconnectCallback -= OnClientDisconnected;
                UnregisterHandlers();
                if (_manager.IsListening && !_manager.IsServer)
                    _manager.Shutdown();
            }
        }

        public void SetCoordinator(ProductUserId coordinator)
        {
            _serverUser = coordinator;
        }

        public void SetRoster(IReadOnlyList<Battle99Member> roster)
        {
            if (roster == null)
                return;

            _rosterUsers.Clear();
            HashSet<string> seen = new HashSet<string>();
            string localId = _localUser != null && _localUser.IsValid() ? _localUser.ToString() : string.Empty;

            for (int i = 0; i < roster.Count; i++)
            {
                ProductUserId user = roster[i].UserId;
                if (user == null || !user.IsValid())
                    continue;

                string id = user.ToString();
                if (id == localId)
                    continue;

                seen.Add(id);
                _rosterUsers[id] = user;
                if (!_remoteStates.ContainsKey(id))
                    _remoteStates[id] = new Battle99RemoteState { UserId = user, Alive = true };
            }

            List<string> remove = null;
            foreach (string id in _remoteStates.Keys)
            {
                if (seen.Contains(id))
                    continue;
                if (remove == null) remove = new List<string>();
                remove.Add(id);
            }
            if (remove != null)
                for (int i = 0; i < remove.Count; i++) _remoteStates.Remove(remove[i]);
        }

        public void BroadcastSnapshot(Battle99NetSnapshot snapshot)
        {
            if (!_connected || !_matchReady || _manager == null)
                return;

            FastBufferWriter writer = new FastBufferWriter(256, Allocator.Temp);
            try
            {
                Battle99DedicatedProtocol.WriteSnapshot(ref writer, snapshot);
                _manager.CustomMessagingManager.SendNamedMessage(
                    Battle99DedicatedProtocol.Snapshot,
                    NetworkManager.ServerClientId,
                    writer,
                    NetworkDelivery.Unreliable);
            }
            finally
            {
                writer.Dispose();
            }
        }

        public void SendAttack(ProductUserId target, int rows)
        {
            if (!_connected || !_matchReady || _manager == null || rows <= 0)
                return;

            using FastBufferWriter writer = new FastBufferWriter(8, Allocator.Temp);
            writer.WriteValueSafe((byte)Mathf.Clamp(rows, 1, 8));
            _manager.CustomMessagingManager.SendNamedMessage(
                Battle99DedicatedProtocol.AttackRequest,
                NetworkManager.ServerClientId,
                writer,
                NetworkDelivery.Reliable);
        }

        public List<ProductUserId> GetAliveRemoteUsers()
        {
            _aliveScratch.Clear();
            foreach (Battle99RemoteState state in _remoteStates.Values)
            {
                if (state.Alive && state.UserId != null && state.UserId.IsValid())
                    _aliveScratch.Add(state.UserId);
            }
            return new List<ProductUserId>(_aliveScratch);
        }

        public int CountAliveRemotePlayers()
        {
            int count = 0;
            foreach (Battle99RemoteState state in _remoteStates.Values)
                if (state.Alive) count++;
            return count;
        }

        public bool TryGetRemoteState(ProductUserId user, out Battle99RemoteState state)
        {
            state = null;
            return user != null && _remoteStates.TryGetValue(user.ToString(), out state);
        }

        private void OnDestroy()
        {
            StopPeer();
        }
    }
}
