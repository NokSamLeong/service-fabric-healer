﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System.Threading.Tasks;
using Guan.Common;
using Guan.Logic;
using System;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;

namespace FabricHealer.Repair.Guan
{
    public class GetRepairHistoryPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static GetRepairHistoryPredicateType Instance;

        class Resolver : GroundPredicateResolver
        {
            public Resolver(
                CompoundTerm input, 
                Constraint constraint, 
                QueryContext context)
                : base(input, constraint, context, 1)
            {

            }

            protected override Task<Term> GetNextTermAsync()
            {
                long repairCount = 0;
                TimeSpan timeWindow = (TimeSpan)Input.Arguments[1].Value.GetEffectiveTerm().GetValue();

                if (timeWindow > TimeSpan.MinValue)
                {
                    repairCount = FabricRepairTasks.GetCompletedRepairCountWithinTimeRangeAsync(
                                        timeWindow,
                                        RepairTaskManager.FabricClientInstance,
                                        FOHealthData,
                                        RepairTaskManager.Token).GetAwaiter().GetResult();
                }
                else
                {
                    string message = $"You must supply a valid TimeSpan string for TimeWindow argument of GetRepairHistoryPredicate. Default result has been supplied (0).";

                    RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                            LogLevel.Info,
                            $"GetRepairHistoryPredicate::{FOHealthData.RepairId}",
                            message,
                            RepairTaskManager.Token).GetAwaiter().GetResult();
                }

                var result = new CompoundTerm(Instance, null);

                // By using "0" for name here means the rule can pass any name for this named variable arg as long as it is consistently used as such in the corresponding rule.
                result.AddArgument(new Constant(repairCount), "0");

                return Task.FromResult<Term>(result);
            }
        }

        public static GetRepairHistoryPredicateType Singleton(
            string name,
            RepairTaskManager repairTaskManager,
            TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new GetRepairHistoryPredicateType(name);
        }

        private GetRepairHistoryPredicateType(
            string name)
            : base(name, true, 2, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }

        public override void AdjustTerm(CompoundTerm term, Rule rule)
        {
            if (!(term.Arguments[0].Value is IndexedVariable))
            {
                throw new GuanException("The first argument, ?repairCount, of GetRepairHistoryPredicateType must be a variable: {0}", term);
            }
        }
    }
}
