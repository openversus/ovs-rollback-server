// MessageSerializer.cs
using OVS.Rollback.Models;
using OVS.Rollback.Utils;
using Rollback.Core;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace OVS
{
    public readonly record struct ClientMessageComplete(ClientHeader Header, object Payload);

    public static class MessageSerializer
    {
        private const int HeaderSize = 5; // 1 type + 4 sequence
        private static readonly ushort[] PlayerConfigValues = [0, 256, 513, 769];

        public static ClientMessageComplete? ParseClientMessage(ReadOnlySpan<byte> buffer)
        {
            if (buffer.Length < HeaderSize) return null;

            int o = 0;
            var header = new ClientHeader {
                Type = (ClientMessageType)buffer[o++],
                Sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer[o..])
            };
            o += 4;

            object? payload = header.Type switch {
                ClientMessageType.NewConnection => ReadNewConnection(buffer, ref o),
                ClientMessageType.Input => ReadInput(buffer, ref o),
                ClientMessageType.PlayerInputAck => ReadPlayerInputAck(buffer, ref o),
                ClientMessageType.MatchResult => ReadMatchResult(buffer, ref o),
                ClientMessageType.QualityData => ReadQualityData(buffer, ref o),
                ClientMessageType.Disconnecting => ReadDisconnecting(buffer, ref o),
                ClientMessageType.PlayerDisconnectedAck => ReadPlayerDisconnectedAck(buffer, ref o),
                ClientMessageType.ReadyToStartMatch => ReadReadyToStart(buffer, ref o),
                _ => null
            };

            return payload is null ? null : new ClientMessageComplete(header, payload);
        }

        private static NewConnectionPayload ReadNewConnection(ReadOnlySpan<byte> buf, ref int o)
        {
            var p = new NewConnectionPayload();
            p.MessageVersion = BinaryPrimitives.ReadUInt16LittleEndian(buf[o..]); o += 2;
            p.PlayerData.TeamId = BinaryPrimitives.ReadUInt16LittleEndian(buf[o..]); o += 2;
            p.PlayerData.PlayerIndex = BinaryPrimitives.ReadUInt16LittleEndian(buf[o..]); o += 2;
            p.MatchData.MatchId = ReadFixedString(buf, ref o, 25);
            p.MatchData.Key = ReadFixedString(buf, ref o, 45);
            p.MatchData.EnvironmentId = ReadFixedString(buf, ref o, 25);
            return p;
        }

        private static InputPayload ReadInput(ReadOnlySpan<byte> buf, ref int o)
        {
            var p = new InputPayload();
            p.StartFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]); o += 4;
            p.ClientFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]); o += 4;
            p.NumFrames = buf[o++];
            p.NumChecksums = buf[o++];

            for (int i = 0; i < p.NumFrames && o + 4 <= buf.Length; i++)
            {
                p.InputPerFrame.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]));
                o += 4;
            }
            for (int i = 0; i < p.NumChecksums && o + 4 <= buf.Length; i++)
            {
                p.ChecksumPerFrame.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]));
                o += 4;
            }
            return p;
        }

        private static PlayerInputAckPayload ReadPlayerInputAck(ReadOnlySpan<byte> buf, ref int o)
        {
            var p = new PlayerInputAckPayload { NumPlayers = buf[o++] };
            for (int i = 0; i < p.NumPlayers && o + 4 <= buf.Length; i++)
            {
                p.AckFrame.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]));
                o += 4;
            }
            p.ServerMessageSequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]);
            return p;
        }

        private static MatchResultPayload ReadMatchResult(ReadOnlySpan<byte> buf, ref int o)
        {
            var p = new MatchResultPayload { NumPlayers = buf[o++] };
            p.LastFrameChecksum = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]); o += 4;
            p.WinningTeamIndex = buf[o++];
            return p;
        }

        private static QualityDataPayload ReadQualityData(ReadOnlySpan<byte> buf, ref int o)
            => new() { ServerMessageSequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]) };

        private static DisconnectingPayload ReadDisconnecting(ReadOnlySpan<byte> buf, ref int o)
            => new() { Reason = buf[o++] };

        private static PlayerDisconnectedAckPayload ReadPlayerDisconnectedAck(ReadOnlySpan<byte> buf, ref int o)
            => new() { PlayerDisconnectedArrayIndex = buf[o++] };

        private static ReadyToStartMatchPayload ReadReadyToStart(ReadOnlySpan<byte> buf, ref int o)
            => new() { Ready = buf[o++] };

        private static string ReadFixedString(ReadOnlySpan<byte> buf, ref int offset, int maxLen)
        {
            int start = offset;
            int end = start;
            int limit = Math.Min(start + maxLen, buf.Length);
            while (end < limit && buf[end] != 0) end++;
            var str = Encoding.ASCII.GetString(buf[start..end]);
            offset += maxLen;
            return str;
        }

        // ═══════════════════════════════════════════
        //  Server → Client Serialization
        // ═══════════════════════════════════════════

        /// <summary>
        /// Serialize a server message. If <paramref name="tryBitPack"/> is true
        /// and the payload is a <see cref="PlayerInputPayload"/>, attempts
        /// uint16-per-input encoding. Falls back to standard uint32 on any failure.
        /// </summary>
        public static byte[] SerializeServerMessage(
            ServerHeader header, object? payload, int maxPlayers,
            bool tryBitPack = false)
        {
            // ── Bit-packed fast path (PlayerInput only) ──
            if (tryBitPack && payload is PlayerInputPayload pip)
            {
                try
                {
                    if (AllInputsFitIn16Bits(pip))
                        return BuildPlayerInputPacket(header, pip, maxPlayers, bitPacked: true);
                }
                catch
                {
                    ServerMetrics.BitPackFallbacks.Add(1);
                    // Fall through to standard path
                }
            }

            // ── Standard path ──
            int size = HeaderSize + CalcPayloadSize(payload, maxPlayers);
            var buf = new byte[size];
            int o = 0;

            buf[o++] = (byte)header.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), header.Sequence); o += 4;
            WritePayload(buf, ref o, payload, maxPlayers);

            return o < buf.Length ? buf[..o] : buf;
        }

        // ── Bit-packing helpers ──

        private static bool AllInputsFitIn16Bits(PlayerInputPayload p)
        {
            foreach (var playerInputs in p.InputPerFrame)
                foreach (var val in playerInputs)
                    if (val > ushort.MaxValue) return false;
            return true;
        }

        private static byte[] BuildPlayerInputPacket(
            ServerHeader header, PlayerInputPayload p, int mp, bool bitPacked)
        {
            int inputBytes = bitPacked ? 2 : 4;

            // Calculate total input count
            int totalInputs = 0;
            for (int i = 0; i < mp && i < p.NumFrames.Count; i++)
                totalInputs += p.NumFrames[i];

            int size = HeaderSize
                + 1                    // NumPlayers
                + mp * 4               // StartFrame[]
                + mp                   // NumFrames[]
                + 2 + 2 + 2 + 2 + 2   // overrides, ping, loss, rift
                + 4                    // ChecksumAckFrame
                + totalInputs * inputBytes;

            var buf = new byte[size];
            int o = 0;

            // Header
            buf[o++] = (byte)header.Type;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), header.Sequence); o += 4;

            // PlayerInput payload
            buf[o++] = p.NumPlayers;

            for (int i = 0; i < mp; i++)
            {
                uint sf = i < p.StartFrame.Count ? p.StartFrame[i] : 0;
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), sf); o += 4;
            }
            for (int i = 0; i < mp; i++)
                buf[o++] = i < p.NumFrames.Count ? p.NumFrames[i] : (byte)0;

            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.NumPredictedOverrides); o += 2;
            BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.NumZeroedOverrides); o += 2;
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.Ping); o += 2;
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.PacketLossPercent); o += 2;
            BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), (short)(p.Rift * 100)); o += 2;
            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.ChecksumAckFrame); o += 4;

            for (int pi = 0; pi < mp; pi++)
            {
                var arr = pi < p.InputPerFrame.Count ? p.InputPerFrame[pi] : [];
                byte nf = pi < p.NumFrames.Count ? p.NumFrames[pi] : (byte)0;
                for (int f = 0; f < nf; f++)
                {
                    uint v = f < arr.Count ? arr[f] : 0;
                    if (bitPacked)
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), (ushort)v);
                        o += 2;
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), v);
                        o += 4;
                    }
                }
            }

            return buf[..o];
        }

        // ── Standard payload sizing ──

        private static int CalcPayloadSize(object? payload, int mp) => payload switch {
            NewConnectionReplyPayload => 9,
            InputAckPayload => 4,
            RequestQualityDataPayload => 4,
            PlayerInputPayload p => 1 + mp * 4 + mp + 2 + 2 + 2 + 2 + 2 + 4
                + Enumerable.Range(0, Math.Min(mp, p.NumFrames.Count))
                    .Sum(i => p.NumFrames[i] * 4),
            PlayersStatusPayload => 1 + mp * 2,
            KickPayload => 6,
            ChecksumAckPayload => 4,
            PlayersConfigurationDataPayload => 1 + mp * 2,
            PlayerDisconnectedPayload => 8,
            ChangePortPayload => 2,
            _ => 0
        };

        // ── Standard payload writer ──

        private static void WritePayload(byte[] buf, ref int o, object? payload, int mp)
        {
            switch (payload)
            {
                case NewConnectionReplyPayload p:
                    buf[o++] = p.Success;
                    buf[o++] = p.MatchNumPlayers;
                    buf[o++] = p.PlayerIndex;
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.MatchDurationInFrames); o += 4;
                    buf[o++] = 0;
                    buf[o++] = p.IsValidationServerDebugMode;
                    break;

                case InputAckPayload p:
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.AckFrame); o += 4;
                    break;

                case PlayerInputPayload p:
                    buf[o++] = p.NumPlayers;
                    for (int i = 0; i < mp; i++)
                    {
                        uint sf = i < p.StartFrame.Count ? p.StartFrame[i] : 0;
                        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), sf); o += 4;
                    }
                    for (int i = 0; i < mp; i++)
                        buf[o++] = i < p.NumFrames.Count ? p.NumFrames[i] : (byte)0;

                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.NumPredictedOverrides); o += 2;
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.NumZeroedOverrides); o += 2;
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.Ping); o += 2;
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.PacketLossPercent); o += 2;
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), (short)(p.Rift * 100)); o += 2;
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.ChecksumAckFrame); o += 4;

                    for (int pi = 0; pi < mp; pi++)
                    {
                        var arr = pi < p.InputPerFrame.Count ? p.InputPerFrame[pi] : [];
                        byte nf = pi < p.NumFrames.Count ? p.NumFrames[pi] : (byte)0;
                        for (int f = 0; f < nf; f++)
                        {
                            uint v = f < arr.Count ? arr[f] : 0;
                            BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), v); o += 4;
                        }
                    }
                    break;

                case RequestQualityDataPayload p:
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.Ping); o += 2;
                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.PacketLossPercent); o += 2;
                    break;

                case PlayersStatusPayload p:
                    buf[o++] = p.NumPlayers;
                    for (int i = 0; i < mp; i++)
                    {
                        short avg = i < p.Status.Count ? p.Status[i].AveragePing : (short)0;
                        BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), avg); o += 2;
                    }
                    break;

                case KickPayload p:
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.Reason); o += 2;
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.Param1); o += 4;
                    break;

                case ChecksumAckPayload p:
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.AckFrame); o += 4;
                    break;

                case PlayersConfigurationDataPayload p:
                    buf[o++] = p.NumPlayers;
                    for (int i = 0; i < mp; i++)
                    {
                        ushort val = PlayerConfigValues[i % PlayerConfigValues.Length];
                        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), val); o += 2;
                    }
                    break;

                case PlayerDisconnectedPayload p:
                    buf[o++] = p.PlayerIndex;
                    buf[o++] = p.ShouldAITakeControl;
                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.AITakeControlFrame); o += 4;
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.PlayerDisconnectedArrayIndex); o += 2;
                    break;

                case ChangePortPayload p:
                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.Port); o += 2;
                    break;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        public static int SerializePlayerInputTo(
            ServerHeader header,
            PlayerInputPayload payload,
            int maxPlayers,
            Span<byte> output)
        {
            var w = new SpanWriter(output);

            // ── Header ──
            w.WriteU8((byte)header.Type);
            w.WriteU32(header.Sequence);

            // ── Body ──
            w.WriteU8(payload.NumPlayers);

            // FIX #1: Write all StartFrames first, then all NumFrames (matches standard path)
            for (int i = 0; i < maxPlayers; i++)
            {
                w.WriteU32(payload.StartFrame[i]);
            }
            for (int i = 0; i < maxPlayers; i++)
            {
                w.WriteU8(payload.NumFrames[i]);
            }

            w.WriteU16(payload.NumPredictedOverrides);
            w.WriteU16(payload.NumZeroedOverrides);
            w.WriteI16(payload.Ping);
            // FIX #2: Write PacketLossPercent as short (2 bytes), not byte
            w.WriteI16(payload.PacketLossPercent);
            // FIX #3: Write Rift as int16 scaled by 100 (matches standard path)
            w.WriteI16((short)(payload.Rift * 100));
            w.WriteU32(payload.ChecksumAckFrame);

            for (int i = 0; i < maxPlayers; i++)
            {
                var frames = payload.InputPerFrame[i];
                int count = frames.Count;
                for (int j = 0; j < count; j++)
                    w.WriteU32(frames[j]);
            }

            return w.Written;
        }
    }
}


