using System;
using System.Collections.Generic;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.0 (Part B #1b "Romance and closeness"): everything that changes the lot for it goes through this command
    /// (VM command 79), so on a visit every player's game does the same thing at the same moment (Utils/ReduxRomance.cs).
    ///   SetSwitch - the lot's game says whether it's on (On).
    ///   SetSkill  - a Sim's Romance skill (A, Value 0-1000) from the side file of the game that keeps the Sim. A guest:
    ///               their own Sim only.
    ///   SetPerks  - a Sim's romance perks (A, Value: Irresistible 1, Smooth Talker 2, Faithful Heart 4). A guest: own Sim.
    ///   Ask       - a romantic choice for another player's Sim (A asks B, Choice): changes nothing, that player's screen
    ///               asks them (Asked). A guest only for their own Sim (A), rate-limited.
    ///   Answer    - their answer (Yes): changes nothing (Answered). A guest only for their own Sim (B).
    ///   Act       - the choice itself (A with B, Obj the sofa / telescope / instrument / mirror, Yes: B's player said yes).
    ///               Practice Romance (A at mirror Obj) may come from a guest for their own Sim; the rest only from the
    ///               game running the lot.
    ///   Gain      - the Romance skill up a little for chatting (A, Value points). The lot's game only.
    ///   FirstKiss - A and B's first kiss (the lot's game noticed it): a moment, a little closer.
    ///   GreatDate - A and B had a great date at a date spot: closer, more fun, the afterglow.
    ///   Caught    - someone (B) came in on A's WooHoo (Yes: nearly - close by): embarrassed, or thrilled if Daring.
    /// </summary>
    public class VMNetReduxRomanceCmd : VMNetCommandBodyAbstract
    {
        public enum RomMode : byte
        {
            SetSwitch = 0, SetSkill = 1, SetPerks = 2, Ask = 3, Answer = 4, Act = 5, Gain = 6, FirstKiss = 7, GreatDate = 8, Caught = 9,
        }

        public RomMode Mode;
        public short A, B, Obj, Value;
        public byte Choice;
        public bool On, Yes;

        /// <summary>Ask, Answer: on every game (each screen asks / tells the right player).</summary>
        public static event Action<VM, VMNetReduxRomanceCmd> Asked, Answered;
        /// <summary>Act: how it went - why not (null: it went ahead), whether it went well, and a "not in the mood".</summary>
        public static event Action<VM, VMNetReduxRomanceCmd, string, bool, VMReduxRomance.Mood> Acted;
        /// <summary>FirstKiss, GreatDate, Caught: on every game (the hearts, the words, the emotions).</summary>
        public static event Action<VM, VMNetReduxRomanceCmd> Moment;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                switch (Mode)
                {
                    case RomMode.SetSwitch: VMReduxRomance.SetOn(vm, On); return true;
                    case RomMode.SetSkill:
                        {
                            var sim = vm.GetObjectById(A) as VMAvatar;
                            if (sim == null || sim.IsPet) return false;
                            sim.SetAttribute(VMReduxRomance.SkillAttr, (short)Math.Max(0, Math.Min(VMReduxRomance.MaxSkill, (int)Value)));
                            return true;
                        }
                    case RomMode.SetPerks:
                        {
                            var sim = vm.GetObjectById(A) as VMAvatar;
                            if (sim == null || sim.IsPet) return false;
                            sim.SetAttribute(VMReduxRomance.PerkAttr, (short)(Value & 7));
                            return true;
                        }
                    case RomMode.Ask: Fire(Asked, vm); return true;
                    case RomMode.Answer: Fire(Answered, vm); return true;
                    case RomMode.Act: return Act(vm);
                    case RomMode.Gain:
                        {
                            var sim = vm.GetObjectById(A) as VMAvatar;
                            if (sim == null || sim.IsPet || !VMReduxRomance.On(vm)) return false;
                            VMReduxRomance.Gain(sim, Math.Max(0, Math.Min(20, (int)Value)));
                            return true;
                        }
                    case RomMode.FirstKiss:
                        {
                            var a = vm.GetObjectById(A) as VMAvatar;
                            var b = vm.GetObjectById(B) as VMAvatar;
                            if (a == null || b == null || a == b || a.IsPet || b.IsPet || !VMReduxRomance.On(vm)) return false;
                            VMReduxRomance.FirstKiss(a, b);
                            Fire(Moment, vm);
                            return true;
                        }
                    case RomMode.GreatDate:
                        {
                            var a = vm.GetObjectById(A) as VMAvatar;
                            var b = vm.GetObjectById(B) as VMAvatar;
                            if (a == null || b == null || !VMReduxRomance.On(vm) || !VMReduxRomance.GreatDate(vm, a, b)) return false;
                            Fire(Moment, vm);
                            return true;
                        }
                    case RomMode.Caught:
                        {
                            var a = vm.GetObjectById(A) as VMAvatar;
                            if (a == null || !VMReduxRomance.On(vm) || !VMReduxRomance.Caught(a, Yes)) return false;
                            Fire(Moment, vm);
                            return true;
                        }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] romance command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private void Fire(Action<VM, VMNetReduxRomanceCmd> ev, VM vm)
        {
            try { ev?.Invoke(vm, this); } catch (Exception e) { Console.WriteLine("[SimsRedux] romance: " + e.Message); }
        }

        private bool Act(VM vm)
        {
            var a = vm.GetObjectById(A) as VMAvatar;
            var b = vm.GetObjectById(B) as VMAvatar;
            var obj = Obj == 0 ? null : vm.GetObjectById(Obj);
            var c = (VMReduxRomance.Choice)Choice;
            bool ok = false;
            var mood = VMReduxRomance.Mood.Fine;
            string why;
            if (a == null || c == VMReduxRomance.Choice.None || c > VMReduxRomance.Choice.BirdsBees) why = "";
            else why = VMReduxRomance.Act(vm, a, b, obj, c, Yes, out ok, out mood);
            try { Acted?.Invoke(vm, this, why, ok, mood); } catch (Exception e) { Console.WriteLine("[SimsRedux] romance: " + e.Message); }
            return why == null;
        }

        // ------------------------------------------------------------------ a visiting player's commands

        /// <summary>
        /// On the lot's game only (Verify runs there before a command joins the lot's ticks, so dropping one keeps every game
        /// in step): one ask or Practice Romance every few seconds from each player, one open ask per asking Sim.
        /// </summary>
        private static readonly Dictionary<uint, DateTime> LastSent = new Dictionary<uint, DateTime>();
        private static readonly Dictionary<short, DateTime> OpenAsk = new Dictionary<short, DateTime>();
        private static readonly TimeSpan SendEvery = TimeSpan.FromSeconds(5), AskOpenFor = TimeSpan.FromMinutes(2);

        private bool Allowed(VMAvatar caller, bool ask)
        {
            var now = DateTime.UtcNow;
            lock (LastSent)
            {
                DateTime t;
                if (ask && OpenAsk.TryGetValue(A, out t) && now - t < AskOpenFor) return false;
                if (LastSent.TryGetValue(caller.PersistID, out t) && now - t < SendEvery) return false;
                if (LastSent.Count > 200) LastSent.Clear();
                if (OpenAsk.Count > 200) OpenAsk.Clear();
                LastSent[caller.PersistID] = now;
                if (ask) OpenAsk[A] = now;
                return true;
            }
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (Mode == RomMode.Answer) lock (LastSent) OpenAsk.Remove(A);
            if (!FromNet) return true; // (this game runs the lot)
            if (caller == null || caller.IsPet) return false;
            switch (Mode)
            {
                case RomMode.SetSkill:
                case RomMode.SetPerks:
                    return A == caller.ObjectID;
                case RomMode.Ask: return A == caller.ObjectID && A != B && Allowed(caller, true);
                case RomMode.Answer: return B == caller.ObjectID;
                case RomMode.Act:
                    return A == caller.ObjectID && Choice == (byte)VMReduxRomance.Choice.PracticeRomance && !Yes && Allowed(caller, false);
            }
            return false;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(A);
            writer.Write(B);
            writer.Write(Obj);
            writer.Write(Value);
            writer.Write(Choice);
            writer.Write(On);
            writer.Write(Yes);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (RomMode)reader.ReadByte();
            A = reader.ReadInt16();
            B = reader.ReadInt16();
            Obj = reader.ReadInt16();
            Value = reader.ReadInt16();
            Choice = reader.ReadByte();
            On = reader.ReadBoolean();
            Yes = reader.ReadBoolean();
        }
    }
}
