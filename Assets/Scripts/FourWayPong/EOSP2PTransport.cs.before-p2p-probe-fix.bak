using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using PlayEveryWare.EpicOnlineServices;
using Unity.Netcode;
using UnityEngine;

namespace FourWayPong
{
    // Netcode for GameObjects transport that sends all game traffic through EOS P2P.
    // The host still runs the authoritative NetworkManager; EOS supplies NAT traversal/relay as needed.
    public sealed class EOSP2PTransport : NetworkTransport
    {
        private const string SocketName = "FourWayPong";
        private const int HeaderSize = 9;
        private const byte PacketMagic = 0xF4;
        private const byte ReliableGameplayChannel = 0;
        private const byte UnreliableGameplayChannel = 1;
        private const byte ConnectionConfirmationChannel = byte.MaxValue;
        private const float ReassemblyTimeout = 15f;
        private const int MaxPacketsPerFrame = 64;

        private static readonly byte[] ConnectionConfirmationPacket =
        {
            (byte)'F', (byte)'W', (byte)'P', (byte)'R',
            (byte)'E', (byte)'A', (byte)'D', (byte)'Y'
        };

        private readonly Queue<TransportEvent> _events = new Queue<TransportEvent>();
        private readonly Dictionary<ulong, ProductUserId> _transportToUser = new Dictionary<ulong, ProductUserId>();
        private readonly Dictionary<ProductUserId, ulong> _userToTransport = new Dictionary<ProductUserId, ulong>();
        private readonly HashSet<ProductUserId> _connectedUsers = new HashSet<ProductUserId>();
        private readonly Dictionary<FragmentKey, FragmentAssembly> _assemblies = new Dictionary<FragmentKey, FragmentAssembly>();

        private P2PInterface _p2p;
        private ProductUserId _localUser;
        private ProductUserId _serverUser;
        private SocketId _socketId;
        private ulong _nextTransportId = 1;
        private uint _nextMessageId = 1;
        private ulong _requestNotify;
        private ulong _establishedNotify;
        private ulong _closedNotify;
        private bool _isServer;
        private bool _initialized;
        private bool _shuttingDown;
        private readonly byte[] _sendBuffer = new byte[P2PInterface.MAX_PACKET_SIZE];
        private readonly byte[] _receiveBuffer = new byte[P2PInterface.MAX_PACKET_SIZE];
        private readonly byte[] _eventPayloadBufferPool = new byte[MaxPacketsPerFrame * P2PInterface.MAX_PACKET_SIZE];
        private int _eventPayloadCursor;
        private int _pumpFrame = -1;
        private int _packetsPumpedThisFrame;
        private float _lastSendWarningTime = -10f;

        public ProductUserId ServerUserIdToConnectTo { get; set; }

        public override ulong ServerClientId => 0;
        public override bool IsSupported => true;

        public bool TryGetProductUserId(ulong clientId, out ProductUserId userId)
        {
            userId = null;
            if (!_initialized)
                return false;

            if (!_isServer && clientId == ServerClientId)
            {
                userId = _serverUser;
                return userId != null && userId.IsValid();
            }

            return _transportToUser.TryGetValue(clientId, out userId) && userId != null && userId.IsValid();
        }

        public bool TryGetClientId(ProductUserId userId, out ulong clientId)
        {
            clientId = ulong.MaxValue;
            if (!_initialized || userId == null || !userId.IsValid())
                return false;

            if (!_isServer && _serverUser != null && Equals(userId, _serverUser))
            {
                clientId = ServerClientId;
                return true;
            }

            return _userToTransport.TryGetValue(userId, out clientId);
        }

        public override void Initialize(NetworkManager networkManager)
        {
            // EOS identity is created asynchronously. Actual P2P initialization is deferred
            // until StartServer/StartClient so NetworkManager may safely exist before login.
        }

        public override bool StartServer()
        {
            if (!EnsureInitialized())
                return false;

            _isServer = true;
            _serverUser = null;
            return true;
        }

