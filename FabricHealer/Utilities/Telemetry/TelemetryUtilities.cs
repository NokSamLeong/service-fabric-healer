﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using FabricHealer.Interfaces;
using FabricHealer.Repair;
using System;
using System.Fabric;
using System.Fabric.Health;
using System.Threading;
using System.Threading.Tasks;

namespace FabricHealer.Utilities.Telemetry
{
    public class TelemetryUtilities
    {
        private readonly StatelessServiceContext serviceContext;
        private readonly ITelemetryProvider telemetryClient;
        private readonly Logger logger;

        public TelemetryUtilities(StatelessServiceContext serviceContext)
        {
            this.serviceContext = serviceContext;
            logger = new Logger(RepairConstants.RepairData)
            {
                EnableVerboseLogging = true,
            };

            if (!FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                return;
            }

            telemetryClient = FabricHealerManager.ConfigSettings.TelemetryProviderType switch
            {
                TelemetryProviderType.AzureApplicationInsights => new AppInsightsTelemetry(FabricHealerManager.ConfigSettings.AppInsightsInstrumentationKey),
                TelemetryProviderType.AzureLogAnalytics => new LogAnalyticsTelemetry(
                                                                FabricHealerManager.ConfigSettings.LogAnalyticsWorkspaceId,
                                                                FabricHealerManager.ConfigSettings.LogAnalyticsSharedKey,
                                                                FabricHealerManager.ConfigSettings.LogAnalyticsLogType),
                _ => null
            };
        }

        /// <summary>
        /// Emits Repair telemetry to AppInsights (or some other external service),
        /// ETW (EventSource), and Health Event to Service Fabric.
        /// </summary>
        /// <param name="level">Log Level.</param>
        /// <param name="source">Err/Warning source id.</param>
        /// <param name="description">Message.</param>
        /// <param name="token">Cancellation token.</param>
        /// <param name="telemetryData">repairData instance.</param>
        /// <returns></returns>
        public async Task EmitTelemetryEtwHealthEventAsync(
                            LogLevel level,
                            string source,
                            string description,
                            CancellationToken token,
                            TelemetryData telemetryData = null,
                            bool verboseLogging = true,
                            TimeSpan ttl = default,
                            string property = "RepairStateInformation",
                            EntityType entityType = EntityType.Node)
        {
            bool isTelemetryDataEvent = string.IsNullOrWhiteSpace(description) && telemetryData != null;

            if (source != null && source != "FabricHealer")
            {
                source = source.Insert(0, "FabricHealer.");
            }
            else
            {
                source = "FabricHealer";
            }

            HealthState healthState = level switch
            {
                LogLevel.Error => HealthState.Error,
                LogLevel.Warning => HealthState.Warning,
                _ => HealthState.Ok
            };

            // Do not write ETW/send Telemetry/create health report if the data is informational-only and verbose logging is not enabled.
            // This means only Warning and Error messages will be transmitted. In general, however, it is best to enable Verbose Logging (default)
            // in FabricHealer as it will not generate noisy local logs and you will have a complete record of mitigation history in your AI or LA workspace.
            if (!verboseLogging && level == LogLevel.Info)
            {
                return;
            }

            // Service Fabric health report generation.
            var healthReporter = new FabricHealthReporter(logger);
            var healthReport = new HealthReport
            {
                AppName = entityType == EntityType.Application ? new Uri("fabric:/FabricHealer") : null,
                Code = telemetryData?.RepairPolicy?.RepairId,
                HealthMessage = description,
                NodeName = serviceContext.NodeContext.NodeName,
                EntityType = entityType,
                State = healthState,
                HealthReportTimeToLive = ttl == default ? TimeSpan.FromMinutes(5) : ttl,
                Property = property,
                SourceId = source,
                EmitLogEvent = true
            };

            healthReporter.ReportHealthToServiceFabric(healthReport);

            if (!FabricHealerManager.ConfigSettings.EtwEnabled && !FabricHealerManager.ConfigSettings.TelemetryEnabled)
            {
                return;
            }

            // TelemetryData
            if (isTelemetryDataEvent)
            {
                // Telemetry.
                if (FabricHealerManager.ConfigSettings.TelemetryEnabled && telemetryClient != null)
                {
                    await telemetryClient.ReportMetricAsync(telemetryData, token);
                }

                // ETW.
                if (FabricHealerManager.ConfigSettings.EtwEnabled)
                {
                    logger.LogEtw(RepairConstants.FabricHealerDataEvent, telemetryData);
                }
            }
            else // Untyped or anonymous-typed operational data.
            {
                if (FabricHealerManager.ConfigSettings.TelemetryEnabled && telemetryClient != null)
                {
                    await telemetryClient?.ReportMetricAsync($"FabicHealerDataEvent.{level}", description, RepairConstants.FabricHealer, token);
                }

                if (FabricHealerManager.ConfigSettings.EtwEnabled)
                {
                    // Anonymous types are supported by FH's ETW impl.
                    var anonType = new
                    {
                        LogLevel = level,
                        Source = source,
                        Message = description,
                        Property = property,
                        EntityType = entityType
                    };

                    logger.LogEtw(RepairConstants.FabricHealerDataEvent, anonType);
                }
            }
        }
    }
}
