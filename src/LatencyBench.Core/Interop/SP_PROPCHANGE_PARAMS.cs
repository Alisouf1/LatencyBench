namespace LatencyBench.Core.Interop;

internal struct SP_PROPCHANGE_PARAMS
{
    public SP_CLASSINSTALL_HEADER ClassInstallHeader;

    public uint StateChange;

    public uint Scope;

    public uint HwProfile;
}
