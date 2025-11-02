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

        private List<MediaDeviceInfo> audioOutputs = new List<MediaDeviceInfo>();
        private MediaDeviceInfo defaultMediaDevice = null;

        /*
        ** Methods
        */

        /// <summary>
        /// Initializes a new instance of the <see cref="MainForm"/> class.
        /// </summary>
        public MainForm()
        {
            InitializeComponent();

            // hook events
            this.Shown += MainForm_Shown;

            controlWebView2.CoreWebView2InitializationCompleted += WebView2_CoreWebView2InitializationCompleted;
        }

        /// <summary>
        /// 
        /// </summary>
        private async void EnumerateAudioDevices()
        {
            string script = @"
                async function selectAudioOutputAsync() {
                    try {
                        console.log(`EnumerateAudioDevices(): checkpoint 1`);

                        // request microphone permission to get a full device list
                        try {
                            const stream = await navigator.mediaDevices.getUserMedia({ audio: true });
                            stream.getTracks().forEach(track => track.stop());
                        } catch (err) {
                            console.warn('Audio permission denied, only default devices available:', err);
                        }

                        console.log(`EnumerateAudioDevices(): checkpoint 2`);

                        const devices = await navigator.mediaDevices.enumerateDevices();
                        if (devices == undefined || devices == null) {
                            console.error('Failure to enumerate audio devices!');
                        }

                        const audioOutputs = devices.filter(device => device.kind === 'audiooutput');
                        if (audioOutputs == undefined || audioOutputs == null) {
                            console.error('Failure to enumerate audio devices!');
                        }

                        if (audioOutputs.length == 0) {
                            console.error('No output audio devices!');
                        }

                        console.log(`EnumerateAudioDevices(): checkpoint 3`);

                        for (const device of audioOutputs) {
                            console.log(`EnumerateAudioDevices(): ${device.kind}: ${device.label} id = ${device.deviceId}`);
                        }

                        // send IPC message
                        window.chrome.webview.postMessage('INTERNAL_AUDIO_DEVICES_LIST:' + JSON.stringify(audioOutputs));

                        return true;
                    } catch (err) {
                        console.error(`EnumerateAudioDevices() failed: ${err}`);
                    }
                }
                selectAudioOutputAsync();
            ";

            await coreWebView2.ExecuteScriptAsync(script);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="deviceId"></param>
        private async void SetAudioOutput(string deviceId)
        {
            string script = $@"
                async function setAllMediaSinkId(sinkId) {{
                    const mediaElements = document.querySelectorAll('audio, video');
                    for (const element of mediaElements) {{
                        try {{
                            await element.setSinkId(sinkId);
                            console.log(`Set sinkId for element: ${{element.tagName}}`);
                        }} catch (error) {{
                            console.error(`Failed to set sinkId for element: ${{element.tagName}}`, error);
                        }}
                    }}

                    if (window.AudioContext) {{
                        const audioContext = new AudioContext();
                        try {{
                            await audioContext.setSinkId(sinkId);
                            console.log(`Set sinkId for AudioContext`);
                        }} catch (error) {{
                            console.error(`Failed to set sinkId for AudioContext`, error);
                        }}
                    }}
                }}

                setAllMediaSinkId('{deviceId}');
            ";

            await coreWebView2.ExecuteScriptAsync(script);
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
                this.coreWebView2 = controlWebView2.CoreWebView2;
                if (Program.ShowDevTools)
                    this.coreWebView2.OpenDevToolsWindow();

                // hook events
                coreWebView2.DocumentTitleChanged += CoreWebView2_DocumentTitleChanged;
                coreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoaded;
                coreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
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
            if (ipcMessage.StartsWith("INTERNAL_AUDIO_DEVICES_LIST:"))
            {
                string json = ipcMessage.Substring("INTERNAL_AUDIO_DEVICES_LIST:".Length);
                audioOutputs = JsonSerializer.Deserialize<List<MediaDeviceInfo>>(json);

                // determine the default device so we can fallback on that
                foreach (MediaDeviceInfo device in audioOutputs)
                    if (device.deviceId == "default")
                    {
                        defaultMediaDevice = device;
                        break;
                    }

                // lets try to set the audio output device on all media elements
                MediaDeviceInfo selectedDevice = defaultMediaDevice;
                foreach (MediaDeviceInfo device in audioOutputs)
                {
                    if ((device.label.ToLowerInvariant().Contains(Program.WaveOutDevice.ToLowerInvariant())) ||
                        device.deviceId.ToLowerInvariant() == Program.WaveOutDevice.ToLowerInvariant())
                    {
                        selectedDevice = device;
                        break;
                    }
                }

                SetAudioOutput(selectedDevice.deviceId);
            }
        }

        /// <summary>
        /// Evnet that occurs when the WebView2's DOM document content is loaded.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CoreWebView2_DOMContentLoaded(object? sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            EnumerateAudioDevices();
        }

        /// <summary>
        /// Event that occurs when WebView2's document title changes.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void CoreWebView2_DocumentTitleChanged(object? sender, object e)
        {
            Invoke(() => this.Text = this.controlWebView2.CoreWebView2.DocumentTitle);
        }

        /// <summary>
        /// Event that occurs when the WinForms form is shown.
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void MainForm_Shown(object? sender, EventArgs e)
        {
            controlWebView2.Source = new Uri(Program.StartupUrl);

            if (Program.AutoRefresh)
            {
                DateTime now = DateTime.Now;
                DateTime nextRunTime = now.Date.AddDays(1).AddHours(3);
                if (now > nextRunTime)
                    nextRunTime = nextRunTime.AddDays(1);

                TimeSpan timeUntilFirstRun = nextRunTime - now;

                refreshTimer = new System.Timers.Timer(timeUntilFirstRun.TotalMilliseconds);
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
            Invoke(() => this.coreWebView2.Reload());

            refreshTimer.Interval = TimeSpan.FromDays(1).TotalMilliseconds;
            refreshTimer.AutoReset = true;
            refreshTimer.Start();
        }
    }

    /// <summary>
    /// JSON MediaDeviceInfo
    /// </summary>
    public class MediaDeviceInfo
    {
        [JsonPropertyName("deviceId")]
        public string deviceId { get; set; }
        [JsonPropertyName("kind")]
        public string kind { get; set; }
        [JsonPropertyName("label")]
        public string label { get; set; }
        [JsonPropertyName("groupId")]
        public string groupId { get; set; }
    }
}
