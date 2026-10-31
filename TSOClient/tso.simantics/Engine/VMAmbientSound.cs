using System;
using System.Collections.Generic;
using FSO.HIT;

namespace FSO.SimAntics.Engine
{
    public class VMAmbientSound
    {
        public static bool ForceDisable;
        public static Dictionary<uint, Ambience> AmbienceByGUID = new Dictionary<uint, Ambience>() //may want to load this from ambience.ini in future...
        {
            {0x3dd887a6, new Ambience("sounddata/ambience/daybirds/daybirds.fsc", false)},
            {0x3dd887aa, new Ambience("sounddata/ambience/explosions/explosions.fsc", false)},
            {0x7dd887ad, new Ambience("sounddata/ambience/farmanimals/farmanimals.fsc", false)},
            {0x9dd887af, new Ambience("sounddata/ambience/gunshots/gunshots.fsc", false)},
            {0xddd887b3, new Ambience("sounddata/ambience/planes/planes.fsc", false)},
            {0xfdd887b5, new Ambience("sounddata/ambience/thunder/thunder.fsc", false)},
            {0x1e128187, new Ambience("sounddata/ambience/breeze/breeze.fsc", false)},
            {0xfe128189, new Ambience("sounddata/ambience/construction/construction.fsc", false)},
            {0x5e12818c, new Ambience("sounddata/ambience/dog/dog.fsc", false)},
            {0xbe12818d, new Ambience("sounddata/ambience/driveby/driveby.fsc", false)},
            {0xde12818f, new Ambience("sounddata/ambience/howlingwind/howlingwind.fsc", false)},
            {0x1e128190, new Ambience("sounddata/ambience/indust/indust.fsc", false)},
            {0x3e128192, new Ambience("sounddata/ambience/insect/insect.fsc", false)},
            {0xbe128196, new Ambience("sounddata/ambience/jungle/jungle.fsc", false)},
            {0xde128198, new Ambience("sounddata/ambience/office/office.fsc", false)},
            {0x3e12819a, new Ambience("sounddata/ambience/restaurant/restaurant.fsc", false)},
            {0xbe12819c, new Ambience("sounddata/ambience/scibleeps/scibleeps.fsc", false)},
            {0x1e1281ac, new Ambience("sounddata/ambience/siren/siren.fsc", false)},
            {0x1e1281ad, new Ambience("sounddata/ambience/wolf/wolf.fsc", false)},
            {0xbe19bb2d, new Ambience("sounddata/ambience/seabirds/seabirds.fsc", false)},
            {0xde19bb31, new Ambience("sounddata/ambience/raindrops/raindrops.fsc", false)},
            {0xbe1a033e, new Ambience("sounddata/ambience/magic/magic.fsc", false)},
            {0xa9b9652a, new Ambience("sounddata/ambience/smallmachines/smallmachines.fsc", false)},
            {0xa9b96536, new Ambience("sounddata/ambience/screams/screams.fsc", false)},
            {0xa9b96539, new Ambience("sounddata/ambience/nightbirds/nightbirds.fsc", false)},
            {0xa9b9653c, new Ambience("sounddata/ambience/gym/gym.fsc", false)},
            {0xa9b9653e, new Ambience("sounddata/ambience/ghost/ghost.fsc", false)},

            //Loops

            {0x9e0bc19a, new Ambience("sounddata/ambience/loops/brook_lp.xa", true)},
            {0xfe0bc1a1, new Ambience("sounddata/ambience/loops/crowd_lp.xa", true)},
            {0x1e0bc1a3, new Ambience("sounddata/ambience/loops/heartbeat_lp.xa", true)},
            {0x5e0bc1a4, new Ambience("sounddata/ambience/loops/indoor_lp.xa", true)},
            {0x5e0bc1a6, new Ambience("sounddata/ambience/loops/insect_lp.xa", true)},
            {0xbe0bc1a9, new Ambience("sounddata/ambience/loops/ocean_lp.xa", true)},
            {0x1e0bc1ab, new Ambience("sounddata/ambience/loops/outdoor_lp.xa", true)},
            {0xde0bc1ad, new Ambience("sounddata/ambience/loops/rain_lp.xa", true)},
            {0x3e0bc2af, new Ambience("sounddata/ambience/loops/scifi_lp.xa", true)},
            {0x1e0bc2b2, new Ambience("sounddata/ambience/loops/storm_lp.xa", true)},
            {0x3e0bc2b4, new Ambience("sounddata/ambience/loops/traffic_lp.xa", true)},
            {0x1e0bc2b5, new Ambience("sounddata/ambience/loops/wind_lp.xa", true)}
        };

