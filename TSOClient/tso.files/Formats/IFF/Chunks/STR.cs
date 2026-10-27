using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using FSO.Files.Utils;

namespace FSO.Files.Formats.IFF.Chunks
{
    /// <summary>
    /// This chunk type holds text strings. 
    /// The first two bytes correspond to the format code, of which there are four types. 
    /// Some chunks in the game do not specify any data after the version number, so be sure to implement bounds checking.
    /// </summary>
    public class STR : IffChunk
    {
        public static string[] LanguageSetNames =
        {
            "English (US)",
            "English (UK)",
            "French",
            "German",
            "Italian",
            "Spanish",
            "Dutch",
            "Danish",
            "Swedish",
            "Norwegian",
            "Finish",
            "Hebrew",
            "Russian",
            "Portuguese",
            "Japanese",
            "Polish",
            "Simplified Chinese",
            "Traditional Chinese",
            "Thai",
            "Korean",
            "Slovak"
        };

        public STRLanguageSet[] LanguageSets = new STRLanguageSet[20];
        public static STRLangCode DefaultLangCode = STRLangCode.EnglishUS;

        /// <summary>
        /// SimsRedux 17.4: the format code the chunk was read in (0 when made in code or never read). Only -3 and -4 tag every
        /// string with its language; the older formats (0, -1, -2) hold one untagged set in slot 0 - whatever language the
        /// install is in. Not saved (the file keeps its own format).
        /// </summary>
        public short ReduxFormat;

        /// <summary>
        /// SimsRedux 17.4: the English US string at index, ONLY when the strings are tagged with their language (format -3 or -4),
        /// else null. It never depends on the language the player's game is set to, so the lot can use it to tell what a choice
        /// is and every game on a visit gets the same answer. (Untagged strings could be in any language - not used.)
        /// </summary>
        public string TaggedEnglish(int index)
        {
            if (ReduxFormat != -3 && ReduxFormat != -4) return null;
            var set = (LanguageSets != null && LanguageSets.Length > 0) ? LanguageSets[0] : null;
            if (set?.Strings == null || index < 0 || index >= set.Strings.Length) return null;
            return set.Strings[index]?.Value;
        }

        public STR()
        {
            for (int i = 0; i < 20; i++) LanguageSets[i] = new STRLanguageSet { Strings = new STRItem[0] };
        }

        /// <summary>
        /// How many strings are in this chunk?
        /// </summary>
        public int Length
        {
            get
            {
                return LanguageSets[0]?.Strings.Length ?? 0;
            }
        }

        public STRLanguageSet GetLanguageSet(STRLangCode set)
        {
            if (set == STRLangCode.Default) set = DefaultLangCode;
            int code = (int)set;
            if ((LanguageSets[code-1]?.Strings.Length ?? 0) == 0) return LanguageSets[0]; //if undefined, fallback to English US
            else return LanguageSets[code-1];
        }

        public bool IsSetInit(STRLangCode set)
        {
            if (set == STRLangCode.Default) set = DefaultLangCode;
            if (set == STRLangCode.EnglishUS) return true;
            int code = (int)set;
            return (LanguageSets[code - 1].Strings.Length > 0);
        }

        public void InitLanguageSet(STRLangCode set)
        {
            if (set == STRLangCode.Default) set = DefaultLangCode;
            int code = (int)set;
            var length = LanguageSets[0].Strings.Length;
            LanguageSets[code - 1].Strings = new STRItem[length];
            for (int i=0; i< length; i++)
            {
                var src = LanguageSets[0].Strings[i];
                LanguageSets[code - 1].Strings[i] = new STRItem()
                {
                    LanguageCode = (byte)code,
                    Value = src.Value,
                    Comment = src.Comment
                };
            }
        }

        /// <summary>
        /// Gets a string from this chunk.
        /// </summary>
        /// <param name="index">Index of string.</param>
        /// <returns>A string at specific index, null if not found.</returns>
        /// 
        public string GetString(int index)
        {
            return GetString(index, STRLangCode.Default);
        }
        public string GetString(int index, STRLangCode language)
        {
            var item = GetStringEntry(index, language);
            if (item != null)
            {
                return item.Value;
            }
            return null;
        }

