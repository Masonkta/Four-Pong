using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using Epic.OnlineServices.P2P;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;

namespace FourWayPong
{
    public struct Battle99NetSnapshot
    {
        public uint Sequence;
        public bool Alive;
        public float PaddleNormalized;
        public float PaddleScale;
        public byte BallMask;
        public Vector2 Ball0;
        public Vector2 Ball1;
        public Vector2 Ball2;
        public Vector2 Ball3;
        public ulong BrickLow;
        public ulong BrickMid;
        public uint BrickHigh;
        public byte PendingGarbage;
    }

    public sealed class Battle99RemoteState
    {
        public ProductUserId UserId;
        public Battle99NetSnapshot Snapshot;
        public bool HasSnapshot;
        public bool Alive = true;
        public float LastReceiveTime;
    }

    public sealed class Battle99PeerService : MonoBehaviour, IBattle99NetworkLink
    {
        private const string SocketName = "FourWayPongB99";
        private const byte SnapshotChannel = 40;
        private const byte ReliableChannel = 41;
        private const byte PingChannel = 42;
        private const byte TypeSnapshot = 1;
        private const byte TypeAttack = 2;
        private const byte TypePing = 3;
        private const byte TypePong = 4;
        private const int SnapshotPacketSize = 51;
        private const int MaxPacketsPerFrame = 64;

        private readonly Dictionary<string, Battle99RemoteState> _remoteStates = new Dictionary<string, Battle99RemoteState>();
        private readonly Dictionary<string, ProductUserId> _remoteUsers = new Dictionary<string, ProductUserId>();
        private readonly HashSet<string> _allowedUsers = new HashSet<string>();
        private readonly byte[] _receiveBuffer = new byte[P2PInterface.MAX_PACKET_SIZE];

        private P2PInterface _p2p;
        private ProductUserId _localUser;
        private ProductUserId _coordinator;
        private SocketId _socketId;
        private ulong _requestNotify;
        private ulong _closedNotify;
        private bool _active;
        private uint _attackSequence;
        private uint _pingSequence;
        private float _pingTimer;
        private float _lastPingSentAt;
        private uint _lastPingAwaiting;

        public event Action<ProductUserId, int> AttackReceived;
        public event Action<int> LocalPlacementReceived;
        public float PingMs { get; private set; } = -1f;
        public IReadOnlyDictionary<string, Battle99RemoteState> RemoteStates => _remoteStates;
        public bool Ready => _active;

        public void StartPeer(ProductUserId localUser, ProductUserId coordinator, IReadOnlyList<Battle99Member> roster)
        {
            StopPeer();

            if (EOSManager.Instance == null || localUser == null || !localUser.IsValid())
                return;

            _p2p = EOSManager.Instance.GetEOSP2PInterface();
            if (_p2p == null)
                return;

            _localUser = localUser;
            _coordinator = coordinator;
            _socketId = new SocketId { SocketName = SocketName };
            _active = true;
            PingMs = Equals(_localUser, _coordinator) ? 0f : -1f;
            _pingTimer = 0f;
            _lastPingAwaiting = 0;
            SetRoster(roster);

            AddNotifyPeerConnectionRequestOptions requestOptions = new AddNotifyPeerConnectionRequestOptions
            {
                LocalUserId = _localUser,
                SocketId = _socketId
            };
            _requestNotify = _p2p.AddNotifyPeerConnectionRequest(ref requestOptions, null, OnConnectionRequest);

            AddNotifyPeerConnectionClosedOptions closedOptions = new AddNotifyPeerConnectionClosedOptions
            {
                LocalUserId = _localUser,
                SocketId = _socketId
            };
            _closedNotify = _p2p.AddNotifyPeerConnectionClosed(ref closedOptions, null, OnConnectionClosed);

            foreach (ProductUserId remote in _remoteUsers.Values)
                OpenPeer(remote);
        }

        public void SetCoordinator(ProductUserId coordinator)
        {
            _coordinator = coordinator;
            if (_coordinator != null && Equals(_coordinator, _localUser))
                PingMs = 0f;
        }

