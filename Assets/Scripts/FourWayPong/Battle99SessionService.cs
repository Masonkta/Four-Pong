using System;
using Epic.OnlineServices;
using Epic.OnlineServices.Sessions;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;

namespace FourWayPong
{
    public sealed class Battle99ServerEndpoint
    {
        public string SessionId;
        public ProductUserId ServerUserId;
        public uint OpenSlots;
    }

    /// <summary>
    /// EOS Sessions discovery/registration for dedicated Brick Battle 99 servers.
    /// The dedicated server itself owns the waiting queue, so the mode can exceed
    /// the 64-member EOS Lobby ceiling and use the configured 99-player capacity.
    /// </summary>
    public sealed class Battle99SessionService : MonoBehaviour
    {
        public const string ServerBucketId = "FourWayPong-B99-Server-v1";
        public const string ModeKey = "FWP_B99_SERVER_MODE";
        public const string StateKey = "FWP_B99_SERVER_STATE";
        public const string ModeValue = "BRICK99";
        public const string AvailableValue = "AVAILABLE";
        public const string BusyValue = "BUSY";

        private SessionsInterface _sessions;
        private SessionSearch _search;
        private Action<bool, string, Battle99ServerEndpoint> _findCallback;
        private Action<bool, string> _joinCallback;
        private Action<bool, string> _advertiseCallback;
        private string _serverSessionName;
        private string _serverSessionId;
        private string _joinedSessionName;

        public string ServerSessionId => _serverSessionId;
        public string ServerSessionName => _serverSessionName;
        public string JoinedSessionName => _joinedSessionName;

        private SessionsInterface Sessions
        {
            get
            {
                if (_sessions == null && EOSManager.Instance != null && EOSManager.Instance.GetEOSPlatformInterface() != null)
                    _sessions = EOSManager.Instance.GetEOSPlatformInterface().GetSessionsInterface();
                return _sessions;
            }
        }

        private ProductUserId LocalUser => EOSManager.Instance != null ? EOSManager.Instance.GetProductUserId() : null;

        public void FindAvailableServer(int requiredSlots, Action<bool, string, Battle99ServerEndpoint> callback)
        {
            ReleaseSearch();
            _findCallback = callback;

            if (Sessions == null || LocalUser == null || !LocalUser.IsValid())
            {
                FinishFind(false, "EOS Sessions are not ready.", null);
                return;
            }

            CreateSessionSearchOptions createOptions = new CreateSessionSearchOptions
            {
                MaxSearchResults = 50
            };
            Result result = Sessions.CreateSessionSearch(ref createOptions, out _search);
            if (result != Result.Success || _search == null)
            {
                FinishFind(false, "Could not create server search: " + result, null);
                return;
            }

            if (!SetSearchParameter(_search, SessionsInterface.SEARCH_BUCKET_ID.ToString(), ServerBucketId) ||
                !SetSearchParameter(_search, ModeKey, ModeValue) ||
                !SetSearchParameter(_search, StateKey, AvailableValue) ||
                !SetSearchParameter(_search, SessionsInterface.SEARCH_MINSLOTSAVAILABLE.ToString(), (long)Mathf.Max(1, requiredSlots)))
            {
                FinishFind(false, "Could not configure dedicated server search.", null);
                return;
            }

            SessionSearchFindOptions findOptions = new SessionSearchFindOptions { LocalUserId = LocalUser };
            _search.Find(ref findOptions, null, OnFindComplete);
        }

        private static bool SetSearchParameter(SessionSearch search, string key, string value)
        {
            AttributeData data = new AttributeData { Key = key, Value = value };
            SessionSearchSetParameterOptions options = new SessionSearchSetParameterOptions
            {
                Parameter = data,
                ComparisonOp = ComparisonOp.Equal
            };
            return search.SetParameter(ref options) == Result.Success;
        }

        private static bool SetSearchParameter(SessionSearch search, string key, long value)
        {
            AttributeData data = new AttributeData { Key = key, Value = value };
            SessionSearchSetParameterOptions options = new SessionSearchSetParameterOptions
            {
                Parameter = data,
                ComparisonOp = ComparisonOp.Greaterthanorequal
            };
            return search.SetParameter(ref options) == Result.Success;
        }