//// MessageSerializer.cs
//using OVS.Rollback.Models;
//using System.Buffers.Binary;
//using System.Text;

//namespace OVS.Rollback.Core;

///// <summary>Parsed client message (header + typed payload).</summary>
//public readonly record struct ClientMessageComplete(ClientHeader Header, object Payload);

//public static class MessageSerializer
//{
//    private const int HeaderSize = 5; // 1 byte type + 4 bytes sequence
//    private static readonly ushort[] PlayerConfigValues = [0, 256, 513, 769];

//    // ═══════════════════════════════════════════
//    //  Client → Server Deserialization
//    // ═══════════════════════════════════════════

//    public static ClientMessageComplete? ParseClientMessage(ReadOnlySpan<byte> buffer)
//    {
//        if (buffer.Length < HeaderSize) return null;

//        int offset = 0;
//        var header = new ClientHeader
//        {
//            Type = (ClientMessageType)buffer[offset++],
//            Sequence = BinaryPrimitives.ReadUInt32LittleEndian(buffer[offset..])
//        };
//        offset += 4;

//        object? payload = header.Type switch
//        {
//            ClientMessageType.NewConnection => ReadNewConnection(buffer, ref offset),
//            ClientMessageType.Input => ReadInput(buffer, ref offset),
//            ClientMessageType.PlayerInputAck => ReadPlayerInputAck(buffer, ref offset),
//            ClientMessageType.MatchResult => ReadMatchResult(buffer, ref offset),
//            ClientMessageType.QualityData => ReadQualityData(buffer, ref offset),
//            ClientMessageType.Disconnecting => ReadDisconnecting(buffer, ref offset),
//            ClientMessageType.PlayerDisconnectedAck => ReadPlayerDisconnectedAck(buffer, ref offset),
//            ClientMessageType.ReadyToStartMatch => ReadReadyToStart(buffer, ref offset),
//            _ => null
//        };

