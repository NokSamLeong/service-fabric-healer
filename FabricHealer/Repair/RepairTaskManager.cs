// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Fabric;
using System.Fabric.Health;
using System.Fabric.Query;
using System.Fabric.Repair;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Interfaces;
using Guan.Logic;
using FabricHealer.Repair.Guan;
using FabricHealer.Utilities;

namespace FabricHealer.Repair
{
    public class RepairTaskManager : IRepairTasks
    {
        private static readonly TimeSpan MaxWaitTimeForInfraRepairTaskCompleted = TimeSpan.FromHours(2);
        internal readonly List<HealthEvent> DetectedHealthEvents = new List<HealthEvent>();
        internal readonly StatelessServiceContext Context;
        internal readonly CancellationToken Token;
        internal readonly TelemetryUtilities TelemetryUtilities;
        internal readonly FabricClient FabricClientInstance;
        private readonly RepairTaskEngine repairTaskEngine;
        private readonly RepairExecutor RepairExec;
        private readonly TimeSpan AsyncTimeout = TimeSpan.FromSeconds(60);
        private readonly DateTime HealthEventsListCreationTime = DateTime.UtcNow;
        private readonly TimeSpan MaxLifeTimeHealthEventsData = TimeSpan.FromDays(2);
        private DateTime LastHealthEventsListClearDateTime;

        public RepairTaskManager(FabricClient fabricClient, StatelessServiceContext context, CancellationToken token)
        {
            FabricClientInstance = fabricClient ?? throw new ArgumentException("FabricClient can't be null");
            Context = context;
            Token = token;
            RepairExec = new RepairExecutor(fabricClient, context, token);
            repairTaskEngine = new RepairTaskEngine(fabricClient);
            TelemetryUtilities = new TelemetryUtilities(fabricClient, context);
            LastHealthEventsListClearDateTime = HealthEventsListCreationTime;
        }

        // TODO.
        public Task<bool> RemoveServiceFabricNodeStateAsync(string nodeName, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public async Task ActivateServiceFabricNodeAsync(string nodeName, CancellationToken cancellationToken)
        {
            await FabricClientInstance.ClusterManager.ActivateNodeAsync(nodeName, AsyncTimeout, cancellationToken).ConfigureAwait(false);
        }

        public async Task<bool> SafeRestartServiceFabricNodeAsync(RepairConfiguration repairConfiguration, RepairTask repairTask, CancellationToken cancellationToken)
        {
            if (!await RepairExec.SafeRestartFabricNodeAsync(
                                    repairConfiguration,
                                    repairTask,
                                    cancellationToken).ConfigureAwait(false))
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                            LogLevel.Info,
                                            "SafeRestartFabricNodeAsync",
                                            $"Did not restart Fabric node {repairConfiguration.NodeName}",
                                            cancellationToken,
                                            repairConfiguration,
                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

                return false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "SafeRestartFabricNodeAsync",
                                        $"Successfully restarted Fabric node {repairConfiguration.NodeName}",
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            return true;
        }

        public async Task StartRepairWorkflowAsync(TelemetryData foHealthData, List<string> repairRules, CancellationToken cancellationToken)
        {
            Node node = null;

            if (foHealthData.NodeName != null)
            {
                node = await GetFabricNodeFromNodeNameAsync(foHealthData.NodeName, cancellationToken).ConfigureAwait(false);
            }

            if (node == null)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                            LogLevel.Warning,
                                            "RepairTaskManager.StartRepairWorkflowAsync",
                                            "Unable to locate target node. Aborting repair.",
                                            cancellationToken,
                                            null,
                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return;
            }

            try
            {
                if (repairRules.Any(r => r.Contains(RepairConstants.RestartVM)))
                {
                    // Do not allow VM reboot to take place in one-node cluster.
                    var nodes = await FabricClientInstance.QueryManager.GetNodeListAsync(
                                        null,
                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                        cancellationToken).ConfigureAwait(false);

                    int nodeCount = nodes.Count;

                    if (nodeCount == 1)
                    {
                        await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                  LogLevel.Warning,
                                                  "RepairTaskManager.StartRepairWorkflowAsync::OneNodeCluster",
                                                  "Will not attempt VM-level repair in a one node cluster.",
                                                  cancellationToken,
                                                  null,
                                                  FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                        return;
                    }
                }
            }
            catch (Exception e) when (e is FabricException || e is OperationCanceledException || e is TimeoutException)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                          LogLevel.Warning,
                                          "RepairTaskManager.StartRepairWorkflowAsync::NodeCount",
                                          $"Unable to determine node count. Will not attempt VM level repairs:{Environment.NewLine}{e}",
                                          cancellationToken,
                                          null,
                                          FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return;
            }

            foHealthData.NodeType = node.NodeType;

