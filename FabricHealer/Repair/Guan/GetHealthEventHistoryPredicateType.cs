﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Guan.Logic;
using FabricHealer.Utilities.Telemetry;
using FabricHealer.Utilities;

namespace FabricHealer.Repair.Guan
{
    public class GetHealthEventHistoryPredicateType : PredicateType
    {
        private static RepairTaskManager RepairTaskManager;
        private static TelemetryData FOHealthData;
        private static GetHealthEventHistoryPredicateType Instance;

        private class Resolver : GroundPredicateResolver
        {
            public Resolver(CompoundTerm input, Constraint constraint, QueryContext context)
                    : base(input, constraint, context, 1)
            {

            }

            protected override Task<Term> GetNextTermAsync()
            {
                long eventCount = 0;
                var timeRange = (TimeSpan)Input.Arguments[1].Value.GetEffectiveTerm().GetObjectValue();

                if (timeRange > TimeSpan.MinValue)
                {
                    eventCount = RepairTaskManager.GetEntityHealthEventCountWithinTimeRange(FOHealthData.HealthEventProperty, timeRange);
                }
                else
                {
                    string message = "You must supply a valid TimeSpan argument for GetHealthEventHistoryPredicateType. Default result has been supplied (0).";

                    RepairTaskManager.TelemetryUtilities.EmitTelemetryEtwHealthEventAsync(
                                                            LogLevel.Info,
                                                            $"GetHealthEventHistoryPredicateType::{FOHealthData.HealthEventProperty}",
                                                            message,
                                                            RepairTaskManager.Token).GetAwaiter().GetResult();
                }

                var result = new CompoundTerm();

                // By using "0" for name here means the rule can pass any name for this named variable arg as long as it is consistently used as such in the corresponding rule.
                result.AddArgument(new Constant(eventCount), "0");
                return Task.FromResult<Term>(result);
            }
        }

        public static GetHealthEventHistoryPredicateType Singleton(string name, RepairTaskManager repairTaskManager, TelemetryData foHealthData)
        {
            RepairTaskManager = repairTaskManager;
            FOHealthData = foHealthData;

            return Instance ??= new GetHealthEventHistoryPredicateType(name);
        }

        private GetHealthEventHistoryPredicateType(string name)
                 : base(name, true, 2, 2)
        {

        }

        public override PredicateResolver CreateResolver(CompoundTerm input, Constraint constraint, QueryContext context)
        {
            return new Resolver(input, constraint, context);
        }

        public override void AdjustTerm(CompoundTerm term, Rule rule)
        {
            if (term.Arguments[0].Value.IsGround())
            {
                throw new GuanException("The first argument of GetHealthEventHistory must be a variable: {0}", term);
            }
        }
    }
}