//        return payload is null ? null : new ClientMessageComplete(header, payload);
//    }

//    private static NewConnectionPayload ReadNewConnection(ReadOnlySpan<byte> buf, ref int o)
//    {
//        var p = new NewConnectionPayload();
//        p.MessageVersion = BinaryPrimitives.ReadUInt16LittleEndian(buf[o..]); o += 2;
//        p.PlayerData.TeamId = BinaryPrimitives.ReadUInt16LittleEndian(buf[o..]); o += 2;
//        p.PlayerData.PlayerIndex = BinaryPrimitives.ReadUInt16LittleEndian(buf[o..]); o += 2;
//        p.MatchData.MatchId = ReadFixedString(buf, ref o, 25);
//        p.MatchData.Key = ReadFixedString(buf, ref o, 45);
//        p.MatchData.EnvironmentId = ReadFixedString(buf, ref o, 25);
//        return p;
//    }

//    private static InputPayload ReadInput(ReadOnlySpan<byte> buf, ref int o)
//    {
//        var p = new InputPayload();
//        p.StartFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]); o += 4;
//        p.ClientFrame = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]); o += 4;
//        p.NumFrames = buf[o++];
//        p.NumChecksums = buf[o++];

//        for (int i = 0; i < p.NumFrames && o + 4 <= buf.Length; i++)
//        {
//            p.InputPerFrame.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]));
//            o += 4;
//        }
//        for (int i = 0; i < p.NumChecksums && o + 4 <= buf.Length; i++)
//        {
//            p.ChecksumPerFrame.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]));
//            o += 4;
//        }
//        return p;
//    }

