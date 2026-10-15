using System;
using System.Collections.Generic;
using System.Linq;
using FSO.SimAntics.Engine;
using FSO.Files.Utils;
using FSO.SimAntics.Model;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;

namespace FSO.SimAntics.Primitives
{
    // Finds a suitable action and queues it onto this sim. Used for pet free will.

    // How autonomy works in The Sims:
    // 
    // Each interaction has "motive advertisements" which determine how many autonomy points are given for each interaction.
    //
    // First, each motive is converted into an "effective motive" using the interaction contribution curves.
    // These curves generally make motive changes more evident at lower values, and cap them at a specific value.
    // This prevents people from considering sleeping at 50% energy, but also allows other motives like fun to use a much higher cap.
    // "Happy" is calculated as an average of all "effective motive"s, including mood.
    // (In Hot Date and onwards this may be weighted by the Happy Weight curves)
    //
    // For each interaction, we only evaluate it if it has the following properties:
    // - non-zero motive range for any advertisement
    // - object is not "occupied"
    // - object has use count of 0 (this will be really slow in freeso so we will skip it for now, TODO)
    // - if any advertisements have a non-zero minimum, they are only considered if the motive is below that minimum (TODO: VERIFY)
    // Interaction check trees are evaluated to see if they are usable. "Auto" is passed to the tree as 1 in param 0, indicating this is an autonomous check.
    // When evaluating an interaction's check tree, it may change the motive ad range and minimum.
    // I don't know if anything changes from 0, but if it does then that would really suck for performance.
    //
    // For each interaction a new Happy score is calculated based on the motive deltas applied to the sim's current motives.
    // Motive delta is divided by 1000 for some reason. I don't really agree with this, but it matches numbers with TS 1.0. May be different in HD+.
    // Personality is applied by **some factor** based on your personality, if present. Likely 0-1 multiplier for personality, 0-2 for skill.
    // Remember to re-apply the interaction curves to the new motives here.Room for optimisation to only update the motives that changed.
    //
    // Attenuation is very simple: Score / (1 + (Attenuation* Distance)). It is applied to the *distance from base happy*, which results in the final score.
    // Individual motive scores can be negative to discourage use of interactions at high motives.These are combined with positives from other motives.
    //
    // Final scores below certain values are ignored:
    // 1E-07 for visitors and family, 1E-06 for sims who are sitting. These constants are in FCNS 2 in global.iff.
    // That means negative scores are ignored. Motive ads when you're in the flat part of the curve are ignored.
    // 

    public class VMFindBestAction : VMPrimitiveHandler
    {
        /// <summary>
        /// SimsRedux: how smart Sims are on their own (single player; online lots always use Smartest so
        /// every player's game - and the server - makes the same choices).
        ///   0 = the original Sims 1 brain, 1 = Smarter, 2 = Smartest (Sims 3/4-style)
        /// </summary>
        public static int ReduxLevel = 2;

        private static readonly VMMotive[] PhysicalMotives = { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene };

        public static VMPersonDataVariable[] VaryByTypes = new VMPersonDataVariable[]
        {
            VMPersonDataVariable.UnusedAndDoNotUse, //0
            VMPersonDataVariable.NicePersonality,
            VMPersonDataVariable.NicePersonality, //inverted
            VMPersonDataVariable.ActivePersonality,
            VMPersonDataVariable.ActivePersonality, //inverted
            VMPersonDataVariable.GenerousPersonality,
            VMPersonDataVariable.GenerousPersonality, //inverted
            VMPersonDataVariable.PlayfulPersonality,
            VMPersonDataVariable.PlayfulPersonality, //inverted
            VMPersonDataVariable.OutgoingPersonality,
            VMPersonDataVariable.OutgoingPersonality, //inverted
            VMPersonDataVariable.NeatPersonality,
            VMPersonDataVariable.NeatPersonality, //inverted


            VMPersonDataVariable.UnusedAndDoNotUse, // cleaning skill -- 13
            VMPersonDataVariable.CookingSkill, // cooking skill
            VMPersonDataVariable.CharismaSkill, // social skill *
            VMPersonDataVariable.MechanicalSkill, // repair skill *
            VMPersonDataVariable.UnusedAndDoNotUse, // gardening skill
            VMPersonDataVariable.UnusedAndDoNotUse, // music skill
            VMPersonDataVariable.CreativitySkill, // creativity skill
            VMPersonDataVariable.UnusedAndDoNotUse, // literacy skill
            VMPersonDataVariable.BodySkill, // physical skill *
            VMPersonDataVariable.LogicSkill // logic skill
        };