        public static List<VMCategorisedAmb> SoundByBitField = new List<VMCategorisedAmb>() {
            new VMCategorisedAmb(0x3dd887a6, 0, "AnimalsSongBirds"),
            new VMCategorisedAmb(0x3dd887aa, 1, "MechanicalExplosions"),
            new VMCategorisedAmb(0x7dd887ad, 0, "AnimalsFarm"),
            new VMCategorisedAmb(0x9dd887af, 1, "MechanicalGunshot"),
            new VMCategorisedAmb(0xddd887b3, 1, "MechanicalPlanes"),
            new VMCategorisedAmb(0xfdd887b5, 2, "WeatherLightingThunder"),

            new VMCategorisedAmb(0x9e0bc19a, 4, "LoopBrook"),
            new VMCategorisedAmb(0xfe0bc1a1, 4, "LoopCrowd"),
            new VMCategorisedAmb(0x1e0bc1a3, 4, "LoopHeartbeat"),
            new VMCategorisedAmb(0x5e0bc1a4, 4, "LoopIndoor"),
            new VMCategorisedAmb(0x5e0bc1a6, 4, "LoopInsects"),
            new VMCategorisedAmb(0xbe0bc1a9, 4, "LoopOcean"),
            new VMCategorisedAmb(0x1e0bc1ab, 4, "LoopOutdoor"),
            new VMCategorisedAmb(0xde0bc1ad, 4, "LoopRain"),
            new VMCategorisedAmb(0x3e0bc2af, 4, "LoopTechno"),
            new VMCategorisedAmb(0x1e0bc2b2, 4, "LoopStorm"),
            new VMCategorisedAmb(0x3e0bc2b4, 4, "LoopTraffic"),
            new VMCategorisedAmb(0x1e0bc2b5, 4, "LoopWind"),

            new VMCategorisedAmb(0x1e128187, 2, "WeatherBreeze"),
            new VMCategorisedAmb(0xfe128189, 1, "MechanicalConstruction"),
            new VMCategorisedAmb(0x5e12818c, 0, "AnimalsDog"),
            new VMCategorisedAmb(0xbe12818d, 1, "MechanicalDriveBy"),
            new VMCategorisedAmb(0xde12818f, 2, "WeatherHowlingWind"),
            new VMCategorisedAmb(0x1e128190, 1, "MechanicalIndustrial"),
            new VMCategorisedAmb(0x3e128192, 0, "AnimalsInsects"),
            new VMCategorisedAmb(0xbe128196, 0, "AnimalsJungle"),
            new VMCategorisedAmb(0xde128198, 3, "PeopleOffice"),
            new VMCategorisedAmb(0x3e12819a, 3, "PeopleRestaurant"),
            new VMCategorisedAmb(0xbe12819c, 1, "MechanicalSciBleeps"),
            new VMCategorisedAmb(0x1e1281ac, 1, "MechanicalSirens"),
            new VMCategorisedAmb(0x1e1281ad, 0, "AnimalsWolf"),
            new VMCategorisedAmb(0xbe19bb2d, 0, "AnimalsSeaBirds"),
            new VMCategorisedAmb(0xde19bb31, 2, "WeatherRainDrops"),
            new VMCategorisedAmb(0xbe1a033e, 3, "PeopleMagic"),
            new VMCategorisedAmb(0xa9b9652a, 1, "MechanicalSmallMachines"),
            new VMCategorisedAmb(0xa9b96536, 3, "PeopleScreams"),
            new VMCategorisedAmb(0xa9b96539, 0, "AnimalsNightBirds"),
            new VMCategorisedAmb(0xa9b9653c, 3, "PeopleGym"),
            new VMCategorisedAmb(0xa9b9653e, 3, "PeopleGhost")
        };

        public Dictionary<byte, AmbiencePlayer> ActiveSounds;
        public ulong ActiveBits;
        public VMCategorisedAmb? ActiveLoop;

