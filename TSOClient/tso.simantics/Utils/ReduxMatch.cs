using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content.TS1;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Primitives;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux 17.0 (Part B #1b extras, R2): family and matchmaking - the lot's side. Simitone's Utils/ReduxMatch.cs and
    /// Utils/ReduxFamily.cs are the game's side (the side files, the dating app, the family tree, adoption, co-parenting).
    /// Everything that changes the lot goes through VMNetReduxMatchCmd (VM command 78).
    ///
    /// On the lot, each grown-up Sim's turn-ons and turn-off are kept on the Sim as person-object attributes 90-92 (two
    /// turn-ons and a turn-off; 0 = none - children have none). Attribute 93 is when the Sim last did a closeness choice
    /// (Spend Quality Time / Cuddle - the lot's clock in game minutes + 1, like VMReduxTraits.ActAttr); 94 is spare. The
    /// Sims 1 person scripts don't use attributes that high (WooHoo 70-77, attraction 78, traits 80-84, R1 85-89, R3 95-99).
    /// Attributes are saved with the lot and sent to every player joining, so every game on a visit reads the same.
    ///
    /// Chemistry (0-100) is worked out from what's on the lot only - both Sims' traits, turn-ons and turn-offs, their
    /// personality, skills, job and Sims 1 star sign - so every game shows (and uses) the same number. Other features can
    /// use Chemistry(a, b). The game also works it out for Sims who aren't on the lot (the dating app, the Relationships
    /// page) from the neighbourhood's person data and its own side files: Profile and Chemistry(Profile, Profile).
    ///
    /// The codes below never change meaning (a new turn-on gets a new code).
    /// </summary>
    public static class VMReduxMatch
    {
        public const int OnAttr = 90, On2Attr = 91, OffAttr = 92, CloseAttr = 93;

        // ------------------------------------------------------------------ turn-ons and turn-offs

        public enum Turn : byte
        {
            None = 0,
            Funny = 1, Cheerful = 2, Kind = 3, Outgoing = 4, Shy = 5, Sporty = 6, LaidBack = 7, Tidy = 8, Creative = 9,
            Brainy = 10, GreatCook = 11, Handy = 12, Charming = 13, Romantic = 14, HardWorking = 15, Wealthy = 16,
            AnimalLover = 17, GreenThumb = 18, Gamer = 19, NightOwl = 20, EarlyBird = 21, FamilyType = 22, Loyal = 23,
            Adventurous = 24, Snobby = 25, Messy = 26, Grumpy = 27, Loner = 28,
        }

        /// <summary>Codes 1 to TurnMax are turn-ons (and can be a turn-off).</summary>
        public const int TurnMax = 28;

        /// <summary>The id (the side file), the name and what it means (what the other Sim has to be), by code.</summary>
        public static readonly string[] TurnIds = new string[TurnMax + 1], TurnNames = new string[TurnMax + 1], TurnAbout = new string[TurnMax + 1];

        static VMReduxMatch()
        {
            T(Turn.Funny, "funny", "Funny", "a Goofball, or very playful");
            T(Turn.Cheerful, "cheerful", "Cheerful", "a Cheerful Sim");
            T(Turn.Kind, "kind", "Kind", "a very nice Sim");
            T(Turn.Outgoing, "outgoing", "Outgoing", "very outgoing, not Shy or a Loner");
            T(Turn.Shy, "shy", "Shy types", "Shy, or quiet and reserved");
            T(Turn.Sporty, "sporty", "Sporty", "Active, or fit (Body skill 5+)");
            T(Turn.LaidBack, "laid-back", "Laid-back", "Lazy, or not very active");
            T(Turn.Tidy, "tidy", "Tidy", "a Neat Freak, or very neat");
            T(Turn.Creative, "creative", "Creative", "Creative, or Creativity skill 5+");
            T(Turn.Brainy, "brainy", "Brainy", "a Bookworm, or Logic skill 5+");
            T(Turn.GreatCook, "great-cook", "Great cook", "a Foodie, or Cooking skill 5+");
            T(Turn.Handy, "handy", "Handy", "Mechanical skill 5+");
            T(Turn.Charming, "charming", "Charming", "Charisma skill 5+");
            T(Turn.Romantic, "romantic", "Romantic", "Romantic or Flirty");
            T(Turn.HardWorking, "hard-working", "Has a job", "anyone with a job");
            T(Turn.Wealthy, "wealthy", "Money", "a well-paid job (level 6 or higher)");
            T(Turn.AnimalLover, "animal-lover", "Animal lovers", "an Animal Lover");
            T(Turn.GreenThumb, "green-thumb", "Green thumbs", "a Green Thumb");
            T(Turn.Gamer, "gamer", "Gamers", "a Gamer");
            T(Turn.NightOwl, "night-owl", "Night owls", "a Night Owl");
            T(Turn.EarlyBird, "early-bird", "Early birds", "an Early Bird");
            T(Turn.FamilyType, "family-type", "Family types", "Family-Oriented");
            T(Turn.Loyal, "loyal", "Loyal", "a Loyal Sim");
            T(Turn.Adventurous, "adventurous", "Adventurous", "Daring or a Free Spirit");
            T(Turn.Snobby, "snobby", "Snobs", "a Snob");
            T(Turn.Messy, "messy", "Messy", "a Slob, or very messy");
            T(Turn.Grumpy, "grumpy", "Grumpy", "Hot-Headed or Gloomy");
            T(Turn.Loner, "loner", "Loners", "a Loner");
        }

        private static void T(Turn t, string id, string name, string about)
        {
            TurnIds[(int)t] = id; TurnNames[(int)t] = name; TurnAbout[(int)t] = about;
        }

        public static bool IsTurn(int code) { return code >= 1 && code <= TurnMax && TurnIds[code] != null; }

        public static byte TurnCode(string id)
        {
            id = (id ?? "").Trim().ToLowerInvariant();
            for (int i = 1; i <= TurnMax; i++) if (TurnIds[i] == id) return (byte)i;
            return 0;
        }

        public static string TurnName(int code) { return IsTurn(code) ? TurnNames[code] : ""; }

        // ------------------------------------------------------------------ star signs (Sims 1)

        /// <summary>The Sims 1 star signs, as person data TS1Zodiac keeps them (1 Aries ... 12 Pisces; 0 none).</summary>
        public static readonly string[] Signs =
        {
            "", "Aries", "Taurus", "Gemini", "Cancer", "Leo", "Virgo", "Libra", "Scorpio", "Sagittarius", "Capricorn", "Aquarius", "Pisces",
        };

        /// <summary>Who each sign is attracted to and put off by (the Sims' own table, 1-based like Signs).</summary>
        private static readonly int[][] SignLikes =
        {
            new int[0], new[] { 3, 2 }, new[] { 1, 7 }, new[] { 12, 6 }, new[] { 2, 8 }, new[] { 9, 4 }, new[] { 11, 9 },
            new[] { 6, 4 }, new[] { 12, 5 }, new[] { 12, 10 }, new[] { 11, 2 }, new[] { 10, 9 }, new[] { 8, 3 },
        };
        private static readonly int[][] SignDislikes =
        {
            new int[0], new[] { 4, 7 }, new[] { 6, 4 }, new[] { 10, 1 }, new[] { 3, 1 }, new[] { 10, 3 }, new[] { 5, 2 },
            new[] { 12, 8 }, new[] { 7, 11 }, new[] { 7, 8 }, new[] { 5, 3 }, new[] { 8, 6 }, new[] { 5, 1 },
        };

        public static string SignName(int sign) { return sign >= 1 && sign <= 12 ? Signs[sign] : ""; }

        /// <summary>+1 a likes b's sign, -1 put off by it, 0 neither (or a sign unknown).</summary>
        public static int SignMatch(int a, int b)
        {
            if (a < 1 || a > 12 || b < 1 || b > 12) return 0;
            if (Array.IndexOf(SignLikes[a], b) >= 0) return 1;
            if (Array.IndexOf(SignDislikes[a], b) >= 0) return -1;
            return 0;
        }

        // ------------------------------------------------------------------ what a Sim is like (on the lot or not)

        /// <summary>Everything chemistry looks at for one Sim.</summary>
        public class Profile
        {
            public bool Female, Child;
            public List<int> Traits = new List<int>();
            public int Orientation;
            public byte On1, On2, Off;
            public int Nice, Active, Playful, Outgoing, Neat;
            public int Cooking, Charisma, Mechanical, Creativity, Body, Logic;
            public int JobType, JobLevel, Sign;

            public bool Has(VMReduxTraits.T t) { return Traits.Contains((int)t) || ((int)t >= 40 && Orientation == (int)t); }
        }

        private static int PD(short[] pd, VMPersonDataVariable v) { int i = (int)v; return (pd != null && i < pd.Length) ? pd[i] : 0; }

        /// <summary>A profile from person data (a neighbour's, or a Sim's on the lot) and the traits and turn-ons kept for them.</summary>
        public static Profile ProfileOf(short[] pd, IEnumerable<int> traits, int orientation, byte on1, byte on2, byte off)
        {
            var p = new Profile
            {
                Female = (PD(pd, VMPersonDataVariable.Gender) & 1) != 0,
                Child = PD(pd, VMPersonDataVariable.PersonsAge) < 18,
                Orientation = VMReduxTraits.IsOrientation(orientation) ? orientation : 0,
                Nice = PD(pd, VMPersonDataVariable.NicePersonality), Active = PD(pd, VMPersonDataVariable.ActivePersonality),
                Playful = PD(pd, VMPersonDataVariable.PlayfulPersonality), Outgoing = PD(pd, VMPersonDataVariable.OutgoingPersonality),
                Neat = PD(pd, VMPersonDataVariable.NeatPersonality),
                Cooking = PD(pd, VMPersonDataVariable.CookingSkill), Charisma = PD(pd, VMPersonDataVariable.CharismaSkill),
                Mechanical = PD(pd, VMPersonDataVariable.MechanicalSkill), Creativity = PD(pd, VMPersonDataVariable.CreativitySkill),
                Body = PD(pd, VMPersonDataVariable.BodySkill), Logic = PD(pd, VMPersonDataVariable.LogicSkill),
                JobType = PD(pd, VMPersonDataVariable.JobType), JobLevel = PD(pd, VMPersonDataVariable.JobPromotionLevel),
                Sign = PD(pd, VMPersonDataVariable.TS1Zodiac),
            };
            if (traits != null) foreach (var t in traits) if (VMReduxTraits.IsNormal(t) && !p.Traits.Contains(t)) p.Traits.Add(t);
            if (!p.Child)
            {
                var clean = Clean(on1, on2, off);
                p.On1 = clean[0]; p.On2 = clean[1]; p.Off = clean[2];
            }
            return p;
        }

        private static readonly VMPersonDataVariable[] ProfileVars =
        {
            VMPersonDataVariable.Gender, VMPersonDataVariable.PersonsAge, VMPersonDataVariable.NicePersonality, VMPersonDataVariable.ActivePersonality,
            VMPersonDataVariable.PlayfulPersonality, VMPersonDataVariable.OutgoingPersonality, VMPersonDataVariable.NeatPersonality,
            VMPersonDataVariable.CookingSkill, VMPersonDataVariable.CharismaSkill, VMPersonDataVariable.MechanicalSkill,
            VMPersonDataVariable.CreativitySkill, VMPersonDataVariable.BodySkill, VMPersonDataVariable.LogicSkill,
            VMPersonDataVariable.JobType, VMPersonDataVariable.JobPromotionLevel, VMPersonDataVariable.TS1Zodiac,
        };

        /// <summary>A Sim on the lot (their traits and turn-ons from the lot's attributes).</summary>
        public static Profile ProfileOf(VMAvatar a)
        {
            if (a == null || a.IsPet) return null;
            var pd = new short[(int)VMPersonDataVariable.TS1Zodiac + 1];
            foreach (var v in ProfileVars) pd[(int)v] = a.GetPersonData(v);
            if (a.IsChild && pd[(int)VMPersonDataVariable.PersonsAge] >= 18) pd[(int)VMPersonDataVariable.PersonsAge] = 9;
            return ProfileOf(pd, VMReduxTraits.Codes(a), ReduxAttraction.OrientationCode(a),
                (byte)a.GetAttribute(OnAttr), (byte)a.GetAttribute(On2Attr), (byte)a.GetAttribute(OffAttr));
        }

        /// <summary>Does this Sim have what the turn-on (or turn-off) is about?</summary>
        public static bool Matches(Profile p, int code)
        {
            if (p == null) return false;
            switch ((Turn)code)
            {
                case Turn.Funny: return p.Has(VMReduxTraits.T.Goofball) || p.Playful >= 700;
                case Turn.Cheerful: return p.Has(VMReduxTraits.T.Cheerful);
                case Turn.Kind: return p.Nice >= 700;
                case Turn.Outgoing: return p.Outgoing >= 700 && !p.Has(VMReduxTraits.T.Shy) && !p.Has(VMReduxTraits.T.Loner);
                case Turn.Shy: return p.Has(VMReduxTraits.T.Shy) || p.Outgoing <= 300;
                case Turn.Sporty: return p.Has(VMReduxTraits.T.Active) || p.Active >= 700 || p.Body >= 500;
                case Turn.LaidBack: return p.Has(VMReduxTraits.T.Lazy) || p.Active <= 300;
                case Turn.Tidy: return p.Has(VMReduxTraits.T.NeatFreak) || p.Neat >= 700;
                case Turn.Creative: return p.Has(VMReduxTraits.T.Creative) || p.Creativity >= 500;
                case Turn.Brainy: return p.Has(VMReduxTraits.T.Bookworm) || p.Logic >= 500;
                case Turn.GreatCook: return p.Has(VMReduxTraits.T.Foodie) || p.Cooking >= 500;
                case Turn.Handy: return p.Mechanical >= 500;
                case Turn.Charming: return p.Charisma >= 500;
                case Turn.Romantic: return p.Has(VMReduxTraits.T.Romantic) || p.Has(VMReduxTraits.T.Flirty);
                case Turn.HardWorking: return p.JobType > 0;
                case Turn.Wealthy: return p.JobType > 0 && p.JobLevel >= 6;
                case Turn.AnimalLover: return p.Has(VMReduxTraits.T.AnimalLover);
                case Turn.GreenThumb: return p.Has(VMReduxTraits.T.GreenThumb);
                case Turn.Gamer: return p.Has(VMReduxTraits.T.Gamer);
                case Turn.NightOwl: return p.Has(VMReduxTraits.T.NightOwl);
                case Turn.EarlyBird: return p.Has(VMReduxTraits.T.EarlyBird);
                case Turn.FamilyType: return p.Has(VMReduxTraits.T.FamilyOriented);
                case Turn.Loyal: return p.Has(VMReduxTraits.T.Loyal);
                case Turn.Adventurous: return p.Has(VMReduxTraits.T.Daring) || p.Has(VMReduxTraits.T.FreeSpirit);
                case Turn.Snobby: return p.Has(VMReduxTraits.T.Snob);
                case Turn.Messy: return p.Has(VMReduxTraits.T.Slob) || p.Neat <= 200;
                case Turn.Grumpy: return p.Has(VMReduxTraits.T.HotHeaded) || p.Has(VMReduxTraits.T.Gloomy);
                case Turn.Loner: return p.Has(VMReduxTraits.T.Loner);
            }
            return false;
        }

        // ------------------------------------------------------------------ chemistry

        /// <summary>One reason chemistry is what it is (for the Chemistry card): +/- points and the words.</summary>
        public class Why { public int Points; public string Text; }

        /// <summary>How much a likes b (0-100, 50 = neutral), and why (reasons may be null).</summary>
        public static int Appeal(Profile a, Profile b, List<Why> reasons, string aName = "", string bName = "")
        {
            if (a == null || b == null) return 50;
            int s = 50;
            Action<int, string> add = (pts, text) => { s += pts; reasons?.Add(new Why { Points = pts, Text = text }); };
            // turn-ons and the turn-off (grown-ups)
            if (!a.Child && !b.Child)
            {
                foreach (var on in new[] { a.On1, a.On2 })
                    if (IsTurn(on) && Matches(b, on)) add(16, aName + " is into " + TurnName(on).ToLowerInvariant() + " - " + bName + " fits");
                if (IsTurn(a.Off) && Matches(b, a.Off)) add(-22, aName + " is turned off by " + TurnName(a.Off).ToLowerInvariant());
            }
            // traits in common, and traits that clash
            int shared = a.Traits.Count(t => b.Traits.Contains(t));
            if (shared > 0) add(Math.Min(3, shared) * 4, shared == 1 ? "a trait in common" : shared + " traits in common");
            foreach (var c in VMReduxTraits.Clashes)
            {
                if ((a.Traits.Contains((int)c[0]) && b.Traits.Contains((int)c[1])) || (a.Traits.Contains((int)c[1]) && b.Traits.Contains((int)c[0])))
                {
                    add(-12, Nice(VMReduxTraits.IdOf((int)c[0])) + " and " + Nice(VMReduxTraits.IdOf((int)c[1])) + " clash");
                    break;
                }
            }
            // personality: alike, or poles apart
            int diff = Math.Abs(a.Nice - b.Nice) + Math.Abs(a.Active - b.Active) + Math.Abs(a.Playful - b.Playful) + Math.Abs(a.Outgoing - b.Outgoing) + Math.Abs(a.Neat - b.Neat);
            if (diff <= 1000) add(6, "similar personalities");
            else if (diff >= 3000) add(-6, "very different personalities");
            // star signs (the Sims' own table)
            int sign = SignMatch(a.Sign, b.Sign);
            if (sign > 0) add(10, SignName(a.Sign) + " likes " + SignName(b.Sign));
            else if (sign < 0) add(-10, SignName(a.Sign) + " clashes with " + SignName(b.Sign));
            return Math.Max(0, Math.Min(100, s));
        }

        /// <summary>"neat-freak" -> "Neat freak" (a trait id in plain words).</summary>
        private static string Nice(string id)
        {
            id = (id ?? "").Replace('-', ' ');
            return id.Length == 0 ? id : char.ToUpperInvariant(id[0]) + id.Substring(1);
        }

        /// <summary>How well two Sims click, 0-100 (the same both ways).</summary>
        public static int Chemistry(Profile a, Profile b)
        {
            if (a == null || b == null) return 50;
            return (Appeal(a, b, null) + Appeal(b, a, null) + 1) / 2;
        }

        /// <summary>Chemistry between two Sims on the lot (the same on every game).</summary>
        public static int Chemistry(VMAvatar a, VMAvatar b)
        {
            if (a == null || b == null || a.IsPet || b.IsPet || a == b) return 50;
            return Chemistry(ProfileOf(a), ProfileOf(b));
        }

        /// <summary>Chemistry in words.</summary>
        public static string Word(int chem)
        {
            if (chem >= 80) return "Sparks fly!";
            if (chem >= 62) return "Great chemistry";
            if (chem >= 45) return "Some chemistry";
            if (chem >= 30) return "A little awkward";
            return "No spark";
        }

        /// <summary>
        /// Chemistry in friendship-only words - for grown-ups who are family (no romance words between relatives). Pairs
        /// with a child never show chemistry at all (Simitone's ReduxMatch.ChemistryWord).
        /// </summary>
        public static string FriendWord(int chem)
        {
            if (chem >= 80) return "Get along great!";
            if (chem >= 62) return "Get along well";
            if (chem >= 45) return "Get along fine";
            if (chem >= 30) return "A little awkward";
            return "Don't really click";
        }

        /// <summary>1 to 5 (the meter's hearts or stars).</summary>
        public static int Level(int chem) { return Math.Max(1, Math.Min(5, 1 + chem / 20)); }

        // ------------------------------------------------------------------ turn-ons on the Sim

        /// <summary>Valid codes only, two different turn-ons, and a turn-off that isn't one of them.</summary>
        public static byte[] Clean(byte on1, byte on2, byte off)
        {
            if (!IsTurn(on1)) on1 = 0;
            if (!IsTurn(on2) || on2 == on1) on2 = 0;
            if (on1 == 0 && on2 != 0) { on1 = on2; on2 = 0; }
            if (!IsTurn(off) || off == on1 || off == on2) off = 0;
            return new[] { on1, on2, off };
        }

        /// <summary>The turn-ons as they'd be put on this Sim (a child: none).</summary>
        public static byte[] Cleaned(VMAvatar a, byte on1, byte on2, byte off)
        {
            if (a == null || a.IsPet || a.IsChild) return new byte[3];
            return Clean(on1, on2, off);
        }

        public static bool Same(VMAvatar a, byte[] codes)
        {
            if (a == null || codes == null || codes.Length < 3) return false;
            return a.GetAttribute(OnAttr) == codes[0] && a.GetAttribute(On2Attr) == codes[1] && a.GetAttribute(OffAttr) == codes[2];
        }

        /// <summary>Puts turn-ons on the Sim (only VMNetReduxMatchCmd calls this).</summary>
        public static void Set(VMAvatar a, byte on1, byte on2, byte off)
        {
            if (a == null || a.IsPet) return;
            var c = Cleaned(a, on1, on2, off);
            a.SetAttribute(OnAttr, c[0]);
            a.SetAttribute(On2Attr, c[1]);
            a.SetAttribute(OffAttr, c[2]);
        }

        // ------------------------------------------------------------------ who's who

        private static bool Person(VMAvatar a) { return a != null && !a.IsPet && !a.Dead; }

        /// <summary>Aromantic or asexual (by the orientation the Sim goes by on the lot).</summary>
        public static bool AroAce(VMAvatar a)
        {
            var o = (VMReduxTraits.T)ReduxAttraction.OrientationCode(a);
            return o == VMReduxTraits.T.Aromantic || o == VMReduxTraits.T.Asexual;
        }

        private static int Daily(VMAvatar a, VMAvatar b) { var r = ReduxWooHoo.Rel(a, b); return (r != null && r.Count > 0) ? r[0] : 0; }
        private static int Life(VMAvatar a, VMAvatar b) { var r = ReduxWooHoo.Rel(a, b); return (r != null && r.Count > 2) ? r[2] : 0; }

        /// <summary>Daily and lifetime relationship up or down (made if they hadn't met - the game's own numbers, -100 to 100).</summary>
        public static void Closer(VMAvatar from, VMAvatar to, int daily, int life)
        {
            var r = ReduxWooHoo.Rel(from, to) ?? MakeRel(from, to);
            if (r == null) return;
            while (r.Count < 4) r.Add(0);
            r[0] = (short)Math.Max(-100, Math.Min(100, r[0] + daily));
            r[2] = (short)Math.Max(-100, Math.Min(100, r[2] + life));
        }

        /// <summary>A new relationship (the two Sims hadn't met), as the game keeps one: daily, in love, lifetime, crush.</summary>
        private static List<short> MakeRel(VMAvatar from, VMAvatar to)
        {
            try
            {
                var neigh = Content.Content.Get().Neighborhood;
                var na = neigh?.GetNeighborByID(from.GetPersonData(VMPersonDataVariable.NeighborId));
                var nb = to.GetPersonData(VMPersonDataVariable.NeighborId);
                if (na == null || nb <= 0) return null;
                if (na.Relationships == null) na.Relationships = new Dictionary<int, List<short>>();
                List<short> r;
                if (!na.Relationships.TryGetValue(nb, out r)) na.Relationships[nb] = r = new List<short> { 0, 0, 0, 0 };
                return r;
            }
            catch { return null; }
        }

        private static void Motive(VMAvatar a, VMMotive m, int add)
        {
            if (a == null) return;
            a.SetMotiveData(m, (short)Math.Max(-100, Math.Min(100, a.GetMotiveData(m) + add)));
        }

        // ------------------------------------------------------------------ closeness: Spend Quality Time, Cuddle

        public enum Close : byte { None = 0, QualityTime = 1, Cuddle = 2 }

        public static string CloseLabel(Close c)
        {
            switch (c)
            {
                case Close.QualityTime: return "Spend Quality Time";
                case Close.Cuddle: return "Cuddle";
            }
            return "";
        }

        public const int CloseCooldown = 45, CloseWrap = 30000;

        private static int Clock(VM vm)
        {
            var c = vm.Context.Clock;
            long m = ((((long)c.Year * 12 + c.Month) * 31 + c.DayOfMonth) * 24 + c.Hours) * 60 + c.Minutes;
            return (int)(((m % CloseWrap) + CloseWrap) % CloseWrap);
        }

        /// <summary>Game minutes until this Sim can do another closeness choice (0: now).</summary>
        public static int CloseWait(VM vm, VMAvatar a)
        {
            if (vm == null || a == null) return 0;
            int last = a.GetAttribute(CloseAttr);
            if (last <= 0 || last > CloseWrap) return 0;
            int since = ((Clock(vm) - (last - 1)) % CloseWrap + CloseWrap) % CloseWrap;
            return since >= CloseCooldown ? 0 : CloseCooldown - since;
        }

        private static bool SameFamily(VMAvatar a, VMAvatar b)
        {
            return a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) == b.GetPersonData(VMPersonDataVariable.TS1FamilyNumber)
                && a.GetPersonData(VMPersonDataVariable.TS1FamilyNumber) > 0;
        }

        /// <summary>
        /// Can a do this with b? null: yes; otherwise why not ("" offers nothing). Spend Quality Time: Sims who know each
        /// other (daily 15+ both ways). Cuddle: close (daily 45+ both ways) - grown-ups, or family of any age.
        /// </summary>
        public static string CanClose(VMAvatar a, VMAvatar b, Close c)
        {
            if (!Person(a) || !Person(b) || a == b || c == Close.None || c > Close.Cuddle) return "";
            if (b.Position == LotTilePos.OUT_OF_WORLD) return "";
            if (b.GetPersonData(VMPersonDataVariable.PersonType) >= 2) return ""; // (the maid, the police...)
            int ab = Daily(a, b), ba = Daily(b, a);
            if (c == Close.QualityTime) return (ab >= 15 && ba >= 15) ? null : "";
            if (!SameFamily(a, b) && (a.IsChild || b.IsChild)) return "";
            return (ab >= 45 && ba >= 45) ? null : "";
        }

        /// <summary>Does it (VMNetReduxMatchCmd Close). null: done; else why not.</summary>
        public static string DoClose(VM vm, VMAvatar a, VMAvatar b, Close c)
        {
            if (vm == null) return "";
            var why = CanClose(a, b, c);
            if (why != null) return why;
            if (a.Thread == null || a.Thread.Queue.Count >= VMThread.MAX_USER_ACTIONS) return "";
            int wait = CloseWait(vm, a);
            if (wait > 0) return "They did that not long ago - try again in about " + Math.Max(1, wait) + " game minutes.";
            a.SetAttribute(CloseAttr, (short)(Clock(vm) + 1));
            int tpm = Math.Max(1, vm.Context.Clock.TicksPerMinute);
            int chem = Chemistry(a, b);
            int extra = (chem - 50) / 25; // (-2 to +2: Sims who click get more out of it)
            if (c == Close.QualityTime)
            {
                ReduxWooHoo.PushWait(vm, a, b, tpm * 30, CloseLabel(c));
                foreach (var s in new[] { a, b }) { Motive(s, VMMotive.Social, 15); Motive(s, VMMotive.Fun, 8); }
                Closer(a, b, 6 + extra, 2);
                Closer(b, a, 6 + extra, 2);
            }
            else
            {
                ReduxWooHoo.PushWait(vm, a, b, tpm * 15, CloseLabel(c));
                foreach (var s in new[] { a, b }) { Motive(s, VMMotive.Social, 12); Motive(s, VMMotive.Comfort, 10); }
                Closer(a, b, 5 + extra, 2);
                Closer(b, a, 5 + extra, 2);
            }
            return null;
        }

        // ------------------------------------------------------------------ platonic life partners

        /// <summary>Close enough to ask (Good Friends: daily 50+ and lifetime 40+, both ways), both grown-ups, and one of them aromantic or asexual.</summary>
        public static bool CanAskLife(VMAvatar a, VMAvatar b)
        {
            if (!ReduxWooHoo.Grownup(a) || !ReduxWooHoo.Grownup(b) || a == b) return false;
            if (!AroAce(a) && !AroAce(b)) return false;
            return Daily(a, b) >= 50 && Daily(b, a) >= 50 && Life(a, b) >= 40 && Life(b, a) >= 40;
        }

        /// <summary>The chance (0-100) b says yes to being a's life partner: how close they are, chemistry, b's ways.</summary>
        public static int LifeChance(VMAvatar a, VMAvatar b)
        {
            if (!CanAskLife(a, b)) return 0;
            int c = 20 + Life(b, a) / 2 + Daily(b, a) / 4 + (Chemistry(a, b) - 50) / 3;
            if (AroAce(b)) c += 15;
            if (VMReduxTraits.Has(b, VMReduxTraits.T.Loyal) || VMReduxTraits.Has(b, VMReduxTraits.T.FamilyOriented)) c += 10;
            if (VMReduxTraits.Has(b, VMReduxTraits.T.Loner)) c -= 10;
            return Math.Max(5, Math.Min(95, c));
        }

        /// <summary>They talked it over: a goes over to b; yes brings them closer, no hurts a little.</summary>
        public static void LifeTalk(VM vm, VMAvatar a, VMAvatar b, bool agreed)
        {
            ReduxWooHoo.PushWait(vm, a, b, Math.Max(1, vm.Context.Clock.TicksPerMinute) * 3, "Ask to Be Life Partners");
            if (agreed) { Closer(a, b, 10, 5); Closer(b, a, 10, 5); }
            else { Closer(a, b, -8, -2); Closer(b, a, -8, -2); }
        }

        // ------------------------------------------------------------------ visitors: a date, a child visiting a parent

        /// <summary>
        /// Brings neighbour GUID onto the lot as a greeted visitor (VMNetReduxTownieCmd's own Arrive), by object anchor.
        /// For a date with host: chemistry gives the evening a head start (they met on the app - a relationship is made if
        /// they hadn't met). Returns the visitor, or null.
        /// </summary>
        public static VMAvatar Arrive(VM vm, uint guid, short anchor, VMAvatar host, bool date)
        {
            if (vm == null || guid == 0) return null;
            var arrive = new VMNetReduxTownieCmd { Mode = VMNetReduxTownieCmd.TownieMode.Arrive, GUID = guid, AnchorID = anchor, Greet = 1 };
            if (!arrive.Execute(vm, null)) return null;
            var ava = vm.Context.ObjectQueries.Avatars.OfType<VMAvatar>().FirstOrDefault(x => !x.Dead && x.Object?.OBJ?.GUID == guid);
            if (ava == null) return null;
            if (date && Person(host) && host != ava)
            {
                int chem = Chemistry(host, ava);
                int start = 4 + chem / 8; // (4 to 16)
                Closer(host, ava, start, 1);
                Closer(ava, host, start, 1);
            }
            return ava;
        }

        // ------------------------------------------------------------------ adoption

        private static readonly uint[] MailboxGUIDs = { 0xEF121974, 0x1D95C9B0 };

        public static string CleanName(string name)
        {
            var s = new string((name ?? "").Where(ch => !char.IsControl(ch) && ch != '|').ToArray()).Trim();
            return s.Length > 24 ? s.Substring(0, 24).Trim() : s;
        }

        private static int Room(VM vm, FAMI fam) { return ReduxWooHoo.MaxFamily - (fam.FamilyGUIDs?.Length ?? 0) - ReduxWooHoo.BabiesOnLot(vm); }

        /// <summary>
        /// A baby adopted by parent (and other, if there's a second parent): the game's own bassinet by the parent, the
        /// baby's DNA as the game makes it, and the chosen name (the child keeps it when the baby grows up). null: done.
        /// </summary>
        public static string AdoptBaby(VM vm, VMAvatar parent, VMAvatar other, string name)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || !ReduxWooHoo.Grownup(parent)) return "";
            if (!(fam.FamilyGUIDs ?? new uint[0]).Contains(parent.Object.OBJ.GUID)) return "";
            if (Room(vm, fam) <= 0) return "Your family is full - a family can have 8 Sims and pets at most (babies count too).";
            var group = vm.Context.CreateObjectInstance(ReduxWooHoo.BabyGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH);
            var baby = group?.BaseObject;
            if (baby == null) return "";
            VMFindLocationFor.FindLocationFor(baby, parent, vm.Context, VMPlaceRequestFlags.Default);
            if (baby.Position == LotTilePos.OUT_OF_WORLD)
            {
                group.Delete(vm.Context);
                return "There's no room by " + (parent.Name ?? "them") + " for the bassinet - try somewhere with more space.";
            }
            var part = group.Objects.FirstOrDefault(x => x?.Object?.OBJ?.GUID == ReduxWooHoo.BabyPartGUID) ?? baby;
            var second = (other != null && !other.Dead && !other.IsPet && other != parent) ? other : parent;
            try
            {
                if (part.TreeByName != null && part.TreeByName.TryGetValue("create DNA", out var tree))
                {
                    var routine = tree.bhav as VMRoutine;
                    if (routine != null) parent.Thread.RunInMyStack(routine, part.Object, new short[] { parent.ObjectID, second.ObjectID, 0, 0 }, part);
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] adoption: the baby's DNA: " + e.Message); }
            var n = CleanName(name);
            if (n.Length > 0) baby.Name = n;
            return null;
        }

        private static string RemoveExt(string item)
        {
            if (item == null) return null;
            var ind = item.LastIndexOf('.');
            return ind != -1 ? item.Substring(0, ind) : item;
        }

        private static string ExtractID(string item, string skin)
        {
            var ind = item.IndexOf('_');
            if (ind != -1) item = item.Substring(0, ind);
            return item + skin;
        }

        private static string FindHG(string item)
        {
            var ind = item.IndexOf('_');
            return ind != -1 ? item.Substring(ind) : item;
        }

        /// <summary>A new child's looks, the way the game makes a new character (VMTS1MakeNewCharacter), with the lot's random numbers.</summary>
        private static SimTemplateCreateInfo ChildInfo(VM vm, bool female, int skinIndex)
        {
            var skins = VMTS1MakeNewCharacter.ColorNames;
            var skin = skins[Math.Max(0, Math.Min(skins.Length - 1, skinIndex))];
            var simtype = (female ? "f" : "m") + "c";
            var bcf = Content.Content.Get().BCFGlobal;
            List<string> headList, bodyList;
            if (!bcf.CollectionsByName.ContainsKey("c") || !bcf.CollectionsByName["c"].ClothesByAvatarType.TryGetValue(simtype, out headList)) return null;
            var heads = headList.ToList();
            if (!bcf.CollectionsByName.ContainsKey("b") || !bcf.CollectionsByName["b"].ClothesByAvatarType.TryGetValue(simtype + "chd", out bodyList)) return null;
            var bodies = bodyList.ToList();
            var tex = Content.Content.Get().AvatarTextures as TS1AvatarTextureProvider;
            if (tex == null) return null;
            var texnames = tex.GetAllNames();
            var headTex = heads.Select(x => RemoveExt(texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, skin))))).ToList();
            var bodyTex = bodies.Select(x => RemoveExt(texnames.FirstOrDefault(y => y.StartsWith(ExtractID(x, skin))))).ToList();
            var hgTex = bodies.Select(x => (RemoveExt(texnames.FirstOrDefault(y => y == "huao" + FindHG(x))) ?? "huao" + skin).Substring(4)).ToList();
            for (int i = 0; i < heads.Count; i++) if (headTex[i] == null) { headTex.RemoveAt(i); heads.RemoveAt(i--); }
            for (int i = 0; i < bodies.Count; i++) if (bodyTex[i] == null) { bodyTex.RemoveAt(i); hgTex.RemoveAt(i); bodies.RemoveAt(i--); }
            if (heads.Count == 0 || bodies.Count == 0) return null;
            int h = (int)vm.Context.NextRandom((ulong)heads.Count), b = (int)vm.Context.NextRandom((ulong)bodies.Count);
            var body = bodies[b];
            var ind = body.IndexOf("_");
            if (ind < 3) return null;
            var info = new SimTemplateCreateInfo(simtype + body.Substring(ind - 3, 3), skin);
            info.BodyStringReplace[1] = body + ",BODY=" + bodyTex[b];
            info.BodyStringReplace[2] = heads[h] + ",HEAD-HEAD=" + headTex[h];
            var hg = hgTex[b];
            info.BodyStringReplace[17] = "HuLO,HAND=huao" + hg;
            info.BodyStringReplace[18] = "HuRO,HAND=huao" + hg;
            info.BodyStringReplace[19] = "HuLP,HAND=huao" + hg;
            info.BodyStringReplace[20] = "HuRP,HAND=huao" + hg;
            info.BodyStringReplace[21] = "HuLO,HAND=huao" + hg;
            info.BodyStringReplace[22] = "HuRC,HAND=huao" + hg;
            return info;
        }

        /// <summary>
        /// A child adopted into the lot's family (parent asked): a new child Sim is made (the lot's random numbers, so every
        /// game makes the same one), joins the family and turns up by the mailbox. Returns the new Sim's GUID (0: not made,
        /// why says why - "" to say nothing).
        /// </summary>
        public static uint AdoptChild(VM vm, VMAvatar parent, bool female, int skin, string name, out string why)
        {
            why = "";
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || !ReduxWooHoo.Grownup(parent)) return 0;
            if (!(fam.FamilyGUIDs ?? new uint[0]).Contains(parent.Object.OBJ.GUID)) return 0;
            if (Room(vm, fam) <= 0) { why = "Your family is full - a family can have 8 Sims and pets at most (babies count too)."; return 0; }
            var info = ChildInfo(vm, female, skin);
            if (info == null) { why = "The adoption agency couldn't find a child's clothes in this game's files."; return 0; }
            var n = CleanName(name);
            info.Name = n.Length > 0 ? n : (female ? "Lily" : "Sam");
            info.Bio = "";
            info.FamilyID = (short)fam.ChunkID;
            for (int i = 0; i < 5; i++) info.PersonalityPoints[i] = (short)(300 + 100 * (int)vm.Context.NextRandom(5));
            Func<int, int> next = (max) => (int)vm.Context.NextRandom((ulong)Math.Max(0, max));
            var guid = SimitoneNeighbourGenerator.GenerateGUID(fam.FamilyGUIDs, next);
            var neighbour = SimitoneNeighbourGenerator.CreateNeighbor(guid, info, next);
            if (neighbour == null) return 0;
            fam.FamilyGUIDs = (fam.FamilyGUIDs ?? new uint[0]).Concat(new[] { guid }).ToArray();
            if (!(fam.RuntimeSubset ?? new uint[0]).Contains(guid)) fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { guid }).ToArray();
            SpawnAtDoor(vm, fam, guid, parent);
            why = null;
            return guid;
        }

        /// <summary>The family member GUID made on the lot by the mailbox (or by "near"); false: no room right now (they come next time).</summary>
        private static bool SpawnAtDoor(VM vm, FAMI fam, uint guid, VMEntity near)
        {
            var ava = vm.Context.CreateObjectInstance(guid, LotTilePos.OUT_OF_WORLD, Direction.NORTH)?.BaseObject as VMAvatar;
            if (ava == null) return false;
            ava.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)fam.ChunkID);
            ava.SetPersonData(VMPersonDataVariable.PersonType, 0);
            ava.AvatarState.Permissions = VMTSOAvatarPermissions.Owner;
            var mailbox = vm.Entities.FirstOrDefault(x => !x.Dead && x.Position != LotTilePos.OUT_OF_WORLD && MailboxGUIDs.Contains(x.Object.OBJ.GUID));
            var anchor = mailbox ?? near;
            if (anchor != null) VMFindLocationFor.FindLocationFor(ava, anchor, vm.Context, VMPlaceRequestFlags.Default);
            if (ava.Position == LotTilePos.OUT_OF_WORLD)
            {
                ava.Delete(true, vm.Context);
                return false;
            }
            vm.Scheduler.RescheduleInterrupt(ava);
            return true;
        }

        // ------------------------------------------------------------------ moving in (life partners)

        /// <summary>
        /// who (on the lot, from another family or a townie) moves into the lot's family, as the game's own "Move In" does:
        /// out of their old family (their family's savings come too when they were the last one in it - never the value of a
        /// house: the last one in a family with a home of its own can't move out of it), into this one, and saved with the
        /// lot from now on. with: the family's Sim they're moving in with (a grown-up), or null. null: done; else why not.
        /// Never a visiting player's Sim or a child.
        /// </summary>
        public static string MoveIn(VM vm, VMAvatar who, VMAvatar with = null)
        {
            var fam = vm?.TS1State?.CurrentFamily;
            if (fam == null || !ReduxWooHoo.Grownup(who)) return "";
            if (who.PersistID >= VMReduxPerks.FirstGuestId) return ""; // (another player's Sim - never)
            if (with != null && (with == who || !ReduxWooHoo.Grownup(with))) return "";
            var guid = who.Object.OBJ.GUID;
            if ((fam.FamilyGUIDs ?? new uint[0]).Contains(guid)) return "";
            if ((fam.FamilyGUIDs?.Length ?? 0) >= ReduxWooHoo.MaxFamily) return "Your family is full - a family can have 8 Sims and pets at most.";
            var neigh = Content.Content.Get().Neighborhood;
            var n = neigh.GetNeighborByID(who.GetPersonData(VMPersonDataVariable.NeighborId));
            if (n == null || n.GUID != guid) return "";
            if (n.PersonData != null && n.PersonData.Length > (int)VMPersonDataVariable.TS1FamilyNumber)
            {
                var famID = n.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber];
                var old = famID > 0 ? neigh.GetFamily((ushort)famID) : null;
                if (old != null && old.ChunkID != fam.ChunkID && old.ChunkID != 0)
                {
                    var left = (old.FamilyGUIDs ?? new uint[0]).Where(x => x != guid).ToArray();
                    // (the last one in a family with a home: they stay - a house never empties into this family's money)
                    if (left.Length == 0 && old.HouseNumber > 0)
                        return (who.Name ?? "They") + " is the last one in their family and has a home of their own - they can't leave it empty to move in.";
                    old.FamilyGUIDs = left;
                    if (left.Length == 0)
                    {
                        fam.Budget += Math.Max(0, old.Budget); // (savings only - never the house's value)
                        old.ChunkParent.FullRemoveChunk(old);
                    }
                }
                n.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber] = (short)fam.ChunkID;
            }
            fam.FamilyGUIDs = (fam.FamilyGUIDs ?? new uint[0]).Concat(new[] { guid }).ToArray();
            if (!(fam.RuntimeSubset ?? new uint[0]).Contains(guid)) fam.RuntimeSubset = (fam.RuntimeSubset ?? new uint[0]).Concat(new[] { guid }).ToArray();
            who.SetPersonData(VMPersonDataVariable.TS1FamilyNumber, (short)fam.ChunkID);
            who.SetPersonData(VMPersonDataVariable.PersonType, 0);
            who.SetPersonData(VMPersonDataVariable.GreetStatus, 1);
            who.SetValue(VMStackObjectVariable.MovementFlags, (short)(who.GetValue(VMStackObjectVariable.MovementFlags) & ~(short)VMMovementFlags.FSOExcludeUserSave));
            who.AvatarState.Permissions = VMTSOAvatarPermissions.Owner;
            if (who.Thread?.Queue != null)
                foreach (var q in who.Thread.Queue.Where(x => x.ActionRoutine?.ID == VMNetReduxTownieCmd.LeaveRoutine).ToList())
                    who.Thread.CancelAction(q.UID);
            return null;
        }
    }
}