        private void OnFindComplete(ref SessionSearchFindCallbackInfo info)
        {
            if (info.ResultCode != Result.Success || _search == null)
            {
                FinishFind(false, "No dedicated Battle 99 server is available.", null);
                return;
            }

            SessionSearchGetSearchResultCountOptions countOptions = new SessionSearchGetSearchResultCountOptions();
            uint count = _search.GetSearchResultCount(ref countOptions);
            for (uint i = 0; i < count; i++)
            {
                SessionSearchCopySearchResultByIndexOptions copyOptions = new SessionSearchCopySearchResultByIndexOptions { SessionIndex = i };
                Result copyResult = _search.CopySearchResultByIndex(ref copyOptions, out SessionDetails details);
                if (copyResult != Result.Success || details == null)
                    continue;

                try
                {
                    SessionDetailsCopyInfoOptions infoOptions = new SessionDetailsCopyInfoOptions();
                    if (details.CopyInfo(ref infoOptions, out SessionDetailsInfo? sessionInfo) != Result.Success || !sessionInfo.HasValue)
                        continue;

                    SessionDetailsInfo value = sessionInfo.Value;
                    if (value.OwnerUserId == null || !value.OwnerUserId.IsValid() || value.NumOpenPublicConnections == 0)
                        continue;
                    if (LocalUser != null && LocalUser.IsValid() && value.OwnerUserId == LocalUser)
                        continue;

                    Battle99ServerEndpoint endpoint = new Battle99ServerEndpoint
                    {
                        SessionId = value.SessionId.ToString(),
                        ServerUserId = value.OwnerUserId,
                        OpenSlots = value.NumOpenPublicConnections
                    };
                    FinishFind(true, string.Empty, endpoint);
                    return;
                }
                finally
                {
                    details.Release();
                }
            }

            FinishFind(false, "No dedicated Battle 99 server is available.", null);
        }

        private void FinishFind(bool success, string error, Battle99ServerEndpoint endpoint)
        {
            Action<bool, string, Battle99ServerEndpoint> callback = _findCallback;
            _findCallback = null;
            ReleaseSearch();
            callback?.Invoke(success, error, endpoint);
        }

        public void JoinServerSession(string sessionId, Action<bool, string> callback)
        {
            _joinCallback = callback;
            ReleaseSearch();

            if (string.IsNullOrWhiteSpace(sessionId) || Sessions == null || LocalUser == null || !LocalUser.IsValid())
            {
                FinishJoin(false, "Dedicated server session is not ready.");
                return;
            }

            CreateSessionSearchOptions createOptions = new CreateSessionSearchOptions { MaxSearchResults = 1 };
            Result result = Sessions.CreateSessionSearch(ref createOptions, out _search);
            if (result != Result.Success || _search == null)
            {
                FinishJoin(false, "Could not find dedicated server session: " + result);
                return;
            }

            SessionSearchSetSessionIdOptions idOptions = new SessionSearchSetSessionIdOptions { SessionId = sessionId };
            result = _search.SetSessionId(ref idOptions);
            if (result != Result.Success)
            {
                FinishJoin(false, "Could not select dedicated server session: " + result);
                return;
            }

            SessionSearchFindOptions findOptions = new SessionSearchFindOptions { LocalUserId = LocalUser };
            _search.Find(ref findOptions, null, OnJoinSearchComplete);
        }

        private void OnJoinSearchComplete(ref SessionSearchFindCallbackInfo info)
        {
            if (info.ResultCode != Result.Success || _search == null)
            {
                FinishJoin(false, "Dedicated server session is no longer available.");
                return;
            }

            SessionSearchGetSearchResultCountOptions countOptions = new SessionSearchGetSearchResultCountOptions();
            if (_search.GetSearchResultCount(ref countOptions) == 0)
            {
                FinishJoin(false, "Dedicated server session was not found.");
                return;
            }

            SessionSearchCopySearchResultByIndexOptions copyOptions = new SessionSearchCopySearchResultByIndexOptions { SessionIndex = 0 };
            Result result = _search.CopySearchResultByIndex(ref copyOptions, out SessionDetails details);
            if (result != Result.Success || details == null)
            {
                FinishJoin(false, "Could not open dedicated server session: " + result);
                return;
            }

            _joinedSessionName = "B99Client" + Guid.NewGuid().ToString("N").Substring(0, 12);
            JoinSessionOptions joinOptions = new JoinSessionOptions
            {
                SessionHandle = details,
                SessionName = _joinedSessionName,
                LocalUserId = LocalUser,
                PresenceEnabled = false
            };

            Sessions.JoinSession(ref joinOptions, details, OnJoinComplete);
        }