        /// <summary>
        /// Handles ambient sound in lots.
        /// </summary>
        /// 
        public VMAmbientSound()
        {
            ActiveSounds = new Dictionary<byte, AmbiencePlayer>();
        }

        public bool AmbienceActive(byte id)
        {
            return ActiveSounds.ContainsKey(id);
        }

        public byte GetAmbienceFromGUID(uint GUID)
        {
            for (byte i = 0; i < SoundByBitField.Count; i++)
            {
                if (SoundByBitField[i].GUID == GUID) return i;
            }
            return 0;
        }

        public VMCategorisedAmb? GetAmbienceFromName(string name)
        {
            for (byte i = 0; i < SoundByBitField.Count; i++)
            {
                if (SoundByBitField[i].Name == name) return SoundByBitField[i];
            }
            return null;
        }

        public void SetAmbience(byte id, bool active)
        {
            if (ForceDisable || HITVM.DISABLE_SOUND) return;
            if (id > SoundByBitField.Count) return;
            if (active)
            {
                ActiveBits |= ((ulong)1 << id);
                if (!ActiveSounds.ContainsKey(id))
                {
                    var cat = SoundByBitField[id];
                    var amb = AmbienceByGUID[cat.GUID];
                    if (cat.Category == 4 && ActiveLoop != null) SetAmbience(GetAmbienceFromGUID(ActiveLoop.Value.GUID), false); //cancel previous loop
                    if (VM.UseWorld) ActiveSounds.Add(id, new AmbiencePlayer(amb));
                    if (cat.Category == 4) ActiveLoop = cat;
                }
            }
            else
            {
                ActiveBits &= ~(((ulong)1 << id));
                if (ActiveSounds.ContainsKey(id))
                {
                    var cat = SoundByBitField[id];
                    ActiveSounds[id].Kill();
                    ActiveSounds.Remove(id);
                    if (cat.Category == 4) ActiveLoop = null;
                }
            }
        }

        public void Kill()
        {
            foreach (var sound in ActiveSounds)
            {
                sound.Value.Kill();
            }
            ActiveSounds.Clear();
        }

    }

    /// <summary>
    /// SimsRedux 19.6 "Neighbourhood life": the quiet sounds of the town around the lot - birds in the morning, a
    /// dog barking somewhere now and then, children playing after school and at the weekend, a lawnmower on weekend
    /// mornings, crickets at night, the hum of traffic in the rush hours and, on community lots, a busier crowd in the
    /// evening.
    ///
    /// Only the game's own sounds, found when the game runs: Sims 1's sound events (the .hot sound lists) whose names
    /// say what they are; where Sims 1 has none for a kind and The Sims Online's files are switched on, its ambience
    /// sequences (the paths in VMAmbientSound's table - which is The Sims Online's: Sims 1 lots never use it). A kind
    /// with nothing found is simply silent. What was found is written to the log once ("neighbourhood sounds: ...").
    ///
    /// Sound only, on this device: nothing here touches the lot (the game's clock is only read), so it needn't be
    /// the same on two games. Quiet, under the Ambience volume (and, for Sims 1's sound events, Sound Effects too -
    /// those play through it), two at a time at most. Called by the game every frame while a lot is open (on: the
    /// "Neighbourhood life" setting); StopAll when the lot closes.
    /// </summary>
    public static class VMNeighbourhoodSound
    {
        private enum Kind { Birds, Night, Dog, Kids, Mower, Traffic, Crowd }
        private const int KindCount = 7;
        /// <summary>How loud each kind is (before the volume settings) - kept low: it's background.</summary>
        private static readonly float[] Loudness = { 0.30f, 0.24f, 0.16f, 0.17f, 0.13f, 0.20f, 0.22f };
        /// <summary>The longest each kind plays before it fades away (seconds).</summary>
        private static readonly float[] Longest = { 22f, 32f, 10f, 16f, 26f, 40f, 40f };
        /// <summary>The Sims Online's ambience for a kind, if its files are there (none for children or a lawnmower).</summary>
        private static readonly string[] TSOPaths =
        {
            "sounddata/ambience/daybirds/daybirds.fsc", "sounddata/ambience/insect/insect.fsc", "sounddata/ambience/dog/dog.fsc",
            null, null, "sounddata/ambience/loops/traffic_lp.xa", "sounddata/ambience/loops/crowd_lp.xa"
        };
        private const int MaxAtOnce = 2;
        private const int OwnerID = -19600; // (no object has this id)

