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

using System.Reflection;
using System.Runtime.InteropServices;

using NAudio.Wave;

namespace WebView2Browser
{
    /// <summary>
    /// 
    /// </summary>
    public enum ERRNO : int
    {
        /// <summary>
        /// No error
        /// </summary>
        ENOERR = 0,
        /// <summary>
        /// Bad commandline options
        /// </summary>
        EBADOPTIONS = 1,
        /// <summary>
        /// Missing configuration file
        /// </summary>
        ENOCONFIG = 2
    }

    /// <summary>
    /// 
    /// </summary>
    public static class ConsoleHelper
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(uint dwProcessId);

        private const uint ATTACH_PARENT_PROCESS = 0x0ffffffff; // Attach to the parent process's console

        /*
        ** Methods
        */

        /// <summary>
        /// 
        /// </summary>
        public static void AttachParentConsole()
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// Gets or sets the audio wave in device index.
        /// </summary>
        public static string WaveOutDevice
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the startup URL.
        /// </summary>
        public static string StartupUrl
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets a flag indicating the dev tool should be shown.
        /// </summary>
        public static bool ShowDevTools
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the number of minutes before refreshing the browser.
        /// </summary>
        public static int AutoRefresh
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the maximum number of refresh deferments before forcing refresh.
        /// </summary>
        public static int MaxDeferredBeforeRefresh
        {
            get;
            set;
        }

        /*
        ** Methods
        */

        /// <summary>
        /// Internal helper to prints the program usage.
        /// </summary>
        private static void Usage(OptionSet p)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            string fileName = Path.GetFileName(assembly.Location);

            Console.WriteLine(string.Format("usage: {0} [-o <wave out device no.>]",
            Path.GetFileNameWithoutExtension(fileName)));
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);

            Console.WriteLine("\nAudio Output Devices:");
            int waveOutDevices = WaveOut.DeviceCount;
            for (int waveOutDevice = 0; waveOutDevice < waveOutDevices; waveOutDevice++)
            {
                WaveOutCapabilities deviceInfo = WaveOut.GetCapabilities(waveOutDevice);
                Console.WriteLine($"\t{waveOutDevice}\t- {deviceInfo.ProductName}");
            }
        }

        /// <summary>
        ///  The main entry point for the application.
        /// </summary>
        /// <param name="args"></param>
        [STAThread]
        static void Main(string[] args)
        {
            bool showHelp = false;

            WaveOutDevice = "default";
            string autoRefreshString = string.Empty, maxDeferredRefreshString = string.Empty;
            AutoRefresh = 0;
            MaxDeferredBeforeRefresh = 5;

            ConsoleHelper.AttachParentConsole();

            // command line parameters
            OptionSet options = new OptionSet()
            {
                { "h|help", "show this message and exit", v => showHelp = v != null },
                { "d", "show browser DevTools", v => ShowDevTools = v != null },

                { "o=|output-device=", "audio output device", v => WaveOutDevice = v },

                { "auto-refresh=", "automatically refresh browser every number of specified minutes", v => autoRefreshString = v },
                { "force-refresh-after=", "force refresh browser after specified number of deferments", v => maxDeferredRefreshString = v },

                { "url=", "startup URL", v => StartupUrl = v },
            };

            // attempt to parse the commandline
            try
            {
                options.Parse(args);
            }
            catch (OptionException)
            {
                Console.WriteLine("error: invalid arguments");
                Usage(options);
                Environment.Exit((int)ERRNO.EBADOPTIONS);
            }

            // show help?
            if (showHelp)
            {
                Usage(options);
                Environment.Exit((int)ERRNO.ENOERR);
            }

            // determine which audio output device we're using
            if (WaveOutDevice == null)
            {
                Console.WriteLine("error: no output audio device specified!");
                Usage(options);
                Environment.Exit((int)ERRNO.EBADOPTIONS);
            }

            if (WaveOutDevice == string.Empty)
            {
                Console.WriteLine("error: no output audio device specified!");
                Usage(options);
                Environment.Exit((int)ERRNO.EBADOPTIONS);
            }

            if (autoRefreshString == null)
                AutoRefresh = 0;
            if (autoRefreshString == string.Empty)
                AutoRefresh = 0;

            if (autoRefreshString != null)
            {
                if (autoRefreshString != string.Empty)
                {
                    int refresh = -1;
                    if (int.TryParse(autoRefreshString, out refresh))
                        AutoRefresh = refresh;
                    else
                    {
                        Console.WriteLine($"error: could not process {autoRefreshString} as a refresh time!");
                        Usage(options);
                        Environment.Exit((int)ERRNO.EBADOPTIONS);
                    }
                }
            }

            if (maxDeferredRefreshString == null)
                MaxDeferredBeforeRefresh = 5;
            if (maxDeferredRefreshString == string.Empty)
                MaxDeferredBeforeRefresh = 5;

            if (maxDeferredRefreshString != null)
            {
                if (maxDeferredRefreshString != string.Empty)
                {
                    int cnt = -1;
                    if (int.TryParse(maxDeferredRefreshString, out cnt))
                        MaxDeferredBeforeRefresh = cnt;
                    else
                    {
                        Console.WriteLine($"error: could not process {maxDeferredRefreshString} as a deferment count!");
                        Usage(options);
                        Environment.Exit((int)ERRNO.EBADOPTIONS);
                    }
                }
            }

            if (StartupUrl == string.Empty)
            {
                Console.WriteLine("error: no startup URL specified!");
                Usage(options);
                Environment.Exit((int)ERRNO.EBADOPTIONS);
            }

            // To customize application configuration such as set high DPI settings or default font,
            // see https://aka.ms/applicationconfiguration.
            ApplicationConfiguration.Initialize();

            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "--allow-insecure-localhost --unsafely-treat-insecure-origin-as-secure --enable-media-stream=1 --autoplay-policy=no-user-gesture-required");

            Application.Run(new MainForm());
        }
    }
}