        public string GetComment(int index)
        {
            return GetComment(index, STRLangCode.Default);
        }
        public string GetComment(int index, STRLangCode language)
        {
            var item = GetStringEntry(index, language);
            if (item != null)
            {
                return item.Comment;
            }
            return null;
        }

        public void SetString(int index, string value)
        {
            SetString(index, value, STRLangCode.Default);
        }
        public void SetString(int index, string value, STRLangCode language)
        {
            var languageSet = GetLanguageSet(language);
            if (index < languageSet.Strings.Length)
            {
                languageSet.Strings[index].Value = value;
            }
        }

        public void SwapString(int srcindex, int dstindex)
        {
            foreach (var languageSet in LanguageSets)
            {
                if (languageSet.Strings.Length == 0) continue; //language not initialized
                var temp = languageSet.Strings[srcindex];
                languageSet.Strings[srcindex] = languageSet.Strings[dstindex];
                languageSet.Strings[dstindex] = temp;
            }
        }

        public void InsertString(int index, STRItem item)
        {
            byte i = 1;
            foreach (var languageSet in LanguageSets) {
                if (languageSet.Strings.Length == 0 && i > 1)
                {
                    i++;
                    continue; //language not initialized
                }
                var newStr = new STRItem[languageSet.Strings.Length + 1];
                Array.Copy(languageSet.Strings, newStr, index); //copy before strings
                newStr[index] = new STRItem()
                {
                    LanguageCode = i,
                    Value = item.Value,
                    Comment = item.Comment
                };
                Array.Copy(languageSet.Strings, index, newStr, index + 1, (languageSet.Strings.Length - index));
                languageSet.Strings = newStr;
                i++;
            }
        }

        public void RemoveString(int index)
        {
            foreach (var languageSet in LanguageSets)
            {
                if (languageSet.Strings.Length == 0) continue; //language not initialized
                var newStr = new STRItem[languageSet.Strings.Length - 1];
                Array.Copy(languageSet.Strings, newStr, index); //copy before strings
                Array.Copy(languageSet.Strings, index + 1, newStr, index, (languageSet.Strings.Length - (index + 1)));
                languageSet.Strings = newStr;
            }
        }

        /// <summary>
        /// SimsRedux: the first two strings (a catalog's name and description) straight from the raw bytes of a
        /// language-coded (FD FF) chunk, the same as GetString(0) and - when it has more than one - GetString(1)
        /// give, without reading every language's strings (the catalog does this for every object while loading).
        /// False if the chunk is in another format (read it normally then).
        /// </summary>
        public static bool PeekFirstTwo(byte[] data, out string first, out string second)
        {
            return PeekFirstTwo(data, out first, out second, out _);
        }

        /// <summary>
        /// SimsRedux: the same, and also the first English US string (englishFirst - null if it has none), which is the
        /// same whatever language the game is in (prices must be worked out the same on every player's game).
        /// </summary>
        public static bool PeekFirstTwo(byte[] data, out string first, out string second, out string englishFirst)
        {
            first = second = englishFirst = null;
            if (data == null || data.Length < 4) return false;
            if (BitConverter.ToInt16(data, 0) != -3) return false;
            try
            {
                int target = (int)DefaultLangCode;
                if (target < 1 || target > 20) target = 1;
                int count = BitConverter.ToUInt16(data, 2);
                int at = 4;
                string e0 = null, e1 = null, t0 = null, t1 = null;
                int english = 0, own = 0;
                for (int i = 0; i < count; i++)
                {
                    if (at >= data.Length) return false;
                    int lang = data[at++];
                    if (lang == 0) lang = 1;
                    int start = at;
                    while (data[at] != 0) at++;
                    string value = null;
                    bool wanted = (lang == 1 && english < 2) || (lang == target && own < 2);
                    if (wanted)
                    {
                        var chars = new char[at - start];
                        for (int k = 0; k < chars.Length; k++) chars[k] = (char)data[start + k];
                        value = new string(chars);
                    }
                    at++;
                    while (data[at] != 0) at++; // (the comment)
                    at++;
                    if (lang > 20) continue;
                    if (lang == 1) { if (english == 0) e0 = value; else if (english == 1) e1 = value; english++; }
                    if (lang == target && target != 1) { if (own == 0) t0 = value; else if (own == 1) t1 = value; own++; }
                    if (english >= 2 && (target == 1 || own >= 2)) break;
                }
                // (GetLanguageSet: the game's language if it has any strings, else English US; Length counts English US)
                bool useOwn = target != 1 && own > 0;
                first = useOwn ? t0 : (english > 0 ? e0 : null);
                if (english > 1) second = useOwn ? (own > 1 ? t1 : null) : e1;
                else second = "";
                englishFirst = english > 0 ? e0 : null;
                return true;
            }
            catch (Exception) { first = second = englishFirst = null; return false; }
        }

