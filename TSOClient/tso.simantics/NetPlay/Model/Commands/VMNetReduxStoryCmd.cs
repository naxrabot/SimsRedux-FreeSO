using System;
using System.Collections.Generic;
using System.IO;
using FSO.SimAntics.Model;
using FSO.SimAntics.Utils;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux 17.2 (Part B #4, S1): story progression's changes reaching a family's own lot (VM command 85).
    ///
    /// While the player plays other families, Simitone's ReduxStory moves the neighbourhood's lives on (skills, jobs,
    /// promotions). A family that lives in a house keeps its Sims' person data in its house file, so what happened to them
    /// is kept as small changes and handed to their lot the next time it opens - through this command, so the lot's
    /// numbers change the same way on every game. Each change is relative to what the Sim has on the lot right now:
    ///   SkillAdd  (A: the skill's person data number, B: points 1-300) - the skill goes up, never past 1000;
    ///   Promote   (A: the job's number)  - one level up, only if they still have that job and aren't at the top;
    ///   NewJob    (A: the job, B: the level) - only if they have no job now (never a child, a pet or a ghost).
    /// Only from the game running the lot (single player - ReduxStory never sends it on a visit), only for the family
    /// living there (ReduxComputer.IsHouseholdSim). At most MaxChanges changes a command.
    /// </summary>
    public class VMNetReduxStoryCmd : VMNetCommandBodyAbstract
    {
        public enum ChangeKind : byte { SkillAdd = 1, Promote = 2, NewJob = 3 }

        public struct Change { public ChangeKind Kind; public short A, B; }

        public const int MaxChanges = 24;

        /// <summary>The Sim (object id on the lot).</summary>
        public short Sim;
        public List<Change> Changes = new List<Change>();

        /// <summary>The skills story progression may change (Cooking, Charisma, Mechanical, Creativity, Body, Logic).</summary>
        public static readonly VMPersonDataVariable[] Skills =
        {
            VMPersonDataVariable.CookingSkill, VMPersonDataVariable.CharismaSkill, VMPersonDataVariable.MechanicalSkill,
            VMPersonDataVariable.CreativitySkill, VMPersonDataVariable.BodySkill, VMPersonDataVariable.LogicSkill,
        };

        /// <summary>Done on every game: the Sim and how many changes went through.</summary>
        public static event Action<VM, VMAvatar, int> Applied;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            if (vm == null || !vm.TS1) return false;
            try
            {
                var sim = vm.GetObjectById(Sim) as VMAvatar;
                if (sim == null || sim.IsPet || sim.Dead || !ReduxComputer.IsHouseholdSim(vm, sim)) return false;
                if (sim.GetPersonData(VMPersonDataVariable.IsGhost) != 0) return false;
                int done = 0;
                foreach (var c in Changes)
                {
                    if (done >= MaxChanges) break;
                    if (Apply(sim, c)) done++;
                }
                try { Applied?.Invoke(vm, sim, done); } catch { }
                return done > 0;
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] story command failed: " + e.Message);
            }
            return false;
        }

        private static bool Apply(VMAvatar sim, Change c)
        {
            switch (c.Kind)
            {
                case ChangeKind.SkillAdd:
                    {
                        var v = (VMPersonDataVariable)c.A;
                        if (Array.IndexOf(Skills, v) < 0 || c.B <= 0) return false;
                        int now = sim.GetPersonData(v);
                        int next = Math.Max(0, Math.Min(1000, now + Math.Min((int)c.B, 300)));
                        if (next == now) return false;
                        sim.SetPersonData(v, (short)next);
                        return true;
                    }
                case ChangeKind.Promote:
                    {
                        if (sim.IsChild) return false;
                        int job = sim.GetPersonData(VMPersonDataVariable.JobType);
                        if (job <= 0 || job != c.A) return false;
                        int top = TopLevel(job);
                        int level = sim.GetPersonData(VMPersonDataVariable.JobPromotionLevel);
                        if (top < 0 || level >= top) return false;
                        sim.SetPersonData(VMPersonDataVariable.JobPromotionLevel, (short)(level + 1));
                        sim.SetPersonData(VMPersonDataVariable.JobPerformance, 0);
                        return true;
                    }
                case ChangeKind.NewJob:
                    {
                        if (sim.IsChild || c.A <= 0) return false;
                        if (sim.GetPersonData(VMPersonDataVariable.JobType) > 0) return false;
                        int top = TopLevel(c.A);
                        if (top < 0) return false;
                        sim.SetPersonData(VMPersonDataVariable.JobType, c.A);
                        sim.SetPersonData(VMPersonDataVariable.JobPromotionLevel, (short)Math.Max(0, Math.Min(top, (int)c.B)));
                        sim.SetPersonData(VMPersonDataVariable.JobPerformance, 0);
                        return true;
                    }
            }
            return false;
        }

        /// <summary>The highest level of a job (its last level), -1: no such job.</summary>
        public static int TopLevel(int job)
        {
            if (job <= 0) return -1;
            var carr = Content.Content.Get().Jobs?.GetJob((ushort)job);
            if (carr?.JobLevels == null || carr.JobLevels.Length == 0) return -1;
            return carr.JobLevels.Length - 1;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // (the game running the lot only - a visiting player never changes the host family's Sims)
            return !FromNet && Changes != null && Changes.Count <= MaxChanges;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Sim);
            int n = Math.Min(MaxChanges, Changes?.Count ?? 0);
            writer.Write((byte)n);
            for (int i = 0; i < n; i++)
            {
                writer.Write((byte)Changes[i].Kind);
                writer.Write(Changes[i].A);
                writer.Write(Changes[i].B);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Sim = reader.ReadInt16();
            int n = Math.Min(MaxChanges, (int)reader.ReadByte());
            Changes = new List<Change>();
            for (int i = 0; i < n; i++)
                Changes.Add(new Change { Kind = (ChangeKind)reader.ReadByte(), A = reader.ReadInt16(), B = reader.ReadInt16() });
        }
    }
}
