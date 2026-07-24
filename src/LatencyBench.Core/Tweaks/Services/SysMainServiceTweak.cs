using System;
using System.ServiceProcess;
using Microsoft.Win32;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks.Services;

public sealed class SysMainServiceTweak : ITweak
{
	private const string ServiceName = "SysMain";

	public TweakDefinition Definition { get; } = new TweakDefinition
	{
		Id = "aggressive.sysmain-disable",
		Category = TweakCategory.Aggressive,
		Name = "Disable SysMain (Superfetch)",
		Description = "Stops and disables the SysMain service. Frees background disk/memory activity, but was designed to speed up app launches on HDDs — less useful (and sometimes counterproductive) on modern SSD systems.",
		Risk = TweakRisk.Aggressive
	};

	public TweakState GetState()
	{
		try
		{
			ServiceController serviceController = new ServiceController("SysMain");
			try
			{
				if (serviceController.StartType == ServiceStartMode.Disabled && serviceController.Status == ServiceControllerStatus.Stopped)
				{
					return TweakState.Applied;
				}
				return TweakState.NotApplied;
			}
			finally
			{
				((IDisposable)serviceController)?.Dispose();
			}
		}
		catch (InvalidOperationException)
		{
			return TweakState.Unknown;
		}
	}

	public void Apply()
	{
		ServiceController serviceController = new ServiceController("SysMain");
		try
		{
			if (serviceController.Status != ServiceControllerStatus.Stopped)
			{
				serviceController.Stop();
				serviceController.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(15.0));
			}
			SetStartMode(ServiceStartMode.Disabled);
		}
		finally
		{
			((IDisposable)serviceController)?.Dispose();
		}
	}

	public void Revert()
	{
		SetStartMode(ServiceStartMode.Automatic);
		ServiceController serviceController = new ServiceController("SysMain");
		try
		{
			serviceController.Start();
			serviceController.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(15.0));
		}
		finally
		{
			((IDisposable)serviceController)?.Dispose();
		}
	}

	private static void SetStartMode(ServiceStartMode mode)
	{
		using RegistryKey registryKey = Registry.LocalMachine.OpenSubKey("SYSTEM\\CurrentControlSet\\Services\\SysMain", writable: true) ?? throw new InvalidOperationException("Service 'SysMain' registry key not found.");
		int startValue = mode switch
		{
			ServiceStartMode.Automatic => 2,
			ServiceStartMode.Manual => 3,
			ServiceStartMode.Disabled => 4,
			_ => throw new ArgumentOutOfRangeException("mode"),
		};
		registryKey.SetValue("Start", startValue, RegistryValueKind.DWord);
	}
}
