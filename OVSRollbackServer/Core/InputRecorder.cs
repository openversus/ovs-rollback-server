// InputRecorder.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Threading;
using OVS.Rollback.Models;

namespace OVS.Rollback.Core
{
    /// <summary>
    /// Records every player slot's input for every frame of one match (InputRecording settings).
    /// <see cref="Record"/> runs on the UDP receive path: an array write, no allocation, no lock.
    /// The arrays are sized once, at match creation (DurationInFrames + ExtraFrames per slot);
    /// an input for a later frame is counted, never stored. The first input received for a
    /// frame is kept, as the match's input history keeps it. Who sits in each slot is taken
    /// from the match config at creation, because the match's player list is cleared when it
    /// ends. <see cref="TryTakePayload"/> builds the recording exactly once.
    /// </summary>
    public sealed class InputRecorder
    {
        private readonly uint[][] _inputs;
        private readonly ulong[][] _received;
        private readonly uint[] _frames;
        private readonly OvsPlayer?[] _slots;
        private readonly uint _capacity;
        private readonly uint _durationFrames;
        private readonly DateTime _startedAtUtc = DateTime.UtcNow;
        private long _dropped;
        private int _taken;

        public InputRecorder(OVSMatchConfig config, int teamSlotCount, uint extraFrames)
        {
            int slots = Math.Max(0, teamSlotCount);
            _durationFrames = config.MatchDuration;
            _capacity = (uint)Math.Min((ulong)config.MatchDuration + extraFrames, int.MaxValue / 2);
            _inputs = new uint[slots][];
            _received = new ulong[slots][];
            _frames = new uint[slots];
            _slots = new OvsPlayer?[slots];
            for (int i = 0; i < slots; i++)
            {
                _inputs[i] = new uint[_capacity];
                _received[i] = new ulong[(_capacity + 63) / 64];
            }

            foreach (OvsPlayer? player in config.Players)
            {
                if (null != player && !player.IsSpectator && !player.IsBot && player.PlayerIndex < slots)
                {
                    _slots[player.PlayerIndex] ??= player;
                }
            }
        }

        public bool Taken => 0 != Volatile.Read(ref _taken);

        /// <summary>The input <paramref name="value"/> of slot <paramref name="slot"/> for <paramref name="frame"/>.</summary>
        public void Record(int slot, uint frame, uint value)
        {
            if (slot < 0 || slot >= _inputs.Length || frame >= _capacity)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            ref ulong word = ref _received[slot][frame >> 6];
            ulong bit = 1UL << (int)(frame & 63);
            if (0 != (word & bit))
            {
                return;
            }

            _inputs[slot][frame] = value;
            word |= bit;
            if (frame >= _frames[slot])
            {
                _frames[slot] = frame + 1;
            }
        }

        /// <summary>Inputs that never reached <see cref="Record"/> (a PlayerIndex outside the match's slots).</summary>
        public void CountDropped(int count)
        {
            if (count > 0)
            {
                Interlocked.Add(ref _dropped, count);
            }
        }

        /// <summary>The recording, the first time it is asked for; null after that, or when no slot has any input.</summary>
        public InputRecordingPayload? TryTakePayload(string matchId, string key, string endedBy)
        {
            if (0 != Interlocked.Exchange(ref _taken, 1))
            {
                return null;
            }

            var payload = new InputRecordingPayload {
                MatchId = matchId,
                Key = key,
                EndedBy = endedBy,
                StartedAtUtc = _startedAtUtc.ToString("O"),
                EndedAtUtc = DateTime.UtcNow.ToString("O"),
                DurationFrames = _durationFrames,
                DroppedInputs = Interlocked.Read(ref _dropped)
            };

            for (int slot = 0; slot < _inputs.Length; slot++)
            {
                uint frames = _frames[slot];
                if (0 == frames)
                {
                    continue;
                }

                OvsPlayer? player = _slots[slot];
                var recorded = new InputRecordingPlayer {
                    PlayerIndex = slot,
                    PlayerId = player?.PlayerId ?? "",
                    PlayerName = player?.PlayerName ?? "",
                    PlayerCharacter = player?.PlayerCharacter ?? "",
                    Frames = frames,
                    Inputs = Compress(_inputs[slot], frames)
                };

                uint received = 0;
                long gapStart = -1;
                for (uint frame = 0; frame < frames; frame++)
                {
                    bool has = 0 != (_received[slot][frame >> 6] & (1UL << (int)(frame & 63)));
                    if (has)
                    {
                        received++;
                        if (gapStart >= 0)
                        {
                            recorded.Missing.Add([(uint)gapStart, frame - 1]);
                            gapStart = -1;
                        }
                    }
                    else if (gapStart < 0)
                    {
                        gapStart = frame;
                    }
                }

                recorded.ReceivedFrames = received;
                payload.Players.Add(recorded);
            }

            return 0 == payload.Players.Count ? null : payload;
        }

        // One little-endian uint32 per frame, gzipped (inputs are held for many frames, so runs compress hard).
        private static byte[] Compress(uint[] inputs, uint frames)
        {
            byte[] raw = new byte[frames * 4];
            for (uint frame = 0; frame < frames; frame++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan((int)(frame * 4)), inputs[frame]);
            }

            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
            {
                gzip.Write(raw);
            }

            return output.ToArray();
        }
    }
}
