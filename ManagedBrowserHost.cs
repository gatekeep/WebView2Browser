// SPDX-License-Identifier: GPL-2.0-only
// Stream Service Manager integration. The browser remains an interactive application.
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace WebView2Browser;

internal sealed class ManagedBrowserHost
{
    private readonly Form form;
    private readonly WebView2 view;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 2000 };
    private readonly StreamWriter output = new(Console.OpenStandardOutput()) { AutoFlush = true };
    private bool checking, loaded;
    private string failure = "";
    private readonly ManagedBrowserAudio audio = new();

    public ManagedBrowserHost(Form form, WebView2 view)
    {
        this.form = form; this.view = view;
        view.CoreWebView2InitializationCompleted += Initialized;
        timer.Tick += async (_, _) => await Check();
        form.Shown += (_, _) => { timer.Start(); _ = Task.Run(ReadCommands); };
        form.FormClosed += (_, _) => { timer.Stop(); timer.Dispose(); output.Dispose(); };
    }
    private void Initialized(object? sender, CoreWebView2InitializationCompletedEventArgs e)
    {
        if (!e.IsSuccess) { failure = "WebView2 initialization failed. Check the runtime and profile folder."; return; }
        view.CoreWebView2.NavigationStarting += (_, _) => { loaded = false; failure = ""; };
        view.CoreWebView2.NavigationCompleted += (_, navigation) =>
        { loaded = navigation.IsSuccess; failure = navigation.IsSuccess ? "" : "Page navigation failed: " + navigation.WebErrorStatus; };
        view.CoreWebView2.ProcessFailed += (_, failureEvent) => { failure = "WebView2 process failed: " + failureEvent.ProcessFailedKind; };
    }
    private async Task ReadCommands()
    {
        using var input = new StreamReader(Console.OpenStandardInput());
        try
        {
            while (await input.ReadLineAsync() is { } command)
            {
                if (form.IsDisposed) return;
                form.BeginInvoke(() =>
                {
                    if (command == "close") form.Close();
                    else if (command == "reload") view.CoreWebView2?.Reload();
                    else if (command == "show") { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); }
                    else if (command.StartsWith("audio ", StringComparison.Ordinal))
                    {
                        try
                        {
                            var settings = JsonSerializer.Deserialize<ManagedAudioSettings>(command[6..]);
                            if (settings is { VolumePercent: >= 0 and <= 100 })
                            { audio.Desired = settings; audio.Apply(view.CoreWebView2); }
                        }
                        catch (JsonException) { /* Ignore malformed control messages. */ }
                    }
                });
            }
            if (!form.IsDisposed) form.BeginInvoke(() => form.Close());
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { /* The owning helper may have closed its pipe. */ }
    }
    private void Report(bool ready, bool playing, string state, string detail)
    {
        if (form.IsDisposed) return;
        try { output.WriteLine("SSM_STATUS " + JsonSerializer.Serialize(new { Ready = ready, Playing = playing, State = state, Detail = detail, AppliedAudio = audio.Applied, AudioControl = true, AudioDetail = audio.Detail })); }
        catch (IOException) { form.Close(); }
    }
    private async Task Check()
    {
        if (checking || form.IsDisposed) return;
        checking = true;
        try
        {
            audio.Apply(view.CoreWebView2);
            // A verification page may return HTTP 403 while still rendering normally.
            if (await VerificationRequired()) { Report(false, false, "Needs attention", "Complete site verification in the browser window. Automatic refresh is paused."); return; }
            if (failure.Length > 0) { Report(false, false, "Faulted", failure); return; }
            if (!loaded || view.CoreWebView2 == null) { Report(false, false, "Starting", "Loading browser page."); return; }
            var json = await view.CoreWebView2.ExecuteScriptAsync("(" + Probe + ")()");
            var status = JsonSerializer.Deserialize<Status>(json);
            if (status == null) Report(false, false, "Starting", "Waiting for page health.");
            else Report(status.Ready, status.Playing, status.State, status.Detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException or JsonException)
        { if (!form.IsDisposed) Report(false, false, "Faulted", "Browser page is not responding."); }
        finally { checking = false; }
    }
    public async Task<bool> VerificationRequired()
    {
        if (view.CoreWebView2 == null) return false;
        return await view.CoreWebView2.ExecuteScriptAsync("(" + VerificationProbe + ")()") == "true";
    }

    // Detect a blocking challenge only. Never interact with verification controls or clear cookies.
    internal const string VerificationProbe = """
    function() {
        if (document.querySelector('#challenge-running, #challenge-stage, #challenge-form') || window._cf_chl_opt) return true;
        const text = document.body?.innerText || '';
        const verifying = /performing security verification|verify you are (?:a )?human|verifying you are (?:a )?human/i.test(text);
        return verifying && (!!document.querySelector('iframe[src*="challenges.cloudflare.com"]') || /cloudflare/i.test(text) || /just a moment/i.test(document.title));
    }
    """;

    private sealed record Status(bool Ready, bool Playing, string State, string Detail);

    // Quiet call queues are healthy. Only active media with stalled progress is a playback failure.
    internal const string Probe = """
    function() {
        const result = (Ready, Playing, State, Detail) => ({Ready, Playing, State, Detail});
        if ([...document.querySelectorAll('input[type=password]')].some(e => e.getClientRects().length))
            return result(false, false, 'Sign in', 'Sign in using the browser window.');
        if (typeof BROADCASTIFY_MODE !== 'undefined' && BROADCASTIFY_MODE && !document.getElementById('call_audio_player'))
            return result(false, false, 'Sign in', 'Open the browser to sign in or select the player.');
        if (typeof currentDeviceSinkId === 'undefined' || !currentDeviceSinkId)
            return result(false, false, 'Starting', 'Waiting for the assigned audio output.');
        let media = [...document.querySelectorAll('audio,video')];
        if (typeof findAllShadowRoots === 'function') for (const root of findAllShadowRoots(document)) media.push(...root.querySelectorAll('audio,video'));
        if (typeof wvAudioPlayer !== 'undefined') media.push(wvAudioPlayer);
        const openMhz = typeof location !== 'undefined' && (location.hostname === 'openmhz.com' || location.hostname.endsWith('.openmhz.com'));
        if (openMhz) {
            const autoplay = [...document.querySelectorAll('.button-item')].find(e => e.textContent.trim() === 'Autoplay');
            if (!autoplay || !document.querySelector('.mediaplayer'))
                return result(false, false, 'Starting', 'Waiting for the OpenMHz player.');
            if (!autoplay.classList.contains('active'))
                return result(false, false, 'Sign in', 'Enable Autoplay in the OpenMHz window.');
            // Select the first (newest) filtered call once per document. Autoplay handles later calls.
            // Explicit archive/call links keep their requested starting point.
            if (!window.__ssmOpenMhzStarted) {
                const query = new URLSearchParams(location.search);
                if (query.has('call-id') || query.has('time')) window.__ssmOpenMhzStarted = true;
                else {
                    const newest = document.querySelector('#calls tbody tr[data-callid]');
                    if (newest) {
                        window.__ssmOpenMhzStarted = true;
                        if (!newest.classList.contains('positive')) {
                            newest.click();
                            return result(false, false, 'Starting', 'Starting newest OpenMHz recording.');
                        }
                    }
                }
            }
            // OpenMHz creates its media element when a call arrives, not when its queue loads.
            if (!media.length) return result(true, false, 'Ready', 'OpenMHz ready; waiting for calls.');
        }
        if (!media.length) return result(false, false, 'Sign in', 'Player unavailable. Open the browser to sign in or select the player.');
        if (media.some(e => e.sinkId !== currentDeviceSinkId)) {
            // Newly created WaveSurfer media may not exist when the page's routing hook first runs.
            if (openMhz && typeof SetAudioOutput === 'function') SetAudioOutput(currentDeviceSinkId);
            return result(false, false, 'Starting', 'Audio output routing is incomplete.');
        }
        if (typeof BROADCASTIFY_MODE !== 'undefined' && BROADCASTIFY_MODE && !window.__ssmStarted) {
            const start = document.getElementById('statebutton');
            if (start) { window.__ssmStarted = true; start.click(); }
        }
        window.__ssmProgress ??= new WeakMap();
        let playing = false;
        for (const element of media) {
            if (element.error) return result(false, false, 'Faulted', 'Audio playback failed (media error ' + element.error.code + ').');
            const active = !element.paused && !element.ended && !!element.currentSrc;
            const previous = window.__ssmProgress.get(element);
            if (!active || !previous || previous.time !== element.currentTime)
                window.__ssmProgress.set(element, {time: element.currentTime, changed: Date.now()});
            else if (Date.now() - previous.changed > 45000)
                return result(false, false, 'Faulted', 'Active audio stopped progressing.');
            playing ||= active;
        }
        return result(true, playing, 'Ready', playing ? 'Browser audio is playing.' : 'Browser ready; waiting for calls.');
    }
    """;
}
