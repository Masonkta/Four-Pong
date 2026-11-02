using System;
using System.Collections.Generic;
using Epic.OnlineServices;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;

namespace FourWayPong
{
    public sealed class Battle99Member
    {
        public ProductUserId UserId;
        public string Name;
        public bool Ready;
    }

    /// <summary>
    /// Client-side Battle 99 matchmaking facade.
    /// EOS Sessions discovers a dedicated server. The dedicated server owns the
    /// waiting queue, ready state, roster lock, attacks, placements, and match lifecycle.
    /// </summary>
    public sealed class Battle99Service : MonoBehaviour
    {
        private readonly List<Battle99Member> _members = new List<Battle99Member>();
        private readonly List<Battle99Member> _matchRoster = new List<Battle99Member>();

        private Battle99SessionService _sessionService;
        private Battle99DedicatedClient _dedicatedClient;
        private bool _searching;
        private float _retryTimer;
        private ProductUserId _dedicatedServerUserId;
        private string _dedicatedSessionId = string.Empty;
        private int _generation;

        public bool Busy { get; private set; }
        public bool InQueue { get; private set; }
        public bool MatchStarted { get; private set; }
        public string Status { get; private set; } = string.Empty;
        public string LobbyId => _dedicatedSessionId;
        public ProductUserId OwnerUserId => _dedicatedServerUserId;
        public ProductUserId LocalUserId => EOSManager.Instance != null ? EOSManager.Instance.GetProductUserId() : null;
        public IReadOnlyList<Battle99Member> Members => _members;
        public IReadOnlyList<Battle99Member> MatchRoster => _matchRoster;
        public bool LocalReady { get; private set; }
        public string DedicatedSessionId => _dedicatedSessionId;
        public ProductUserId DedicatedServerUserId => _dedicatedServerUserId;
        public bool UsesDedicatedServer => true;
        public Battle99DedicatedClient DedicatedClient => _dedicatedClient;

        private void Awake()
        {
            _sessionService = GetComponent<Battle99SessionService>();
            if (_sessionService == null)
                _sessionService = gameObject.AddComponent<Battle99SessionService>();

            _dedicatedClient = GetComponent<Battle99DedicatedClient>();
            if (_dedicatedClient == null)
                _dedicatedClient = gameObject.AddComponent<Battle99DedicatedClient>();

            _dedicatedClient.QueueUpdated -= OnQueueUpdated;
            _dedicatedClient.QueueUpdated += OnQueueUpdated;
        }

        private void Update()
        {
            if (!Busy && !InQueue)
                return;

            if (InQueue && !MatchStarted && _dedicatedClient != null && !_dedicatedClient.Connected)
            {
                InQueue = false;
                Busy = true;
                _dedicatedClient.StopPeer();
                _sessionService.LeaveJoinedSession();
                _dedicatedSessionId = string.Empty;
                _dedicatedServerUserId = null;
                _members.Clear();
                LocalReady = false;
                Status = "Server filled or became busy. Finding another server...";
                _retryTimer = 1f;
                return;
            }

            if (_dedicatedClient != null && _dedicatedClient.Connected)
            {
                if (!MatchStarted && _dedicatedClient.MatchReady)
                {
                    CopyMatchRoster();
                    MatchStarted = true;
                    Busy = false;
                    Status = "Match starting.";
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_dedicatedClient.Status))
                    Status = _dedicatedClient.Status;
            }

            if (_searching || InQueue)
                return;

            _retryTimer -= Time.unscaledDeltaTime;
            if (_retryTimer <= 0f)
                FindServer();
        }

        public void BeginQueue()
        {
            if (Busy || InQueue)
                return;

            ProductUserId local = LocalUserId;
            if (local == null || !local.IsValid())
            {
                Status = "Online services are not ready.";
                return;
            }

            ClearState(false);
            _generation++;
            Busy = true;
            Status = "Finding a Brick Battle 99 server...";
            FindServer();
        }

