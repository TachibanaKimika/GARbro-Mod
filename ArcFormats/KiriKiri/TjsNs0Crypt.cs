//! \file       TjsNs0Crypt.cs
//! \date       2026 Jul 30
//! \brief      PackinOne TJS/ns0 stream decryption.
//
// Copyright (C) 2026 by GARbro-Mod-Onachi contributors
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to
// deal in the Software without restriction, including without limitation the
// rights to use, copy, modify, merge, publish, distribute, sublicense, and/or
// sell copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS
// IN THE SOFTWARE.
//

using System;

namespace GameRes.Formats.KiriKiri
{
    /// <summary>
    /// Decrypts the stream cipher variants used by PackinOne TJS/ns0 files.
    /// The method number selects ChaCha8/12/20. Methods 1-3 expand buffered
    /// blocks with PackinOne's xorshift stream; methods 4-6 emit scalar ChaCha
    /// blocks.
    /// </summary>
    internal static class TjsNs0Crypt
    {
        static readonly uint[] Blake2sIv = {
            0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A,
            0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19,
        };

        static readonly byte[,] Blake2sSigma = {
            { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
            { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
            { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
            { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
            { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
            { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
            { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
            { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
            { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
            { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
        };

        const uint XxPrime1 = 0x9E3779B1;
        const uint XxPrime2 = 0x85EBCA77;
        const uint XxPrime3 = 0xC2B2AE3D;
        const uint XxPrime4 = 0x27D4EB2F;
        const uint XxPrime5 = 0x165667B1;

        public static bool TryDecrypt (byte[] input, uint seed, ushort method,
                                       byte[] initialization_vector, out byte[] output)
        {
            output = null;
            int rounds;
            int block_count;
            switch (method)
            {
            case 1:
                rounds = 8;
                block_count = 16;
                break;
            case 2:
                rounds = 12;
                block_count = 8;
                break;
            case 3:
                rounds = 20;
                block_count = 4;
                break;
            case 4:
                rounds = 8;
                block_count = 1;
                break;
            case 5:
                rounds = 12;
                block_count = 1;
                break;
            case 6:
                rounds = 20;
                block_count = 1;
                break;
            default:
                return false;
            }

            if (null == input)
                return false;
            var iv = initialization_vector ?? new byte[0];
            var seed_bytes = new[] {
                (byte)seed, (byte)(seed >> 8), (byte)(seed >> 16), (byte)(seed >> 24)
            };
            var key = Blake2s (iv, seed_bytes);
            ulong nonce = (ulong)seed << 32 | XxHash32 (iv, seed);
            output = new byte[input.Length];
            var key_stream = new byte[block_count*64];
            uint fallback = (uint)nonce ^ (uint)(nonce >> 32);
            if (0 == fallback)
                fallback = seed;
            ulong counter = 0;
            for (int position = 0; position < input.Length; position += key_stream.Length)
            {
                ChaChaBlock (key, counter++, nonce, rounds, key_stream);
                for (int word = 16; word < key_stream.Length/4; ++word)
                {
                    uint value = ReadUInt32 (key_stream, (word-16)*4);
                    value ^= value << 13;
                    value ^= value >> 17;
                    value ^= value << 5;
                    WriteUInt32 (key_stream, word*4, 0 != value ? value : fallback);
                }
                int count = Math.Min (key_stream.Length, input.Length-position);
                for (int i = 0; i < count; ++i)
                    output[position+i] = (byte)(input[position+i] ^ key_stream[i]);
            }
            return true;
        }

        static byte[] Blake2s (byte[] input, byte[] key)
        {
            const int block_size = 64;
            var state = (uint[])Blake2sIv.Clone();
            state[0] ^= 0x01010020u ^ (uint)(key.Length << 8);

            var message = new byte[block_size+input.Length];
            Buffer.BlockCopy (key, 0, message, 0, key.Length);
            if (input.Length > 0)
                Buffer.BlockCopy (input, 0, message, block_size, input.Length);

            ulong counter = 0;
            int position = 0;
            while (position+block_size < message.Length)
            {
                counter += block_size;
                Blake2sCompress (state, message, position, counter, false);
                position += block_size;
            }
            int remaining = message.Length-position;
            counter += (uint)remaining;
            var final_block = new byte[block_size];
            Buffer.BlockCopy (message, position, final_block, 0, remaining);
            Blake2sCompress (state, final_block, 0, counter, true);

            var digest = new byte[32];
            for (int i = 0; i < state.Length; ++i)
                WriteUInt32 (digest, i*4, state[i]);
            return digest;
        }

        static void Blake2sCompress (uint[] state, byte[] block, int offset,
                                     ulong counter, bool is_final)
        {
            var message = new uint[16];
            var work = new uint[16];
            for (int i = 0; i < message.Length; ++i)
                message[i] = ReadUInt32 (block, offset+i*4);
            for (int i = 0; i < 8; ++i)
            {
                work[i] = state[i];
                work[i+8] = Blake2sIv[i];
            }
            work[12] ^= (uint)counter;
            work[13] ^= (uint)(counter >> 32);
            if (is_final)
                work[14] = ~work[14];

            for (int round = 0; round < 10; ++round)
            {
                Blake2sMix (work, 0, 4, 8, 12,
                    message[Blake2sSigma[round, 0]], message[Blake2sSigma[round, 1]]);
                Blake2sMix (work, 1, 5, 9, 13,
                    message[Blake2sSigma[round, 2]], message[Blake2sSigma[round, 3]]);
                Blake2sMix (work, 2, 6, 10, 14,
                    message[Blake2sSigma[round, 4]], message[Blake2sSigma[round, 5]]);
                Blake2sMix (work, 3, 7, 11, 15,
                    message[Blake2sSigma[round, 6]], message[Blake2sSigma[round, 7]]);
                Blake2sMix (work, 0, 5, 10, 15,
                    message[Blake2sSigma[round, 8]], message[Blake2sSigma[round, 9]]);
                Blake2sMix (work, 1, 6, 11, 12,
                    message[Blake2sSigma[round, 10]], message[Blake2sSigma[round, 11]]);
                Blake2sMix (work, 2, 7, 8, 13,
                    message[Blake2sSigma[round, 12]], message[Blake2sSigma[round, 13]]);
                Blake2sMix (work, 3, 4, 9, 14,
                    message[Blake2sSigma[round, 14]], message[Blake2sSigma[round, 15]]);
            }
            for (int i = 0; i < state.Length; ++i)
                state[i] ^= work[i] ^ work[i+8];
        }

        static void Blake2sMix (uint[] value, int a, int b, int c, int d, uint x, uint y)
        {
            unchecked
            {
                value[a] = value[a]+value[b]+x;
                value[d] = RotateRight (value[d] ^ value[a], 16);
                value[c] += value[d];
                value[b] = RotateRight (value[b] ^ value[c], 12);
                value[a] = value[a]+value[b]+y;
                value[d] = RotateRight (value[d] ^ value[a], 8);
                value[c] += value[d];
                value[b] = RotateRight (value[b] ^ value[c], 7);
            }
        }

        static uint XxHash32 (byte[] data, uint seed)
        {
            unchecked
            {
                int position = 0;
                uint hash;
                if (data.Length >= 16)
                {
                    uint value1 = seed+XxPrime1+XxPrime2;
                    uint value2 = seed+XxPrime2;
                    uint value3 = seed;
                    uint value4 = seed-XxPrime1;
                    int limit = data.Length-16;
                    do
                    {
                        value1 = XxRound (value1, ReadUInt32 (data, position));
                        value2 = XxRound (value2, ReadUInt32 (data, position+4));
                        value3 = XxRound (value3, ReadUInt32 (data, position+8));
                        value4 = XxRound (value4, ReadUInt32 (data, position+12));
                        position += 16;
                    }
                    while (position <= limit);
                    hash = RotateLeft (value1, 1)+RotateLeft (value2, 7)
                         + RotateLeft (value3, 12)+RotateLeft (value4, 18);
                }
                else
                    hash = seed+XxPrime5;

                hash += (uint)data.Length;
                while (position <= data.Length-4)
                {
                    hash += ReadUInt32 (data, position)*XxPrime3;
                    hash = RotateLeft (hash, 17)*XxPrime4;
                    position += 4;
                }
                while (position < data.Length)
                {
                    hash += data[position]*XxPrime5;
                    hash = RotateLeft (hash, 11)*XxPrime1;
                    ++position;
                }
                hash ^= hash >> 15;
                hash *= XxPrime2;
                hash ^= hash >> 13;
                hash *= XxPrime3;
                hash ^= hash >> 16;
                return hash;
            }
        }

        static uint XxRound (uint accumulator, uint input)
        {
            unchecked
            {
                accumulator += input*XxPrime2;
                accumulator = RotateLeft (accumulator, 13);
                return accumulator*XxPrime1;
            }
        }

        static void ChaChaBlock (byte[] key, ulong counter, ulong nonce,
                                 int rounds, byte[] output)
        {
            var initial = new uint[16];
            initial[0] = 0x61707865;
            initial[1] = 0x3320646E;
            initial[2] = 0x79622D32;
            initial[3] = 0x6B206574;
            for (int i = 0; i < 8; ++i)
                initial[i+4] = ReadUInt32 (key, i*4);
            initial[12] = (uint)counter;
            initial[13] = (uint)(counter >> 32);
            initial[14] = (uint)nonce;
            initial[15] = (uint)(nonce >> 32);

            var work = (uint[])initial.Clone();
            for (int round = 0; round < rounds; round += 2)
            {
                ChaChaQuarterRound (work, 0, 4, 8, 12);
                ChaChaQuarterRound (work, 1, 5, 9, 13);
                ChaChaQuarterRound (work, 2, 6, 10, 14);
                ChaChaQuarterRound (work, 3, 7, 11, 15);
                ChaChaQuarterRound (work, 0, 5, 10, 15);
                ChaChaQuarterRound (work, 1, 6, 11, 12);
                ChaChaQuarterRound (work, 2, 7, 8, 13);
                ChaChaQuarterRound (work, 3, 4, 9, 14);
            }
            unchecked
            {
                for (int i = 0; i < work.Length; ++i)
                    WriteUInt32 (output, i*4, work[i]+initial[i]);
            }
        }

        static void ChaChaQuarterRound (uint[] value, int a, int b, int c, int d)
        {
            unchecked
            {
                value[a] += value[b];
                value[d] = RotateLeft (value[d] ^ value[a], 16);
                value[c] += value[d];
                value[b] = RotateLeft (value[b] ^ value[c], 12);
                value[a] += value[b];
                value[d] = RotateLeft (value[d] ^ value[a], 8);
                value[c] += value[d];
                value[b] = RotateLeft (value[b] ^ value[c], 7);
            }
        }

        static uint RotateLeft (uint value, int count)
        {
            return value << count | value >> (32-count);
        }

        static uint RotateRight (uint value, int count)
        {
            return value >> count | value << (32-count);
        }

        static uint ReadUInt32 (byte[] data, int offset)
        {
            return (uint)(data[offset] | data[offset+1] << 8
                | data[offset+2] << 16 | data[offset+3] << 24);
        }

        static void WriteUInt32 (byte[] data, int offset, uint value)
        {
            data[offset] = (byte)value;
            data[offset+1] = (byte)(value >> 8);
            data[offset+2] = (byte)(value >> 16);
            data[offset+3] = (byte)(value >> 24);
        }
    }
}
