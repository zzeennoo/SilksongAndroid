// TextureFormatProbe — what texture formats this GPU samples, and what the
// loaded textures actually cost, from inside the running game.
//
// The Linux depot ships desktop block-compressed textures (DXT1, DXT5, BC7).
// When the GPU cannot sample a format, Unity expands the texture to RGBA32
// on the CPU at load time and uploads that: four times the bytes of a DXT5
// atlas, eight times a DXT1 one. The expanded copy lives in the driver, not
// in the process's malloc heap, so it barely shows in PSS -- which is exactly
// the shape of the AYANEO trace: available memory fell by 1.6 GB while the
// app's own heap sat at 48 MB.
//
// Two lines of evidence, both cheap, both logged once:
//
//   1. SystemInfo.SupportsTextureFormat for the formats that matter. This is
//      the fact the whole hypothesis rests on and it takes one frame to ask.
//   2. Snapshots of every Texture2D the engine holds, grouped by format, at
//      fixed times after the first scene -- the second one lands while the
//      language-select screen is up on a slow device. This is observed
//      RESIDENCY, which the depot-wide texture-report deliberately is not.
//
// Nothing here changes anything. It is on for every device, because the
// answer is only useful when it can be compared across them, and two
// FindObjectsOfTypeAll walks over a few thousand objects cost less than one
// frame of the game.

#if UNITY_ANDROID && !UNITY_EDITOR
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;

public class TextureFormatProbe : MonoBehaviour
{
    const string Tag = "[TextureProbe] ";
    // Seconds after the first scene. The second is late enough that a 3 GB
    // device has reached the language screen; if it is killed before then,
    // the first snapshot is the last word.
    static readonly float[] SnapshotSeconds = { 12f, 40f };
    const int TopN = 20;

    static TextureFormatProbe _instance;
    float _started;
    int _next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Bootstrap()
    {
        if (_instance != null) return;
        try { LogSupport(); }
        catch (System.Exception e) { Debug.LogWarning(Tag + "support query failed: " + e.Message); }

        var go = new GameObject("__TextureFormatProbe__");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<TextureFormatProbe>();
        _instance._started = Time.realtimeSinceStartup;
    }

    static void LogSupport()
    {
        var sb = new StringBuilder(Tag);
        sb.Append("gfx=").Append(SystemInfo.graphicsDeviceType)
          .Append(" gpu=\"").Append(SystemInfo.graphicsDeviceName).Append('"')
          .Append(" driver=\"").Append(SystemInfo.graphicsDeviceVersion).Append('"')
          .Append(" sysmem=").Append(SystemInfo.systemMemorySize).Append("MB")
          .Append(" gfxmem=").Append(SystemInfo.graphicsMemorySize).Append("MB")
          .Append(" maxTex=").Append(SystemInfo.maxTextureSize);
        Debug.Log(sb.ToString());

        sb.Clear().Append(Tag).Append("supports:");
        Support(sb, TextureFormat.DXT1);
        Support(sb, TextureFormat.DXT5);
        Support(sb, TextureFormat.BC7);
        Support(sb, TextureFormat.BC4);
        Support(sb, TextureFormat.BC5);
        Support(sb, TextureFormat.ETC2_RGB);
        Support(sb, TextureFormat.ETC2_RGBA1);
        Support(sb, TextureFormat.ETC2_RGBA8);
        Support(sb, TextureFormat.ASTC_6x6);
        Support(sb, TextureFormat.ASTC_4x4);
        Debug.Log(sb.ToString());
    }

    static void Support(StringBuilder sb, TextureFormat f)
    {
        bool ok;
        try { ok = SystemInfo.SupportsTextureFormat(f); }
        catch (System.Exception) { ok = false; }
        sb.Append(' ').Append(f).Append('=').Append(ok ? "yes" : "NO");
    }

    void Update()
    {
        if (_next >= SnapshotSeconds.Length)
        {
            Destroy(gameObject);
            return;
        }
        if (Time.realtimeSinceStartup - _started < SnapshotSeconds[_next]) return;
        int which = _next++;
        try { Snapshot(which); }
        catch (System.Exception e) { Debug.LogWarning(Tag + "snapshot failed: " + e.Message); }
    }

