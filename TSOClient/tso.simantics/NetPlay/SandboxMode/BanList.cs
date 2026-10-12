using FSO.Common;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using FSO.Common.IO;
namespace FSO.SimAntics.NetPlay.SandboxMode
{
    public class BanList
    {
        HashSet<string> BannedHosts;
        string Dir;

        public BanList()
        {
            Dir = FSOEnvironment.UserDir;
            var path = Path.Combine(Dir, "banlist.txt");
            if (!FSOFile.Exists(path)) FSOFile.Create(path).Close();

            using (var list = FSOFile.OpenText(path))
            {
                BannedHosts = new HashSet<string>();
                string line;
                while ((line = list.ReadLine()) != null)
                {
                    BannedHosts.Add(line);
                }
            }
        }

        public bool Contains(string host)
        {
            return BannedHosts.Contains(host.ToLowerInvariant());
        }

        public void Add(string host)
        {
            BannedHosts.Add(host.ToLowerInvariant());
            Write();
        }

        public void Remove(string host)
        {
            BannedHosts.Remove(host.ToLowerInvariant());
            Write();
        }

        public List<string> List()
        {
            return BannedHosts.ToList();
        }

        public void Write()
        {
            using (var list = FSOFile.Open(Dir + "banlist.txt", FileMode.Create))
            {
                var writer = new StreamWriter(list);
                foreach (var host in BannedHosts)
                {
                    writer.WriteLine(host);
                }
            }
        }
    }
}
