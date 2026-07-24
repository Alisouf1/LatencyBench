using System;
using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.HidTesting.Models;
using LatencyBench.Core.Interop;

namespace LatencyBench.Core.HidTesting;

public sealed class HidDeviceEnumerator
{
	public List<HidDeviceInfo> EnumerateDevices()
	{
		List<HidDeviceInfo> list = new List<HidDeviceInfo>();
		foreach (KeyValuePair<uint, string> item in SetupApi.EnumerateDeviceInterfacePaths(SetupApi.GUID_DEVINTERFACE_HID))
		{
			item.Deconstruct(out var key, out var value);
			uint devInst = key;
			string devicePath = value;
			string deviceId = CfgMgr32.GetDeviceId(devInst);
			if (deviceId != null)
			{
				string friendlyName = CfgMgr32.GetStringProperty(devInst, 13u) ?? CfgMgr32.GetStringProperty(devInst, 1u) ?? deviceId;
				string stringProperty = CfgMgr32.GetStringProperty(devInst, 8u);
				(ushort, ushort)? tuple = HidApi.TryGetUsage(devicePath);
				list.Add(new HidDeviceInfo
				{
					InstanceId = deviceId,
					FriendlyName = friendlyName,
					DevicePath = devicePath,
					DeviceClass = stringProperty,
					UsagePage = (tuple?.Item1 ?? 0),
					Usage = (tuple?.Item2 ?? 0)
				});
			}
		}
		return list.OrderBy<HidDeviceInfo, string>((HidDeviceInfo d) => d.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
	}
}
