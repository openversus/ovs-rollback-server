// SpanWriter.cs
using System;
using System.Collections.Generic;
using System.Text;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Rollback.Core
{
    /// <summary>
    /// Zero-allocation binary writer over a <see cref="Span{T}"/>.
    /// All multi-byte writes are little-endian (identical to BinaryWriter default).
    /// </summary>
    public ref struct SpanWriter
    {
        private readonly Span<byte> _buf;
        private int _pos;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SpanWriter(Span<byte> buffer) { _buf = buffer; _pos = 0; }

        public readonly int Written
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteU8(byte v) => _buf[_pos++] = v;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteI16(short v)
        {
            BinaryPrimitives.WriteInt16LittleEndian(_buf[_pos..], v);
            _pos += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteU16(ushort v)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_buf[_pos..], v);
            _pos += 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteU32(uint v)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_buf[_pos..], v);
            _pos += 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WriteF32(float v)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_buf[_pos..], v);
            _pos += 4;
        }
    }
}