//    private static PlayerInputAckPayload ReadPlayerInputAck(ReadOnlySpan<byte> buf, ref int o)
//    {
//        var p = new PlayerInputAckPayload { NumPlayers = buf[o++] };
//        for (int i = 0; i < p.NumPlayers && o + 4 <= buf.Length; i++)
//        {
//            p.AckFrame.Add(BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]));
//            o += 4;
//        }
//        p.ServerMessageSequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]);
//        return p;
//    }

//    private static MatchResultPayload ReadMatchResult(ReadOnlySpan<byte> buf, ref int o)
//    {
//        var p = new MatchResultPayload { NumPlayers = buf[o++] };
//        p.LastFrameChecksum = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]); o += 4;
//        p.WinningTeamIndex = buf[o++];
//        return p;
//    }

//    private static QualityDataPayload ReadQualityData(ReadOnlySpan<byte> buf, ref int o)
//        => new() { ServerMessageSequenceNumber = BinaryPrimitives.ReadUInt32LittleEndian(buf[o..]) };

//    private static DisconnectingPayload ReadDisconnecting(ReadOnlySpan<byte> buf, ref int o)
//        => new() { Reason = buf[o++] };

//    private static PlayerDisconnectedAckPayload ReadPlayerDisconnectedAck(ReadOnlySpan<byte> buf, ref int o)
//        => new() { PlayerDisconnectedArrayIndex = buf[o++] };

