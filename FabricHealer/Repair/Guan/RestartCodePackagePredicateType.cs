﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using Guan.Logic;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using System.Threading.Tasks;

namespace FabricHealer.Repair.Guan
{
    public class RestartCodePackagePredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static RestartCodePackagePredicateType Instance;

        private class Resolver : BooleanPredicateResolver
        {
            private readonly RepairConfiguration repairConfiguration;

            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context)
            {

                repairConfiguration = new RepairConfiguration
                {
                    AppName = !string.IsNullOrWhiteSpace(FOHealthData.ApplicationName) ? new Uri(FOHealthData.ApplicationName) : null,
                    ContainerId = FOHealthData.ContainerId,
                    FOErrorCode = FOHealthData.Code,
                    NodeName = FOHealthData.NodeName,
                    NodeType = FOHealthData.NodeType,
                    PartitionId = !string.IsNullOrWhiteSpace(FOHealthData.PartitionId) ? new Guid(FOHealthData.PartitionId) : default,
                    ReplicaOrInstanceId = FOHealthData.ReplicaId > 0 ? FOHealthData.ReplicaId : 0,
                    ServiceName = !string.IsNullOrWhiteSpace(FOHealthData.ServiceName) ? new Uri(FOHealthData.ServiceName) : null,
                    FOHealthMetricValue = FOHealthData.Value,
                    RepairPolicy = new RepairPolicy()
                };
            }

            protected override async Task<bool> CheckAsync()
            {
                // RepairPolicy
                repairConfiguration.RepairPolicy.RepairAction = RepairActionType.RestartCodePackage;
                repairConfiguration.RepairPolicy.RepairId = FOHealthData.RepairId;
                repairConfiguration.RepairPolicy.TargetType = RepairTargetType.Application;

                int count = Input.Arguments.Count;

                for (int i = 0; i < count; i++)
                {
                    var typeString = Input.Arguments[i].Value.GetEffectiveTerm().GetObjectValue().GetType().Name;
                    switch (typeString)
                    {
                        case "TimeSpan":
                            repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck = (TimeSpan)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        case "Boolean":
                            repairConfiguration.RepairPolicy.DoHealthChecks = (bool)Input.Arguments[i].Value.GetObjectValue();
                            break;

                        default:
                            throw new GuanException($"Unsupported input: {Input.Arguments[i].Value.GetObjectValue().GetType()}");
                    }
                }

                // Try to schedule repair with RM.
                var repairTask = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                          () => RepairTaskManager.ScheduleFabricHealerRepairTaskAsync(
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                           RepairTaskManager.Token).ConfigureAwait(false);

                if (repairTask == null)
                {
                    return false;
                }

                // Try to execute repair (FH executor does this work and manages repair state).
                bool success = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                        () => RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync(
                                                                                    repairTask,
                                                                                    repairConfiguration,
                                                                                    RepairTaskManager.Token),
                                                        RepairTaskManager.Token).ConfigureAwait(false);
                return success;
            }
        }

        public static RestartCodePackagePredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new RestartCodePackagePredicateType(name);
        }

        private RestartCodePackagePredicateType(string name)
                 : base(name, true, 0)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }
    }
}
