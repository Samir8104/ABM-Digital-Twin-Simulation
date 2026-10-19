/*
This code is licensed under MIT license, Copyright (c) 2019 Kostas Cheliotis
https://github.com/cheliotk/unity_abm_framework/blob/master/LICENSE
*/

using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
namespace ABMU
{
    namespace Core
    {
        /// <summary>
        /// The Scheduler class manages the behaviour execution queue and handles the actual stepper execution calls.
        /// </summary>
        public class Scheduler
        {
            /// <summary>
            /// Dictionary structure for the scheduler queue,
            /// holds all steppers currently registered in the simulation
            /// that DO NOT run every tick
            /// Structure:
            /// key 1- StepperQOrder (EARLY, NORMAL, LATE)
            /// key 2-- StepperPriorityValue (0-1000)
            /// key 3--- step (0 = this frame, 1 = next frame, etc)
            /// key 4---- int index for List of Stepper objects for current frame
            /// val 5----- Stepper object
            /// </summary>
            Dictionary<Stepper.StepperQueueOrder, Dictionary<int, Dictionary<int, List<Stepper>>>> steppers;

            /// <summary>
            /// Dictionary structure for the scheduler queue for steppers running every tick,
            /// holds all steppers currently registered in the simulation that have a step = 1
            /// Structure:
            /// key 1- StepperQOrder (EARLY, NORMAL, LATE)
            /// key 2-- StepperPriorityValue (0-1000)
            /// key 3--- int index for List of Stepper objects for current frame
            /// val 4---- Stepper object
            /// </summary>
            public Dictionary<Stepper.StepperQueueOrder, Dictionary<int, List<Stepper>>> steppersEveryTick;

            /// <summary>
            /// List of steppers created during this frame
            /// </summary>
            List<Stepper> steppersCreatedThisFrame;

            /// <summary>
            /// List of steppers created during this frame that should also run this frame
            /// </summary>
            List<Stepper> steppersToRunThisFrame;

            /// <summary>
            /// List of steppers flagged to be destroyed during this queue
            /// </summary>
            List<Stepper> steppersDestroyedThisFrame;
            readonly HashSet<Stepper> destroyedSet = new();

            /// <summary>
            /// The current stepper priority value,
            /// loops through the entirety of existing priority values during the Tick() method
            /// and resets at the end of the frame.
            /// Is used to establish proper executino of steppers created this tick 
            /// </summary>
            int currentStepperP = -1;
            readonly List<KeyValuePair<int, List<Stepper>>> countdownScratch = new();
            readonly List<Stepper> dueScratch = new();
            readonly Stack<List<Stepper>> bucketPool = new();
            readonly List<Stepper.StepperQueueOrder> queueScratch = new();
            readonly List<int> priorityScratch = new();
            static readonly Unity.Profiling.ProfilerMarker TickMarker = new("Simulation.Scheduler");

            /// <summary>
            /// Constructor method for the Scheduler class
            /// </summary>
            public Scheduler(){
                Init();
            }
            
            /// <summary>
            /// Initializer method for this scheduler. Stepper Dict structure initialization
            /// </summary>
            void Init(){
                steppers = new Dictionary<Stepper.StepperQueueOrder, Dictionary<int, Dictionary<int, List<Stepper> >>>();
                steppersEveryTick = new Dictionary<Stepper.StepperQueueOrder, Dictionary<int, List<Stepper>>>();
                steppersCreatedThisFrame = new List<Stepper>();
                steppersDestroyedThisFrame = new List<Stepper>();
                steppersToRunThisFrame = new List<Stepper>();
            }

            /// <summary>
            /// Adds a stepper to the schedule
            /// </summary>
            /// <param name="s">The stepper to be added</param>
            public void RegisterStepper(Stepper s){
                steppersCreatedThisFrame.Add(s);
                if(s.priority > currentStepperP && s.startFrame == 0){
                    steppersToRunThisFrame.Add(s);
                }
            }
            
            /// <summary>
            /// Adds to the scheduler queue all steppers created this frame, but not yet registered
            /// </summary>
            void RegisterSteppersCreated(){
                foreach (Stepper s in steppersCreatedThisFrame)
                {
                    RegisterFutureStepper(s);
                }
            }

            /// <summary>
            /// Removes from the queue all steppers that have been destroyed this frame, but are still on the queue
            /// </summary>
            void DeregisterSteppersDestroyed(){
                foreach (Stepper s in steppersDestroyedThisFrame)
                {
                    DeregisterDestroyedStepper(s);
                }
            }

