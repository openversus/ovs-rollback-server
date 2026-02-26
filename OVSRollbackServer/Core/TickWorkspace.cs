using OVS.Rollback.Models;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// Pre-allocated workspace reused every tick. One per match.
    /// Eliminates all per-frame allocations in the hot path.
    /// </summary>
    public sealed class TickWorkspace
    {
        public readonly int MaxPlayers;

        // ── Pooled payload (inner lists cleared, never re-created) ──
        public readonly PlayerInputPayload Payload;

        // ── Scratch array for acked frames ──
        public readonly uint[] AckedFrames;

        // ── Player snapshot (avoids ConcurrentDictionary.ToArray() every tick) ──
        public readonly KeyValuePair<string, PlayerInfo>[] PlayerSnapshot;
        public int PlayerCount;

        // ── Reusable byte buffers for serialize → compress → send ──
        //    4 KB each is ~8× the max packet size — safe headroom.
        public readonly byte[] SerializeBuffer = new byte[4096];
        public readonly byte[] CompressBuffer = new byte[4096];

        public TickWorkspace(int maxPlayers)
        {
            MaxPlayers = maxPlayers;
            AckedFrames = new uint[maxPlayers];
            PlayerSnapshot = new KeyValuePair<string, PlayerInfo>[maxPlayers];

            Payload = new PlayerInputPayload {
                StartFrame = new List<uint>(maxPlayers),
                NumFrames = new List<byte>(maxPlayers),
                InputPerFrame = new List<List<uint>>(maxPlayers)
            };

            for (int i = 0; i < maxPlayers; i++)
            {
                Payload.StartFrame.Add(0);
                Payload.NumFrames.Add(0);
                Payload.InputPerFrame.Add(new List<uint>(30));
            }
        }

        /// <summary>
        /// Snapshot the live player dictionary into the pre-allocated array.
        /// ConcurrentDictionary's enumerator is lock-free and allocation-free.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void RefreshPlayerSnapshot(ConcurrentDictionary<string, PlayerInfo> players)
        {
            int i = 0;
            foreach (var kvp in players)
            {
                if (i >= PlayerSnapshot.Length) break;
                PlayerSnapshot[i++] = kvp;
            }
            PlayerCount = i;
        }

        /// <summary>
        /// Reset payload fields for the next recipient.
        /// List.Clear() sets Count=0 but retains the backing array — zero allocation.
        /// </summary>
        public void ResetForRecipient()
        {
            for (int i = 0; i < MaxPlayers; i++)
            {
                Payload.StartFrame[i] = 0;
                Payload.NumFrames[i] = 0;
                Payload.InputPerFrame[i].Clear();
            }
            Payload.NumPredictedOverrides = 0;
            Payload.NumZeroedOverrides = 0;
            Payload.NumPlayers = 0;
            Payload.Ping = 0;
            Payload.PacketsLossPercent = 0;
            Payload.Rift = 0f;
            Payload.ChecksumAckFrame = 0;
            Array.Clear(AckedFrames, 0, MaxPlayers);
        }
    }
}
