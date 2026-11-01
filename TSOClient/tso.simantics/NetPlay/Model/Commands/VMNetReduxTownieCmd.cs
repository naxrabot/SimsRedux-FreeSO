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
            /// <summary>Make the Sim (or, Kind Dog, the dog) GUID out on the sidewalk at tile From (OtherID: the Sim it goes with
            /// - the dog's walker, the first child; 19.10: it starts a tile behind them on the sidewalk, no longer anywhere around
            /// them), marked with Kind, greet status Greet (-1: 0), walking along the sidewalk to tile To (19.10: in short goes,
            /// and no Sims 1 "Leave" after it - the game deciding takes them off at the end).</summary>
            StreetWalk = 4,
            /// <summary>Street Sim ObjectID stops for a social with OtherID (Kind: 0 a chat, 1 children playing, 2 a wave at
            /// the family; Pick picks among the ones found), then walks on to its end tile.</summary>
            StreetSocial = 5,
            /// <summary>Street Sim ObjectID sets off for its end tile again (if nothing it was given is still waiting). 19.10: Kind 1 -
            /// for the end of the sidewalk at ToX/ToY instead (the nearer one: the walk kept stopping short).</summary>
            StreetGo = 6,
            /// <summary>Street Sim ObjectID is stopped by the family (greeted, talked to): no longer a street Sim - its walk is
            /// called off and it's an ordinary walk-by / visitor from now on.</summary>
            StreetStay = 7,
            /// <summary>Street Sim ObjectID reached the end of the sidewalk: taken off the lot (with its Go Here markers).</summary>
            StreetRemove = 8,
            /// <summary>19.6 test cheat "streetlife": a passing car right away (FSO.SimAntics.Utils.ReduxTraffic.SpawnNow).</summary>
            StreetCar = 9,
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
                    case TownieMode.StreetCar: return FSO.SimAntics.Utils.ReduxTraffic.SpawnNow(vm);
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
            // (19.7: every step and every refusal goes in the game log - "street life: walk <GUID> (<kind>) ...")
            string tag = "[SimsRedux] street life: walk " + GUID.ToString("X8") + " (" + (Sidewalk.Kind)Kind + ")";
            if (!vm.TS1 || Kind <= 0 || Kind > Sidewalk.MaxKind) { Console.WriteLine(tag + " refused - not a street kind"); return false; }
            if (vm.Context.ObjectQueries.Avatars.Any(x => x.Object?.OBJ?.GUID == GUID)) { Console.WriteLine(tag + " refused - already on the lot"); return false; }
            var fam = vm.TS1State?.CurrentFamily;
            if (fam?.FamilyGUIDs != null && fam.FamilyGUIDs.Contains(GUID)) { Console.WriteLine(tag + " refused - the family playing here"); return false; }
            if (!OnLot(vm, FromX, FromY) || !OnLot(vm, ToX, ToY)) { Console.WriteLine(tag + " refused - tiles " + FromX + "," + FromY + " / " + ToX + "," + ToY + " off the lot"); return false; }
            var ava = vm.Context.CreateObjectInstance(GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) { Console.WriteLine(tag + " refused - no character to make (no object for that GUID)"); return false; }
            // (a dog only as a dog walker's dog; anyone else is a person)
            bool dog = Kind == (short)Sidewalk.Kind.Dog;
            if (ava.IsPet != dog || (dog && !ava.IsDog))
            {
                Console.WriteLine(tag + " refused - " + (ava.Name ?? "") + (ava.IsPet ? " is a pet" : " is not a dog"));
                ava.Delete(true, vm.Context);
                return false;
            }
            // passing by: a visitor nobody has greeted yet (Sims 1's walk-by - the family can greet them as usual)
            ava.SetPersonData(VMPersonDataVariable.PersonType, 1);
            ava.SetPersonData(VMPersonDataVariable.GreetStatus, (short)(Greet >= 0 ? Greet : 0));
            // (19.6 fix: when a Sim starts, Sims 1's "person main" sends a visitor whose greet status is 0 on a home lot
            //  straight to the front door - "begin visiting": ring the bell, wait to be greeted, go home - so every
            //  passer-by turned into a bell-ringer. -1 is what "begin visiting" itself sets on the way to the door, so
            //  person main lets them be; once they're walking, the game deciding sets 0 again (SetPerson, from
            //  ReduxStreetPeople) so the family can greet them as usual. Dogs don't have that script.)
            if (!dog && Greet <= 0) ava.SetPersonData(VMPersonDataVariable.GreetStatus, -1);
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
            // a Sim's pace - trotting, it keeps up with its walker and a little ahead); everyone else walks
            ava.SetValue(VMStackObjectVariable.WalkStyle, StreetWalkStyle(ava));

            // (19.10: only ever on the lot's own sidewalk. 20.1: along one of the lot's ways from edge to edge - From at an open
            //  end of its sidewalks (the edge of the lot, give or take two for a follower), To another - round the corner on a
            //  corner lot; never from or to the corner, a sidewalk's trimmed end or the road)
            var route = Sidewalk.RouteFor(Sidewalk.Routes(vm.Context), FromX, FromY, ToX, ToY);
            if (route == null)
            {
                Console.WriteLine(tag + " refused - " + FromX + "," + FromY + " / " + ToX + "," + ToY + " aren't the start and end of a way along this lot's sidewalks");
                ava.Delete(true, vm.Context);
                return false;
            }
            var leg0 = route.Legs[0];
            var wk = leg0.W;
            var dir = Facing(FromX, FromY, wk.X(leg0.To), wk.Y(leg0.To));
            bool placed = PlaceOnWalk(vm, ava, wk, Math.Sign(leg0.To - leg0.From), dir);
            // (19.7: no free spot on the sidewalk - by the lot's street portal instead, as the doorbell walk-bys (Arrive)
            //  come in, and they walk along the sidewalk from there. 19.10: only a portal on the lot's side of the street, and
            //  only a spot right by the sidewalk - many lots have portals across the street, and the spot by one could be on
            //  the road: there they're refused instead)
            var anchor = (!placed && AnchorID != 0) ? vm.GetObjectById(AnchorID) : null;
            if (anchor != null && !anchor.Dead && anchor.Position != LotTilePos.OUT_OF_WORLD
                && wk.LotSide(anchor.Position.TileX, anchor.Position.TileY))
            {
                placed = VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default) && ava.Position != LotTilePos.OUT_OF_WORLD
                    && Sidewalk.NearSidewalk(vm.Context, wk, ava.Position.TileX, ava.Position.TileY) && ava.Position.Level == 1
                    // (20.1: and only on the sidewalk itself - never the grass strip by the kerb)
                    && wk.Off(ava.Position.TileX, ava.Position.TileY) == 0 && Sidewalk.IsStopTile(vm.Context, wk, ava.Position.TileX, ava.Position.TileY);
            }
            if (!placed)
            {
                Console.WriteLine(tag + " refused - " + (ava.Name ?? "") + ": no free spot on the sidewalk near " + FromX + "," + FromY
                    + (AnchorID != 0 ? " (nor by a street portal on this side of the street)" : ""));
                ava.Delete(true, vm.Context);
                return false;
            }
            Console.WriteLine(tag + ": " + (ava.Name ?? "") + " made at " + ava.Position.TileX + "," + ava.Position.TileY + " on the sidewalk " + wk + " - way " + route);
            // (19.10: along the sidewalk in short goes, and no Sims 1 "Leave" after it any more - that walked them to a random
            //  portal, through the yard or across the street; at the end of the sidewalk the game deciding takes them off)
            if (!WalkRoute(vm, ava, ToX, ToY, false))
            {
                Console.WriteLine(tag + " - " + (ava.Name ?? "") + " can't walk along the sidewalk - taken off again");
                ava.Delete(true, vm.Context);
                return false;
            }
            Console.WriteLine(tag + ": " + (ava.Name ?? "") + " walking along the sidewalk to " + ToX + "," + ToY);
            vm.Scheduler.RescheduleInterrupt(ava);
            return true;
        }

        /// <summary>
        /// The first free sidewalk tile from From (19.10: on the sidewalk's own line only - never a tile to the side, which
        /// could be the road or the yard): From, then a tile on towards To, one back, two on, two back... up to 6 - the same
        /// order on every game. (A follower - the dog, the second child - is sent from a tile behind its leader.) sign: the way
        /// along the sidewalk they'll walk (20.1: towards the end of the first leg of their way).
        /// </summary>
        private bool PlaceOnWalk(VM vm, VMAvatar ava, Sidewalk.Walk wk, int sign, Direction dir)
        {
            int start = wk.Along(FromX, FromY);
            if (sign == 0) sign = 1;
            for (int i = 0; i <= 12; i++)
            {
                int a = start + ((i & 1) == 1 ? sign * ((i + 1) / 2) : -sign * (i / 2));
                if (a < wk.Min - 2 || a > wk.Max + 2) continue;
                int x = wk.X(a), y = wk.Y(a);
                if (!Sidewalk.IsStopTile(vm.Context, wk, x, y)) continue; // (20.1: a sidewalk tile, on a sidewalk)
                var pos = LotTilePos.FromBigTile((short)x, (short)y, 1);
                if (vm.Context.IsOutOfBounds(pos)) continue;
                if (ava.SetPosition(pos, dir, vm.Context).Status == VMPlacementError.Success) return true;
            }
            return false;
        }

        /// <summary>20.1: a street Sim's walk style: joggers run and dogs trot (Sims 1's running style); everyone else walks.</summary>
        private static short StreetWalkStyle(VMAvatar ava)
        {
            return (short)((ava.IsPet || ava.GetAttribute(Sidewalk.StreetAttr) == (short)Sidewalk.Kind.Jogger) ? 1 : 0);
        }

        private enum StopKind { Mid, Corner, End }

        /// <summary>
        /// 19.10: queues the walk along the sidewalk: a Go Here every few tiles, each to a tile on the sidewalk's line, so the
        /// routing never has a reason to leave it (one long Go Here cut through the yard or behind the house). A stop whose
        /// tile is taken moves to the nearest free sidewalk tile beside it (2 either way); none: that stop is skipped. head:
        /// right after what the Sim is doing now, ahead of anything else waiting. False: nothing queued (already there, no
        /// way along the sidewalks, or no stop could be placed).
        /// 20.1: the rest of the Sim's way to the open end ex,ey (Sidewalk.Plan): along the sidewalk they're on, and on a corner
        /// lot to the corner - a stop right on it - and on along the other street's sidewalk. Each stop is HopTiles on from
        /// where the one before really went, so no go is 8 tiles or more (Sims 1's Go Here runs then).
        /// </summary>
        private static bool WalkRoute(VM vm, VMAvatar ava, int ex, int ey, bool head)
        {
            var p = ava.Position;
            short tx, ty;
            var legs = Sidewalk.PlanFor(vm.Context, p.TileX, p.TileY, ex, ey, false, out tx, out ty);
            if (legs == null || legs.Count == 0) return false;
            // (the end they're really heading for - another edge, if theirs was lost)
            if (tx != ex || ty != ey) ava.SetAttribute(Sidewalk.EndAttr, Sidewalk.Pack(tx, ty));
            // (they walk - a "run" left over from a go that was cut short is put right)
            ava.SetValue(VMStackObjectVariable.WalkStyle, StreetWalkStyle(ava));
            var actions = new List<VMQueuedAction>();
            for (int k = 0; k < legs.Count; k++)
            {
                var l = legs[k];
                var wk = l.W;
                int dir = Math.Sign(l.To - l.From);
                int at = l.From;
                if (k == 0 && wk.Off(p.TileX, p.TileY) != 0)
                {
                    // (off the sidewalk's line now - after a chat, a wave: back onto it first)
                    int got;
                    var back = HopTo(vm, ava, wk, at, dir == 0 ? 1 : dir, StopKind.Mid, out got);
                    if (back != null) { actions.Add(back); at = got; }
                }
                if (dir == 0) continue;
                while (true)
                {
                    int next = at + dir * Sidewalk.HopTiles;
                    bool final = dir > 0 ? next >= l.To - 1 : next <= l.To + 1; // (no stop a tile short of the end)
                    if (final) next = l.To;
                    int got;
                    var act = HopTo(vm, ava, wk, next, dir, final ? (k == legs.Count - 1 ? StopKind.End : StopKind.Corner) : StopKind.Mid, out got);
                    if (act != null) { actions.Add(act); at = got; }
                    else at = next;
                    if (final) break;
                }
            }
            if (actions.Count == 0) return false;
            // (pushed to the head they go in backwards, so they come out in order)
            if (head) actions.Reverse();
            foreach (var act in actions)
            {
                if (head) act.Flags |= TTABFlags.FSOPushHead;
                ava.Thread.EnqueueAction(act);
            }
            return true;
        }

        /// <summary>
        /// One stop of a walk along the sidewalk: Sims 1's own Go Here (as the pie menu gives it), its marker on a sidewalk tile
        /// at along-position a, or the nearest free one beside it on the line (back towards the start first, so a stop never
        /// overshoots; the last stop looks further back, up to a hop; 20.1: a corner only back, up to 2 - never past it, onto
        /// the crossing) - null: none free. got: the along-position the marker went to.
        /// </summary>
        private static VMQueuedAction HopTo(VM vm, VMAvatar ava, Sidewalk.Walk wk, int a, int dir, StopKind kind, out int got)
        {
            got = a;
            var group = vm.Context.CreateObjectInstance(Sidewalk.GotoGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            var go = (group != null && group.Objects.Count > 0) ? group.Objects[0] : null;
            if (go == null)
            {
                Console.WriteLine("[SimsRedux] street life: no Go Here object to walk " + (ava.Name ?? "") + " along the sidewalk");
                return null;
            }
            // (19.7 fix: the Go Here interaction is TTA index 2 in Sims 1's Destination.iff - asking for 0 always got nothing)
            int goHere = Sidewalk.GotoInteraction(go);
            if (dir == 0) dir = 1;
            bool placed = false;
            int reach = kind == StopKind.End ? Sidewalk.HopTiles : 2;
            for (int i = 0; i <= reach * 2 && !placed; i++)
            {
                // a, one back, one on, two back, two on... (the last stop and a corner: only back - never past them)
                int off = (i == 0) ? 0 : ((i & 1) == 1 ? -dir * ((i + 1) / 2) : dir * (i / 2));
                if (kind != StopKind.Mid && off * dir > 0) continue;
                if (kind != StopKind.End && Math.Abs(off) > 2) continue;
                int at = a + off;
                if (at < wk.Min - 2 || at > wk.Max + 2) continue;
                int x = wk.X(at), y = wk.Y(at);
                if (!Sidewalk.IsStopTile(vm.Context, wk, x, y)) continue; // (20.1: a sidewalk tile, on a sidewalk)
                var pos = LotTilePos.FromBigTile((short)x, (short)y, 1);
                if (vm.Context.IsOutOfBounds(pos)) continue;
                placed = go.SetPosition(pos, Direction.NORTH, vm.Context).Status == VMPlacementError.Success;
                if (placed) got = at;
            }
            VMQueuedAction action = (placed && goHere >= 0) ? go.GetAction(goHere, ava, vm.Context, false, new short[] { 0, 0, 0, 0 }) : null;
            if (action == null)
            {
                Console.WriteLine("[SimsRedux] street life: can't walk " + (ava.Name ?? "") + " to " + wk.X(a) + "," + wk.Y(a) + " - "
                    + (!placed ? "no free sidewalk tile for the Go Here marker there" : goHere < 0 ? "the Go Here object has no Go Here" : "Go Here refused"));
                go.Delete(true, vm.Context);
                return null;
            }
            // (a passer-by is a visitor, the dog a pet: Go Here is the player's, so its permissions are skipped)
            action.Flags |= TTABFlags.FSOSkipPermissions;
            action.Priority = (short)VMQueuePriority.UserDriven;
            return action;
        }

        /// <summary>
        /// 19.10: calls off the street walk queued for a Sim (its Go Here stops, and any Sims 1 "Leave"): the ones not started
        /// yet are taken out of the queue and their Go Here markers off the lot (a stopped walk used to leave its marker behind);
        /// the one running is cancelled as before and tidies up after itself.
        /// </summary>
        private static void CancelWalk(VM vm, VMAvatar ava, bool alsoLeave)
        {
            var th = ava.Thread;
            if (th == null) return;
            var mine = th.Queue.Where(x => x.Callee?.Object?.OBJ?.GUID == Sidewalk.GotoGUID || (alsoLeave && x.ActionRoutine?.ID == LeaveRoutine)).ToList();
            var markers = new List<VMEntity>();
            foreach (var a in mine)
            {
                bool started = th.Queue.IndexOf(a) <= th.ActiveQueueBlock;
                if (!started && a.Callee != null && a.Callee != ava && a.Callee.Object?.OBJ?.GUID == Sidewalk.GotoGUID) markers.Add(a.Callee);
                th.CancelAction(a.UID);
            }
            foreach (var go in markers)
                if (!go.Dead && !th.Queue.Any(x => x.Callee == go)) go.Delete(true, vm.Context);
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
            short tx, ty;
            if (Sidewalk.PlanFor(vm.Context, me.Position.TileX, me.Position.TileY, ex, ey, false, out tx, out ty) == null) return false;
            // the walk they were on stops (where they are, on the sidewalk); after the social they set off again for the same
            // end, along the sidewalk (19.10; 20.1: round the corner if their way goes on there)
            CancelWalk(vm, me, false);
            if (!WalkRoute(vm, me, ex, ey, true)) return false;
            me.Thread.EnqueueAction(social); // (ahead of the new walk: both go right after what's running now)
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
            // (19.10: Kind 1 - the walk keeps stopping short: they turn for the nearer end of the sidewalk instead, and leave
            //  the lot there - never through the yard. 20.1: the nearest edge of the lot along the sidewalks - never a corner)
            //  (the end the game deciding picked is in ToX/ToY - so it knows where they'll be; else the nearest one from here)
            int gx = ex, gy = ey;
            if (Kind == 1)
            {
                short nx, ny;
                if (Sidewalk.IsExit(vm.Context, ToX, ToY)) { gx = ToX; gy = ToY; }
                else if (Sidewalk.NearestExit(vm.Context, ava.Position.TileX, ava.Position.TileY, out nx, out ny)) { gx = nx; gy = ny; }
                ava.SetAttribute(Sidewalk.EndAttr, Sidewalk.Pack(gx, gy));
            }
            // (a Sims 1 "Leave" queued before 19.10 is called off: it walked them to a random portal - the walk is all now)
            foreach (var a in th.Queue.Where(x => x.ActionRoutine?.ID == LeaveRoutine && th.Queue.IndexOf(x) > th.ActiveQueueBlock).ToList())
                th.CancelAction(a.UID);
            return WalkRoute(vm, ava, gx, gy, true);
        }

        private bool StreetStay(VM vm)
        {
            var ava = vm.GetObjectById(ObjectID) as VMAvatar;
            if (ava == null || !Sidewalk.IsStreet(ava)) return false;
            ava.SetAttribute(Sidewalk.StreetAttr, 0);
            ava.SetAttribute(Sidewalk.EndAttr, 0);
            ava.SetValue(VMStackObjectVariable.WalkStyle, 0);
            CancelWalk(vm, ava, true); // (19.10: and the Go Here markers of the stops not reached yet)
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
            Console.WriteLine("[SimsRedux] street life: " + (ava.Name ?? "") + " taken off the street at " + ava.Position.TileX + "," + ava.Position.TileY);
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
