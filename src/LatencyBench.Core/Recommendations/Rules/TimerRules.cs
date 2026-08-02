using System.Collections.Generic;
using LatencyBench.Core.Recommendations.Models;
using LatencyBench.Core.Timers;
using LatencyBench.Core.Tweaks.Models;

namespace LatencyBench.Core.Recommendations.Rules;

/// <summary>
/// Flags a forced platform clock.
/// <para>
/// This rule exists to undo advice, not to give it. "Enable HPET" is one of the most widely repeated
/// latency tips and it is wrong on essentially every machine sold in the last decade.
/// </para>
/// </summary>
public sealed class PlatformClockRule : IRecommendationRule
{
    public string Id => "rec.system.platform-clock";

    public string Title => "Forced platform clock (HPET) is set";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        TimerState? timers = context.Timers;
        if (timers is null)
        {
            return new RuleOutcome.Undetermined(Id, Title, "Timer state has not been read yet.");
        }

        if (timers.UsePlatformClock == BcdFlagState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id,
                Title,
                timers.BootConfigurationError
                    ?? "The boot configuration could not be read. Run LatencyBench as administrator.");
        }

        if (timers.UsePlatformClock != BcdFlagState.On)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                "No forced platform clock is set, which is correct — Windows picks the best available " +
                "clock source on its own, and on modern hardware that is the invariant TSC.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.System,
            Reasoning =
                "Someone has set the useplatformclock boot option on this PC, which forces Windows to use " +
                "the platform timer — the HPET on most machines — as its clock source instead of choosing " +
                "one. Reading the HPET means an off-chip access costing on the order of a microsecond, " +
                "while the invariant TSC that Windows would otherwise pick is an on-core register read " +
                "costing tens of nanoseconds. Since every timestamp the kernel takes goes through that " +
                "path, forcing it adds overhead to everything, including the interrupt handling this app " +
                "exists to measure.",
            ExpectedBenefit =
                "Removing it lets Windows go back to the TSC. On a machine where this was set, this is " +
                "usually the single largest improvement available, and it is visible in a DPC/ISR trace.",
            Risks =
                "On genuinely old hardware with an unreliable TSC, Windows will simply keep using the HPET " +
                "after the option is removed, because the decision goes back to being its own. The setting " +
                "is a boot option, so a restart is needed either way.",
            Evidence = new List<Evidence>
            {
                new("Boot option useplatformclock is set to Yes.", "Boot configuration data"),
                new(
                    timers.UsePlatformTick == BcdFlagState.On
                        ? "useplatformtick is also set, which forces the platform timer for periodic ticks too."
                        : "useplatformtick is not set.",
                    "Boot configuration data")
            },
            // Deliberately manual. Writing to the boot configuration is a class of change this app
            // should not make on the user's behalf: a mistake there costs a boot, not a setting.
            Safety = SafetyAssessment.Build(
                Reversibility.Manual,
                BlastRadius.SystemWide,
                requiresReboot: true),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ManualOnly(
                "Open an administrator Command Prompt and run:  bcdedit /deletevalue useplatformclock\n" +
                "If useplatformtick is also set, remove it the same way. Restart, then re-run a DPC/ISR " +
                "trace and compare it against one taken beforehand."),
            RequiresReboot = true,
            ImpactScore = 95
        });
    }
}

/// <summary>
/// Explains the per-process timer resolution change, and offers the documented way back to the old
/// global behaviour.
/// </summary>
public sealed class TimerResolutionRule : IRecommendationRule
{
    public string Id => "rec.system.timer-resolution";

    public string Title => "Timer resolution is per-process on this build";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        TimerState? timers = context.Timers;
        if (timers is null)
        {
            return new RuleOutcome.Undetermined(Id, Title, "Timer state has not been read yet.");
        }

        if (!timers.UsesPerProcessTimerResolution)
        {
            return new RuleOutcome.NotApplicable(
                Id,
                Title,
                "This Windows build still applies a timer resolution request machine-wide, so there is " +
                "nothing to restore.");
        }

