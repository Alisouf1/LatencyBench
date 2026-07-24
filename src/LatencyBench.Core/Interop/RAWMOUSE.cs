namespace LatencyBench.Core.Interop;

internal struct RAWMOUSE
{
	public ushort usFlags;

	public ushort usButtonFlags;

	public ushort usButtonData;

	public uint ulRawButtons;

	public int lLastX;

	public int lLastY;

	public uint ulExtraInformation;
}