        private void OnJoinComplete(ref JoinSessionCallbackInfo info)
        {
            SessionDetails details = info.ClientData as SessionDetails;
            details?.Release();

            if (info.ResultCode != Result.Success)
            {
                _joinedSessionName = string.Empty;
                FinishJoin(false, "Could not join dedicated server session: " + info.ResultCode);
                return;
            }

            FinishJoin(true, string.Empty);
        }

        private void FinishJoin(bool success, string error)
        {
            Action<bool, string> callback = _joinCallback;
            _joinCallback = null;
            ReleaseSearch();
            callback?.Invoke(success, error);
        }

        public void LeaveJoinedSession()
        {
            if (Sessions == null || string.IsNullOrEmpty(_joinedSessionName))
                return;

            DestroySessionOptions options = new DestroySessionOptions { SessionName = _joinedSessionName };
            Sessions.DestroySession(ref options, null, OnJoinedSessionDestroyed);
            _joinedSessionName = string.Empty;
        }

        private void OnJoinedSessionDestroyed(ref DestroySessionCallbackInfo info)
        {
            if (info.ResultCode != Result.Success && info.ResultCode != Result.NotFound)
                Debug.LogWarning("Battle99 joined EOS Session cleanup returned " + info.ResultCode);
        }

        public void AdvertiseDedicatedServer(int maxPlayers, Action<bool, string> callback)
        {
            _advertiseCallback = callback;
            if (Sessions == null || LocalUser == null || !LocalUser.IsValid())
            {
                FinishAdvertise(false, "EOS Sessions are not ready for the dedicated server.");
                return;
            }

            _serverSessionName = "B99Server" + Guid.NewGuid().ToString("N").Substring(0, 12);
            CreateSessionModificationOptions createOptions = new CreateSessionModificationOptions
            {
                SessionName = _serverSessionName,
                BucketId = ServerBucketId,
                MaxPlayers = (uint)Mathf.Clamp(maxPlayers, 2, SessionsInterface.MAXREGISTEREDPLAYERS),
                LocalUserId = LocalUser,
                PresenceEnabled = false,
                SanctionsEnabled = false,
                AllowedPlatformIds = null
            };

            Result result = Sessions.CreateSessionModification(ref createOptions, out SessionModification modification);
            if (result != Result.Success || modification == null)
            {
                FinishAdvertise(false, "Could not create dedicated server EOS Session: " + result);
                return;
            }

            SessionModificationSetPermissionLevelOptions permissionOptions = new SessionModificationSetPermissionLevelOptions
            {
                PermissionLevel = OnlineSessionPermissionLevel.PublicAdvertised
            };
            modification.SetPermissionLevel(ref permissionOptions);

            SessionModificationSetJoinInProgressAllowedOptions joinOptions = new SessionModificationSetJoinInProgressAllowedOptions
            {
                AllowJoinInProgress = true
            };
            modification.SetJoinInProgressAllowed(ref joinOptions);

            SessionModificationSetInvitesAllowedOptions inviteOptions = new SessionModificationSetInvitesAllowedOptions
            {
                InvitesAllowed = false
            };
            modification.SetInvitesAllowed(ref inviteOptions);

            AddAdvertisedAttribute(modification, ModeKey, ModeValue);
            AddAdvertisedAttribute(modification, StateKey, AvailableValue);

            UpdateSessionOptions updateOptions = new UpdateSessionOptions { SessionModificationHandle = modification };
            Sessions.UpdateSession(ref updateOptions, modification, OnServerAdvertised);
        }