        if (timers.GlobalTimerResolutionRequested
            || context.StateOf("timer.global-resolution-requests") == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(
                Id, Title, "Global timer resolution requests are already restored.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.System,
            Reasoning =
                "From Windows 10 version 2004 onwards, a process asking for a finer timer resolution only " +
                "gets it for itself — other processes keep seeing the default. That change is why the " +
                "third-party utilities that claim to \"set your timer to 0.5 ms\" no longer do anything " +
                "outside their own process: they raise it, the setting applies to the utility, and the " +
                "game keeps running on whatever it requested for itself. " +
                "GlobalTimerResolutionRequests is the documented switch that restores the old behaviour.",
            ExpectedBenefit =
                "A game or engine that requests a fine timer gets it applied machine-wide again, so " +
                "background work is scheduled on the same fine tick rather than the coarse default. " +
                "If nothing on this PC requests a fine resolution, this changes nothing.",
            Risks =
                "A fine timer resolution across the whole system raises idle power draw and wakes cores " +
                "more often, because the scheduler tick fires far more frequently. On a laptop that is a " +
                "real battery cost. Needs a restart.",
            Evidence = new List<Evidence>
            {
                new($"System clock currently at {timers.CurrentMs:0.###} ms " +
                    $"(supported range {timers.FinestMs:0.###} to {timers.CoarsestMs:0.###} ms).",
                    "Kernel timer resolution"),
                new(
                    timers.IsRaised
                        ? "Something on this PC has already raised the timer above the idle default."
                        : "The timer is at the idle default — nothing is currently requesting a finer one.",
                    "Kernel timer resolution"),
                new($"Windows build {context.Profile.Windows.BuildNumber} is at or beyond " +
                    $"{TimerDiagnostics.PerProcessTimerResolutionBuild}, where requests became per-process.",
                    "Windows version")
            },
            Safety = SafetyAssessment.Build(
                Reversibility.AutomaticAfterRestart,
                BlastRadius.SystemWide,
                requiresReboot: true),
            // Inferred: whether it helps depends entirely on whether the software the user cares about
            // requests a fine timer at all.
            Confidence = RecommendationConfidence.Inferred,
            Action = new RecommendedAction.ApplyTweak("timer.global-resolution-requests"),
            RequiresReboot = true,
            ImpactScore = context.Profile.HasBattery ? 25 : 40
        });
    }
}

/// <summary>Multimedia class scheduler profile for games.</summary>
public sealed class MmcssGamesProfileRule : IRecommendationRule
{
    public string Id => "rec.cpu.mmcss-games";

    public string Title => "Multimedia scheduler profile for games";

    public RuleOutcome Evaluate(RecommendationContext context)
    {
        TweakState category = context.StateOf("mmcss.games-scheduling-category");
        TweakState sfio = context.StateOf("mmcss.games-sfio-priority");

        if (category == TweakState.Applied && sfio == TweakState.Applied)
        {
            return new RuleOutcome.NotApplicable(
                Id, Title, "The Games profile is already set to the high scheduling category and I/O priority.");
        }

        if (category == TweakState.Unknown && sfio == TweakState.Unknown)
        {
            return new RuleOutcome.Undetermined(
                Id, Title, "The multimedia scheduler profile could not be read.");
        }

        return new RuleOutcome.Applicable(new Recommendation
        {
            Id = Id,
            Title = Title,
            Category = RecommendationCategory.Cpu,
            Reasoning =
                "The multimedia class scheduler gives registered threads a guaranteed share of CPU time and " +
                "a scheduling category that outranks ordinary work. Games register with its \"Games\" task " +
                "profile, which ships at the Medium category. Raising it to High means the game's threads " +
                "are scheduled ahead of background work rather than alongside it.",
            ExpectedBenefit =
                "Fewer scheduling delays for a game competing with background activity. This only affects " +
                "software that actually registers with the scheduler — a game that does not is unchanged.",
            Risks =
                "Background tasks get less CPU while a registered application is running, which is the " +
                "intended effect. Reverted by restoring the original values, which are recorded first.",
            Evidence = new List<Evidence>
            {
                new(
                    category == TweakState.Applied
                        ? "Scheduling Category is already High."
                        : "Scheduling Category is at the shipped default rather than High.",
                    "Multimedia scheduler Games profile"),
                new(
                    sfio == TweakState.Applied
                        ? "SFIO Priority is already High."
                        : "SFIO Priority is at the shipped default rather than High.",
                    "Multimedia scheduler Games profile")
            },
            Safety = SafetyAssessment.Build(Reversibility.FullyAutomatic, BlastRadius.Subsystem),
            Confidence = RecommendationConfidence.Likely,
            Action = new RecommendedAction.ApplyTweak(
                category == TweakState.Applied ? "mmcss.games-sfio-priority" : "mmcss.games-scheduling-category"),
            ImpactScore = 40
        });
    }
}