//    private static ReadyToStartMatchPayload ReadReadyToStart(ReadOnlySpan<byte> buf, ref int o)
//        => new() { Ready = buf[o++] };

//    private static string ReadFixedString(ReadOnlySpan<byte> buf, ref int offset, int maxLen)
//    {
//        int start = offset;
//        int end = start;
//        int limit = Math.Min(start + maxLen, buf.Length);
//        while (end < limit && buf[end] != 0) end++;
//        var str = Encoding.ASCII.GetString(buf[start..end]);
//        offset += maxLen; // always advance by full field width
//        return str;
//    }

//    // ═══════════════════════════════════════════
//    //  Server → Client Serialization
//    // ═══════════════════════════════════════════

//    public static byte[] SerializeServerMessage(ServerHeader header, object? payload, int maxPlayers)
//    {
//        int size = HeaderSize + CalcPayloadSize(payload, maxPlayers);
//        var buf = new byte[size];
//        int o = 0;

//        // Header
//        buf[o++] = (byte)header.Type;
//        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), header.Sequence);
//        o += 4;

//        // Payload
//        WritePayload(buf, ref o, payload, maxPlayers);

//        return o < buf.Length ? buf[..o] : buf;
//    }

//    private static int CalcPayloadSize(object? payload, int mp) => payload switch
//    {
//        NewConnectionReplyPayload => 9,
//        InputAckPayload => 4,
//        RequestQualityDataPayload => 4,
//        PlayerInputPayload p => 1 + mp * 4 + mp + 2 + 2 + 2 + 2 + 2 + 4
//            + Enumerable.Range(0, Math.Min(mp, p.NumFrames.Count)).Sum(i => p.NumFrames[i] * 4),
//        PlayersStatusPayload => 1 + mp * 2,
//        KickPayload => 6,
//        ChecksumAckPayload => 4,
//        PlayersConfigurationDataPayload => 1 + mp * 2,
//        PlayerDisconnectedPayload => 8,
//        ChangePortPayload => 2,
//        _ => 0 // null / StartGame — no payload
//    };

