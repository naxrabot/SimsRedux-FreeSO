using System;
using System.IO;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux test cheat "burglar": a burglar comes to this lot now.
    ///
    /// It runs the game's own "CT - Force Thief" tree (0x1018) on the lot's invisible NPC Controller - the same
    /// tree as the Mailbox's debug interaction "Force Burglar". That tree finds the neighbourhood's burglar (the
    /// NPC Thief neighbour), and unless a visit is already scheduled for him, schedules one (a Scheduler object,
    /// visitor type 1 = thief: he arrives about 15 game minutes later, by the road) and marks today as the
    /// controller's "thief day" so the nightly roll doesn't send a second one. It skips the usual conditions
    /// (night, everyone asleep, a 1-in-N roll) - the burglar comes whoever is awake.
    ///
    /// Same on every player's game. Online, only players with an Admin account can use it.
    /// </summary>
    public class VMNetReduxBurglarCmd : VMNetCommandBodyAbstract
    {
        public const uint NPCControllerGUID = 0x70F69082, ThiefGUID = 0xC7857D8A, SchedulerGUID = 0x8535DBF8;
        public const ushort ForceThiefTree = 0x1018;
        public const string ForceThiefName = "CT - Force Thief";

        public enum BurglarState : byte
        {
            WillCome = 0,       // the tree will schedule him
            NoController = 1,   // this lot has no NPC Controller (no burglars here)
            NoThief = 2,        // the neighbourhood has no burglar neighbour
            AlreadyHere = 3,    // a burglar is on the lot now
            AlreadyComing = 4,  // he's already scheduled
            NoTree = 5          // the controller hasn't got the Force Thief tree
        }

        /// <summary>What running the cheat on this lot would do (the client says so before sending it).</summary>
        public static BurglarState Predict(VM vm)
        {
            return Predict(vm, out _, out _);
        }

        private static BurglarState Predict(VM vm, out VMEntity controller, out VMRoutine tree)
        {
            controller = null; tree = null;
            var q = vm?.Context?.ObjectQueries;
            if (q == null) return BurglarState.NoController;
            var ctrls = q.GetObjectsByGUID(NPCControllerGUID);
            if (ctrls != null) foreach (var c in ctrls) if (c != null && !c.Dead) { controller = c; break; }
            if (controller == null) return BurglarState.NoController;

            if (controller.TreeByName != null && controller.TreeByName.TryGetValue(ForceThiefName, out var entry))
                tree = entry?.bhav as VMRoutine;
            if (tree == null) tree = controller.Object?.Resource?.GetRoutine(ForceThiefTree) as VMRoutine;
            if (tree == null) return BurglarState.NoTree;

            var thieves = q.GetObjectsByGUID(ThiefGUID);
            if (thieves != null) foreach (var t in thieves) if (t != null && !t.Dead) return BurglarState.AlreadyHere;

            var hood = Content.Content.Get()?.Neighborhood;
            short thief = (hood == null) ? (short)-1 : hood.SetToNext(0, ThiefGUID);
            if (thief < 0) return BurglarState.NoThief;

            // (the tree's own "Check Schedule": a Scheduler whose attribute 0 is that neighbour)
            var schedulers = q.GetObjectsByGUID(SchedulerGUID);
            if (schedulers != null) foreach (var s in schedulers) if (s != null && !s.Dead && s.GetAttribute(0) == thief) return BurglarState.AlreadyComing;
            return BurglarState.WillCome;
        }

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            if (Predict(vm, out var controller, out var tree) != BurglarState.WillCome) return false;
            var thread = controller.Thread;
            if (thread == null) return false;
            // run it in the controller's own stack (as "Call Named Tree" does), with the controller as the
            // stack object; its temps are put back afterwards so its own main loop carries on as it was
            var temps = (short[])thread.TempRegisters.Clone();
            var tempXL = (int[])thread.TempXL.Clone();
            try
            {
                return thread.RunInMyStack(tree, controller.Object, new short[4], controller);
            }
            finally
            {
                Array.Copy(temps, thread.TempRegisters, Math.Min(temps.Length, thread.TempRegisters.Length));
                Array.Copy(tempXL, thread.TempXL, Math.Min(tempXL.Length, thread.TempXL.Length));
            }
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            // online: only a player whose town account is an Admin (the host's own game may - single player,
            // or a lot hosted from the Windows game, where the game itself only allows it offline)
            if (FromNet) return vm.ReduxAdmins.Contains(ActorUID);
            return true;
        }

        public override void SerializeInto(BinaryWriter writer) { base.SerializeInto(writer); }
        public override void Deserialize(BinaryReader reader) { base.Deserialize(reader); }
    }
}