        public static VMMotive[] WeightMotives = new VMMotive[]
        {
            VMMotive.Energy,
            VMMotive.Comfort,
            VMMotive.Hunger,
            VMMotive.Hygiene,
            VMMotive.Bladder,
            VMMotive.Mood,
            VMMotive.Room,
            VMMotive.Social,
            VMMotive.Fun
        };

        public static int[] MotiveToWeight = Enumerable.Range(0, 16).Select(motive => Array.IndexOf(WeightMotives, (VMMotive)motive)).ToArray();

        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            //if we already have some action, do nothing
            var better = context.Caller.Thread.Queue.FirstOrDefault(x => x.Priority > (context.Caller as VMAvatar).GetPersonData(VMPersonDataVariable.Priority));
            if (better != null) {
                context.StackObject = better.Callee;
                return VMPrimitiveExitCode.GOTO_TRUE;
            }

            var ents = new List<VMEntity>(context.VM.Context.ObjectQueries.WithAutonomy);
            var processed = new HashSet<short>();
            var caller = (VMAvatar)context.Caller;
            var pos1 = caller.Position;

            var visitor = (caller.GetPersonData(VMPersonDataVariable.PersonType) == 1);
            var child = (caller.IsChild && context.VM.TS1);
            var attenTable = visitor ? TTAB.VisitorAttenuationValues : TTAB.AttenuationValues;
            var global = Content.Content.Get().WorldObjectGlobals;
            var interactionCurve = child ? global.InteractionScoreChild : global.InteractionScore;
            var happyCurve = child ? global.HappyWeightChild : global.HappyWeight;

            var isStray = caller.IsPet && context.VM.TS1 && caller.GetPersonData(VMPersonDataVariable.GreetStatus) == 0 && caller.GetPersonData(VMPersonDataVariable.PersonType) == 1;

            // === HAPPY CALCULATION ===

            var newStyle = false;
            // TODO: new style
            var weights = newStyle ? new float[9] : new float[9] { 1, 1, 1, 1, 1, 1, 1, 1, 1 }; //TODO: weights from curves for new
            var totalWeight = weights.Sum();
            for (int i = 0; i < 9; i++) weights[i] /= totalWeight;
            var minScore = (caller.GetPersonData(VMPersonDataVariable.Posture) > 0) ? 1e-6 : 1e-7;

            var motives = WeightMotives.Select(x => caller.GetMotiveData(x));
            var happyParts = motives
                .Zip(interactionCurve, (motive, curve) => curve.GetPoint(motive))
                .Zip(weights, (motive, weight) => motive * weight)
                .ToArray();

            float baseHappy = happyParts.Sum();

            // === SIMSREDUX: SMARTER BRAIN - what matters right now ===
            int level = context.VM.ReduxNoPause ? 2 : ReduxLevel;
            var brain = (level > 0 && context.VM.TS1 && !caller.IsPet) ? new ReduxBrain(context.VM, caller, level) : null;