        public override bool StartClient()
        {
            if (!EnsureInitialized())
                return false;

            if (ServerUserIdToConnectTo == null || !ServerUserIdToConnectTo.IsValid())
            {
                Debug.LogError("EOSP2PTransport: no valid EOS host ProductUserId was supplied.");
                return false;
            }

            if (Equals(ServerUserIdToConnectTo, _localUser))
            {
                Debug.LogError("EOSP2PTransport: the selected host is the local EOS user. Use a different PC or EOS identity for the client.");
                return false;
            }

            _isServer = false;
            _serverUser = ServerUserIdToConnectTo;
            return OpenConnection(_serverUser);
        }

        private bool EnsureInitialized()
        {
            if (_initialized)
                return true;

            if (EOSManager.Instance == null)
            {
                Debug.LogError("EOSP2PTransport: EOSManager is missing from the scene.");
                return false;
            }

            _localUser = EOSManager.Instance.GetProductUserId();
            _p2p = EOSManager.Instance.GetEOSP2PInterface();
            if (_localUser == null || !_localUser.IsValid() || _p2p == null)
            {
                Debug.LogError("EOSP2PTransport: EOS is not logged in/configured yet.");
                return false;
            }

            _socketId = new SocketId { SocketName = SocketName };
            _shuttingDown = false;
            _transportToUser.Clear();
            _userToTransport.Clear();
            _connectedUsers.Clear();
            _assemblies.Clear();
            _events.Clear();
            _nextTransportId = 1;
            _eventPayloadCursor = 0;
            _pumpFrame = -1;
            _packetsPumpedThisFrame = 0;
            _lastSendWarningTime = -10f;

            AddNotifyPeerConnectionRequestOptions requestOptions = new AddNotifyPeerConnectionRequestOptions
            {
                LocalUserId = _localUser,
                SocketId = _socketId
            };
            _requestNotify = _p2p.AddNotifyPeerConnectionRequest(ref requestOptions, null, OnConnectionRequest);

            AddNotifyPeerConnectionEstablishedOptions establishedOptions = new AddNotifyPeerConnectionEstablishedOptions
            {
                LocalUserId = _localUser,
                SocketId = _socketId
            };
            _establishedNotify = _p2p.AddNotifyPeerConnectionEstablished(ref establishedOptions, null, OnConnectionEstablished);

            AddNotifyPeerConnectionClosedOptions closedOptions = new AddNotifyPeerConnectionClosedOptions
            {
                LocalUserId = _localUser,
                SocketId = _socketId
            };
            _closedNotify = _p2p.AddNotifyPeerConnectionClosed(ref closedOptions, null, OnConnectionClosed);

            _initialized = true;
            return true;
        }

        private void OnConnectionRequest(ref OnIncomingConnectionRequestInfo info)
        {
            if (_shuttingDown || info.RemoteUserId == null || !info.RemoteUserId.IsValid())
                return;

            // Match PlayEveryWare's reference transport: only the server accepts
            // incoming EOS connection requests. A client opens one outgoing
            // connection to the selected host and waits for the server's
            // confirmation packet before reporting NetworkEvent.Connect to NGO.
            if (!_isServer)
            {
                CloseConnectionOptions rejectOptions = new CloseConnectionOptions
                {
                    LocalUserId = _localUser,
                    RemoteUserId = info.RemoteUserId,
                    SocketId = _socketId
                };
                _p2p.CloseConnection(ref rejectOptions);
                return;
            }

            if (Equals(info.RemoteUserId, _localUser))
            {
                Debug.LogWarning("EOSP2PTransport: rejected a P2P connection request from the local EOS user.");
                return;
            }

            AcceptConnectionOptions options = new AcceptConnectionOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = info.RemoteUserId,
                SocketId = _socketId
            };
            Result result = _p2p.AcceptConnection(ref options);
            if (result != Result.Success)
            {
                Debug.LogWarning("EOSP2PTransport: could not accept P2P connection: " + result);
                return;
            }

            // From the server's perspective the remote side requested the
            // connection and we just accepted it, so the transport is ready.
            // Queue the NGO connect event and send a private confirmation packet
            // that tells the client when it is safe to report its own connect.
            QueueConnected(info.RemoteUserId);
            SendConnectionConfirmation(info.RemoteUserId);
        }

