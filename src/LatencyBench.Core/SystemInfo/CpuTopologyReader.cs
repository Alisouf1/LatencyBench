using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;
using LatencyBench.Core.SystemInfo.Models;

namespace LatencyBench.Core.SystemInfo;

/// <summary>
/// Reads the CPU's physical layout from GetLogicalProcessorInformationEx.
/// <para>
/// The API returns a packed sequence of variable-length records whose payload is a union, so it is
/// walked by byte offset rather than marshalled into structs: the managed layout of a union
/// containing a variable-length trailing array cannot be expressed with StructLayout, and the
/// NUMA and cache records changed shape in Windows 11 (part of their Reserved block became a
/// GroupCount field). The offsets used here are the ones both layouts agree on.
/// </para>
/// </summary>
public sealed class CpuTopologyReader
{
	private const int RecordRelationshipOffset = 0;
	private const int RecordSizeOffset = 4;
	private const int RecordPayloadOffset = 8;

	/// <summary>sizeof(GROUP_AFFINITY) on x64: KAFFINITY (8) + Group (2) + Reserved[3] (6).</summary>
	private const int GroupAffinitySize = 16;

	/// <summary>sizeof(PROCESSOR_GROUP_INFO): two BYTEs, Reserved[38], then KAFFINITY.</summary>
	private const int ProcessorGroupInfoSize = 48;

	public CpuTopology Read()
	{
		byte[] buffer = QueryAll();

		// Group sizes have to be known before any other record can be turned into a global logical
		// processor index, and RelationGroup is not guaranteed to come first in the buffer.
		int[] groupBaseIndex = ReadGroupBaseIndexes(buffer, out ushort activeGroupCount, out int totalLogicalProcessors);

		var cores = new List<PhysicalCore>();
		var numaNodes = new List<NumaNode>();
		var caches = new List<CacheLevel>();
		int packageCount = 0;

		int offset = 0;
		int coreIndex = 0;
		while (offset < buffer.Length)
		{
			var record = buffer.AsSpan(offset);
			uint relationship = BinaryPrimitives.ReadUInt32LittleEndian(record[RecordRelationshipOffset..]);
			uint size = BinaryPrimitives.ReadUInt32LittleEndian(record[RecordSizeOffset..]);
			if (size == 0 || offset + size > buffer.Length)
			{
				break;
			}

			var payload = record[RecordPayloadOffset..(int)size];

			switch (relationship)
			{
				case SystemInfoApi.RelationProcessorCore:
					cores.Add(ReadProcessorCore(payload, groupBaseIndex, coreIndex++));
					break;

				case SystemInfoApi.RelationNumaNode:
					numaNodes.Add(ReadNumaNode(payload, groupBaseIndex));
					break;

				case SystemInfoApi.RelationCache:
					caches.Add(ReadCache(payload, groupBaseIndex));
					break;

				case SystemInfoApi.RelationProcessorPackage:
					packageCount++;
					break;
			}

			offset += (int)size;
		}

		// EfficiencyClass is a relative ranking, not an absolute one: the documented meaning is that a
		// higher value is a faster core, with no fixed scale. So P/E can only be decided by comparing
		// against the highest class actually present on this CPU.
		byte highestEfficiencyClass = cores.Count > 0 ? cores.Max(core => core.EfficiencyClass) : (byte)0;
		bool hybrid = cores.Select(core => core.EfficiencyClass).Distinct().Count() > 1;

		var classified = cores
			.Select(core => core with
			{
				Class = !hybrid
					? CoreClass.Uniform
					: (core.EfficiencyClass == highestEfficiencyClass ? CoreClass.Performance : CoreClass.Efficiency)
			})
			.ToList();

		return new CpuTopology
		{
			PhysicalCores = classified,
			NumaNodes = numaNodes,
			Caches = caches,
			LogicalProcessorCount = totalLogicalProcessors > 0 ? totalLogicalProcessors : Environment.ProcessorCount,
			PackageCount = Math.Max(1, packageCount),
			ActiveProcessorGroupCount = activeGroupCount > 0 ? activeGroupCount : (ushort)1
		};
	}

	private static byte[] QueryAll()
	{
		uint length = 0;
		if (SystemInfoApi.GetLogicalProcessorInformationEx(SystemInfoApi.RelationAll, null, ref length))
		{
			// Succeeding with a null buffer would mean there is nothing to report.
			return Array.Empty<byte>();
		}

		int error = Marshal.GetLastWin32Error();
		if (error != SystemInfoApi.ErrorInsufficientBuffer)
		{
			throw new Win32Exception(error, "Could not determine the size of the processor topology buffer.");
		}

		byte[] buffer = new byte[length];
		if (!SystemInfoApi.GetLogicalProcessorInformationEx(SystemInfoApi.RelationAll, buffer, ref length))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the processor topology.");
		}