    class Bucket
    {
        public int count;
        public long pixels;
        public long runtimeBytes;
        public long expandedBytes;
        public int withMips;
    }

    /// <summary>
    /// Whether the player will hold this format as RGBA32 here. Asked of the
    /// GPU rather than assumed, so the number is right on a device that does
    /// sample DXT.
    /// </summary>
    static bool ExpandsToRgba32(TextureFormat f)
    {
        switch (f)
        {
            case TextureFormat.DXT1:
            case TextureFormat.DXT5:
            case TextureFormat.DXT1Crunched:
            case TextureFormat.DXT5Crunched:
            case TextureFormat.BC4:
            case TextureFormat.BC5:
            case TextureFormat.BC7:
                return !SystemInfo.SupportsTextureFormat(f);
            default:
                return false;
        }
    }

    static long Rgba32Chain(int w, int h, int mips)
    {
        long total = 0;
        for (int i = 0; i < Mathf.Max(1, mips); i++)
            total += 4L * Mathf.Max(1, w >> i) * Mathf.Max(1, h >> i);
        return total;
    }

    static void Snapshot(int which)
    {
        var all = Resources.FindObjectsOfTypeAll<Texture2D>();
        var buckets = new Dictionary<TextureFormat, Bucket>();
        var largest = new List<KeyValuePair<long, string>>();
        long totalRuntime = 0, totalExpanded = 0;
        var expands = new Dictionary<TextureFormat, bool>();

        for (int i = 0; i < all.Length; i++)
        {
            var t = all[i];
            if (t == null) continue;
            TextureFormat f = t.format;
            Bucket b;
            if (!buckets.TryGetValue(f, out b)) buckets[f] = b = new Bucket();
            bool expand;
            if (!expands.TryGetValue(f, out expand)) expands[f] = expand = ExpandsToRgba32(f);

            long runtime = 0;
            try { runtime = Profiler.GetRuntimeMemorySizeLong(t); } catch (System.Exception) { }
            long expanded = expand ? Rgba32Chain(t.width, t.height, t.mipmapCount) : 0;

            b.count++;
            b.pixels += (long)t.width * t.height;
            b.runtimeBytes += runtime;
            b.expandedBytes += expanded;
            if (t.mipmapCount > 1) b.withMips++;
            totalRuntime += runtime;
            totalExpanded += expanded;

            long rank = expanded > 0 ? expanded : runtime;
            if (rank > 0)
                largest.Add(new KeyValuePair<long, string>(rank,
                    $"{rank / 1048576.0:0.0}MiB {t.width}x{t.height} mips={t.mipmapCount} {f} readable={t.isReadable} \"{t.name}\""));
        }

        Debug.Log($"{Tag}snapshot {which + 1} at {Time.realtimeSinceStartup:0}s: {all.Length} Texture2D, " +
                  $"runtime={totalRuntime / 1048576.0:0.0}MiB (Profiler), " +
                  $"rgba32-expansion={totalExpanded / 1048576.0:0.0}MiB (formats this GPU cannot sample)");

        foreach (var pair in buckets)
        {
            var b = pair.Value;
            Debug.Log($"{Tag}  {pair.Key,-16} n={b.count,5} pixels={b.pixels / 1048576.0,8:0.0}M " +
                      $"runtime={b.runtimeBytes / 1048576.0,8:0.0}MiB expanded={b.expandedBytes / 1048576.0,8:0.0}MiB " +
                      $"mipped={b.withMips} {(expands[pair.Key] ? "EXPANDS" : "")}");
        }

        largest.Sort((a, c) => c.Key.CompareTo(a.Key));
        int n = Mathf.Min(TopN, largest.Count);
        for (int i = 0; i < n; i++)
            Debug.Log(Tag + "  top " + (i + 1) + ": " + largest[i].Value);
    }
}
#endif
