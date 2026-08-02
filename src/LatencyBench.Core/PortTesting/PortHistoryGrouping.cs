using System.Collections.Generic;
using System.Linq;
using LatencyBench.Core.Models;

namespace LatencyBench.Core.PortTesting;

public static class PortHistoryGrouping
{
    public const int HighFrequencyThresholdHz = 100;

    public static bool IsHighFrequency(PortRankResult result)
    {
        return result.PollingRateHz.GetValueOrDefault() >= 100;
    }

    public static List<PortHistoryGroup> GroupByPort(IEnumerable<PortRankResult> results)
    {
        return (from g in (from r in results
                           group r by (PortLocation: r.PortLocation, IsHighFrequency: IsHighFrequency(r))).Select(delegate (IGrouping<(string PortLocation, bool IsHighFrequency), PortRankResult> g)
                       {
                           List<PortRankResult> list = g.OrderByDescending((PortRankResult r) => r.SavedAt).ToList();
                           PortRankResult best = (from r in g
                                                  where r.AverageJitterMs.HasValue
                                                  orderby r.AverageJitterMs
                                                  select r).FirstOrDefault() ?? list[0];
                           return new PortHistoryGroup
                           {
                               PortLocation = g.Key.PortLocation,
                               Best = best,
                               History = list
                           };
                       })
                orderby g.Best.AverageJitterMs ?? double.MaxValue
                select g).ToList();
    }
}
