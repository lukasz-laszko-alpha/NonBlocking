// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using NB = Alc.NonBlocking;

namespace NonBlockingTests
{
    public class EnumerationAllocationTests
    {
        private static NB.ConcurrentDictionary<int, int> MakeIntDict(int n)
        {
            var d = new NB.ConcurrentDictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                d.TryAdd(i, i * 2);
            }

            return d;
        }

        private static NB.ConcurrentDictionary<string, string> MakeStringDict(int n)
        {
            var d = new NB.ConcurrentDictionary<string, string>();
            for (int i = 0; i < n; i++)
            {
                d.TryAdd("k" + i, "v" + i);
            }

            return d;
        }

        // Measures the steady-state allocation of an action after warming up the JIT.
        // Tiered re-JIT happens on a background thread, so it does not count against the
        // current thread's allocation counter once the methods have been compiled once.
        private static long AllocatedBy(Action action)
        {
            for (int w = 0; w < 16; w++)
            {
                action();
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int rep = 0; rep < 64; rep++)
            {
                action();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        [Fact]
        public static void Foreach_OverDictionary_ValueTypes_IsAllocationFree()
        {
            var d = MakeIntDict(1000);
            long sum = 0;
            long allocated = AllocatedBy(() =>
            {
                foreach (var kv in d)
                {
                    sum += kv.Key + kv.Value;
                }
            });

            GC.KeepAlive(sum);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public static void Foreach_OverDictionary_RefTypes_IsAllocationFree()
        {
            var d = MakeStringDict(1000);
            int len = 0;
            long allocated = AllocatedBy(() =>
            {
                foreach (var kv in d)
                {
                    len += kv.Key.Length + kv.Value.Length;
                }
            });

            GC.KeepAlive(len);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public static void Foreach_OverKeys_IsAllocationFree()
        {
            var d = MakeIntDict(1000);
            long sum = 0;
            long allocated = AllocatedBy(() =>
            {
                foreach (var k in d.Keys)
                {
                    sum += k;
                }
            });

            GC.KeepAlive(sum);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public static void Foreach_OverValues_IsAllocationFree()
        {
            var d = MakeIntDict(1000);
            long sum = 0;
            long allocated = AllocatedBy(() =>
            {
                foreach (var v in d.Values)
                {
                    sum += v;
                }
            });

            GC.KeepAlive(sum);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public static void Keys_And_Values_AreCachedInstances()
        {
            var d = MakeIntDict(10);
            Assert.Same(d.Keys, d.Keys);
            Assert.Same(d.Values, d.Values);
        }

        [Fact]
        public static void Keys_MatchDictionary()
        {
            var d = MakeIntDict(500);
            var expected = new HashSet<int>(Enumerable.Range(0, 500));

            var fromKeys = new HashSet<int>();
            foreach (var k in d.Keys)
            {
                Assert.True(fromKeys.Add(k));
            }

            Assert.Equal(500, d.Keys.Count);
            Assert.True(expected.SetEquals(fromKeys));
        }

        [Fact]
        public static void Values_MatchDictionary()
        {
            var d = MakeIntDict(500);
            var expected = new HashSet<int>(Enumerable.Range(0, 500).Select(i => i * 2));

            var fromValues = new HashSet<int>();
            foreach (var v in d.Values)
            {
                fromValues.Add(v);
            }

            Assert.Equal(500, d.Values.Count);
            Assert.True(expected.SetEquals(fromValues));
        }

        [Fact]
        public static void Keys_Contains_Works()
        {
            var d = MakeIntDict(100);
            Assert.True(d.Keys.Contains(42));
            Assert.False(d.Keys.Contains(1000));
        }

        [Fact]
        public static void Values_Contains_Works()
        {
            var d = MakeIntDict(100);
            Assert.True(d.Values.Contains(42 * 2));
            Assert.False(d.Values.Contains(-1));
        }

        [Fact]
        public static void Keys_CopyTo_Works()
        {
            var d = MakeIntDict(50);
            var array = new int[50];
            d.Keys.CopyTo(array, 0);
            Assert.Equal(Enumerable.Range(0, 50).OrderBy(x => x), array.OrderBy(x => x));
        }

        [Fact]
        public static void Values_CopyTo_Works()
        {
            var d = MakeIntDict(50);
            var array = new int[50];
            d.Values.CopyTo(array, 0);
            Assert.Equal(Enumerable.Range(0, 50).Select(i => i * 2).OrderBy(x => x), array.OrderBy(x => x));
        }

        [Fact]
        public static void Keys_IsReadOnly_AndMutatorsThrow()
        {
            var d = MakeIntDict(10);
            ICollection<int> keys = d.Keys;
            Assert.True(keys.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => keys.Add(1));
            Assert.Throws<NotSupportedException>(() => keys.Remove(1));
            Assert.Throws<NotSupportedException>(() => keys.Clear());
        }

        [Fact]
        public static void Values_IsReadOnly_AndMutatorsThrow()
        {
            var d = MakeIntDict(10);
            ICollection<int> values = d.Values;
            Assert.True(values.IsReadOnly);
            Assert.Throws<NotSupportedException>(() => values.Add(1));
            Assert.Throws<NotSupportedException>(() => values.Remove(1));
            Assert.Throws<NotSupportedException>(() => values.Clear());
        }

        [Fact]
        public static void InterfaceKeysValues_StillWork()
        {
            var d = MakeIntDict(20);

            IDictionary<int, int> idict = d;
            Assert.Equal(20, idict.Keys.Count);
            Assert.Equal(20, idict.Values.Count);

            IReadOnlyDictionary<int, int> rod = d;
            Assert.Equal(20, rod.Keys.Count());
            Assert.Equal(20, rod.Values.Count());

            IDictionary nonGeneric = d;
            Assert.Equal(20, nonGeneric.Keys.Count);
            Assert.Equal(20, nonGeneric.Values.Count);
        }

        [Fact]
        public static void Enumerator_MatchesDictionaryContents()
        {
            var d = MakeIntDict(1000);
            var seen = new Dictionary<int, int>();
            foreach (var kv in d)
            {
                seen[kv.Key] = kv.Value;
            }

            Assert.Equal(1000, seen.Count);
            for (int i = 0; i < 1000; i++)
            {
                Assert.Equal(i * 2, seen[i]);
            }
        }
    }
}
