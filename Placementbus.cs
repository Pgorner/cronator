// PlacementBus.cs
using System;
using System.Collections.Concurrent;
using System.Drawing;

namespace Cronator
{
    /// <summary>
    /// In-proc live placement store. Settings tab pushes rects here,
    /// renderer (widgets) read from here each draw tick. Also persists to disk as you already do.
    /// Works across assemblies via AppDomain data bag.
    /// </summary>
    public static class PlacementBus
    {
        private const string MapKey = "Cronator.PlacementBus.Map";

        // key forms:
        //  - monitor-scoped: $"m{mon}:{name.ToLowerInvariant()}"
        //  - name-only:      $"*:{name.ToLowerInvariant()}"   (fallback)
        private static ConcurrentDictionary<string, RectangleF> Map
        {
            get
            {
                var o = AppDomain.CurrentDomain.GetData(MapKey) as ConcurrentDictionary<string, RectangleF>;
                if (o == null)
                {
                    o = new ConcurrentDictionary<string, RectangleF>();
                    AppDomain.CurrentDomain.SetData(MapKey, o);
                }
                return o;
            }
        }

        private static string K(int? mon, string name)
            => (mon.HasValue ? $"m{mon.Value}:" : "*:") + (name ?? "").Trim().ToLowerInvariant();

        private static RectangleF San(RectangleF r)
        {
            float Clamp01(float x) => x < 0 ? 0 : (x > 1 ? 1 : x);
            return new RectangleF(
                Clamp01(r.X), Clamp01(r.Y),
                Math.Max(0.02f, Clamp01(r.Width)),
                Math.Max(0.02f, Clamp01(r.Height)));
        }

        public static event Action<int, string, RectangleF>? Changed;

        public static void Set(int monitorIndex, string name, RectangleF rectNorm)
        {
            rectNorm = San(rectNorm);
            Map[K(monitorIndex, name)] = rectNorm;   // monitor-scoped
            Map[K(null,          name)] = rectNorm;  // fallback
            Changed?.Invoke(monitorIndex, name, rectNorm);
        }

        public static void Set(string name, RectangleF rectNorm)
        {
            rectNorm = San(rectNorm);
            Map[K(null, name)] = rectNorm;           // name-only
        }

        /// <summary>First tries monitor-scoped key, then name-only fallback.</summary>
        public static bool TryGet(int? monitorIndex, string name, out RectangleF rectNorm)
        {
            if (monitorIndex.HasValue && Map.TryGetValue(K(monitorIndex, name), out rectNorm)) return true;
            if (Map.TryGetValue(K(null, name), out rectNorm)) return true;
            rectNorm = default;
            return false;
        }

        /// <summary>Optional: preload what you read from disk at startup.</summary>
        public static void Seed(int monitorIndex, string name, RectangleF rectNorm)
        {
            rectNorm = San(rectNorm);
            Map.TryAdd(K(monitorIndex, name), rectNorm);
            Map.TryAdd(K(null, name), rectNorm);
        }
    }
}
