namespace LatencyBench.Core.Models;

public enum InterruptAffinityPolicy
{
	MachineDefault,
	AllCloseProcessors,
	OneCloseProcessor,
	AllProcessorsInMachine,
	SpecifiedProcessors,
	SpreadMessagesAcrossAllProcessors
}
