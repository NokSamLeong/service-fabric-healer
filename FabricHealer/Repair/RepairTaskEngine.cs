﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Fabric.Description;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using Newtonsoft.Json.Linq;

namespace FabricHealer.Repair
{
    public sealed class RepairTaskEngine
    {
        public const string HostVMReboot = "System.Reboot";
        public const string FHTaskIdPrefix = "FH";
        public const string AzureTaskIdPrefix = "Azure";
        public const string FabricHealerExecutorName = "FabricHealer";
        public const string InfrastructureServiceName = "fabric:/System/InfrastructureService";

        public async Task<RepairTask> CreateFabricHealerRepairTask(RepairExecutorData executorData, CancellationToken token)
        {
            if (executorData == null || executorData.RepairData.NodeName == null)
            {
                return null;
            }

            var repairs = await GetFHRepairTasksCurrentlyProcessingAsync(FabricHealerExecutorName, token);
            
            if (repairs?.Count > 0)
            {
                if (repairs.Any(r => r.ExecutorData.Contains(executorData.RepairData.RepairPolicy.RepairId)))
                {
                    return null;
                }
            }

            var impact = executorData.RepairData.RepairPolicy.RepairAction switch
            {
                RepairActionType.RestartFabricNode => NodeImpactLevel.Restart,
                RepairActionType.RemoveFabricNodeState => NodeImpactLevel.RemoveData,
                _ => NodeImpactLevel.None
            };

            var nodeRepairImpact = new NodeRepairImpactDescription();
            var impactedNode = new NodeImpact(executorData.RepairData.NodeName, impact);
            nodeRepairImpact.ImpactedNodes.Add(impactedNode);
            RepairActionType repairAction = executorData.RepairData.RepairPolicy.RepairAction;
            string repair = Enum.GetName(typeof(RepairActionType), repairAction);
            string taskId = $"{FHTaskIdPrefix}/{Guid.NewGuid()}/{repair}/{executorData.RepairData.NodeName}";
            bool doHealthChecks = impact != NodeImpactLevel.None;

            // Health checks for app level repairs.
            if (executorData.RepairData.RepairPolicy.DoHealthChecks && impact == NodeImpactLevel.None && 
                                                            (repairAction == RepairActionType.RestartCodePackage ||
                                                             repairAction == RepairActionType.RestartReplica ||
                                                             repairAction == RepairActionType.RemoveReplica))
            {
                doHealthChecks = true;
            }

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health level reports.
            if (executorData.RepairData.HealthState == System.Fabric.Health.HealthState.Error)
            {
                doHealthChecks = false;
            }

            var repairTask = new ClusterRepairTask(taskId, repair)
            {
                Target = new NodeRepairTargetDescription(executorData.RepairData.NodeName),
                Impact = nodeRepairImpact,
                Description = $"FabricHealer executing repair {repair} on node {executorData.RepairData.NodeName}",
                State = RepairTaskState.Preparing,
                Executor = FabricHealerExecutorName,
                ExecutorData = JsonSerializationUtility.TrySerialize(executorData, out string exData) ? exData : null,
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
            };

            return repairTask;
        }

        /// <summary>
        /// This function returns the list of currently processing FH repair tasks.
        /// </summary>
        /// <returns>List of repair tasks in Preparing, Approved, Executing or Restoring state</returns>
        public async Task<RepairTaskList> GetFHRepairTasksCurrentlyProcessingAsync(string executorName, CancellationToken cancellationToken)
        {
            var repairTasks = await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                        FHTaskIdPrefix,
                                        RepairTaskStateFilter.Active |
                                        RepairTaskStateFilter.Approved |
                                        RepairTaskStateFilter.Executing,
                                        executorName,
                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                        cancellationToken);

            return repairTasks;
        }

        // This allows InfrastructureService to schedule and run reboot im concert with VMSS over MR.
        public async Task<RepairTask> CreateVmRebootISRepairTaskAsync(TelemetryData repairData, string executorName, CancellationToken cancellationToken)
        {
            // This constraint (MaxResults) is used just to make sure there is more 1 node in the cluster. We don't need a list of all nodes.
            var nodeQueryDesc = new NodeQueryDescription
            {
                MaxResults = 3,
            };

            NodeList nodes = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                    () => FabricHealerManager.FabricClientSingleton.QueryManager.GetNodePagedListAsync(
                                            nodeQueryDesc,
                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                            cancellationToken),
                                     cancellationToken);

            if (nodes?.Count == 1)
            {
                return null;
            }

            string taskId = $"{FHTaskIdPrefix}/{HostVMReboot}/{(uint)repairData.NodeName.GetHashCode()}/{repairData.NodeType}";
            bool doHealthChecks = !SupportedErrorCodes.GetCodeNameFromErrorCode(repairData.Code).Contains("Error");

            // Error health state on target SF entity can block RM from approving the job to repair it (which is the whole point of doing the job).
            // So, do not do health checks if customer configures FO to emit Error health reports.
            // In general, FO should *not* be configured to emit Error events. See FO documentation.

            var repairTask = new ClusterRepairTask(taskId, HostVMReboot)
            {
                Target = new NodeRepairTargetDescription(repairData.NodeName),
                Description = $"{repairData.RepairPolicy.RepairId}",
                Executor = executorName,
                PerformPreparingHealthCheck = doHealthChecks,
                PerformRestoringHealthCheck = doHealthChecks,
                State = RepairTaskState.Claimed,
            };

            return repairTask;
        }

        public async Task<bool> IsFHRepairTaskRunningAsync(string executorName, TelemetryData repairdData, CancellationToken token)
        {
            // All RepairTasks are prefixed with FH, regardless of repair target type (VM, fabric node, system service process, codepackage, replica).
            // For VM-level repair, RM will create a new task for IS that replaces FH executor data with IS job info, but the original FH repair task will
            // remain in an active state which will block any duplicate scheduling by another FH instance.
            var currentFHRepairTasksInProgress =
                            await FabricHealerManager.FabricClientSingleton.RepairManager.GetRepairTaskListAsync(
                                    FHTaskIdPrefix,
                                    RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                                    executorName,
                                    FabricHealerManager.ConfigSettings.AsyncTimeout,
                                    token);

            if (currentFHRepairTasksInProgress == null || currentFHRepairTasksInProgress.Count == 0)
            {
                return false;
            }

            foreach (var repair in currentFHRepairTasksInProgress)
            {
                // This check is to see if there are any FH-as-executor repairs in flight.
                if (executorName == FabricHealerExecutorName)
                {
                    if (!JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData executorData))
                    {
                        continue;
                    }
                    
                    if (executorData.RepairData.RepairPolicy == null)
                    {
                        return false;
                    }

                    // The node repair check ensures that only one node-level repair can take place in a cluster (no concurrent node restarts), by default.
                    // FH is conservative, by design.
                    if (repairdData.RepairPolicy.RepairId == executorData.RepairData.RepairPolicy.RepairId ||
                        executorData.RepairData.RepairPolicy.RepairAction == RepairActionType.RestartFabricNode)
                    {
                        return true;
                    }
                }
                else if (repair.Executor == $"{InfrastructureServiceName}/{repairdData.NodeType}")
                {
                    // This would block scheduling any VM level operation (reboot) already in flight.
                    // For IS repairs, unique id is stored in the repair task's Description property.
                    if (repair.Description == repairdData.RepairPolicy.RepairId)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}
