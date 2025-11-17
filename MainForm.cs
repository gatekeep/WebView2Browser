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
                var lastPlayedTime = Date.now();
                var inactivtyWarning = false;
                const inactivityThreshold = 300000;

                const mediaElements = document.querySelectorAll('audio');
                for (const element of mediaElements) {
                    element.addEventListener('timeupdate', () => {
                        lastPlayedTime = Date.now();
                        inactivityWarning = false;
                    });

                    element.addEventListener('play', () => {
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
        private async void CreateGlobalFunctions()
        {
            string script = @"
                var localDeviceLabel = 'default';
                var currentDeviceSinkId = null;

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
                            if (deviceLabel.includes(localDeviceLabel) || deviceId.includes(localDeviceLabel)) {
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
                            await element.setSinkId(sinkId);
                            console.log(`SetAudioOutput(): set sinkId for element: ${element.tagName}`);
                        } catch (error) {
                            console.error(`SetAudioOutput(): failed to set sinkId for element: ${element.tagName}`, error);
                        }
                    }

                    console.log(`SetAudioOutput(): setting window context to deviceId ${sinkId}`);

                    if (window.AudioContext) {
                        const audioContext = new AudioContext();
                        try {
                            await audioContext.setSinkId(sinkId);
                            console.log(`SetAudioOutput(): set sinkId for AudioContext`);
                        } catch (error) {
                            console.error(`SetAudioOutput(): failed to set sinkId for AudioContext`, error);
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

                    return playing;
                }
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        private async void EnumerateAudioDevices()
        {
            string script = @"EnumerateAudioDevices();";
            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="deviceLabel"></param>
        private async void SetDeviceLabel(string deviceLabel)
        {
            string script = $@"SetDeviceLabel('{deviceLabel}');";
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
            if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
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
        private void CoreWebView2_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string ipcMessage = e.TryGetWebMessageAsString();
            if (ipcMessage.StartsWith("INTERNAL_FORCE_REFRESH"))
                TriggerPageRefresh();
            else if (ipcMessage.StartsWith("INTERNAL_RESET_AUDIO_DEVICE"))
                EnumerateAudioDevices();
        }

        /// <summary>
        /// Evnet that occurs when the WebView2's DOM document content is loaded.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CoreWebView2_DOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            CreateGlobalFunctions();

            if (Program.WaveOutDevice != "default")
            {
                SetDeviceLabel(Program.WaveOutDevice);
                EnumerateAudioDevices();
            }
            
            ForcePageScrollback();
            JavascriptGC();

            if (Program.WaveOutDevice != "default")
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
            // clear cache and refresh
            Invoke(async () => {
                string isPlaying = await IsAudioPlaying();
                if (isPlaying == "true" && defermentCount < Program.MaxDeferredBeforeRefresh)
                {
                    refreshTimer.Interval = TimeSpan.FromSeconds(15).TotalMilliseconds;
                    refreshTimer.AutoReset = true;
                    refreshTimer.Start();

                    await coreWebView2.ExecuteScriptAsync(@"console.log(`OnRefreshTimedEvent(): audio active -- skipping refresh for 15 seconds`)");
                    defermentCount++;
                    return;
                }

                refreshTimer.Interval = TimeSpan.FromMinutes(Program.AutoRefresh).TotalMilliseconds;
                refreshTimer.AutoReset = true;
                refreshTimer.Start();

                numberOfPageLoads = 0;
                lastPageLoad = DateTime.Now;

                defermentCount = 0;
                
                await coreWebView2.Profile.ClearBrowsingDataAsync();
                TriggerPageRefresh();
            });
        }
    }
}
