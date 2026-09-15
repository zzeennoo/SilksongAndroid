// LowMemoryProfile — one definition of the hardware boundary below which the
// port must favour survival over optional visual work.
//
// Android reports usable physical memory here, not the number printed on the
// box. The AYANEO Pocket Air Mini's advertised 3 GB is 2892 MB through Unity;
// ordinary 4 GB devices report comfortably above this boundary. Keep this a
// device property rather than a model allow-list so other 3 GB handhelds get
// the same protection.

#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine;

public static class LowMemoryProfile
{
    public const int MEMORY_LIMIT_MB = 3200;
    public const int MAX_FRAME_RATE = 60;
    public const int MAX_RENDER_SHORT_SIDE = 720;

    static int _memoryMb = int.MinValue;
    static bool _announced;

    public static int MemoryMb
    {
        get
        {
            if (_memoryMb == int.MinValue)
                _memoryMb = SystemInfo.systemMemorySize;
            return _memoryMb;
        }
    }

    public static bool Enabled => MemoryMb > 0 && MemoryMb <= MEMORY_LIMIT_MB;

    public static void Announce()
    {
        if (_announced || !Enabled) return;
        _announced = true;
        Debug.Log(
            $"[LowMemoryProfile] enabled: memory={MemoryMb}MB, " +
            $"shader warmup=off, frame cap={MAX_FRAME_RATE}, " +
            $"render short side<={MAX_RENDER_SHORT_SIDE}");
    }
}
#endif
