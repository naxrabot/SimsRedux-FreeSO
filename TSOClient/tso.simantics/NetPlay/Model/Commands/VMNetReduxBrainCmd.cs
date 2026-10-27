using System;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.4 (Part B #9): Adaptive Sims, the Sim brain on the lot (Utils/ReduxBrain.cs, VMReduxBrain), VM command 86.
    ///   Set      - puts a Sim's brain (W, the 9 packed weight attributes 121-129) and their free will and learned play style
    ///              (Settings, attribute 120) on the Sim, so every game on the lot scores that Sim's choices the same. Sent by
    ///              the game that owns the brain: the lot's game for its own family and townies (from its side file or the
    ///              shipped baseline), a visiting player's game for their own Sim only. All zeros = no brain (Smartest).
    ///   FreeWill - only the Sim's free will (Settings bits 0-2), the rest of attribute 120 kept: the player picked Free Will.
    /// A visiting player only for their own Sim.
    /// </summary>
    public class VMNetReduxBrainCmd : VMNetCommandBodyAbstract
    {
        public enum BrainMode : byte { Set = 0, FreeWill = 1 }

        public BrainMode Mode;
        public short Sim;
        public short Settings;
        public short[] W = new short[VMReduxBrain.WeightAttrs];

        /// <summary>A Sim's brain or free will changed on this lot (every game).</summary>
        public static event Action<VM, VMAvatar> Changed;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1) return false;
            try
            {
                var sim = vm.GetObjectById(Sim) as VMAvatar;
                if (sim == null || sim.IsPet || sim.Dead) return false;
                switch (Mode)
                {
                    case BrainMode.Set: VMReduxBrain.Set(sim, W, Settings); break;
                    case BrainMode.FreeWill: VMReduxBrain.SetFreeWill(sim, (VMReduxBrain.FreeWill)Math.Max(0, Math.Min(3, Settings & 7))); break;
                    default: return false;
                }
                try { Changed?.Invoke(vm, sim); } catch { }
                return true;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] brain command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!FromNet) return true; // (this game runs the lot)
            // a visiting player: only their own Sim
            if (caller == null || caller.IsPet) return false;
            return Sim == caller.ObjectID && (Mode == BrainMode.Set || Mode == BrainMode.FreeWill);
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(Sim);
            writer.Write(Settings);
            for (int i = 0; i < VMReduxBrain.WeightAttrs; i++) writer.Write((W != null && i < W.Length) ? W[i] : (short)0);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (BrainMode)reader.ReadByte();
            Sim = reader.ReadInt16();
            Settings = reader.ReadInt16();
            W = new short[VMReduxBrain.WeightAttrs];
            for (int i = 0; i < VMReduxBrain.WeightAttrs; i++) W[i] = reader.ReadInt16();
        }
    }
}
