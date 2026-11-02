using System;
using System.Collections;
using Epic.OnlineServices;
using Epic.OnlineServices.Connect;
using Epic.OnlineServices.Lobby;
using PlayEveryWare.EpicOnlineServices;
using UnityEngine;

namespace FourWayPong
{
    /// <summary>
    /// Handles the tiny amount of EOS backend state Four-Way Pong needs:
    /// anonymous Device-ID Connect login plus a 6-digit join-code lobby.
    /// Gameplay data itself travels through EOSP2PTransport, not through Lobby.
    /// </summary>
    public sealed class EOSRoomService : MonoBehaviour
    {
        private const string LobbyPrefix = "FWP-";
        private const string CodeDigits = "23456789";
        private const int CodeLength = 6;

        private Action<bool, string> _createCallback;
        private Action<bool, string, ProductUserId> _joinCallback;
        private string _pendingCode;
        private int _createAttempts;

        public bool Ready { get; private set; }
        public bool Busy { get; private set; }
        public bool IsRoomHost { get; private set; }
        public string CurrentRoomCode { get; private set; } = string.Empty;
        public string CurrentLobbyId { get; private set; } = string.Empty;
        public ProductUserId CurrentHostUserId { get; private set; }
        public string Status { get; private set; } = "Starting Epic Online Services...";

        private void Start()
        {
            StartCoroutine(BeginWhenEOSExists());
        }

        private IEnumerator BeginWhenEOSExists()
        {
            float timeout = Time.realtimeSinceStartup + 5f;
            while ((EOSManager.Instance == null || EOSManager.Instance.GetEOSPlatformInterface() == null) && Time.realtimeSinceStartup < timeout)
                yield return null;

            if (EOSManager.Instance == null || EOSManager.Instance.GetEOSPlatformInterface() == null)
            {
                Status = "EOS is not configured yet. In Unity use EOS Plugin > EOS Configuration, then restart Play Mode.";
                yield break;
            }

            ProductUserId existing = EOSManager.Instance.GetProductUserId();
            if (existing != null && existing.IsValid())
            {
                Ready = true;
                Status = "Online services ready.";
                yield break;
            }

            CreateOrReuseDeviceId();
        }

        private void CreateOrReuseDeviceId()
        {
            if (EOSManager.Instance == null || EOSManager.Instance.GetEOSConnectInterface() == null)
            {
                Status = "EOS Connect is unavailable. Check the EOS product configuration.";
                return;
            }

            Busy = true;
            Status = "Signing in anonymously...";
            var connect = EOSManager.Instance.GetEOSConnectInterface();
            CreateDeviceIdOptions options = new CreateDeviceIdOptions
            {
                DeviceModel = Application.productName + " Desktop"
            };
            connect.CreateDeviceId(ref options, null, OnDeviceIdCreated);
        }

        private void OnDeviceIdCreated(ref CreateDeviceIdCallbackInfo info)
        {
            if (info.ResultCode != Result.Success && info.ResultCode != Result.DuplicateNotAllowed)
            {
                Busy = false;
                Status = "Could not create/reuse the EOS Device ID: " + info.ResultCode;
                return;
            }

            EOSManager.Instance.StartConnectLoginWithOptions(
                ExternalCredentialType.DeviceidAccessToken,
                null,
                "PongPlayer",
                onloginCallback: OnConnectLogin);
        }

        private void OnConnectLogin(LoginCallbackInfo info)
        {
            if (info.ResultCode == Result.Success)
            {
                FinishLogin();
                return;
            }

            if (info.ResultCode == Result.InvalidUser)
            {
                EOSManager.Instance.CreateConnectUserWithContinuanceToken(info.ContinuanceToken, OnConnectUserCreated);
                return;
            }

            Busy = false;
            Status = "EOS anonymous login failed: " + info.ResultCode;
        }

        private void OnConnectUserCreated(CreateUserCallbackInfo info)
        {
            if (info.ResultCode == Result.Success)
            {
                FinishLogin();
                return;
            }

            Busy = false;
            Status = "EOS could not create the anonymous player identity: " + info.ResultCode;
        }

        private void FinishLogin()
        {
            ProductUserId user = EOSManager.Instance.GetProductUserId();
            Ready = user != null && user.IsValid();
            Busy = false;
            Status = Ready ? "Online services ready." : "EOS login completed without a valid Product User ID.";
        }

