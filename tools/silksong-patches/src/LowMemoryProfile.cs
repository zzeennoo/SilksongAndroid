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
using UnityEngine.Rendering;

public static class LowMemoryProfile
{
    public const int MEMORY_LIMIT_MB = 3200;
    public const int MAX_FRAME_RATE_VULKAN = 30;
    public const int MAX_FRAME_RATE_GLES3 = 60;
    public const int MAX_RENDER_SHORT_SIDE = 540;
    public const int MIN_TEXTURE_MIP_LIMIT = 1;

    static int _memoryMb = int.MinValue;
    static bool _announced;
    static bool _graphicsApplied;

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
    public static bool IsOpenGles => SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3;
    public static int MaxFrameRate => IsOpenGles ? MAX_FRAME_RATE_GLES3 : MAX_FRAME_RATE_VULKAN;

    public static void Announce()
    {
        if (_announced || !Enabled) return;
        _announced = true;
        Debug.Log(
            $"[LowMemoryProfile] enabled: memory={MemoryMb}MB, " +
            $"graphics={SystemInfo.graphicsDeviceType}, shader warmup=off, frame cap={MaxFrameRate}, " +
            $"render short side<={MAX_RENDER_SHORT_SIDE}, " +
            $"texture mip limit>={MIN_TEXTURE_MIP_LIMIT}, AA=off");
    }

    /// <summary>
    /// Reduce allocations that live in the graphics driver rather than in the
    /// managed or native heaps reported by dumpsys meminfo.
    ///
    /// On the 3 GB AYANEO, native heap stayed near 48 MB while MemAvailable
    /// fell by 1.6 GB and CmaFree reached zero immediately before Android's
    /// low-memory killer removed the foreground game. Vulkan/ION allocations
    /// are substantially under-counted by per-process PSS on this device, so
    /// heap-only tuning cannot address that failure mode.
    /// </summary>
    public static void ApplyGraphicsLimits()
    {
        if (_graphicsApplied || !Enabled) return;
        _graphicsApplied = true;

        try
        {
            int oldMipLimit = QualitySettings.globalTextureMipmapLimit;
            int oldAa = QualitySettings.antiAliasing;

            // One mip level means half the width and height for mipmapped
            // textures, cutting their top-level allocation to one quarter.
            // Do not relax a stricter value the player or project already set.
            if (QualitySettings.globalTextureMipmapLimit < MIN_TEXTURE_MIP_LIMIT)
                QualitySettings.globalTextureMipmapLimit = MIN_TEXTURE_MIP_LIMIT;
            QualitySettings.antiAliasing = 0;
            QualitySettings.anisotropicFiltering = AnisotropicFiltering.Disable;

            Debug.Log(
                $"[LowMemoryProfile] graphics limits: texture mip limit " +
                $"{oldMipLimit}->{QualitySettings.globalTextureMipmapLimit}, " +
                $"AA {oldAa}->{QualitySettings.antiAliasing}, anisotropic=off");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning("[LowMemoryProfile] couldn't apply graphics limits: " + ex.Message);
        }
    }
}
#endif
