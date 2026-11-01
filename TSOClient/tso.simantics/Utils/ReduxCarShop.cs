using FSO.Common.IO;
using FSO.LotView.Model;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace FSO.SimAntics.Utils
{
    /// <summary>
    /// SimsRedux car pack round 3: cars a family can buy (Buy mode > Miscellaneous > Cars: "The Showroom") and drive.
    ///
    /// WHAT A BOUGHT CAR IS ON THE LOT
    /// The game's own standard car (CarStd, "Car - Standard Carpool", GUID CarGUID - in every player's Sims files), parked:
    ///   attribute 0  OwnedMarker (98) - the car's shared main (CarGlobals "shared main") only works as a carpool when it's 1;
    ///                anything else just idles, so it never drives off on its own. ReduxTraffic leaves it alone (it isn't
    ///                passing traffic, Marker 99) and doesn't count it as a carpool (which would stop the street's traffic);
    ///   attribute 20 its NFSU2 car, paint and version (ReduxCarSkins.SkinAttribute - ReduxCarSkins.Code(car, variant, paint));
    ///   attribute 21 the family it belongs to (their FAMI chunk number);
    ///   players can move and sell it in Buy mode (movement flags), and it sells back like any object (it remembers what
    ///   was paid: the group's price, saved with the lot).
    /// A game without the car pack draws it as the Sims car; one with the pack as that NFSU2 car (ReduxCarSkins.Swap - drawing
    /// only). Nothing on the lot ever depends on the pack, and the save format is unchanged (attributes only).
    /// A car parked for a trip (VisitMarker, 97) is the same, on the lot the family drove to - it isn't theirs to sell there.
    ///
    /// PRICES live in the pack's manifest (carpack.json: each car's versions with their price and parts' prices - what the
    /// panel's Car Customizer will edit in round 4) and, for a game without the pack, in the table below (the same numbers
    /// as Client/Tools/SimsRedux.CarPack's CarCatalog - its tests check the two match). The game that runs the lot works the
    /// price out when the car is bought and the command carries it, so every game of the lot takes the same money.
    ///
    /// Everything that changes the lot here is called from VMNetReduxCarCmd (the same on every game) and looks only at the
    /// lot's own state.
    /// </summary>
    public static class ReduxCarShop
    {
        /// <summary>The Sims car a bought car is (every Sims 1 install has it: the standard carpool car, 3 x 6 tiles).</summary>
        public const uint CarGUID = 0xdb6dedb2;
        /// <summary>Attribute 0 of a family's own car, and of a car parked for a trip.</summary>
        public const short OwnedMarker = 98, VisitMarker = 97;
        /// <summary>Attribute 21: the family's FAMI chunk number. Attribute 22: a passing car that is a parked car driving off (its object id).</summary>
        public const int FamilyAttribute = 21, TagAttribute = 22;
        /// <summary>How the tiles of a parked car lie: 6 along the street, 3 across.</summary>
        public const int CarLength = 6, CarWidth = 3;

        // ================================================================ the catalog

        public sealed class Part { public string Name = ""; public int Price; }
        public sealed class Paint { public int Id; public string Name = ""; public int R, G, B; }
        public sealed class Variant
        {
            /// <summary>0 stock, 1 the customised version, 2.. more premade versions (round 4: the panel's Car Customizer).</summary>
            public int Id;
            public string Name = "";
            public List<Part> Parts = new List<Part>();
            public List<Paint> Paints = new List<Paint>();
            /// <summary>What the parts add to the car's price.</summary>
            public int Extra { get { int s = 0; foreach (var p in Parts) s += p.Price; return s + ExtraWithoutParts; } }
            /// <summary>A manifest price above the car's that its parts don't explain (a version listed with a price only).</summary>
            public int ExtraWithoutParts;
        }
        public sealed class CarModel
        {
            /// <summary>The car's number (ReduxCarSkins.CarFolders).</summary>
            public int Car;
            public string Name = "", Kind = "";
            public int BasePrice;
            public List<Variant> Variants = new List<Variant>();
            public Variant VariantById(int id) { foreach (var v in Variants) if (v.Id == id) return v; return null; }
        }

        /// <summary>NFSU2's ten basic gloss paints (the stock versions) - CarCatalog.Normal.</summary>
        private static readonly Paint[] NormalPaints =
        {
            P(0, "Silver", 130, 130, 142), P(1, "White", 190, 190, 190), P(2, "Blue", 8, 60, 161), P(3, "Yellow", 210, 203, 10),
            P(4, "Red", 141, 15, 15), P(5, "Grey", 62, 62, 62), P(6, "Black", 16, 16, 16), P(7, "Orange", 165, 75, 15),
            P(8, "Navy blue", 5, 15, 109), P(9, "Green", 37, 124, 10),
        };
        /// <summary>The customised versions' paints - CarCatalog.Custom.</summary>
        private static readonly Paint[] CustomPaints =
        {
            P(0, "Orange", 242, 92, 5), P(1, "Lime", 107, 209, 20), P(2, "Purple", 92, 20, 148), P(3, "Yellow", 242, 199, 10),
            P(4, "Electric blue", 0, 107, 242),
        };
        private static Paint P(int id, string name, int r, int g, int b) { return new Paint { Id = id, Name = name, R = r, G = g, B = b }; }

        // ---- PRICES: must match Client/Tools/SimsRedux.CarPack/Pack/CarCatalog.cs (Price, CustomParts) ----
        /// <summary>The cars on sale (car number, name, kind, price): the everyday cars and the traffic vehicles - not the service ones.</summary>
        private static readonly object[][] Table =
        {
            new object[] { 0, "Nissan 240SX", "Sports", 9500 },
            new object[] { 1, "Audi A3", "Compact", 6000 },
            new object[] { 2, "Toyota Celica", "Sports", 9000 },
            new object[] { 3, "Honda Civic", "Compact", 4500 },
            new object[] { 4, "Toyota Corolla GTS", "Compact", 4000 },
            new object[] { 5, "Mitsubishi Eclipse", "Sports", 10000 },
            new object[] { 6, "Cadillac Escalade", "SUV", 11000 },
            new object[] { 7, "Ford Focus", "Compact", 4200 },
            new object[] { 8, "Volkswagen Golf", "Compact", 5000 },
            new object[] { 9, "Lexus IS300", "Sports", 12000 },
            new object[] { 10, "Mitsubishi Lancer Evolution VIII", "Sports", 14000 },
            new object[] { 11, "Mazda MX-5", "Sports", 9000 },
            new object[] { 12, "Peugeot 206", "Compact", 4000 },
            new object[] { 13, "Acura RSX", "Sports", 9500 },
            new object[] { 14, "Nissan Sentra", "Compact", 4500 },
            new object[] { 15, "Nissan Skyline", "Sports", 15000 },
            new object[] { 16, "Hyundai Tiburon", "Sports", 9000 },
            new object[] { 17, "Pickup truck", "Truck", 7500 },
            new object[] { 18, "Minivan", "Van", 6500 },
            new object[] { 19, "Panel van", "Van", 6000 },
            new object[] { 20, "SUV", "SUV", 8000 },
        };
        /// <summary>The customised version's parts and what each adds (CarSpec.Customised: kit, hood, rims, spoiler, lower).</summary>
        private static readonly object[][] CustomParts =
        {
            new object[] { "Body kit", 1500 },
            new object[] { "Vented hood", 600 },
            new object[] { "Enkei rims", 1200 },
            new object[] { "Spoiler", 800 },
            new object[] { "Lowered", 500 },
        };

        private static List<CarModel> BuiltIn()
        {
            var l = new List<CarModel>();
            foreach (var row in Table)
            {
                int car = (int)row[0];
                var m = new CarModel { Car = car, Name = (string)row[1], Kind = (string)row[2], BasePrice = (int)row[3] };
                m.Variants.Add(new Variant { Id = 0, Name = "Stock", Paints = NormalPaints.ToList() });
                if (car < ReduxCarSkins.EverydayCars)
                {
                    var v = new Variant { Id = 1, Name = "Custom", Paints = CustomPaints.ToList() };
                    foreach (var p in CustomParts) v.Parts.Add(new Part { Name = (string)p[0], Price = (int)p[1] });
                    l.Add(m);
                    m.Variants.Add(v);
                    continue;
                }
                l.Add(m);
            }
            return l;
        }

        private static List<CarModel> Cached;
        private static string CachedFor;
        private static readonly object Lock = new object();

        /// <summary>
        /// The Showroom's cars: the table above, with what the pack's manifest says on top (prices, paints, parts, and any more
        /// versions it lists - round 4) when this game has the pack. Read again when the pack folder changes. Never used for a
        /// decision inside the lot's tick: the price is worked out once by the game buying, and travels in the command.
        /// </summary>
        public static List<CarModel> Catalog()
        {
            lock (Lock)
            {
                var folder = ReduxCarSkins.PackFolder ?? "";
                if (Cached != null && CachedFor == folder) return Cached;
                var l = BuiltIn();
                if (folder.Length > 0)
                {
                    try { MergeManifest(l, Path.Combine(folder, "carpack.json")); }
                    catch (Exception e) { Console.WriteLine("[SimsRedux] showroom: couldn't read the pack's prices (" + e.Message + ") - the built-in ones are used"); }
                }
                Cached = l; CachedFor = folder;
                return l;
            }
        }

        /// <summary>Forget the catalog (the pack was updated).</summary>
        public static void Reset() { lock (Lock) { Cached = null; } }

        public static CarModel ModelOf(int car) { foreach (var m in Catalog()) if (m.Car == car) return m; return null; }

        /// <summary>What a car costs in this version: its price plus its parts' (-1: not on sale).</summary>
        public static int PriceOf(int car, int variant)
        {
            var m = ModelOf(car);
            var v = m?.VariantById(variant);
            return v == null ? -1 : m.BasePrice + v.Extra;
        }

        /// <summary>
        /// carpack.json (PackBuilder): cars[] { id, variants[] { variant, id?, name?, price, basePrice?, partPrices{}, paints[] { id, name, hex } } }.
        /// A version is matched by its id ("stock" = 0, "custom" = 1 when there's none); a field that isn't there keeps the table's.
        /// </summary>
        private static void MergeManifest(List<CarModel> cars, string path)
        {
            if (!FSOFile.Exists(path)) return;
            using (var doc = JsonDocument.Parse(FSOFile.ReadAllText(path)))
            {
                JsonElement list;
                if (!doc.RootElement.TryGetProperty("cars", out list) || list.ValueKind != JsonValueKind.Array) return;
                foreach (var c in list.EnumerateArray())
                {
                    int id = Int(c, "id", -1);
                    var m = cars.FirstOrDefault(x => x.Car == id);
                    JsonElement vars;
                    if (m == null || !c.TryGetProperty("variants", out vars) || vars.ValueKind != JsonValueKind.Array) continue;
                    // (the stock version first: its price is the car's)
                    var ordered = vars.EnumerateArray().OrderBy(x => VariantId(x) == 0 ? 0 : 1).ToList();
                    foreach (var jv in ordered)
                    {
                        int vid = VariantId(jv);
                        if (vid < 0 || vid > ReduxCarSkins.MaxVariant) continue;
                        if (vid > 1 && m.Car >= ReduxCarSkins.EverydayCars) continue;
                        var v = m.VariantById(vid);
                        if (v == null) { v = new Variant { Id = vid, Name = "Version " + vid }; m.Variants.Add(v); }
                        var name = Str(jv, "name");
                        if (!string.IsNullOrWhiteSpace(name)) v.Name = name.Trim();
                        int basePrice = Int(jv, "basePrice", -1);
                        if (vid == 0 && basePrice <= 0) basePrice = Int(jv, "price", -1);
                        if (vid == 0 && basePrice > 0) m.BasePrice = basePrice;
                        JsonElement parts;
                        if (jv.TryGetProperty("partPrices", out parts) && parts.ValueKind == JsonValueKind.Object)
                        {
                            var pl = new List<Part>();
                            foreach (var p in parts.EnumerateObject())
                                if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out int pp) && pp >= 0) pl.Add(new Part { Name = p.Name, Price = pp });
                            if (pl.Count > 0 || vid == 0) v.Parts = pl;
                        }
                        int price = Int(jv, "price", -1);
                        v.ExtraWithoutParts = 0;
                        if (vid != 0 && price > 0)
                        {
                            int left = price - m.BasePrice - v.Parts.Sum(p => p.Price);
                            if (left > 0) v.ExtraWithoutParts = left;
                        }
                        JsonElement paints;
                        if (jv.TryGetProperty("paints", out paints) && paints.ValueKind == JsonValueKind.Array)
                        {
                            var pl = new List<Paint>();
                            foreach (var jp in paints.EnumerateArray())
                            {
                                int pid = Int(jp, "id", -1);
                                if (pid < 0 || pid > 15) continue;
                                var hex = (Str(jp, "hex") ?? "").TrimStart('#');
                                int rgb = 0;
                                if (hex.Length == 6) try { rgb = Convert.ToInt32(hex, 16); } catch { rgb = 0; }
                                pl.Add(new Paint { Id = pid, Name = Str(jp, "name") ?? ("Paint " + (pid + 1)), R = (rgb >> 16) & 255, G = (rgb >> 8) & 255, B = rgb & 255 });
                            }
                            if (pl.Count > 0) v.Paints = pl;
                        }
                    }
                    m.Variants.Sort((a, b) => a.Id.CompareTo(b.Id));
                }
            }
        }

        private static int VariantId(JsonElement v)
        {
            int id = Int(v, "id", -1);
            if (id >= 0) return id;
            var name = (Str(v, "variant") ?? "").Trim().ToLowerInvariant();
            return name == "stock" || name.Length == 0 ? 0 : name == "custom" ? 1 : -1;
        }

        private static int Int(JsonElement e, string name, int def)
        {
            JsonElement v;
            if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
            return def;
        }

        private static string Str(JsonElement e, string name)
        {
            JsonElement v;
            return e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        // ================================================================ cars on the lot (read only)

        /// <summary>The car's main tile (its group's base object) - the one with the attributes.</summary>
        public static VMEntity BaseOf(VMEntity e) { return e?.MultitileGroup?.BaseObject ?? e; }

        /// <summary>A family's own car or a car parked for a trip (not passing traffic, not a carpool).</summary>
        public static bool IsParkedCar(VMEntity e)
        {
            if (e == null || e is VMAvatar) return false;
            var b = BaseOf(e);
            if (b == null || ReduxCarSkins.CarGuid(b) != CarGUID) return false;
            var m = b.GetAttribute(0);
            return m == OwnedMarker || m == VisitMarker;
        }

        public static bool IsOwnedCar(VMEntity e) { return IsParkedCar(e) && BaseOf(e).GetAttribute(0) == OwnedMarker; }
        public static bool IsVisitCar(VMEntity e) { return IsParkedCar(e) && BaseOf(e).GetAttribute(0) == VisitMarker; }
        public static short FamilyOf(VMEntity e) { return IsParkedCar(e) ? BaseOf(e).GetAttribute(FamilyAttribute) : (short)0; }
        public static short SkinOfCar(VMEntity e) { return IsParkedCar(e) ? BaseOf(e).GetAttribute(ReduxCarSkins.SkinAttribute) : (short)0; }

        /// <summary>The parked cars of a family on this lot (their own, or parked for the trip), lowest object id first.</summary>
        public static List<VMEntity> CarsOf(VM vm, int family)
        {
            var l = new List<VMEntity>();
            if (vm?.Entities == null || family <= 0) return l;
            foreach (var e in vm.Entities)
            {
                if (e == null || e.Dead || !(e is VMGameObject) || e.MultitileGroup?.BaseObject != e) continue;
                if (e.Position == LotTilePos.OUT_OF_WORLD) continue;
                if (IsParkedCar(e) && e.GetAttribute(FamilyAttribute) == family) l.Add(e);
            }
            l.Sort((a, b) => a.ObjectID.CompareTo(b.ObjectID));
            return l;
        }

        /// <summary>A car's tiles on the lot (level 1).</summary>
        public static List<Point> TilesOf(VMEntity car)
        {
            var l = new List<Point>();
            var g = BaseOf(car)?.MultitileGroup;
            if (g == null) return l;
            foreach (var o in g.Objects)
            {
                if (o.Position == LotTilePos.OUT_OF_WORLD) continue;
                var p = new Point(o.Position.TileX, o.Position.TileY);
                if (!l.Contains(p)) l.Add(p);
            }
            return l;
        }

        // ================================================================ where a car parks (deterministic)

        /// <summary>
        /// The free 3 x 6 spot a car parks in: on the lot's own ground (inside the area the family may build on - never on the
        /// sidewalk or the street), level 1, flat, no walls or fences, no objects or Sims on any of its tiles, not on a pool or
        /// road. Lying along the street, as near to it as there is room, then nearest the middle of the lot's front. The same
        /// answer on every game of the lot (only the lot's own state, a fixed search order). False: no room anywhere.
        /// </summary>
        public static bool FindSpot(VM vm, out int x0, out int y0, out Direction dir)
        {
            x0 = y0 = 0; dir = Direction.EAST;
            var ctx = vm?.Context;
            var arch = ctx?.Architecture;
            if (arch == null) return false;
            int w = arch.Width, h = arch.Height;
            // the lot's own part (where the family may build): its middle
            int bx0 = int.MaxValue, by0 = int.MaxValue, bx1 = -1, by1 = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (ctx.IsUserOutOfBounds(LotTilePos.FromBigTile((short)x, (short)y, 1))) continue;
                    if (x < bx0) bx0 = x; if (y < by0) by0 = y; if (x > bx1) bx1 = x; if (y > by1) by1 = y;
                }
            if (bx1 < 0) return false;
            int cx2 = bx0 + bx1 + 1, cy2 = by0 + by1 + 1; // (twice the middle)

            bool haveRoad = ReduxTraffic.NearestRoad(arch, cx2 / 2, cy2 / 2, out bool roadAlongX, out int middle);
            var avatars = new List<Point>();
            foreach (var a in ctx.ObjectQueries.Avatars)
                if (a != null && !a.Dead && a.Position != LotTilePos.OUT_OF_WORLD && a.Position.Level == 1) avatars.Add(new Point(a.Position.TileX, a.Position.TileY));

            long best = long.MaxValue;
            bool found = false;
            for (int pass = 0; pass < 2 && !found; pass++)
            {
                // (along the street first; across it only if there's no room that way - or no street at all)
                bool alongX = pass == 0 ? (haveRoad ? roadAlongX : true) : !(haveRoad ? roadAlongX : true);
                int cw = alongX ? CarLength : CarWidth, ch = alongX ? CarWidth : CarLength;
                for (int y = Math.Max(1, by0); y + ch - 1 <= Math.Min(h - 2, by1); y++)
                    for (int x = Math.Max(1, bx0); x + cw - 1 <= Math.Min(w - 2, bx1); x++)
                    {
                        long across, along;
                        if (haveRoad)
                        {
                            across = roadAlongX ? Math.Abs((2 * y + ch) - 2 * middle) : Math.Abs((2 * x + cw) - 2 * middle);
                            along = roadAlongX ? Math.Abs((2 * x + cw) - cx2) : Math.Abs((2 * y + ch) - cy2);
                        }
                        else { across = Math.Abs((2 * y + ch) - cy2); along = Math.Abs((2 * x + cw) - cx2); }
                        long score = across * 10000 + along;
                        if (score >= best) continue;
                        if (!RectFree(vm, x, y, cw, ch, avatars)) continue;
                        best = score; x0 = x; y0 = y; found = true;
                        // facing the way the near lane drives (right-hand traffic - ReduxTraffic), so it pulls straight out
                        if (alongX) dir = (haveRoad && roadAlongX && 2 * y + ch < 2 * middle) ? Direction.WEST : Direction.EAST;
                        else dir = (haveRoad && !roadAlongX && 2 * x + cw > 2 * middle) ? Direction.NORTH : Direction.SOUTH;
                    }
            }
            return found;
        }

        /// <summary>Is every tile of this rectangle free for a parked car (see FindSpot)?</summary>
        public static bool RectFree(VM vm, int x0, int y0, int cw, int ch, List<Point> avatars = null)
        {
            var ctx = vm.Context;
            var arch = ctx.Architecture;
            for (int y = y0; y < y0 + ch; y++)
                for (int x = x0; x < x0 + cw; x++)
                {
                    var pos = LotTilePos.FromBigTile((short)x, (short)y, 1);
                    if (ctx.IsOutOfBounds(pos) || ctx.IsUserOutOfBounds(pos)) return false;
                    if (arch.GetTerrainSloped((short)x, (short)y)) return false;
                    if (arch.GetWall((short)x, (short)y, 1).Segments != 0) return false;
                    var floor = arch.GetFloor((short)x, (short)y, 1).Pattern;
                    if (floor == 9 || floor == 10 || floor == 11 || floor >= 65534) return false; // (road, pool, water)
                    var objs = ctx.ObjectQueries.GetObjectsAt(pos);
                    if (objs != null) foreach (var o in objs) if (o != null && !o.Dead) return false;
                    if (avatars != null) foreach (var a in avatars) if (a.X == x && a.Y == y) return false;
                }
            // (a wall or fence along the far edges belongs to the next tile over)
            for (int y = y0; y < y0 + ch; y++)
                if (x0 + cw < arch.Width && (arch.GetWall((short)(x0 + cw), (short)y, 1).Segments & WallSegments.TopLeft) != 0) return false;
            for (int x = x0; x < x0 + cw; x++)
                if (y0 + ch < arch.Height && (arch.GetWall((short)x, (short)(y0 + ch), 1).Segments & WallSegments.TopRight) != 0) return false;
            return true;
        }

        // ================================================================ changing the lot (VMNetReduxCarCmd only)

        /// <summary>
        /// Parks a car (the standard car, as an NFSU2 car with this skin) with its tiles on the rectangle FindSpot gave, facing
        /// dir: the family's own (OwnedMarker: they can move and sell it; it remembers its price) or one parked for a trip
        /// (VisitMarker). Null: it couldn't be made or doesn't fit there.
        /// </summary>
        public static VMMultitileGroup Park(VM vm, int x0, int y0, Direction dir, short skin, short family, short marker, int price)
        {
            var ctx = vm.Context;
            VMMultitileGroup group = null;
            try { group = ctx.CreateObjectInstance(CarGUID, LotTilePos.OUT_OF_WORLD, dir); } catch { group = null; }
            if (group == null || group.Objects.Count == 0 || group.BaseObject == null) { group?.Delete(ctx); return null; }
            foreach (var o in group.Objects) o.SetAttribute(0, marker); // (its main just idles - never a carpool)
            var b = group.BaseObject;
            b.SetAttribute(ReduxCarSkins.SkinAttribute, skin);
            b.SetAttribute(FamilyAttribute, family);

            // where its base tile goes so that its tiles cover the spot (the same turning ChangePosition does)
            int rot = dir == Direction.NORTH ? 0 : dir == Direction.EAST ? 2 : dir == Direction.SOUTH ? 4 : 6;
            var mat = Matrix.CreateRotationZ((float)(rot * Math.PI / 4.0));
            var bOff = group.Offsets[group.Objects.IndexOf(b)];
            var lead = new Vector3(bOff.x, bOff.y, 0);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int i = 0; i < group.Objects.Count; i++)
            {
                var off = Vector3.Transform(new Vector3(group.Offsets[i].x, group.Offsets[i].y, 0) - lead, mat);
                int ox = (int)Math.Round(off.X), oy = (int)Math.Round(off.Y);
                minX = Math.Min(minX, ox); minY = Math.Min(minY, oy); maxX = Math.Max(maxX, ox); maxY = Math.Max(maxY, oy);
            }
            int tilesX = (maxX - minX) / 16 + 1, tilesY = (maxY - minY) / 16 + 1;
            if (!RectFree(vm, x0, y0, tilesX, tilesY)) { group.Delete(ctx); return null; }
            var pos = new LotTilePos((short)(x0 * 16 + 8 - minX), (short)(y0 * 16 + 8 - minY), 1);
            VMPlacementResult res;
            ReduxTraffic.Moving = true; // (our own checks above - the car object's own placing rules are for the street)
            try { res = group.ChangePosition(pos, dir, ctx, VMPlaceRequestFlags.AllowIntersection); }
            finally { ReduxTraffic.Moving = false; }
            if (res.Status != VMPlacementError.Success || b.Position == LotTilePos.OUT_OF_WORLD) { group.Delete(ctx); return null; }
            if (marker == OwnedMarker)
            {
                foreach (var o in group.Objects)
                {
                    var mf = (VMMovementFlags)o.GetValue(VMStackObjectVariable.MovementFlags);
                    o.SetValue(VMStackObjectVariable.MovementFlags, (short)(mf | VMMovementFlags.PlayersCanMove | VMMovementFlags.PlayersCanDelete));
                }
                group.InitialPrice = Math.Max(0, price);
            }
            ReduxCarSkins.Refresh(group); // (its NFSU2 picture - drawing only)
            return group;
        }

        /// <summary>
        /// The Sim walks to the car: Sims 1's own Go Here (the pie menu's) to the free tile beside the car nearest them. False:
        /// no free tile beside it, or no Go Here object.
        /// </summary>
        public static bool WalkTo(VM vm, VMAvatar sim, VMEntity car)
        {
            var ctx = vm.Context;
            var tiles = TilesOf(car);
            if (tiles.Count == 0 || sim == null || sim.Position == LotTilePos.OUT_OF_WORLD) return false;
            var around = new List<Point>();
            foreach (var t in tiles)
                foreach (var d in new[] { new Point(0, -1), new Point(1, 0), new Point(0, 1), new Point(-1, 0) })
                {
                    var n = new Point(t.X + d.X, t.Y + d.Y);
                    if (!tiles.Contains(n) && !around.Contains(n)) around.Add(n);
                }
            int sx = sim.Position.TileX, sy = sim.Position.TileY;
            around.Sort((a, b) =>
            {
                int da = (a.X - sx) * (a.X - sx) + (a.Y - sy) * (a.Y - sy), db = (b.X - sx) * (b.X - sx) + (b.Y - sy) * (b.Y - sy);
                if (da != db) return da.CompareTo(db);
                return a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.X.CompareTo(b.X);
            });
            VMMultitileGroup group = null;
            try { group = ctx.CreateObjectInstance(ReduxSidewalk.GotoGUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH); } catch { group = null; }
            var go = (group != null && group.Objects.Count > 0) ? group.Objects[0] : null;
            if (go == null) return false;
            int goHere = ReduxSidewalk.GotoInteraction(go);
            bool placed = false;
            foreach (var p in around)
            {
                var pos = LotTilePos.FromBigTile((short)p.X, (short)p.Y, 1);
                if (ctx.IsOutOfBounds(pos)) continue;
                if (go.SetPosition(pos, Direction.NORTH, ctx).Status == VMPlacementError.Success) { placed = true; break; }
            }
            var action = (placed && goHere >= 0) ? go.GetAction(goHere, sim, ctx, false, new short[] { 0, 0, 0, 0 }) : null;
            if (action == null) { go.Delete(true, ctx); return false; }
            action.Flags |= FSO.Files.Formats.IFF.Chunks.TTABFlags.FSOSkipPermissions;
            action.Priority = (short)VMQueuePriority.UserDriven;
            sim.Thread.EnqueueAction(action);
            return true;
        }

        /// <summary>The car parked for a trip on this lot - any left from before (a lot saved with one) - go.</summary>
        public static void ClearVisitCars(VM vm)
        {
            var gone = new List<VMMultitileGroup>();
            foreach (var e in vm.Entities)
                if (e != null && !e.Dead && e.MultitileGroup?.BaseObject == e && IsVisitCar(e)) gone.Add(e.MultitileGroup);
            foreach (var g in gone) g.Delete(vm.Context);
        }
    }
}
