﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Crypton.Carbonator
{
    /// <summary>
    /// Controls operation of Carbonator service
    /// </summary>
    internal static class CarbonatorInstance
    {

        private static Timer metricCollectorTimer = null;
        private static List<CounterWatcher> _watchers = new List<CounterWatcher>();

        static IOutputClient outputClient = null;

        private static bool _started = false;
        private static Config.CarbonatorSection conf = null;

        #region Collection Timers State control
        private class StateControl
        {
            public bool IsRunning = false;
            public uint CheckNumber = 1;
            public CultureInfo Culture = null;
        }
        #endregion

        /// <summary>
        /// Starts collection of performance counter metrics and relaying of data to Graphite
        /// </summary>
        [PerformanceCounterPermission(System.Security.Permissions.SecurityAction.Demand)]
        public static void StartCollection()
        {
            if (_started)
                return;
            _started = true;

            conf = Config.CarbonatorSection.Current;
            if (conf == null)
            {
                Log.Fatal("[StartCollection] Carbonator configuration is missing. This service cannot start");
                throw new InvalidOperationException("Carbonator configuration is missing. This service cannot start");
            }

            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo(conf.DefaultCulture);
            Thread.CurrentThread.CurrentUICulture = CultureInfo.GetCultureInfo(conf.DefaultCulture);

            // load counter watchers that will actually collect metrics for us
            foreach (Config.PerformanceCounterElement counterConfig in Config.CarbonatorSection.Current.Counters)
            {
                CounterWatcher watcher = new CounterWatcher(counterConfig);
                try
                {
                    watcher.Initialize();
                }
                catch (Exception any)
                {
                    Log.Error("[StartCollection] Failed to initialize performance counter watcher for path '{0}'; this configuration element will be skipped: {1} (inner: {2})", counterConfig.Template, any.Message, any.InnerException != null ? any.InnerException.Message : "(null)");
                    continue;
                }
                _watchers.Add(watcher);
            }

            // start collection and reporting timers
            CultureInfo culture;
            try
            {
                culture = CultureInfo.GetCultureInfo(conf.DefaultCulture);
            }
            catch (Exception any)
            {
                Log.Fatal($"[{nameof(StartCollection)}] unable to find specific culture in configuration: {conf.DefaultCulture} -> {any.Message}; verify that is a known culture string");
                throw;
            }
            metricCollectorTimer = new Timer(collectMetrics, new StateControl() { Culture = culture }, conf.CollectionInterval, conf.CollectionInterval);

            // start output client
            var configuredName = Config.CarbonatorSection.Current.Output.DefaultOutput;
            foreach (Config.OutputElementCollection.OutputElementProxy proxy in Config.CarbonatorSection.Current.Output)
            {
                if (proxy.Entry.Name == configuredName)
                {
                    switch (proxy.Entry.Type)
                    {
                        case "graphite":
                            outputClient = new GraphiteClient(proxy.Entry as Config.GraphiteOutputElement);
                            break;
                        case "influxdb":
                            outputClient = new InfluxDbClient(proxy.Entry as Config.InfluxDbOutputElement);
                            break;
                    }
                    outputClient.Start();
                }
            }

            if (outputClient == null)
            {
                throw new InvalidOperationException("No output client is configured, check <output> configuration");
            }

            Log.Info("[StartCollection] Carbonator service loaded {0} watchers", _watchers.Count);
        }

        /// <summary>
        /// Stops collection of performance counter metrics and relaying of data to Graphite
        /// </summary>
        public static void StopCollection()
        {
            if (!_started)
                return;
            _started = false;

            metricCollectorTimer.Dispose();
            outputClient.Dispose();

            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
            _watchers.Clear();
        }


        /// <summary>
        /// Timer callback that collects metrics
        /// </summary>
        /// <param name="state"></param>
        [PerformanceCounterPermission(System.Security.Permissions.SecurityAction.Demand)]
        private static void collectMetrics(object state)
        {
            StateControl control = state as StateControl;
            if (control.IsRunning)
                return; // skip this run if we're already collecting data
            control.IsRunning = true;

            // restore configured culture setting for this async thread
            Thread.CurrentThread.CurrentCulture = control.Culture;
            Thread.CurrentThread.CurrentUICulture = control.Culture;

            // gather metrics from all watchers
            List<CollectedMetric> metrics = new List<CollectedMetric>();
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.Report(metrics);
                }
                catch (Exception any)
                {
                    Log.Warning($"[{nameof(collectMetrics)}] (#{control.CheckNumber}) Failed to Report on counter watcher for path '{watcher.MetricPath}'; this report will be skipped for now: {any.Message} (inner: {any.InnerException?.Message})");
                    continue;
                }
            }

            // transfer metrics over for sending
            foreach (var item in metrics)
            {
                if (!outputClient.TryAdd(item))
                {
                    Log.Warning($"[{nameof(collectMetrics)}] (#{control.CheckNumber}) Failed to relocate collected metrics to buffer for sending, buffer may be full; increase metric buffer in configuration");
                }

                Log.Debug($"[{nameof(collectMetrics)}] (#{control.CheckNumber}) item stringified: {item.ToString()}");
            }

            unchecked
            {
                control.CheckNumber++;
            }

            control.IsRunning = false;
        }

    }
}
