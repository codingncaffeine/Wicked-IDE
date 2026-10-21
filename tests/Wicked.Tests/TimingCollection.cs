namespace Wicked.Tests;

/// <summary>
/// Tests that time the app against a bound (the UI thread per frame, throughput): xUnit runs this collection alone, after
/// the parallel ones, so a busy neighbour's work is not measured as ours.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TimingCollection
{
    public const string Name = "Timing";
}