            List<VMPieMenuInteraction> validActions = new List<VMPieMenuInteraction>();
            foreach (var iobj in ents)
            {
                if (iobj.Position == LotTilePos.OUT_OF_WORLD) continue;
                var obj = iobj.MultitileGroup.GetInteractionGroupLeader(iobj);
                if (processed.Contains(obj.ObjectID) || (obj is VMGameObject && ((VMGameObject)obj).Disabled > 0)) continue;
                processed.Add(obj.ObjectID);

                if (isStray)
                {
                    if (obj is VMGameObject) continue;
                    //determine if the object is indoors
                    var roomID = context.VM.Context.GetObjectRoom(obj);
                    roomID = (ushort)Math.Max(0, Math.Min(context.VM.Context.RoomInfo.Length - 1, roomID));
                    var room = context.VM.Context.RoomInfo[roomID];
                    var outside = room.Room.IsOutside;
                    if (!outside) continue;
                }
                var pos2 = obj.Position;
                var distance = (float)Math.Sqrt(Math.Pow(pos1.x - pos2.x, 2) + Math.Pow(pos1.y - pos2.y, 2) + Math.Pow((pos1.Level - pos2.Level) * 320, 2.0)) / 16.0f;
                var inUse = obj.GetFlag(VMEntityFlags.Occupied);

                if (obj.TreeTable == null) continue;
                foreach (var entry in obj.TreeTable.AutoInteractions)
                {
                    var id = entry.TTAIndex;
                    if (inUse && !obj.TreeTable.Interactions.Any(x => x.JoiningIndex == id)) continue;
                    var advertisements = entry.ActiveMotiveEntries; //TODO: cache this
                    if (advertisements.Length == 0) continue; //no ads on this object.

                    var action = obj.GetAction((int)id, caller, context.VM.Context, false);
                    TTAs ttas = obj.TreeTableStrings;

                    caller.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    var actionStrings = caller.Thread.CheckAction(action, true);

                    var pie = new List<VMPieMenuInteraction>();
                    if (actionStrings != null)
                    {
                        if (actionStrings.Count > 0)
                        {
                            foreach (var actionS in actionStrings)
                            {
                                if (actionS.Name == null) actionS.Name = ttas?.GetString((int)id) ?? "***MISSING***";
                                actionS.ID = (byte)id;
                                actionS.Entry = entry;
                                actionS.Global = false;
                                pie.Add(actionS);
                            }
                        }
                        else
                        {
                            if (ttas != null)
                            {
                                pie.Add(new VMPieMenuInteraction()
                                {
                                    Name = ttas.GetString((int)id),
                                    ID = (byte)id,
                                    Entry = entry,
                                    Global = false,
                                });
                            }
                        }
                    }
                    var first = pie.FirstOrDefault();
                    if (first != null)
                    {
                        // calculate score for this tree.
                        // start with the base happy value, and modify it for each motive changed.
                        float score = baseHappy;
                        for (int i = 0; i < advertisements.Length; i++)
                        {
                            var motiveI = advertisements[i].MotiveIndex;
                            var motiveScore = entry.MotiveEntries[motiveI];
                            short min = motiveScore.EffectRangeMinimum;
                            short max = motiveScore.EffectRangeDelta;
                            short personality = (short)motiveScore.PersonalityModifier;
                            if (first.MotiveAdChanges != null)
                            {
                                first.MotiveAdChanges.TryGetValue((0 << 16) | motiveI, out min);
                                first.MotiveAdChanges.TryGetValue((1 << 16) | motiveI, out max);
                                first.MotiveAdChanges.TryGetValue((2 << 16) | motiveI, out personality);
                            }

                            if (max == 0 && min > 0)
                            {
                                //invalid delta. do from 0..delta instead (fix child-talk preference?)
                                max = min;
                                min = 0;
                            }
                            max += min; //it's a delta, add min to it

                            var myMotive = caller.GetMotiveData((VMMotive)motiveI);
                            if (min != 0 && myMotive > min) continue;

                            // subtract the base contribution for this motive from happy
                            var weightInd = MotiveToWeight[motiveI];
                            if (weightInd == -1) continue;
                            score -= happyParts[weightInd];

                            float personalityMul = 1;
                            if (personality > 0 && personality < VaryByTypes.Length)
                            {
                                personalityMul = caller.GetPersonData(VaryByTypes[personality]);
                                personalityMul /= 1000f;
                                if (personality < 13)
                                {
                                    if ((personality & 1) == 0)
                                    {
                                        personalityMul = 1 - personalityMul;
                                    }
                                    if (brain != null) personalityMul = brain.Personality(personalityMul, (VMMotive)motiveI);
                                } else
                                {
                                    personalityMul *= 2;
                                }
                            }

                            // then add the new contribution for this motive.
                            score += interactionCurve[weightInd].GetPoint(myMotive + (max * personalityMul) / 1000f) * weights[weightInd];
                        }

                        // score relative to base
                        score -= baseHappy;
                        // modify score using attenuation
                        float atten = (entry.AttenuationCode == 0 || entry.AttenuationCode >= attenTable.Length) ?
                            entry.AttenuationValue : attenTable[entry.AttenuationCode];

                        score = score / (1 + atten * distance);

                        // SimsRedux: the smarter brain weighs it (urgent needs first, time of day, work, nearby)
                        if (brain != null && score > 0) score *= brain.Weigh(entry, first, advertisements, distance);

                        if (score > minScore)
                        {
                            foreach (var item in pie)
                            {
                                item.Score = score;
                                item.Callee = obj;
                                validActions.Add(first);
                            }
                        }
                    }
                    //if (attenScore != 0) attenScore += (int)context.VM.Context.NextRandom(31) - 15;
                    
                    //TODO: Lockout interactions that have been used before for a few sim hours (in ts1 ticks. same # of ticks for tso probably)
                    //TODO: special logic for socials?
                }
            }
            List<VMPieMenuInteraction> sorted = validActions.OrderByDescending(x => x.Score).ToList();
            sorted = TakeTopActions(sorted, 4);
            var selection = sorted.FirstOrDefault();
            if (selection == null) return VMPrimitiveExitCode.GOTO_FALSE;
            // SimsRedux: no silly picks - only choices close to the best one are in the draw, and when a
            // need is urgent the Sim simply does the best thing for it
            if (brain != null)
            {
                var best = selection.Score;
                sorted = sorted.Where(x => x.Score >= best * (level >= 2 ? 0.5f : 0.3f)).ToList();
            }
            if (!selection.Entry.AutoFirst && !(brain != null && brain.Urgent))
            {
                // weighted random selection
                //var slice = sorted.Take(Math.Min(4, sorted.Count)).ToList();
                var totalScore = sorted.Sum(x => x.Score);
                var random = context.VM.Context.NextRandom(10000);

                float randomTotal = 0;
                for (int i=0; i < sorted.Count; i++)
                {
                    var action = sorted[i];
                    randomTotal += (sorted[i].Score / totalScore) * 10000;
                    if (random <= randomTotal)
                    {
                        selection = action;
                        break;
                    }
                }
            }

