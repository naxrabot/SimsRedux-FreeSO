using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 16.9 (Part B #1b): traits on the lot (Utils/ReduxTraits.cs, VMReduxTraits).
    ///   Set - puts a Sim's traits (T0-T2, up to three normal traits) and orientation (Orient) on the Sim (Sim, an object
    ///         id), as person-object attributes 80-83, so every game on the lot reads the same ones. Sent by the game that
    ///         owns the Sim's traits: the lot's game for its own family and townies (from its side file), a visiting
    ///         player's game for their own Sim only. All zeros clears them (the Feature Switch "traits" off).
    ///   Act - a trait-only choice (Choice, VMReduxTraits.Choice) by Sim on Target: the Sim goes and does it, and the
    ///         trait's bonus applies. A visiting player only for their own Sim.
    /// </summary>
    public class VMNetReduxTraitsCmd : VMNetCommandBodyAbstract
    {
        public enum TraitMode : byte { Set = 0, Act = 1 }

        public TraitMode Mode;
        public short Sim, Target;
        public byte T0, T1, T2, Orient, Choice;

        /// <summary>Set: a Sim's traits changed on this lot (every game).</summary>
        public static event Action<VM, VMAvatar> Changed;
        /// <summary>Act: done (why null) or not (why: the reason, or "" to say nothing).</summary>
        public static event Action<VM, VMNetReduxTraitsCmd, string> Acted;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                var sim = vm.GetObjectById(Sim) as VMAvatar;
                if (sim == null || sim.IsPet) return false;
                if (Mode == TraitMode.Set)
                {
                    VMReduxTraits.Set(sim, new[] { T0, T1, T2 }, Orient);
                    try { Changed?.Invoke(vm, sim); } catch { }
                    return true;
                }
                if (Mode == TraitMode.Act)
                {
                    var why = VMReduxTraits.Act(vm, sim, vm.GetObjectById(Target), (VMReduxTraits.Choice)Choice);
                    try { Acted?.Invoke(vm, this, why); } catch { }
                    return why == null;
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] traits command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!FromNet) return true; // (this game runs the lot)
            // a visiting player: only their own Sim
            if (caller == null || caller.IsPet) return false;
            if (Sim != caller.ObjectID) return false;
            if (Mode == TraitMode.Act)
            {
                // (16.9: not with a full queue, on something gone or switched off, or again before the Sim's cooldown is over -
                //  Execute checks the same, this just keeps extras out of the lot's ticks)
                if (caller.Thread == null || caller.Thread.Queue.Count >= FSO.SimAntics.Engine.VMThread.MAX_USER_ACTIONS) return false;
                var target = vm.GetObjectById(Target);
                if (target == null || target.Dead) return false;
                if (target is VMGameObject && ((VMGameObject)target).Disabled > 0) return false;
                return VMReduxTraits.ActWait(vm, caller) == 0;
            }
            return Mode == TraitMode.Set;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(Sim);
            writer.Write(Target);
            writer.Write(T0);
            writer.Write(T1);
            writer.Write(T2);
            writer.Write(Orient);
            writer.Write(Choice);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (TraitMode)reader.ReadByte();
            Sim = reader.ReadInt16();
            Target = reader.ReadInt16();
            T0 = reader.ReadByte();
            T1 = reader.ReadByte();
            T2 = reader.ReadByte();
            Orient = reader.ReadByte();
            Choice = reader.ReadByte();
        }
    }
}
