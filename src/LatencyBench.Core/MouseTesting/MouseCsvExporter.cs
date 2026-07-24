using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using LatencyBench.Core.MouseTesting.Models;

namespace LatencyBench.Core.MouseTesting;

public static class MouseCsvExporter
{
	public static void Write(TextWriter writer, IReadOnlyList<MouseSample> samples, double ticksPerMillisecond)
	{
		writer.WriteLine("time_ms,dx,dy,button_flags");
		if (samples.Count == 0)
		{
			return;
		}
		long timestampTicks = samples[0].TimestampTicks;
		foreach (MouseSample sample in samples)
		{
			double value = (double)(sample.TimestampTicks - timestampTicks) / ticksPerMillisecond;
			IFormatProvider invariantCulture = CultureInfo.InvariantCulture;
			DefaultInterpolatedStringHandler handler = new DefaultInterpolatedStringHandler(3, 4, invariantCulture);
			handler.AppendFormatted(value, "0.###");
			handler.AppendLiteral(",");
			handler.AppendFormatted(sample.Dx);
			handler.AppendLiteral(",");
			handler.AppendFormatted(sample.Dy);
			handler.AppendLiteral(",");
			handler.AppendFormatted(sample.ButtonFlags);
			writer.WriteLine(string.Create(invariantCulture, ref handler));
		}
	}
}
