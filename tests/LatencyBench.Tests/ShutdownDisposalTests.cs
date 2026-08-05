using System;
using System.Linq;
using System.Reflection;
using LatencyBench.App.ViewModels;

namespace LatencyBench.Tests;

/// <summary>
/// Guards deterministic cleanup of the OS resources the app holds for its whole lifetime.
///
/// These leaked only until process exit, which is why nothing surfaced them: an open registry key and
/// a cancellation source plus worker threads are reclaimed when the process ends. That is not
/// deterministic cleanup, and it hides a genuine leak the moment either owner is used somewhere
/// longer-lived than the main window.
/// </summary>
public sealed class ShutdownDisposalTests
{
    /// <summary>
    /// Every IDisposable the app owns at window scope must be released by MainViewModel.Shutdown.
    /// Asserted by reflection rather than by name, so a disposable added later fails this test
    /// instead of quietly leaking.
    /// </summary>
    [Fact]
    public void EveryDisposableViewModelIsReleasedOnShutdown()
    {
        Type[] disposableViewModels = typeof(MainViewModel).Assembly
            .GetTypes()
            .Where(t => t.Namespace == typeof(MainViewModel).Namespace)
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IDisposable).IsAssignableFrom(t))
            .ToArray();

        Assert.NotEmpty(disposableViewModels);

        // The properties MainViewModel exposes for each tab.
        PropertyInfo[] tabProperties = typeof(MainViewModel)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => disposableViewModels.Contains(p.PropertyType))
            .ToArray();

        Assert.NotEmpty(tabProperties);

        string shutdownBody = ReadShutdownSource();

        foreach (PropertyInfo tab in tabProperties)
        {
            Assert.True(
                shutdownBody.Contains($"{tab.Name}.Dispose()", StringComparison.Ordinal) ||
                shutdownBody.Contains($"{tab.Name}.Shutdown()", StringComparison.Ordinal),
                $"MainViewModel.Shutdown does not release '{tab.Name}' ({tab.PropertyType.Name}), " +
                "which implements IDisposable. Add it to Shutdown or it leaks until process exit.");
        }
    }

    [Fact]
    public void PortTestViewModelIsDisposable()
    {
        // It owns CpuLoadGenerator, which holds a CancellationTokenSource and the worker threads it
        // starts for load testing. Stop() runs after each test; Dispose never did.
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(PortTestViewModel)));
    }

    [Fact]
    public void ShutdownReleasesTheSharedAffinityService()
    {
        // InterruptAffinityService holds an open HKLM key for the device-enum root and is shared by
        // the Affinity, MSI and Optimize tabs. It was a constructor local, so nothing released it.
        string shutdownBody = ReadShutdownSource();

        Assert.Contains("_affinityService.Dispose()", shutdownBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads MainViewModel.Shutdown from source. Asserting on the source rather than on behaviour is
    /// deliberate: constructing MainViewModel enumerates the real device tree and opens a real
    /// registry key, which a unit test must not do, and there is no seam to observe disposal through
    /// without adding one purely for the test.
    /// </summary>
    private static string ReadShutdownSource()
    {
        string path = TestPaths.RepoFile(@"src\LatencyBench.App\ViewModels\MainViewModel.cs");
        string source = System.IO.File.ReadAllText(path);

        int start = source.IndexOf("public void Shutdown()", StringComparison.Ordinal);
        Assert.True(start >= 0, "MainViewModel.Shutdown was not found - this test needs updating.");

        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not delimit MainViewModel.Shutdown.");

        return source[start..end];
    }
}

/// <summary>Locates repository files from the test binary's location.</summary>
internal static class TestPaths
{
    public static string RepoFile(string relativePath)
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !System.IO.Directory.Exists(System.IO.Path.Combine(directory.FullName, "src")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return System.IO.Path.Combine(directory!.FullName, relativePath);
    }
}
