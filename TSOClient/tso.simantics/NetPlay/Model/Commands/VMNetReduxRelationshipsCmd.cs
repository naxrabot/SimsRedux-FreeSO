using System;
using System.Collections.Generic;
using System.IO;
using FSO.LotView.Model;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;
using Stage = FSO.SimAntics.Utils.VMReduxRelationships.Stage;
using Rels = FSO.SimAntics.Utils.VMReduxRelationships;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.0 (Part B #1b extras, R1): relationship stages, boundaries, breakups, weddings and anniversaries
    /// (Utils/ReduxRelationships.cs). Everything that changes the lot for them goes through this command (VM command 77), so
    /// on a visit every player's game does the same at the same moment.
    ///   SetState    - a Sim's partner (B, a neighbour id), stage and boundaries (Value) and heartbroken-until day (C) from
    ///                 the game that keeps the Sim's side file: the lot's game for its own family and townies, a visiting
    ///                 player's game for their own Sim only.
    ///   Ask         - asking another player's Sim (Act): changes nothing, that player's screen asks them (Asked). A guest
    ///                 only for their own Sim (A), one at a time.
    ///   Answer      - that player's answer (Yes): changes nothing (Answered). A guest only for their own Sim (B).
    ///   Act         - A asks B (Act: go steady, propose, break up, divorce, get back together, agree on limits Value).
    ///                 Yes: B's player already agreed, On: B's player said no; otherwise the lot works out B's answer with
    ///                 its own random numbers. Only the game running the lot sends it.
    ///   Cheated     - the lot's game saw A's romance (Value: VMReduxRelationships.Cheat) with B, and it counts as
    ///                 cheating to A's partner C (On: on C's home lot): C is hurt.
    ///   Ceremony    - A and B's wedding (Value: Stage Married), commitment ceremony (Committed) or life partners ceremony
    ///                 (LifePartners): the family pays Cost, the cake (Cake) goes down, everyone gathers, rings (Rings 0 none,
    ///                 1 simple, 2 fancy).
    ///   Anniversary - A (and B, if here) have an anniversary today (Value: how many): a happy day.
    ///   MoveOut     - A leaves the family (a townie from now on) and walks off.
    ///   Honeymoon   - A and B on their honeymoon: Value 1 a toast, 2 watching the sunset, 3 a day of it.
    /// Only SetState, Ask and Answer can come from a visiting player (for their own Sim); the rest only from the lot's game.
    /// </summary>
    public class VMNetReduxRelationshipsCmd : VMNetCommandBodyAbstract
    {
        public enum RelMode : byte
        {
            SetState = 0, Ask = 1, Answer = 2, Act = 3, Cheated = 4, Ceremony = 5, Anniversary = 6, MoveOut = 7, Honeymoon = 9, // (8: not used - moving in is VMNetReduxMatchCmd's)
        }

        /// <summary>What one Sim asks of another (Act, Ask).</summary>
        public enum Acts : byte { None = 0, GoSteady = 1, Propose = 2, BreakUp = 3, Divorce = 4, MakeUp = 5, Limits = 6 }

        public RelMode Mode;
        public short A, B, C, Value;
        public Acts Act;
        public byte Rings;
        public bool Cake, Yes, On;
        public int Cost;

        /// <summary>(Not sent.) Set while it runs: did B agree (Act)?</summary>
        public bool Agreed;

        /// <summary>Ask, Answer: on every game (the screen asks / tells the right player).</summary>
        public static event Action<VM, VMNetReduxRelationshipsCmd> Asked, Answered;
        /// <summary>Everything else that happened (why: null done, "" not done - say nothing, otherwise why not), on every game.</summary>
        public static event Action<VM, VMNetReduxRelationshipsCmd, VMAvatar, VMAvatar, string> Done;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (!vm.TS1) return false;
            try
            {
                switch (Mode)
                {
                    case RelMode.SetState: return SetState(vm);
                    case RelMode.Ask: Fire(Asked, vm); return true;
                    case RelMode.Answer: Fire(Answered, vm); return true;
                    case RelMode.Act: return DoAct(vm);
                    case RelMode.Cheated: return DoCheated(vm);
                    case RelMode.Ceremony: return DoCeremony(vm);
                    case RelMode.Anniversary: return DoAnniversary(vm);
                    case RelMode.MoveOut:
                        {
                            var a = Sim(vm, A);
                            if (a == null) return Tell(vm, null, null, "");
                            var why = Rels.CantMoveOut(vm, a);
                            if (why != null) return Tell(vm, a, null, why);
                            return Rels.MoveOut(vm, a) ? Tell(vm, a, null, null) : Tell(vm, a, null, "");
                        }
                    case RelMode.Honeymoon: return DoHoneymoon(vm);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] relationships command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        private static VMAvatar Sim(VM vm, short id)
        {
            var a = vm.GetObjectById(id) as VMAvatar;
            return (a == null || a.IsPet || a.Dead) ? null : a;
        }

        private void Fire(Action<VM, VMNetReduxRelationshipsCmd> ev, VM vm)
        {
            try { ev?.Invoke(vm, this); } catch (Exception e) { Console.WriteLine("[SimsRedux] relationships: " + e.Message); }
        }

        private bool Tell(VM vm, VMAvatar a, VMAvatar b, string why)
        {
            try { Done?.Invoke(vm, this, a, b, why); } catch (Exception e) { Console.WriteLine("[SimsRedux] relationships: " + e.Message); }
            return why == null;
        }

        private static int TicksPerMinute(VM vm) { return Math.Max(1, vm.Context.Clock.TicksPerMinute); }

        // ------------------------------------------------------------------ SetState

        private bool SetState(VM vm)
        {
            var sim = Sim(vm, A);
            if (sim == null) return false;
            if (sim.IsChild)
            {
                // (17.0: a child never has a partner, a stage, romantic boundaries or a broken heart - only the "on" bit)
                sim.SetAttribute(Rels.PartnerAttr, 0);
                sim.SetAttribute(Rels.StateAttr, (short)(Value & Rels.OnBit));
                sim.SetAttribute(Rels.HeartAttr, 0);
                return true;
            }
            sim.SetAttribute(Rels.PartnerAttr, (short)Math.Max(0, (int)B));
            sim.SetAttribute(Rels.StateAttr, (short)(Value & Rels.OwnerBits));
            sim.SetAttribute(Rels.HeartAttr, (short)Math.Max(0, (int)C));
            return true;
        }

        // ------------------------------------------------------------------ asking (Act)

        private static int Num(List<short> r, int i) { return (r != null && r.Count > i) ? r[i] : 0; }

        /// <summary>The chance (0-100) that b says yes to a for this - from how b feels about a, b's partner and ways.</summary>
        public static int Chance(VMAvatar a, VMAvatar b, Acts act, int limits = 0)
        {
            if (a == null || b == null) return 0;
            var r = ReduxWooHoo.Rel(b, a);
            int daily = Num(r, 0), life = Num(r, 2);
            bool love = Num(r, 1) == 1 || Num(r, 3) == 1;
            int c;
            switch (act)
            {
                case Acts.GoSteady:
                    if (!ReduxAttraction.Mutual(a, b)) return 0;
                    c = (love ? 55 : 20) + life / 3 + daily / 5;
                    if (Rels.Taken(b, a) && (Rels.Boundaries(b) & Rels.BOkOthers) == 0) c = 5; // (they're with someone)
                    break;
                case Acts.Propose:
                    if (!ReduxAttraction.Mutual(a, b)) return 0;
                    c = 25 + life / 2 + daily / 5 + (love ? 10 : 0);
                    break;
                case Acts.MakeUp:
                    if (!ReduxAttraction.Mutual(a, b)) return 0;
                    c = 15 + life / 2 + daily / 4;
                    if (Rels.Taken(b, a)) c = 5;
                    break;
                case Acts.Limits:
                    c = 50 + life / 3 + (VMReduxTraits.Has(b, VMReduxTraits.T.Jealous) || VMReduxTraits.Has(b, VMReduxTraits.T.Loyal) ? 25 : 0);
                    break;
                default: return 100;
            }
            if (daily < -20) c /= 2;
            if (VMReduxTraits.Has(b, VMReduxTraits.T.Romantic) && act != Acts.Limits) c += 10;
            return Math.Max(5, Math.Min(95, c));
        }

        private bool DoAct(VM vm)
        {
            var a = Sim(vm, A);
            var b = Sim(vm, B);
            Agreed = false;
            if (a == null || b == null || a == b || Act == Acts.None || Act > Acts.Limits) return Tell(vm, a, b, "");
            if (!ReduxWooHoo.Grownup(a) || !ReduxWooHoo.Grownup(b)) return Tell(vm, a, b, "");
            var together = Rels.Together(a, b);
            var today = Rels.Today(vm);
            int perMinute = TicksPerMinute(vm);

            switch (Act)
            {
                case Acts.BreakUp:
                case Acts.Divorce:
                    {
                        // (17.0: only a couple can break up - partners on the lot, or in love both ways)
                        if (together == Stage.None && !ReduxWooHoo.InLove(a, b)) return Tell(vm, a, b, "");
                        // (no asking - it's over. The one left behind takes it harder.)
                        ReduxWooHoo.PushWait(vm, a, b, perMinute * 3, Act == Acts.Divorce ? "Ask for a Divorce" : "Break Up");
                        Rels.Unlink(a, b);
                        Rels.SetLove(a, b, false);
                        Rels.Closer(b, a, -50, -25);
                        Rels.Closer(a, b, -25, -10);
                        b.SetAttribute(Rels.HeartAttr, (short)Math.Min(32000, today + 3));
                        a.SetAttribute(Rels.HeartAttr, (short)Math.Max((int)a.GetAttribute(Rels.HeartAttr), Math.Min(32000, today + 1)));
                        Rels.Motive(b, VMMotive.Social, -40);
                        Rels.Motive(b, VMMotive.Fun, -30);
                        Rels.Motive(a, VMMotive.Social, -15);
                        Agreed = true;
                        return Tell(vm, a, b, null);
                    }
                case Acts.GoSteady:
                    if (together != Stage.None || Rels.PartnerOf(a) != 0) return Tell(vm, a, b, "");
                    break;
                case Acts.Propose:
                    if (together != Stage.Steady && together != Stage.Committed) return Tell(vm, a, b, "");
                    break;
                case Acts.MakeUp:
                    if (together != Stage.None || Rels.PartnerOf(a) != 0) return Tell(vm, a, b, "");
                    break;
                case Acts.Limits:
                    if (!Rels.On(a) || !Rels.On(b) || (Value & Rels.BoundaryBits) == 0) return Tell(vm, a, b, "");
                    break;
            }

            bool agreed;
            if (Act != Acts.Limits && !ReduxAttraction.Mutual(a, b)) agreed = false;
            else if (Yes) agreed = true;  // (B's player said yes)
            else if (On) agreed = false;  // (B's player said no)
            else agreed = (int)vm.Context.NextRandom(100) < Chance(a, b, Act, Value);
            Agreed = agreed;

            string talk;
            switch (Act)
            {
                case Acts.GoSteady: talk = "Ask to Go Steady"; break;
                case Acts.Propose: talk = "Propose"; break;
                case Acts.MakeUp: talk = "Ask to Get Back Together"; break;
                default: talk = "Talk About Our Relationship"; break;
            }
            ReduxWooHoo.PushWait(vm, a, b, perMinute * 2, talk);

            if (!agreed)
            {
                Rels.Closer(b, a, -8, -2);
                Rels.Closer(a, b, Act == Acts.Propose ? -20 : -10, Act == Acts.Propose ? -6 : -3);
                return Tell(vm, a, b, null);
            }
            switch (Act)
            {
                case Acts.GoSteady:
                    Rels.Link(a, b, Stage.Steady);
                    Rels.SetLove(a, b, true);
                    Rels.Closer(a, b, 10, 5);
                    Rels.Closer(b, a, 10, 5);
                    break;
                case Acts.Propose:
                    Rels.Link(a, b, Stage.Engaged); // (Committed Partners who get engaged marry later)
                    Rels.SetLove(a, b, true);
                    Rels.Closer(a, b, 15, 8);
                    Rels.Closer(b, a, 15, 8);
                    a.SetAttribute(Rels.CelebrateAttr, today);
                    b.SetAttribute(Rels.CelebrateAttr, today);
                    Rels.Motive(a, VMMotive.Fun, 20);
                    Rels.Motive(b, VMMotive.Fun, 20);
                    break;
                case Acts.MakeUp:
                    Rels.Link(a, b, Stage.Steady);
                    Rels.SetLove(a, b, true);
                    Rels.Closer(a, b, 20, 10);
                    Rels.Closer(b, a, 20, 10);
                    a.SetAttribute(Rels.HeartAttr, 0);
                    b.SetAttribute(Rels.HeartAttr, 0);
                    break;
                case Acts.Limits:
                    foreach (var s in new[] { a, b })
                    {
                        int st = s.GetAttribute(Rels.StateAttr);
                        int bits = Rels.Boundaries(s) | (Value & (Rels.BFlirt | Rels.BKiss | Rels.BWooHoo | Rels.BHome));
                        s.SetAttribute(Rels.StateAttr, (short)((st & ~Rels.BoundaryBits) | Rels.BCustom | bits));
                    }
                    Rels.Closer(a, b, 5, 2);
                    Rels.Closer(b, a, 5, 2);
                    break;
            }
            return Tell(vm, a, b, null);
        }

        // ------------------------------------------------------------------ cheating

        private bool DoCheated(VM vm)
        {
            var a = Sim(vm, A);
            var b = Sim(vm, B);
            var c = Sim(vm, C);
            var kind = (Rels.Cheat)Math.Max(0, Math.Min(3, (int)Value));
            if (a == null || b == null || c == null || a == b || a == c || b == c || kind == Rels.Cheat.None) return Tell(vm, a, b, "");
            bool partners = Rels.PartnerOf(c) == Rels.Nid(a) || ReduxWooHoo.InLove(c, a);
            if (!partners || !Rels.Counts(c, kind, On)) return Tell(vm, a, b, "");
            Rels.Closer(c, a, kind == Rels.Cheat.WooHoo ? -35 : -25, kind == Rels.Cheat.Flirt ? -6 : -12);
            Rels.Closer(a, c, -5, -2);
            Rels.Closer(c, b, -15, -5);
            c.SetAttribute(Rels.HurtAttr, Rels.Today(vm));
            return Tell(vm, a, b, null);
        }

        // ------------------------------------------------------------------ weddings and ceremonies

        private bool DoCeremony(VM vm)
        {
            var a = Sim(vm, A);
            var b = Sim(vm, B);
            var stage = (Stage)Math.Max(0, Math.Min(5, (int)Value));
            if (a == null || b == null || a == b || (stage != Stage.Married && stage != Stage.Committed && stage != Stage.LifePartners)) return Tell(vm, a, b, "");
            if (!ReduxWooHoo.Grownup(a) || !ReduxWooHoo.Grownup(b)) return Tell(vm, a, b, "");
            if (stage != Stage.LifePartners && !ReduxAttraction.Mutual(a, b)) return Tell(vm, a, b, "They aren't attracted to each other.");
            // (17.0: only a couple - engaged, partners or in love for a wedding or commitment ceremony; life partners for theirs)
            var now = Rels.Together(a, b);
            if (stage == Stage.LifePartners)
            {
                if (now != Stage.LifePartners && !VMReduxMatch.AroAce(a) && !VMReduxMatch.AroAce(b)) return Tell(vm, a, b, "They need to be life partners first.");
            }
            else if ((now == Stage.None || now == Stage.LifePartners) && !ReduxWooHoo.InLove(a, b)) return Tell(vm, a, b, "They need to be a couple (engaged, or in love) first.");
            var fam = Rels.Family(vm);
            int cost = Math.Max(0, Math.Min(100000, Cost));
            if (cost > 0)
            {
                if (fam == null) return Tell(vm, a, b, "There's no family here to pay for it.");
                if (fam.Budget < cost) return Tell(vm, a, b, "The family doesn't have §" + cost.ToString("N0") + " for it.");
                fam.Budget -= cost;
            }
            var today = Rels.Today(vm);
            int perMinute = TicksPerMinute(vm);
            Rels.Link(a, b, stage);
            if (stage != Stage.LifePartners) Rels.SetLove(a, b, true);
            foreach (var pair in new[] { new[] { a, b }, new[] { b, a } })
            {
                var s = pair[0];
                s.SetAttribute(Rels.CelebrateAttr, today);
                s.SetAttribute(Rels.HeartAttr, 0);
                Rels.Closer(s, pair[1], 20, 10);
                Rels.Motive(s, VMMotive.Fun, 30);
                Rels.Motive(s, VMMotive.Social, 40);
                ReduxWooHoo.PushWait(vm, s, pair[1], perMinute * 30, stage == Stage.Married ? "Say Our Vows" : "Make Our Promises");
                if (Rings > 0) ReduxWooHoo.PushWait(vm, s, pair[1], perMinute * 10, "Exchange Rings");
            }
            if (Cake) Rels.PlaceCake(vm, a);
            // everyone else here gathers round to watch (and has a good time)
            string watch = stage == Stage.Married ? "Watch the Wedding" : "Watch the Ceremony";
            foreach (var e in vm.Context.ObjectQueries.Avatars)
            {
                var g = e as VMAvatar;
                if (g == null || g == a || g == b || g.IsPet || g.Dead || g.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (g.GetPersonData(VMPersonDataVariable.PersonType) >= 2 || g.GetPersonData(VMPersonDataVariable.IsGhost) != 0) continue;
                if (ReduxWooHoo.Busy(g)) continue;
                ReduxWooHoo.PushWait(vm, g, a, perMinute * 45, watch);
                Rels.Motive(g, VMMotive.Social, 20);
                Rels.Motive(g, VMMotive.Fun, 15);
            }
            return Tell(vm, a, b, null);
        }

        private bool DoAnniversary(VM vm)
        {
            var a = Sim(vm, A);
            var b = Sim(vm, B);
            if (a == null) return Tell(vm, a, b, "");
            var today = Rels.Today(vm);
            foreach (var s in new[] { a, b })
            {
                if (s == null) continue;
                s.SetAttribute(Rels.CelebrateAttr, today);
                Rels.Motive(s, VMMotive.Fun, 25);
                Rels.Motive(s, VMMotive.Social, 20);
            }
            if (b != null)
            {
                Rels.Closer(a, b, 5, 3);
                Rels.Closer(b, a, 5, 3);
            }
            return Tell(vm, a, b, null);
        }

        private bool DoHoneymoon(VM vm)
        {
            var a = Sim(vm, A);
            var b = Sim(vm, B);
            if (a == null || b == null || a == b || !Rels.OnHoneymoon(a)) return Tell(vm, a, b, "");
            int perMinute = TicksPerMinute(vm);
            switch (Value)
            {
                case 1:
                    ReduxWooHoo.PushWait(vm, a, b, perMinute * 20, "Toast to Us");
                    ReduxWooHoo.PushWait(vm, b, a, perMinute * 20, "Toast to Us");
                    foreach (var s in new[] { a, b }) { Rels.Motive(s, VMMotive.Fun, 20); Rels.Motive(s, VMMotive.Social, 25); }
                    break;
                case 2:
                    ReduxWooHoo.PushWait(vm, a, b, perMinute * 30, "Watch the Sunset Together");
                    ReduxWooHoo.PushWait(vm, b, a, perMinute * 30, "Watch the Sunset Together");
                    foreach (var s in new[] { a, b }) { Rels.Motive(s, VMMotive.Comfort, 15); Rels.Motive(s, VMMotive.Fun, 20); }
                    break;
                case 3:
                    a.SetAttribute(Rels.CelebrateAttr, Rels.Today(vm));
                    b.SetAttribute(Rels.CelebrateAttr, Rels.Today(vm));
                    break;
                default: return Tell(vm, a, b, "");
            }
            Rels.Closer(a, b, 8, 4);
            Rels.Closer(b, a, 8, 4);
            return Tell(vm, a, b, null);
        }

        // ------------------------------------------------------------------ who may send what

        /// <summary>
        /// A visiting player's asks, on the lot's game only (Verify runs there before a command goes into the lot's ticks):
        /// one open ask per asking Sim (until it's answered, or AskOpenFor), and at most one every AskEvery from each player.
        /// </summary>
        private static readonly Dictionary<uint, DateTime> LastAsk = new Dictionary<uint, DateTime>();
        private static readonly Dictionary<short, DateTime> OpenAsk = new Dictionary<short, DateTime>();
        private static readonly Dictionary<uint, DateTime> LastState = new Dictionary<uint, DateTime>();
        private static readonly TimeSpan AskEvery = TimeSpan.FromSeconds(10), AskOpenFor = TimeSpan.FromMinutes(2), StateEvery = TimeSpan.FromSeconds(1);

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

        private bool StateAllowed(VMAvatar caller)
        {
            var now = DateTime.UtcNow;
            lock (LastState)
            {
                DateTime t;
                if (LastState.TryGetValue(caller.PersistID, out t) && now - t < StateEvery) return false;
                if (LastState.Count > 200) LastState.Clear();
                LastState[caller.PersistID] = now;
                return true;
            }
        }

        /// <summary>17.0: a visiting player's answers, at most one every AnswerEvery.</summary>
        private static readonly Dictionary<uint, DateTime> LastAnswer = new Dictionary<uint, DateTime>();
        private static readonly TimeSpan AnswerEvery = TimeSpan.FromSeconds(2);

        private bool AnswerAllowed(VMAvatar caller)
        {
            var now = DateTime.UtcNow;
            lock (LastAnswer)
            {
                DateTime t;
                if (LastAnswer.TryGetValue(caller.PersistID, out t) && now - t < AnswerEvery) return false;
                if (LastAnswer.Count > 200) LastAnswer.Clear();
                LastAnswer[caller.PersistID] = now;
                return true;
            }
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            if (!FromNet)
            {
                if (Mode == RelMode.Answer) lock (OpenAsk) OpenAsk.Remove(A);
                return true; // (this game runs the lot)
            }
            // a visiting player: only their own Sim
            if (caller == null || caller.IsPet) return false;
            switch (Mode)
            {
                case RelMode.SetState: return A == caller.ObjectID && StateAllowed(caller);
                case RelMode.Ask:
                    {
                        if (A != caller.ObjectID || A == B || Act <= Acts.None || Act > Acts.Limits) return false;
                        // (17.0: both grown-ups - no ask ever reaches a child)
                        if (!ReduxWooHoo.Grownup(caller) || !ReduxWooHoo.Grownup(Sim(vm, B))) return false;
                        return AskAllowed(caller);
                    }
                case RelMode.Answer:
                    {
                        if (B != caller.ObjectID || !AnswerAllowed(caller)) return false;
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
            writer.Write(C);
            writer.Write(Value);
            writer.Write((byte)Act);
            writer.Write(Rings);
            writer.Write(Cake);
            writer.Write(Yes);
            writer.Write(On);
            writer.Write(Cost);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (RelMode)reader.ReadByte();
            A = reader.ReadInt16();
            B = reader.ReadInt16();
            C = reader.ReadInt16();
            Value = reader.ReadInt16();
            Act = (Acts)reader.ReadByte();
            Rings = reader.ReadByte();
            Cake = reader.ReadBoolean();
            Yes = reader.ReadBoolean();
            On = reader.ReadBoolean();
            Cost = reader.ReadInt32();
        }
    }
}