        private class Playing
        {
            public Kind Kind;
            public HITSound Hit;
            public FSCPlayer Fsc;
            public Microsoft.Xna.Framework.Audio.SoundEffect Sfx;
            public Microsoft.Xna.Framework.Audio.SoundEffectInstance Loop;
            public float Age, Life, Pan, Fade = 1f;
            public bool FadeIn;
        }

        private static List<string>[] Events;
        private static string[] TSOFound;
        private static readonly List<Playing> Active = new List<Playing>();
        private static readonly float[] Wait = new float[KindCount];
        private static VM LastVM;
        private static readonly Random Rand = new Random();
        private static int Failures;

        /// <param name="vm">The lot open now (null: none).</param>
        /// <param name="on">The "Neighbourhood life" setting (and the lot showing).</param>
        /// <param name="community">A community lot (the game knows from its neighbourhood).</param>
        /// <param name="seconds">Real time since the last call.</param>
        public static void Tick(VM vm, bool on, bool community, float seconds)
        {
            try
            {
                var hit = HITVM.Get();
                if (!on || vm == null || hit == null || !vm.TS1 || !vm.Ready || !VM.UseWorld
                    || VMAmbientSound.ForceDisable || HITVM.DISABLE_SOUND || vm.Context?.Clock == null)
                {
                    StopAll();
                    return;
                }
                if (vm != LastVM)
                {
                    StopAll();
                    LastVM = vm;
                    for (int i = 0; i < KindCount; i++) Wait[i] = 4f + (float)Rand.NextDouble() * 20f; // (not all at once on arrival)
                }
                Find();
                seconds = Math.Max(0f, Math.Min(0.25f, seconds));
                float amb = hit.GetMasterVolume(FSO.HIT.Model.HITVolumeGroup.AMBIENCE);

                // the ones playing: their volume, and fading away at the end
                for (int i = 0; i < Active.Count; i++)
                {
                    var p = Active[i];
                    p.Age += seconds;
                    if (p.Age > p.Life) p.Fade -= seconds / 2f; // (two seconds to fade out)
                    float fadeIn = p.FadeIn ? Math.Min(1f, p.Age / 2f) : 1f;
                    float vol = Loudness[(int)p.Kind] * Math.Max(0f, p.Fade) * fadeIn;
                    bool done = p.Fade <= 0f;
                    if (p.Hit != null)
                    {
                        if (p.Hit.Dead) done = true;
                        else p.Hit.SetVolume(vol * amb, p.Pan, OwnerID);
                    }
                    else if (p.Fsc != null) p.Fsc.SetVolume(vol); // (its notes are under the Ambience volume already)
                    else if (p.Loop != null)
                    {
                        try { p.Loop.Volume = Math.Max(0f, Math.Min(1f, vol * amb)); } catch { done = true; }
                    }
                    if (done)
                    {
                        Stop(p);
                        Active.RemoveAt(i--);
                    }
                }

                // something new now and then, by the time of day
                var clock = vm.Context.Clock;
                bool weekend = FSO.SimAntics.Utils.ReduxWeek.Day(clock) >= 5;
                for (int k = 0; k < KindCount; k++)
                {
                    Wait[k] -= seconds;
                    if (Wait[k] > 0) continue;
                    float every = Every((Kind)k, clock.Hours, weekend, community);
                    if (every <= 0) { Wait[k] = 10f; continue; } // (not at this hour - looked at again in a while)
                    Wait[k] = every * (0.6f + (float)Rand.NextDouble() * 0.8f);
                    if (Active.Count >= MaxAtOnce || IsPlaying((Kind)k)) continue;
                    Start((Kind)k);
                }
            }
            catch (Exception e)
            {
                if (Failures++ < 3) Console.WriteLine("[SimsRedux] neighbourhood sounds: " + e.Message);
                StopAll();
            }
        }