            try
            {
                _ = await RunGuanQueryAsync(foHealthData, repairRules);
            }
            catch (GuanException ge)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                           LogLevel.Warning,
                                           "StartRepairWorkflowAsync:GuanException",
                                           $"Failed in Guan: {ge}",
                                           cancellationToken,
                                           null,
                                           FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// This is the entry point to Guan parsing and query execution. It creates the necessary Guan objects to successfully execute logic rules based on supplied FO data 
        /// and related repair rules.
        /// </summary>
        /// <param name="foHealthData">Health data from FO for target SF entity</param>
        /// <param name="repairRules">Repair rules that are related to target SF entity</param>
        /// <param name="repairExecutorData">Optional Repair data that is used primarily when some repair is being restarted (after an FH restart, for example)</param>
        /// <returns></returns>
        public async Task<bool> RunGuanQueryAsync(TelemetryData foHealthData, List<string> repairRules, RepairExecutorData repairExecutorData = null)
        {
            // Add predicate types to functor table. Note that all health information data from FO are automatically passed to all predicates.
            FunctorTable functorTable = new FunctorTable();

            // Add external helper predicates.
            functorTable.Add(CheckFolderSizePredicateType.Singleton(RepairConstants.CheckFolderSize, this, foHealthData));
            functorTable.Add(GetRepairHistoryPredicateType.Singleton(RepairConstants.GetRepairHistory, this, foHealthData));
            functorTable.Add(GetHealthEventHistoryPredicateType.Singleton(RepairConstants.GetHealthEventHistory, this, foHealthData));
            functorTable.Add(CheckInsideRunIntervalPredicateType.Singleton(RepairConstants.CheckInsideRunInterval, this, foHealthData));
            functorTable.Add(EmitMessagePredicateType.Singleton(RepairConstants.EmitMessage, this));

            // Add external repair predicates.
            functorTable.Add(DeleteFilesPredicateType.Singleton(RepairConstants.DeleteFiles, this, foHealthData));
            functorTable.Add(RestartCodePackagePredicateType.Singleton(RepairConstants.RestartCodePackage, this, foHealthData));
            functorTable.Add(RestartFabricNodePredicateType.Singleton(RepairConstants.RestartFabricNode, this, repairExecutorData, repairTaskEngine, foHealthData));
            functorTable.Add(RestartFabricSystemProcessPredicateType.Singleton(RepairConstants.RestartFabricSystemProcess, this, foHealthData));
            functorTable.Add(RestartReplicaPredicateType.Singleton(RepairConstants.RestartReplica, this, foHealthData));
            functorTable.Add(RestartVMPredicateType.Singleton(RepairConstants.RestartVM, this, foHealthData));

            // Parse rules.
            Module module = Module.Parse("external", repairRules, functorTable);

            // Create guan query.
            var queryDispatcher = new GuanQueryDispatcher(module);

            /* Bind default arguments to goal (Mitigate). */

            List<CompoundTerm> compoundTerms = new List<CompoundTerm>();

            // Mitigate is the head of the rules used in FH. It's the goal that Guan will try to accomplish based on the logical expressions (or subgoals) that form a given rule.
            CompoundTerm compoundTerm = new CompoundTerm("Mitigate");

            // The type of metric that led FO to generate the unhealthy evaluation for the entity (App, Node, VM, Replica, etc).
            // We rename these for brevity for simplified use in logic rule composition (e;g., MetricName="Threads" instead of MetricName="Total Thread Count").
            foHealthData.Metric = FOErrorWarningCodes.GetMetricNameFromCode(foHealthData.Code);

            // These args hold the related values supplied by FO and are available anywhere Mitigate is used as a rule head.
            // Think of these as facts from FabricObserver.
            compoundTerm.AddArgument(new Constant(foHealthData.ApplicationName), RepairConstants.AppName);
            compoundTerm.AddArgument(new Constant(foHealthData.Code), RepairConstants.FOErrorCode);
            compoundTerm.AddArgument(new Constant(foHealthData.Metric), RepairConstants.MetricName);
            compoundTerm.AddArgument(new Constant(foHealthData.NodeName), RepairConstants.NodeName);
            compoundTerm.AddArgument(new Constant(foHealthData.NodeType), RepairConstants.NodeType);
            compoundTerm.AddArgument(new Constant(foHealthData.ObserverName), RepairConstants.ObserverName);
            compoundTerm.AddArgument(new Constant(foHealthData.OS), RepairConstants.OS);
            compoundTerm.AddArgument(new Constant(foHealthData.ServiceName), RepairConstants.ServiceName);
            compoundTerm.AddArgument(new Constant(foHealthData.SystemServiceProcessName), RepairConstants.SystemServiceProcessName);
            compoundTerm.AddArgument(new Constant(foHealthData.PartitionId), RepairConstants.PartitionId);
            compoundTerm.AddArgument(new Constant(foHealthData.ReplicaId), RepairConstants.ReplicaOrInstanceId);
            compoundTerm.AddArgument(new Constant(Convert.ToInt64(foHealthData.Value)), RepairConstants.MetricValue);
            compoundTerms.Add(compoundTerm);

            // Run Guan query.
            // This is where the supplied rules are run with FO data that may or may not lead to mitigation of some supported SF entity in trouble (or a VM/Disk).
            return await queryDispatcher.RunQueryAsync(compoundTerms).ConfigureAwait(false);
        }

