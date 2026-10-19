using Xunit;

namespace ParksComputing.XferLang.Tests;

/// <summary>
/// The classes that measure memory. They run alone, because a measurement of the whole process
/// (GC.GetTotalMemory) counts whatever tests running beside it allocate.
/// </summary>
[CollectionDefinition("Memory measurements", DisableParallelization = true)]
public sealed class MemoryMeasurements
{
}