        public void CreateRoom(Action<bool, string> callback)
        {
            if (!Ready || Busy)
            {
                callback?.Invoke(false, Ready ? "Online services are busy." : Status);
                return;
            }

            LeaveRoom(false);
            _createCallback = callback;
            _createAttempts = 0;
            Busy = true;
            TryCreateRoom();
        }

        private void TryCreateRoom()
        {
            _createAttempts++;
            _pendingCode = GenerateRoomCode();
            string lobbyId = ToLobbyId(_pendingCode);
            ProductUserId localUser = EOSManager.Instance.GetProductUserId();

            CreateLobbyOptions options = new CreateLobbyOptions
            {
                LocalUserId = localUser,
                MaxLobbyMembers = PongConfig.MaxPlayers,
                PermissionLevel = LobbyPermissionLevel.Publicadvertised,
                PresenceEnabled = false,
                AllowInvites = true,
                BucketId = "FourWayPong-v1",
                DisableHostMigration = true,
                EnableRTCRoom = false,
                LocalRTCOptions = null,
                LobbyId = lobbyId,
                EnableJoinById = true,
                RejoinAfterKickRequiresInvite = false,
                AllowedPlatformIds = null,
                CrossplayOptOut = false
            };

            Status = "Creating room " + _pendingCode + "...";
            EOSManager.Instance.GetEOSLobbyInterface().CreateLobby(ref options, null, OnLobbyCreated);
        }

        private void OnLobbyCreated(ref CreateLobbyCallbackInfo info)
        {
            if (info.ResultCode != Result.Success)
            {
                // A custom lobby ID has to be globally unique. A random collision is rare,
                // but retrying gives Host the instant-code UX without exposing that detail.
                if (_createAttempts < 4 && info.ResultCode == Result.DuplicateNotAllowed)
                {
                    TryCreateRoom();
                    return;
                }

                Busy = false;
                Status = "Room creation failed: " + info.ResultCode;
                Action<bool, string> callback = _createCallback;
                _createCallback = null;
                callback?.Invoke(false, Status);
                return;
            }

            CurrentRoomCode = _pendingCode;
            CurrentLobbyId = info.LobbyId.ToString();
            if (string.IsNullOrWhiteSpace(CurrentLobbyId))
                CurrentLobbyId = ToLobbyId(CurrentRoomCode);
            IsRoomHost = true;
            CurrentHostUserId = EOSManager.Instance.GetProductUserId();
            Busy = false;
            Status = "Room ready. Code: " + CurrentRoomCode;

            Action<bool, string> done = _createCallback;
            _createCallback = null;
            done?.Invoke(true, string.Empty);
        }

        public void JoinRoom(string code, Action<bool, string, ProductUserId> callback)
        {
            if (!Ready || Busy)
            {
                callback?.Invoke(false, Ready ? "Online services are busy." : Status, null);
                return;
            }

            string normalized = NormalizeRoomCode(code);
            if (!IsValidRoomCode(normalized))
            {
                callback?.Invoke(false, "Enter the 6-digit room code from the host.", null);
                return;
            }

            LeaveRoom(false);
            _joinCallback = callback;
            _pendingCode = normalized;
            Busy = true;
            Status = "Joining room " + normalized + "...";

            JoinLobbyByIdOptions options = new JoinLobbyByIdOptions
            {
                LobbyId = ToLobbyId(normalized),
                LocalUserId = EOSManager.Instance.GetProductUserId(),
                PresenceEnabled = false,
                LocalRTCOptions = null,
                CrossplayOptOut = false
            };
            EOSManager.Instance.GetEOSLobbyInterface().JoinLobbyById(ref options, null, OnLobbyJoined);
        }

        private void OnLobbyJoined(ref JoinLobbyByIdCallbackInfo info)
        {
            if (info.ResultCode != Result.Success)
            {
                Busy = false;
                Status = "Could not join that room: " + info.ResultCode;
                Action<bool, string, ProductUserId> callback = _joinCallback;
                _joinCallback = null;
                callback?.Invoke(false, Status, null);
                return;
            }

            CurrentRoomCode = _pendingCode;
            CurrentLobbyId = info.LobbyId.ToString();
            if (string.IsNullOrWhiteSpace(CurrentLobbyId))
                CurrentLobbyId = ToLobbyId(CurrentRoomCode);
            IsRoomHost = false;

            if (!TryResolveLobbyOwner(out ProductUserId owner, out string error))
            {
                Busy = false;
                Status = error;
                Action<bool, string, ProductUserId> failed = _joinCallback;
                _joinCallback = null;
                failed?.Invoke(false, error, null);
                return;
            }

            CurrentHostUserId = owner;
            Busy = false;
            Status = "Room found. Connecting to host...";
            Action<bool, string, ProductUserId> done = _joinCallback;
            _joinCallback = null;
            done?.Invoke(true, string.Empty, owner);
        }