        // The repair will be executed by SF Infrastructure service, not FH. This is the case for all
        // VM-level repairs. IS will communicate with VMSS (for example) to guarantee safe repairs in MR-enabled
        // clusters.RM, as usual, will orchestrate the repair cycle.
        public async Task<bool> ExecuteRMInfrastructureRepairTask(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            var infraServices = await FabricRepairTasks.GetInfrastructureServiceInstancesAsync(FabricClientInstance, cancellationToken).ConfigureAwait(false);
            var arrServices = infraServices as Service[] ?? infraServices.ToArray();

            if (arrServices.Length == 0)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "ExecuteRMInfrastructureRepairTask",
                                             "Infrastructure Service not found. Will not attemp VM repair.",
                                             cancellationToken,
                                             repairConfiguration,
                                             FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return false;
            }

            string executorName = null;

            foreach (var service in arrServices)
            {
                if (!service.ServiceName.OriginalString.Contains(repairConfiguration.NodeType))
                {
                    continue;
                }

                executorName = service.ServiceName.OriginalString;

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "RepairTaskManager.ExecuteRMInfrastructureRepairTask",
                                             $"IS RepairTask {RepairTaskEngine.HostVMReboot} " +
                                             $"Executor set to {executorName}.",
                                             cancellationToken,
                                             repairConfiguration,
                                             FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                break;
            }

            if (executorName == null)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "ExecuteRMInfrastructureRepairTask",
                                             "Unable to find InfrastructureService service instance." +
                                             "Exiting RepairTaskManager.ScheduleFHRepairTaskAsync.",
                                             cancellationToken,
                                             repairConfiguration,
                                             FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return false;
            }

            // Make sure there is not already a repair job executing reboot repair for target node.
            var isRepairAlreadyInProgress =
                    await repairTaskEngine.IsFHRepairTaskRunningAsync(
                                             executorName,
                                             repairConfiguration,
                                             cancellationToken).ConfigureAwait(false);

