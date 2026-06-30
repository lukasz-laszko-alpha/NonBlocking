// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    // Phase 3a: int/long-backed enum keys (with the default comparer) reuse the int/long key
    // leaves via reinterpret, eliminating Boxed<TKey>. Composes with the value-inline paths, so
    // <enum,int> and <enum,Guid> are allocation-free on both key and value sides.
    public class InlineEnumKeyTests
    {
        private enum Color { Red = 0, Green = 1, Blue = 2 } // int-backed

        private enum BigFlag : long { None = 0, Hi = 1L << 40 } // long-backed

        private enum Small : byte { A = 0, B = 1 } // byte-backed -> stays boxed

        [Fact]
        public static void IntEnumKey_IntValue_CorrectAndAllocationFree()
        {
            const int N = 20000;
            // (Color)i is a valid Color value for any int i, giving N distinct enum keys.
            for (int w = 0; w < 8; w++)
            {
                var warm = new NB.ConcurrentDictionary<Color, int>(1, N, null);
                for (int i = 0; i < N; i++) warm.TryAdd((Color)i, i);
            }

            var d = new NB.ConcurrentDictionary<Color, int>(1, N, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) d.TryAdd((Color)i, i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // key inline as int + value inline => no per-entry boxes
            Assert.True(allocated < N, $"<enum,int> insert of {N} allocated {allocated} bytes (expected < {N})");
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++) Assert.Equal(i, d[(Color)i]);

            // named-value correctness, including the zero value Color.Red
            Assert.True(d.ContainsKey(Color.Red));
            Assert.True(d.TryRemove(Color.Green, out var gv) && gv == 1);
            Assert.False(d.ContainsKey(Color.Green));
        }

        [Fact]
        public static void IntEnumKey_GuidValue_KeyAndValueInline()
        {
            var d = new NB.ConcurrentDictionary<Color, Guid>();
            var g = Guid.NewGuid();
            d[Color.Blue] = g;
            Assert.Equal(g, d[Color.Blue]);
            d[(Color)1000] = Guid.Empty;
            Assert.Equal(Guid.Empty, d[(Color)1000]);
            Assert.Equal(2, d.Count);
        }

        [Fact]
        public static void LongEnumKey_RoundTrip()
        {
            var d = new NB.ConcurrentDictionary<BigFlag, int>();
            // start at 1 so none of these collide with BigFlag.None == (BigFlag)0
            for (int i = 1; i <= 5000; i++) Assert.True(d.TryAdd((BigFlag)((long)i << 33), i));
            for (int i = 1; i <= 5000; i++) Assert.Equal(i, d[(BigFlag)((long)i << 33)]);
            Assert.False(d.ContainsKey(BigFlag.None));
            d[BigFlag.None] = -1;
            Assert.Equal(-1, d[BigFlag.None]);
            Assert.Equal(5001, d.Count);
        }

        [Fact]
        public static void ByteEnumKey_StillCorrect_BoxedPath()
        {
            // byte-backed enum is smaller than the int slot -> stays on the boxed key path.
            // Correctness must still hold.
            var d = new NB.ConcurrentDictionary<Small, int>();
            Assert.True(d.TryAdd(Small.A, 10));
            Assert.True(d.TryAdd(Small.B, 20));
            Assert.Equal(10, d[Small.A]);
            Assert.Equal(20, d[Small.B]);
            Assert.True(d.TryRemove(Small.A, out var v) && v == 10);
            Assert.Equal(1, d.Count);
        }

        [Fact]
        public static void IntEnumKey_CustomComparer_StillCorrect_BoxedPath()
        {
            // a custom comparer cannot be reinterpreted, so this falls to the boxed key path.
            var d = new NB.ConcurrentDictionary<Color, int>(EqualityComparer<Color>.Default);
            for (int i = 0; i < 1000; i++) d[(Color)i] = i;
            for (int i = 0; i < 1000; i++) Assert.Equal(i, d[(Color)i]);
            Assert.Equal(1000, d.Count);
        }
    }
}
