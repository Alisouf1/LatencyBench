using System.Runtime.CompilerServices;
using System.Windows;

// Lets LatencyBench.Tests exercise a handful of ViewModel members (e.g. ProcessTuningViewModel's
// SetPriority/SetIoPriority/ClearAffinity, AffinityViewModel's RefreshCoreUsage) directly rather than
// only through their public RelayCommand wrappers, matching how LatencyBench.Core already exposes
// test-only seams as "public virtual" rather than widening these to public here.
[assembly: InternalsVisibleTo("LatencyBench.Tests")]

[assembly: ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
