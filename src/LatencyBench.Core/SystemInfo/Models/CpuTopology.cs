using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.SystemInfo.Models;

/// <summary>How a physical core is classified on a hybrid CPU.</summary>
public enum CoreClass
{
	/// <summary>Not a hybrid CPU, or the class could not be determined.</summary>
	Uniform,

	/// <summary>Performance core — the highest efficiency class present.</summary>
	Performance,

	/// <summary>Efficiency core — a lower efficiency class than the fastest cores on this CPU.</summary>
	Efficiency
}

/// <summary>One physical core and the logical processors it presents.</summary>
public sealed record PhysicalCore(
	int CoreIndex,
	IReadOnlyList<int> LogicalProcessors,
	ushort Group,
	byte EfficiencyClass,
	CoreClass Class,
	bool IsSimultaneousMultithreaded)
{
	/// <summary>The logical processor a single-threaded workload should be pinned to when this core
	/// is chosen — the lowest-numbered one, so the choice is stable between runs. Null only if the
	/// core reported no logical processors at all, which some virtualised CPUs do.</summary>
	public int? PrimaryLogicalProcessor => LogicalProcessors.Count > 0 ? LogicalProcessors[0] : null;
}

public sealed record NumaNode(int NodeNumber, IReadOnlyList<int> LogicalProcessors);

public sealed record CacheLevel(byte Level, string Kind, uint SizeBytes, ushort LineSizeBytes, IReadOnlyList<int> LogicalProcessors);

/// <summary>
/// The CPU's physical layout. Everything the affinity and recommendation logic needs to answer
/// "which core should this device's interrupts go to" without guessing.
/// </summary>
public sealed class CpuTopology
{
	public required IReadOnlyList<PhysicalCore> PhysicalCores { get; init; }

	public required IReadOnlyList<NumaNode> NumaNodes { get; init; }

	public required IReadOnlyList<CacheLevel> Caches { get; init; }

	public required int LogicalProcessorCount { get; init; }

	public required int PackageCount { get; init; }

	public required ushort ActiveProcessorGroupCount { get; init; }

	/// <summary>True when the CPU exposes more than one efficiency class — Intel's P/E split, or
	/// AMD's mixed standard and dense core dies.</summary>
	public bool IsHybrid => PhysicalCores.Select(core => core.EfficiencyClass).Distinct().Count() > 1;

	public bool HasSimultaneousMultithreading => PhysicalCores.Any(core => core.IsSimultaneousMultithreaded);

	/// <summary>
	/// True when this machine has more than 64 logical processors and Windows therefore splits them
	/// across processor groups. The interrupt-affinity registry format LatencyBench writes carries a
	/// single 64-bit mask with no group number, so it cannot address such a machine correctly.
	/// </summary>
	public bool UsesMultipleProcessorGroups => ActiveProcessorGroupCount > 1;

	public IReadOnlyList<PhysicalCore> PerformanceCores =>
		PhysicalCores.Where(core => core.Class != CoreClass.Efficiency).ToList();

	/// <summary>The NUMA node a logical processor belongs to, or null when it could not be mapped.</summary>
	public int? NumaNodeOf(int logicalProcessor) =>
		NumaNodes.FirstOrDefault(node => node.LogicalProcessors.Contains(logicalProcessor))?.NodeNumber;

	public PhysicalCore? CoreOf(int logicalProcessor) =>
		PhysicalCores.FirstOrDefault(core => core.LogicalProcessors.Contains(logicalProcessor));
}
