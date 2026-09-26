// SPDX-License-Identifier: GPL-2.0-only
/**
* GPL2 Open Source. Use is subject to license terms.
* DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
*/
/*
*   Copyright (C) 2025 by Bryan Biedenkapp N2PLL
*
*   This program is free software: you can redistribute it and/or modify
*   it under the terms of the GNU Affero General Public License as published by
*   the Free Software Foundation, either version 2 of the License, or
*   (at your option) any later version.
*
*   This program is distributed in the hope that it will be useful,
*   but WITHOUT ANY WARRANTY; without even the implied warranty of
*   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
*   GNU Affero General Public License for more details.
*/

using System;
using System.Timers;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Web.WebView2;
using Microsoft.Web.WebView2.Core;

namespace WebView2Browser
{
    /// <summary>
    /// 
    /// </summary>
    public partial class MainForm : Form
    {
        private System.Timers.Timer refreshTimer;
        private CoreWebView2 coreWebView2;

        private int defermentCount = 0;

        private int numberOfPageLoads = 0;
        private DateTime lastPageLoad = DateTime.Now;

        private bool devicesEnumerated = false;
        private ManagedBrowserHost? managerHost;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="MainForm"/> class.
        /// </summary>
        public MainForm()
        {
            InitializeComponent();
            InitializeWebView2Async();

            // hook events
            this.Shown += MainForm_Shown;

            controlWebView2.CoreWebView2InitializationCompleted += WebView2_CoreWebView2InitializationCompleted;
            if (Program.ManagerMode) managerHost = new ManagedBrowserHost(this, controlWebView2);
        }

        /// <summary>
        /// 
        /// </summary>
        private async void InitializeWebView2Async()
        {
            var options = new CoreWebView2EnvironmentOptions("--disable-web-security --allow-insecure-localhost --unsafely-treat-insecure-origin-as-secure --enable-media-stream=1 --autoplay-policy=no-user-gesture-required");
            string userDataFolder = Path.Combine(Application.StartupPath, Program.UserProfilePath);

            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);

            await controlWebView2.EnsureCoreWebView2Async(environment);
        }