        /// <summary>
        /// Gets a STRItem instance from this STR chunk.
        /// </summary>
        /// <param name="index">Index of STRItem.</param>
        /// <returns>STRItem at index, null if not found.</returns>
        public STRItem GetStringEntry(int index)
        {
            return GetStringEntry(index, STRLangCode.Default);
        }
        public STRItem GetStringEntry(int index, STRLangCode language)
        {
            var languageSet = GetLanguageSet(language);
            if (index < (languageSet?.Strings.Length ?? 0) && index > -1)
            {
                return languageSet.Strings[index];
            }
            return null;
        }

        /// <summary>
        /// Reads a STR chunk from a stream.
        /// </summary>
        /// <param name="iff">An Iff instance.</param>
        /// <param name="stream">A Stream object holding a STR chunk.</param>
        public override void Read(IffFile iff, Stream stream)
        {
            using (var io = IoBuffer.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                var formatCode = io.ReadInt16();
                ReduxFormat = formatCode; // (SimsRedux 17.4: TaggedEnglish)
                LanguageSets = new STRLanguageSet[20];
                if (!io.HasMore){
                    for (int i = 0; i < 20; i++) LanguageSets[i] = new STRLanguageSet { Strings = new STRItem[0] };
                    return;
                }

                if (formatCode == 0)
                {
                    var numStrings = io.ReadUInt16();
                    for (int i = 0; i < 20; i++) LanguageSets[i] = new STRLanguageSet { Strings = new STRItem[0] };
                    LanguageSets[0].Strings = new STRItem[numStrings];
                    for (var i = 0; i < numStrings; i++)
                    {
                        LanguageSets[0].Strings[i] = new STRItem
                        {
                            Value = io.ReadPascalString()
                        };
                    }
                }
                //This format changed 00 00 to use C strings rather than Pascal strings.
                else if (formatCode == -1)
                {
                    var numStrings = io.ReadUInt16();
                    for (int i = 0; i < 20; i++) LanguageSets[i] = new STRLanguageSet { Strings = new STRItem[0] };
                    LanguageSets[0].Strings = new STRItem[numStrings];
                    for (var i = 0; i < numStrings; i++)
                    {
                        LanguageSets[0].Strings[i] = new STRItem
                        {
                            Value = io.ReadNullTerminatedUTF8()
                        };
                    }
                }
                //This format changed FF FF to use string pairs rather than single strings.
                else if (formatCode == -2)
                {
                    var numStrings = io.ReadUInt16();
                    for (int i = 0; i < 20; i++) LanguageSets[i] = new STRLanguageSet { Strings = new STRItem[0] };
                    LanguageSets[0].Strings = new STRItem[numStrings];
                    for (var i = 0; i < numStrings; i++)
                    {
                        LanguageSets[0].Strings[i] = new STRItem
                        {
                            Value = io.ReadNullTerminatedString(),
                            Comment = io.ReadNullTerminatedString()
                        };
                    }
                }
                //This format changed FD FF to use a language code.
                else if (formatCode == -3)
                {
                    var numStrings = io.ReadUInt16();
                    for (int i = 0; i < 20; i++) LanguageSets[i] = new STRLanguageSet { Strings = new STRItem[0] };
                    List<STRItem>[] LangSort = new List<STRItem>[20];
                    for (var i = 0; i < numStrings; i++)
                    {
                        var item = new STRItem
                        {
                            LanguageCode = io.ReadByte(),
                            Value = io.ReadNullTerminatedString(),
                            Comment = io.ReadNullTerminatedString()
                        };

                        var lang = item.LanguageCode;
                        if (lang == 0) lang = 1;
                        else if (lang < 0 || lang > 20) continue; //???
                        if (LangSort[lang - 1] == null)
                        {
                            LangSort[lang-1] = new List<STRItem>();
                        }

                        LangSort[lang - 1].Add(item);
                    }
                    for (int i=0; i<LanguageSets.Length; i++)
                    {
                        if (LangSort[i] != null) LanguageSets[i].Strings = LangSort[i].ToArray();
                    }
                }

                //This format is only used in The Sims Online. The format is essentially a performance improvement: 
                //it counteracts both the short string limit of 255 characters found in 00 00 and the inherent slowness 
                //of null-terminated strings in the other formats (which requires two passes over each string), and it 
                //also provides a string pair count for each language set which eliminates the need for two passes over 
                //each language set.
                else if (formatCode == -4)
                {
                    var numLanguageSets = io.ReadByte();
                    if (LanguageSets.Length != numLanguageSets)
                    {
                        this.LanguageSets = new STRLanguageSet[numLanguageSets];
                    }

                    for(var i=0; i < numLanguageSets; i++)
                    {
                        var item = new STRLanguageSet();
                        var numStringPairs = io.ReadUInt16();

                        if (numStringPairs == 0)
                        {
                            continue;
                        }

                        item.Strings = new STRItem[numStringPairs];
                        for (var x = 0; x < numStringPairs; x++)
                        {
                            item.Strings[x] = new STRItem 
                            {
                                LanguageCode = (byte)(io.ReadByte() + 1),
                                Value = io.ReadVariableLengthPascalString(),
                                Comment = io.ReadVariableLengthPascalString()
                            };
                        }
                        this.LanguageSets[i] = item;
                    }
                }
            }
        }
        public override bool Write(IffFile iff, Stream stream)
        {
            using (var io = IoWriter.FromStream(stream, ByteOrder.LITTLE_ENDIAN))
            {
                if (IffFile.TargetTS1)
                {
                    // TS1 format - null terminated string
                    io.WriteInt16(-3);
                    var total = (short)LanguageSets.Sum(x => x?.Strings?.Length ?? 0);
                    io.WriteInt16(total);
                    foreach (var set in LanguageSets)
                    {
                        if (set?.Strings != null)
                        {
                            foreach (var str in set.Strings)
                            {
                                io.WriteByte((byte)(str.LanguageCode));
                                io.WriteNullTerminatedString(str.Value);
                                io.WriteNullTerminatedString(str.Comment);
                            }
                        }
                    }
                    for (int i=0; i<total; i++)
                    {
                        io.WriteByte(0xA3);
                    }
                }
                else
                {
                    // TSO format - variable length pascal
                    io.WriteInt16(-4);
                    io.WriteByte(20);

                    foreach (var set in LanguageSets)
                    {
                        if (set?.Strings == null)
                        {
                            io.WriteInt16(0);
                        }
                        else
                        {
                            io.WriteUInt16((ushort)set.Strings.Length);

                            foreach (var str in set.Strings)
                            {
                                io.WriteByte((byte)(str.LanguageCode - 1));
                                io.WriteVariableLengthPascalString(str.Value);
                                io.WriteVariableLengthPascalString(str.Comment);
                            }
                        }
                    }
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Item in a STR chunk.
    /// </summary>
    public class STRItem
    {
        public byte LanguageCode;
        public string Value;
        public string Comment;

        public STRItem()
        {

        }

        public STRItem(string value)
        {
            Value = value;
            Comment = "";
        }
    }

    public enum STRLangCode : byte
    {
        Default = 0,
        EnglishUS = 1,
        EnglishUK = 2,
        French = 3,
        German = 4,
        Italian = 5,
        Spanish = 6,
        Dutch = 7,
        Danish = 8,
        Swedish = 9,
        Norwegian = 10,
        Finish = 11,
        Hebrew = 12,
        Russian = 13,
        Portuguese = 14,
        Japanese = 15,
        Polish = 16,
        SimplifiedChinese = 17,
        TraditionalChinese = 18,
        Thai = 19,
        Korean = 20,

        //begin freeso
        Slovak = 21
    }

    /// <summary>
    /// Set of STRItems for a language.
    /// </summary>
    public class STRLanguageSet
    {
        public STRItem[] Strings = new STRItem[0];
    }
}