//    private static void WritePayload(byte[] buf, ref int o, object? payload, int mp)
//    {
//        switch (payload)
//        {
//            case NewConnectionReplyPayload p:
//                buf[o++] = p.Success;
//                buf[o++] = p.MatchNumPlayers;
//                buf[o++] = p.PlayerIndex;
//                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.MatchDurationInFrames); o += 4;
//                buf[o++] = 0; // unknown field
//                buf[o++] = p.IsValidationServerDebugMode;
//                break;

//            case InputAckPayload p:
//                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.AckFrame); o += 4;
//                break;

//            case PlayerInputPayload p:
//                buf[o++] = p.NumPlayers;
//                for (int i = 0; i < mp; i++)
//                {
//                    uint sf = i < p.StartFrame.Count ? p.StartFrame[i] : 0;
//                    BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), sf); o += 4;
//                }
//                for (int i = 0; i < mp; i++)
//                    buf[o++] = i < p.NumFrames.Count ? p.NumFrames[i] : (byte)0;

//                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.NumPredictedOverrides); o += 2;
//                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.NumZeroedOverrides); o += 2;
//                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.Ping); o += 2;
//                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.PacketLossPercent); o += 2;
//                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), (short)(p.Rift * 100)); o += 2;
//                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.ChecksumAckFrame); o += 4;

//                for (int pi = 0; pi < mp; pi++)
//                {
//                    var arr = pi < p.InputPerFrame.Count ? p.InputPerFrame[pi] : [];
//                    byte nf = pi < p.NumFrames.Count ? p.NumFrames[pi] : (byte)0;
//                    for (int f = 0; f < nf; f++)
//                    {
//                        uint v = f < arr.Count ? arr[f] : 0;
//                        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), v); o += 4;
//                    }
//                }
//                break;

//            case RequestQualityDataPayload p:
//                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.Ping); o += 2;
//                BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), p.PacketLossPercent); o += 2;
//                break;

//            case PlayersStatusPayload p:
//                buf[o++] = p.NumPlayers;
//                for (int i = 0; i < mp; i++)
//                {
//                    short avg = i < p.Status.Count ? p.Status[i].AveragePing : (short)0;
//                    BinaryPrimitives.WriteInt16LittleEndian(buf.AsSpan(o), avg); o += 2;
//                }
//                break;

//            case KickPayload p:
//                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.Reason); o += 2;
//                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.Param1); o += 4;
//                break;

//            case ChecksumAckPayload p:
//                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.AckFrame); o += 4;
//                break;

//            case PlayersConfigurationDataPayload p:
//                buf[o++] = p.NumPlayers;
//                for (int i = 0; i < mp; i++)
//                {
//                    ushort val = PlayerConfigValues[i % PlayerConfigValues.Length];
//                    BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), val); o += 2;
//                }
//                break;

//            case PlayerDisconnectedPayload p:
//                buf[o++] = p.PlayerIndex;
//                buf[o++] = p.ShouldAITakeControl;
//                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(o), p.AITakeControlFrame); o += 4;
//                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.PlayerDisconnectedArrayIndex); o += 2;
//                break;

//            case ChangePortPayload p:
//                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(o), p.Port); o += 2;
//                break;

//            // null = StartGame (no payload body)
//        }
//    }
//}

