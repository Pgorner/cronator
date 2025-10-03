using System;
using System.IO;
using System.Reflection;

namespace Cronator
{
    internal static class ResourceLoader
    {
        internal static Stream? Open(string resourceName)
        {
            // resourceName is the *manifest* name, usually "<DefaultNamespace>.<path.with.dots>"
            // e.g. "Cronator.Assets.cronator.gif"
            return Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        }

        internal static byte[]? ReadAllBytes(string resourceName)
        {
            using var s = Open(resourceName);
            if (s == null) return null;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
    }
}