        public void SetRoster(IReadOnlyList<Battle99Member> roster)
        {
            if (roster == null)
                return;

            HashSet<string> current = new HashSet<string>();
            for (int i = 0; i < roster.Count; i++)
            {
                ProductUserId user = roster[i].UserId;
                if (user == null || !user.IsValid() || (_localUser != null && Equals(user, _localUser)))
                    continue;

                string id = user.ToString();
                current.Add(id);
                _allowedUsers.Add(id);
                _remoteUsers[id] = user;
                if (!_remoteStates.ContainsKey(id))
                {
                    _remoteStates[id] = new Battle99RemoteState
                    {
                        UserId = user,
                        Alive = true,
                        HasSnapshot = false,
                        LastReceiveTime = Time.unscaledTime
                    };
                    if (_active)
                        OpenPeer(user);
                }
            }

            List<string> removed = null;
            foreach (KeyValuePair<string, Battle99RemoteState> pair in _remoteStates)
            {
                if (current.Contains(pair.Key))
                    continue;
                removed ??= new List<string>();
                removed.Add(pair.Key);
            }

            if (removed != null)
            {
                for (int i = 0; i < removed.Count; i++)
                {
                    string id = removed[i];
                    if (_remoteStates.TryGetValue(id, out Battle99RemoteState state))
                        state.Alive = false;
                    _allowedUsers.Remove(id);
                }
            }
        }

        private void Update()
        {
            if (!_active || _p2p == null)
                return;

            PumpPackets();
            UpdatePing();
        }

        private void UpdatePing()
        {
            ProductUserId pingTarget = _coordinator;
            if (pingTarget == null || !pingTarget.IsValid() || Equals(pingTarget, _localUser))
            {
                pingTarget = null;
                foreach (ProductUserId remote in _remoteUsers.Values)
                {
                    pingTarget = remote;
                    break;
                }
            }

            if (pingTarget == null || !pingTarget.IsValid())
            {
                PingMs = 0f;
                return;
            }

            _pingTimer += Time.unscaledDeltaTime;
            if (_pingTimer < 1f)
                return;

            _pingTimer = 0f;
            uint seq = ++_pingSequence;
            _lastPingAwaiting = seq;
            _lastPingSentAt = Time.realtimeSinceStartup;
            byte[] packet = new byte[13];
            WriteHeader(packet, TypePing);
            WriteUInt(packet, 5, seq);
            WriteFloat(packet, 9, _lastPingSentAt);
            SendRaw(pingTarget, PingChannel, PacketReliability.UnreliableUnordered, false, packet);
        }

        public void BroadcastSnapshot(Battle99NetSnapshot snapshot)
        {
            if (!_active || _remoteUsers.Count == 0)
                return;

            byte[] packet = EncodeSnapshot(snapshot);
            foreach (ProductUserId remote in _remoteUsers.Values)
                SendRaw(remote, SnapshotChannel, PacketReliability.UnreliableUnordered, false, packet);
        }

        public void SendAttack(ProductUserId target, int rows)
        {
            if (!_active || target == null || !target.IsValid() || rows <= 0)
                return;

            byte[] packet = new byte[10];
            WriteHeader(packet, TypeAttack);
            WriteUInt(packet, 5, ++_attackSequence);
            packet[9] = (byte)Mathf.Clamp(rows, 1, 8);
            SendRaw(target, ReliableChannel, PacketReliability.ReliableOrdered, true, packet);
        }

        public List<ProductUserId> GetAliveRemoteUsers()
        {
            List<ProductUserId> result = new List<ProductUserId>();
            foreach (Battle99RemoteState state in _remoteStates.Values)
            {
                if (state.Alive && state.UserId != null && state.UserId.IsValid())
                    result.Add(state.UserId);
            }
            return result;
        }

        public int CountAliveRemotePlayers()
        {
            int count = 0;
            foreach (Battle99RemoteState state in _remoteStates.Values)
                if (state.Alive)
                    count++;
            return count;
        }

        public bool TryGetRemoteState(ProductUserId user, out Battle99RemoteState state)
        {
            state = null;
            if (user == null)
                return false;
            return _remoteStates.TryGetValue(user.ToString(), out state);
        }

        private void OnConnectionRequest(ref OnIncomingConnectionRequestInfo info)
        {
            if (!_active || info.RemoteUserId == null || !info.RemoteUserId.IsValid())
                return;

            string id = info.RemoteUserId.ToString();
            if (!_allowedUsers.Contains(id))
                return;

            AcceptConnectionOptions options = new AcceptConnectionOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = info.RemoteUserId,
                SocketId = _socketId
            };
            _p2p.AcceptConnection(ref options);
        }

