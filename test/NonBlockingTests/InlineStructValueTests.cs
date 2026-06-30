// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Threading.Tasks;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    // Verifies the Phase 2 seqlock value-inline specialization (DictionaryImplValSeqlock) used for
    // unmanaged value types that do not fit Phase 1's 32-bit slot: long/double/Guid/decimal/
    // DateTime/custom structs. Stored inline in the Entry (no per-entry heap object), with a
    // per-entry seqlock making multi-word reads/writes tear-free.
    public class InlineStructValueTests
    {
        [Fact]
        public static void LongDoubleGuidDecimalDateTime_RoundTrip()
        {
            var dl = new NB.ConcurrentDictionary<int, long>();
            foreach (var v in new[] { long.MinValue, -1L, 0L, 1L, long.MaxValue })
            {
                dl[1] = v;
                Assert.Equal(v, dl[1]);
            }

            var dd = new NB.ConcurrentDictionary<int, double>();
            foreach (var v in new[] { double.NaN, double.NegativeInfinity, 0d, -0d, 3.14159d, double.MaxValue })
            {
                dd[1] = v;
                Assert.True(dd.TryGetValue(1, out var got));
                Assert.Equal(BitConverter.DoubleToInt64Bits(v), BitConverter.DoubleToInt64Bits(got));
            }

            var dg = new NB.ConcurrentDictionary<int, Guid>();
            var g = Guid.NewGuid();
            dg[1] = g;
            Assert.Equal(g, dg[1]);
            dg[1] = Guid.Empty;
            Assert.Equal(Guid.Empty, dg[1]);

            var dm = new NB.ConcurrentDictionary<int, decimal>();
            foreach (var v in new[] { decimal.MinValue, -1m, 0m, 1.2345m, decimal.MaxValue })
            {
                dm[1] = v;
                Assert.Equal(v, dm[1]);
            }

            var dt = new NB.ConcurrentDictionary<int, DateTime>();
            var now = DateTime.UtcNow;
            dt[1] = now;
            Assert.Equal(now, dt[1]);
        }

        [Fact]
        public static void DefaultStructValue_IsDistinctFromEmpty()
        {
            // storing default(Guid)/0L must be a present entry, not read as an empty slot.
            var dg = new NB.ConcurrentDictionary<int, Guid>();
            Assert.True(dg.TryAdd(7, Guid.Empty));
            Assert.True(dg.ContainsKey(7));
            Assert.True(dg.TryGetValue(7, out var v));
            Assert.Equal(Guid.Empty, v);
            Assert.Equal(1, dg.Count);
            Assert.True(dg.TryRemove(7, out _));
            Assert.False(dg.ContainsKey(7));
            Assert.True(dg.TryAdd(7, Guid.Empty));
        }

        [Fact]
        public static void LongValue_PreSized_Insert_IsAllocationFree()
        {
            const int N = 20000;
            for (int w = 0; w < 8; w++)
            {
                var warm = new NB.ConcurrentDictionary<int, long>(1, N, null);
                for (int i = 0; i < N; i++) warm.TryAdd(i, i);
            }

            var d = new NB.ConcurrentDictionary<int, long>(1, N, null);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < N; i++) d.TryAdd(i, (long)i << 20);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            // < 1 byte/op: proves no per-entry box (boxing is ~24 B/op). The small residual is
            // one-time JIT/OSR noise on the measured loop, not per-op allocation.
            Assert.True(allocated < N,
                $"<int,long> inline insert of {N} allocated {allocated} bytes (expected < {N}; boxed would allocate ~{N * 24})");
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++) Assert.Equal((long)i << 20, d[i]);
        }

        [Fact]
        public static void GuidValue_Update_IsAllocationFree()
        {
            const int N = 4096;
            var d = new NB.ConcurrentDictionary<int, Guid>(1, N, null);
            var g = Guid.NewGuid();
            for (int i = 0; i < N; i++) d[i] = g;

            for (int w = 0; w < 16; w++)
                for (int i = 0; i < N; i++) d[i] = g;

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < 64; rep++)
                for (int i = 0; i < N; i++) d[i] = g;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.True(allocated == 0, $"<int,Guid> updates allocated {allocated} bytes (expected 0)");
        }

        [Fact]
        public static void GuidValue_Resize_Churn_Correctness()
        {
            const int N = 100_000;
            var d = new NB.ConcurrentDictionary<int, Guid>();

            Guid V(int i) => new Guid(i, (short)(i >> 16), (short)i, 1, 2, 3, 4, 5, 6, 7, 8);

            for (int i = 0; i < N; i++) Assert.True(d.TryAdd(i, V(i)));
            Assert.Equal(N, d.Count);
            for (int i = 0; i < N; i++) Assert.Equal(V(i), d[i]);

            for (int i = 0; i < N; i += 2) Assert.True(d.TryRemove(i, out _));
            Assert.Equal(N / 2, d.Count);
            for (int i = 0; i < N; i++)
            {
                if (i % 2 == 0) Assert.False(d.ContainsKey(i));
                else Assert.Equal(V(i), d[i]);
            }
        }

        // 16-byte struct with an internal invariant used to detect torn reads.
        private struct Pair
        {
            public long A;
            public long B;
        }

        private const long MAGIC = unchecked((long)0xC0FFEE_DEADBEEF);

        [Fact]
        public static void Struct_ConcurrentWriters_NoTornReads()
        {
            // Many threads write Pair{A=x, B=x^MAGIC} to a small shared key set while readers verify
            // the invariant A ^ B == MAGIC. A torn read (A from one write, B from another) would
            // break the invariant. The per-entry seqlock must prevent that.
            const int keys = 32;
            const int writerThreads = 6;
            const int readerThreads = 4;
            const int iters = 300_000;

            var d = new NB.ConcurrentDictionary<int, Pair>();
            for (int k = 0; k < keys; k++) d[k] = new Pair { A = 0, B = 0 ^ MAGIC };

            long tornCount = 0;
            long stop = 0;

            var readers = new Task[readerThreads];
            for (int r = 0; r < readerThreads; r++)
            {
                readers[r] = Task.Run(() =>
                {
                    while (System.Threading.Volatile.Read(ref stop) == 0)
                    {
                        for (int k = 0; k < keys; k++)
                        {
                            if (d.TryGetValue(k, out var p))
                            {
                                if ((p.A ^ p.B) != MAGIC)
                                {
                                    System.Threading.Interlocked.Increment(ref tornCount);
                                }
                            }
                        }
                    }
                });
            }

            Parallel.For(0, writerThreads, t =>
            {
                var rnd = new Random(t * 31 + 7);
                for (int i = 0; i < iters; i++)
                {
                    int k = rnd.Next(keys);
                    long x = ((long)t << 40) | (uint)i;
                    d[k] = new Pair { A = x, B = x ^ MAGIC };
                }
            });

            System.Threading.Volatile.Write(ref stop, 1);
            Task.WaitAll(readers);

            Assert.Equal(0, System.Threading.Interlocked.Read(ref tornCount));

            // final state still satisfies the invariant
            for (int k = 0; k < keys; k++)
            {
                Assert.True(d.TryGetValue(k, out var p));
                Assert.Equal(MAGIC, p.A ^ p.B);
            }
        }

        [Fact]
        public static void GetOrAdd_And_AddOrUpdate_StructValues()
        {
            var d = new NB.ConcurrentDictionary<int, Guid>();
            var g1 = Guid.NewGuid();
            var g2 = Guid.NewGuid();

            Assert.Equal(g1, d.GetOrAdd(1, _ => g1));
            Assert.Equal(g1, d.GetOrAdd(1, _ => g2)); // already present -> factory not used
            Assert.Equal(g1, d[1]);

            var dl = new NB.ConcurrentDictionary<int, long>();
            // concurrent GetOrAdd on the same key must converge to a single value
            long[] observed = new long[64];
            Parallel.For(0, 64, i =>
            {
                observed[i] = dl.GetOrAdd(42, k => (long)k << 20);
            });
            Assert.All(observed, v => Assert.Equal(42L << 20, v));
            Assert.Equal(1, dl.Count);

            // AddOrUpdate
            Assert.Equal(10L, dl.AddOrUpdate(7, 10L, (k, old) => old + 1));
            Assert.Equal(11L, dl.AddOrUpdate(7, 10L, (k, old) => old + 1));
        }

        [Fact]
        public static void LongValue_Concurrent_Stress_MatchesOracle()
        {
            const int threads = 8;
            const int perThread = 25_000;
            var d = new NB.ConcurrentDictionary<int, long>();

            Parallel.For(0, threads, t =>
            {
                int baseKey = t * perThread;
                for (int i = 0; i < perThread; i++)
                {
                    int k = baseKey + i;
                    Assert.True(d.TryAdd(k, (long)k << 32));
                }
                for (int i = 0; i < perThread; i++)
                {
                    int k = baseKey + i;
                    d[k] = ((long)k << 32) + 1;
                }
                for (int i = 0; i < perThread; i++)
                {
                    int k = baseKey + i;
                    if (k % 3 == 0) Assert.True(d.TryRemove(k, out var rv) && rv == ((long)k << 32) + 1);
                }
            });

            int expected = 0;
            for (int k = 0; k < threads * perThread; k++)
            {
                if (k % 3 == 0)
                {
                    Assert.False(d.ContainsKey(k));
                }
                else
                {
                    Assert.True(d.TryGetValue(k, out var v));
                    Assert.Equal(((long)k << 32) + 1, v);
                    expected++;
                }
            }
            Assert.Equal(expected, d.Count);
        }
    }
}
