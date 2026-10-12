using System.IO;
using System.Linq;

using FSO.Common.IO;
namespace FSO.Common.Utils
{
    public static class PathCaseTools
    {
        public static string Insensitive(string file)
        {
            var dir = FSODirectory.GetFiles(Path.GetDirectoryName(file));
            return dir.FirstOrDefault(x => x.ToLowerInvariant().Replace('\\', '/') == file.ToLowerInvariant().Replace('\\', '/'));
        }
    }
}
