using FSO.Content.TS1;
using FSO.Files.Utils;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Utils;
using System;
using System.IO;
using System.Linq;

namespace FSO.SimAntics.Primitives
{
    public class VMTS1MakeNewCharacter : VMPrimitiveHandler
    {
        public static string[] ColorNames = new string[]
        {
            "lgt",
            "med",
            "drk"
        };
        public override VMPrimitiveExitCode Execute(VMStackFrame context, VMPrimitiveOperand args)
        {
            //make the character iff, save it, and return their new neighbour id.

            var operand = (VMTS1MakeNewCharacterOperand)args;
            // SimsRedux: the VM's own random (not new Random()), so every client in a shared lot makes the same Sim.
            var vmContext = context.VM.Context;
            Func<int, int> next = (max) => (int)vmContext.NextRandom((ulong)Math.Max(0, max));
            var guid = SimitoneNeighbourGenerator.GenerateGUID(new uint[0], next);

            //get
            var color = context.Locals[operand.ColorLocal];
            var age = context.Locals[operand.AgeLocal];
            var gender = context.Locals[operand.GenderLocal];


            SimTemplateCreateInfo info;

            if (operand.AvatarType == 0)
            {
                var simtype = ((gender > 0) ? "f" : "m") + ((age < 18) ? "c" : "m");
                if (color < 0 || color >= ColorNames.Length) return VMPrimitiveExitCode.GOTO_FALSE; // SimsRedux: a bad skin colour fails instead of crashing
                var skin = ColorNames[color];
                var code = simtype;

                var bcf = Content.Content.Get().BCFGlobal;
                System.Collections.Generic.List<string> headList, bodyList;
                if (!bcf.CollectionsByName.ContainsKey("c") || !bcf.CollectionsByName["c"].ClothesByAvatarType.TryGetValue(simtype, out headList))
                    return VMPrimitiveExitCode.GOTO_FALSE;
                var heads = headList.ToList(); // (SimsRedux: a copy - the unusable ones are taken out below, and that used to empty the game's own lists)
                if (simtype[1] == 'c') simtype += "chd";
                if (!bcf.CollectionsByName.ContainsKey("b") || !bcf.CollectionsByName["b"].ClothesByAvatarType.TryGetValue(simtype, out bodyList))
                    return VMPrimitiveExitCode.GOTO_FALSE;
                var bodies = bodyList.ToList();

                //pick a random head and body.

                var tex = (TS1AvatarTextureProvider)Content.Content.Get().AvatarTextures;
                var texnames = tex.GetAllNames();

                var headTex = heads.Select(x => RemoveExt(texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, skin))))).ToList();
                var bodyTex = bodies.Select(x => RemoveExt(texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, skin))))).ToList();
                var handgroupTex = bodies.Select(x => (RemoveExt(texnames.FirstOrDefault(y => y == "huao" + FindHG(x))) ?? "huao" + skin).Substring(4)).ToList();

                for (int i = 0; i < heads.Count; i++)
                {
                    if (headTex[i] == null)
                    {
                        headTex.RemoveAt(i);
                        heads.RemoveAt(i--);
                    }
                }

                for (int i = 0; i < bodies.Count; i++)
                {
                    if (bodyTex[i] == null)
                    {
                        bodyTex.RemoveAt(i);
                        handgroupTex.RemoveAt(i);
                        bodies.RemoveAt(i--);
                    }
                }

                if (heads.Count == 0 || bodies.Count == 0) return VMPrimitiveExitCode.GOTO_FALSE; // SimsRedux: nothing usable - fail, not crash
                var headInd = (int)context.VM.Context.NextRandom((ulong)heads.Count);
                var bodyInd = (int)context.VM.Context.NextRandom((ulong)bodies.Count);

                var body = bodies[bodyInd];

                var ind = body.IndexOf("_");
                if (ind < 3) return VMPrimitiveExitCode.GOTO_FALSE;
                var bodyType = body.Substring(ind - 3, 3);
                code += bodyType;
                info = new SimTemplateCreateInfo(code, skin);

                info.BodyStringReplace[1] = body + ",BODY=" + bodyTex[bodyInd];
                info.BodyStringReplace[2] = heads[headInd] + ",HEAD-HEAD=" + headTex[headInd];

                var hand = (simtype[1] == 'c') ? 'u' : simtype[0];
                var hg = handgroupTex[bodyInd];
                info.BodyStringReplace[17] = "H" + hand + "LO,HAND=" + "huao" + hg;
                info.BodyStringReplace[18] = "H" + hand + "RO,HAND=" + "huao" + hg;
                info.BodyStringReplace[19] = "H" + hand + "LP,HAND=" + "huao" + hg;
                info.BodyStringReplace[20] = "H" + hand + "RP,HAND=" + "huao" + hg;
                info.BodyStringReplace[21] = "H" + hand + "LO,HAND=" + "huao" + hg;
                info.BodyStringReplace[22] = "H" + hand + "RC,HAND=" + "huao" + hg;
            } else
            {
                //index
                var index = context.StackObject.GetAttribute(8);

                Tuple<string, string>[] outfits;
                if (operand.AvatarType == 1)
                {
                    //dog
                    info = new SimTemplateCreateInfo("dog", gender > 0);
                    info.CustomGUID = 0x4A70DF92;
                    outfits = VMTS1PurchasableOutfitHelper.GetValidOutfits(null, -1);
                } else
                {
                    info = new SimTemplateCreateInfo("kat", gender > 0);
                    info.CustomGUID = 0x7BEA0977;
                    outfits = VMTS1PurchasableOutfitHelper.GetValidOutfits(null, -2);
                }


                if (outfits == null || index < 0 || index >= outfits.Length) return VMPrimitiveExitCode.GOTO_FALSE; // SimsRedux: bad outfit index fails instead of crashing
                info.BodyStringReplace[1] = outfits[index].Item1;
            }

            info.Name = context.StackObject.Name;
            info.Bio = "";
            for (int i = 0; i < 5; i++)
                info.PersonalityPoints[i] = 500; //todo: randomize

            var n = SimitoneNeighbourGenerator.CreateNeighbor(guid, info, next);

            context.StackObjectID = n.NeighbourID;
            return VMPrimitiveExitCode.GOTO_TRUE;
        }

        private string FindHG(string item)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(ind);
            return item;
        }

        private string RemoveExt(string item)
        {
            if (item == null) return null;
            var ind = item.LastIndexOf('.');
            if (ind != -1) return item.Substring(0, ind);
            return item;
        }

        private string ExtractID(string item, string skncol)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(0, ind);
            return item + skncol;
        }
    }

    public class VMTS1MakeNewCharacterOperand : VMPrimitiveOperand
    {
        public byte ColorLocal { get; set; }
        public byte AgeLocal { get; set; }
        public byte GenderLocal { get; set; }
        public byte AvatarType { get; set; }

        #region VMPrimitiveOperand Members
        public void Read(byte[] bytes)
        {
            using (var io = IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN))
            {
                ColorLocal = io.ReadByte();
                AgeLocal = io.ReadByte();
                GenderLocal = io.ReadByte();

                AvatarType = io.ReadByte();
            }
        }

        public void Write(byte[] bytes)
        {
            using (var io = new BinaryWriter(new MemoryStream(bytes)))
            {
                io.Write(ColorLocal);
                io.Write(AgeLocal);
                io.Write(GenderLocal);

                io.Write(AvatarType);
            }
        }
        #endregion
    }
}
