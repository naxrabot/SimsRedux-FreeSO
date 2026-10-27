using System;
using System.Collections.Generic;
using System.IO;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.0 (Part B #1b extras, R2): family and matchmaking (Utils/ReduxMatch.cs - VMReduxMatch). Everything that
    /// changes the lot for them goes through this command, so on a visit every player's game does the same at the same moment.
    ///   SetTurnOns - puts a Sim's turn-ons (T0, T1) and turn-off (T2) on the Sim (A) as attributes 90-92. Sent by the game
    ///                that owns the Sim: the lot's game for its own family and townies, a visiting player's for their own Sim.
    ///   Close      - A does a closeness choice (Kind, VMReduxMatch.Close: Spend Quality Time / Cuddle) with B. A guest only
    ///                for their own Sim (A), once every VMReduxMatch.CloseCooldown game minutes.
    ///   Ask        - "Ask to Be Life Partners" to another player's Sim (Singleplayer+): changes nothing, that player's
    ///                screen asks them (Asked). A guest only for their own Sim (A).
    ///   Answer     - that player's answer (Yes): changes nothing (Answered). A guest only for their own Sim (B).
    ///   LifeTalk   - the talk itself (A asks B; Yes: B's player already agreed, No: B's player said no). Otherwise the
    ///                lot works out B's answer with its own random numbers. Only the lot's game sends it.
    ///   Arrive     - neighbour GUID comes onto the lot as a greeted visitor by object Anchor: a date set up on the dating
    ///                app (Kind 0, with A the Sim they're dating - chemistry gives them a head start) or a child visiting a
    ///                parent who lives here (Kind 1). Only the lot's game sends it.
    ///   AdoptBaby  - a baby adopted by A (and B, a second parent, or 0) arrives in a bassinet, named Name. Lot's game only.
    ///   AdoptChild - a child (Female, Skin 0-2, Name) adopted by A joins the family and turns up by the mailbox. Lot's game only.
    ///   MoveIn     - A (a life partner on the lot) moves into the lot's family. Lot's game only.
    /// </summary>
    public class VMNetReduxMatchCmd : VMNetCommandBodyAbstract
    {
        public enum MatchMode : byte { SetTurnOns = 0, Close = 1, Ask = 2, Answer = 3, LifeTalk = 4, Arrive = 5, AdoptBaby = 6, AdoptChild = 7, MoveIn = 8 }

        public MatchMode Mode;
        public short A, B, Anchor;
        public byte T0, T1, T2, Kind, Skin;
        public bool Yes, No, Female;
        public uint GUID;
        public string Name = "";

        /// <summary>Close: done (why null) or not (why: the reason, or "" to say nothing). On every game.</summary>
        public static event Action<VM, VMNetReduxMatchCmd, string> Closed;
        /// <summary>Ask, Answer: on every game (the screen asks / tells the right player).</summary>
        public static event Action<VM, VMNetReduxMatchCmd> Asked, Answered;
        /// <summary>LifeTalk: how it went (agreed or not), on every game - each game keeps the link for its own Sims.</summary>
        public static event Action<VM, VMNetReduxMatchCmd, VMAvatar, VMAvatar, bool> Talked;
        /// <summary>Arrive: the visitor (null: they couldn't come).</summary>
        public static event Action<VM, VMNetReduxMatchCmd, VMAvatar> Arrived;
        /// <summary>AdoptBaby / AdoptChild: the new child's GUID (0 for a baby, or not adopted) and why not (null: done).</summary>
        public static event Action<VM, VMNetReduxMatchCmd, uint, string> Adopted;
        /// <summary>MoveIn: why not (null: moved in).</summary>
        public static event Action<VM, VMNetReduxMatchCmd, string> MovedIn;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                var a = vm.GetObjectById(A) as VMAvatar;
                var b = vm.GetObjectById(B) as VMAvatar;
                switch (Mode)
                {
                    case MatchMode.SetTurnOns:
                        if (a == null || a.IsPet) return false;
                        VMReduxMatch.Set(a, T0, T1, T2);
                        return true;
                    case MatchMode.Close:
                        {
                            var why = (a == null || b == null) ? "" : VMReduxMatch.DoClose(vm, a, b, (VMReduxMatch.Close)Kind);
                            Fire(Closed, vm, why);
                            return why == null;
                        }
                    case MatchMode.Ask: Fire(Asked, vm); return true;
                    case MatchMode.Answer: Fire(Answered, vm); return true;
                    case MatchMode.LifeTalk:
                        {
                            if (a == null || b == null || a == b || !VMReduxMatch.CanAskLife(a, b)) return false;
                            bool agreed;
                            if (Yes) agreed = true;
                            else if (No) agreed = false;
                            else agreed = (int)vm.Context.NextRandom(100) < VMReduxMatch.LifeChance(a, b);
                            VMReduxMatch.LifeTalk(vm, a, b, agreed);
                            try { Talked?.Invoke(vm, this, a, b, agreed); } catch (Exception e) { Console.WriteLine("[SimsRedux] match: " + e.Message); }
                            return true;
                        }
                    case MatchMode.Arrive:
                        {
                            var ava = VMReduxMatch.Arrive(vm, GUID, Anchor, a, Kind == 0);
                            try { Arrived?.Invoke(vm, this, ava); } catch (Exception e) { Console.WriteLine("[SimsRedux] match: " + e.Message); }
                            return ava != null;
                        }
                    case MatchMode.AdoptBaby:
                        {
                            var why = VMReduxMatch.AdoptBaby(vm, a, b, Name);
                            FireAdopted(vm, 0, why);
                            return why == null;
                        }
                    case MatchMode.AdoptChild:
                        {
                            string why;
                            var guid = VMReduxMatch.AdoptChild(vm, a, Female, Skin, Name, out why);
                            FireAdopted(vm, guid, why);
                            return guid != 0;
                        }
                    case MatchMode.MoveIn:
                        {
                            var why = VMReduxMatch.MoveIn(vm, a, b);
                            try { MovedIn?.Invoke(vm, this, why); } catch (Exception e) { Console.WriteLine("[SimsRedux] match: " + e.Message); }
                            return why == null;
                        }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] match command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private void Fire(Action<VM, VMNetReduxMatchCmd> ev, VM vm)
        {
            try { ev?.Invoke(vm, this); } catch (Exception e) { Console.WriteLine("[SimsRedux] match: " + e.Message); }
        }

        private void Fire(Action<VM, VMNetReduxMatchCmd, string> ev, VM vm, string why)
        {
            try { ev?.Invoke(vm, this, why); } catch (Exception e) { Console.WriteLine("[SimsRedux] match: " + e.Message); }
        }

        private void FireAdopted(VM vm, uint guid, string why)
        {
            try { Adopted?.Invoke(vm, this, guid, why); } catch (Exception e) { Console.WriteLine("[SimsRedux] match: " + e.Message); }
        }

        /// <summary>
        /// A visiting player's asks, on the lot's game only (Verify runs there, before a command goes into the lot's ticks -
        /// so dropping one keeps every game in step): one open ask per asking Sim (until answered, or AskOpenFor), and at
        /// most one every AskEvery from each player.
        /// </summary>
        private static readonly Dictionary<uint, DateTime> LastAsk = new Dictionary<uint, DateTime>();
        private static readonly Dictionary<short, DateTime> OpenAsk = new Dictionary<short, DateTime>();
        private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(10), AskOpenFor = TimeSpan.FromMinutes(2);

        private bool AskAllowed(VMAvatar caller)
        {
            var now = DateTime.UtcNow;
            lock (OpenAsk)
            {
                DateTime t;
                if (OpenAsk.TryGetValue(A, out t) && now - t < AskOpenFor) return false;
                if (LastAsk.TryGetValue(caller.PersistID, out t) && now - t < AskEvery) return false;
                if (LastAsk.Count > 200) LastAsk.Clear();
                if (OpenAsk.Count > 200) OpenAsk.Clear();
                LastAsk[caller.PersistID] = now;
                OpenAsk[A] = now;
                return true;
            }
        }

        /// <summary>17.0: a visiting player's answers and turn-on updates, at most one every few seconds each.</summary>
        private static readonly Dictionary<uint, DateTime> LastAnswer = new Dictionary<uint, DateTime>(), LastTurnOns = new Dictionary<uint, DateTime>();
        private static readonly TimeSpan AnswerEvery = TimeSpan.FromSeconds(2), TurnOnsEvery = TimeSpan.FromSeconds(2);

        private static bool Every(Dictionary<uint, DateTime> last, VMAvatar caller, TimeSpan every)
        {
            var now = DateTime.UtcNow;
            lock (last)
            {
                DateTime t;
                if (last.TryGetValue(caller.PersistID, out t) && now - t < every) return false;
                if (last.Count > 200) last.Clear();
                last[caller.PersistID] = now;
                return true;
            }
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!FromNet)
            {
                if (Mode == MatchMode.Answer) lock (OpenAsk) OpenAsk.Remove(A);
                return true; // (this game runs the lot)
            }
            // a visiting player: only their own Sim, and never the lot's family, money or visitors
            if (caller == null || caller.IsPet) return false;
            switch (Mode)
            {
                case MatchMode.SetTurnOns: return A == caller.ObjectID && Every(LastTurnOns, caller, TurnOnsEvery);
                case MatchMode.Close:
                    {
                        if (A != caller.ObjectID || A == B) return false;
                        if (caller.Thread == null || caller.Thread.Queue.Count >= FSO.SimAntics.Engine.VMThread.MAX_USER_ACTIONS) return false;
                        var b = vm.GetObjectById(B) as VMAvatar;
                        if (b == null || b.Dead) return false;
                        return VMReduxMatch.CloseWait(vm, caller) == 0;
                    }
                case MatchMode.Ask:
                    {
                        // (17.0: both grown-ups - no ask dialog ever reaches a child)
                        if (A != caller.ObjectID || A == B) return false;
                        var b = vm.GetObjectById(B) as VMAvatar;
                        if (!ReduxWooHoo.Grownup(caller) || !ReduxWooHoo.Grownup(b)) return false;
                        return AskAllowed(caller);
                    }
                case MatchMode.Answer:
                    {
                        if (B != caller.ObjectID || !Every(LastAnswer, caller, AnswerEvery)) return false;
                        lock (OpenAsk) OpenAsk.Remove(A);
                        return true;
                    }
            }
            return false;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(A);
            writer.Write(B);
            writer.Write(Anchor);
            writer.Write(T0);
            writer.Write(T1);
            writer.Write(T2);
            writer.Write(Kind);
            writer.Write(Skin);
            writer.Write(Yes);
            writer.Write(No);
            writer.Write(Female);
            writer.Write(GUID);
            writer.Write(VMReduxMatch.CleanName(Name));
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (MatchMode)reader.ReadByte();
            A = reader.ReadInt16();
            B = reader.ReadInt16();
            Anchor = reader.ReadInt16();
            T0 = reader.ReadByte();
            T1 = reader.ReadByte();
            T2 = reader.ReadByte();
            Kind = reader.ReadByte();
            Skin = reader.ReadByte();
            Yes = reader.ReadBoolean();
            No = reader.ReadBoolean();
            Female = reader.ReadBoolean();
            GUID = reader.ReadUInt32();
            Name = VMReduxMatch.CleanName(reader.ReadString());
        }
    }
}
