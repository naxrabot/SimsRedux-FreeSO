using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content.TS1;
using FSO.Files.Formats.IFF.Chunks;

namespace FSO.SimAntics.Utils
{
    public static class VMTS1PurchasableOutfitHelper
    {
        public static string[] OutfitTypes = new string[] { "b", "f", "s", "l", "w", "h" };
        public static short[] OutfitTypeToInd = new short[] { 1, 30, 31, 32, 33, 34 };

        public static Tuple<string, string>[] GetValidOutfits(VMAvatar avatar, short outfitType)
        {
            string skin = "";
            string collectionType, simtype;
            if (avatar == null || outfitType < 0)
            {
                collectionType = "b";
                simtype = (outfitType == -1) ? "dog" : "cat";
            }
            else {
                if (avatar.IsCat)
                {
                    collectionType = "b";
                    simtype = "cat";
                }
                else if (avatar.IsDog)
                {
                    collectionType = "b";
                    simtype = "dog";
                }
                else
                {
                    collectionType = OutfitTypes[outfitType];

                    var bodyStrings = avatar.Object.Resource.Get<FSO.Files.Formats.IFF.Chunks.STR>(avatar.Object.OBJ.BodyStringID);
                    simtype = bodyStrings.GetString(1).Substring(4);
                    simtype = simtype.Substring(0, simtype.IndexOf('_'));
                    skin = bodyStrings.GetString(14);
                }
            }
            
            var col = Content.Content.Get().BCFGlobal.CollectionsByName[collectionType];
            // (SimsRedux: a copy - the ones without a picture are taken out below, and that used to take them out of
            //  the game's own list for good, for every Sim; a Sim type with none of this kind has an empty list)
            List<string> ofType;
            var bodies = col.ClothesByAvatarType.TryGetValue(simtype, out ofType) ? ofType.ToList() : new List<string>();

            var tex = (TS1AvatarTextureProvider)Content.Content.Get().AvatarTextures;
            var texnames = tex.GetAllNames();

            var bodyTex = bodies.Select(x => RemoveExt(texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, skin))))).ToList();
            var handgroupTex = bodies.Select(x => (RemoveExt(texnames.FirstOrDefault(y => y == "huao" + FindHG(x))) ?? "huao" + skin).Substring(4)).ToList();

            var result = new List<Tuple<string, string>>();
            for (int i = 0; i < bodies.Count; i++)
            {
                if (bodyTex[i] == null)
                {
                    bodyTex.RemoveAt(i);
                    handgroupTex.RemoveAt(i);
                    bodies.RemoveAt(i--);
                } else
                {
                    result.Add(new Tuple<string, string>(
                        bodies[i] + ",BODY=" + bodyTex[i],
                        handgroupTex[i]
                        ));
                }
            }

            // SimsRedux: then The Sims Online's for this occasion (grown-ups of this gender and build; everyday, formal,
            // swimwear, sleepwear - not its costumes), after the Sims 1 ones, with the Sims 1 hands in the Sim's skin
            // tone. Only when this game loaded them - which the lot's content stamp makes the same for a lot worker
            // and all its players, so everyone builds the same list (the dresser picks from it by number).
            var tso = Content.Content.Get().ReduxTSOAvatarSet;
            var type = (simtype ?? "").ToLowerInvariant();
            if (tso != null && avatar != null && !avatar.IsPet && outfitType >= 0 && outfitType <= 3 && type.Length == 5 && type[1] == 'a')
            {
                var tsoSkin = (ReduxTSOAvatars.SkinIndex(skin) >= 0) ? skin.ToLowerInvariant() : "lgt";
                foreach (var o in tso.ForDresser(type[0], type.Substring(2), OutfitTypes[outfitType][0]))
                    result.Add(new Tuple<string, string>(o.Name + ",BODY=" + o.TextureName(tsoSkin), tsoSkin));
            }

            return result.ToArray();
        }

        public static short GetSuitIndex(VMAvatar avatar, short outfitType)
        {
            var validSuits = GetValidOutfits(avatar, outfitType);
            var bodyStrings = avatar.Object.Resource.Get<FSO.Files.Formats.IFF.Chunks.STR>(avatar.Object.OBJ.BodyStringID);

            var bsInd = OutfitTypeToInd[outfitType];
            var prevInd = Array.IndexOf(validSuits, bodyStrings.GetString(bsInd).Split(';')[0].ToLowerInvariant());
            return (short)prevInd;
        }

        public static short SetSuit(VMAvatar avatar, short outfitType, short newIndex)
        {
            var validSuits = GetValidOutfits(avatar, outfitType);
            var bodyStrings = avatar.Object.Resource.Get<FSO.Files.Formats.IFF.Chunks.STR>(avatar.Object.OBJ.BodyStringID);

            var bsInd = OutfitTypeToInd[outfitType];
            var oldSuit = bodyStrings.GetString(bsInd).Split(';')[0];
            var prevInd = Array.FindIndex(validSuits, x => x.Item1.ToLowerInvariant() == bodyStrings.GetString(bsInd).Split(';')[0].ToLowerInvariant());

            var newSuit = validSuits[newIndex];
            bodyStrings.SetString(bsInd, newSuit.Item1, STRLangCode.EnglishUS);

            if (outfitType == 0 && newSuit.Item2 != "")
            {
                //right now only replace the handgroup when changing the default outfit.
                var hg = newSuit.Item2;
                bodyStrings.SetString(17, TilEQ(bodyStrings.GetString(17, STRLangCode.EnglishUS)) + "huao" + hg, STRLangCode.EnglishUS);
                bodyStrings.SetString(18, TilEQ(bodyStrings.GetString(18, STRLangCode.EnglishUS)) + "huao" + hg, STRLangCode.EnglishUS);
                bodyStrings.SetString(19, TilEQ(bodyStrings.GetString(19, STRLangCode.EnglishUS)) + "huap" + hg, STRLangCode.EnglishUS);
                bodyStrings.SetString(20, TilEQ(bodyStrings.GetString(20, STRLangCode.EnglishUS)) + "huap" + hg, STRLangCode.EnglishUS);
                bodyStrings.SetString(21, TilEQ(bodyStrings.GetString(21, STRLangCode.EnglishUS)) + "huac" + hg, STRLangCode.EnglishUS);
                bodyStrings.SetString(22, TilEQ(bodyStrings.GetString(22, STRLangCode.EnglishUS)) + "huac" + hg, STRLangCode.EnglishUS);
            }

            Content.Content.Get().Neighborhood.AvatarChanged(avatar.Object.OBJ.GUID);

            return (short)prevInd;
        }

        public static string TilEQ(string item)
        {
            var ind = item.IndexOf('=');
            if (ind != -1) item = item.Substring(ind);
            return item;
        }

        private static string FindHG(string item)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(ind);
            return item;
        }

        private static string RemoveExt(string item)
        {
            if (item == null) return null;
            var ind = item.LastIndexOf('.');
            if (ind != -1) return item.Substring(0, ind);
            return item;
        }

        private static string ExtractID(string item, string skncol)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(0, ind);
            return item + skncol;
        }

        private static string InsertSkinColor(string name, string skncol)
        {
            var ind = name.IndexOf('_');
            if (ind != -1) name = name.Insert(ind, skncol);
            return name;
        }
    }
}