        private bool TryResolveLobbyOwner(out ProductUserId owner, out string error)
        {
            owner = null;
            error = string.Empty;
            LobbyInterface lobby = EOSManager.Instance.GetEOSLobbyInterface();
            CopyLobbyDetailsHandleOptions handleOptions = new CopyLobbyDetailsHandleOptions
            {
                LobbyId = CurrentLobbyId,
                LocalUserId = EOSManager.Instance.GetProductUserId()
            };

            Result handleResult = lobby.CopyLobbyDetailsHandle(ref handleOptions, out LobbyDetails details);
            if (handleResult != Result.Success || details == null)
            {
                error = "Joined the room, but could not read its host: " + handleResult;
                return false;
            }

            try
            {
                LobbyDetailsCopyInfoOptions infoOptions = new LobbyDetailsCopyInfoOptions();
                Result infoResult = details.CopyInfo(ref infoOptions, out LobbyDetailsInfo? info);
                if (infoResult != Result.Success || !info.HasValue || info.Value.LobbyOwnerUserId == null || !info.Value.LobbyOwnerUserId.IsValid())
                {
                    error = "Joined the room, but its host identity was unavailable: " + infoResult;
                    return false;
                }

                owner = info.Value.LobbyOwnerUserId;
                return true;
            }
            finally
            {
                details.Release();
            }
        }

        public void LeaveRoom(bool updateStatus = true)
        {
            if (string.IsNullOrWhiteSpace(CurrentLobbyId) || EOSManager.Instance == null || EOSManager.Instance.GetEOSLobbyInterface() == null)
            {
                ClearRoomState(updateStatus);
                return;
            }

            ProductUserId localUser = EOSManager.Instance.GetProductUserId();
            LobbyInterface lobby = EOSManager.Instance.GetEOSLobbyInterface();
            if (IsRoomHost)
            {
                DestroyLobbyOptions options = new DestroyLobbyOptions
                {
                    LocalUserId = localUser,
                    LobbyId = CurrentLobbyId
                };
                lobby.DestroyLobby(ref options, null, OnLobbyDestroyed);
            }
            else
            {
                LeaveLobbyOptions options = new LeaveLobbyOptions
                {
                    LocalUserId = localUser,
                    LobbyId = CurrentLobbyId
                };
                lobby.LeaveLobby(ref options, null, OnLobbyLeft);
            }

            ClearRoomState(updateStatus);
        }

        private void OnLobbyDestroyed(ref DestroyLobbyCallbackInfo info)
        {
            if (info.ResultCode != Result.Success)
                Debug.LogWarning("EOS room cleanup returned " + info.ResultCode);
        }

        private void OnLobbyLeft(ref LeaveLobbyCallbackInfo info)
        {
            if (info.ResultCode != Result.Success)
                Debug.LogWarning("EOS lobby leave returned " + info.ResultCode);
        }

        private void ClearRoomState(bool updateStatus)
        {
            CurrentRoomCode = string.Empty;
            CurrentLobbyId = string.Empty;
            CurrentHostUserId = null;
            IsRoomHost = false;
            _pendingCode = string.Empty;
            if (updateStatus && Ready)
                Status = "Online services ready.";
        }

        private static string GenerateRoomCode()
        {
            // Numeric-only codes cannot accidentally spell profanity or offensive words.
            // 2-9 also avoids visually ambiguous 0 and 1.
            char[] code = new char[CodeLength];
            for (int i = 0; i < code.Length; i++)
                code[i] = CodeDigits[UnityEngine.Random.Range(0, CodeDigits.Length)];
            return new string(code);
        }

        public static bool IsValidRoomCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != CodeLength)
                return false;

            for (int i = 0; i < code.Length; i++)
            {
                if (CodeDigits.IndexOf(code[i]) < 0)
                    return false;
            }

            return true;
        }

        public static string NormalizeRoomCode(string value)
        {
            string code = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
            if (code.StartsWith(LobbyPrefix, StringComparison.OrdinalIgnoreCase))
                code = code.Substring(LobbyPrefix.Length);
            code = code.Replace(" ", string.Empty).Replace("-", string.Empty);
            return code;
        }

        private static string ToLobbyId(string roomCode)
        {
            return LobbyPrefix + NormalizeRoomCode(roomCode);
        }
    }
}