        private void OnConnectionClosed(ref OnRemoteConnectionClosedInfo info)
        {
            if (info.RemoteUserId == null)
                return;
            if (_remoteStates.TryGetValue(info.RemoteUserId.ToString(), out Battle99RemoteState state))
                state.LastReceiveTime = Time.unscaledTime;
        }

        private void OpenPeer(ProductUserId remote)
        {
            if (!_active || remote == null || !remote.IsValid() || Equals(remote, _localUser))
                return;

            AcceptConnectionOptions options = new AcceptConnectionOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = remote,
                SocketId = _socketId
            };
            _p2p.AcceptConnection(ref options);
        }

        private void PumpPackets()
        {
            for (int i = 0; i < MaxPacketsPerFrame; i++)
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
                    return;

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

                if (receiveResult != Result.Success || remote == null || !remote.IsValid() || socket.SocketName.ToString() != SocketName)
                    continue;

                string id = remote.ToString();
                if (!_allowedUsers.Contains(id))
                    continue;

                int length = (int)bytesWritten;
                if (length < 5 || !HasMagic(_receiveBuffer))
                    continue;

                byte type = _receiveBuffer[4];
                if (channel == SnapshotChannel && type == TypeSnapshot)
                {
                    if (TryDecodeSnapshot(_receiveBuffer, length, out Battle99NetSnapshot snapshot))
                    {
                        if (!_remoteStates.TryGetValue(id, out Battle99RemoteState state))
                        {
                            state = new Battle99RemoteState { UserId = remote };
                            _remoteStates[id] = state;
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
                else if (channel == ReliableChannel && type == TypeAttack && length >= 10)
                {
                    int rows = Mathf.Clamp(_receiveBuffer[9], 1, 8);
                    AttackReceived?.Invoke(remote, rows);
                }
                else if (channel == PingChannel && type == TypePing && length >= 13)
                {
                    byte[] pong = new byte[13];
                    Buffer.BlockCopy(_receiveBuffer, 0, pong, 0, 13);
                    pong[4] = TypePong;
                    SendRaw(remote, PingChannel, PacketReliability.UnreliableUnordered, false, pong);
                }
                else if (channel == PingChannel && type == TypePong && length >= 13)
                {
                    uint seq = ReadUInt(_receiveBuffer, 5);
                    if (seq == _lastPingAwaiting)
                    {
                        float ms = Mathf.Max(0f, (Time.realtimeSinceStartup - _lastPingSentAt) * 1000f);
                        PingMs = PingMs < 0f ? ms : Mathf.Lerp(PingMs, ms, 0.25f);
                    }
                }
            }
        }

        private void SendRaw(ProductUserId remote, byte channel, PacketReliability reliability, bool allowDelayed, byte[] data)
        {
            if (!_active || _p2p == null || remote == null || !remote.IsValid() || data == null)
                return;

            SendPacketOptions options = new SendPacketOptions
            {
                LocalUserId = _localUser,
                RemoteUserId = remote,
                SocketId = _socketId,
                AllowDelayedDelivery = allowDelayed,
                Channel = channel,
                Reliability = reliability,
                Data = new ArraySegment<byte>(data)
            };
            _p2p.SendPacket(ref options);
        }

        private static byte[] EncodeSnapshot(Battle99NetSnapshot snapshot)
        {
            byte[] packet = new byte[SnapshotPacketSize];
            WriteHeader(packet, TypeSnapshot);
            WriteUInt(packet, 5, snapshot.Sequence);
            packet[9] = snapshot.Alive ? (byte)1 : (byte)0;
            WriteUShort(packet, 10, Quantize01(snapshot.PaddleNormalized));
            packet[12] = (byte)Mathf.RoundToInt(Mathf.InverseLerp(0.5f, 2f, Mathf.Clamp(snapshot.PaddleScale, 0.5f, 2f)) * 255f);
            packet[13] = snapshot.BallMask;
            WriteBall(packet, 14, snapshot.Ball0);
            WriteBall(packet, 18, snapshot.Ball1);
            WriteBall(packet, 22, snapshot.Ball2);
            WriteBall(packet, 26, snapshot.Ball3);
            WriteULong(packet, 30, snapshot.BrickLow);
            WriteULong(packet, 38, snapshot.BrickMid);
            WriteUInt(packet, 46, snapshot.BrickHigh);
            packet[50] = snapshot.PendingGarbage;
            return packet;
        }

        private static bool TryDecodeSnapshot(byte[] data, int length, out Battle99NetSnapshot snapshot)
        {
            snapshot = default;
            if (length < SnapshotPacketSize)
                return false;

            snapshot.Sequence = ReadUInt(data, 5);
            snapshot.Alive = data[9] != 0;
            snapshot.PaddleNormalized = Dequantize01(ReadUShort(data, 10));
            snapshot.PaddleScale = Mathf.Lerp(0.5f, 2f, data[12] / 255f);
            snapshot.BallMask = data[13];
            snapshot.Ball0 = ReadBall(data, 14);
            snapshot.Ball1 = ReadBall(data, 18);
            snapshot.Ball2 = ReadBall(data, 22);
            snapshot.Ball3 = ReadBall(data, 26);
            snapshot.BrickLow = ReadULong(data, 30);
            snapshot.BrickMid = ReadULong(data, 38);
            snapshot.BrickHigh = ReadUInt(data, 46);
            snapshot.PendingGarbage = data[50];
            return true;
        }

        private static void WriteHeader(byte[] packet, byte type)
        {
            packet[0] = (byte)'B';
            packet[1] = (byte)'9';
            packet[2] = (byte)'9';
            packet[3] = (byte)'P';
            packet[4] = type;
        }

        private static bool HasMagic(byte[] data)
        {
            return data[0] == (byte)'B' && data[1] == (byte)'9' && data[2] == (byte)'9' && data[3] == (byte)'P';
        }

        private static ushort Quantize01(float value)
        {
            return (ushort)Mathf.RoundToInt(Mathf.Clamp01(value) * 65535f);
        }

        private static float Dequantize01(ushort value)
        {
            return value / 65535f;
        }

        private static void WriteBall(byte[] data, int offset, Vector2 normalized)
        {
            WriteUShort(data, offset, Quantize01(normalized.x));
            WriteUShort(data, offset + 2, Quantize01(normalized.y));
        }

        private static Vector2 ReadBall(byte[] data, int offset)
        {
            return new Vector2(Dequantize01(ReadUShort(data, offset)), Dequantize01(ReadUShort(data, offset + 2)));
        }

        private static void WriteUInt(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
            data[offset + 2] = (byte)(value >> 16);
            data[offset + 3] = (byte)(value >> 24);
        }

        private static uint ReadUInt(byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16 | data[offset + 3] << 24);
        }

        private static void WriteUShort(byte[] data, int offset, ushort value)
        {
            data[offset] = (byte)value;
            data[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUShort(byte[] data, int offset)
        {
            return (ushort)(data[offset] | data[offset + 1] << 8);
        }

        private static void WriteULong(byte[] data, int offset, ulong value)
        {
            for (int i = 0; i < 8; i++)
                data[offset + i] = (byte)(value >> (i * 8));
        }

        private static ulong ReadULong(byte[] data, int offset)
        {
            ulong value = 0;
            for (int i = 0; i < 8; i++)
                value |= (ulong)data[offset + i] << (i * 8);
            return value;
        }

        private static void WriteFloat(byte[] data, int offset, float value)
        {
            byte[] bytes = BitConverter.GetBytes(value);
            Buffer.BlockCopy(bytes, 0, data, offset, 4);
        }

        public void StopPeer()
        {
            if (_p2p != null && _localUser != null && _localUser.IsValid())
            {
                if (_requestNotify != 0) _p2p.RemoveNotifyPeerConnectionRequest(_requestNotify);
                if (_closedNotify != 0) _p2p.RemoveNotifyPeerConnectionClosed(_closedNotify);

                foreach (ProductUserId remote in _remoteUsers.Values)
                {
                    CloseConnectionOptions close = new CloseConnectionOptions
                    {
                        LocalUserId = _localUser,
                        RemoteUserId = remote,
                        SocketId = _socketId
                    };
                    _p2p.CloseConnection(ref close);
                }
            }

            _requestNotify = 0;
            _closedNotify = 0;
            _active = false;
            _p2p = null;
            _localUser = null;
            _coordinator = null;
            _allowedUsers.Clear();
            _remoteUsers.Clear();
            _remoteStates.Clear();
            PingMs = -1f;
        }

        private void OnDestroy()
        {
            StopPeer();
        }
    }
}
