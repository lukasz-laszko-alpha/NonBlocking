// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using BenchmarkDotNet.Attributes;
using NB = Alc.NonBlocking;
using SC = System.Collections.Concurrent;

namespace NonBlockingTests
{
    // Run with: dotnet run -c Release -- bdn --filter *EnumerationBenchmarks*
    // [MemoryDiagnoser] reports allocated bytes/op; the NonBlocking rows are expected to be 0 B.
    [MemoryDiagnoser]
    public class EnumerationBenchmarks
    {
        [Params(100, 10000)]
        public int Count;

        private NB.ConcurrentDictionary<int, int> _nb;
        private SC.ConcurrentDictionary<int, int> _sys;

        [GlobalSetup]
        public void Setup()
        {
            _nb = new NB.ConcurrentDictionary<int, int>();
            _sys = new SC.ConcurrentDictionary<int, int>();
            for (int i = 0; i < Count; i++)
            {
                _nb.TryAdd(i, i);
                _sys.TryAdd(i, i);
            }
        }

        [Benchmark(Baseline = true)]
        public long System_Foreach()
        {
            long s = 0;
            foreach (var kv in _sys)
            {
                s += kv.Value;
            }

            return s;
        }

        [Benchmark]
        public long NonBlocking_Foreach()
        {
            long s = 0;
            foreach (var kv in _nb)
            {
                s += kv.Value;
            }

            return s;
        }

        [Benchmark]
        public long NonBlocking_Keys()
        {
            long s = 0;
            foreach (var k in _nb.Keys)
            {
                s += k;
            }

            return s;
        }

        [Benchmark]
        public long NonBlocking_Values()
        {
            long s = 0;
            foreach (var v in _nb.Values)
            {
                s += v;
            }

            return s;
        }
    }
}