        /// <summary>The lot closed (or the setting went off): everything stops now.</summary>
        public static void StopAll()
        {
            for (int i = 0; i < Active.Count; i++)
            {
                try { Stop(Active[i]); } catch { }
            }
            Active.Clear();
            LastVM = null;
        }

        /// <summary>
        /// About how often (real seconds) a kind is heard at this hour, or 0 for not now. Weekdays: school until 3, the
        /// rush hours 7-9 AM and 4-7 PM. Weekends: children out all day, lawnmowers in the morning.
        /// </summary>
        private static float Every(Kind kind, int hour, bool weekend, bool community)
        {
            switch (kind)
            {
                case Kind.Birds:
                    if (hour >= 5 && hour < 10) return 25f;  // the dawn chorus
                    if (hour >= 10 && hour < 19) return 75f;
                    return 0f;
                case Kind.Night:
                    return (hour >= 20 || hour < 5) ? 36f : 0f; // crickets
                case Kind.Dog:
                    if (hour >= 7 && hour < 22) return 120f;
                    return hour >= 22 ? 260f : 0f;
                case Kind.Kids:
                    if (weekend ? (hour >= 9 && hour < 19) : (hour >= 15 && hour < 19)) return 60f;
                    return (community && hour >= 10 && hour < 18) ? 90f : 0f;
                case Kind.Mower:
                    return (weekend && !community && hour >= 8 && hour < 12) ? 300f : 0f;
                case Kind.Traffic:
                    if (!weekend && ((hour >= 7 && hour < 9) || (hour >= 16 && hour < 19))) return 42f; // the rush hours
                    return (community && hour >= 9 && hour < 20) ? 100f : 0f;
                case Kind.Crowd:
                    if (!community) return 0f;
                    if (hour >= 17) return 42f; // a busy evening
                    return (hour >= 10) ? 80f : 0f;
            }
            return 0f;
        }

        private static bool IsPlaying(Kind kind)
        {
            foreach (var p in Active) if (p.Kind == kind) return true;
            return false;
        }

        private static void Start(Kind kind)
        {
            int k = (int)kind;
            var p = new Playing { Kind = kind, Life = Longest[k] * (0.7f + (float)Rand.NextDouble() * 0.3f) };
            p.FadeIn = Longest[k] >= 25f; // (the long ones come in softly; a bark or a laugh plays as it is)
            var names = Events[k];
            if (names.Count > 0)
            {
                var name = names[Rand.Next(names.Count)];
                var snd = HITVM.Get().PlaySoundEvent(name);
                if (snd == null) return;
                if (!snd.AlreadyOwns(OwnerID)) snd.AddOwner(OwnerID);
                p.Hit = snd;
                p.Pan = p.FadeIn ? 0f : (float)(Rand.NextDouble() - 0.5);
                snd.SetVolume(0f, p.Pan, OwnerID);
            }
            else if (TSOFound[k] != null)
            {
                var path = TSOFound[k];
                if (path.EndsWith(".fsc", StringComparison.OrdinalIgnoreCase))
                {
                    p.Fsc = HITVM.Get().PlayFSC(path);
                    if (p.Fsc == null) return;
                    p.Fsc.SetVolume(0f);
                    p.FadeIn = true;
                }
                else
                {
                    byte[] data = new FSO.Files.XA.XAFile(path).DecompressedData;
                    using (var stream = new System.IO.MemoryStream(data))
                        p.Sfx = Microsoft.Xna.Framework.Audio.SoundEffect.FromStream(stream);
                    p.Loop = p.Sfx.CreateInstance();
                    p.Loop.IsLooped = true;
                    p.Loop.Volume = 0f;
                    p.Loop.Play();
                    p.FadeIn = true;
                }
            }
            else return;
            Active.Add(p);
        }

        private static void Stop(Playing p)
        {
            if (p.Hit != null)
            {
                if (p.Hit.AlreadyOwns(OwnerID)) p.Hit.RemoveOwner(OwnerID); // (no owner left: it stops on its next tick)
                p.Hit = null;
            }
            if (p.Fsc != null) { HITVM.Get()?.StopFSC(p.Fsc); p.Fsc = null; }
            if (p.Loop != null) { try { p.Loop.Stop(); p.Loop.Dispose(); } catch { } p.Loop = null; }
            if (p.Sfx != null) { try { p.Sfx.Dispose(); } catch { } p.Sfx = null; }
        }