        private void OnServerAdvertised(ref UpdateSessionCallbackInfo info)
        {
            SessionModification modification = info.ClientData as SessionModification;
            modification?.Release();

            if (info.ResultCode != Result.Success)
            {
                FinishAdvertise(false, "Dedicated server Session advertisement failed: " + info.ResultCode);
                return;
            }

            _serverSessionId = info.SessionId.ToString();
            FinishAdvertise(true, string.Empty);
        }

        private void FinishAdvertise(bool success, string error)
        {
            Action<bool, string> callback = _advertiseCallback;
            _advertiseCallback = null;
            callback?.Invoke(success, error);
        }


        public void RegisterServerPlayer(ProductUserId user)
        {
            if (Sessions == null || string.IsNullOrEmpty(_serverSessionName) || user == null || !user.IsValid())
                return;

            RegisterPlayersOptions options = new RegisterPlayersOptions
            {
                SessionName = _serverSessionName,
                PlayersToRegister = new[] { user }
            };
            Sessions.RegisterPlayers(ref options, null, OnRegisterServerPlayer);
        }

        private void OnRegisterServerPlayer(ref RegisterPlayersCallbackInfo info)
        {
            if (info.ResultCode != Result.Success)
                Debug.LogWarning("Battle99 server player registration returned " + info.ResultCode);
        }

        public void UnregisterServerPlayer(ProductUserId user)
        {
            if (Sessions == null || string.IsNullOrEmpty(_serverSessionName) || user == null || !user.IsValid())
                return;

            UnregisterPlayersOptions options = new UnregisterPlayersOptions
            {
                SessionName = _serverSessionName,
                PlayersToUnregister = new[] { user }
            };
            Sessions.UnregisterPlayers(ref options, null, OnUnregisterServerPlayer);
        }

        private void OnUnregisterServerPlayer(ref UnregisterPlayersCallbackInfo info)
        {
            if (info.ResultCode != Result.Success && info.ResultCode != Result.NotFound)
                Debug.LogWarning("Battle99 server player unregister returned " + info.ResultCode);
        }

        public void SetDedicatedServerState(bool busy)
        {
            if (Sessions == null || string.IsNullOrEmpty(_serverSessionName))
                return;

            UpdateSessionModificationOptions options = new UpdateSessionModificationOptions { SessionName = _serverSessionName };
            Result result = Sessions.UpdateSessionModification(ref options, out SessionModification modification);
            if (result != Result.Success || modification == null)
                return;

            AddAdvertisedAttribute(modification, StateKey, busy ? BusyValue : AvailableValue);
            UpdateSessionOptions updateOptions = new UpdateSessionOptions { SessionModificationHandle = modification };
            Sessions.UpdateSession(ref updateOptions, modification, OnServerStateUpdated);
        }

        private void OnServerStateUpdated(ref UpdateSessionCallbackInfo info)
        {
            SessionModification modification = info.ClientData as SessionModification;
            modification?.Release();
        }

        public void DestroyDedicatedServerSession()
        {
            if (Sessions == null || string.IsNullOrEmpty(_serverSessionName))
                return;

            DestroySessionOptions options = new DestroySessionOptions { SessionName = _serverSessionName };
            Sessions.DestroySession(ref options, null, OnServerSessionDestroyed);
            _serverSessionName = string.Empty;
            _serverSessionId = string.Empty;
        }

        private void OnServerSessionDestroyed(ref DestroySessionCallbackInfo info)
        {
            if (info.ResultCode != Result.Success && info.ResultCode != Result.NotFound)
                Debug.LogWarning("Battle99 dedicated EOS Session cleanup returned " + info.ResultCode);
        }

        private static void AddAdvertisedAttribute(SessionModification modification, string key, string value)
        {
            AttributeData data = new AttributeData { Key = key, Value = value };
            SessionModificationAddAttributeOptions options = new SessionModificationAddAttributeOptions
            {
                SessionAttribute = data,
                AdvertisementType = SessionAttributeAdvertisementType.Advertise
            };
            modification.AddAttribute(ref options);
        }

        private void ReleaseSearch()
        {
            if (_search == null)
                return;
            _search.Release();
            _search = null;
        }

        private void OnDestroy()
        {
            ReleaseSearch();
        }
    }
}
