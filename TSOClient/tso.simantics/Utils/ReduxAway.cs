using System;
using System.Linq;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.6 fix: Sims who are away from the lot - at work, at school, riding in a car.
    ///
    /// Sims 1's "At Work" and "At School" (the car portal, "Portal - Car", interactions 0 and 1) walk the Sim to the
    /// carpool or bus, set them hidden and out of the world ("send out of world"), mark them away (person data 87,
    /// "at work/school") and wait there until the end of the day, then put them back by the car ("return to world").
    /// The game ignores a cancel while they're away. But the stuck-action guard (UIInteractionQueue: a cancelled action
    /// still going 45 seconds later resets the Sim, VMNetResetSimCmd) took that as "stuck" and reset the Sim: their
    /// script was cleared with them still out of the world, hidden and marked away - nothing left to bring them back.
    /// They stayed nowhere, and the house was saved like that ("cancel At Work bricks the save").
    ///
    /// Now: a cancel of what an away Sim is doing is refused (VMNetInteractionCancelCmd - the same on every game, and the
    /// queue says why); a reset of a Sim who was away puts them back on the lot (VMAvatar.Reset, VMNetResetSimCmd); and a
    /// house loaded from its save with a family Sim stranded off the lot - nothing running that keeps them away - gets
    /// them back by the mailbox (VM.Load: every game loading the same save does the same).
    /// Lot state only, never settings or this device's clock.
    /// </summary>
    public static class ReduxAway
    {
        /// <summary>Sims 1's car portal ("Portal - Car"): its interaction 0 is At Work, 1 At School.</summary>
        public const uint CarPortalGUID = 0x865a6812;

        /// <summary>Off the lot: out of the world, not held by anyone or anything.</summary>
        public static bool OffLot(VMAvatar sim)
        {
            return sim != null && !sim.Dead && sim.Position == LotTilePos.OUT_OF_WORLD && sim.Container == null;
        }

        /// <summary>
        /// Something that takes Sims off the lot: the car portal, the carpools, the school bus, taxis, limos and other
        /// cars and buses (by the game's own object names: "Car - ...", "Portal - ...", and taxi/bus/limo/shuttle/trolley).
        /// </summary>
        public static bool IsAwayObject(VMEntity e)
        {
            var obj = e?.Object?.OBJ;
            if (obj == null) return false;
            if (obj.GUID == CarPortalGUID) return true;
            var label = obj.ChunkLabel ?? "";
            if (label.StartsWith("Car - ", StringComparison.Ordinal) || label.StartsWith("Portal - ", StringComparison.Ordinal)) return true;
            foreach (var word in label.ToLowerInvariant().Split(new[] { ' ', '-', '_', '(', ')', ',', '.', '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                switch (word)
                {
                    case "taxi": case "cab": case "bus": case "limo": case "limousine": case "shuttle": case "trolley": case "carpool":
                        return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The running interaction (at the front of the queue, up to the active block) that keeps this Sim away - At
        /// Work, At School, a ride - or null.
        /// </summary>
        public static VMQueuedAction AwayAction(VMAvatar sim)
        {
            var thread = sim?.Thread;
            if (thread == null || thread.Queue == null || thread.ActiveQueueBlock < 0) return null;
            if (!thread.Stack.Any(f => f != null && f.ActionTree)) return null; // (nothing of an interaction is running)
            int last = Math.Min(thread.ActiveQueueBlock, thread.Queue.Count - 1);
            for (int i = 0; i <= last; i++)
            {
                var q = thread.Queue[i];
                if (q != null && IsAwayObject(q.Callee)) return q;
            }
            return null;
        }

        /// <summary>Away for real: off the lot, with the interaction that took them there still running.</summary>
        public static bool IsAway(VMAvatar sim)
        {
            return OffLot(sim) && AwayAction(sim) != null;
        }

        /// <summary>
        /// A cancel of this action is refused: the Sim is off the lot and it's one of the actions running now (cancelling
        /// it could only leave them nowhere). Actions still waiting in the queue behind it can be cancelled as usual.
        /// </summary>
        public static bool CancelRefused(VMAvatar sim, ushort actionUID)
        {
            if (!OffLot(sim)) return false;
            var thread = sim.Thread;
            if (thread?.Queue == null) return false;
            int index = thread.Queue.FindIndex(q => q != null && q.UID == actionUID);
            if (index < 0) return false;
            return index <= thread.ActiveQueueBlock; // (running now; nothing running: cancel as usual)
        }

        /// <summary>"Skye is at work - they'll be home at 5 PM." (for the queue's notice; plain words)</summary>
        public static string Notice(VMAvatar sim)
        {
            var name = string.IsNullOrWhiteSpace(sim?.Name) ? "This Sim" : sim.Name;
            var away = AwayAction(sim);
            bool portal = away?.Callee?.Object?.OBJ?.GUID == CarPortalGUID;
            bool school = portal && (away.InteractionNumber == 1 || (away.InteractionNumber != 0 && sim.IsChild));
            bool work = portal && !school;
            if (school) return name + " is at school - they'll be home at 3 PM.";
            if (work)
            {
                try
                {
                    int job = sim.GetPersonData(VMPersonDataVariable.JobType);
                    var lvl = (job > 0) ? FSO.Content.Content.Get().Jobs?.GetJobLevel((short)job, sim.GetPersonData(VMPersonDataVariable.JobPromotionLevel)) : null;
                    if (lvl != null) return name + " is at work - they'll be home at " + Hour(lvl.EndTime) + ".";
                }
                catch { }
                return name + " is at work - they'll be home after their shift.";
            }
            return name + " is away right now - they'll be back soon.";
        }

        private static string Hour(int h)
        {
            h = ((h % 24) + 24) % 24;
            int h12 = h % 12 == 0 ? 12 : h % 12;
            return h12 + (h < 12 ? " AM" : " PM");
        }

        /// <summary>
        /// After a reset (VMAvatar.Reset - a script error, the stuck-action guard, the debug menu): a Sim the car portal
        /// had taken away (person data 87 set) who is still out of the world comes back onto the lot. The reset already
        /// cleared their script, so nothing else would ever bring them back.
        /// </summary>
        public static void AfterReset(VM vm, VMAvatar sim)
        {
            if (vm == null || !vm.TS1 || !OffLot(sim)) return;
            if (sim.GetPersonData(VMPersonDataVariable.TS1AtWorkSchool) == 0) return;
            try { BringBack(vm, sim, "was reset while away at work or school"); }
            catch (Exception e) { Console.WriteLine("[SimsRedux] away: couldn't bring " + sim.Name + " back: " + e.Message); }
        }

        /// <summary>
        /// On loading a house from its save (VM.Load, never an online join or resync): a Sim of the family who is off the
        /// lot with nothing running that keeps them away - left there by the old reset, or a save whose away script
        /// started over - is put back by the mailbox, not hidden, not marked away. Sims away for real (At Work or At
        /// School still running) are left as they are: their carpool brings them home. Returns how many came back.
        /// </summary>
        public static int RepairStranded(VM vm)
        {
            if (vm == null || !vm.TS1 || vm.ReduxLoadingLiveState) return 0;
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null) return 0;
            int n = 0;
            foreach (var sim in vm.Entities.OfType<VMAvatar>().OrderBy(a => a.ObjectID).ToList())
            {
                if (!OffLot(sim) || sim.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) != fam.ChunkID) continue;
                if (sim.GetPersonData(VMPersonDataVariable.PersonType) == 1) continue; // (a visitor - not the family's)
                if (AwayAction(sim) != null) continue; // (really at work / at school / riding: the game brings them back)
                try
                {
                    if (BringBack(vm, sim, "was saved off the lot with nothing to bring them back")) n++;
                }
                catch (Exception e) { Console.WriteLine("[SimsRedux] away: couldn't bring " + sim.Name + " back: " + e.Message); }
            }
            return n;
        }

        /// <summary>
        /// Puts an off-lot Sim back on the lot: their script starts over (as a reset), they're no longer hidden or marked
        /// at work/school, and they stand by the mailbox (else next to a family member, else the nearest open spot to the
        /// middle of the lot). Lot state only - no random numbers - so every game does the same.
        /// </summary>
        public static bool BringBack(VM vm, VMAvatar sim, string why)
        {
            var thread = sim.Thread;
            if (thread != null)
            {
                thread.Stack.Clear();
                thread.Queue.Clear();
                thread.ActiveQueueBlock = -1;
                thread.BlockingState = null;
                thread.EODConnection = null;
                thread.Interrupt = false;
                thread.QueueDirty = true;
            }
            if (vm.GlobalBlockingDialog == sim)
            {
                vm.GlobalBlockingDialog = null;
                if (!vm.ReduxNoPause && vm.LastSpeedMultiplier > 0)
                {
                    vm.SpeedMultiplier = vm.LastSpeedMultiplier;
                    vm.LastSpeedMultiplier = 0;
                }
            }
            sim.SetPersonData(VMPersonDataVariable.Priority, 0);
            sim.SetPersonData(VMPersonDataVariable.NonInterruptable, 0);
            sim.SetFlag(VMEntityFlags.InteractionCanceled, false);
            sim.SetFlag(VMEntityFlags.NotifiedByIdleForInput, false);
            sim.ClearMotiveChanges(); // (the work day's need changes)
            if (sim.Animations != null) sim.Animations.Clear();
            bool placed = Place(vm, sim);
            vm.Scheduler.RescheduleInterrupt(sim);
            Console.WriteLine("[SimsRedux] away: " + (sim.Name ?? "a Sim") + " " + why + " - "
                + (placed ? "back on the lot at " + sim.Position.TileX + "," + sim.Position.TileY : "no room on the lot for them"));
            return placed;
        }

        /// <summary>
        /// The car portal's At Work / At School ended (VMThread.EndCurrentInteraction) with the Sim still off the lot -
        /// it only does that when it went wrong (e.g. the carpool came for a Sim already stranded off the lot and the walk
        /// to the car failed). They're put back on the lot; their script carries on as it is (back to their own loop).
        /// </summary>
        public static void InteractionEnded(VMEntity entity, VMQueuedAction interaction)
        {
            var sim = entity as VMAvatar;
            var vm = sim?.Thread?.Context?.VM;
            if (vm == null || !vm.TS1 || interaction?.Callee?.Object?.OBJ == null) return;
            if (interaction.Callee.Object.OBJ.GUID != CarPortalGUID || !OffLot(sim)) return;
            try
            {
                bool placed = Place(vm, sim);
                Console.WriteLine("[SimsRedux] away: " + (sim.Name ?? "a Sim") + "'s \"" + interaction.Name + "\" ended off the lot - "
                    + (placed ? "back on the lot" : "no room on the lot for them"));
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] away: couldn't bring " + sim.Name + " back: " + e.Message); }
        }

        /// <summary>
        /// No longer hidden or marked at work/school, and standing by the mailbox (else next to a family member, else
        /// the nearest open spot to the middle of the lot). The script is left alone. Lot state only - no random numbers.
        /// </summary>
        private static bool Place(VM vm, VMAvatar sim)
        {
            sim.SetPersonData(VMPersonDataVariable.TS1AtWorkSchool, 0);
            sim.SetValue(VMStackObjectVariable.Hidden, 0); // (the car hides them on the way out)

            var mailbox = vm.Entities.Where(x => !x.Dead && x.Object?.OBJ != null && x.Position != LotTilePos.OUT_OF_WORLD
                && (x.Object.OBJ.GUID == 0xEF121974 || x.Object.OBJ.GUID == 0x1D95C9B0)).OrderBy(x => x.ObjectID).FirstOrDefault();
            if (mailbox != null) VMFindLocationFor.FindLocationFor(sim, mailbox, vm.Context, VMPlaceRequestFlags.Default);
            if (sim.Position == LotTilePos.OUT_OF_WORLD)
            {
                int family = sim.GetPersonData(VMPersonDataVariable.TS1FamilyNumber);
                var near = vm.Entities.OfType<VMAvatar>()
                    .Where(x => x != sim && !x.Dead && x.Container == null && x.Position != LotTilePos.OUT_OF_WORLD
                        && x.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == family)
                    .OrderBy(x => x.ObjectID).FirstOrDefault();
                if (near != null) VMFindLocationFor.FindLocationFor(sim, near, vm.Context, VMPlaceRequestFlags.Default);
            }
            if (sim.Position == LotTilePos.OUT_OF_WORLD) VMNetDebugSimCmd.MoveToFreeSpot(vm, sim);
            return sim.Position != LotTilePos.OUT_OF_WORLD;
        }
    }
}
