// SPDX-License-Identifier: GPL-2.0-only
// Per-profile volume. Never change a VAC endpoint's master level or another app's session.
using Microsoft.Web.WebView2.Core;
using NAudio.CoreAudioApi;

namespace WebView2Browser;

internal sealed record ManagedAudioSettings(int VolumePercent = 100, bool Muted = false);

internal sealed class ManagedBrowserAudio
{
    public ManagedAudioSettings? Desired { get; set; }
    public ManagedAudioSettings? Applied { get; private set; }
    public string Detail { get; private set; } = "Waiting for audio settings.";

    public void Apply(CoreWebView2? core)
    {
        Applied = null;
        if (core == null || Desired == null) return;
        try
        {
            core.IsMuted = Desired.Muted;
            var owners = core.Environment.GetProcessInfos().Select(p => (uint)p.ProcessId).ToHashSet();
            var found = false;
            using var devices = new MMDeviceEnumerator();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    var sessions = device.AudioSessionManager.Sessions;
                    for (var i = 0; i < sessions.Count; i++)
                    {
                        using var session = sessions[i];
                        if (session.IsSystemSoundsSession || !owners.Contains(session.GetProcessID)) continue;
                        var volume = session.SimpleAudioVolume;
                        var level = Desired.VolumePercent / 100f;
                        if (Math.Abs(volume.Volume - level) > 0.001f) volume.Volume = level;
                        if (volume.Mute != Desired.Muted) volume.Mute = Desired.Muted;
                        if (Math.Abs(volume.Volume - level) > 0.001f || volume.Mute != Desired.Muted)
                            throw new InvalidOperationException("Audio level was not applied.");
                        found = true;
                    }
                }
            }
            if (found) Applied = Desired;
            Detail = found ? "Applied live." : "Waiting for an audio session.";
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        { Detail = "Audio control unavailable. Check Windows audio devices."; }
    }
}
