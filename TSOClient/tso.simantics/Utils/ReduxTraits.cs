using System;
using System.Collections.Generic;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 16.9 (Part B #1b): traits - the lot's side. Simitone's Utils/ReduxTraits.cs keeps each Sim's traits in a
    /// side file next to the neighbourhood (redux_traits.txt, by Sim GUID) and puts them on the lot with VMNetReduxTraitsCmd
    /// (VM command 74) whenever a Sim is on the lot without them (or with old ones).
    ///
    /// On the lot they're kept on the Sim themselves, as person-object attributes 80-83 (80-82: up to three traits, 83:
    /// the orientation; 0 = none) - the Sims 1 person scripts don't use attributes that high (ReduxWooHoo uses 70-77).
    /// Attributes are saved with the lot and sent to every player joining, so every game on a visit has the same traits at
    /// the same moment, and nothing new goes into the save format. Everything here reads only those attributes, the lot's
    /// clock and the Sims, so every game (and the town's lot workers) makes the same choices.
    ///
    /// The codes below never change meaning (a new trait gets a new code). Normal traits 1-39, orientations 40-49.
    /// </summary>
    public static class VMReduxTraits
    {
        public const int FirstAttr = 80, Slots = 3, OrientAttr = 83;

        public enum T : byte
        {
            None = 0,
            Cheerful = 1, HotHeaded = 2, Gloomy = 3, Goofball = 4, Snob = 5, Loner = 6, Foodie = 7, NeatFreak = 8, Slob = 9,
            Lazy = 10, Active = 11, Bookworm = 12, NightOwl = 13, EarlyBird = 14, Romantic = 15, Flirty = 16, Shy = 17,
            FamilyOriented = 18, Jealous = 19, Loyal = 20, HatesChildren = 21, Creative = 22, Gamer = 23, GreenThumb = 24,
            AnimalLover = 25, Shameless = 26, Daring = 27, Polyamorous = 28, FreeSpirit = 29,
            // orientations (one at most, not one of the three)
            Straight = 40, Gay = 41, Bisexual = 42, Asexual = 43, Aromantic = 44,
        }

        /// <summary>The trait ids (lower-case, as Simitone's ReduxTraits and the side file use them), by code.</summary>
        public static readonly string[] Ids = new string[64];

        /// <summary>Traits that can't go together (on one Sim).</summary>
        public static readonly T[][] Conflicts =
        {
            new[] { T.NeatFreak, T.Slob }, new[] { T.Lazy, T.Active }, new[] { T.NightOwl, T.EarlyBird },
            new[] { T.Cheerful, T.Gloomy }, new[] { T.Loner, T.Flirty }, new[] { T.Shy, T.Flirty }, new[] { T.Shy, T.Shameless },
            new[] { T.FamilyOriented, T.HatesChildren }, new[] { T.Snob, T.Slob },
            new[] { T.Loyal, T.Polyamorous }, new[] { T.Loyal, T.FreeSpirit }, new[] { T.Jealous, T.Polyamorous },
            new[] { T.Jealous, T.FreeSpirit }, new[] { T.Polyamorous, T.FreeSpirit },
        };

        /// <summary>Traits that rub each other up the wrong way between two Sims (friction).</summary>
        public static readonly T[][] Clashes =
        {
            new[] { T.Snob, T.Goofball }, new[] { T.NeatFreak, T.Slob }, new[] { T.Cheerful, T.Gloomy },
        };

        static VMReduxTraits()
        {
            foreach (T t in Enum.GetValues(typeof(T)))
            {
                if (t == T.None) continue;
                Ids[(int)t] = IdFrom(t.ToString());
            }
        }

        /// <summary>"HotHeaded" -> "hot-headed", "NeatFreak" -> "neat-freak".</summary>
        private static string IdFrom(string name)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                var c = name[i];
                if (char.IsUpper(c) && i > 0) sb.Append('-');
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        public static byte Code(string id)
        {
            id = (id ?? "").Trim().ToLowerInvariant();
            for (int i = 1; i < Ids.Length; i++) if (Ids[i] == id) return (byte)i;
            return 0;
        }

        public static string IdOf(int code) { return (code > 0 && code < Ids.Length) ? Ids[code] : null; }
        public static bool IsOrientation(int code) { return code >= 40 && code < 50 && IdOf(code) != null; }
        public static bool IsNormal(int code) { return code >= 1 && code < 40 && IdOf(code) != null; }

        public static bool Conflict(int a, int b)
        {
            foreach (var c in Conflicts)
                if (((int)c[0] == a && (int)c[1] == b) || ((int)c[0] == b && (int)c[1] == a)) return true;
            return false;
        }

        public static bool Clash(VMAvatar a, VMAvatar b)
        {
            if (a == null || b == null) return false;
            foreach (var c in Clashes)
                if ((Has(a, c[0]) && Has(b, c[1])) || (Has(a, c[1]) && Has(b, c[0]))) return true;
            return false;
        }

        // ------------------------------------------------------------------ on the Sim

        public static bool Has(VMAvatar a, T t)
        {
            if (a == null || a.IsPet || t == T.None) return false;
            if ((int)t >= 40) return a.GetAttribute(OrientAttr) == (short)t;
            for (int i = 0; i < Slots; i++) if (a.GetAttribute(FirstAttr + i) == (short)t) return true;
            return false;
        }

        /// <summary>The Sim's traits on the lot (codes, no zeros).</summary>
        public static List<int> Codes(VMAvatar a)
        {
            var l = new List<int>();
            if (a == null || a.IsPet) return l;
            for (int i = 0; i < Slots; i++)
            {
                int c = a.GetAttribute(FirstAttr + i);
                if (IsNormal(c) && !l.Contains(c)) l.Add(c);
            }
            return l;
        }

        /// <summary>The orientation code on the lot (0: none - attracted to everyone, as in Sims 1).</summary>
        public static int Orientation(VMAvatar a)
        {
            if (a == null || a.IsPet) return 0;
            int c = a.GetAttribute(OrientAttr);
            return IsOrientation(c) ? c : 0;
        }

        /// <summary>Anything at all on this Sim (a quick test before the work).</summary>
        public static bool Any(VMAvatar a)
        {
            if (a == null || a.IsPet) return false;
            return a.GetAttribute(FirstAttr) != 0 || a.GetAttribute(FirstAttr + 1) != 0 || a.GetAttribute(FirstAttr + 2) != 0 || a.GetAttribute(OrientAttr) != 0;
        }

        /// <summary>Same as on the lot already?</summary>
        public static bool Same(VMAvatar a, byte[] codes, byte orient)
        {
            if (a == null) return false;
            for (int i = 0; i < Slots; i++)
            {
                int want = (codes != null && i < codes.Length) ? codes[i] : 0;
                if (a.GetAttribute(FirstAttr + i) != want) return false;
            }
            return a.GetAttribute(OrientAttr) == orient;
        }

        /// <summary>Puts traits on the Sim (only VMNetReduxTraitsCmd calls this): unknown codes, repeats and conflicts dropped.</summary>
        public static void Set(VMAvatar a, byte[] codes, byte orient)
        {
            if (a == null || a.IsPet) return;
            var keep = new List<int>();
            if (codes != null)
                foreach (var c in codes)
                {
                    if (!IsNormal(c) || keep.Contains(c) || keep.Count >= Slots) continue;
                    bool bad = false;
                    foreach (var k in keep) if (Conflict(k, c)) bad = true;
                    if (!bad) keep.Add(c);
                }
            for (int i = 0; i < Slots; i++) a.SetAttribute(FirstAttr + i, (short)(i < keep.Count ? keep[i] : 0));
            a.SetAttribute(OrientAttr, (short)(IsOrientation(orient) ? orient : 0));
        }

        // ------------------------------------------------------------------ (who a Sim is attracted to: ReduxAttraction, from the orientation)

        public static bool Female(VMAvatar a) { return a != null && (a.GetPersonData(VMPersonDataVariable.Gender) & 1) != 0; }

        // ------------------------------------------------------------------ what kind of choice it is (from its name)

        [Flags]
        private enum K : uint
        {
            None = 0, Clean = 1, Exercise = 2, Rest = 4, Sleep = 8, Read = 16, Game = 32, Create = 64, Garden = 128,
            Food = 256, Joke = 512, Mean = 1024, Romance = 2048, TV = 4096, Swim = 8192,
        }

        private static readonly Dictionary<string, K> KindCache = new Dictionary<string, K>();

        private static bool Has(string n, params string[] words)
        {
            foreach (var w in words) if (n.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        private static K KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return K.None;
            K k;
            lock (KindCache)
            {
                if (KindCache.TryGetValue(name, out k)) return k;
            }
            var n = name.Trim().ToLowerInvariant();
            k = K.None;
            if (n == "sleep" || n.StartsWith("sleep")) k |= K.Sleep;
            if (n.StartsWith("nap") || n.StartsWith("sit") || n.StartsWith("rest") || Has(n, "relax", "lounge", "lie down")) k |= K.Rest;
            if (n.StartsWith("watch") || Has(n, "watch tv", " tv")) k |= K.TV | K.Rest;
            bool eat = n.StartsWith("eat") || Has(n, " eat", "/eat");
            if (!eat && Has(n, "clean", "mop", "empty trash", "take out trash", "tidy", "wash dish", "scrub") || n == "empty") k |= K.Clean;
            if (Has(n, "work out", "workout", "exercise", "jog", "lift weight", "basketball", "aerobic", "dance", "jump")) k |= K.Exercise;
            if (Has(n, "swim", "dive")) k |= K.Swim | K.Exercise;
            if (n.StartsWith("read") || Has(n, "/read", "study", "book")) k |= K.Read;
            if (Has(n, "game", "pinball", "arcade", "chess", "video", "joystick")) k |= K.Game;
            if (n.StartsWith("sing") || Has(n, "paint", "piano", "guitar", "sculpt", "draw", "drum", " sing", "/sing", "write", "compose", "violin", "instrument")) k |= K.Create;
            if (n == "water" || n.StartsWith("water ") || n.StartsWith("tend") || Has(n, "garden", "weed", "prune", "harvest")) k |= K.Garden;
            if (eat || Has(n, "cook", "meal", "snack", "grill", "serve", "breakfast", "lunch", "dinner", "dessert")) k |= K.Food;
            if (Has(n, "joke", "tickle", "entertain", "puppet", "juggle", "funny", "goof", "silly")) k |= K.Joke;
            if (Has(n, "insult", "slap", "tease", "annoy", "shove", "fight", "attack", "taunt", "scold", "/nag", "poke", "raspberry") || n.StartsWith("nag")) k |= K.Mean;
            if (Has(n, "kiss", "flirt", "intimate", "romantic", "back rub", "dance.../slow", "proposition", "serenade", "cuddle", "woohoo")) k |= K.Romance;
            lock (KindCache)
            {
                if (KindCache.Count > 6000) KindCache.Clear();
                KindCache[name] = k;
            }
            return k;
        }

        // ------------------------------------------------------------------ autonomy (VMFindBestAction)

        private static readonly int[] NoCodes = new int[0];

        /// <summary>
        /// How much the Sim's traits make them want this choice (1 = no change; a multiplier on Sims 1's own score). Only
        /// the Sim's traits on the lot, the choice's name, who it's done to and the lot's clock - the same on every game.
        /// </summary>
        public static float AutonomyWeight(VM vm, VMAvatar me, VMEntity obj, string name)
        {
            if (vm == null || !vm.TS1 || me == null || me.IsPet || !Any(me)) return 1f;
            var k = KindOf(name);
            var other = obj as VMAvatar;
            bool social = other != null && other != me && !other.IsPet;
            bool pet = other != null && other.IsPet;
            int hour = vm.Context.Clock.Hours;
            bool late = hour >= 21 || hour < 3, morning = hour >= 5 && hour < 10, bedtimeEarly = hour >= 20 && hour <= 23;
            bool fun = (k & (K.Game | K.Create | K.Read | K.TV | K.Exercise | K.Joke)) != 0;
            float w = 1f;

            // (who a Sim is attracted to - the orientation - is ReduxAttraction's: romantic choices toward anyone else aren't offered at all)
            for (int i = 0; i < Slots; i++)
            {
                switch ((T)me.GetAttribute(FirstAttr + i))
                {
                    case T.Cheerful: if (social) w *= (k & K.Mean) != 0 ? 0.5f : 1.2f; break;
                    case T.HotHeaded: if ((k & K.Mean) != 0) w *= 1.8f; break;
                    case T.Gloomy: if (social) w *= 0.85f; if ((k & K.Joke) != 0) w *= 0.6f; break;
                    case T.Goofball: if ((k & K.Joke) != 0) w *= 2f; if ((k & (K.TV | K.Game)) != 0) w *= 1.2f; break;
                    case T.Snob: if (social && Has(other, T.Goofball)) w *= 0.5f; if ((k & K.Joke) != 0) w *= 0.6f; break;
                    case T.Loner: if (social) w *= 0.5f; break;
                    case T.Foodie: if ((k & K.Food) != 0) w *= 1.5f; break;
                    case T.NeatFreak: if ((k & K.Clean) != 0) w *= 2f; if (social && Has(other, T.Slob)) w *= 0.6f; break;
                    case T.Slob: if ((k & K.Clean) != 0) w *= 0.35f; break;
                    case T.Lazy: if ((k & K.Rest) != 0) w *= 1.5f; if ((k & K.Exercise) != 0) w *= 0.4f; if ((k & K.Clean) != 0) w *= 0.7f; break;
                    case T.Active: if ((k & K.Exercise) != 0) w *= 2f; if ((k & K.Rest) != 0) w *= 0.7f; break;
                    case T.Bookworm: if ((k & K.Read) != 0) w *= 2.2f; break;
                    case T.NightOwl:
                        if ((k & K.Sleep) != 0) w *= bedtimeEarly || hour == 0 ? 0.5f : (morning ? 1.3f : 1f);
                        else if (fun && late) w *= 1.4f;
                        break;
                    case T.EarlyBird:
                        if ((k & K.Sleep) != 0) w *= bedtimeEarly ? 1.6f : (morning ? 0.6f : 1f);
                        else if (morning && (k & (K.Exercise | K.Garden | K.Read)) != 0) w *= 1.4f;
                        else if (fun && (hour >= 23 || hour < 4)) w *= 0.6f;
                        break;
                    case T.Romantic: if (social && (k & K.Romance) != 0) w *= 1.7f; break;
                    case T.Flirty: if (social && (k & K.Romance) != 0) w *= 1.9f; break;
                    case T.Shy: if (social) w *= (k & K.Romance) != 0 ? 0.6f : 0.7f; break;
                    case T.FamilyOriented:
                        if (social && other.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == me.GetPersonData(VMPersonDataVariable.TS1FamilyNumber)) w *= 1.6f;
                        if (social && other.IsChild) w *= 1.4f;
                        break;
                    case T.Loyal:
                        if (social)
                        {
                            var rel = ReduxWooHoo.Rel(me, other);
                            if (rel != null && rel.Count > 0 && rel[0] >= 50) w *= 1.5f;
                        }
                        break;
                    case T.HatesChildren: if (social && other.IsChild) w *= 0.25f; break;
                    case T.Creative: if ((k & K.Create) != 0) w *= 2f; break;
                    case T.Gamer: if ((k & K.Game) != 0) w *= 2.2f; if ((k & K.TV) != 0) w *= 1.2f; break;
                    case T.GreenThumb: if ((k & K.Garden) != 0) w *= 2.2f; break;
                    case T.AnimalLover: if (pet) w *= 2.2f; break;
                    case T.Daring: if ((k & K.Swim) != 0) w *= 1.4f; else if ((k & K.Exercise) != 0) w *= 1.15f; break;
                }
            }
            return Math.Max(0.05f, Math.Min(3.5f, w));
        }

        /// <summary>Cooking menus (ReduxCooking): a Foodie cooks as if 3 levels better at the dish.</summary>
        public static int CookBonus(VMAvatar a) { return Has(a, T.Foodie) ? 3 : 0; }

        // ------------------------------------------------------------------ trait-only choices (VMNetReduxTraitsCmd Act)

        public enum Choice : byte { None = 0, TellJoke = 1, ReadGoodBook = 2, SavourSnack = 3, TendLovingly = 4, Cuddle = 5 }

        public static T TraitFor(Choice c)
        {
            switch (c)
            {
                case Choice.TellJoke: return T.Goofball;
                case Choice.ReadGoodBook: return T.Bookworm;
                case Choice.SavourSnack: return T.Foodie;
                case Choice.TendLovingly: return T.GreenThumb;
                case Choice.Cuddle: return T.AnimalLover;
            }
            return T.None;
        }

        public static string Label(Choice c)
        {
            switch (c)
            {
                case Choice.TellJoke: return "Tell a Joke";
                case Choice.ReadGoodBook: return "Read a Good Book";
                case Choice.SavourSnack: return "Savour a Snack";
                case Choice.TendLovingly: return "Tend Lovingly";
                case Choice.Cuddle: return "Cuddle";
            }
            return "";
        }

        private static bool Matches(Choice c, string n)
        {
            n = (n ?? "").Trim().ToLowerInvariant();
            switch (c)
            {
                case Choice.TellJoke: return n.Contains("joke");
                case Choice.ReadGoodBook: return n.StartsWith("read");
                case Choice.SavourSnack: return n.Contains("snack");
                case Choice.TendLovingly: return n == "water" || n.StartsWith("water ");
                case Choice.Cuddle: return n.StartsWith("pet") || n.Contains("cuddle") || n.Contains("scratch") || n.StartsWith("play");
            }
            return false;
        }

        /// <summary>The piece of the object with the game's own menu entry for this choice, and the entry (null: none).</summary>
        public static VMEntity FindFor(VMEntity obj, Choice c, out TTABInteraction found)
        {
            found = null;
            var group = obj?.MultitileGroup?.Objects ?? (obj == null ? new List<VMEntity>() : new List<VMEntity> { obj });
            foreach (var o in group)
            {
                if (o?.TreeTable == null || o.TreeTableStrings == null) continue;
                foreach (var ia in o.TreeTable.Interactions)
                    if (Matches(c, o.TreeTableStrings.GetString((int)ia.TTAIndex))) { found = ia; return o; }
            }
            return null;
        }

        /// <summary>The trait-only choices this Sim has on that object or Sim (empty with no traits).</summary>
        public static List<Choice> ChoicesFor(VMAvatar me, VMEntity obj)
        {
            var l = new List<Choice>();
            if (me == null || me.IsPet || obj == null || !Any(me)) return l;
            var other = obj as VMAvatar;
            foreach (Choice c in Enum.GetValues(typeof(Choice)))
            {
                if (c == Choice.None || !Has(me, TraitFor(c))) continue;
                if (c == Choice.TellJoke) { if (other != null && other != me && !other.IsPet && !other.Dead) l.Add(c); continue; }
                if (c == Choice.Cuddle) { if (other != null && other.IsPet && !other.Dead) l.Add(c); continue; }
                if (other != null) continue;
                TTABInteraction ia;
                if (FindFor(obj, c, out ia) != null) l.Add(c);
            }
            return l;
        }

        private static void Motive(VMAvatar a, VMMotive m, int add)
        {
            if (a == null) return;
            a.SetMotiveData(m, (short)Math.Max(-100, Math.Min(100, a.GetMotiveData(m) + add)));
        }

        private static void Daily(VMAvatar a, VMAvatar b, int add)
        {
            var r = ReduxWooHoo.Rel(a, b);
            if (r == null || r.Count == 0) return;
            r[0] = (short)Math.Max(-100, Math.Min(100, r[0] + add));
        }

        /// <summary>Does the choice (the Sim goes and does the game's own action for it, then the trait's bonus). Null: done; else why not.</summary>
        public static string Act(VM vm, VMAvatar me, VMEntity obj, Choice c)
        {
            if (vm == null || me == null || obj == null || me.Dead) return "";
            if (!ChoicesFor(me, obj).Contains(c)) return "";
            var other = obj as VMAvatar;
            TTABInteraction ia;
            var piece = FindFor(obj, c, out ia);
            bool pushed = false;
            if (piece != null)
            {
                var action = piece.GetAction((int)ia.TTAIndex, me, vm.Context, false, new short[4]);
                if (action != null)
                {
                    // (the check trees share this Sim's temp registers - put them back exactly as they were)
                    var th = me.Thread;
                    var temps = (short[])th.TempRegisters.Clone();
                    var tempXL = (int[])th.TempXL.Clone();
                    var hide = me.ObjectData[(int)VMStackObjectVariable.HideInteraction];
                    List<VMPieMenuInteraction> ok = null;
                    try { me.ObjectData[(int)VMStackObjectVariable.HideInteraction] = 0; ok = th.CheckAction(action, false); }
                    catch (Exception) { ok = null; }
                    finally
                    {
                        Array.Copy(temps, th.TempRegisters, Math.Min(temps.Length, th.TempRegisters.Length));
                        Array.Copy(tempXL, th.TempXL, Math.Min(tempXL.Length, th.TempXL.Length));
                        me.ObjectData[(int)VMStackObjectVariable.HideInteraction] = hide;
                    }
                    if (ok != null)
                    {
                        // (as a player's click does it - VMNetInteractionCmd)
                        short param = ok.Count > 0 ? ok[0].Param0 : (short)0;
                        var real = piece.GetAction((int)ia.TTAIndex, me, vm.Context, false, new short[] { param, 0, 0, 0 });
                        if (real != null) { th.EnqueueAction(real); pushed = true; }
                    }
                }
            }
            // (the game's own action isn't on offer right now: they go over and do it anyway, for a moment)
            if (!pushed) ReduxWooHoo.PushWait(vm, me, obj, Math.Max(1, vm.Context.Clock.TicksPerMinute) * 10, Label(c));

            switch (c)
            {
                case Choice.TellJoke:
                    Motive(me, VMMotive.Social, 5);
                    Motive(other, VMMotive.Social, 5);
                    if (Has(other, T.Snob) || Has(other, T.Gloomy))
                    {
                        Daily(me, other, 2);
                        Daily(other, me, Has(other, T.Snob) ? -3 : 1);
                    }
                    else
                    {
                        Motive(other, VMMotive.Fun, 10);
                        Daily(me, other, 3);
                        Daily(other, me, 3);
                    }
                    break;
                case Choice.ReadGoodBook: Motive(me, VMMotive.Fun, 12); break;
                case Choice.SavourSnack: Motive(me, VMMotive.Hunger, 8); Motive(me, VMMotive.Fun, 8); break;
                case Choice.TendLovingly: Motive(me, VMMotive.Fun, 8); Motive(me, VMMotive.Room, 5); break;
                case Choice.Cuddle: Motive(me, VMMotive.Fun, 10); Motive(me, VMMotive.Social, 8); Motive(other, VMMotive.Social, 10); break;
            }
            return null;
        }
    }
}
