using NAudio.CoreAudioApi;

namespace VTTranslate.Core.Audio;

/// <summary>Enumerates real Windows audio endpoints via WASAPI (NAudio.CoreAudioApi) — never fakes device lists.</summary>
public static class AudioDeviceCatalog
{
    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultId(enumerator, DataFlow.Capture);
        return enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, true, d.ID == defaultId, ClassifyDevice(d.FriendlyName)))
            .ToList();
    }

    /// <summary>
    /// Render (output/playback) devices — also used as the *source* for loopback capture
    /// of remote/system audio, since WASAPI loopback captures what a render device is playing.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = TryGetDefaultId(enumerator, DataFlow.Render);
        return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo(d.ID, d.FriendlyName, false, d.ID == defaultId, ClassifyDevice(d.FriendlyName)))
            .ToList();
    }

    private static string? TryGetDefaultId(MMDeviceEnumerator enumerator, DataFlow flow)
    {
        try
        {
            return enumerator.GetDefaultAudioEndpoint(flow, Role.Communications).ID;
        }
        catch
        {
            return null; // no default device configured — not fatal, just no default highlighted
        }
    }

    /// <summary>
    /// Best-effort classification from the device's friendly name. WASAPI endpoint IDs
    /// don't cleanly expose bus type through NAudio's public surface, so this is a
    /// heuristic (documented as such), not a hardware-ID query — good enough to help a
    /// user pick "the Bluetooth headset" vs. "the built-in mic" from a dropdown, not a
    /// guaranteed classification.
    /// </summary>
    private static AudioDeviceKind ClassifyDevice(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("cable") || n.Contains("virtual"))
            return AudioDeviceKind.Virtual;
        if (n.Contains("bluetooth") || n.Contains("hands-free") || n.Contains("hands free"))
            return AudioDeviceKind.Bluetooth;
        if (n.Contains("usb"))
            return AudioDeviceKind.Usb;
        return AudioDeviceKind.BuiltIn;
    }
}