            /// <summary>
            /// Removes a given stepper from the scheduler queue
            /// </summary>
            /// <param name="s">The stepper to be removed</param>
            public void DeregisterStepper(Stepper s){
                if (s != null && destroyedSet.Add(s)) steppersDestroyedThisFrame.Add(s);
            }

            /// <summary>
            /// Removes from the queue a stepper that has been destroyed, but not yet de-registered.
            /// </summary>
            /// <param name="s">The stepper to be removed from the queue</param>
            void DeregisterDestroyedStepper(Stepper s){
                if (s == null) return;
                if (s.step == 1)
                {
                    if (steppersEveryTick.TryGetValue(s.stepperQueue, out var priorities) &&
                        priorities.TryGetValue(s.priority, out var list)) list.Remove(s);
                }
                else if (steppers.TryGetValue(s.stepperQueue, out var priorities) &&
                         priorities.TryGetValue(s.priority, out var countdowns))
                {
                    foreach (var list in countdowns.Values)
                        if (list.Remove(s)) break;
                }
            }

            /// <summary>
            /// Registers a stepper to be run at a future timestep.
            /// This is used to re-register steppers that have just executed this frame, as well as steppers created during the scheduler tick.
            /// </summary>
            /// <param name="s">The stepper to be registered</param>
            void RegisterFutureStepper(Stepper s){
                if(s.step == 1){
                    RegisterFutureStepperWithStep_1(s);
                }
                else{
                    RegisterFutureStepperWithStepNot_1(s);
                }
            }

            /// <summary>
            /// Registers a stepper to be run at all future timesteps (for steppers with step = 1)
            /// </summary>
            /// <param name="s">The stepper to be registered</param>
            void RegisterFutureStepperWithStep_1(Stepper s){
                Dictionary<int, List<Stepper>> steppersEveryTick_pval;
                if(steppersEveryTick.TryGetValue(s.stepperQueue, out steppersEveryTick_pval)){
                    if(steppersEveryTick_pval.ContainsKey(s.priority)){
                        steppersEveryTick_pval[s.priority].Add(s);
                    }
                    else{
                        steppersEveryTick_pval.Add(s.priority, new List<Stepper>());
                        steppersEveryTick_pval[s.priority].Add(s);
                    }
                }
                else{
                    steppersEveryTick.Add(s.stepperQueue, new Dictionary<int, List<Stepper>>());
                    steppersEveryTick[s.stepperQueue].Add(s.priority, new List<Stepper>());
                    steppersEveryTick[s.stepperQueue][s.priority].Add(s);
                }
            }

            /// <summary>
            /// Registers a stepper to be run at future intervals (for steppers with step > 1)
            /// </summary>
            /// <param name="s">The stepper to be registered</param>
            void RegisterFutureStepperWithStepNot_1(Stepper s){
                Dictionary<int, Dictionary<int, List<Stepper>>> steppers_pval;
                if(steppers.TryGetValue(s.stepperQueue, out steppers_pval)){
                    Dictionary<int, List<Stepper>> steppers_step;
                    if(steppers_pval.TryGetValue(s.priority, out steppers_step)){
                        if(steppers_step.ContainsKey(s.step-1)){
                            steppers_step[s.step-1].Add(s);
                        }
                        else{
                            steppers_step.Add(s.step-1, RentBucket());
                            steppers_step[s.step-1].Add(s);
                        }
                    }
                    else{
                        steppers_pval.Add(s.priority, new Dictionary<int, List<Stepper>>());
                        steppers_pval[s.priority].Add(s.step-1, RentBucket());
                        steppers_pval[s.priority][s.step-1].Add(s);
                    }
                }
                else{
                    steppers.Add(s.stepperQueue, new Dictionary<int, Dictionary<int, List<Stepper>>>());
                    steppers[s.stepperQueue].Add(s.priority, new Dictionary<int, List<Stepper>>());
                    steppers[s.stepperQueue][s.priority].Add(s.step-1, RentBucket());
                    steppers[s.stepperQueue][s.priority][s.step-1].Add(s);
                }
            }

            /// <summary>
            /// Advances the scheduler stepper queue by one tick, moving all currently registered steppers one tick forward, closer to execution
            /// </summary>
            List<Stepper> RentBucket() => bucketPool.Count > 0 ? bucketPool.Pop() : new List<Stepper>();

            void AdvanceSchedulerTick()
            {
                foreach (var priorities in steppers.Values)
                foreach (var countdowns in priorities.Values)
                {
                    countdownScratch.Clear();
                    dueScratch.Clear();
                    foreach (var entry in countdowns) countdownScratch.Add(entry);
                    countdowns.Clear();
                    foreach (var entry in countdownScratch)
                    {
                        if (entry.Key == 0 || entry.Value.Count == 0)
                        {
                            if (entry.Key == 0) dueScratch.AddRange(entry.Value);
                            entry.Value.Clear();
                            bucketPool.Push(entry.Value);
                        }
                        else countdowns.Add(entry.Key - 1, entry.Value);
                    }
                    foreach (var s in dueScratch) RegisterFutureStepper(s);
                }
            }

