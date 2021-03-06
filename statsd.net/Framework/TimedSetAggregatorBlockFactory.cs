﻿using statsd.net.shared.Messages;
using statsd.net.shared.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Collections.Concurrent;
using log4net;
using statsd.net.shared.Structures;
using statsd.net.core.Structures;

namespace statsd.net.Framework
{
    public class TimedSetAggregatorBlockFactory
    {
        public static readonly char[] UNDERSCORE = new char[] { '_' };
        public const string METRIC_IDENTIFIER_SEPARATOR = "^ ^";
        public static readonly string[] METRIC_IDENTIFIER_SEPARATOR_SPLITTER = new String[] { "^ ^" };

        public static ActionBlock<StatsdMessage> CreateBlock(ITargetBlock<CounterBucket> target,
          string rootNamespace,
          IIntervalService intervalService,
          ILog log)
        {
            var sets = new ConcurrentDictionary<string, ConcurrentDictionary<MetricInfo, int>>();
            var windows = new ConcurrentDictionary<string, ConcurrentDictionary<MetricInfo, bool>>();
            var root = rootNamespace;
            var ns = String.IsNullOrEmpty(rootNamespace) ? "" : rootNamespace + ".";
            var timeWindow = new TimeWindow();

            var incoming = new ActionBlock<StatsdMessage>(p =>
              {
                  var set = p as Set;
                  var metricName = set.Name + METRIC_IDENTIFIER_SEPARATOR + set.Value;

                  var metricInfo = new MetricInfo(metricName, set.Tags);
                  foreach (var period in timeWindow.AllPeriods)
                  {
                      windows.AddOrUpdate(period,
                        (key) =>
                        {
                            var window = new ConcurrentDictionary<MetricInfo, bool>();
                            window.AddOrUpdate(metricInfo, (key2) => true, (key2, oldValue) => true);
                            return window;
                        },
                        (key, window) =>
                        {
                            window.AddOrUpdate(metricInfo, (key2) => true, (key2, oldValue) => true);
                            return window;
                        }
                      );
                  }
              },
              new ExecutionDataflowBlockOptions() { MaxDegreeOfParallelism = DataflowBlockOptions.Unbounded });

            intervalService.Elapsed += (sender, e) =>
              {
                  if (sets.Count == 0)
                  {
                      return;
                  }
                  var currentTimeWindow = new TimeWindow();
                  var periodsNotPresent = currentTimeWindow.GetDifferences(timeWindow);
                  // Make the current time window the one we use to manage the collections
                  timeWindow = currentTimeWindow;
                  CounterBucket bucket;

                  // (Parallel) For each period that was measured (Day, Week, Month etc)
                    // for every unique metric + value
                      // Count the number of entries that start with the metric name
                      // Add that metric to the list


                  foreach (var period in periodsNotPresent)
                  {
                      ConcurrentDictionary<MetricInfo, bool> window;
                      // Take this window out of the dictionary
                      if (windows.TryRemove(period, out window))
                      {
                          var parts = period.Split(UNDERSCORE);
                          var qualifier = "." + parts[0] + "." + parts[1];

                          var metricsAndValues = window.ToArray();
                          var metrics = new Dictionary<MetricInfo, double>();
                          for (int index = 0; index < metricsAndValues.Length; index++)
                          {
                              var metricName = metricsAndValues[index].Key.Name.Split(METRIC_IDENTIFIER_SEPARATOR_SPLITTER, StringSplitOptions.RemoveEmptyEntries)[0] + qualifier;
                              var metricInfo = new MetricInfo(metricName, metricsAndValues[index].Key.Tags);
                              if (metrics.ContainsKey(metricInfo))
                              {
                                  metrics[metricInfo] += 1;
                              }
                              else
                              {
                                  metrics[metricInfo] = 1;
                              }
                          }

                          var metricList = metrics.Select(metric =>
                              {
                                  return new KeyValuePair<MetricInfo, double>(
                                    metric.Key,
                                    metric.Value
                                  );
                              }).ToArray();
                          bucket = new CounterBucket(metricList, e.Epoch, ns);
                          target.Post(bucket);
                          break;
                      }
                  }
              };
            incoming.Completion.ContinueWith(p =>
              {
                  // Tell the upstream block that we're done
                  target.Complete();
              });
            return incoming;
        }
    }
}
