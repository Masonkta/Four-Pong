using System;
using System.Collections.Generic;
using Epic.OnlineServices;

namespace FourWayPong
{
    public interface IBattle99NetworkLink
    {
        event Action<ProductUserId, int> AttackReceived;
        event Action<int> LocalPlacementReceived;

        float PingMs { get; }
        bool Ready { get; }

        void StartPeer(ProductUserId localUser, ProductUserId coordinator, IReadOnlyList<Battle99Member> roster);
        void StopPeer();
        void SetCoordinator(ProductUserId coordinator);
        void SetRoster(IReadOnlyList<Battle99Member> roster);
        void BroadcastSnapshot(Battle99NetSnapshot snapshot);
        void SendAttack(ProductUserId target, int rows);
        List<ProductUserId> GetAliveRemoteUsers();
        int CountAliveRemotePlayers();
        bool TryGetRemoteState(ProductUserId user, out Battle99RemoteState state);
    }
}