        /// <summary>
        /// 
        /// </summary>
        private async void AudioPlaybackWatchdog()
        {
            string script = @"
                console.log(`AudioPlaybackWatchdog(): checkpoint 1`);

                var lastPlayedTime = Date.now();
                var inactivityWarning = false;
                const inactivityThreshold = 300000;

                let mediaElements = Array.from(document.querySelectorAll('audio'));
                if (mediaElements == undefined) {
                    console.error('AudioPlaybackWatchdog(): no DOM audio elements found on page!');
                    mediaElements = [];
                }

                const openShadowRoots = findAllShadowRoots(document);
                for (const shadowRoot of openShadowRoots) {
                    const shadowMediaElements = shadowRoot.querySelectorAll('audio');
                    if (shadowMediaElements != undefined && shadowMediaElements.length > 0) {
                        console.log(shadowMediaElements.length + ' audio elements found in shadow root, adding to watchdog');
                        for (const element of shadowMediaElements) {
                            mediaElements.push(element);
                        }
                    }
                }

                if (mediaElements.length == 0) {
                    console.error('AudioPlaybackWatchdog(): no audio elements found on page!');
                    return;
                }

                console.log(`AudioPlaybackWatchdog(): monitoring ${mediaElements.length} audio elements for playback inactivity`);

                for (const element of mediaElements) {
                    element.addEventListener('timeupdate', () => {
                        lastPlayedTime = Date.now();
                        inactivityWarning = false;
                    });

                    element.addEventListener('play', () => {
                        if (BROADCASTIFY_MODE && AUDIO_HAS_CHANGED) {
                            try {
                                console.log(`AudioPlaybackWatchdog(): stopping current audio playback after a source change`);
                                element.pause();
                            } catch (err) { 
                                console.error('AudioPlaybackWatchdog(): Error pausing audio after src change:', err);
                            }

                            SetAudioOutput(currentDeviceSinkId);

                            try {
                                console.log(`AudioPlaybackWatchdog(): resuming current audio playback after a source change`);
                                element.play().catch(err => console.error('Playback failed', err));
                            } catch (err) { 
                                console.error('AudioPlaybackWatchdog(): Error resuming audio after src change:', err);
                            }

                            AUDIO_HAS_CHANGED = false;
                        }

                        // send IPC message
                        window.chrome.webview.postMessage('INTERNAL_RESET_AUDIO_DEVICE');

                        lastPlayedTime = Date.now();
                        inactivityWarning = false;
                        console.log(`AudioPlaybackWatchdog(): active audio playback at ${lastPlayedTime}`);
                    });

                    element.addEventListener('pause', () => {
                        console.log(`AudioPlaybackWatchdog(): audio playback has stopped`);

                        // send IPC message
                        window.chrome.webview.postMessage('INTERNAL_RESET_AUDIO_DEVICE');
                    });

                    setInterval(() => {
                        const now = Date.now();
                        if (now - lastPlayedTime > (inactivityThreshold / 2) && !inactivityWarning) {
                            inactivityWarning = true;
                            console.log(`AudioPlaybackWatchdog(): warning, last audio playback at ${lastPlayedTime}, will refresh in ${inactivityThreshold / 2}s`);
                        }

                        if (now - lastPlayedTime > inactivityThreshold) {
                            // send IPC message
                            window.chrome.webview.postMessage('INTERNAL_FORCE_REFRESH');
                        }
                    }, 1000); // Check every second
                }
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async Task CreateGlobalFunctions()
        {
            string script = @"
                var localDeviceLabel = 'default';
                var currentDeviceSinkId = null;

                function findAllShadowRoots(node) {
                    const allElements = Array.from(node.querySelectorAll('*'));
                    let shadowRoots = [];

                    allElements.forEach(element => {
                        if (element.shadowRoot && element.shadowRoot.mode === 'open') {
                            shadowRoots.push(element.shadowRoot);
                            shadowRoots = shadowRoots.concat(findAllShadowRoots(element.shadowRoot));
                        }
                    });

                    return shadowRoots;
                }

                async function SetDeviceLabel(label) {
                    localDeviceLabel = label.toLowerCase();
                    console.log(`SetDeviceLabel(): setting device label to ${localDeviceLabel}`);
                }

                async function EnumerateAudioDevices() {
                    try {
                        console.log(`EnumerateAudioDevices(): request microphone permissions`);

                        var sinkId = null;

                        // request microphone permission to get a full device list
                        try {
                            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                            stream.getTracks().forEach(track => track.stop());
                        } catch (err) {
                            console.warn('EnumerateAudioDevices(): audio permission denied, only default devices available:', err);
                        }

                        console.log(`EnumerateAudioDevices(): enumerate system devices`);

                        const devices = await navigator.mediaDevices.enumerateDevices();
                        if (devices == undefined || devices == null) {
                            console.error('Failure to enumerate audio devices!');
                            return false;
                        }

                        const audioOutputs = devices.filter(device => device.kind === 'audiooutput');
                        if (audioOutputs == undefined || audioOutputs == null) {
                            console.error('EnumerateAudioDevices(): failure to enumerate audio devices!');
                            return false;
                        }

                        if (audioOutputs.length == 0) {
                            console.error('EnumerateAudioDevices(): no output audio devices!');
                            return false;
                        }

                        console.log(`EnumerateAudioDevices(): process device list, ${audioOutputs.length} devices`);
                        for (const device of audioOutputs) {
                            const deviceLabel = device.label.toLowerCase();
                            const deviceId = device.deviceId.toLowerCase();
                            console.debug(`EnumerateAudioDevices(): device ${deviceLabel}, deviceId ${device.deviceId}`);
                            if (SSM_MANAGED_MODE ? (deviceLabel === localDeviceLabel || deviceId === localDeviceLabel) : (deviceLabel.includes(localDeviceLabel) || deviceId.includes(localDeviceLabel))) {
                                console.log(`EnumerateAudioDevices(): found device ${localDeviceLabel}, deviceId ${device.deviceId}, setting audio output`);
                                sinkId = device.deviceId;
                                break;
                            }
                        }

                        if (sinkId == null) {
                            console.error(`EnumerateAudioDevices(): failed to find device ${localDeviceLabel}!!!`);
                            return false;
                        }

                        if (sinkId != currentDeviceSinkId) {
                            await SetAudioOutput(sinkId);
                        } else {
                            console.log(`EnumerateAudioDevices(): current deviceId already set; skipped setting`);
                        }

                        return true;
                    } catch (err) {
                        console.error(`EnumerateAudioDevices() failed: ${err}`);
                    }
                }

                async function SetAudioOutput(sinkId) {
                    if (sinkId == null) {
                        console.error(`SetAudioOutput(): device sink Id was not set!`);
                        return;
                    }

                    currentDeviceSinkId = sinkId;

                    console.log(`SetAudioOutput(): setting element tags to deviceId ${sinkId}`);

                    const mediaElements = document.querySelectorAll('audio, video');
                    for (const element of mediaElements) {
                        try {
                            console.log(`SetAudioOutput(): sinkId for element: ${element.tagName} ${element.id} ${element.sinkId}`);
                            await element.setSinkId(sinkId);
                            console.log(`SetAudioOutput(): set sinkId for element: ${element.tagName} ${element.id} ${element.sinkId}`);
                        } catch (error) {
                            console.error(`SetAudioOutput(): failed to set sinkId for element: ${element.tagName} ${element.id}`, error);
                        }
                    }

                    console.log(`SetAudioOutput(): setting shadow root element tags to deviceId ${sinkId}`);

                    const openShadowRoots = findAllShadowRoots(document);
                    for (const shadowRoot of openShadowRoots) {
                        const shadowMediaElements = shadowRoot.querySelectorAll('audio, video');
                        for (const element of shadowMediaElements) {
                            try {
                                await element.setSinkId(sinkId);
                                console.log(`SetAudioOutput(): set sinkId for shadow element: ${element.tagName} ${element.id} ${element.sinkId}`);
                            } catch (error) {
                                console.error(`SetAudioOutput(): failed to set sinkId for shadow element: ${element.tagName} ${element.id}`, error);
                            }
                        }
                    }

                    console.log(`SetAudioOutput(): setting window context to deviceId ${sinkId}`);

                    if (window.AudioContext) {
                        const audioContext = new (window.AudioContext || window.webkitAudioContext)();
                        try {
                            await audioContext.setSinkId(sinkId);
                            console.log(`SetAudioOutput(): set sinkId for AudioContext ${audioContext.sinkId}`);
                        } catch (error) {
                            console.error(`SetAudioOutput(): failed to set sinkId for AudioContext`, error);
                        }
                    }

                    if (BROADCASTIFY_MODE && wvAudioPlayer) {
                        try {
                            if (wvAudioPlayer.sinkId !== currentDeviceSinkId) {
                                console.log(`SetAudioOutput(): sinkId for element: ${wvAudioPlayer.tagName} ${wvAudioPlayer.id} ${wvAudioPlayer.sinkId}`);
                                await wvAudioPlayer.setSinkId(currentDeviceSinkId);
                                console.log(`SetAudioOutput(): set sinkId for element: ${wvAudioPlayer.tagName} ${wvAudioPlayer.id} ${wvAudioPlayer.sinkId}`);
                            }
                        } catch (error) {
                            console.error(`SetAudioOutput(): failed to set sinkId for element: ${wvAudioPlayer.tagName} ${wvAudioPlayer.id}`, error);
                        }
                    }
                }

                function IsMediaPlaying() {
                    var playing = false;
                    const mediaElements = document.querySelectorAll('audio, video');
                    for (const element of mediaElements) {
                        if (!element.paused) {
                            playing = true;
                            break;
                        }
                    }

                    if (!playing) {
                        const openShadowRoots = findAllShadowRoots(document);
                        for (const shadowRoot of openShadowRoots) {
                            const shadowMediaElements = shadowRoot.querySelectorAll('audio, video');
                            for (const element of shadowMediaElements) {
                                if (!element.paused) {
                                    playing = true;
                                    break;
                                }
                            }

                            if (playing)
                                break;
                        }
                    }

                    return playing;
                }
            ";

            await coreWebView2.ExecuteScriptAsync("var SSM_MANAGED_MODE = " + (Program.ManagerMode ? "true;" : "false;") + script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async Task EnumerateAudioDevices()
        {
            string script = @"EnumerateAudioDevices();";
            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="deviceLabel"></param>
        private async Task SetDeviceLabel(string deviceLabel)
        {
            string script = "SetDeviceLabel(" + System.Text.Json.JsonSerializer.Serialize(deviceLabel) + ");";
            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async void ForcePageScrollback()
        {
            string script = @"
                console.log(`ForcePageScrollback(): forcing page scrollback`);
                window.scroll(0,0);
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async void JavascriptGC()
        {
            string script = @"
                console.log(`JavascriptGC(): forcing javascript garbage collection`);
                window.gc();
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async void CheckAndHookShadowRoot()
        {
            string script = @"
                console.log(`CheckAndHookShadowRoot(): checking for shadow DOM roots to hook`);
                const openShadowRoots = findAllShadowRoots(document);
                for (const shadowRoot of openShadowRoots) {
                    const shadowHost = shadowRoot.host;
                    console.log(`CheckAndHookShadowRoot(): shadow root host element: ${shadowHost.tagName}`);

                    const shadowHostParent = shadowHost.parentElement.parentElement;
                    if (shadowHostParent) {
                        console.log(`CheckAndHookShadowRoot(): shadow root host parent element: ${shadowHostParent.tagName} ${shadowHostParent.class}`);

                        const observer = new MutationObserver(function(mutations) {
                            mutations.forEach(function(mutation) {
                                console.log(`CheckAndHookShadowRoot(): mutation observed in shadow root: ${mutation.type}`);

                                if (mutation.type === 'childList') {
                                    SetAudioOutput(currentDeviceSinkId);
                                }
                            });
                        });

                        observer.observe(shadowHostParent, { attributes: true, childList: true, subtree: true, characterData: true });
                    }
                }
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async Task HookAudioObserver()
        {
            string script = @"
                const BROADCASTIFY_MODE = true;

                console.log(`HookAudioObserver(): BROADCASTIFY_MODE`);
                console.log(`HookAudioObserver(): creating shadow audio tag`);
                const wvAudioPlayer = document.createElement('audio');
                wvAudioPlayer.id = 'shadow-audio-player';

                console.log(`HookAudioObserver(): hooking audio tag for changes`);

                const audioElement = document.getElementById('call_audio_player');
                if (audioElement) {
                audioElement.addEventListener('play', () => { audioElement.pause(); audioElement.currentTime = 0; });

                const observer = new MutationObserver(function(mutations) {
                    mutations.forEach(function(mutation) {
                        console.log(`HookAudioObserver(): mutation observed in audio tag: ${mutation.type}`);

                        if (mutation.type === 'attributes' && mutation.attributeName === 'src') {
                            const newSrc = audioElement.getAttribute('src');
                            console.log(`HookAudioObserver(): audio source changed ${newSrc}`);
                            SetAudioOutput(currentDeviceSinkId);
                            wvAudioPlayer.src = newSrc;
                            try {
                                wvAudioPlayer.play();
                            } catch (error) {
                                console.error(`HookAudioObserver(): shadow-audio-player play() interrupt`, error);
                            }
                        }
                    });
                });

                observer.observe(audioElement, { attributes: true });
                if (!SSM_MANAGED_MODE) document.getElementById('statebutton')?.click();
                }
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async Task DisableBroadcastify()
        {
            string script = @"
                const BROADCASTIFY_MODE = false;
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <returns></returns>
        private async Task<string> IsAudioPlaying()
        {
            string script = @"IsMediaPlaying();";
            return await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private void TriggerPageRefresh()
        {
            if (Program.DisableRefresh)
                return;

            // clear cache and refresh
            Invoke(async () =>
            {
                JavascriptGC();
                coreWebView2.Reload();

                // force scroll back to top
                ForcePageScrollback();
            });
        }

        /// <summary>
        /// Event that occurs once the WebView2 initialization is completed.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void WebView2_CoreWebView2InitializationCompleted(object? sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            if (e.IsSuccess)
            {
                coreWebView2 = controlWebView2.CoreWebView2;
                if (Program.ShowDevTools)
                    coreWebView2.OpenDevToolsWindow();

                coreWebView2.Settings.IsPasswordAutosaveEnabled = true;

                // hook events
                coreWebView2.PermissionRequested += CoreWebView2_PermissionRequested;
                coreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                coreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoaded;
                coreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
            }
        }

        /// <summary>
        /// Event that occurs when the web page asks for media permission.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        /// <exception cref="NotImplementedException"></exception>
        private void CoreWebView2_PermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            // always allow media permissions
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone ||
                e.PermissionKind == CoreWebView2PermissionKind.Camera)
            {
                e.State = CoreWebView2PermissionState.Allow;
                e.Handled = true;
            }
        }

        /// <summary>
        /// Event that occurs when a web message is recieved.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string ipcMessage = e.TryGetWebMessageAsString();
            if (ipcMessage.StartsWith("INTERNAL_FORCE_REFRESH"))
                TriggerPageRefresh();
            else if (ipcMessage.StartsWith("INTERNAL_RESET_AUDIO_DEVICE"))
                await EnumerateAudioDevices();
        }

        /// <summary>
        /// Evnet that occurs when the WebView2's DOM document content is loaded.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private async void CoreWebView2_DOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            await CreateGlobalFunctions();

            if (Program.BroadcastifyMode)
                await HookAudioObserver();
            else
                await DisableBroadcastify();

            if (Program.WaveOutDevice != "default")
            {
                await SetDeviceLabel(Program.WaveOutDevice);
                await EnumerateAudioDevices();
                CheckAndHookShadowRoot();
            }

            if (!Program.BroadcastifyMode)
                ForcePageScrollback();
            JavascriptGC();

            if (Program.WaveOutDevice != "default" && !Program.ManagerMode)
                AudioPlaybackWatchdog();

            numberOfPageLoads++;
            lastPageLoad = DateTime.Now;

            Invoke(() => this.Text = $"{controlWebView2.CoreWebView2.DocumentTitle} (loaded on {lastPageLoad.ToString()}, {numberOfPageLoads} loads)");
        }

        /// <summary>
        /// Event that occurs when WebView2's document title changes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
        {
            Invoke(() => this.Text = $"{controlWebView2.CoreWebView2.DocumentTitle} (loaded on {lastPageLoad.ToString()}, {numberOfPageLoads} loads)");
        }

        /// <summary>
        /// Event that occurs when the WinForms form is shown.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_Shown(object? sender, EventArgs e)
        {
            controlWebView2.Source = new Uri(Program.StartupUrl);

            if (Program.AutoRefresh > 0)
            {
                refreshTimer = new System.Timers.Timer(TimeSpan.FromMinutes(Program.AutoRefresh).TotalMilliseconds);
                refreshTimer.Elapsed += OnRefreshTimedEvent;
                refreshTimer.AutoReset = false;
                refreshTimer.Start();
            }
        }

        /// <summary>
        /// Event that occurs when the refresh timer expires.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="e"></param>
        private void OnRefreshTimedEvent(object source, ElapsedEventArgs e)
        {
            if (Program.DisableRefresh)
                return;

            // clear cache and refresh
            Invoke(async () => {
                string isPlaying = await IsAudioPlaying();
                if (isPlaying == "true" && defermentCount < Program.MaxDeferredBeforeRefresh)
                {
                    refreshTimer.Interval = TimeSpan.FromSeconds(30).TotalMilliseconds;
                    refreshTimer.AutoReset = true;
                    refreshTimer.Start();

                    await coreWebView2.ExecuteScriptAsync(@"console.log(`OnRefreshTimedEvent(): audio active -- skipping refresh for 30 seconds`)");
                    defermentCount++;
                    return;
                }

                refreshTimer.Interval = TimeSpan.FromMinutes(Program.AutoRefresh).TotalMilliseconds;
                refreshTimer.AutoReset = true;
                refreshTimer.Start();

                numberOfPageLoads = 0;
                lastPageLoad = DateTime.Now;

                defermentCount = 0;
                
                //await coreWebView2.Profile.ClearBrowsingDataAsync();
                TriggerPageRefresh();
            });
        }
    }
}
