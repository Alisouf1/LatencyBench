using System;
using System.Collections.Generic;
using System.Linq;

namespace LatencyBench.Core.Models;

public static class UsbDeviceNodeExtensions
{
	public static List<string> GetDescendantDeviceNames(this UsbDeviceNode node)
	{
		List<string> names = new List<string>();
		foreach (UsbDeviceNode child in node.Children)
		{
			Visit(child);
		}
		return names.Distinct().ToList();
		void Visit(UsbDeviceNode usbDeviceNode)
		{
			if (usbDeviceNode.Children.Count == 0 && !usbDeviceNode.IsHub)
			{
				names.Add(usbDeviceNode.FriendlyName);
			}
			foreach (UsbDeviceNode child2 in usbDeviceNode.Children)
			{
				Visit(child2);
			}
		}
	}

	public static string GetBestDisplayName(this UsbDeviceNode node)
	{
		List<UsbDeviceNode> subtree = new List<UsbDeviceNode> { node };
		Collect(node);
		string? text = subtree.Select((UsbDeviceNode n) => n.FriendlyName).FirstOrDefault((string n) => !n.StartsWith("HID", StringComparison.OrdinalIgnoreCase) && !n.StartsWith("USB ", StringComparison.OrdinalIgnoreCase) && !n.Contains("compliant", StringComparison.OrdinalIgnoreCase) && !n.Contains("VID_", StringComparison.OrdinalIgnoreCase));
		if (text != null)
		{
			return text;
		}
		List<string> source = (from n in subtree
			select n.DeviceClass into c
			where c != null
			select c).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
		List<string> list = new List<string>();
		if (source.Any((string c) => string.Equals(c, "Mouse", StringComparison.OrdinalIgnoreCase)))
		{
			list.Add("Mouse");
		}
		if (source.Any((string c) => string.Equals(c, "Keyboard", StringComparison.OrdinalIgnoreCase)))
		{
			list.Add("Keyboard");
		}
		if (source.Any((string c) => string.Equals(c, "DiskDrive", StringComparison.OrdinalIgnoreCase)))
		{
			list.Add("Storage");
		}
		if (source.Any((string c) => string.Equals(c, "Media", StringComparison.OrdinalIgnoreCase) || string.Equals(c, "AudioEndpoint", StringComparison.OrdinalIgnoreCase)))
		{
			list.Add("Audio device");
		}
		return (list.Count > 0) ? string.Join(" + ", list) : node.FriendlyName;
		void Collect(UsbDeviceNode current)
		{
			foreach (UsbDeviceNode child in current.Children)
			{
				subtree.Add(child);
				Collect(child);
			}
		}
	}

	public static List<string> GetAttachedPhysicalDeviceNames(this UsbDeviceNode node)
	{
		List<string> names = new List<string>();
		Visit(node);
		return names.Distinct().ToList();
		void Visit(UsbDeviceNode current)
		{
			foreach (UsbDeviceNode child in current.Children)
			{
				if (child.IsHub)
				{
					Visit(child);
				}
				else if (current.IsHub || current.IsHostController)
				{
					string bestDisplayName = child.GetBestDisplayName();
					List<string> list = names;
					int? portNumber = child.PortNumber;
					object item;
					if (portNumber.HasValue)
					{
						int valueOrDefault = portNumber.GetValueOrDefault();
						item = $"{bestDisplayName} (port {valueOrDefault})";
					}
					else
					{
						item = bestDisplayName;
					}
					list.Add((string)item);
				}
			}
		}
	}

	/// <summary>
	/// Walks up from any device instance ID (typically a HID collection or interface, several tree
	/// levels below the actual product) to the nearest hub-attached ancestor — the physical device slot
	/// with a real port number, as opposed to the intermediate composite-device interface/collection
	/// nodes which each report their own sub-index as if it were a port.
	///
	/// This is the resolution step that makes <see cref="GetBestDisplayName"/> useful for a device you
	/// only know by instance ID: a HID collection's own FriendlyName is near-universally a generic
	/// Windows string ("HID-compliant mouse"), but its physical ancestor's subtree usually contains the
	/// device's real product name from a sibling interface, which GetBestDisplayName finds.
	/// </summary>
	public static (UsbDeviceNode? PhysicalNode, UsbDeviceNode? HostController) FindPhysicalDeviceAncestor(
		this IReadOnlyList<UsbDeviceNode> hostControllers, string instanceId)
	{
		foreach (var hostController in hostControllers)
		{
			var path = FindPath(hostController, instanceId, []);
			if (path is null)
			{
				continue;
			}

			for (var i = path.Count - 1; i >= 1; i--)
			{
				if (path[i - 1].IsHub)
				{
					return (path[i], hostController);
				}
			}

			return (null, hostController);
		}

		return (null, null);
	}

	private static List<UsbDeviceNode>? FindPath(UsbDeviceNode node, string instanceId, List<UsbDeviceNode> pathSoFar)
	{
		var path = new List<UsbDeviceNode>(pathSoFar) { node };
		if (string.Equals(node.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase))
		{
			return path;
		}

		foreach (var child in node.Children)
		{
			var found = FindPath(child, instanceId, path);
			if (found is not null)
			{
				return found;
			}
		}

		return null;
	}
}
