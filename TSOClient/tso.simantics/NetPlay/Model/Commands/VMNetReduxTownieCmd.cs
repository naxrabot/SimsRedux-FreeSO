using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Primitives;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Sidewalk = FSO.SimAntics.Utils.ReduxSidewalk;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    /// <summary>
    /// SimsRedux: townies and walk-bys. Everything that changes the lot (a townie arriving, heading
    /// home, leaving, or being marked as greeted) happens through this command, so on an online lot
    /// every player's game does exactly the same thing at the same moment. Only the lot's own game
    /// (single player, or the server's lot) ever sends it.
    /// </summary>
    public class VMNetReduxTownieCmd : VMNetCommandBodyAbstract
    {
        public enum TownieMode : byte
        {
            /// <summary>Make the Sim GUID and put them by object AnchorID.</summary>
            Arrive = 0,
            /// <summary>Sim ObjectID heads home with Sims 1's own global "Leave".</summary>
            Leave = 1,
            /// <summary>Object ObjectID is taken off the lot.</summary>
            Remove = 2,
            /// <summary>Sim ObjectID gets person type PersonType and greet status Greet (-1 = leave as is).</summary>
            SetPerson = 3,

            // ---- 19.6: street life (FSO.SimAntics.Utils.ReduxSidewalk; decided by Simitone.Client.Utils.ReduxStreetPeople) ----
            /// <summary>Make the Sim (or, Kind Dog, the dog) GUID out on the sidewalk at tile From (OtherID: next to that Sim
            /// instead - the dog by its walker, the second child), marked with Kind, greet status Greet (-1: 0), walking to
            /// tile To, then Sims 1's own "Leave".</summary>
            StreetWalk = 4,
            /// <summary>Street Sim ObjectID stops for a social with OtherID (Kind: 0 a chat, 1 children playing, 2 a wave at
            /// the family; Pick picks among the ones found), then walks on to its end tile.</summary>
            StreetSocial = 5,
            /// <summary>Street Sim ObjectID sets off for its end tile again (if nothing it was given is still waiting).</summary>
            StreetGo = 6,
            /// <summary>Street Sim ObjectID is stopped by the family (greeted, talked to): no longer a street Sim - its walk is
            /// called off and it's an ordinary walk-by / visitor from now on.</summary>
            StreetStay = 7,
            /// <summary>Street Sim ObjectID reached the end of the sidewalk: taken off the lot (with its Go Here markers).</summary>
            StreetRemove = 8,
        }

        /// <summary>Sims 1's global "Leave" routine (route to a random portal, then go).</summary>
        public const ushort LeaveRoutine = 368;

        public TownieMode Mode;
        public uint GUID;
        public short AnchorID;
        public short ObjectID;
        public short PersonType = -1;
        public short Greet = -1;
        // 19.6 street modes only (written and read only for them - the first four modes' bytes are as before)
        public short FromX, FromY, ToX, ToY, Kind, OtherID, Pick;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            try
            {
                switch (Mode)
                {
                    case TownieMode.Arrive: return Arrive(vm);
                    case TownieMode.Leave:
                        {
                            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
                            return ava != null && PushLeave(vm, ava);
                        }
                    case TownieMode.Remove:
                        {
                            var ent = vm.GetObjectById(ObjectID);
                            if (ent == null || ent.Dead) return false;
                            // (18.6: not while someone is in the middle of an interaction with them - petting a visiting pet, a
                            //  chat: their script still points at this Sim and broke when it vanished (Headless Test #13:
                            //  "Standard Exit" on Henri LeStanc). The game that sent this tries again later.)
                            if (InUseByOthers(vm, ent)) return false;
                            ent.Delete(true, vm.Context);
                            return true;
                        }
                    case TownieMode.SetPerson:
                        {
                            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
                            if (ava == null) return false;
                            if (PersonType >= 0) ava.SetPersonData(VMPersonDataVariable.PersonType, PersonType);
                            if (Greet >= 0) ava.SetPersonData(VMPersonDataVariable.GreetStatus, Greet);
                            return true;
                        }
                    case TownieMode.StreetWalk: return StreetWalk(vm);
                    case TownieMode.StreetSocial: return StreetSocial(vm);
                    case TownieMode.StreetGo: return StreetGo(vm);
                    case TownieMode.StreetStay: return StreetStay(vm);
                    case TownieMode.StreetRemove: return StreetRemove(vm);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("[SimsRedux] townie command " + Mode + " failed: " + e.Message);
            }
            return false;
        }

        /// <summary>Another Sim's running script has this one as its object (or is running one of its interactions) right now.</summary>
        private static bool InUseByOthers(VM vm, VMEntity ent)
        {
            foreach (var o in vm.Context.ObjectQueries.Avatars)
            {
                if (o == ent || o.Dead || o.Thread == null) continue;
                var st = o.Thread.Stack;
                for (int i = 0; i < st.Count; i++)
                {
                    var f = st[i];
                    if (f == null) continue;
                    if (f.Callee == ent || f.StackObject == ent) return true;
                }
            }
            return false;
        }

        private bool Arrive(VM vm)
        {
            if (vm.Context.ObjectQueries.Avatars.Any(x => x.Object?.OBJ?.GUID == GUID)) return false; // already here
            var ava = vm.Context.CreateObjectInstance(GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) return false;
            if (ava.IsPet)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            // a visitor (made the way the game makes any neighbour, with their neighbour data)
            ava.SetPersonData(VMPersonDataVariable.PersonType, 1);
            if (Greet >= 0) ava.SetPersonData(VMPersonDataVariable.GreetStatus, Greet);
            // arriving in good shape, the way Sims 1's own "init visitor" sets them up: a visitor checks
            // their needs all the time and goes home as soon as one is low - with the needs they had saved
            // in the neighbourhood (often near empty) they left the moment they were greeted
            // (the lot's own random numbers: every game on an online lot sets the same)
            foreach (var m in new[] { VMMotive.Energy, VMMotive.Comfort, VMMotive.Hunger, VMMotive.Hygiene, VMMotive.Bladder })
                ava.SetMotiveData(m, (short)(45 + (int)vm.Context.NextRandom(40)));
            ava.SetMotiveData(VMMotive.Social, (short)(10 + (int)vm.Context.NextRandom(40)));
            ava.SetMotiveData(VMMotive.Fun, (short)(20 + (int)vm.Context.NextRandom(40)));
            // passing through: never saved into the lot (Sims 1 sends visitors home when you leave)
            ava.SetValue(VMStackObjectVariable.MovementFlags,
                (short)(ava.GetValue(VMStackObjectVariable.MovementFlags) | (short)VMMovementFlags.FSOExcludeUserSave));

            var anchor = vm.GetObjectById(AnchorID);
            if (anchor != null && !anchor.Dead) VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default);
            if (ava.Position == LotTilePos.OUT_OF_WORLD)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            vm.Scheduler.RescheduleInterrupt(ava);
            return true;
        }

        // ------------------------------------------------------------------
        // 19.6: street life - people walking along the sidewalk in front of the lot
        // ------------------------------------------------------------------

        private static bool OnLot(VM vm, int x, int y)
        {
            var arch = vm.Context.Architecture;
            return arch != null && x >= 0 && y >= 0 && x < arch.Width && y < arch.Height;
        }

        private static bool IsLotFamily(VM vm, VMAvatar ava)
        {
            var fam = vm.TS1State?.CurrentFamily;
            if (fam == null || ava == null || ava.IsPet) return false;
            return ava.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == fam.ChunkID
                || (fam.FamilyGUIDs != null && fam.FamilyGUIDs.Contains(ava.Object.OBJ.GUID));
        }

        /// <summary>The walk (Go Here) and the "Leave" a street mode queued.</summary>
        private static bool IsStreetAction(VMQueuedAction a)
        {
            return a != null && (a.Callee?.Object?.OBJ?.GUID == Sidewalk.GotoGUID || a.ActionRoutine?.ID == LeaveRoutine);
        }

        private static Direction Facing(int fx, int fy, int tx, int ty)
        {
            int dx = tx - fx, dy = ty - fy;
            if (Math.Abs(dx) >= Math.Abs(dy)) return dx >= 0 ? Direction.EAST : Direction.WEST;
            return dy >= 0 ? Direction.SOUTH : Direction.NORTH;
        }

        private bool StreetWalk(VM vm)
        {
            if (!vm.TS1 || Kind <= 0 || Kind > Sidewalk.MaxKind) return false;
            if (vm.Context.ObjectQueries.Avatars.Any(x => x.Object?.OBJ?.GUID == GUID)) return false; // already here
            var fam = vm.TS1State?.CurrentFamily;
            if (fam?.FamilyGUIDs != null && fam.FamilyGUIDs.Contains(GUID)) return false; // (never the family playing here)
            if (!OnLot(vm, FromX, FromY) || !OnLot(vm, ToX, ToY)) return false;
            var ava = vm.Context.CreateObjectInstance(GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) return false;
            // (a dog only as a dog walker's dog; anyone else is a person)
            bool dog = Kind == (short)Sidewalk.Kind.Dog;
            if (ava.IsPet != dog || (dog && !ava.IsDog))
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            // passing by: a visitor nobody has greeted yet (Sims 1's walk-by - the family can greet them as usual)
            ava.SetPersonData(VMPersonDataVariable.PersonType, 1);
            ava.SetPersonData(VMPersonDataVariable.GreetStatus, (short)(Greet >= 0 ? Greet : 0));
            // in good shape, as Arrive (a visitor low on a need heads home at once) - the lot's own random numbers
            foreach (var m in new[] { VMMotive.Energy, VMMotive.Comfort, VMMotive.Hunger, VMMotive.Hygiene, VMMotive.Bladder })
                ava.SetMotiveData(m, (short)(45 + (int)vm.Context.NextRandom(40)));
            ava.SetMotiveData(VMMotive.Social, (short)(10 + (int)vm.Context.NextRandom(40)));
            ava.SetMotiveData(VMMotive.Fun, (short)(20 + (int)vm.Context.NextRandom(40)));
            // never saved into the lot
            ava.SetValue(VMStackObjectVariable.MovementFlags,
                (short)(ava.GetValue(VMStackObjectVariable.MovementFlags) | (short)VMMovementFlags.FSOExcludeUserSave));
            ava.SetAttribute(Sidewalk.StreetAttr, Kind);
            ava.SetAttribute(Sidewalk.EndAttr, Sidewalk.Pack(ToX, ToY));
            // joggers run (Sims 1's running walk style, as when a Sim hurries to a fire); so does the dog (pets walk at half
            // a Sim's pace - trotting, it keeps up with its walker and a little ahead)
            ava.SetValue(VMStackObjectVariable.WalkStyle, (short)(Kind == (short)Sidewalk.Kind.Jogger || dog ? 1 : 0));

            var dir = Facing(FromX, FromY, ToX, ToY);
            bool placed = false;
            var lead = (OtherID != 0) ? vm.GetObjectById(OtherID) as VMAvatar : null;
            if (lead != null && !lead.Dead && lead.Position != LotTilePos.OUT_OF_WORLD)
                placed = VMFindLocationFor.FindLocationFor(ava, lead, vm.Context, VMPlaceRequestFlags.Default) && ava.Position != LotTilePos.OUT_OF_WORLD;
            if (!placed) placed = PlaceOnWalk(vm, ava, dir);
            if (!placed || !WalkTo(vm, ava, ToX, ToY, false))
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            // (if nothing else takes them off at the end of the sidewalk, Sims 1's own "Leave" does - not the dog: it runs
            //  ahead and waits at the end for its walker, and goes with them)
            if (!dog) PushLeave(vm, ava);
            vm.Scheduler.RescheduleInterrupt(ava);
            return true;
        }

        /// <summary>The first free tile from From towards To (and a tile either side), in the same order on every game.</summary>
        private bool PlaceOnWalk(VM vm, VMAvatar ava, Direction dir)
        {
            bool alongX = Math.Abs(ToX - FromX) >= Math.Abs(ToY - FromY);
            int sx = alongX ? Math.Sign(ToX - FromX) : 0, sy = alongX ? 0 : Math.Sign(ToY - FromY);
            var sides = new[] { 0, 1, -1 };
            for (int i = 0; i <= 6; i++)
                foreach (var side in sides)
                {
                    int x = FromX + sx * i + (alongX ? 0 : side), y = FromY + sy * i + (alongX ? side : 0);
                    var pos = LotTilePos.FromBigTile((short)x, (short)y, 1);
                    if (!OnLot(vm, x, y) || vm.Context.IsOutOfBounds(pos)) continue;
                    if (ava.SetPosition(pos, dir, vm.Context).Status == VMPlacementError.Success) return true;
                }
            return false;
        }

        /// <summary>
        /// Queues a walk to tile (x, y) - Sims 1's own Go Here, as the pie menu gives it (the marker by the tile, or the nearest
        /// free spot within 2 tiles). head: right after what the Sim is doing now, ahead of anything else waiting.
        /// </summary>
        private static bool WalkTo(VM vm, VMAvatar ava, short x, short y, bool head)
        {
            var group = vm.Context.CreateObjectInstance(Sidewalk.GotoGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            var go = (group != null && group.Objects.Count > 0) ? group.Objects[0] : null;
            if (go == null) return false;
            bool placed = false;
            for (int r = 0; r <= 2 && !placed; r++)
                for (int dy = -r; dy <= r && !placed; dy++)
                    for (int dx = -r; dx <= r && !placed; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                        if (!OnLot(vm, x + dx, y + dy)) continue;
                        var pos = LotTilePos.FromBigTile((short)(x + dx), (short)(y + dy), 1);
                        placed = go.SetPosition(pos, Direction.NORTH, vm.Context).Status == VMPlacementError.Success;
                    }
            VMQueuedAction action = placed ? go.GetAction(0, ava, vm.Context, false, new short[] { 0, 0, 0, 0 }) : null;
            if (action == null)
            {
                go.Delete(true, vm.Context);
                return false;
            }
            // (a passer-by is a visitor, the dog a pet: Go Here is the player's, so its permissions are skipped)
            action.Flags |= TTABFlags.FSOSkipPermissions;
            if (head) action.Flags |= TTABFlags.FSOPushHead;
            action.Priority = (short)VMQueuePriority.UserDriven;
            ava.Thread.EnqueueAction(action);
            return true;
        }

        private static readonly string[][] SocialWords =
        {
            new[] { "talk", "chat", "compliment", "joke" },                     // 0: neighbours chatting
            new[] { "tag", "rock", "hand slap", "tickle", "play", "talk" },     // 1: children playing
            new[] { "wave" },                                                   // 2: a wave at the family
        };
        /// <summary>Never on the street: nothing romantic, rude or that invites itself in.</summary>
        private static readonly string[] NeverWords =
        {
            "kiss", "flirt", "hug", "romance", "romantic", "woohoo", "propose", "proposition", "rub", "massage", "dance", "insult",
            "tease", "fight", "attack", "poke", "goose", "nuzzle", "cuddle", "serenade", "date", "love", "bite", "brag",
            "scare", "ask", "move in", "marry", "invite", "leave", "goodbye", "suggest",
        };

        /// <summary>The social on target's own menu for this kind, by its English name (the same on every game) - null: none.</summary>
        private static TTABInteraction FindSocial(VMEntity target, short kind, short pick)
        {
            var ttab = target?.TreeTable;
            var ttas = target?.TreeTableStrings;
            if (ttab?.Interactions == null || ttas == null || kind < 0 || kind >= SocialWords.Length) return null;
            var found = new List<TTABInteraction>();
            foreach (var word in SocialWords[kind])
                foreach (var ia in ttab.Interactions)
                {
                    if (ia == null || found.Contains(ia)) continue;
                    var n = (ttas.TaggedEnglish((int)ia.TTAIndex) ?? ttas.GetString((int)ia.TTAIndex, STRLangCode.EnglishUS) ?? "").ToLowerInvariant();
                    if (n.Length == 0 || !n.Contains(word)) continue;
                    if (NeverWords.Any(x => n.Contains(x))) continue;
                    if (n.Contains("slap") && !n.Contains("hand slap")) continue;
                    found.Add(ia);
                }
            if (found.Count == 0) return null;
            return found[Math.Abs((int)pick) % found.Count];
        }

        private bool StreetSocial(VM vm)
        {
            var me = vm.GetObjectById(ObjectID) as VMAvatar;
            var other = vm.GetObjectById(OtherID) as VMAvatar;
            if (me == null || other == null || me == other || other.Dead || !Sidewalk.IsStreet(me)) return false;
            if (me.IsPet || other.IsPet || me.Position == LotTilePos.OUT_OF_WORLD || other.Position == LotTilePos.OUT_OF_WORLD) return false;
            if (Kind == 2) { if (!IsLotFamily(vm, other)) return false; } // (a wave: only at someone of the family here)
            else if (!Sidewalk.IsStreet(other)) return false;            // (a chat or a game: only with another Sim out on the street)
            // children play with children; grown-ups chat with grown-ups
            if (Kind == 1 && !(me.IsChild && other.IsChild)) return false;
            if (Kind == 0 && me.IsChild != other.IsChild) return false;
            var ia = FindSocial(other, Kind, Pick);
            if (ia == null) return false;
            var social = other.GetAction((int)ia.TTAIndex, me, vm.Context, false, new short[4]);
            if (social == null) return false;
            social.Flags |= TTABFlags.FSOSkipPermissions | TTABFlags.FSOPushHead;
            social.Priority = (short)VMQueuePriority.UserDriven;
            short ex, ey;
            Sidewalk.EndOf(me, out ex, out ey);
            var th = me.Thread;
            // the walk they were on stops; after the social they set off again for the same end
            var old = th.Queue.Where(x => x.Callee?.Object?.OBJ?.GUID == Sidewalk.GotoGUID).Select(x => x.UID).ToList();
            if (!WalkTo(vm, me, ex, ey, true)) return false;
            th.EnqueueAction(social); // (ahead of the new walk: both go right after what's running now)
            foreach (var uid in old) th.CancelAction(uid);
            return true;
        }

        private bool StreetGo(VM vm)
        {
            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
            if (ava == null || !Sidewalk.IsStreet(ava) || ava.Position == LotTilePos.OUT_OF_WORLD) return false;
            short ex, ey;
            Sidewalk.EndOf(ava, out ex, out ey);
            if (!OnLot(vm, ex, ey)) return false;
            var th = ava.Thread;
            if (th.Queue.Any(x => x.Callee?.Object?.OBJ?.GUID == Sidewalk.GotoGUID)) return false; // (still walking)
            // (the walk first, then the Leave)
            foreach (var a in th.Queue.Where(x => x.ActionRoutine?.ID == LeaveRoutine && th.Queue.IndexOf(x) > th.ActiveQueueBlock).ToList())
                th.CancelAction(a.UID);
            if (!WalkTo(vm, ava, ex, ey, true)) return false;
            if (!ava.IsPet) PushLeave(vm, ava);
            return true;
        }

        private bool StreetStay(VM vm)
        {
            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
            if (ava == null || !Sidewalk.IsStreet(ava)) return false;
            ava.SetAttribute(Sidewalk.StreetAttr, 0);
            ava.SetAttribute(Sidewalk.EndAttr, 0);
            ava.SetValue(VMStackObjectVariable.WalkStyle, 0);
            foreach (var a in ava.Thread.Queue.Where(IsStreetAction).ToList()) ava.Thread.CancelAction(a.UID);
            return true;
        }

        private bool StreetRemove(VM vm)
        {
            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
            if (ava == null || ava.Dead || !Sidewalk.IsStreet(ava)) return false;
            var fam = vm.TS1State?.CurrentFamily;
            if (fam?.FamilyGUIDs != null && fam.FamilyGUIDs.Contains(ava.Object.OBJ.GUID)) return false; // (adopted meanwhile: stays)
            if (InUseByOthers(vm, ava)) return false; // (the game that sent this tries again later)
            var markers = ava.Thread.Queue.Select(x => x.Callee)
                .Where(x => x != null && x != ava && !x.Dead && x.Object?.OBJ?.GUID == Sidewalk.GotoGUID).Distinct().ToList();
            ava.Delete(true, vm.Context);
            foreach (var go in markers) if (!go.Dead) go.Delete(true, vm.Context);
            return true;
        }

        /// <summary>Queues Sims 1's own global "Leave" for this Sim (they walk off the lot and go).</summary>
        public static bool PushLeave(VM vm, VMAvatar ava)
        {
            var tree = ava.GetRoutineWithOwner(LeaveRoutine, vm.Context);
            if (tree?.routine == null) return false;
            var label = tree.bhav?.ChunkLabel ?? "";
            if (label.IndexOf("Leave", StringComparison.OrdinalIgnoreCase) < 0) return false; // not the routine we expect
            ava.Thread.EnqueueAction(new VMQueuedAction
            {
                Callee = ava,
                StackObject = ava,
                IconOwner = ava,
                CodeOwner = tree.owner,
                ActionRoutine = tree.routine,
                Name = "Go Home",
                Priority = (short)VMQueuePriority.UserDriven,
                Flags = TTABFlags.FSOSkipPermissions,
            });
            return true;
        }

        public override bool Verify(VM vm, VMAvatar caller)
        {
            // only the lot's own game sends these - never a player
            return !FromNet;
        }

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write((byte)Mode);
            writer.Write(GUID);
            writer.Write(AnchorID);
            writer.Write(ObjectID);
            writer.Write(PersonType);
            writer.Write(Greet);
            if (Mode >= TownieMode.StreetWalk)
            {
                writer.Write(FromX); writer.Write(FromY); writer.Write(ToX); writer.Write(ToY);
                writer.Write(Kind); writer.Write(OtherID); writer.Write(Pick);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Mode = (TownieMode)reader.ReadByte();
            GUID = reader.ReadUInt32();
            AnchorID = reader.ReadInt16();
            ObjectID = reader.ReadInt16();
            PersonType = reader.ReadInt16();
            Greet = reader.ReadInt16();
            if (Mode >= TownieMode.StreetWalk)
            {
                FromX = reader.ReadInt16(); FromY = reader.ReadInt16(); ToX = reader.ReadInt16(); ToY = reader.ReadInt16();
                Kind = reader.ReadInt16(); OtherID = reader.ReadInt16(); Pick = reader.ReadInt16();
            }
        }
    }
}
