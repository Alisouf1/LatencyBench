using System;
using LatencyBench.Core.Tweaks;

namespace LatencyBench.Tests;

/// <summary>
/// Every power GUID is validated before it reaches an HKLM path or a powercfg argument.
///
/// Not all of these are constants: PowerCfgAcValueTweak.Revert reads the scheme GUID back out of
/// tweak-backups.json - a file under %LocalAppData% that any process running as the user can edit -
/// and passes it to SetAcValueIndex, which interpolates it into a registry path. Corruption is a more
/// likely cause than tampering, but the consequence is the same.
///
/// These tests assert rejection only. The accepting paths need real hardware, an elevated process, and
/// would mutate the machine's power configuration, so they are covered by the existing
/// PowerCfgRunner tests rather than here.
/// </summary>
public sealed class PowerCfgGuidValidationTests
{
    private static readonly string[] InvalidGuids =
    {
        null!,
        "",
        "   ",
        "not-a-guid",
        @"..\..\Services",
        @"8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c\..\Evil",
        "8c5e7fda-e8bf-4a96-9a85",                       // truncated
        "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c-extra",    // trailing junk
        "*",
        "%SystemRoot%",
        "8c5e7fda_e8bf_4a96_9a85_a6e23a8c635c",          // underscores, not a GUID
    };

    public static TheoryData<string> Invalid()
    {
        var data = new TheoryData<string>();
        foreach (string value in InvalidGuids)
        {
            data.Add(value);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void SchemeExistsRejectsNonGuids(string schemeGuid)
    {
        Assert.Throws<ArgumentException>(() => new PowerCfgRunner().SchemeExists(schemeGuid));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void SetActiveSchemeRejectsNonGuids(string schemeGuid)
    {
        Assert.Throws<ArgumentException>(() => new PowerCfgRunner().SetActiveScheme(schemeGuid));
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public void QueryAcValueIndexRejectsANonGuidScheme(string schemeGuid)
    {
        Assert.Throws<ArgumentException>(() => new PowerCfgRunner().QueryAcValueIndex(
            schemeGuid,
            "54533251-82be-4824-96c1-47b60b740d00",
            "893dee8e-2bef-41e0-89c6-b55d0929964c"));
    }

    [Fact]
    public void QueryAcValueIndexRejectsANonGuidSubgroup()
    {
        Assert.Throws<ArgumentException>(() => new PowerCfgRunner().QueryAcValueIndex(
            "381b4222-f694-41f0-9685-ff5bb260df2e",
            @"..\..\Services",
            "893dee8e-2bef-41e0-89c6-b55d0929964c"));
    }

    [Fact]
    public void QueryAcValueIndexRejectsANonGuidSetting()
    {
        Assert.Throws<ArgumentException>(() => new PowerCfgRunner().QueryAcValueIndex(
            "381b4222-f694-41f0-9685-ff5bb260df2e",
            "54533251-82be-4824-96c1-47b60b740d00",
            "*"));
    }

    [Fact]
    public void SetAcValueIndexRejectsEachGuidPositionIndependently()
    {
        var runner = new PowerCfgRunner();
        const string valid = "381b4222-f694-41f0-9685-ff5bb260df2e";

        Assert.Throws<ArgumentException>(() => runner.SetAcValueIndex("bad", valid, valid, 0));
        Assert.Throws<ArgumentException>(() => runner.SetAcValueIndex(valid, "bad", valid, 0));
        Assert.Throws<ArgumentException>(() => runner.SetAcValueIndex(valid, valid, "bad", 0));
    }

    [Fact]
    public void TheRejectionNamesTheParameterAndPointsAtTheLikelyCause()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new PowerCfgRunner().SetActiveScheme("not-a-guid"));

        Assert.Equal("schemeGuid", ex.ParamName);
        Assert.Contains("corrupt", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BracedAndPlainGuidFormsAreBothAccepted()
    {
        // Windows writes power scheme keys without braces, but a value round-tripped through a .NET
        // Guid can carry them. Rejecting one form would break reverting a legitimately saved setting.
        var runner = new PowerCfgRunner();

        // Neither of these exists, so the call returns false rather than throwing - which is the
        // point: validation passed and the lookup ran.
        Assert.False(runner.SchemeExists("{00000000-0000-0000-0000-000000000001}"));
        Assert.False(runner.SchemeExists("00000000-0000-0000-0000-000000000001"));
    }
}
