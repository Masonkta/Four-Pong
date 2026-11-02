using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace FourWayPong
{
    public static class Battle99DedicatedProtocol
    {
        public const string Hello = "FWP_B99_HELLO";
        public const string QueueState = "FWP_B99_QUEUE";
        public const string QueueMember = "FWP_B99_MEMBER";
        public const string QueueRemove = "FWP_B99_REMOVE";
        public const string ReadyState = "FWP_B99_READY";
        public const string Start = "FWP_B99_START";
        public const string Snapshot = "FWP_B99_SNAPSHOT";
        public const string AttackRequest = "FWP_B99_ATTACK_REQ";
        public const string Attack = "FWP_B99_ATTACK";
        public const string Placement = "FWP_B99_PLACEMENT";
        public const string Ping = "FWP_B99_PING";
        public const string Pong = "FWP_B99_PONG";
        public const string ServerStatus = "FWP_B99_SERVER_STATUS";

        public static void WriteSnapshot(ref FastBufferWriter writer, Battle99NetSnapshot snapshot)
        {
            writer.WriteValueSafe(snapshot.Sequence);
            writer.WriteValueSafe(snapshot.Alive);
            writer.WriteValueSafe(snapshot.PaddleNormalized);
            writer.WriteValueSafe(snapshot.PaddleScale);
            writer.WriteValueSafe(snapshot.BallMask);
            WriteVector2(ref writer, snapshot.Ball0);
            WriteVector2(ref writer, snapshot.Ball1);
            WriteVector2(ref writer, snapshot.Ball2);
            WriteVector2(ref writer, snapshot.Ball3);
            writer.WriteValueSafe(snapshot.BrickLow);
            writer.WriteValueSafe(snapshot.BrickMid);
            writer.WriteValueSafe(snapshot.BrickHigh);
            writer.WriteValueSafe(snapshot.PendingGarbage);
        }

        public static void ReadSnapshot(ref FastBufferReader reader, out Battle99NetSnapshot snapshot)
        {
            snapshot = default;
            reader.ReadValueSafe(out snapshot.Sequence);
            reader.ReadValueSafe(out snapshot.Alive);
            reader.ReadValueSafe(out snapshot.PaddleNormalized);
            reader.ReadValueSafe(out snapshot.PaddleScale);
            reader.ReadValueSafe(out snapshot.BallMask);
            snapshot.Ball0 = ReadVector2(ref reader);
            snapshot.Ball1 = ReadVector2(ref reader);
            snapshot.Ball2 = ReadVector2(ref reader);
            snapshot.Ball3 = ReadVector2(ref reader);
            reader.ReadValueSafe(out snapshot.BrickLow);
            reader.ReadValueSafe(out snapshot.BrickMid);
            reader.ReadValueSafe(out snapshot.BrickHigh);
            reader.ReadValueSafe(out snapshot.PendingGarbage);
        }

        public static void WriteString(ref FastBufferWriter writer, string value)
        {
            FixedString128Bytes fixedValue = new FixedString128Bytes(value ?? string.Empty);
            writer.WriteValueSafe(fixedValue);
        }

        public static string ReadString(ref FastBufferReader reader)
        {
            reader.ReadValueSafe(out FixedString128Bytes value);
            return value.ToString();
        }

        private static void WriteVector2(ref FastBufferWriter writer, Vector2 value)
        {
            writer.WriteValueSafe(value.x);
            writer.WriteValueSafe(value.y);
        }

        private static Vector2 ReadVector2(ref FastBufferReader reader)
        {
            reader.ReadValueSafe(out float x);
            reader.ReadValueSafe(out float y);
            return new Vector2(x, y);
        }
    }
}
