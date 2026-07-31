using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Network;

public sealed class NetworkAdapterEnumerator
{
	private static readonly Guid GuidDevClassNet = new Guid("4D36E972-E325-11CE-BFC1-08002BE10318");

	/// <summary>
	/// Physical adapters only — the ones that actually raise interrupts and have power management.
	/// <para>
	/// The Net setup class is full of software pseudo-adapters: every WAN Miniport (PPPoE, PPTP,
	/// IKEv2, L2TP, IP, IPv6, SSTP, Network Monitor), the Kernel Debug Network Adapter, and any VPN
	/// or hypervisor virtual switch. On a plain desktop they outnumber the real NIC nine to one.
	/// They enumerate under ROOT\ rather than a hardware bus, which is what this filters on.
	/// Including them made "disable power management on network adapters" walk devices that have no
	/// power management, and would have had the recommendation engine reasoning about ten adapters
	/// on a machine with one.
	/// </para>
	/// </summary>
	public List<NetworkAdapterInfo> EnumeratePhysicalAdapters()
	{
		return EnumerateAdapters()
			.Where(adapter => IsHardwareBacked(adapter.InstanceId))
			.ToList();
	}

	private static bool IsHardwareBacked(string instanceId)
	{
		// A real NIC is enumerated by the bus it sits on. Anything the software root enumerated is a
		// virtual adapter by construction.
		return instanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase)
			|| instanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase)
			|| instanceId.StartsWith("SD\\", StringComparison.OrdinalIgnoreCase)
			|| instanceId.StartsWith("PCMCIA\\", StringComparison.OrdinalIgnoreCase);
	}

	public List<NetworkAdapterInfo> EnumerateAdapters()
	{
		Guid classGuid = GuidDevClassNet;
		nint num = SetupApi.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 2u);
		if (num == IntPtr.Zero || num == new IntPtr(-1))
		{
			return new List<NetworkAdapterInfo>();
		}
		try
		{
			List<NetworkAdapterInfo> list = new List<NetworkAdapterInfo>();
			uint num2 = 0u;
			SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA
			{
				cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
			};
			while (SetupApi.SetupDiEnumDeviceInfo(num, num2, ref deviceInfoData))
			{
				string? deviceId = CfgMgr32.GetDeviceId(deviceInfoData.DevInst);
				if (deviceId != null)
				{
					string friendlyName = CfgMgr32.GetStringProperty(deviceInfoData.DevInst, 13u) ?? CfgMgr32.GetStringProperty(deviceInfoData.DevInst, 1u) ?? deviceId;
					list.Add(new NetworkAdapterInfo
					{
						InstanceId = deviceId,
						FriendlyName = friendlyName
					});
				}
				num2++;
				deviceInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
			}
			return list;
		}
		finally
		{
			SetupApi.SetupDiDestroyDeviceInfoList(num);
		}
	}
}
