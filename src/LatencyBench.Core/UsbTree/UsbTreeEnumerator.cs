using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using LatencyBench.Core.Interop;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.UsbTree;

public sealed class UsbTreeEnumerator
{
	private static readonly Regex VidPidPattern = new Regex("VID_([0-9A-Fa-f]{4})(?:&PID_([0-9A-Fa-f]{4}))?", RegexOptions.Compiled);

	public List<UsbDeviceNode> EnumerateHostControllers()
	{
		Guid classGuid = SetupApi.GUID_DEVCLASS_USB;
		nint num = SetupApi.SetupDiGetClassDevs(ref classGuid, null, IntPtr.Zero, 2u);
		if (num == IntPtr.Zero || num == new IntPtr(-1))
		{
			return new List<UsbDeviceNode>();
		}
		try
		{
			HashSet<uint> hashSet = new HashSet<uint>();
			Dictionary<uint, string> hubDevicePaths = SetupApi.EnumerateDeviceInterfacePaths(SetupApi.GUID_DEVINTERFACE_USB_HUB);
			uint num2 = 0u;
			SP_DEVINFO_DATA deviceInfoData = new SP_DEVINFO_DATA
			{
				cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>()
			};
			while (SetupApi.SetupDiEnumDeviceInfo(num, num2, ref deviceInfoData))
			{
				hashSet.Add(deviceInfoData.DevInst);
				num2++;
				deviceInfoData.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();
			}
			List<UsbDeviceNode> list = new List<UsbDeviceNode>();
			foreach (uint item in hashSet)
			{
				if (CfgMgr32.CM_Get_Parent(out var pdnDevInst, item, 0u) != 0 || !hashSet.Contains(pdnDevInst))
				{
					UsbDeviceNode usbDeviceNode = BuildNode(item, null, hubDevicePaths, isHostController: true);
					if (usbDeviceNode != null)
					{
						list.Add(usbDeviceNode);
					}
				}
			}
			return list.OrderBy<UsbDeviceNode, string>((UsbDeviceNode n) => n.FriendlyName, StringComparer.OrdinalIgnoreCase).ThenBy<UsbDeviceNode, string>((UsbDeviceNode n) => n.InstanceId, StringComparer.OrdinalIgnoreCase).ToList();
		}
		finally
		{
			SetupApi.SetupDiDestroyDeviceInfoList(num);
		}
	}

	private static string? Clean(string? name)
	{
		string text = name?.Trim();
		return string.IsNullOrEmpty(text) ? null : text;
	}

	private UsbDeviceNode? BuildNode(uint devInst, int? portNumber, Dictionary<uint, string> hubDevicePaths, bool isHostController)
	{
		string deviceId = CfgMgr32.GetDeviceId(devInst);
		if (deviceId == null)
		{
			return null;
		}
		string friendlyName = Clean((!isHostController) ? CfgMgr32.GetBusReportedDeviceDesc(devInst) : null) ?? Clean(CfgMgr32.GetStringProperty(devInst, 13u)) ?? Clean(CfgMgr32.GetStringProperty(devInst, 1u)) ?? deviceId;
		string stringProperty = CfgMgr32.GetStringProperty(devInst, 8u);
		bool isWorking = CfgMgr32.IsStarted(devInst);
		bool isHub = !isHostController && hubDevicePaths.ContainsKey(devInst);
		string vendorId = null;
		string productId = null;
		Match match = VidPidPattern.Match(deviceId);
		if (match.Success)
		{
			vendorId = match.Groups[1].Value.ToUpperInvariant();
			if (match.Groups[2].Success)
			{
				productId = match.Groups[2].Value.ToUpperInvariant();
			}
		}
		UsbSpeed speed = UsbSpeed.Unknown;
		if (portNumber.HasValue)
		{
			int valueOrDefault = portNumber.GetValueOrDefault();
			if (CfgMgr32.CM_Get_Parent(out var pdnDevInst, devInst, 0u) == 0 && hubDevicePaths.TryGetValue(pdnDevInst, out string value))
			{
				speed = UsbIoctl.TryGetSpeed(value, valueOrDefault);
			}
		}
		UsbDeviceNode usbDeviceNode = new UsbDeviceNode
		{
			InstanceId = deviceId,
			FriendlyName = friendlyName,
			DeviceClass = stringProperty,
			VendorId = vendorId,
			ProductId = productId,
			PortNumber = portNumber,
			IsHub = isHub,
			IsHostController = isHostController,
			IsWorking = isWorking,
			Speed = speed
		};
		if (CfgMgr32.CM_Get_Child(out var pdnDevInst2, devInst, 0u) == 0)
		{
			AppendSiblingChain(usbDeviceNode, pdnDevInst2, hubDevicePaths);
		}
		return usbDeviceNode;
	}

	private void AppendSiblingChain(UsbDeviceNode parent, uint firstChildDevInst, Dictionary<uint, string> hubDevicePaths)
	{
		uint num = firstChildDevInst;
		while (true)
		{
			int? dwordProperty = CfgMgr32.GetDwordProperty(num, 29u);
			UsbDeviceNode usbDeviceNode = BuildNode(num, dwordProperty, hubDevicePaths, isHostController: false);
			if (usbDeviceNode != null)
			{
				parent.Children.Add(usbDeviceNode);
			}
			if (CfgMgr32.CM_Get_Sibling(out var pdnDevInst, num, 0u) != 0)
			{
				break;
			}
			num = pdnDevInst;
		}
	}
}
