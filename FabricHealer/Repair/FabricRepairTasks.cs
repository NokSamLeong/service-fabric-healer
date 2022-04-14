﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Fabric;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Interfaces;

namespace FabricHealer.Repair
{
    public static class FabricRepairTasks
    {
        public static async Task<bool> IsRepairTaskInDesiredStateAsync(
                                         string taskId,
                                         FabricClient fabricClient,
                                         string executorName,
                                         List<RepairTaskState> desiredStates)
        {
            IList<RepairTask> repairTaskList = await fabricClient.RepairManager.GetRepairTaskListAsync(
                                                                                   taskId,
                                                                                   RepairTaskStateFilter.All,
                                                                                   executorName).ConfigureAwait(false);

            return desiredStates.Any(desiredState => repairTaskList.Count(rt => rt.State == desiredState) > 0);
        }

        /// <summary>
        /// Cancels a repair task based on its current state.
        /// </summary>
        /// <param name="repairTask"><see cref="RepairTask"/> to be cancelled</param>
        /// <param name="fabricClient">FabricClient instance.</param>
        /// <returns></returns>
        public static async Task CancelRepairTaskAsync(RepairTask repairTask, FabricClient fabricClient)
        {
            await Task.Delay(1000).ConfigureAwait(false);

            switch (repairTask.State)
            {
                case RepairTaskState.Restoring:
                case RepairTaskState.Completed:

                    break;

                case RepairTaskState.Created:
                case RepairTaskState.Claimed:
                case RepairTaskState.Preparing:
                {
                    try
                    {
                        _ = await fabricClient.RepairManager.CancelRepairTaskAsync(
                                                                   repairTask.TaskId,
                                                                   repairTask.Version,
                                                                   true).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // RM throws IOE if state is already Completed or in some state that is not supported for transition. Ignore.
                    }

                    break;
                }
                case RepairTaskState.Approved:
                case RepairTaskState.Executing:

                    repairTask.State = RepairTaskState.Restoring;
                    repairTask.ResultStatus = RepairTaskResult.Cancelled;
                    try
                    {
                        _ = await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(repairTask).ConfigureAwait(false);
                    }
                    catch (FabricException fe)
                    {
                        // TOTHINK
                        if (fe.ErrorCode == FabricErrorCode.SequenceNumberCheckFailed)
                        {
                            // Not sure what to do here. This can randomly take place (timing).
                        }
                    }
                    break;

                case RepairTaskState.Invalid:

                    break;

                default:

                    throw new Exception($"Repair task {repairTask.TaskId} is in invalid state {repairTask.State}");
            }
        }

        public static async Task<bool> CompleteCustomActionRepairJobAsync(
                                               RepairTask repairTask,
                                               FabricClient fabricClient,
                                               StatelessServiceContext context,
                                               CancellationToken token)
        {
            var telemetryUtilities = new TelemetryUtilities(fabricClient, context);

            try
            {
                if (repairTask.ResultStatus == RepairTaskResult.Succeeded
                    || repairTask.State == RepairTaskState.Completed
                    || repairTask.State == RepairTaskState.Restoring)
                {
                    return true;
                }

                repairTask.State = RepairTaskState.Restoring;
                repairTask.ResultStatus = RepairTaskResult.Succeeded;

                _ =  await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                                                            repairTask,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FabricException || e is TaskCanceledException || e is OperationCanceledException || e is TimeoutException)
            {
                return false;
            }
            catch (InvalidOperationException e)
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "FabricRepairTasks.CompleteCustomActionRepairJobAsync",
                                             $"Failed to Complete Repair Job {repairTask.TaskId} due to invalid state transition.{Environment.NewLine}:{e}",
                                             token).ConfigureAwait(false);

