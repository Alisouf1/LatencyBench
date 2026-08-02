using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Tweaks;

public interface ITweak
{
    TweakDefinition Definition { get; }

    TweakState GetState();

    void Apply();

    void Revert();
}
