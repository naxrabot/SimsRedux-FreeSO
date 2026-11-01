using FSO.Content.Framework;
using FSO.Content.Model;

namespace FSO.Content.TS1
{
    public class TS1AvatarTextureProvider : TS1SubProvider<ITextureRef>
    {
        public TS1AvatarTextureProvider(TS1Provider baseProvider) : base(baseProvider, new string[] { ".bmp", ".tga" })
        {
        }

        public override ITextureRef Get(string name)
        {
            // SimsRedux: a Sims Online outfit's texture (see ReduxTSOAvatars) - never one of the names listed by
            // GetAllNames, so the Sims 1 outfits' "first texture starting with..." look-ups never land on one
            if (ReduxTSOAvatars.IsTSOFile(name) || ReduxTSOAvatars.IsTSOName(name)) return ReduxTSOAvatars.Instance?.GetTexture(name);
            var try1 = base.Get(name.ToLowerInvariant() + ".bmp");
            if (try1 != null) return try1;
            return base.Get(name.ToLowerInvariant()+".tga");
        }

        /// <summary>SimsRedux 19.9: is this texture there? (from the file lists - nothing is read or decoded)</summary>
        public bool HasTexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (ReduxTSOAvatars.IsTSOFile(name) || ReduxTSOAvatars.IsTSOName(name)) return ReduxTSOAvatars.Instance?.HasTexture(name) ?? false;
            var n = name.ToLowerInvariant();
            return Has(n + ".bmp") || Has(n + ".tga");
        }
    }
}