        /// <summary>Finds the game's own sounds for each kind, once (by the sound events' names).</summary>
        private static void Find()
        {
            if (Events != null) return;
            var found = new List<string>[KindCount];
            for (int i = 0; i < KindCount; i++) found[i] = new List<string>();
            var tso = new string[KindCount];
            try
            {
                var events = FSO.Content.Content.Get()?.Audio?.Events;
                if (events != null)
                {
                    foreach (var pair in events)
                    {
                        if (pair.Value == null || pair.Value.EventType != FSO.Files.HIT.HITEvents.kSoundobPlay) continue;
                        int kind = KindOf(pair.Key);
                        if (kind >= 0) found[kind].Add(pair.Key);
                    }
                }
                for (int i = 0; i < KindCount; i++) found[i].Sort(StringComparer.Ordinal);
                if (FSO.Content.Content.HasTSO)
                {
                    for (int i = 0; i < KindCount; i++)
                    {
                        if (found[i].Count > 0 || TSOPaths[i] == null) continue;
                        var path = FSO.Content.Content.TSOPath(TSOPaths[i]);
                        if (FSO.Common.IO.FSOFile.Exists(path)) tso[i] = path;
                    }
                }
            }
            catch (Exception e) { Console.WriteLine("[SimsRedux] neighbourhood sounds: couldn't look through the sounds: " + e.Message); }
            var line = new System.Text.StringBuilder("[SimsRedux] neighbourhood sounds:");
            for (int i = 0; i < KindCount; i++)
                line.Append(" " + ((Kind)i).ToString().ToLowerInvariant() + "=" + (found[i].Count > 0 ? string.Join("|", found[i]) : tso[i] != null ? "(Sims Online)" : "-"));
            Console.WriteLine(line.ToString());
            TSOFound = tso;
            Events = found;
        }

        private static readonly char[] Separators = { '_', '-', ' ', '.' };

        /// <summary>Which kind a sound event's name says it is (-1: none) - whole words of the name only.</summary>
        private static int KindOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            var words = new HashSet<string>(name.ToLowerInvariant().Split(Separators, StringSplitOptions.RemoveEmptyEntries));
            // never a Sim's voice, an interface sound, music, a radio or TV, a phone, a nightclub, a car horn
            foreach (var w in new[] { "vox", "ui", "music", "station", "bkground", "tv", "radio", "stereo", "jukebox", "phone", "honk", "nc", "dj" })
                if (words.Contains(w)) return -1;
            Func<string[], bool> any = (ws) => { foreach (var w in ws) if (words.Contains(w)) return true; return false; };
            if (any(new[] { "cricket", "crickets", "cicada", "cicadas", "frog", "frogs", "owl", "nightbirds", "insect", "insects" })
                || (words.Contains("night") && any(new[] { "amb", "ambience", "ambient", "bird", "birds" })))
                return (int)Kind.Night;
            if (any(new[] { "bird", "birds", "songbird", "songbirds", "daybirds", "chirp", "chirps", "birdsong" })) return (int)Kind.Birds;
            if (any(new[] { "mower", "lawnmower", "mowing" })) return (int)Kind.Mower;
            if (any(new[] { "kids", "children", "playground", "schoolyard" })
                || (any(new[] { "kid", "child" }) && any(new[] { "laugh", "laughs", "laughing", "play", "playing", "giggle", "amb", "ambience" })))
                return (int)Kind.Kids;
            if (any(new[] { "dog", "dogs" }) && any(new[] { "bark", "barks", "barking", "distant", "amb", "ambience" })) return (int)Kind.Dog;
            if (any(new[] { "traffic", "driveby" }) || (any(new[] { "street", "cars", "city" }) && any(new[] { "amb", "ambience", "ambient", "distant" })))
                return (int)Kind.Traffic;
            if (any(new[] { "crowd", "murmur", "walla", "chatter" })) return (int)Kind.Crowd;
            return -1;
        }
    }

    public struct VMCategorisedAmb
    {
        public uint GUID;
        public byte Category;
        public string Name;

        public VMCategorisedAmb(uint guid, byte cat, string name)
        {
            GUID = guid;
            Category = cat;
            Name = name;
        }
    }
}
