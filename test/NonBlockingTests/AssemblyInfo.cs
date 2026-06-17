// Copyright (c) Vladimir Sadov. All rights reserved.
//
// This file is distributed under the MIT License. See LICENSE.md for details.

using Xunit;

// Run test methods serially. The allocation tests measure GC.GetAllocatedBytesForCurrentThread()
// and assert exactly zero; running them alongside the allocation-heavy concurrent stress tests
// (which trigger Gen2 GCs that also evict ArrayPool buffers) makes those measurements flaky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