                return false;
            }
            catch (Exception e)
            {
                await telemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "FabricRepairTasks.CompleteCustomActionRepairJobAsync",
                                             $"Failed to Complete Repair Job {repairTask.TaskId} with unhandled exception:{Environment.NewLine}{e}",
                                             token).ConfigureAwait(false);
                throw;
            }

            return true;
        }

        public static async Task<RepairTask> ScheduleRepairTaskAsync(
                                                     RepairConfiguration repairConfiguration,
                                                     RepairExecutorData executorData,
                                                     string executorName,
                                                     FabricClient fabricClient,
                                                     CancellationToken token)
        {
            var repairTaskEngine = new RepairTaskEngine(fabricClient);
            RepairActionType repairAction = repairConfiguration.RepairPolicy.RepairAction;
            RepairTask repairTask;

            await Task.Delay(new Random().Next(100, 1500));

            var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(executorName, repairConfiguration, token).ConfigureAwait(false);

            if (isRepairAlreadyInProgress)
            {
                return null;
            }

            switch (repairAction)
            {
                case RepairActionType.RestartVM:

                    repairTask = await repairTaskEngine.CreateVmRebootISRepairTaskAsync(repairConfiguration, executorName, token);
                    break;

                case RepairActionType.DeleteFiles:
                case RepairActionType.RestartCodePackage:
                case RepairActionType.RestartFabricNode:
                case RepairActionType.RestartProcess:
                case RepairActionType.RestartReplica:

                    repairTask = await repairTaskEngine.CreateFabricHealerRepairTask(executorData, token);
                    break;

                default:

                    FabricHealerManager.RepairLogger.LogWarning("Unknown or Unsupported FabricRepairAction specified.");
                    return null;
            }

            bool success = await TryCreateRepairTaskAsync(
                                    fabricClient,
                                    repairTask,
                                    repairConfiguration,
                                    repairTaskEngine,
                                    token).ConfigureAwait(false);

            return success ? repairTask : null;
        }

        private static async Task<bool> TryCreateRepairTaskAsync(
                                            FabricClient fabricClient,
                                            RepairTask repairTask,
                                            RepairConfiguration repairConfiguration,
                                            RepairTaskEngine repairTaskEngine,
                                            CancellationToken token)
        {
            if (repairTask == null)
            {
                return false;
            }

            await Task.Delay(new Random().Next(100, 1500));

            try
            {
                var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(repairTask.Executor, repairConfiguration, token).ConfigureAwait(false);

                if (!isRepairAlreadyInProgress)
                {
                    _ = await fabricClient.RepairManager.CreateRepairTaskAsync(repairTask, FabricHealerManager.ConfigSettings.AsyncTimeout, token).ConfigureAwait(false);
                    return true;
                }
            }
            catch (FabricException fe)
            {
                string message = $"Unable to create repairtask:{Environment.NewLine}{fe}";
                FabricHealerManager.RepairLogger.LogWarning(message);
                await FabricHealerManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                                 LogLevel.Warning,
                                                                 "FabricRepairTasks::TryCreateRepairTaskAsync",
                                                                 message,
                                                                 token,
                                                                 repairConfiguration,
                                                                 FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            }

            return false;
        }

        public static async Task<long> SetFabricRepairJobStateAsync(
                                          RepairTask repairTask,
                                          RepairTaskState repairState,
                                          RepairTaskResult repairResult,
                                          FabricClient fabricClient,
                                          CancellationToken token)
        {
            repairTask.State = repairState;
            repairTask.ResultStatus = repairResult;

            return await fabricClient.RepairManager.UpdateRepairExecutionStateAsync(
                                                          repairTask,
                                                          FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                          token).ConfigureAwait(false);
        }

        public static async Task<IEnumerable<Service>> GetInfrastructureServiceInstancesAsync(FabricClient fabricClient, CancellationToken cancellationToken)
        {
            var allSystemServices =
                await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                               () => fabricClient.QueryManager.GetServiceListAsync(
                                                                                  new Uri(RepairConstants.SystemAppName),
                                                                                  null,
                                                                                  FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                  cancellationToken),
                                               cancellationToken).ConfigureAwait(false);

            var infraInstances = 
                allSystemServices.Where(i => i.ServiceTypeName.Equals(RepairConstants.InfrastructureServiceType, StringComparison.InvariantCultureIgnoreCase));

            return infraInstances;
        }

        public static async Task<bool> IsLastCompletedFHRepairTaskWithinTimeRangeAsync(
                                         TimeSpan interval,
                                         FabricClient fabricClient,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {

            // Repairs where FH or IS is executor.
            var allRecentFHRepairTasksCompleted =
                            await fabricClient.RepairManager.GetRepairTaskListAsync(
                                                                RepairTaskEngine.FHTaskIdPrefix,
                                                                RepairTaskStateFilter.Completed,
                                                                null,
                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                cancellationToken).ConfigureAwait(false);

            if (allRecentFHRepairTasksCompleted == null || allRecentFHRepairTasksCompleted.Count == 0)
            {
                return false;
            }

            var orderedRepairList = allRecentFHRepairTasksCompleted.OrderByDescending(o => o.CompletedTimestamp).ToList();

            // There could be several repairs of this type for the same repair target in RM's db.
            if (orderedRepairList.Any(r => r.ExecutorData.Contains(repairData.RepairId)))
            {
                foreach (var repair in orderedRepairList)
                {
                    if (repair.ExecutorData.Contains(repairData.RepairId))
                    {
                        // Completed aborted/cancelled repair tasks should not block repairs if they are inside run interval.
                        return repair.CompletedTimestamp != null && repair.Flags != RepairTaskFlags.AbortRequested && repair.Flags != RepairTaskFlags.CancelRequested && DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= interval;
                    }
                }
            }

            // VM repairs - IS is executor, ExecutorData is supplied by IS. Custom FH repair id supplied as repair Description.
            foreach (var repair in allRecentFHRepairTasksCompleted.Where(r => r.ResultStatus == RepairTaskResult.Succeeded))
            {
                if (repair.Executor != $"fabric:/System/InfrastructureService/{repairData.NodeType}" ||
                    repair.Description != repairData.RepairId)
                {
                    continue;
                }

                if (!(repair.CompletedTimestamp is { }))
                {
                    return false;
                }

                // Completed aborted/cancelled repair tasks should not block repairs if they are inside run interval.
                if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= interval
                    && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                {
                    return true;
                }
            }

            return false;
        }

        public static async Task<int> GetCompletedRepairCountWithinTimeRangeAsync(
                                         TimeSpan timeWindow,
                                         FabricClient fabricClient,
                                         TelemetryData repairData,
                                         CancellationToken cancellationToken)
        {
            var allRecentFHRepairTasksCompleted =
                            await fabricClient.RepairManager.GetRepairTaskListAsync(
                                                                RepairTaskEngine.FHTaskIdPrefix,
                                                                RepairTaskStateFilter.Completed,
                                                                null,
                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                cancellationToken).ConfigureAwait(false);
            if (!allRecentFHRepairTasksCompleted.Any())
            {
                return 0;
            }

            int count = 0;

            foreach (var repair in allRecentFHRepairTasksCompleted.Where(r => r.ResultStatus == RepairTaskResult.Succeeded))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Non-VM repairs (FH is executor, custom repair ExecutorData supplied by FH.)
                if (repair.Executor == RepairTaskEngine.FabricHealerExecutorName)
                {
                    var fhExecutorData = JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData exData) ? exData : null;

                    if (fhExecutorData == null || fhExecutorData.RepairPolicy == null || repair.CompletedTimestamp == null || !repair.CompletedTimestamp.HasValue)
                    {
                        continue;
                    }

                    if (repairData.RepairId != fhExecutorData.RepairPolicy.RepairId)
                    {
                        continue;
                    }

                    // Note: Completed aborted/cancelled repair tasks should not block repairs if they are inside run interval.
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow
                        && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                    {
                        count++;
                    }
                }
                // VM repairs (IS is executor, ExecutorData supplied by IS. Custom FH repair id supplied as repair Description.)
                else if (repair.Executor == $"{RepairTaskEngine.InfrastructureServiceName}/{repairData.NodeType}" && repair.Description == repairData.RepairId)
                {
                    if (repair.CompletedTimestamp == null || !repair.CompletedTimestamp.HasValue)
                    {
                        continue;
                    }

                    // Note: Completed aborted/cancelled repair tasks should not block repairs if they are inside max time window
                    // for a repair cycle (of n repair attempts at a run interval of y)
                    if (DateTime.UtcNow.Subtract(repair.CompletedTimestamp.Value) <= timeWindow
                        && repair.Flags != RepairTaskFlags.CancelRequested && repair.Flags != RepairTaskFlags.AbortRequested)
                    {
                        count++;
                    }
                }
            }

            return count;
        }
    }
}