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
        ///   0 = the original Sims 1 brain, 1 = Smarter, 2 = Smartest (Sims 3/4-style),
        ///   3 = Adaptive (17.4: Smartest plus each Sim's own learned brain - Utils/ReduxBrain.cs; the default; online-style
        ///   lots always use it), -1 = Off (the family does nothing on their own)
        /// </summary>
        public static int ReduxLevel = 3;

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

            // SimsRedux: the player is always in charge. While anything the player ordered is still
            // waiting in this Sim's queue (even behind the action that's running now), the Sim never
            // adds a choice of their own - their own ideas only come once the player's list is empty.
            if (context.VM.TS1 && context.Caller is VMAvatar && !((VMAvatar)context.Caller).IsPet)
            {
                var th = context.Caller.Thread;
                for (int i = th.ActiveQueueBlock + 1; i < th.Queue.Count; i++)
                    if (th.Queue[i].Priority >= (short)VMQueuePriority.UserDriven && th.Queue[i].Mode == VMQueueMode.Normal)
                        return VMPrimitiveExitCode.GOTO_FALSE;
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
            // (17.4: an online-style lot - a visit - is always Adaptive: each Sim's brain is on the lot itself, sent by its owner)
            int level = context.VM.ReduxNoPause ? 3 : ReduxLevel;
            // (a Sim of the family living here - not a visitor, NPC or pet)
            // (a guest player's Sim carries the lot family's number but isn't the household - IsHouseholdSim)
            bool familySim = context.VM.TS1 && !caller.IsPet && !visitor && FSO.SimAntics.Utils.ReduxComputer.IsHouseholdSim(context.VM, caller);
            // Off (single player): the player's own family does nothing on their own; visitors still do
            if (level < 0 && familySim)
                return VMPrimitiveExitCode.GOTO_FALSE;
            var brain = (level > 0 && context.VM.TS1 && !caller.IsPet) ? new ReduxBrain(context.VM, caller, level) : null;

            // SimsRedux 17.4: free will per Sim (attribute 120, VMReduxBrain) - how OFTEN a Sim acts on their own; Sim Smarts
            // decides how WELL. For the household's Sims and a visiting player's own Sim (EffectiveFreeWill - only the Sim's own
            // game sets it, with command 86, so it's the same on every game). Off: only for a need in the red, plus the
            // essentials (a fire, paying the bills, calling the repairman - FindChore). Needs only: what a need asks for, plus
            // the same essentials.
            var freeWill = FSO.SimAntics.Utils.VMReduxBrain.EffectiveFreeWill(context.VM, caller);
            bool fwOff = freeWill == FSO.SimAntics.Utils.VMReduxBrain.FreeWill.Off;
            bool fwRestrict = fwOff || freeWill == FSO.SimAntics.Utils.VMReduxBrain.FreeWill.NeedsOnly;
            bool fwEssentialsOnly = fwOff && !FSO.SimAntics.Utils.VMReduxBrain.InRed(caller); // (no choices of their own - only FindChore's essentials)
            if (fwEssentialsOnly && !(brain != null && familySim && level >= 2 && !visitor))
                return VMPrimitiveExitCode.GOTO_FALSE;
            // SimsRedux 17.4: Adaptive - the Sim's own learned brain weighs every choice too (whole numbers, from the lot only)
            bool adaptive = level >= 3 && context.VM.TS1 && !caller.IsPet;
            // (a context for the brain's factors only when there are any - VMReduxBrain.Weight makes none otherwise)
            var brainCtx = (adaptive && FSO.SimAntics.Utils.VMReduxBrain.FactorCount > 0) ? new FSO.SimAntics.Utils.VMReduxBrainContext() : null;

            // SimsRedux: something to do when nothing is *needed*. Sims 1 only scores a choice by how much it
            // raises a need, so with every need in the flat top part of its curve (most of the day, with the
            // slower motives built in) nothing scores at all and the Sim just stands there. These are the
            // fun / social / tidying / skill choices to fall back on then (family Sims only).
            List<VMPieMenuInteraction> idleChoices = (brain != null && familySim && !fwRestrict) ? new List<VMPieMenuInteraction>() : null;

            List<VMPieMenuInteraction> validActions = new List<VMPieMenuInteraction>();
            foreach (var iobj in ents)
            {
                if (fwEssentialsOnly) break; // (17.4 free will Off: nothing scored - only the essentials below)
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
                double ddx = pos1.x - pos2.x, ddy = pos1.y - pos2.y, ddz = (pos1.Level - pos2.Level) * 320;
                var distance = (float)Math.Sqrt(ddx * ddx + ddy * ddy + ddz * ddz) / 16.0f;
                var inUse = obj.GetFlag(VMEntityFlags.Occupied);

                if (obj.TreeTable == null) continue;
                foreach (var entry in obj.TreeTable.AutoInteractions)
                {
                    var id = entry.TTAIndex;
                    if (inUse && !HasJoining(obj.TreeTable, id)) continue;
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
                    if (first != null && fwRestrict && !FSO.SimAntics.Utils.VMReduxBrain.Allowed(freeWill, caller, entry, first)) first = null; // (17.4 free will)
                    if (first != null)
                    {
                        // calculate score for this tree.
                        // start with the base happy value, and modify it for each motive changed.
                        float score = baseHappy;
                        float idleAppeal = 0; bool needsAd = false; // SimsRedux: for idleChoices (see above)
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

                            // SimsRedux: how much this offers someone with nothing they need (raw ad strength).
                            // Anything for a physical need (eating, sleeping, the bathroom, washing) is left
                            // to the normal scoring - no eating when full, no naps when rested.
                            if (idleChoices != null && max > 0)
                            {
                                var mo = (VMMotive)motiveI;
                                bool skillAd = personality >= 13 && personality < VaryByTypes.Length;
                                float mul = skillAd ? Math.Max(personalityMul, 0.5f) : personalityMul; // (skill-building even at skill 0)
                                if (mo == VMMotive.Fun || mo == VMMotive.Social || mo == VMMotive.Room || mo == VMMotive.Mood) idleAppeal += max * mul;
                                else if (mo == VMMotive.Comfort) idleAppeal += max * mul * 0.3f;
                                else needsAd = true;
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
                        if (brain != null && score > 0) score *= brain.Weigh(entry, first, advertisements, distance, obj, (int)id);
                        // SimsRedux 15.7: their own bed (or side) first; someone else's only when there's nowhere else
                        if (context.VM.TS1 && score > 0) score *= FSO.SimAntics.Utils.ReduxBeds.AutonomyWeight(context.VM, caller, obj, (int)id);
                        // (17.4 perf: the trait and brain weights are only worked out when they can matter - a choice that
                        //  scores, or one kept as something to do when nothing is needed)
                        bool idleCandidate = idleChoices != null && !needsAd && idleAppeal > 0;
                        bool weighs = score > 0 || idleCandidate;
                        // SimsRedux 16.9: traits (Neat Freaks clean, Bookworms read, Loners keep to themselves...)
                        // (17.4: by the entry's English name - never first.Name, which is in the player's language)
                        float traitWeight = (context.VM.TS1 && weighs) ? FSO.SimAntics.Utils.VMReduxTraits.AutonomyWeight(context.VM, caller, obj, entry) : 1f;
                        if (score > 0) score *= traitWeight;
                        // SimsRedux 17.4: Adaptive - the Sim's brain (per mille, 1000 = no change; language-independent)
                        int brainWeight = (adaptive && weighs) ? FSO.SimAntics.Utils.VMReduxBrain.Weight(context.VM, caller, obj, entry, first, distance, level, brainCtx) : 1000;
                        if (score > 0 && brainWeight != 1000) score *= brainWeight / 1000f;

                        if (score > minScore)
                        {
                            foreach (var item in pie)
                            {
                                item.Score = score;
                                item.Callee = obj;
                                validActions.Add(first);
                            }
                        }
                        else if (idleCandidate)
                        {
                            // SimsRedux: not needed right now - keep it as something to do if nothing else is
                            // (same manners as everything else: not near a fire, not next to sleepers...)
                            float idle = idleAppeal / 1000f / (1 + 0.1f * distance);
                            idle *= brain.Weigh(entry, first, advertisements, distance, obj, (int)id);
                            idle *= traitWeight;
                            if (brainWeight != 1000) idle *= brainWeight / 1000f;
                            if (idle > minScore)
                            {
                                first.Score = idle;
                                first.Callee = obj;
                                idleChoices.Add(first);
                            }
                        }
                    }
                    //if (attenScore != 0) attenScore += (int)context.VM.Context.NextRandom(31) - 15;
                    
                    //TODO: Lockout interactions that have been used before for a few sim hours (in ts1 ticks. same # of ticks for tso probably)
                    //TODO: special logic for socials?
                }
            }
            // SimsRedux: food that's already made comes first - a hungry Sim finishes their plate, takes a
            // serving from the group meal or has the leftovers, instead of cooking a whole new meal
            if (context.VM.TS1) ReduxPreferReadyFood(validActions);
            List<VMPieMenuInteraction> sorted = validActions.OrderByDescending(x => x.Score).ToList();
            sorted = TakeTopActions(sorted, 4);
            var selection = sorted.FirstOrDefault();
            bool fromIdle = false; // (17.4: for "Why I'm doing this")
            // SimsRedux: nothing is needed - do something anyway (fun, a chat, tidying up, a skill)
            if (selection == null && idleChoices != null && idleChoices.Count > 0)
            {
                fromIdle = true;
                sorted = TakeTopActions(idleChoices.OrderByDescending(x => x.Score).ToList(), 4);
                selection = sorted.FirstOrDefault();
            }
            // SimsRedux: no silly picks - only choices close to the best one are in the draw, and when a
            // need is urgent the Sim simply does the best thing for it
            if (brain != null && selection != null)
            {
                var best = selection.Score;
                sorted = sorted.Where(x => x.Score >= best * (level >= 2 ? 0.5f : 0.3f)).ToList();
            }
            // SimsRedux stage 2: chores when the Sim is doing fine - pay the bills, repair what's broken
            // (with the Mechanical skill for it), the Sims 4 way. (Looked at even when nothing else scored:
            // a Sim with every need fine is exactly the one who should be paying the bills or calling
            // the fire department.)
            bool chore = false, isBed = false;
            // SimsRedux: bedtime - a tired family Sim at night (or anyone exhausted) goes to bed, instead of
            // the bed losing out to one more round of fun
            if (brain != null && level >= 1 && familySim)
            {
                var bed = (fwOff && caller.GetMotiveData(VMMotive.Energy) >= FSO.SimAntics.Utils.VMReduxBrain.RedBelow) ? null : brain.FindBed(context, ents);
                if (bed != null) { selection = bed; chore = true; isBed = true; }
            }
            if (!chore && brain != null && level >= 2 && !visitor)
            {
                var c = brain.FindChore(context, ents, fwRestrict); // (17.4 free will Off / Needs only: only the essentials - a fire, the bills, the repairman)
                if (c != null) { selection = c; chore = true; }
            }
            if (selection == null) return VMPrimitiveExitCode.GOTO_FALSE;
            if (!chore && !selection.Entry.AutoFirst && !(brain != null && brain.Urgent))
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
                brain?.Remember(selection);
                if (brain != null && brain.ChoreCode != 0) brain.MarkCall(brain.ChoreCode);
                // SimsRedux 17.4: "Why I'm doing this" (display only - nothing on the lot reads it)
                if (context.VM.TS1 && !caller.IsPet)
                {
                    try
                    {
                        string note = null;
                        if (adaptive && !chore && FSO.SimAntics.Utils.VMReduxBrain.FactorCount > 0)
                        {
                            var wctx = brainCtx ?? new FSO.SimAntics.Utils.VMReduxBrainContext();
                            FSO.SimAntics.Utils.VMReduxBrain.Weight(context.VM, caller, selection.Callee, selection.Entry, selection, 0, level, wctx);
                            note = wctx.Why;
                        }
                        FSO.SimAntics.Utils.VMReduxBrain.SetWhy(caller, selection.Callee, selection.ID,
                            FSO.SimAntics.Utils.VMReduxBrain.Explain(context.VM, caller, selection, brain?.ChoreCode ?? 0, isBed, chore, fromIdle, note));
                    }
                    catch { }
                }
                context.StackObject = selection.Callee;
                return VMPrimitiveExitCode.GOTO_TRUE;
            } else
            {
                return VMPrimitiveExitCode.GOTO_FALSE;
            }
            
        }

        /// <summary>
        /// SimsRedux: game minutes a family Sim stands idle (nothing at all in their queue) before they pick
        /// something to do themselves. The Sim the player is controlling waits a little longer, so the player
        /// has time to give the next order. Online every Sim waits the same (no "selected Sim" there).
        /// </summary>
        public const int IdleMinutes = 2, IdleMinutesSelected = 5, IdleMinutesOnline = 4;

        private static readonly VMFindBestAction IdleFinder = new VMFindBestAction();

        /// <summary>
        /// SimsRedux: the idle safety net, called from VMAvatar.Tick. However often (or rarely) the Sims 1
        /// person code gets round to free will (rarely, or never for the Sim you control) - a family Sim with an
        /// empty queue for a few game minutes looks for something to do, exactly as if the person code had
        /// asked (same scoring, same Autonomous priority, so any order from the player interrupts it).
        /// Only lot state is used (queue, needs, game clock, BrainMemory[16..17]), so it runs the same on
        /// the server and every player's game. Visitors, NPCs and pets are left to the original game, and
        /// the Off setting turns it off.
        /// </summary>
        public static void ReduxIdleCheck(VMAvatar sim)
        {
            var thread = sim?.Thread;
            var vm = thread?.Context?.VM;
            if (vm == null || !vm.TS1 || sim.Dead || sim.IsPet) return;
            var mem = sim.BrainMemory;
            if (mem == null || mem.Length < 18) return;
            int level = vm.ReduxNoPause ? 3 : ReduxLevel;
            var family = vm.TS1State?.CurrentFamily;

            bool idle = level >= 0 && family != null
                && sim.GetPersonData(VMPersonDataVariable.PersonType) != 1                        // not a visitor/NPC
                && FSO.SimAntics.Utils.ReduxComputer.IsHouseholdSim(vm, sim)                                          // the family living here (not a guest player's Sim)
                && sim.GetPersonData(VMPersonDataVariable.IsGhost) == 0
                && thread.Queue.Count == 0                                                        // nothing queued at all
                && thread.BlockingState == null && thread.Stack.Count > 0 && !thread.Stack[thread.Stack.Count - 1].ActionTree
                && sim.Position != LotTilePos.OUT_OF_WORLD && sim.Container == null              // here (not at work, not carried)
                && sim.GetMotiveData(VMMotive.SleepState) == 0
                && vm.GlobalBlockingDialog == null;
            if (!idle) { mem[16] = 0; mem[17] = 0; return; }

            var clock = vm.Context.Clock;
            int now = clock.DayOfMonth * 1440 + clock.Hours * 60 + clock.Minutes;
            if (mem[17] == 0) { mem[16] = now; mem[17] = 1; return; } // just became idle: start counting
            var ago = now - mem[16]; if (ago < 0) ago += 30 * 1440;
            int wait = vm.ReduxNoPause ? IdleMinutesOnline : ((vm.GetGlobalValue(3) == sim.ObjectID) ? IdleMinutesSelected : IdleMinutes);
            wait = FSO.SimAntics.Utils.VMReduxBrain.IdleWait(sim, wait); // (17.4: free will High, and the play style the brain learned)
            if (ago < wait) return;

            mem[16] = now; // (if nothing comes of it, try again a few minutes later)
            var frame = new VMStackFrame
            {
                Thread = thread,
                Caller = sim,
                Callee = sim,
                CodeOwner = sim.Object,
                Args = new short[4],
                Locals = new short[0]
            };
            // the interactions' check trees share this Sim's temp registers; the person code didn't ask for
            // this search, so put its temps back exactly as they were afterwards
            var temps = (short[])thread.TempRegisters.Clone();
            var tempXL = (int[])thread.TempXL.Clone();
            var hide = sim.ObjectData[(int)VMStackObjectVariable.HideInteraction];
            try
            {
                IdleFinder.Execute(frame, null);
            }
            catch (Exception)
            {
                // a broken object's check tree must never take the whole lot down (same on every machine)
            }
            finally
            {
                Array.Copy(temps, thread.TempRegisters, Math.Min(temps.Length, thread.TempRegisters.Length));
                Array.Copy(tempXL, thread.TempXL, Math.Min(tempXL.Length, thread.TempXL.Length));
                sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = hide;
            }
        }

        /// <summary>SimsRedux perf: Interactions.Any(x => x.JoiningIndex == id) without a closure per check.</summary>
        private static bool HasJoining(TTAB table, uint id)
        {
            var ias = table.Interactions;
            for (int i = 0; i < ias.Length; i++) if (ias[i].JoiningIndex == id) return true;
            return false;
        }

        private static readonly string[] ReduxCookNames = { "have a meal", "serve meal", "have lite meal" };

        /// <summary>
        /// (17.4 lockstep: by each choice's ENGLISH name - VMReduxBrain.EnglishName, the object's own strings read as English US -
        /// never a.Name, which is in the player's language; no English name = not recognised, the same on every game.)
        /// </summary>
        private static string ReduxEnglish(VMPieMenuInteraction a)
        {
            return FSO.SimAntics.Utils.VMReduxBrain.EnglishName(a.Callee, a.Entry) ?? "";
        }

        private static void ReduxPreferReadyFood(List<VMPieMenuInteraction> actions)
        {
            try
            {
                bool ready = false;
                foreach (var a in actions)
                {
                    var name = ReduxEnglish(a);
                    if (name.StartsWith("have leftovers", StringComparison.OrdinalIgnoreCase)) { ready = true; break; }
                    var label = a.Callee?.Object?.OBJ?.ChunkLabel ?? "";
                    if (label.StartsWith("Food -", StringComparison.OrdinalIgnoreCase)
                        && (name.Equals("eat", StringComparison.OrdinalIgnoreCase) || name.Equals("serve", StringComparison.OrdinalIgnoreCase)
                            || name.StartsWith("grab a slice", StringComparison.OrdinalIgnoreCase))) { ready = true; break; }
                }
                if (!ready) return;
                actions.RemoveAll(IsCookChoice);
            }
            catch (Exception) { }
        }

        private static bool IsCookChoice(VMPieMenuInteraction a)
        {
            var n = ReduxEnglish(a);
            foreach (var c in ReduxCookNames) if (n.StartsWith(c, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
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
        /// <summary>17.4: a fire on the lot (free will Off still reacts to it).</summary>
        public bool FireOnLot { get { return Fires.Count > 0; } }
        private readonly bool Night, LateNight, Daytime, BeforeWork;

        // (shared, so scoring - which runs for every choice of every Sim - doesn't allocate them each time)
        private static readonly VMMotive[] PhysicalNeeds = { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene };
        private static readonly VMMotive[] NonFunNeeds = { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene, VMMotive.Comfort, VMMotive.Room };

        public ReduxBrain(VM vm, VMAvatar sim, int level)
        {
            Sim = sim;
            Level = level;
            Hour = vm.Context.Clock.Hours;
            for (int i = 0; i < 16; i++) Needs[i] = sim.GetMotiveData((VMMotive)i);
            foreach (var m in PhysicalNeeds)
                if (Needs[(int)m] < -40) Urgent = true;
            Now = vm.Context.Clock.DayOfMonth * 1440 + Hour * 60 + vm.Context.Clock.Minutes;
            NeedsFine = Needs[(int)VMMotive.Hunger] > 0 && Needs[(int)VMMotive.Bladder] > 0 && Needs[(int)VMMotive.Energy] > -20 && Needs[(int)VMMotive.Hygiene] > -20;
            // the last thing they chose: still working on it?
            if (Level >= 2 && sim.BrainMemory != null && sim.BrainMemory.Length >= 12 && sim.BrainMemory[1] != 0)
            {
                LastAgo = Now - sim.BrainMemory[1];
                if (LastAgo < 0) LastAgo += 30 * 1440; // (the month wrapped)
                LastObject = sim.BrainMemory[0] >> 16;
                var ln = sim.BrainMemory[2];
                if (ln >= 1 && ln <= 16) LastNeed = (VMMotive)(ln - 1); // (100+ are phone calls, not needs)
            }
            // stage 3: fires on the lot, Sims who are asleep (be quiet near them), doors (don't hang about in them),
            // and whose plate is whose (a Sim eating from a plate claims it for a while, even if they get up)
            // (one pass over the lot - it was two, each with several name searches per object, on every decision;
            //  what kind of object it is now comes from a cache by object type: KindOf)
            var ents = vm.Entities;
            for (int ei = 0; ei < ents.Count; ei++)
            {
                var e = ents[ei];
                if (e is VMGameObject)
                {
                    if (e.Position == LotTilePos.OUT_OF_WORLD) continue;
                    var kind = KindOf(e);
                    if ((kind & ObjKind.Door) != 0) Doors.Add(e.Position);
                    if ((kind & ObjKind.Fire) != 0) Fires.Add(e.Position);
                }
                else if (e is VMAvatar a)
                {
                    if (a.BrainMemory != null && a.BrainMemory.Length >= 14 && a.Thread?.Queue != null && a.Thread.Queue.Count > 0)
                    {
                        var q = a.Thread.Queue[0];
                        if (q.Callee != null && q.Callee != a && IsPlate(q.Callee) && (q.Name ?? "").IndexOf("Eat", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            a.BrainMemory[12] = q.Callee.ObjectID;
                            a.BrainMemory[13] = Now;
                        }
                    }
                    if (a != sim && a.Position != LotTilePos.OUT_OF_WORLD && a.GetMotiveData(VMMotive.SleepState) != 0) Sleepers.Add(a);
                }
            }
            Night = Hour >= 22 || Hour < 6;
            LateNight = Hour >= 23 || Hour < 5;
            Daytime = Hour >= 8 && Hour < 20;

            // a job (and the hour the carpool comes): get ready in the 2 hours before
            try
            {
                var job = sim.GetPersonData(VMPersonDataVariable.JobType);
                // (not on a day off - no car pool comes: Utils/ReduxWeek.cs)
                if (job > 0 && !sim.IsChild && (!vm.TS1 || FSO.SimAntics.Utils.ReduxWeek.Works(job, FSO.SimAntics.Utils.ReduxWeek.Day(vm.Context.Clock))))
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

        // ---------- stage 2: memory ----------

        private readonly int Now; // game minutes (day of month * 1440 + time of day)
        private static readonly VMMotive[] MemoryNeeds = { VMMotive.Hunger, VMMotive.Bladder, VMMotive.Energy, VMMotive.Hygiene, VMMotive.Comfort, VMMotive.Room, VMMotive.Social, VMMotive.Fun };
        private VMMotive LastNeed = (VMMotive)(-1);
        private int LastObject = -1, LastAgo = int.MaxValue;
        private bool NeedsFine;

        private readonly List<LotTilePos> Fires = new List<LotTilePos>();
        private readonly List<VMAvatar> Sleepers = new List<VMAvatar>();
        private readonly List<LotTilePos> Doors = new List<LotTilePos>();

        // SimsRedux perf: what kind of object it is (from its name) is worked out once per object type and kept,
        // instead of several case-insensitive name searches per object on every decision of every Sim. (Kept
        // with the name and GUID it came from, so a renamed object - the Sim template - is looked at again.)
        [Flags] private enum ObjKind : byte { None = 0, Door = 1, Plate = 2, Fire = 4, Phone = 8 }
        private sealed class KindEntry { public string Label; public uint GUID; public ObjKind Kind; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FSO.Content.GameObject, KindEntry> ObjKinds
            = new System.Runtime.CompilerServices.ConditionalWeakTable<FSO.Content.GameObject, KindEntry>();

        private static ObjKind KindOf(VMEntity e)
        {
            var go = e?.Object;
            if (go?.OBJ == null) return ObjKind.None;
            var label = go.OBJ.ChunkLabel ?? "";
            var guid = go.OBJ.GUID;
            var k = ObjKinds.GetValue(go, _ => new KindEntry());
            lock (k)
            {
                if (!ReferenceEquals(k.Label, label) || k.GUID != guid)
                {
                    var kind = ObjKind.None;
                    if (label.IndexOf("door", StringComparison.OrdinalIgnoreCase) >= 0 && label.IndexOf("doorbell", StringComparison.OrdinalIgnoreCase) < 0) kind |= ObjKind.Door;
                    if (label.IndexOf("plate", StringComparison.OrdinalIgnoreCase) >= 0 && label.IndexOf("serving", StringComparison.OrdinalIgnoreCase) < 0) kind |= ObjKind.Plate;
                    if (guid == VMBurn.FIRE_GUID || label.Equals("fire", StringComparison.OrdinalIgnoreCase) || label.StartsWith("fire -", StringComparison.OrdinalIgnoreCase)
                        || label.Equals("fire1", StringComparison.OrdinalIgnoreCase)) kind |= ObjKind.Fire;
                    if (label.IndexOf("phone", StringComparison.OrdinalIgnoreCase) >= 0) kind |= ObjKind.Phone;
                    k.Kind = kind; k.Label = label; k.GUID = guid;
                }
                return k.Kind;
            }
        }

        private static bool IsDoor(VMEntity e) { return (KindOf(e) & ObjKind.Door) != 0; }
        /// <summary>A single plate of food (not the serving dish a group meal is shared from).</summary>
        private static bool IsPlate(VMEntity e) { return (KindOf(e) & ObjKind.Plate) != 0; }
        /// <summary>Someone else is (or was just) eating from this plate.</summary>
        private bool SomeoneElsesPlate(VM vm, VMEntity plate)
        {
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar; var m = a?.BrainMemory;
                if (a == null || a == Sim || m == null || m.Length < 14 || m[12] != plate.ObjectID) continue;
                var ago = Now - m[13]; if (ago < 0) ago += 30 * 1440;
                if (ago <= 120) return true;
            }
            return false;
        }
        /// <summary>Set when FindChore picked a phone call (100 = repairman, 101 = fire department), so it isn't made twice.</summary>
        public int ChoreCode;

        private static bool IsFire(VMEntity e) { return (KindOf(e) & ObjKind.Fire) != 0; }

        private static float Tiles(LotTilePos a, LotTilePos b)
        {
            if (a.Level != b.Level) return 1000;
            float dx = a.x - b.x, dy = a.y - b.y;
            return (float)Math.Sqrt(dx * dx + dy * dy) / 16f;
        }

        /// <summary>The phone call is in this Sim's memory, so nobody in the family calls again while help is on the way.</summary>
        public void MarkCall(int code)
        {
            var m = Sim.BrainMemory;
            if (m != null && m.Length >= 12) m[2] = code;
            // (also kept on its own, so four more things done since don't make them forget the call)
            if (m != null && m.Length >= 20 && (code == 100 || code == 101)) m[code == 100 ? 18 : 19] = Math.Max(1, Now);
        }

        /// <summary>Did anyone in the family make this call in the last few hours?</summary>
        private static bool CalledRecently(VM vm, int code, int now, int window)
        {
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar; var m = a?.BrainMemory;
                if (m == null || m.Length < 12) continue;
                if (m.Length >= 20 && (code == 100 || code == 101))
                {
                    var stamp = m[code == 100 ? 18 : 19];
                    if (stamp != 0)
                    {
                        var since = now - stamp; if (since < 0) since += 30 * 1440;
                        if (since <= window) return true;
                    }
                }
                for (int i = 0; i < 4; i++)
                {
                    if (m[i * 3 + 2] != code) continue;
                    var ago = now - m[i * 3 + 1]; if (ago < 0) ago += 30 * 1440;
                    if (ago <= window) return true;
                }
            }
            return false;
        }

        /// <summary>A phone service (by words in its menu name), as a choice the Sim can make, or null.</summary>
        private VMPieMenuInteraction PhoneService(VMStackFrame context, List<VMEntity> ents, Func<string, bool> wanted)
        {
            var done = new HashSet<short>();
            VMPieMenuInteraction best = null; float bestDist = float.MaxValue;
            // (a copy: the check trees run below can make or delete objects, which changes the lot's list)
            foreach (var iobj in context.VM.Entities.ToArray())
            {
                if (!(iobj is VMGameObject) || iobj.Position == LotTilePos.OUT_OF_WORLD) continue;
                var obj = iobj.MultitileGroup.GetInteractionGroupLeader(iobj);
                if (!done.Add(obj.ObjectID) || obj.TreeTable == null || obj.GetFlag(VMEntityFlags.Occupied)) continue;
                if ((KindOf(obj) & ObjKind.Phone) == 0) continue;
                var ttas = obj.TreeTableStrings;
                foreach (var ia in obj.TreeTable.Interactions)
                {
                    var action = obj.GetAction((int)ia.TTAIndex, Sim, context.VM.Context, false);
                    if (action == null) continue;
                    Sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    var avail = Sim.Thread.CheckAction(action, false);
                    if (avail == null) continue;
                    var baseName = ttas?.GetString((int)ia.TTAIndex) ?? "";
                    var options = (avail.Count > 0) ? avail : new List<VMPieMenuInteraction> { new VMPieMenuInteraction { Name = baseName } };
                    foreach (var opt in options)
                    {
                        var name = opt.Name ?? baseName;
                        if (!wanted(name) && !wanted(baseName + "/" + name)) continue;
                        var d = Tiles(Sim.Position, obj.Position);
                        if (d < bestDist)
                        {
                            bestDist = d;
                            best = new VMPieMenuInteraction { Name = name, ID = (byte)ia.TTAIndex, Param0 = opt.Param0, Entry = ia, Global = false, Callee = obj, Score = 1 };
                        }
                    }
                }
            }
            return best;
        }

        private int Entry(int i, int part) { var m = Sim.BrainMemory; return (m == null || m.Length < 12) ? 0 : m[i * 3 + part]; }

        /// <summary>What the Sim chose: it goes to the top of their memory (the oldest is forgotten).</summary>
        public void Remember(VMPieMenuInteraction chosen)
        {
            var m = Sim.BrainMemory;
            if (m == null || m.Length < 12 || chosen?.Callee == null) return;
            for (int i = 11; i >= 3; i--) m[i] = m[i - 3];
            m[0] = (chosen.Callee.ObjectID << 16) | (chosen.ID & 0xFFFF);
            m[1] = Now;
            // the need it was mainly for: the lowest need it helps with (or 0 = fun / other)
            int need = 0; short lowest = short.MaxValue;
            if (chosen.Entry != null)
                foreach (var mo in MemoryNeeds)
                    if (mo != VMMotive.Fun && mo != VMMotive.Social && Ads(chosen.Entry, chosen, mo) && Needs[(int)mo] < lowest) { lowest = Needs[(int)mo]; need = (int)mo + 1; }
            m[2] = need;
        }

        /// <summary>
        /// Time for bed: the nearest bed's Sleep when it's night and the Sim is tired, or any time they're
        /// exhausted (a sofa nap only if there's no bed). Null when it isn't bedtime, or a worse need
        /// (bladder, hunger) should come first.
        /// </summary>
        public VMPieMenuInteraction FindBed(VMStackFrame context, List<VMEntity> ents)
        {
            if (Sim.IsPet || Sim.GetMotiveData(VMMotive.SleepState) != 0) return null;
            var energy = Needs[(int)VMMotive.Energy];
            bool tired = energy < -55 || (Night && energy < 15) || (LateNight && energy < 40);
            if (!tired) return null;
            if (BeforeWork && energy > -60) return null; // (the carpool is coming)
            // the bathroom or a meal first if that's worse
            if (Needs[(int)VMMotive.Bladder] < Math.Min(-40, (int)energy) || Needs[(int)VMMotive.Hunger] < Math.Min(-50, (int)energy)) return null;

            VMPieMenuInteraction best = null, nap = null; float bestDist = float.MaxValue, napDist = float.MaxValue;
            int bestTier = int.MaxValue; // SimsRedux 15.7: 0 their own bed or side, 1 nobody's, 2 someone else's (ReduxBeds)
            var done = new HashSet<short>();
            foreach (var iobj in ents)
            {
                if (iobj.Position == LotTilePos.OUT_OF_WORLD || !(iobj is VMGameObject)) continue;
                var obj = iobj.MultitileGroup.GetInteractionGroupLeader(iobj);
                if (!done.Add(obj.ObjectID) || obj.TreeTable == null || ((VMGameObject)obj).Disabled > 0) continue;
                var ttas = obj.TreeTableStrings;
                foreach (var ia in obj.TreeTable.Interactions)
                {
                    var kind = TtaKindOf(ttas, ia.TTAIndex);
                    bool sleep = (kind & TtaKind.Sleep) != 0;
                    bool isNap = (kind & TtaKind.Nap) != 0;
                    if (!sleep && !isNap) continue;
                    var name = (ttas?.GetString((int)ia.TTAIndex) ?? "").Trim(); // (only for the one or two that match)
                    if (GivenUp(obj.ObjectID, ia.TTAIndex)) continue;
                    var action = obj.GetAction((int)ia.TTAIndex, Sim, context.VM.Context, false);
                    if (action == null) continue;
                    Sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    var avail = Sim.Thread.CheckAction(action, true);
                    if (avail == null) continue; // (someone's in it, a child's bed for a grown-up...)
                    var d = Tiles(Sim.Position, obj.Position);
                    var choice = new VMPieMenuInteraction { Name = name, ID = (byte)ia.TTAIndex, Entry = ia, Global = false, Callee = obj, Score = 1,
                        Param0 = avail.Count > 0 ? avail[0].Param0 : (short)0 };
                    int pref = sleep ? FSO.SimAntics.Utils.ReduxBeds.Preference(context.VM, Sim, obj, (int)ia.TTAIndex) : -1;
                    int tier = (pref < 0) ? 1 : pref;
                    if (sleep && (tier < bestTier || (tier == bestTier && d < bestDist))) { bestTier = tier; bestDist = d; best = choice; }
                    else if (isNap && d < napDist) { napDist = d; nap = choice; }
                }
            }
            best = best ?? nap;
            if (best == null) return null;
            // picked twice in the last hour and still not asleep (they can't get to it): leave it a while
            if (TriesRecently(best.Callee.ObjectID, best.ID, 60) >= 2) { GiveUp(best.Callee.ObjectID, best.ID); return null; }
            return best;
        }

        /// <summary>
        /// A chore worth doing now (bills to pay, something broken it can fix), or null. essentialsOnly (17.4: free will Off or
        /// Needs only): only the essentials - calling the fire department, paying the bills, calling the repairman - no
        /// housework and no fixing things themselves.
        /// </summary>
        public VMPieMenuInteraction FindChore(VMStackFrame context, List<VMEntity> ents, bool essentialsOnly = false)
        {
            var family = context.VM.TS1State?.CurrentFamily;
            if (family == null || !FSO.SimAntics.Utils.ReduxComputer.IsHouseholdSim(context.VM, Sim)) return null; // (not a guest player's Sim)

            // stage 3 - a fire: the nearest grown-up calls the fire department (once), before anything else
            if (Fires.Count > 0 && !Sim.IsChild && !CalledRecently(context.VM, 101, Now, 60))
            {
                var call = PhoneService(context, ents, n => n.IndexOf("Fire Dep", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Firem", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Fire Brigade", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.EndsWith("/Fire", StringComparison.OrdinalIgnoreCase));
                if (call != null) { ChoreCode = 101; return call; }
            }
            if (Urgent || !NeedsFine) return null;
            bool handy = Sim.GetPersonData(VMPersonDataVariable.MechanicalSkill) >= 200;
            // SimsRedux: housework is for everyone (not only Neat Sims): dirty dishes, puddles, a full
            // trash can, thirsty plants, a dirty toilet or shower - when the place is getting messy
            bool messy = Needs[(int)VMMotive.Room] < 60;
            VMPieMenuInteraction best = null; float bestDist = float.MaxValue;
            var done = new HashSet<short>();
            foreach (var iobj in ents)
            {
                if (iobj.Position == LotTilePos.OUT_OF_WORLD) continue;
                var obj = iobj.MultitileGroup.GetInteractionGroupLeader(iobj);
                if (!done.Add(obj.ObjectID) || obj.TreeTable == null || obj.GetFlag(VMEntityFlags.Occupied)) continue;
                var ttas = obj.TreeTableStrings;
                foreach (var ia in obj.TreeTable.Interactions)
                {
                    var kind = TtaKindOf(ttas, ia.TTAIndex);
                    bool bills = (kind & TtaKind.Bills) != 0;
                    bool repair = handy && !essentialsOnly && (kind & TtaKind.Repair) != 0;
                    bool housework = messy && !essentialsOnly && IsHousework(obj, kind);
                    if (!bills && !repair && !housework) continue;
                    var name = ttas?.GetString((int)ia.TTAIndex) ?? "";
                    if (housework && (IsPlate(obj) && SomeoneElsesPlate(context.VM, obj))) continue; // (someone's still eating)
                    if (GivenUp(obj.ObjectID, ia.TTAIndex)) continue; // tried it again and again: leave it for a while
                    var action = obj.GetAction((int)ia.TTAIndex, Sim, context.VM.Context, false);
                    if (action == null) continue;
                    Sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    var avail = Sim.Thread.CheckAction(action, true);
                    if (avail == null) continue; // not available right now (no bills, nothing broken)
                    var p = obj.Position;
                    double ddx = Sim.Position.x - p.x, ddy = Sim.Position.y - p.y;
                    var d = (float)Math.Sqrt(ddx * ddx + ddy * ddy) / 16f + (bills ? 0 : repair ? 5 : 8);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        best = new VMPieMenuInteraction { Name = name, ID = (byte)ia.TTAIndex, Entry = ia, Global = false, Callee = obj, Score = 1 };
                    }
                }
            }
            if (best != null)
            {
                // the same chore picked twice already in the last 2 hours and it's still waiting (they can't
                // get to it, or keep being pulled away): give up on it for 3 hours instead of looping
                if (TriesRecently(best.Callee.ObjectID, best.ID, 120) >= 2) { GiveUp(best.Callee.ObjectID, best.ID); return null; }
                return best;
            }

            // stage 3 - something is broken and nobody in the family here can fix it: call the repairman (once)
            // (once a day at most - the repairman can take a while to come, and is already on the way -
            //  and not while one is here)
            if (!Sim.IsChild && !CalledRecently(context.VM, 100, Now, 24 * 60) && !RepairmanHere(context.VM) && !SomeoneHandy(context.VM, family.ChunkID) && SomethingBroken(context))
            {
                var call = PhoneService(context, ents, n => n.IndexOf("Repairman", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Repair Man", StringComparison.OrdinalIgnoreCase) >= 0 || n.EndsWith("/Repair", StringComparison.OrdinalIgnoreCase));
                if (call != null) { ChoreCode = 100; return call; }
            }
            return null;
        }

        /// <summary>
        /// SimsRedux: housework anyone does - clean up dishes and puddles, empty the trash, water plants,
        /// clean the toilet, shower or tub. (Each only shows when it's needed: the object's own check
        /// decides.) A meal with servings still on it is left alone - someone may still want some.
        /// </summary>
        private static bool IsHousework(VMEntity obj, TtaKind kind)
        {
            if ((kind & TtaKind.Housework) == 0) return false;
            if ((kind & TtaKind.Clean) != 0 && FSO.SimAntics.Utils.ReduxLeftovers.IsGroupMeal(obj) && obj.GetAttribute(2) > 0) return false;
            return true;
        }

        // SimsRedux perf: what a menu choice is (from its name) - worked out once per object type's menu and kept
        // (with the name it came from, so a changed name is looked at again), not on every decision.
        [Flags] private enum TtaKind : ushort { None = 0, Bills = 1, Repair = 2, Clean = 4, Housework = 8, Sleep = 16, Nap = 32 }
        private sealed class TtaEntry { public string Name; public TtaKind Kind; }
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TTAs, Dictionary<uint, TtaEntry>> TtaKinds
            = new System.Runtime.CompilerServices.ConditionalWeakTable<TTAs, Dictionary<uint, TtaEntry>>();

        private static TtaKind TtaKindOf(TTAs ttas, uint index)
        {
            if (ttas == null) return TtaKind.None;
            var name = ttas.GetString((int)index) ?? "";
            var dict = TtaKinds.GetValue(ttas, _ => new Dictionary<uint, TtaEntry>());
            lock (dict)
            {
                TtaEntry entry;
                if (!dict.TryGetValue(index, out entry)) { entry = new TtaEntry(); dict[index] = entry; }
                if (!ReferenceEquals(entry.Name, name))
                {
                    entry.Kind = ClassifyTta(name);
                    entry.Name = name;
                }
                return entry.Kind;
            }
        }

        private static TtaKind ClassifyTta(string name)
        {
            var kind = TtaKind.None;
            if (name.IndexOf("Pay Bills", StringComparison.OrdinalIgnoreCase) >= 0) kind |= TtaKind.Bills;
            if (name.StartsWith("Repair", StringComparison.OrdinalIgnoreCase)) kind |= TtaKind.Repair;
            // housework anyone does (as IsHousework always looked for it)
            if (name.IndexOf("Eat", StringComparison.OrdinalIgnoreCase) < 0)
            {
                bool clean = name.Equals("Clean", StringComparison.OrdinalIgnoreCase) || name.Equals("Clean Up", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Clean ", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Mop", StringComparison.OrdinalIgnoreCase);
                bool trash = name.IndexOf("Empty Trash", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Take Out Trash", StringComparison.OrdinalIgnoreCase) >= 0
                    || name.Equals("Empty", StringComparison.OrdinalIgnoreCase);
                bool water = name.Equals("Water", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Water ", StringComparison.OrdinalIgnoreCase);
                if (clean) kind |= TtaKind.Clean;
                if (clean || trash || water) kind |= TtaKind.Housework;
            }
            var trimmed = name.Trim();
            if (trimmed.Equals("Sleep", StringComparison.OrdinalIgnoreCase)) kind |= TtaKind.Sleep;
            if (trimmed.Equals("Nap", StringComparison.OrdinalIgnoreCase)) kind |= TtaKind.Nap;
            return kind;
        }

        // SimsRedux: chores a Sim keeps failing. BrainMemory[14] = the chore (object << 16 | interaction),
        // [15] = when they gave up on it (game minutes). Game time only, so every machine agrees.
        private const int GiveUpMinutes = 180;

        private static int ChoreKey(short objectID, uint tta) { return (objectID << 16) | ((int)tta & 0xFFFF); }

        private int TriesRecently(short objectID, uint tta, int window)
        {
            var m = Sim.BrainMemory;
            if (m == null || m.Length < 12) return 0;
            int key = ChoreKey(objectID, tta), n = 0;
            for (int i = 0; i < 4; i++)
            {
                if (m[i * 3] != key || m[i * 3 + 1] == 0) continue;
                var ago = Now - m[i * 3 + 1]; if (ago < 0) ago += 30 * 1440;
                if (ago <= window) n++;
            }
            return n;
        }

        private bool GivenUp(short objectID, uint tta)
        {
            var m = Sim.BrainMemory;
            if (m == null || m.Length < 16 || m[14] != ChoreKey(objectID, tta)) return false;
            var ago = Now - m[15]; if (ago < 0) ago += 30 * 1440;
            return ago < GiveUpMinutes;
        }

        private void GiveUp(short objectID, uint tta)
        {
            var m = Sim.BrainMemory;
            if (m == null || m.Length < 16) return;
            m[14] = ChoreKey(objectID, tta);
            m[15] = Now;
        }

        /// <summary>A repairman is already on the lot.</summary>
        private static bool RepairmanHere(VM vm)
        {
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                if (e.Position == LotTilePos.OUT_OF_WORLD) continue;
                var n = (e.Name ?? "") + " " + (e.Object?.OBJ?.ChunkLabel ?? "");
                if (n.IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool SomeoneHandy(VM vm, int family)
        {
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var a = e as VMAvatar;
                if (a == null || a.IsPet || a.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family && FSO.SimAntics.Utils.ReduxComputer.IsHouseholdSim(vm, a) && a.GetPersonData(VMPersonDataVariable.MechanicalSkill) >= 200) return true;
            }
            return false;
        }

        /// <summary>Is anything on the lot waiting to be repaired? (its Repair interaction is available)</summary>
        private bool SomethingBroken(VMStackFrame context)
        {
            var done = new HashSet<short>();
            // (a copy: the check trees run below can make or delete objects, which changes the lot's list)
            foreach (var iobj in context.VM.Entities.ToArray())
            {
                if (!(iobj is VMGameObject) || iobj.Position == LotTilePos.OUT_OF_WORLD) continue;
                var obj = iobj.MultitileGroup.GetInteractionGroupLeader(iobj);
                if (!done.Add(obj.ObjectID) || obj.TreeTable == null) continue;
                // (only what the repairman fixes: his "find something to repair" looks for a repair state above 0 -
                //  a Repair interaction on anything else would have him called every day for nothing)
                bool needsRepair = false;
                foreach (var part in obj.MultitileGroup.Objects)
                    if (part.GetValue(VMStackObjectVariable.RepairState) > 0) { needsRepair = true; break; }
                if (!needsRepair) continue;
                var ttas = obj.TreeTableStrings;
                foreach (var ia in obj.TreeTable.Interactions)
                {
                    var name = ttas?.GetString((int)ia.TTAIndex) ?? "";
                    if (!name.StartsWith("Repair", StringComparison.OrdinalIgnoreCase)) continue;
                    var action = obj.GetAction((int)ia.TTAIndex, Sim, context.VM.Context, false);
                    if (action == null) continue;
                    Sim.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0;
                    if (Sim.Thread.CheckAction(action, false) != null) return true;
                }
            }
            return false;
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
        public float Weigh(TTABInteraction entry, VMPieMenuInteraction first, TTABMotiveEntry[] ads, float distance, VMEntity target, int interaction)
        {
            float w = 1;
            short objectID = target.ObjectID;

            // stage 3 - stay away from a fire (only what's well clear of it is worth doing)
            foreach (var f in Fires)
                if (Tiles(f, target.Position) < 4) { w *= 0.05f; break; }

            // stage 3 - manners: don't wake anyone. Socials with a sleeping Sim are off, and loud fun
            // (TV, stereo, games) near someone asleep is much less appealing
            if (target is VMAvatar ta && ta.GetMotiveData(VMMotive.SleepState) != 0) w *= 0.02f;

            // stage 3 - manners: don't eat someone else's plate (they're coming back for it)
            if (IsPlate(target) && SomeoneElsesPlate(Sim.Thread.Context.VM, target)) w *= 0.01f;

            // stage 3 - manners: don't stop for a chat with someone standing in a doorway (it blocks the way)
            if (target is VMAvatar inDoor)
                foreach (var d in Doors)
                    if (Tiles(d, inDoor.Position) < 1.2f) { w *= 0.3f; break; }

            // stage 3 - manners: fun near someone asleep (TV, stereo, games) is much less appealing
            if (Sleepers.Count > 0 && !(target is VMAvatar))
            {
                bool funOnly = true;
                foreach (var m in NonFunNeeds)
                    if (Ads(entry, first, m)) { funOnly = false; break; }
                if (funOnly)
                    foreach (var z in Sleepers)
                        if (Tiles(z.Position, target.Position) < 6) { w *= 0.3f; break; }
            }

            // stage 2 - finish what they start: still hungry after the plate, still tired after a nap -
            // go back to it (the same object first) instead of wandering off to something else
            if (Level >= 2 && LastNeed >= 0 && LastAgo <= 60 && Needs[(int)LastNeed] < 60)
            {
                if (Ads(entry, first, LastNeed)) w *= (objectID == LastObject) ? 2.5f : 1.3f;
                else if (!Urgent) w *= 0.7f;
            }

            // stage 2 - no back-and-forth: the same fun thing over and over, or bouncing between two
            // objects for the same need, gets less appealing
            if (Level >= 2)
            {
                int key = (objectID << 16) | (interaction & 0xFFFF);
                for (int i = 0; i < 4; i++)
                {
                    var stamp = Entry(i, 1);
                    if (stamp == 0) continue;
                    var ago = Now - stamp; if (ago < 0) ago += 30 * 1440;
                    if (Entry(i, 0) == key && Entry(i, 2) == 0 && ago <= 90) w *= 0.5f;              // the same fun again
                    if (i == 1 && (Entry(1, 0) >> 16) == objectID && (Entry(0, 0) >> 16) != objectID
                        && Entry(0, 2) == Entry(1, 2) && Entry(0, 2) != 0 && ago <= 30) w *= 0.5f;  // ping-pong
                }
            }

            // stage 2 - safety: with very low Cooking skill, a snack beats cooking a full meal (fires)
            if (Level >= 2 && Sim.GetPersonData(VMPersonDataVariable.CookingSkill) < 200 && Needs[(int)VMMotive.Hunger] > -60)
            {
                var n = FSO.SimAntics.Utils.VMReduxBrain.EnglishName(target, entry) ?? ""; // (17.4: English - never the shown name, lockstep)
                if (n.IndexOf("Breakfast", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Lunch", StringComparison.OrdinalIgnoreCase) >= 0
                    || n.IndexOf("Dinner", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("Cook", StringComparison.OrdinalIgnoreCase)) w *= 0.5f;
            }
            bool helpsPhysical = false, onlyFunSocial = true;
            foreach (var m in NonFunNeeds)
                if (Ads(entry, first, m)) { onlyFunSocial = false; break; }

            // 1. needs first: the lower a need, the more what fixes it matters
            foreach (var m in PhysicalNeeds)
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
