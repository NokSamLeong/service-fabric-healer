﻿// ------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See License.txt in the repo root for license information.
// ------------------------------------------------------------
namespace Guan.Logic
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Threading.Tasks;

    /// <summary>
    /// A module is a collection of predicate types.
    /// </summary>
    public class Module
    {
        private static readonly Module SystemModule = CreateSystemModule();

        private string name;
        private bool dynamic;
        private Dictionary<string, PredicateType> types;

        internal Module(string name)
        {
            this.name = name;
            this.types = new Dictionary<string, PredicateType>();
            this.dynamic = true;
        }

        private Module(string name, Dictionary<string, PredicateType> types)
        {
            this.name = name;
            this.types = types;
            this.dynamic = false;
        }

        public string Name
        {
            get
            {
                return this.name;
            }
        }

        public static async Task<Module> ParseAsync(string path, IFunctorProvider provider, List<string> publicTypes = null)
        {
            List<string> ruleExpressions = new List<string>();
            string current = null;
            using (StreamReader reader = new StreamReader(path))
            {
                string line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    line = line.Trim();
                    if (line.Length > 0 && !line.StartsWith("#"))
                    {
                        if (current != null)
                        {
                            line = current + line;
                        }

                        if (line.EndsWith("\\"))
                        {
                            line = line.TrimEnd('\\');
                            current = line;
                        }
                        else
                        {
                            ruleExpressions.Add(line);
                            current = null;
                        }
                    }
                }

                ReleaseAssert.IsTrue(current == null, "Last line not completed for {0}", path);
            }

            return Parse(path, ruleExpressions, provider, publicTypes);
        }

        public static Module Parse(string name, List<string> ruleExpressions, IFunctorProvider provider, List<string> publicTypes = null)
        {
            List<Rule> rules = new List<Rule>(ruleExpressions.Count);
            foreach (string ruleExpression in ruleExpressions)
            {
                Rule rule;
                try
                {
                    rule = Rule.Parse(ruleExpression);
                    rules.Add(rule);
                }
                catch (GuanException e)
                {
                    throw new GuanException(e, "Fail to parse rule {0} in module {1}", ruleExpression, name);
                }
            }

            return Parse(name, rules, provider, publicTypes);
        }

        /// <summary>
        /// Add a dynamic predicate (assert).
        /// </summary>
        /// <param name="term">The predicate.</param>
        /// <param name="append">Whether to add the predicate in append mode.</param>
        public void Add(CompoundTerm term, bool append)
        {
            if (!this.dynamic)
            {
                throw new GuanException("Not a dynamic module");
            }

            lock (this.types)
            {
                PredicateType type;
                if (!this.types.TryGetValue(term.Functor.Name, out type))
                {
                    type = new PredicateType(term.Functor.Name, true);
                    this.types.Add(term.Functor.Name, type);
                }

                CompoundTerm head = new CompoundTerm(type, VariableBinding.Ground, term.Arguments);
                Rule rule = new Rule(term.ToString(), head, new List<CompoundTerm>(), VariableTable.Empty);
                type.AddRule(this, rule, append);
            }
        }

        public void Add(PredicateType predicateType)
        {
            lock (this.types)
            {
                this.types[predicateType.Name] = predicateType;
            }
        }

        public IEnumerable<PredicateType> GetPublicTypes()
        {
            List<PredicateType> result = new List<PredicateType>();
            foreach (KeyValuePair<string, PredicateType> type in this.types)
            {
                if (type.Value.IsPublic)
                {
                    result.Add(type.Value);
                }
            }

            return result;
        }

        public PredicateType GetPredicateType(string name)
        {
            PredicateType result;
            if (this.types.TryGetValue(name, out result) && result.IsPublic)
            {
                return result;
            }

            return null;
        }

        public override string ToString()
        {
            return this.name;
        }

        internal static Module Parse(string name, List<Rule> rules, IFunctorProvider provider, List<string> publicTypes)
        {
            Dictionary<string, PredicateType> types = new Dictionary<string, PredicateType>();
            Module result = new Module(name, types);

            for (int i = 0; i < rules.Count;)
            {
                string typeName = rules[i].Head.Functor.Name;
                if (typeName != "desc")
                {
                    if (!types.TryGetValue(typeName, out PredicateType type))
                    {
                        if (provider != null)
                        {
                            type = provider.FindFunctor(typeName, result) as PredicateType;
                        }

                        if (type == null)
                        {
                            bool isPublic = (publicTypes != null ? publicTypes.Contains(typeName) : !typeName.StartsWith("_"));
                            type = new PredicateType(typeName, isPublic);
                        }

                        types.Add(typeName, type);
                    }

                    rules[i].Head.Functor = type;
                    UpdateFunctor(rules[i].Head, types, provider, result);
                    i++;
                }
                else
                {
                    ProcessDesc(rules[i], types);
                    rules.RemoveAt(i);
                }
            }

            for (int i = 0; i < rules.Count; i++)
            {
                bool remove = false;
                foreach (CompoundTerm goal in rules[i].Goals)
                {
                    string typeName = goal.Functor.Name;
                    PredicateType type;
                    if (!types.TryGetValue(typeName, out type))
                    {
                        type = GetGoalPredicateType(typeName, provider, result);
                        if (type == FailPredicateType.NotApplicable)
                        {
                            remove = true;
                        }
                        else if (type == null)
                        {
                            throw new GuanException("Predicate type {0} not defined in rule {1}", typeName, rules[i]);
                        }
                    }

                    goal.Functor = type;
                    UpdateFunctor(goal, types, provider, result);
                }

                if (!remove)
                {
                    try
                    {
                        rules[i].PostProcessing();
                    }
                    catch (GuanException e)
                    {
                        throw new GuanException(e, "Fail to process rule {0}", rules[i]);
                    }

                    rules[i].Head.PredicateType.AddRule(result, rules[i], true);
                }
            }

            return result;
        }

        private static void ProcessDesc(Rule rule, Dictionary<string, PredicateType> types)
        {
            if (rule.Goals.Count > 0 || rule.Head.Arguments.Count < 2 || !rule.Head.IsGround() || !(rule.Head.Arguments[0].Value is Constant))
            {
                throw new GuanException("Invalid desc predicate: {0}", rule);
            }

            rule.ProcessMetaDataHead();

            Constant constant = (Constant)rule.Head.Arguments[0].Value;
            PredicateType type;
            string typeName = constant.GetStringValue();
            if (typeName == null || !types.TryGetValue(typeName, out type))
            {
                if (rule.Head.Arguments.Count == 2)
                {
                    Constant arg = rule.Head.Arguments[1].Value as Constant;
                    if (arg != null && arg.GetStringValue() == "dynamic")
                    {
                        types.Add(typeName, new DynamicPredicateType(typeName));
                        return;
                    }
                }

                throw new GuanException("Type {0} in desc predicate not defined", constant);
            }

            for (int i = 1; i < rule.Head.Arguments.Count; i++)
            {
                string name = rule.Head.Arguments[i].Name;
                if (name == i.ToString())
                {
                    name = null;
                }

                try
                {
                    type.ProcessMetaData(name, rule.Head.Arguments[i].Value);
                }
                catch (GuanException e)
                {
                    throw new GuanException(e, "Meta-data {0} {1} can't be applied to {2}", name, rule.Head.Arguments[i].Value, type);
                }
            }
        }

        private static Functor GetFunctor(string name, IFunctorProvider provider, Module from)
        {
            Functor result;
            if (provider != null)
            {
                result = provider.FindFunctor(name, from);
                if (result != null)
                {
                    return result;
                }
            }

            result = PredicateType.GetBuiltInType(name);
            if (result != null)
            {
                return result;
            }

            if (name == Functor.ClassObject.Name)
            {
                return Functor.ClassObject;
            }

            if (SystemModule != null)
            {
                result = SystemModule.GetPredicateType(name);
                if (result != null)
                {
                    return result;
                }
            }

            GuanFunc func = GuanExpression.GetGuanFunc(name);
            if (func != null)
            {
                return new EvaluatedFunctor(func);
            }

            return null;
        }

        private static PredicateType GetGoalPredicateType(string typeName, IFunctorProvider provider, Module from)
        {
            Functor functor = GetFunctor(typeName, provider, from);
            EvaluatedFunctor evaluatedFunctor = functor as EvaluatedFunctor;
            if (evaluatedFunctor != null)
            {
                return evaluatedFunctor.ConstraintType;
            }
            else
            {
                return functor as PredicateType;
            }
        }

        private static void UpdateFunctor(CompoundTerm term, Dictionary<string, PredicateType> types, IFunctorProvider provider, Module from)
        {
            foreach (TermArgument arg in term.Arguments)
            {
                CompoundTerm compound = arg.Value as CompoundTerm;
                if (compound != null)
                {
                    if (!(compound.Functor is PredicateType))
                    {
                        PredicateType type;
                        if (types.TryGetValue(compound.Functor.Name, out type))
                        {
                            compound.Functor = type;
                        }
                        else
                        {
                            Functor functor = GetFunctor(compound.Functor.Name, provider, from);
                            if (functor != null)
                            {
                                compound.Functor = functor;
                            }
                        }
                    }

                    UpdateFunctor(compound, types, provider, from);
                }
            }
        }

        private static Module CreateSystemModule()
        {
            List<string> rules = new List<string>() 
            {
                "repeat",
                "repeat :- repeat",
                "append([], ?Ys, ?Ys)",
                "append([?X|?Xs], ?Ys, [?X|?Zs]) :- append(?Xs, ?Ys, ?Zs)",
                "member(?X, [?X|_]",
                "member(?X, [_|?Xs]) :- member(?X, ?Xs)",
                "length([], 0)",
                "length([_|?Xs], ?Y) :- length(?Xs, ?Z), ?Y is ?Z + 1",
                "reverse(?Xs, ?Ys) :- _reverse(?Xs, [], ?Ys)",
                "_reverse([], ?Ys, ?Ys",
                "_reverse([?X|?Xs], ?A, ?Ys) :- _reverse(?Xs, [?X|?A], ?Ys)",
                "AddToList(?X, [], [?X])",
                "AddToList(?X, [?X|?Ys], [?X|?Ys]) :- !",
                "AddToList(?X, [?Y|?Ys], [?X,?Y|?Ys]) :- ?X < ?Y, !",
                "AddToList(?X, [?Y|?Ys], [?Y|?Zs]) :- AddToList(?X, ?Ys, ?Zs)",
                "AddToMap(kv(?K,?V), [], [kv(?K,?V)])",
                "AddToMap(kv(?K,?V), [kv(?K,_)|?Ys], [kv(?K,?V)|?Ys]) :- !",
                "AddToMap(kv(?K,?V), [kv(?K1,V1)|?Ys], [kv(?K,?V),kv(?K1,V1)|?Ys]) :- ?K < ?K1, !",
                "AddToMap(kv(?K,?V), [kv(?K1,V1)|?Ys], [kv(?K1,V1)|?Zs]) :- AddToMap(kv(?K,?V), ?Ys, ?Zs)"
            };

            return Module.Parse("System", rules, null);
        }
    }
}