            var qaction = selection.Callee.GetAction(selection.ID, context.Caller, context.VM.Context, false, new short[] { selection.Param0, 0, 0, 0 });
            if (qaction != null)
            {
                qaction.Priority = (short)VMQueuePriority.Autonomous;
                context.Caller.Thread.EnqueueAction(qaction);
                context.StackObject = selection.Callee;
                return VMPrimitiveExitCode.GOTO_TRUE;
            } else
            {
                return VMPrimitiveExitCode.GOTO_FALSE;
            }
            
        }

        private List<VMPieMenuInteraction> TakeTopActions(List<VMPieMenuInteraction> list, int count)
        {
            var result = new List<VMPieMenuInteraction>();
            foreach (var action in list)
            {
                var users = action.Callee.GetValue(VMStackObjectVariable.UseCount);
                if (users == 0 || action.Callee.TreeTable.Interactions.Any(x => x.JoiningIndex == action.ID))
                {
                    result.Add(action);
                    if (result.Count >= count) break;
                }
            }
            return result;
        }
    }

    /// <summary>
    /// SimsRedux: the smarter brain (roadmap: Autonomy improvements). It doesn't replace Sims 1's own
    /// scoring - it weighs each choice the way a Sims 3/4 Sim would:
    ///  - needs first: a need near red (hunger, bladder, energy, hygiene) pushes everything else aside
    ///  - deal with needs before they go red
    ///  - sleep at night, don't nap in the middle of the day, wind down late at night
    ///  - before the carpool: eat, use the bathroom, wash - don't start sleeping or long fun
    ///  - prefer the closer object for the same job
    ///  - personality matters more; everyone tidies up a bit, not only Neat Sims
    /// Everything here comes from the lot's own state (needs, clock, job, personality), so every
    /// player's game and the server make exactly the same choice.
    /// </summary>
    public class ReduxBrain
    {
        private readonly VMAvatar Sim;
        private readonly int Level;
        private readonly int Hour;
        private readonly short[] Needs = new short[16];
        /// <summary>A physical need is close to red: the Sim does the best thing for it, no dawdling.</summary>
        public bool Urgent;
        private readonly bool Night, LateNight, Daytime, BeforeWork;

        public ReduxBrain(VM vm, VMAvatar sim, int level)
        {
            Sim = sim;
            Level = level;
            Hour = vm.Context.Clock.Hours;
            for (int i = 0; i < 16; i++) Needs[i] = sim.GetMotiveData((VMMotive)i);
            foreach (var m in new[] { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene })
                if (Needs[(int)m] < -40) Urgent = true;
            Night = Hour >= 22 || Hour < 6;
            LateNight = Hour >= 23 || Hour < 5;
            Daytime = Hour >= 8 && Hour < 20;

            // a job (and the hour the carpool comes): get ready in the 2 hours before
            try
            {
                var job = sim.GetPersonData(VMPersonDataVariable.JobType);
                if (job > 0 && !sim.IsChild)
                {
                    var lvl = Content.Content.Get().Jobs?.GetJobLevel(job, sim.GetPersonData(VMPersonDataVariable.JobPromotionLevel));
                    if (lvl != null)
                    {
                        var start = lvl.StartTime % 24;
                        var until = (start - Hour + 24) % 24;
                        BeforeWork = until >= 1 && until <= 2;
                    }
                }
            }
            catch { }
        }

        /// <summary>Personality counts for more (Sims 3/4 traits); cleaning is something everyone does a bit.</summary>
        public float Personality(float mul, VMMotive motive)
        {
            if (Level < 2) return mul;
            mul = 0.25f + mul * 1.5f; // stronger both ways
            if (motive == VMMotive.Room) mul = Math.Max(mul, 0.6f); // everyone tidies up, not only Neat Sims
            return mul;
        }

        private bool Ads(TTABInteraction entry, VMPieMenuInteraction first, VMMotive m)
        {
            if (entry.MotiveEntries == null || (int)m >= entry.MotiveEntries.Length) return false;
            var e = entry.MotiveEntries[(int)m];
            short min = e.EffectRangeMinimum, max = e.EffectRangeDelta, v;
            if (first.MotiveAdChanges != null)
            {
                if (first.MotiveAdChanges.TryGetValue((0 << 16) | (int)m, out v)) min = v;
                if (first.MotiveAdChanges.TryGetValue((1 << 16) | (int)m, out v)) max = v;
            }
            return (max + min) > 0;
        }

        /// <summary>How much more (or less) appealing this choice is right now.</summary>
        public float Weigh(TTABInteraction entry, VMPieMenuInteraction first, TTABMotiveEntry[] ads, float distance)
        {
            float w = 1;
            bool helpsPhysical = false, onlyFunSocial = true;
            foreach (var m in new[] { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene, VMMotive.Comfort, VMMotive.Room })
                if (Ads(entry, first, m)) onlyFunSocial = false;

            // 1. needs first: the lower a need, the more what fixes it matters
            foreach (var m in new[] { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene })
            {
                if (!Ads(entry, first, m)) continue;
                var v = Needs[(int)m];
                if (m == VMMotive.Energy && Daytime && v > -50) continue; // (sleep is handled with the time of day below)
                helpsPhysical = true;
                if (v < -70) w *= 8;
                else if (v < -40) w *= 4;
                else if (v < 0) w *= 1.6f;          // deal with it before it goes red
                else if (v < 30 && Level >= 2) w *= 1.15f;
            }
            if (Urgent && !helpsPhysical) w *= 0.2f;  // the bladder is bursting - the TV can wait

            // 2. the time of day (Smartest)
            if (Level >= 2)
            {
                bool sleepy = Ads(entry, first, VMMotive.Energy);
                var energy = Needs[(int)VMMotive.Energy];
                if (sleepy)
                {
                    if (Night && energy < 60) w *= energy < 0 ? 3 : 1.8f;          // bedtime
                    else if (Daytime && energy > -50) w *= 0.3f;                    // no naps unless exhausted
                }
                if (LateNight && onlyFunSocial && energy < 40) w *= 0.5f;           // wind down
                if (BeforeWork)
                {
                    if (Ads(entry, first, VMMotive.Hunger) || Ads(entry, first, VMMotive.Hygiene) || Ads(entry, first, VMMotive.Bladder)) w *= 2;
                    if (sleepy && energy > -60) w *= 0.2f;                          // don't fall asleep before the carpool
                    if (onlyFunSocial) w *= 0.6f;
                }
            }

            // 3. the closer object for the same job
            if (Level >= 2) w /= (1 + 0.02f * distance);
            return w;
        }
    }

    public class VMFindBestActionOperand : VMPrimitiveOperand
    {
        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN))
            {
            }
        }

        public void Write(byte[] bytes) { }
        #endregion
    }
}