        private void FindServer()
        {
            if (_searching)
                return;

            _searching = true;
            int generation = _generation;
            _sessionService.FindAvailableServer(1, (success, error, endpoint) =>
            {
                if (generation != _generation)
                    return;
                _searching = false;
                if (!success || endpoint == null || endpoint.ServerUserId == null || !endpoint.ServerUserId.IsValid())
                {
                    Status = string.IsNullOrWhiteSpace(error) ? "No Battle 99 server is available." : error;
                    _retryTimer = 2f;
                    return;
                }

                _dedicatedSessionId = endpoint.SessionId ?? string.Empty;
                _dedicatedServerUserId = endpoint.ServerUserId;
                Status = "Joining Battle 99 server...";

                _sessionService.JoinServerSession(_dedicatedSessionId, (joined, joinError) =>
                {
                    if (generation != _generation)
                    {
                        if (joined) _sessionService.LeaveJoinedSession();
                        return;
                    }
                    if (!joined)
                    {
                        Status = joinError;
                        _dedicatedSessionId = string.Empty;
                        _dedicatedServerUserId = null;
                        _retryTimer = 2f;
                        return;
                    }

                    string name = string.IsNullOrWhiteSpace(PongSettings.PlayerName) ? "Player" : PongSettings.PlayerName;
                    _dedicatedClient.ConnectToServer(
                        _dedicatedServerUserId,
                        _dedicatedSessionId,
                        name,
                        null,
                        (connected, connectError) =>
                        {
                            if (generation != _generation)
                            {
                                if (connected) _dedicatedClient.StopPeer();
                                return;
                            }
                            if (!connected)
                            {
                                Status = connectError;
                                _sessionService.LeaveJoinedSession();
                                _dedicatedSessionId = string.Empty;
                                _dedicatedServerUserId = null;
                                _retryTimer = 2f;
                                return;
                            }

                            InQueue = true;
                            Busy = false;
                            LocalReady = false;
                            Status = "Waiting for players.";
                        });
                });
            });
        }

        public void ToggleReady()
        {
            if (!InQueue || MatchStarted || Busy || _dedicatedClient == null || !_dedicatedClient.Connected)
                return;

            _dedicatedClient.SetReady(!LocalReady);
        }

        private void OnQueueUpdated(IReadOnlyList<Battle99Member> members)
        {
            _members.Clear();
            LocalReady = false;
            ProductUserId local = LocalUserId;
            string localId = local != null && local.IsValid() ? local.ToString() : string.Empty;

            if (members != null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    Battle99Member source = members[i];
                    Battle99Member copy = new Battle99Member
                    {
                        UserId = source.UserId,
                        Name = source.Name,
                        Ready = source.Ready
                    };
                    _members.Add(copy);
                    if (copy.UserId != null && copy.UserId.IsValid() && copy.UserId.ToString() == localId)
                        LocalReady = copy.Ready;
                }
            }

            if (InQueue && !MatchStarted)
                Status = _members.Count >= PongConfig.Battle99MinPlayers
                    ? "Ready when everyone is ready."
                    : "Waiting for at least 2 players.";
        }

        private void CopyMatchRoster()
        {
            _matchRoster.Clear();
            for (int i = 0; i < _members.Count; i++)
            {
                Battle99Member source = _members[i];
                _matchRoster.Add(new Battle99Member
                {
                    UserId = source.UserId,
                    Name = source.Name,
                    Ready = true
                });
            }
        }

        public void Leave()
        {
            _generation++;
            _dedicatedClient?.StopPeer();
            _sessionService?.LeaveJoinedSession();
            ClearState(true);
        }

        private void ClearState(bool clearStatus)
        {
            Busy = false;
            InQueue = false;
            MatchStarted = false;
            LocalReady = false;
            _searching = false;
            _retryTimer = 0f;
            _dedicatedSessionId = string.Empty;
            _dedicatedServerUserId = null;
            _members.Clear();
            _matchRoster.Clear();
            if (clearStatus)
                Status = string.Empty;
        }

        private void OnDestroy()
        {
            if (_dedicatedClient != null)
                _dedicatedClient.QueueUpdated -= OnQueueUpdated;
        }
    }
}