        private void OnConnectionEstablished(ref OnPeerConnectionEstablishedInfo info)
        {
            // Do not translate this EOS notification directly into NGO's
            // NetworkEvent.Connect. EOS can report physical establishment before
            // both sides of our socket handshake are ready. The server reports
            // connect after accepting the incoming request; the client reports it
            // only after receiving ConnectionConfirmationPacket.
        }

        private void OnConnectionClosed(ref OnRemoteConnectionClosedInfo info)
        {
            if (_shuttingDown || info.RemoteUserId == null)
                return;

            QueueDisconnected(info.RemoteUserId);
        }

        private bool OpenConnection(ProductUserId remoteUser)
        {
            AcceptConnectionOptions options = new AcceptConnectionOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = remoteUser,
                SocketId = _socketId
            };
            Result result = _p2p.AcceptConnection(ref options);
            if (result != Result.Success)
            {
                Debug.LogError("EOSP2PTransport: could not request P2P connection: " + result);
                return false;
            }
            return true;
        }

        private void SendConnectionConfirmation(ProductUserId remoteUser)
        {
            SendPacketOptions options = new SendPacketOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = remoteUser,
                SocketId = _socketId,
                AllowDelayedDelivery = true,
                Channel = ConnectionConfirmationChannel,
                Reliability = PacketReliability.ReliableOrdered,
                Data = new ArraySegment<byte>(ConnectionConfirmationPacket)
            };

            Result result = _p2p.SendPacket(ref options);
            if (result != Result.Success)
                Debug.LogWarning("EOSP2PTransport: connection confirmation failed: " + result);
        }

        private static bool IsConnectionConfirmation(byte[] bytes, int length)
        {
            if (bytes == null || length != ConnectionConfirmationPacket.Length)
                return false;

            for (int i = 0; i < length; i++)
            {
                if (bytes[i] != ConnectionConfirmationPacket[i])
                    return false;
            }

            return true;
        }

        private void QueueConnected(ProductUserId remoteUser)
        {
            ulong id;
            if (_isServer)
            {
                if (!_userToTransport.TryGetValue(remoteUser, out id))
                {
                    id = _nextTransportId++;
                    _userToTransport[remoteUser] = id;
                    _transportToUser[id] = remoteUser;
                }
            }
            else
            {
                id = ServerClientId;
                _serverUser = remoteUser;
            }

            if (!_connectedUsers.Add(remoteUser))
                return;

            _events.Enqueue(new TransportEvent(NetworkEvent.Connect, id, default));
        }

        private void QueueDisconnected(ProductUserId remoteUser)
        {
            ulong id;
            if (_isServer)
            {
                if (!_userToTransport.TryGetValue(remoteUser, out id))
                    return;
            }
            else
            {
                if (_serverUser == null || remoteUser != _serverUser)
                    return;
                id = ServerClientId;
            }

            if (!_connectedUsers.Remove(remoteUser))
                return;

            _events.Enqueue(new TransportEvent(NetworkEvent.Disconnect, id, default));
        }

        public override void Send(ulong clientId, ArraySegment<byte> payload, NetworkDelivery networkDelivery)
        {
            if (!_initialized || payload.Array == null)
                return;

            ProductUserId remote = ResolveUser(clientId);
            if (remote == null || !remote.IsValid())
                return;

            int maxPayload = P2PInterface.MAX_PACKET_SIZE - HeaderSize;
            int fragmentCount = Math.Max(1, (payload.Count + maxPayload - 1) / maxPayload);
            if (fragmentCount > ushort.MaxValue)
            {
                Debug.LogError("EOSP2PTransport: payload is too large to fragment.");
                return;
            }

            bool requestedUnreliable = networkDelivery == NetworkDelivery.Unreliable ||
                                       networkDelivery == NetworkDelivery.UnreliableSequenced;
            bool fragmented = fragmentCount > 1;
            PacketReliability reliability = fragmented
                ? PacketReliability.ReliableOrdered
                : ToReliability(networkDelivery);
            byte channel = !fragmented && requestedUnreliable ? UnreliableGameplayChannel : ReliableGameplayChannel;
            bool allowDelayedDelivery = !requestedUnreliable || fragmented;

            uint messageId = _nextMessageId++;
            for (int fragmentIndex = 0; fragmentIndex < fragmentCount; fragmentIndex++)
            {
                int sourceOffset = fragmentIndex * maxPayload;
                int fragmentPayloadSize = Math.Min(maxPayload, payload.Count - sourceOffset);
                int packetLength = HeaderSize + fragmentPayloadSize;
                byte[] bytes = _sendBuffer;
                bytes[0] = PacketMagic;
                WriteUInt(bytes, 1, messageId);
                WriteUShort(bytes, 5, (ushort)fragmentIndex);
                WriteUShort(bytes, 7, (ushort)fragmentCount);
                if (fragmentPayloadSize > 0)
                    Buffer.BlockCopy(payload.Array, payload.Offset + sourceOffset, bytes, HeaderSize, fragmentPayloadSize);

                SendPacketOptions options = new SendPacketOptions
                {
                    LocalUserId = _localUser,
                    RemoteUserId = remote,
                    SocketId = _socketId,
                    AllowDelayedDelivery = allowDelayedDelivery,
                    Channel = channel,
                    Reliability = reliability,
                    Data = new ArraySegment<byte>(bytes, 0, packetLength)
                };

                Result result = _p2p.SendPacket(ref options);
                if (result != Result.Success)
                {
                    // Do not turn a temporary network problem into a logging/GC storm.
                    if (Time.realtimeSinceStartup - _lastSendWarningTime >= 2f)
                    {
                        _lastSendWarningTime = Time.realtimeSinceStartup;
                        Debug.LogWarning("EOSP2PTransport: SendPacket failed: " + result);
                    }
                    return;
                }
            }
        }

        public override NetworkEvent PollEvent(out ulong clientId, out ArraySegment<byte> payload, out float receiveTime)
        {
            if (_initialized && _events.Count == 0)
                PumpPackets();

            if (_events.Count > 0)
            {
                TransportEvent ev = _events.Dequeue();
                clientId = ev.ClientId;
                payload = ev.Payload;
                receiveTime = Time.realtimeSinceStartup;
                return ev.Type;
            }

            clientId = 0;
            payload = default;
            receiveTime = Time.realtimeSinceStartup;
            return NetworkEvent.Nothing;
        }

        private void PumpPackets()
        {
            CleanupAssemblies();

            int frame = Time.frameCount;
            if (_pumpFrame != frame)
            {
                _pumpFrame = frame;
                _packetsPumpedThisFrame = 0;
            }

            int budget = MaxPacketsPerFrame - _packetsPumpedThisFrame;
            if (budget <= 0)
                return;

            for (int packetIndex = 0; packetIndex < budget; packetIndex++)
            {
                GetNextReceivedPacketSizeOptions sizeOptions = new GetNextReceivedPacketSizeOptions
                {
                    LocalUserId = _localUser,
                    RequestedChannel = null
                };
                Result sizeResult = _p2p.GetNextReceivedPacketSize(ref sizeOptions, out uint packetSize);
                if (sizeResult != Result.Success || packetSize == 0)
                    return;

                if (packetSize > _receiveBuffer.Length)
                {
                    // EOS P2P should never exceed MAX_PACKET_SIZE. Drop malformed data instead of allocating.
                    return;
                }

                ReceivePacketOptions receiveOptions = new ReceivePacketOptions
                {
                    LocalUserId = _localUser,
                    MaxDataSizeBytes = packetSize,
                    RequestedChannel = null
                };
                ProductUserId remote = null;
                SocketId socket = default;
                Result receiveResult = _p2p.ReceivePacket(
                    ref receiveOptions,
                    ref remote,
                    ref socket,
                    out byte channel,
                    new ArraySegment<byte>(_receiveBuffer, 0, (int)packetSize),
                    out uint bytesWritten);

                _packetsPumpedThisFrame++;

                if (receiveResult != Result.Success || remote == null || socket.SocketName.ToString() != SocketName)
                    continue;

                int length = (int)bytesWritten;
                if (channel == ConnectionConfirmationChannel)
                {
                    if (!_isServer && _serverUser != null && Equals(remote, _serverUser) && IsConnectionConfirmation(_receiveBuffer, length))
                        QueueConnected(remote);
                    continue;
                }

                if (channel != ReliableGameplayChannel && channel != UnreliableGameplayChannel)
                    continue;

                ulong transportId = EnsureRemoteForData(remote);
                if (transportId == ulong.MaxValue)
                    continue;

                ProcessPacket(remote, transportId, _receiveBuffer, length);
            }
        }

        private ulong EnsureRemoteForData(ProductUserId remote)
        {
            if (_isServer)
            {
                if (_userToTransport.TryGetValue(remote, out ulong id) && _connectedUsers.Contains(remote))
                    return id;

                return ulong.MaxValue;
            }

            if (_serverUser != null && Equals(remote, _serverUser) && _connectedUsers.Contains(remote))
                return ServerClientId;

            return ulong.MaxValue;
        }

        private void ProcessPacket(ProductUserId remote, ulong transportId, byte[] bytes, int length)
        {
            if (length < HeaderSize || bytes[0] != PacketMagic)
                return;

            uint messageId = ReadUInt(bytes, 1);
            ushort fragmentIndex = ReadUShort(bytes, 5);
            ushort fragmentCount = ReadUShort(bytes, 7);
            if (fragmentCount == 0 || fragmentIndex >= fragmentCount)
                return;

            int dataLength = length - HeaderSize;

            if (fragmentCount == 1)
            {
                int slotOffset = _eventPayloadCursor * P2PInterface.MAX_PACKET_SIZE;
                _eventPayloadCursor = (_eventPayloadCursor + 1) % MaxPacketsPerFrame;
                if (dataLength > 0)
                    Buffer.BlockCopy(bytes, HeaderSize, _eventPayloadBufferPool, slotOffset, dataLength);
                _events.Enqueue(new TransportEvent(
                    NetworkEvent.Data,
                    transportId,
                    new ArraySegment<byte>(_eventPayloadBufferPool, slotOffset, dataLength)));
                return;
            }

            byte[] fragment = new byte[dataLength];
            if (dataLength > 0)
                Buffer.BlockCopy(bytes, HeaderSize, fragment, 0, dataLength);

            FragmentKey key = new FragmentKey(remote, messageId);
            if (!_assemblies.TryGetValue(key, out FragmentAssembly assembly) || assembly.Fragments.Length != fragmentCount)
            {
                assembly = new FragmentAssembly(fragmentCount);
                _assemblies[key] = assembly;
            }

            assembly.LastUpdate = Time.realtimeSinceStartup;
            if (assembly.Fragments[fragmentIndex] == null)
            {
                assembly.Fragments[fragmentIndex] = fragment;
                assembly.Received++;
            }

            if (assembly.Received != fragmentCount)
                return;

            int total = 0;
            for (int i = 0; i < assembly.Fragments.Length; i++)
                total += assembly.Fragments[i].Length;

            byte[] complete = new byte[total];
            int offset = 0;
            for (int i = 0; i < assembly.Fragments.Length; i++)
            {
                byte[] part = assembly.Fragments[i];
                Buffer.BlockCopy(part, 0, complete, offset, part.Length);
                offset += part.Length;
            }
            _assemblies.Remove(key);
            _events.Enqueue(new TransportEvent(NetworkEvent.Data, transportId, new ArraySegment<byte>(complete)));
        }

        private void CleanupAssemblies()
        {
            if (_assemblies.Count == 0)
                return;

            float now = Time.realtimeSinceStartup;
            List<FragmentKey> stale = null;
            foreach (KeyValuePair<FragmentKey, FragmentAssembly> pair in _assemblies)
            {
                if (now - pair.Value.LastUpdate <= ReassemblyTimeout)
                    continue;
                stale ??= new List<FragmentKey>();
                stale.Add(pair.Key);
            }
            if (stale == null)
                return;
            foreach (FragmentKey key in stale)
                _assemblies.Remove(key);
        }

        public override void DisconnectRemoteClient(ulong clientId)
        {
            if (!_initialized || !_isServer || !_transportToUser.TryGetValue(clientId, out ProductUserId remote))
                return;
            CloseConnection(remote);
        }

        public override void DisconnectLocalClient()
        {
            if (_initialized && !_isServer && _serverUser != null)
                CloseConnection(_serverUser);
        }

        private void CloseConnection(ProductUserId remote)
        {
            CloseConnectionOptions options = new CloseConnectionOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = remote,
                SocketId = _socketId
            };
            _p2p.CloseConnection(ref options);
        }

        public override ulong GetCurrentRtt(ulong clientId)
        {
            // EOS P2P does not expose RTT for this transport. Netcode does not require it.
            return 0;
        }

        public override void Shutdown()
        {
            if (!_initialized)
                return;

            _shuttingDown = true;
            if (_isServer)
            {
                foreach (ProductUserId remote in new List<ProductUserId>(_userToTransport.Keys))
                    CloseConnection(remote);
            }
            else if (_serverUser != null)
            {
                CloseConnection(_serverUser);
            }

            if (_requestNotify != 0) _p2p.RemoveNotifyPeerConnectionRequest(_requestNotify);
            if (_establishedNotify != 0) _p2p.RemoveNotifyPeerConnectionEstablished(_establishedNotify);
            if (_closedNotify != 0) _p2p.RemoveNotifyPeerConnectionClosed(_closedNotify);

            _requestNotify = _establishedNotify = _closedNotify = 0;
            _events.Clear();
            _transportToUser.Clear();
            _userToTransport.Clear();
            _connectedUsers.Clear();
            _assemblies.Clear();
            _p2p = null;
            _localUser = null;
            _serverUser = null;
            ServerUserIdToConnectTo = null;
            _initialized = false;
            _isServer = false;
            _shuttingDown = false;
        }

        private ProductUserId ResolveUser(ulong clientId)
        {
            if (!_isServer)
                return clientId == ServerClientId ? _serverUser : null;
            return _transportToUser.TryGetValue(clientId, out ProductUserId user) ? user : null;
        }

        private static PacketReliability ToReliability(NetworkDelivery delivery)
        {
            return delivery switch
            {
                NetworkDelivery.Unreliable => PacketReliability.UnreliableUnordered,
                NetworkDelivery.UnreliableSequenced => PacketReliability.UnreliableUnordered,
                NetworkDelivery.Reliable => PacketReliability.ReliableOrdered,
                _ => PacketReliability.ReliableOrdered
            };
        }

        private static void WriteUInt(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value >> 24);
            bytes[offset + 1] = (byte)(value >> 16);
            bytes[offset + 2] = (byte)(value >> 8);
            bytes[offset + 3] = (byte)value;
        }

        private static uint ReadUInt(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
                   ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        private static void WriteUShort(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)(value >> 8);
            bytes[offset + 1] = (byte)value;
        }

        private static ushort ReadUShort(byte[] bytes, int offset)
        {
            return (ushort)((bytes[offset] << 8) | bytes[offset + 1]);
        }

        private readonly struct FragmentKey : IEquatable<FragmentKey>
        {
            private readonly ProductUserId _user;
            private readonly uint _messageId;

            public FragmentKey(ProductUserId user, uint messageId)
            {
                _user = user;
                _messageId = messageId;
            }

            public bool Equals(FragmentKey other) => Equals(_user, other._user) && _messageId == other._messageId;
            public override bool Equals(object obj) => obj is FragmentKey other && Equals(other);
            public override int GetHashCode() => ((_user != null ? _user.GetHashCode() : 0) * 397) ^ (int)_messageId;
        }

        private sealed class FragmentAssembly
        {
            public readonly byte[][] Fragments;
            public int Received;
            public float LastUpdate;

            public FragmentAssembly(int count)
            {
                Fragments = new byte[count][];
                LastUpdate = Time.realtimeSinceStartup;
            }
        }

        private readonly struct TransportEvent
        {
            public readonly NetworkEvent Type;
            public readonly ulong ClientId;
            public readonly ArraySegment<byte> Payload;

            public TransportEvent(NetworkEvent type, ulong clientId, ArraySegment<byte> payload)
            {
                Type = type;
                ClientId = clientId;
                Payload = payload;
            }
        }
    }
}