		// The second call can report a smaller length than the probe reserved.
		return length < buffer.Length ? buffer[..(int)length] : buffer;
	}

	/// <summary>
	/// Windows numbers logical processors per group, but every mask LatencyBench works with is a flat
	/// system-wide index. This builds the offset that converts one to the other.
	/// </summary>
	private static int[] ReadGroupBaseIndexes(byte[] buffer, out ushort activeGroupCount, out int totalLogicalProcessors)
	{
		activeGroupCount = 0;
		totalLogicalProcessors = 0;

		int offset = 0;
		while (offset < buffer.Length)
		{
			var record = buffer.AsSpan(offset);
			uint relationship = BinaryPrimitives.ReadUInt32LittleEndian(record[RecordRelationshipOffset..]);
			uint size = BinaryPrimitives.ReadUInt32LittleEndian(record[RecordSizeOffset..]);
			if (size == 0 || offset + size > buffer.Length)
			{
				break;
			}

			if (relationship == SystemInfoApi.RelationGroup)
			{
				var payload = record[RecordPayloadOffset..(int)size];
				activeGroupCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);

				var baseIndexes = new int[activeGroupCount];
				int running = 0;
				for (int group = 0; group < activeGroupCount; group++)
				{
					int groupInfoOffset = 24 + (group * ProcessorGroupInfoSize);
					if (groupInfoOffset + ProcessorGroupInfoSize > payload.Length)
					{
						break;
					}

					baseIndexes[group] = running;
					running += payload[groupInfoOffset + 1]; // ActiveProcessorCount
				}

				totalLogicalProcessors = running;
				return baseIndexes;
			}

			offset += (int)size;
		}

		// No RelationGroup record: a single-group machine, so group-relative indexes are already global.
		return new int[] { 0 };
	}

	private static PhysicalCore ReadProcessorCore(ReadOnlySpan<byte> payload, int[] groupBaseIndex, int coreIndex)
	{
		byte flags = payload[0];
		byte efficiencyClass = payload[1];
		ushort groupCount = BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]);
		if (groupCount == 0)
		{
			groupCount = 1;
		}

		var logicalProcessors = new List<int>();
		ushort primaryGroup = 0;
		for (int i = 0; i < groupCount; i++)
		{
			int maskOffset = 24 + (i * GroupAffinitySize);
			if (maskOffset + GroupAffinitySize > payload.Length)
			{
				break;
			}

			ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(payload[maskOffset..]);
			ushort group = BinaryPrimitives.ReadUInt16LittleEndian(payload[(maskOffset + 8)..]);
			if (i == 0)
			{
				primaryGroup = group;
			}

			AddSetBits(mask, group, groupBaseIndex, logicalProcessors);
		}

		// A core with more than one logical processor is running SMT regardless of what Flags says;
		// the flag is checked as well because some virtualised CPUs report it without the extra
		// logical processor being visible in the mask.
		bool smt = (flags & SystemInfoApi.LtpPcSmt) != 0 || logicalProcessors.Count > 1;

		return new PhysicalCore(coreIndex, logicalProcessors, primaryGroup, efficiencyClass, CoreClass.Uniform, smt);
	}

	private static NumaNode ReadNumaNode(ReadOnlySpan<byte> payload, int[] groupBaseIndex)
	{
		int nodeNumber = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);

		// Windows 11 repurposed two bytes of this record's Reserved block as GroupCount. On earlier
		// builds those bytes are zero, which is why a zero count is normalised to one instead of
		// being treated as "no groups".
		ushort groupCount = payload.Length >= 24 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[22..]) : (ushort)1;
		if (groupCount == 0)
		{
			groupCount = 1;
		}

		var logicalProcessors = new List<int>();
		for (int i = 0; i < groupCount; i++)
		{
			int maskOffset = 24 + (i * GroupAffinitySize);
			if (maskOffset + GroupAffinitySize > payload.Length)
			{
				break;
			}

			ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(payload[maskOffset..]);
			ushort group = BinaryPrimitives.ReadUInt16LittleEndian(payload[(maskOffset + 8)..]);
			AddSetBits(mask, group, groupBaseIndex, logicalProcessors);
		}

		return new NumaNode(nodeNumber, logicalProcessors);
	}

	private static CacheLevel ReadCache(ReadOnlySpan<byte> payload, int[] groupBaseIndex)
	{
		byte level = payload[0];
		ushort lineSize = BinaryPrimitives.ReadUInt16LittleEndian(payload[2..]);
		uint cacheSize = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
		uint cacheType = BinaryPrimitives.ReadUInt32LittleEndian(payload[8..]);

		ushort groupCount = payload.Length >= 32 ? BinaryPrimitives.ReadUInt16LittleEndian(payload[30..]) : (ushort)1;
		if (groupCount == 0)
		{
			groupCount = 1;
		}

		var logicalProcessors = new List<int>();
		for (int i = 0; i < groupCount; i++)
		{
			int maskOffset = 32 + (i * GroupAffinitySize);
			if (maskOffset + GroupAffinitySize > payload.Length)
			{
				break;
			}

			ulong mask = BinaryPrimitives.ReadUInt64LittleEndian(payload[maskOffset..]);
			ushort group = BinaryPrimitives.ReadUInt16LittleEndian(payload[(maskOffset + 8)..]);
			AddSetBits(mask, group, groupBaseIndex, logicalProcessors);
		}

		return new CacheLevel(level, DescribeCacheType(cacheType), cacheSize, lineSize, logicalProcessors);
	}

	private static string DescribeCacheType(uint cacheType) => cacheType switch
	{
		0 => "Unified",
		1 => "Instruction",
		2 => "Data",
		3 => "Trace",
		_ => "Unknown"
	};

	private static void AddSetBits(ulong mask, ushort group, int[] groupBaseIndex, List<int> destination)
	{
		int groupBase = group < groupBaseIndex.Length ? groupBaseIndex[group] : 0;
		while (mask != 0)
		{
			int bit = System.Numerics.BitOperations.TrailingZeroCount(mask);
			destination.Add(groupBase + bit);
			mask &= mask - 1;
		}
	}
}
