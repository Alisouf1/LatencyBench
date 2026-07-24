using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.Network;

public sealed class NetworkAdapterEnumerator
{
	private static readonly Guid GuidDevClassNet = new Guid("4D36E972-E325-11CE-BFC1-08002BE10318");

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
				string deviceId = CfgMgr32.GetDeviceId(deviceInfoData.DevInst);
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
