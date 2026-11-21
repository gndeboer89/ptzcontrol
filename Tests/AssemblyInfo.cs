using Xunit;

// Several tests share process-wide state (the one UDP socket all cameras use) or measure short reply windows
// on the loopback network, and the concurrency test deliberately keeps several threads busy. Run them one after another.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
