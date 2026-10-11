using NAudio.CoreAudioApi;

namespace LANDock.Ultrasound;

/// <summary>The microphones and speakers of this PC, and which one the app uses.</summary>
public static class AudioDevices
{
    public sealed record Device(string Id, string Name);

    /// <summary>Words that mark a virtual output (it does not reach the air, so it cannot carry a sound to a microphone).</summary>
    private static readonly string[] VirtualWords = { "virtual", "steam", "cable", "stream", "discord", "voicemeeter", "vb-audio", "hdmi" };

    public static List<Device> Inputs() => Enumerate(DataFlow.Capture);

    public static List<Device> Outputs() => Enumerate(DataFlow.Render);

    private static List<Device> Enumerate(DataFlow flow)
    {
        try
        {
            return new MMDeviceEnumerator().EnumerateAudioEndPoints(flow, DeviceState.Active)
                .Select(d => new Device(d.ID, d.FriendlyName)).ToList();
        }
        catch { return new List<Device>(); }
    }

    /// <summary>The microphone with this id, or the default one when the id is empty or no longer connected.</summary>
    public static MMDevice Input(string? id)
    {
        var en = new MMDeviceEnumerator();
        if (!string.IsNullOrEmpty(id))
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                if (d.ID == id) return d;
        return en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
    }

    /// <summary>The speakers with this id. With no id: a physical output (never a virtual one, which does not reach the air).</summary>
    public static MMDevice Output(string? id)
    {
        var en = new MMDeviceEnumerator();
        var outputs = en.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (!string.IsNullOrEmpty(id))
        {
            var chosen = outputs.FirstOrDefault(d => d.ID == id);
            if (chosen != null) return chosen;
        }
        var physical = outputs.FirstOrDefault(d =>
            !VirtualWords.Any(w => d.FriendlyName.Contains(w, StringComparison.OrdinalIgnoreCase)));
        return physical ?? en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
    }

    /// <summary>The name of a device id, or a note when it is not connected (for the settings).</summary>
    public static string NameOf(string id, List<Device> list) =>
        list.FirstOrDefault(d => d.Id == id)?.Name ?? (id == "" ? "automatic" : "(not connected)");
}