            /// <summary>
            /// Executes all steppers registered for execution at this update tick.
            /// Stepper execution is performed synchronously for priority-sensitive steppers by filtering by stepperQueue order and stepper priority value,
            /// but is a-synchronous for contemporaneous steppers.
            /// i.e. for steppers with equal priority, all steppers on one agent are executed first before moving to the next agent.
            /// </summary>
            public void Tick(){
                using var profile = TickMarker.Auto();
                RegisterSteppersCreated();
                DeregisterSteppersDestroyed();

                steppersCreatedThisFrame.Clear();
                steppersToRunThisFrame.Clear();
                steppersDestroyedThisFrame.Clear();
                destroyedSet.Clear();

                List<Stepper.StepperQueueOrder> stepperQValsUnique = GetListOfRegisteredStepperQValues();
                
                // Loop through all available stepper queue values in order (EARLY, NORMAL, LATE)
                foreach (var stepperQ in stepperQValsUnique)
                {
                    int prevStepperP = -1;

                    List<int> stepperPValsUnique = GetListOfRegisteredPriorityValuesAtQ(stepperQ);
                    
                    // Loop through all available stepper priority values in order for this stepper queue 
                    foreach (int stepperP in stepperPValsUnique)
                    {
                        currentStepperP = stepperP;
                        RunNewSteppers(stepperQ, prevStepperP, stepperP, false);

                        if(steppersEveryTick.ContainsKey(stepperQ))
                        {
                            if(steppersEveryTick[stepperQ].ContainsKey(stepperP)){
                                foreach (Stepper s in steppersEveryTick[stepperQ][stepperP])
                                {
                                    if(!destroyedSet.Contains(s))
                                        s.Step();
                                }
                            }
                        }

                        if(steppers.ContainsKey(stepperQ)){
                            if (steppers[stepperQ].ContainsKey(stepperP))
                            {
                                if(steppers[stepperQ][stepperP].ContainsKey(0)){

                                    foreach (Stepper s in steppers[stepperQ][stepperP][0])
                                    {
                                        if(!destroyedSet.Contains(s))
                                            s.Step();
                                    }
                                }
                            }
                        }

                        RunNewSteppers(stepperQ, stepperP, stepperP, true);

                        prevStepperP = stepperP;
                    }
                    
                }
                steppersToRunThisFrame.Sort((a,b) => a.priority.CompareTo(b.priority));
                for (int i = 0; i < steppersToRunThisFrame.Count; i++)
                {
                    var s = steppersToRunThisFrame[i];
                    if (!destroyedSet.Contains(s)) s.Step();
                }

                currentStepperP = -1;
                AdvanceSchedulerTick();
            }

            /// <summary>
            /// Get a list of all available stepper queue values currently in the simulation
            /// </summary>
            /// <returns>An ordered Stepper.StepperQueueOrder List of all available stepper queue values</returns>
            void RunNewSteppers(Stepper.StepperQueueOrder queue, int min, int max, bool exact)
            {
                for (int i = 0; i < steppersToRunThisFrame.Count;)
                {
                    var s = steppersToRunThisFrame[i];
                    bool match = s.stepperQueue == queue && (exact ? s.priority == max : s.priority > min && s.priority < max);
                    if (!match) { i++; continue; }
                    steppersToRunThisFrame.RemoveAt(i);
                    if (!destroyedSet.Contains(s)) s.Step();
                }
            }

            List<Stepper.StepperQueueOrder> GetListOfRegisteredStepperQValues()
            {
                queueScratch.Clear();
                foreach (var q in steppers.Keys) queueScratch.Add(q);
                foreach (var q in steppersEveryTick.Keys)
                    if (!queueScratch.Contains(q)) queueScratch.Add(q);
                queueScratch.Sort();
                return queueScratch;
            }

            List<int> GetListOfRegisteredPriorityValuesAtQ(Stepper.StepperQueueOrder queue)
            {
                priorityScratch.Clear();
                if (steppers.TryGetValue(queue, out var scheduled))
                    foreach (var p in scheduled.Keys) priorityScratch.Add(p);
                if (steppersEveryTick.TryGetValue(queue, out var everyTick))
                    foreach (var p in everyTick.Keys)
                        if (!priorityScratch.Contains(p)) priorityScratch.Add(p);
                priorityScratch.Sort();
                return priorityScratch;
            }
        }
    }
}