            if (isRepairAlreadyInProgress)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "ExecuteRMInfrastructureRepairTask",
                                             "Virtual machine repair task for VM " +
                                             $"{await RepairExec.GetMachineHostNameFromFabricNodeNameAsync(repairConfiguration.NodeName, cancellationToken)} " +
                                             "is already in progress. Will not schedule another VM repair at this time.",
                                             cancellationToken,
                                             repairConfiguration,
                                             FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return false;
            }

            // Create repair task for target node.
            var repairTask = await FabricRepairTasks.ScheduleRepairTaskAsync(
                                                             repairConfiguration,
                                                             null,
                                                             executorName,
                                                             FabricClientInstance,
                                                             cancellationToken).ConfigureAwait(false);

            if (repairTask == null)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "ExecuteRMInfrastructureRepairTask",
                                             "Unable to create Repair Task.",
                                             cancellationToken,
                                             repairConfiguration,
                                             FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                         LogLevel.Info,
                                         "ExecuteRMInfrastructureRepairTask",
                                         $"Successfully created Repair Task {repairTask.TaskId}",
                                         cancellationToken,
                                         repairConfiguration,
                                         FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

            var timer = Stopwatch.StartNew();

            // It can take a while to get from a VM reboot/reimage to a healthy Fabric node, so block here until repair completes.
            // Note that, by design, this will block any other FabricHealer-initiated repair from taking place in the cluster.
            // FabricHealer is designed to be very conservative with respect to node level repairs. 
            // It is a good idea to not change this default behavior.
            while (timer.Elapsed < MaxWaitTimeForInfraRepairTaskCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!await FabricRepairTasks.IsRepairTaskInDesiredStateAsync(
                                               repairTask.TaskId,
                                               FabricClientInstance,
                                               executorName,
                                               new List<RepairTaskState> { RepairTaskState.Completed }))
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                             LogLevel.Info,
                                             "ExecuteRMInfrastructureRepairTask::Completed",
                                             $"Successfully completed repair {repairConfiguration.RepairPolicy.RepairId}",
                                             cancellationToken,
                                             repairConfiguration,
                                             FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                timer.Stop();
                return true;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                         LogLevel.Info,
                                         "ExecuteRMInfrastructureRepairTask::Timeout",
                                         $"Max wait time of {MaxWaitTimeForInfraRepairTaskCompleted} has elapsed for repair " +
                                         $"{repairConfiguration.RepairPolicy.RepairId}.",
                                         cancellationToken,
                                         repairConfiguration,
                                         FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            return false;
        }

        public async Task<bool> DeleteFilesAsyncAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            return await RepairExec.DeleteFilesAsync(repairConfiguration, cancellationToken);
        }

        public async Task<bool> RestartReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            var result = await RepairExec.RestartReplicaAsync(
                                            repairConfiguration ?? throw new ArgumentException("configuration can't be null."),
                                            cancellationToken).ConfigureAwait(false);
            return result != null;
        }

        public async Task<bool> RemoveReplicaAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            var result = await RepairExec.RemoveReplicaAsync(
                                            repairConfiguration ?? throw new ArgumentException("configuration can't be null."),
                                            cancellationToken).ConfigureAwait(false);
            return result != null;
        }

        public async Task<bool> RestartDeployedCodePackageAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            string actionMessage =
                "Attempting to restart deployed code package for service " +
                $"{repairConfiguration.ServiceName.OriginalString} " +
                $"({repairConfiguration.ReplicaOrInstanceId}) on Node {repairConfiguration.NodeName}.";

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RestartDeployedCodePackageAsync::Starting",
                                        actionMessage,
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

            var result = await RepairExec.RestartDeployedCodePackageAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);

            if (result == null)
            {
                return false;
            }

            actionMessage =
                "Successfully restarted deployed code package for service " +
                $"{repairConfiguration.ServiceName.OriginalString} " +
                $"({repairConfiguration.ReplicaOrInstanceId}) on Node {repairConfiguration.NodeName}.";

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RestartDeployedCodePackageAsync::Success",
                                        actionMessage,
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            return true;
        }

        /// <summary>
        /// Restarts Service Fabric system service process.
        /// </summary>
        /// <param name="repairConfiguration">RepairConfiguration instance.</param>
        /// <param name="cancellationToken">CancellationToken instance.</param>
        /// <returns>A Task containing a boolean value representing success or failure of the repair action.</returns>
        private async Task<bool> RestartSystemServiceProcessAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repairConfiguration.SystemServiceProcessName))
            {
                return false;
            }

            // Can only kill processes on the same node where FH instance that took the job is running.
            if (repairConfiguration.NodeName != Context.NodeContext.NodeName)
            {
                return false;
            }

            string actionMessage =
               $"Attempting to restart Service Fabric system process {repairConfiguration.SystemServiceProcessName}.";

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairExecutor.RestartSystemServiceProcessAsync::Start",
                                        actionMessage,
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

            bool result = await RepairExec.RestartSystemServiceProcessAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);

            if (!result)
            {
                return false;
            }

            string statusSuccess = $"Successfully restarted Service Fabric system service process {repairConfiguration.SystemServiceProcessName} on node {repairConfiguration.NodeName}.";

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairExecutor.RestartSystemServiceProcessAsync::Success",
                                        statusSuccess,
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            return true;
        }

        private async Task<Node> GetFabricNodeFromNodeNameAsync(string nodeName, CancellationToken cancellationToken)
        {
            try
            {
                var nodes = await FabricClientInstance.QueryManager.GetNodeListAsync(nodeName, AsyncTimeout, cancellationToken).ConfigureAwait(false);
                return nodes.Count > 0 ? nodes[0] : null;
            }
            catch (FabricException fe)
            {
                FabricHealerManager.RepairLogger.LogError($"Error getting node {nodeName}:{Environment.NewLine}{fe}");
                return null;
            }
        }

        public async Task<RepairTask> ScheduleFabricHealerRepairTaskAsync(RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            await Task.Delay(new Random().Next(100, 1500));

            // Has the repair already been scheduled by a different FH instance?
            if (await repairTaskEngine.IsFHRepairTaskRunningAsync(RepairTaskEngine.FHTaskIdPrefix, repairConfiguration, cancellationToken))
            {
                return null;
            }

            // Don't attempt a node level repair on a node where there is already an active node-level repair.
            var currentlyExecutingRepairs =
                await FabricClientInstance.RepairManager.GetRepairTaskListAsync(
                                                            RepairTaskEngine.FHTaskIdPrefix,
                                                            RepairTaskStateFilter.Active | RepairTaskStateFilter.Approved | RepairTaskStateFilter.Executing,
                                                            RepairTaskEngine.FabricHealerExecutorName,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            cancellationToken).ConfigureAwait(false);

            if (currentlyExecutingRepairs.Count > 0)
            {
                foreach (var repair in currentlyExecutingRepairs.Where(task => task.ExecutorData.Contains(repairConfiguration.NodeName)))
                {
                    if (!JsonSerializationUtility.TryDeserialize(repair.ExecutorData, out RepairExecutorData repairExecutorData))
                    {
                        continue;
                    }

                    if (repairExecutorData.RepairPolicy.RepairAction != RepairActionType.RestartFabricNode &&
                        repairExecutorData.RepairPolicy.RepairAction != RepairActionType.RestartVM)
                    {
                        continue;
                    }

                    string message =
                        $"Node {repairConfiguration.NodeName} already has a node-impactful repair in progress: " +
                        $"{Enum.GetName(typeof(RepairActionType), repairConfiguration.RepairPolicy.RepairAction)}: {repair.TaskId}" +
                        "Exiting RepairTaskManager.ScheduleFabricHealerRmRepairTaskAsync.";

                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                LogLevel.Info,
                                                "ScheduleRepairTask::NodeRepairAlreadyInProgress",
                                                message,
                                                cancellationToken,
                                                repairConfiguration,
                                                FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                    return null;
                }
            }

            var executorData = new RepairExecutorData
            {
                ExecutorTimeoutInMinutes = (int)MaxWaitTimeForInfraRepairTaskCompleted.TotalMinutes,
                FOErrorCode = repairConfiguration.FOErrorCode,
                FOMetricValue = repairConfiguration.FOHealthMetricValue,
                RepairPolicy = repairConfiguration.RepairPolicy,
                NodeName = repairConfiguration.NodeName,
                NodeType = repairConfiguration.NodeType,
                PartitionId = repairConfiguration.PartitionId,
                ReplicaOrInstanceId = repairConfiguration.ReplicaOrInstanceId,
                ServiceName = repairConfiguration.ServiceName,
                SystemServiceProcessName = repairConfiguration.SystemServiceProcessName,
            };

            // Create custom FH repair task for target node.
            var repairTask = await FabricRepairTasks.ScheduleRepairTaskAsync(
                                                        repairConfiguration,
                                                        executorData,
                                                        RepairTaskEngine.FabricHealerExecutorName,
                                                        FabricClientInstance,
                                                        cancellationToken).ConfigureAwait(false);
            return repairTask;
        }

        public async Task<bool> ExecuteFabricHealerRmRepairTaskAsync(RepairTask repairTask, RepairConfiguration repairConfiguration, CancellationToken cancellationToken)
        {
            if (repairTask == null)
            {
                return false;
            }

            TimeSpan approvalTimeout = TimeSpan.FromMinutes(10);
            Stopwatch stopWatch = Stopwatch.StartNew();
            bool isApproved = false;

            var repairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, cancellationToken).ConfigureAwait(false);

            if (repairs.All(repair => repair.TaskId != repairTask.TaskId))
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                            LogLevel.Info,
                                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                            $"Failed to find scheduled repair task {repairTask.TaskId}.",
                                            Token,
                                            repairConfiguration,
                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                return false;
            }

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairTaskManager::WaitingForApproval",
                                        $"Waiting for RM to Approve repair task {repairTask.TaskId}.",
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

            while (approvalTimeout >= stopWatch.Elapsed)
            {
                repairs = await repairTaskEngine.GetFHRepairTasksCurrentlyProcessingAsync(RepairTaskEngine.FabricHealerExecutorName, cancellationToken).ConfigureAwait(false);

                // Was repair cancelled (or cancellation requested) by another FH instance for some reason? Could be due to FH going down or a new deployment or a bug (fix it...).
                if (repairs.Any(repair => repair.TaskId == repairTask.TaskId
                                       && (repair.State == RepairTaskState.Completed && repair.ResultStatus == RepairTaskResult.Cancelled
                                           || repair.Flags == RepairTaskFlags.CancelRequested || repair.Flags == RepairTaskFlags.AbortRequested)))
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                LogLevel.Info,
                                                "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                                $"Repair Task {repairTask.TaskId} was aborted or cancelled.",
                                                Token,
                                                repairConfiguration,
                                                FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                    return false;
                }

                if (!repairs.Any(repair => repair.TaskId == repairTask.TaskId && repair.State == RepairTaskState.Approved))
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                isApproved = true;
                break;
            }

            stopWatch.Stop();
            stopWatch.Reset();

            if (isApproved)
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                           LogLevel.Info,
                                           "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_Approved",
                                           $"RM has Approved repair task {repairTask.TaskId}.",
                                           cancellationToken,
                                           repairConfiguration,
                                           FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            }
            else
            {
                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                            LogLevel.Info,
                                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_NotApproved",
                                            $"RM did not Approve repair task {repairTask.TaskId}. Cancelling...",
                                            cancellationToken,
                                            repairConfiguration,
                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

                await FabricRepairTasks.CancelRepairTaskAsync(repairTask, FabricClientInstance);
                return false;
            }

            _ = await FabricRepairTasks.SetFabricRepairJobStateAsync(
                                            repairTask,
                                            RepairTaskState.Executing,
                                            RepairTaskResult.Pending,
                                            FabricClientInstance,
                                            cancellationToken).ConfigureAwait(false);

            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_MovedExecuting",
                                        $"Executing repair {repairTask.TaskId}.",
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
            bool success;
            var repairAction = repairConfiguration.RepairPolicy.RepairAction;
            try
            {
                switch (repairAction)
                {
                    case RepairActionType.DeleteFiles:
                        {
                            success = await DeleteFilesAsyncAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                            break;
                        }

                    // Note: For SF app container services, RestartDeployedCodePackage API does not work.
                    // Thus, using Restart/Remove(stateful/stateless)Replica API instead, which does restart container instances.
                    case RepairActionType.RestartCodePackage:
                        {
                            if (string.IsNullOrWhiteSpace(repairConfiguration.ContainerId))
                            {
                                success = await RestartDeployedCodePackageAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                // Need replica or instance details..
                                var repList = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                                                                                        repairConfiguration.PartitionId,
                                                                                        repairConfiguration.ReplicaOrInstanceId,
                                                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                        cancellationToken).ConfigureAwait(false);
                                if (repList.Count == 0)
                                {
                                    success = false;
                                    break;
                                }

                                var rep = repList[0];

                                // Restarting stateful replica will restart the container instance.
                                if (rep.ServiceKind == ServiceKind.Stateful)
                                {
                                    success = await RestartReplicaAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                                }
                                else
                                {
                                    // For stateless intances, you need to remove the replica, which will
                                    // restart the container instance.
                                    success = await RemoveReplicaAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                                }
                            }

                            break;
                        }
                    case RepairActionType.RemoveReplica:
                        {
                            var repList = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                                                            repairConfiguration.PartitionId,
                                                            repairConfiguration.ReplicaOrInstanceId,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            cancellationToken).ConfigureAwait(false);
                            if (repList.Count == 0)
                            {
                                success = false;
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                                            $"Stateless Instance {repairConfiguration.ReplicaOrInstanceId} not found on partition " +
                                                            $"{repairConfiguration.PartitionId}.",
                                                            cancellationToken,
                                                            repairConfiguration,
                                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                                break;
                            }

                            success = await RemoveReplicaAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                    case RepairActionType.RestartProcess:
                        {
                            success = await RestartSystemServiceProcessAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                            break;
                        }
                    case RepairActionType.RestartReplica:
                        {
                            var repList = await FabricClientInstance.QueryManager.GetReplicaListAsync(
                                                            repairConfiguration.PartitionId,
                                                            repairConfiguration.ReplicaOrInstanceId,
                                                            FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                            cancellationToken).ConfigureAwait(false);
                            if (repList.Count == 0)
                            {
                                success = false;
                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                                            $"Stateful replica {repairConfiguration.ReplicaOrInstanceId} not found on partition " +
                                                            $"{repairConfiguration.PartitionId}.",
                                                            cancellationToken,
                                                            repairConfiguration,
                                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                                break;
                            }

                            var replica = repList[0];

                            // Restart - stateful replica.
                            if (replica.ServiceKind == ServiceKind.Stateful)
                            {
                                success = await RestartReplicaAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                            }
                            else
                            {
                                // For stateless replicas (aka instances), you need to remove the replica. The runtime will create a new one
                                // and place it.
                                success = await RemoveReplicaAsync(repairConfiguration, cancellationToken).ConfigureAwait(false);
                            }

                            break;
                        }
                    case RepairActionType.RestartFabricNode:
                        {
                            var executorData = repairTask.ExecutorData;

                            if (string.IsNullOrWhiteSpace(executorData))
                            {

                                await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            "RepairTaskManager.SafeRestartFabricNode",
                                                            $"Repair {repairTask.TaskId} is missing ExecutorData.",
                                                            cancellationToken,
                                                            repairConfiguration,
                                                            FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                                success = false;
                            }
                            else
                            {
                                success = await SafeRestartServiceFabricNodeAsync(repairConfiguration, repairTask, cancellationToken).ConfigureAwait(false);
                            }

                            break;
                        }
                    default:
                        return false;
                }
            }
            catch (FabricException)
            {
                return false;
            }

            // What was the target (a node, app, replica, etc..)?
            string repairTarget = repairConfiguration.RepairPolicy.RepairId;

            switch (repairConfiguration.RepairPolicy.TargetType)
            {
                case RepairTargetType.Application:
                    {
                        repairTarget = $"{repairConfiguration.AppName.OriginalString} on Node {repairConfiguration.NodeName}";

                        if (repairConfiguration.AppName.OriginalString == RepairConstants.SystemAppName && !string.IsNullOrWhiteSpace(repairConfiguration.SystemServiceProcessName))
                        {
                            repairTarget = $"{repairConfiguration.SystemServiceProcessName} on Node {repairConfiguration.NodeName}";
                        }

                        break;
                    }

                case RepairTargetType.Node:
                    repairTarget = repairConfiguration.NodeName;
                    break;

                case RepairTargetType.Replica:
                    repairTarget = $"{repairConfiguration.ServiceName}";
                    break;

                case RepairTargetType.Partition:
                    break;

                case RepairTargetType.VirtualMachine:
                    break;

                default:
                    throw new ArgumentException("Unknown repair target type.");
            }

            if (success)
            {
                string target = Enum.GetName(typeof(RepairTargetType), repairConfiguration.RepairPolicy.TargetType);
                TimeSpan maxWaitForHealthStateOk = TimeSpan.FromMinutes(30);

                switch (repairConfiguration.RepairPolicy.TargetType)
                {
                    case RepairTargetType.Application when repairConfiguration.AppName.OriginalString != RepairConstants.SystemAppName:
                    case RepairTargetType.Replica:
                        maxWaitForHealthStateOk = repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(10);
                        break;

                    case RepairTargetType.Application when repairConfiguration.AppName.OriginalString == RepairConstants.SystemAppName && repairConfiguration.RepairPolicy.RepairAction == RepairActionType.RestartProcess:
                        maxWaitForHealthStateOk = repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                           ? repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck
                           : TimeSpan.FromMinutes(5);
                        break;

                    case RepairTargetType.Application when repairConfiguration.AppName.OriginalString == RepairConstants.SystemAppName && repairConfiguration.RepairPolicy.RepairAction == RepairActionType.RestartFabricNode:
                        maxWaitForHealthStateOk = repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck > TimeSpan.MinValue
                            ? repairConfiguration.RepairPolicy.MaxTimePostRepairHealthCheck
                            : TimeSpan.FromMinutes(30);
                        break;

                    case RepairTargetType.Node:
                        break;

                    case RepairTargetType.Partition:
                        break;

                    case RepairTargetType.VirtualMachine:
                        break;

                    default:
                        throw new ArgumentOutOfRangeException();
                }

                // Check healthstate of repair target to see if the repair worked.
                bool isHealthy = await IsRepairTargetHealthyAfterCompletedRepair(repairConfiguration, maxWaitForHealthStateOk, cancellationToken);

                if (isHealthy)
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                LogLevel.Info,
                                                "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                                $"{target} Repair target {repairTarget} successfully healed.",
                                                cancellationToken,
                                                repairConfiguration,
                                                FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                }
                else
                {
                    await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                LogLevel.Info,
                                                "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync",
                                                $"{target} Repair target {repairTarget} not successfully healed.",
                                                cancellationToken,
                                                repairConfiguration,
                                                FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);
                }

                // Tell RM we are ready to move to Completed state as our custom code has completed its repair execution successfully.
                // This is done by setting the repair task to Restoring State with ResultStatus Succeeded. RM will then move forward to Restoring
                // (and do any restoring health checks if specified), then Complete the repair job.
                _ = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                   () => FabricRepairTasks.CompleteCustomActionRepairJobAsync(
                                                                               repairTask,
                                                                               FabricClientInstance,
                                                                               Context,
                                                                               cancellationToken),
                                                   cancellationToken).ConfigureAwait(false);

                // Let RM catch up.
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
                return isHealthy;
            }

            // Executor failure. Cancel repair task.
            await TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                        LogLevel.Info,
                                        "RepairTaskManager.ExecuteFabricHealerRmRepairTaskAsync_ExecuteFailed",
                                        $"Executor failed for repair {repairTask.TaskId}. See logs for details. Cancelling repair task.",
                                        cancellationToken,
                                        repairConfiguration,
                                        FabricHealerManager.ConfigSettings.EnableVerboseLogging).ConfigureAwait(false);

            await FabricRepairTasks.CancelRepairTaskAsync(repairTask, FabricClientInstance).ConfigureAwait(false);
            return false;
        }

        // Support for GetHealthEventHistoryPredicateType, which enables time-scoping logic rules based on health events related to specific SF entities/targets.
        internal int GetEntityHealthEventCountWithinTimeRange(string property, TimeSpan timeWindow)
        {
            int count = 0;
            var healthEvents = DetectedHealthEvents.Where(evt => evt.HealthInformation.Property == property);

            if (healthEvents == null || !healthEvents.Any())
            {
                return count;
            }

            foreach (HealthEvent healthEvent in healthEvents)
            {
                if (DateTime.UtcNow.Subtract(healthEvent.SourceUtcTimestamp) > timeWindow)
                {
                    continue;
                }
                count++;
            }

            // Lifetime management of Health Events list data. Data is kept in-memory only for 2 days. If FH process restarts, data is not preserved.
            if (DateTime.UtcNow.Subtract(LastHealthEventsListClearDateTime) >= MaxLifeTimeHealthEventsData)
            {
                DetectedHealthEvents.Clear();
                LastHealthEventsListClearDateTime = DateTime.UtcNow;
            }

            return count;
        }

        /// <summary>
        /// This function checks to see if the target of a repair is healthy after the repair task completed. 
        /// This will signal the result via telemetry and as a health event.
        /// </summary>
        /// <param name="repairConfig">RepairConfiguration instance</param>
        /// <param name="maxTimeToWait">Amount of time to wait for cluster to settle.</param>
        /// <param name="token">CancellationToken instance</param>
        /// <returns>Boolean representing whether the repair target is healthy after a completed repair operation.</returns>
        private async Task<bool> IsRepairTargetHealthyAfterCompletedRepair(RepairConfiguration repairConfig, TimeSpan maxTimeToWait, CancellationToken token)
        {
            if (repairConfig == null)
            {
                return false;
            }

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed <= maxTimeToWait)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                if (await GetCurrentAggregatedHealthStateAsync(repairConfig, token).ConfigureAwait(false) == HealthState.Ok)
                {
                    stopwatch.Stop();
                    return true;
                }

                await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }

            stopwatch.Stop();
            return false;
        }

        /// <summary>
        /// Determines aggregated health state for repair target in supplied repair configuration.
        /// </summary>
        /// <param name="repairConfig">RepairConfiguration instance.</param>
        /// <param name="token">CancellationToken instance.</param>
        /// <returns></returns>
        private async Task<HealthState> GetCurrentAggregatedHealthStateAsync(RepairConfiguration repairConfig, CancellationToken token)
        {
            switch (repairConfig.RepairPolicy.TargetType)
            {
                case RepairTargetType.Application:
                {
                    var appHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                                    () => FabricClientInstance.HealthManager.GetApplicationHealthAsync(
                                                                                repairConfig.AppName,
                                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                token),
                                                                    token);

                    bool isTargetAppHealedOnTargetNode = false;
                    
                    // System Service repairs (process restarts)
                    if (repairConfig.AppName.OriginalString == RepairConstants.SystemAppName)
                    {
                        isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                            h => JsonSerializationUtility.TryDeserialize(
                                    h.HealthInformation.Description,
                                    out TelemetryData foHealthData)
                                        && foHealthData.NodeName == repairConfig.NodeName
                                        && foHealthData.SystemServiceProcessName == repairConfig.SystemServiceProcessName
                                        && foHealthData.HealthState.ToLower() == "ok");
                    }
                    else // Application repairs (code package restarts)
                    {
                        isTargetAppHealedOnTargetNode = appHealth.HealthEvents.Any(
                            h => JsonSerializationUtility.TryDeserialize(
                                    h.HealthInformation.Description,
                                    out TelemetryData foHealthData)
                                        && foHealthData.NodeName == repairConfig.NodeName
                                        && foHealthData.ApplicationName == repairConfig.AppName.OriginalString
                                        && foHealthData.HealthState.ToLower() == "ok");
                    }

                    return isTargetAppHealedOnTargetNode ? HealthState.Ok : appHealth.AggregatedHealthState;
                }
                case RepairTargetType.Node:
                case RepairTargetType.VirtualMachine:
                {
                    var nodeHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                                    () => FabricClientInstance.HealthManager.GetNodeHealthAsync(
                                                                                repairConfig.NodeName,
                                                                                FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                token),
                                                                    token);

                    bool isTargetNodeHealed = nodeHealth.HealthEvents.Any(
                                                h => JsonSerializationUtility.TryDeserialize(
                                                        h.HealthInformation.Description,
                                                        out TelemetryData foHealthData)
                                                        && foHealthData.NodeName == repairConfig.NodeName
                                                        && foHealthData.HealthState.ToLower() == "ok");

                    return isTargetNodeHealed ? HealthState.Ok : nodeHealth.AggregatedHealthState;
                }
                case RepairTargetType.Replica:
                {
                    // Make sure the Partition where the restarted replica was located is now healthy.
                    var partitionHealth = await FabricClientRetryHelper.ExecuteFabricActionWithRetryAsync(
                                                                            () => FabricClientInstance.HealthManager.GetPartitionHealthAsync(
                                                                                        repairConfig.PartitionId,
                                                                                        FabricHealerManager.ConfigSettings.AsyncTimeout,
                                                                                        token),
                                                                            token);
                    return partitionHealth.AggregatedHealthState;
                }
                default:
                    return HealthState.Unknown;
            }
        }
    }
}
