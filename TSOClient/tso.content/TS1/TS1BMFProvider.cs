using FSO.Content.Framework;
using FSO.Vitaboy;

namespace FSO.Content.TS1
{
    public class TS1BMFProvider : TS1SubProvider<Mesh>
    {
        public TS1BMFProvider(TS1Provider baseProvider) : base(baseProvider, new string[] { ".bmf", ".skn" })
        {
        }

        public override void Init()
        {
            base.Init();
        }

        public override Mesh Get(string name)
        {
            // SimsRedux: a Sims Online outfit's mesh ("xtsob~...", see ReduxTSOAvatars)
            if (ReduxTSOAvatars.IsTSOFile(name)) return ReduxTSOAvatars.Instance?.GetMesh(name);
            var s = name.Replace(".mesh", "").ToLowerInvariant();
            return base.Get(s + ".bmf") ?? base.Get(s + ".skn");
        }
    }
}
