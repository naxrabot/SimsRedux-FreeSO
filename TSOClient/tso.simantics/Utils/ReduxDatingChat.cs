using System;
using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 19.4: chatting on the Dating App is a real chat. Joey (Oct 30): "When chatting via the dating app, let's have
    /// it so it's the same like chatting online where they type out and it's a process and the interaction is as if they
    /// were talking in person about talking about interests and stuff (not that you pick that, but how it plays out)
    /// instead of it just being a click and instant". Tapping Chat (VMNetReduxMatchCmd Chat, the lot's game only) puts
    /// "Chat on the Dating App" in the Sim's queue: at the home computer (the computer's own "Play", the same sitting and
    /// typing as Shop Online - VMReduxShop) or, from the cellphone, where they stand, with the phone out. While it's their
    /// active interaction they type for ChatMinutes game minutes (a progress bar over their head); cancelled like any other
    /// interaction. The conversation itself is decided when it starts, like an in-person Talk: four exchanges, each Sim in
    /// turn raising one of the Sims 1 interests (Travel, Violence, Politics, The 60s, Weather, Sports, Music, Outdoors - the
    /// ones they care about most come up most), the other reacting by how much they care about it (loves it / polite /
    /// not their thing), nudged by their chemistry and the listener's Nice - with the lot's random numbers. How it went
    /// decides the relationship (daily and lifetime, both ways - VMReduxMatch.HoodChat, the game's own relationship lists):
    /// lots in common and good chemistry give the most, clashing gives little or a little less. When they finish (or are
    /// stopped part way - the exchanges done so far count, lifetime only for a whole chat) Done tells the games; the
    /// Dating App window shows the chat as it's typed, from the attributes below, and the result.
    ///
    /// LOCKSTEP: started only from the command, finished from VMAvatar's tick (ReduxComputer.Tick); only lot state - the
    /// Sim's queue, the clock, the lot's random numbers, the Sim's own person data and these person attributes (saved with
    /// the lot and sent to anyone joining, so a reload or a visitor arriving mid-chat changes nothing). The match's
    /// interests and chemistry come in the command from the game that sent it, so every game decides the same chat.
    ///   130 StateAttr   where (1 phone, 2 computer) | nth chat today &lt;&lt; 2 | (daily + 16) &lt;&lt; 5 | (lifetime + 4) &lt;&lt; 10
    ///   131 GuidLoAttr  the match's GUID, low 16 bits
    ///   132 GuidHiAttr  ... high 16 bits
    ///   133 TypedAttr   ticks typed so far
    ///   134 TalkAttr    exchanges 1-2 (8 bits each: topic 0-7 | reaction &lt;&lt; 3 | 0x80)
    ///   135 Talk2Attr   exchanges 3-4
    /// (105-109 the cellphone and Shop Online, 110-115 multitasking and the player pause, 120-129 the brain.)
    /// </summary>
    public static class VMReduxDatingChat
    {
        public const int StateAttr = 130, GuidLoAttr = 131, GuidHiAttr = 132, TypedAttr = 133, TalkAttr = 134, Talk2Attr = 135;
        /// <summary>The chat in the Sim's queue.</summary>
        public const string ActionName = "Chat on the Dating App";
        /// <summary>Game minutes typing (about 20 seconds at normal speed).</summary>
        public const int ChatMinutes = 20;
        public const int Exchanges = 4, Topics = 8;
        public const int WherePhone = 1, WhereComputer = 2;
        /// <summary>How the listener took a topic.</summary>
        public enum React { Clash = 0, Polite = 1, Love = 2 }

        public class Result
        {
            public uint Guid;
            /// <summary>What it did to the relationship (both ways), and the exchanges that were had (0-4).</summary>
            public int Daily, Life, Done, Nth;
            /// <summary>Stopped before the end (cancelled or interrupted).</summary>
            public bool Stopped, Phone;
            /// <summary>The conversation (Decode).</summary>
            public short Talk, Talk2;
            /// <summary>The match is in this game's neighbourhood and the relationship was written.</summary>
            public bool Written;
        }

        /// <summary>A chat ended, on every game at the same tick (only the Sim's own game shows it).</summary>
        public static event Action<VM, VMAvatar, Result> Done;

        public static bool Chatting(VMAvatar sim) { return sim != null && sim.GetAttribute(StateAttr) != 0; }

        public static uint GuidOf(VMAvatar sim)
        {
            if (sim == null) return 0;
            return (uint)(ushort)sim.GetAttribute(GuidLoAttr) | ((uint)(ushort)sim.GetAttribute(GuidHiAttr) << 16);
        }

        public static bool OnPhone(VMAvatar sim) { return sim != null && (sim.GetAttribute(StateAttr) & 3) == WherePhone; }

        /// <summary>How far through the chat (0-100) - drawing only.</summary>
        public static int Progress(VM vm, VMAvatar sim)
        {
            if (vm == null || !Chatting(sim)) return 0;
            int need = ChatMinutes * Math.Max(1, vm.Context.Clock.TicksPerMinute);
            return Math.Max(0, Math.Min(100, sim.GetAttribute(TypedAttr) * 100 / need));
        }

        /// <summary>The k-th exchange (0-3) of a conversation: false if there isn't one. Even k: my Sim speaks.</summary>
        public static bool Decode(short talk, short talk2, int k, out int topic, out React react)
        {
            topic = 0; react = React.Polite;
            if (k < 0 || k >= Exchanges) return false;
            int word = (ushort)(k < 2 ? talk : talk2);
            int b = (k % 2 == 0) ? (word & 0xFF) : ((word >> 8) & 0xFF);
            if ((b & 0x80) == 0) return false;
            topic = b & 7;
            react = (React)Math.Min(2, (b >> 3) & 3);
            return true;
        }

        private static string FirstName(VMAvatar sim)
        {
            var n = (sim?.Name ?? "").Trim();
            if (n.IndexOf(' ') > 0) n = n.Substring(0, n.IndexOf(' '));
            return n.Length == 0 ? "Your Sim" : n;
        }

        private static int Level(int v) { return Math.Max(0, Math.Min(10, v / 100)); }

        /// <summary>
        /// The Sim starts a chat with the match (the command's part). null: queued; otherwise why not (plain English, ""
        /// for nothing to say). computer: the home computer, or null for the cellphone. theirs: the match's 8 interests
        /// (0-10), theirNice: their Nice (0-10).
        /// </summary>
        public static string Start(VM vm, VMAvatar sim, VMEntity computer, uint guid, int nth, int chem, int[] theirs, int theirNice)
        {
            if (vm == null || !vm.TS1 || sim?.Thread == null || sim.Dead || guid == 0 || theirs == null || theirs.Length < Topics) return "";
            if (!ReduxWooHoo.Grownup(sim)) return "";
            if (Chatting(sim)) return FirstName(sim) + " is already chatting on the Dating App.";
            if (VMReduxShop.Shopping(sim)) return FirstName(sim) + " is busy ordering something on the computer.";
            // (on the lot already: by GUID, which every game has - not the neighbourhood, which only this one has)
            var avatars = vm.Context.ObjectQueries.Avatars;
            for (int i = 0; i < avatars.Count; i++)
            {
                var x = avatars[i] as VMAvatar;
                if (x != null && !x.Dead && x != sim && x.Object?.OBJ != null && x.Object.OBJ.GUID == guid)
                    return "They're right here on the lot - go and say hello in person!";
            }

            // the interaction: at the computer (its own "Play", sitting and typing), or standing with the cellphone
            bool queued = false;
            int where = WherePhone;
            if (computer != null)
            {
                if (computer.Dead || computer is VMAvatar) return "";
                where = WhereComputer;
                var target = computer.MultitileGroup?.GetInteractionGroupLeader(computer) ?? computer;
                uint play;
                if (ReduxComputer.PlayOf(target, out play))
                {
                    var action = target.GetAction((int)play, sim, vm.Context, false, new short[4]);
                    if (action != null)
                    {
                        action.Name = ActionName;
                        action.Priority = (short)VMQueuePriority.UserDriven;
                        sim.Thread.EnqueueAction(action);
                        queued = true;
                    }
                }
                if (!queued) queued = ReduxWooHoo.PushWait(vm, sim, target, 30000, ActionName);
            }
            else queued = PushPhone(sim);
            if (!queued) return "";

            // the conversation, decided now
            chem = Math.Max(0, Math.Min(100, chem));
            var mine = new int[Topics];
            for (int i = 0; i < Topics; i++) mine[i] = Level(sim.GetPersonData((VMPersonDataVariable)((int)VMPersonDataVariable.Interest_TravelToys + i)));
            var them = new int[Topics];
            for (int i = 0; i < Topics; i++) them[i] = Math.Max(0, Math.Min(10, theirs[i]));
            int myNice = Level(sim.GetPersonData(VMPersonDataVariable.NicePersonality));
            theirNice = Math.Max(0, Math.Min(10, theirNice));
            int used = 0, score = 0;
            var codes = new int[Exchanges];
            for (int k = 0; k < Exchanges; k++)
            {
                var speaker = (k % 2 == 0) ? mine : them;
                var listener = (k % 2 == 0) ? them : mine;
                int listenerNice = (k % 2 == 0) ? theirNice : myNice;
                // the speaker brings up what they care about (each topic once)
                int total = 0;
                for (int i = 0; i < Topics; i++) if ((used & (1 << i)) == 0) total += speaker[i] * speaker[i] + 1;
                int pick = (int)vm.Context.NextRandom((ulong)Math.Max(1, total));
                int topic = 0;
                for (int i = 0; i < Topics; i++)
                {
                    if ((used & (1 << i)) != 0) continue;
                    pick -= speaker[i] * speaker[i] + 1;
                    if (pick < 0) { topic = i; break; }
                }
                used |= 1 << topic;
                // the other takes it as much as they care about it - good chemistry and a Nice listener help
                int care = listener[topic];
                int roll = (int)vm.Context.NextRandom(100);
                React r;
                if (care >= 7) r = roll < 8 ? React.Polite : React.Love;
                else if (care >= 4) r = roll < 25 + (chem - 50) / 2 + (listenerNice - 5) * 3 ? React.Love : React.Polite;
                else r = roll < chem / 4 + listenerNice * 2 ? React.Polite : React.Clash;
                score += r == React.Love ? 3 : (r == React.Polite ? 1 : -2);
                codes[k] = topic | ((int)r << 3) | 0x80;
            }
            int mood = sim.GetMotiveData(VMMotive.Mood);
            int daily = score + (chem - 50) / 25 + (mood >= 50 ? 1 : (mood <= -50 ? -1 : 0));
            daily = Math.Max(-4, Math.Min(12, daily));
            int life = daily >= 8 ? 2 : (daily >= 3 ? 1 : (daily <= -3 ? -1 : 0));
            // (the 3rd chat of the day with the same Sim counts for about half, the 4th and on for very little)
            nth = Math.Max(1, Math.Min(7, nth));
            if (nth == VMReduxMatch.ChatsFull + 1) { if (daily > 0) daily = (daily + 1) / 2; life = Math.Min(life, 1); }
            else if (nth > VMReduxMatch.ChatsFull + 1) { if (daily > 0) daily = 1; life = Math.Min(life, 0); }

            sim.SetAttribute(StateAttr, (short)(where | (nth << 2) | ((daily + 16) << 5) | ((life + 4) << 10)));
            sim.SetAttribute(GuidLoAttr, unchecked((short)(guid & 0xFFFF)));
            sim.SetAttribute(GuidHiAttr, unchecked((short)(guid >> 16)));
            sim.SetAttribute(TypedAttr, 0);
            sim.SetAttribute(TalkAttr, unchecked((short)(codes[0] | (codes[1] << 8))));
            sim.SetAttribute(Talk2Attr, unchecked((short)(codes[2] | (codes[3] << 8))));
            VMReduxPlayerPause.Mark(vm, sim); // (the player's own choice: free will waits a while after it)
            return null;
        }

        // ---------------------------------------------------------------- the cellphone: standing where they are, typing

        /// <summary>The phone chat's script: stand where they are for Args[0] ticks, or until told to stop (no walking).</summary>
        public const ushort PhoneTreeID = 0x1F62;
        private const string PhoneLabel = "SimsRedux - Dating App Chat";
        private static readonly object Lock = new object();

        private static VMRoutine PhoneRoutine(VMEntity obj)
        {
            var res = obj?.Object?.Resource;
            if (res?.Iff == null || res.RoutineCache == null) return null;
            lock (Lock)
            {
                object cached;
                if (res.RoutineCache.TryGetValue(PhoneTreeID, out cached))
                {
                    var r = cached as VMRoutine;
                    return (r?.Chunk != null && r.Chunk.ChunkLabel == PhoneLabel) ? r : null; // (the id is the object's own)
                }
                if (res.Iff.Get<BHAV>(PhoneTreeID) != null) return null; // (taken by something else)
                var op = new byte[8];
                var bhav = new BHAV
                {
                    ChunkID = PhoneTreeID,
                    ChunkLabel = PhoneLabel,
                    ChunkType = "BHAV",
                    ChunkProcessed = true,
                    AddedByPatch = true,
                    Version = 0,
                    Type = 0,
                    Args = 4,
                    Locals = 0,
                    Instructions = new BHAVInstruction[]
                    {
                        // 0: idle for Args[0] ticks, or until the action is cancelled (as ReduxWooHoo's wait, minus the walk)
                        new BHAVInstruction { Opcode = 17, TruePointer = 254, FalsePointer = 254, Operand = op },
                    }
                };
                var routine = VMTranslator.ReduxAssemble(bhav, res);
                if (routine == null) return null;
                res.Iff.AddChunk(bhav);
                res.RoutineCache[PhoneTreeID] = routine;
                return routine;
            }
        }

        /// <summary>Queues the phone chat for the Sim: they stay where they are, phone out, typing.</summary>
        private static bool PushPhone(VMAvatar sim)
        {
            var routine = PhoneRoutine(sim);
            if (routine == null) return false;
            sim.Thread.EnqueueAction(new VMQueuedAction
            {
                Callee = sim,
                StackObject = sim,
                IconOwner = sim,
                CodeOwner = sim.Object,
                ActionRoutine = routine,
                Name = ActionName,
                Args = new short[] { 30000, 0, 0, 0 },
                Priority = (short)VMQueuePriority.UserDriven,
                Flags = TTABFlags.FSOSkipPermissions,
            });
            return true;
        }

        /// <summary>Every tick, for every Sim (ReduxComputer.Tick): a chat in progress is counted, finished or dropped.</summary>
        public static void Tick(VMAvatar sim)
        {
            if (sim == null || sim.GetAttribute(StateAttr) == 0) return;
            var thread = sim.Thread;
            var vm = thread?.Context?.VM;
            if (vm == null) return;
            VMQueuedAction action = null;
            var q = thread.Queue;
            for (int i = 0; i < q.Count; i++)
                if (q[i].Name == ActionName) { action = q[i]; break; }
            int typed = sim.GetAttribute(TypedAttr);
            int need = ChatMinutes * Math.Max(1, vm.Context.Clock.TicksPerMinute);
            // gone from the queue (taken off, or the computer's routine ended by itself), cancelled or interrupted: what
            // they'd typed so far counts
            if (action == null || action.NotifyIdle) { Finish(vm, sim, typed, need); return; }
            if (action != thread.ActiveAction) return;
            bool phone = (sim.GetAttribute(StateAttr) & 3) == WherePhone;
            if (!phone && !ReduxComputer.AtKeyboard(sim, action)) return;
            typed++;
            // (the phone by their head while they type - it shows for about a minute each time it comes out)
            if (phone && typed % 900 == 1) VMReduxPhone.Set(vm, sim, true);
            ReduxComputer.ShowShopProgress(sim, typed * 100 / Math.Max(1, need));
            if (typed < need) { sim.SetAttribute(TypedAttr, (short)Math.Min(short.MaxValue, typed)); return; }
            Finish(vm, sim, need, need);
            thread.CancelAction(action.UID); // (done: they put the phone away / get up)
        }

        private static void Finish(VM vm, VMAvatar sim, int typed, int need)
        {
            int st = sim.GetAttribute(StateAttr);
            var res = new Result
            {
                Guid = GuidOf(sim),
                Phone = (st & 3) == WherePhone,
                Nth = (st >> 2) & 7,
                Talk = sim.GetAttribute(TalkAttr),
                Talk2 = sim.GetAttribute(Talk2Attr),
            };
            int daily = ((st >> 5) & 31) - 16, life = ((st >> 10) & 7) - 4;
            bool whole = typed >= need;
            res.Done = whole ? Exchanges : Math.Max(0, Math.Min(Exchanges, typed * Exchanges / Math.Max(1, need)));
            res.Stopped = !whole;
            res.Daily = whole ? daily : daily * res.Done / Exchanges;
            res.Life = whole ? life : 0;
            sim.SetAttribute(StateAttr, 0);
            sim.SetAttribute(GuidLoAttr, 0);
            sim.SetAttribute(GuidHiAttr, 0);
            sim.SetAttribute(TypedAttr, 0);
            sim.SetAttribute(TalkAttr, 0);
            sim.SetAttribute(Talk2Attr, 0);
            if (res.Phone && typed > 0) VMReduxPhone.Set(vm, sim, false);
            if (res.Done > 0)
            {
                sim.SetMotiveData(VMMotive.Social, (short)Math.Max(-100, Math.Min(100, sim.GetMotiveData(VMMotive.Social) + 3 * res.Done)));
                if (res.Daily != 0 || res.Life != 0) res.Written = VMReduxMatch.HoodChat(sim, res.Guid, res.Daily, res.Life);
            }
            try { Done?.Invoke(vm, sim, res); } catch (Exception e) { Console.WriteLine("[SimsRedux] dating app chat: " + e.Message); }
        }
    }
}
