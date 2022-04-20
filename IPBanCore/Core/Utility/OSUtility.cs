﻿/*
MIT License

Copyright (c) 2012-present Digital Ruby, LLC - https://www.digitalruby.com

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

#pragma warning disable CA1416 // Validate platform compatibility

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace DigitalRuby.IPBanCore
{
    /// <summary>
    /// Operating system utility methods
    /// </summary>
    public class OSUtility
    {
        /// <summary>
        /// Unknown operating system
        /// </summary>
        public const string Unknown = "Unknown";

        /// <summary>
        /// Windows
        /// </summary>
        public const string Windows = "Windows";

        /// <summary>
        /// Linux
        /// </summary>
        public const string Linux = "Linux";

        /// <summary>
        /// Macintosh / OS 10+
        /// </summary>
        public const string Mac = "Mac";

        /// <summary>
        /// Operating system name (i.e. Windows, Linux or OSX)
        /// </summary>
        public static string Name { get; private set; }

        /// <summary>
        /// Operating system cpu architecture (i.e. x86 or x64)
        /// </summary>
        public static string CpuArchitecture { get; private set; }

        /// <summary>
        /// Operating system version
        /// </summary>
        public static string Version { get; private set; }

        /// <summary>
        /// Operating system friendly/code name
        /// </summary>
        public static string FriendlyName { get; private set; }

        /// <summary>
        /// Operating system description
        /// </summary>
        public static string Description { get; private set; }

        /// <summary>
        /// Whether the OS uses the yum package manager (Linux only).
        /// True: Uses yum package manager (non Ubuntu/Debian)
        /// False: Uses apt (Ubuntu/Degian).
        /// </summary>
        public static bool UsesYumPackageManager { get; private set; }

        private static readonly string tempFolder;

        private static bool isWindows;
        private static bool isLinux;
        private static bool isMac;


        private static readonly string processVerb;

        static OSUtility()
        {
            try
            {
                tempFolder = Path.GetTempPath();
                if (string.IsNullOrWhiteSpace(tempFolder))
                {
                    if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    {
                        tempFolder = "c://temp";
                        processVerb = "runas";
                    }
                    else
                    {
                        tempFolder = "/tmp";
                    }
                }
                Directory.CreateDirectory(tempFolder);
                LoadOSInfo();
            }
            catch (Exception ex)
            {
                Logger.Error("Error in OSUtility static constructor", ex);
            }
        }

        private static string ExtractRegex(string input, string regex, string defaultValue)
        {
            Match m = Regex.Match(input, regex, RegexOptions.IgnoreCase | RegexOptions.Multiline);
            if (m.Success)
            {
                return m.Groups["value"].Value.Trim('[', ']', '"', '\'', '(', ')', ' ', '\r', '\n', '\t');
            }
            return defaultValue;
        }

        private static void LoadOSInfo()
        {
            Logger.Warn("Detecting os version...");

            // start off with built in version info, this is not as detailed or nice as we like,
            //  so we try some other ways to get more detailed information
            CpuArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
            Version = Environment.OSVersion.Version.ToString();
            Description = RuntimeInformation.OSDescription;

            // attempt to get detailed version info
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                LoadVersionFromLinux();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LoadVersionFromWindows();
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                // TODO: Implement better for MAC
                Name = FriendlyName = OSUtility.Mac;
                FriendlyName = "OSX";
                isMac = true;
            }
            else
            {
                Name = OSUtility.Unknown;
                FriendlyName = "Unknown";
            }

            Logger.Warn("OS version detected: {0}, app version: {1}",
                OSString(), Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3));
        }

        private static void LoadVersionFromLinux()
        {
            Name = FriendlyName = OSUtility.Linux;
            isLinux = true;
            string tempFile = GetTempFileName();
            using Process p = Process.Start("/bin/bash", "-c \"cat /etc/*release* > " + tempFile + "\"");
            p.WaitForExit();
            System.Threading.Tasks.Task.Delay(100); // wait a small bit for file to really be closed
            string versionText = File.ReadAllText(tempFile).Trim();
            ExtensionMethods.FileDeleteWithRetry(tempFile);
            if (string.IsNullOrWhiteSpace(versionText))
            {
                Logger.Error(new IOException("Unable to load os version from /etc/*release* ..."));
            }
            else
            {
                FriendlyName = ExtractRegex(versionText, "^(Id|Distrib_Id)=(?<value>.*?)$", string.Empty);
                if (FriendlyName.Length != 0)
                {
                    string codeName = ExtractRegex(versionText, "^(Name|Distrib_CodeName)=(?<value>.+)$", string.Empty);
                    if (codeName.Length != 0)
                    {
                        FriendlyName += " - " + codeName;
                    }
                    Version = ExtractRegex(versionText, "^Version_Id=(?<value>.+)$", Version);
                }
            }

            UsesYumPackageManager = FriendlyName.Contains("centos", StringComparison.OrdinalIgnoreCase) ||
                OSUtility.FriendlyName.Contains("fedora", StringComparison.OrdinalIgnoreCase) ||
                OSUtility.FriendlyName.Contains("red hat", StringComparison.OrdinalIgnoreCase) ||
                OSUtility.FriendlyName.Contains("redhat", StringComparison.OrdinalIgnoreCase);
        }

        private static void LoadVersionFromWindows()
        {
            Name = FriendlyName = OSUtility.Windows;
            isWindows = true;
            string friendlyName = HKLM_GetString(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ProductName");
            if (!string.IsNullOrWhiteSpace(friendlyName))
            {
                int pos = friendlyName.IndexOf(' ');
                if (pos > 0)
                {
                    string firstWord = friendlyName[..pos];

                    // as long as there are no extended chars, prepend Microsoft prefix
                    // some os will prepend Microsoft in another language
                    if (firstWord.Any(c => c > 126))
                    {
                        FriendlyName = friendlyName;
                    }
                    else
                    {
                        FriendlyName = "Microsoft " + friendlyName;
                    }
                }
            }

            // Windows loves to add a trailing .0 for some reason
            Version = Regex.Replace(Version, "\\.0$", string.Empty);
        }

        private static string HKLM_GetString(string path, string key)
        {
            try
            {
                using Microsoft.Win32.RegistryKey rk = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(path);
                if (rk == null) return "";
                return (string)rk.GetValue(key);
            }
            catch { return ""; }
        }

        private static void PopulateUsersWindows(Dictionary<string, bool> newUsers)
        {
            // Windows: WMIC
            // wmic useraccount get disabled,name
            // FALSE username
            // TRUE  disabledusername
            string output = StartProcessAndWait("wmic", "useraccount get disabled,name");
            string[] lines = output.Split('\n').Skip(1).ToArray();
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                int pos = trimmedLine.IndexOf(' ');
                if (pos >= 0)
                {
                    string disabled = trimmedLine[..pos].Trim();
                    string foundUserName = trimmedLine[pos..].Trim();
                    _ = bool.TryParse(disabled, out bool disabledBool);
                    newUsers[foundUserName] = !disabledBool;
                }
            }
        }

        private static void PopulateUsersLinux(Dictionary<string, bool> newUsers)
        {
            // Linux: /etc/passwd
            if (File.Exists("/etc/passwd") &&
                File.Exists("/etc/shadow"))
            {
                // enabled users must have an entry in password hash file
                string[] lines = File.ReadAllLines("/etc/shadow");

                // example line:
                // root:!$1$Fp$SSSuo3L.xA5s/kMEEIloU1:18049:0:99999:7:::
                foreach (string[] pieces in lines.Select(l => l.Split(':')).Where(p => p.Length == 9))
                {
                    string checkUserName = pieces[0].Trim();
                    string pwdHash = pieces[1].Trim();
                    bool hasPwdHash = (pwdHash.Length != 0 && pwdHash[0] != '*' && pwdHash[0] != '!');
                    newUsers[checkUserName] = hasPwdHash;
                }

                // filter out nologin users
                lines = File.ReadAllLines("/etc/passwd");

                // example line:
                // root:x:0:0:root:/root:/bin/bash
                foreach (string[] pieces in lines.Select(l => l.Split(':')).Where(p => p.Length == 7))
                {
                    // x means shadow file is where the password is at
                    string checkUserName = pieces[0].Trim();
                    string nologin = pieces[6];
                    bool cannotLogin = (nologin.Contains("nologin", StringComparison.OrdinalIgnoreCase) ||
                        nologin.Contains("/bin/false", StringComparison.OrdinalIgnoreCase));
                    if (cannotLogin)
                    {
                        newUsers[checkUserName] = false;
                    }
                }
            }
        }

        /// <summary>
        /// Get a string representing the operating system
        /// </summary>
        /// <returns>String</returns>
        public static string OSString()
        {
            return $"Name: {Name}, Version: {Version}, Friendly Name: {FriendlyName}, Description: {Description}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private class MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
            public MEMORYSTATUSEX()
            {
                this.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
            }
        }

        [return: MarshalAs(UnmanagedType.Bool)]
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MEMORYSTATUSEX lpBuffer);

        /// <summary>
        /// Get memory usage
        /// </summary>
        /// <param name="totalMemory">Total system memory in bytes</param>
        /// <param name="availableMemory">Available system memory in bytes</param>
        /// <returns></returns>
        public static bool GetMemoryUsage(out long totalMemory, out long availableMemory)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                MEMORYSTATUSEX mem = new();
                if (GlobalMemoryStatusEx(mem))
                {
                    totalMemory = (long)mem.ullTotalPhys;
                    availableMemory = (long)mem.ullAvailPhys;
                    return true;
                }
            }
            else if (File.Exists("/proc/meminfo"))
            {
                // try up to 10 times to get the file open and read
                for (int i = 0; i < 10; i++)
                {
                    try
                    {
                        // example:
                        // MemTotal:       66980684 kB
                        // MemFree:        50547060 kB
                        // TODO: Consider using pinvoke...
                        using StreamReader reader = File.OpenText("/proc/meminfo");
                        string total = reader.ReadLine();
                        string available = reader.ReadLine();
                        Match totalMatch = Regex.Match(total, "[0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        Match availableMatch = Regex.Match(available, "[0-9]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                        totalMemory = long.Parse(totalMatch.Value, CultureInfo.InvariantCulture) * 1024; // kb to bytes
                        availableMemory = long.Parse(availableMatch.Value, CultureInfo.InvariantCulture) * 1024; // kb to bytes
                        return true;
                    }
                    catch
                    {
                        // non-fatal, don't want this to crash the thread
                        System.Threading.Thread.Sleep(20);
                    }
                }
            }

            totalMemory = availableMemory = 0;
            return false;
        }

        /// <summary>
        /// Get aggregate disk usage
        /// </summary>
        /// <param name="totalStorage">Total storage possible for all drives</param>
        /// <param name="availableStorage">Total storage available for all drives</param>
        /// <returns>True if storage was able to be retrieved, false otherwise</returns>
        public static bool GetDiskUsage(out long totalStorage, out long availableStorage)
        {
            totalStorage = availableStorage = 0;
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.IsReady && drive.DriveType == DriveType.Fixed)
                    {
                        totalStorage += drive.TotalSize;
                        availableStorage += drive.AvailableFreeSpace;
                    }
                }
            }
            catch
            {
                // non-fatal
            }
            return totalStorage > 0;
        }

        /// <summary>
        /// Attempt to determine current cpu usage. For Linux, mpstat must be installed first.
        /// Ubuntu/Debian: apt install -y sysstat
        /// Rest: yum install -y sysstat
        /// </summary>
        /// <param name="percentUsed">Percent of all cpu resources currently in use, 0.0-1.0</param>
        /// <returns>True if cpu usage could be determined, false otherwise</returns>
        public static bool GetCpuUsage(out float percentUsed)
        {
            percentUsed = 0.0f;
            if (isWindows)
            {
                string output = StartProcessAndWait(60000, "wmic", "cpu get loadpercentage", out _, LogLevel.Trace);
                string[] lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 1 && float.TryParse(lines[^1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out percentUsed))
                {
                    percentUsed *= 0.01f;
                    return true;
                }
            }
            else if (isLinux)
            {
                string output = StartProcessAndWait(60000, "mpstat", string.Empty, out _, LogLevel.Trace);
                string[] lines = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (lines.Length > 1)
                {
                    int pos = lines[^1].LastIndexOf(' ');
                    if (pos > 0)
                    {
                        string piece = lines[^1][pos..].Trim();
                        if (pos > 0 && float.TryParse(piece, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out percentUsed))
                        {
                            percentUsed = 1.0f - (0.01f * percentUsed);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Easy way to execute processes. If the process has not finished after 60 seconds, it is forced killed.
        /// </summary>
        /// <param name="program">Program to run</param>
        /// <param name="args">Arguments</param>
        /// <param name="allowedExitCodes">Allowed exit codes, if null or empty it is not checked, otherwise a mismatch will throw an exception.</param>
        /// <returns>Output</returns>
        /// <exception cref="ApplicationException">Exit code did not match allowed exit codes</exception>
        public static string StartProcessAndWait(string program, string args, params int[] allowedExitCodes)
        {
            return StartProcessAndWait(60000, program, args, out _, LogLevel.Info, allowedExitCodes);
        }
        /// <summary>
        /// Easy way to execute processes. If the process has not finished after timeoutMilliseconds, it is forced killed.
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
        /// <param name="program">Program to run</param>
        /// <param name="args">Arguments</param>
        /// <param name="allowedExitCodes">Allowed exit codes, if null or empty it is not checked, otherwise a mismatch will throw an exception.</param>
        /// <returns>Output</returns>
        /// <exception cref="ApplicationException">Exit code did not match allowed exit codes</exception>
        public static string StartProcessAndWait(int timeoutMilliseconds, string program, string args,
            params int[] allowedExitCodes)
        {
            return StartProcessAndWait(timeoutMilliseconds, program, args, out _, LogLevel.Info, allowedExitCodes);
        }

        /// <summary>
        /// Easy way to execute processes. If the process has not finished after 60 seconds, it is forced killed.
        /// </summary>
        /// <param name="program">Program to run</param>
        /// <param name="args">Arguments</param>
        /// <param name="exitCode">Receives the exit code</param>
        /// <param name="allowedExitCodes">Allowed exit codes, if null or empty it is not checked, otherwise a mismatch will throw an exception.</param>
        /// <returns>Output</returns>
        /// <exception cref="ApplicationException">Exit code did not match allowed exit codes</exception>
        public static string StartProcessAndWait(string program, string args,
            out int exitCode, params int[] allowedExitCodes)
        {
            return StartProcessAndWait(60000, program, args, out exitCode, LogLevel.Info, allowedExitCodes);
        }

        /// <summary>
        /// Easy way to execute processes. If the process has not finished after timeoutMilliseconds, it is forced killed.
        /// </summary>
        /// <param name="timeoutMilliseconds">Timeout in milliseconds</param>
        /// <param name="program">Program to run</param>
        /// <param name="args">Arguments</param>
        /// <param name="exitCode">Receives the exit code</param>
        /// <param name="logLevel">Log level</param>
        /// <param name="allowedExitCodes">Allowed exit codes, if null or empty it is not checked, otherwise a mismatch will throw an exception.</param>
        /// <returns>Output</returns>
        /// <exception cref="ApplicationException">Exit code did not match allowed exit codes</exception>
        public static string StartProcessAndWait(int timeoutMilliseconds, string program, string args,
            out int exitCode, LogLevel logLevel = LogLevel.Info, params int[] allowedExitCodes)
        {
            StringBuilder output = new();
            int _exitCode = -1;
            Exception _ex = null;
            Thread thread = new(new ParameterizedThreadStart((_state) =>
            {
                try
                {
                    Logger.Log(logLevel, $"Executing process {program} {args}...");

                    var startInfo = new ProcessStartInfo(program, args)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        WindowStyle = ProcessWindowStyle.Hidden,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        Verb = processVerb
                    };
                    using var process = new Process
                    {
                        StartInfo = startInfo
                    };

                    process.Start();
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        output.Append("Terminating process due to timeout");
                        process.Kill();
                    }
                    string stdOut = process.StandardOutput.ReadToEnd();
                    string stdErr = process.StandardError.ReadToEnd();
                    output.Append(stdOut);
                    output.Append(stdErr);
                    _exitCode = process.ExitCode;
                    if (allowedExitCodes.Length != 0 && Array.IndexOf(allowedExitCodes, process.ExitCode) < 0)
                    {
                        _ex = new ApplicationException($"Program {program} {args}: failed with exit code {process.ExitCode}, output: {output}");
                    }
                }
                catch (Exception ex)
                {
                    _ex = ex;
                }
            }));
            thread.Start();
            int timeout = (timeoutMilliseconds < 1 ? Timeout.Infinite : timeoutMilliseconds + 5000);
            exitCode = _exitCode;
            if (_ex is not null)
            {
                throw _ex;
            }
            else if (!thread.Join(timeout))
            {
                throw new ApplicationException("Timed out waiting for process result");
            }
            return output.ToString();
        }

        private static Dictionary<string, bool> users = new(StringComparer.OrdinalIgnoreCase); // user name, enabled
        private static DateTime usersExpire = IPBanService.UtcNow;

        /// <summary>
        /// The amount of time to cache the users found in the <see cref="UserIsActive(string)"/> method.
        /// Default is 1 day.
        /// </summary>
        public static TimeSpan UserIsActiveCacheTime { get; set; } = TimeSpan.FromDays(1.0);

        /// <summary>
        /// Check if a user name is active on the local machine
        /// </summary>
        /// <param name="userName">User name to check</param>
        /// <returns>True if user name is active, false otherwise</returns>
        public static bool UserIsActive(string userName)
        {
            if (string.IsNullOrWhiteSpace(userName))
            {
                return false;
            }
            userName = userName.Trim();

            // check cache first
            bool cacheExpired = (usersExpire <= IPBanService.UtcNow);
            if (cacheExpired)
            {
                try
                {
                    Dictionary<string, bool> newUsers = new(StringComparer.OrdinalIgnoreCase);
                    lock (users)
                    {
                        cacheExpired = (usersExpire <= IPBanService.UtcNow);
                        if (cacheExpired)
                        {
                            if (isWindows)
                            {
                                PopulateUsersWindows(newUsers);
                            }
                            else if (isLinux)
                            {
                                PopulateUsersLinux(newUsers);
                            }
                            // TODO: MAC

                            usersExpire = IPBanService.UtcNow + UserIsActiveCacheTime;
                            users = newUsers;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Error("Error determining if user is active", ex);
                }
            }

            users.TryGetValue(userName, out bool enabled);
            return enabled;
        }

        /// <summary>
        /// Generate a new temporary file name using TempFolder, but do not create the file
        /// </summary>
        public static string GetTempFileName()
        {
            return Path.Combine(tempFolder, Guid.NewGuid().ToString("N") + ".tmp");
        }

        /// <summary>
        /// Add app domain exception handlers
        /// </summary>
        /// <param name="domain">Appdomain</param>
        public static void AddAppDomainExceptionHandlers(AppDomain domain)
        {
            domain.UnhandledException += (obj, ex) =>
            {
                if (ex.ExceptionObject is Exception _ex)
                {
                    Logger.Error(_ex);
                }
            };
            domain.FirstChanceException += (obj, ex) =>
            {
                //Logger.Error(ex.Exception);
            };
        }

        /// <summary>
        /// Get the current temp folder
        /// </summary>
        public static string TempFolder { get { return tempFolder; } }

        /// <summary>
        /// Are we on Windows?
        /// </summary>
        public static bool IsWindows => isWindows;

        /// <summary>
        /// Are we on Linux?
        /// </summary>
        public static bool IsLinux => isLinux;

        /// <summary>
        /// Are we on Mac?
        /// </summary>
        public static bool IsMac => isMac;

        /// <summary>
        /// Determine if system is Windows 7 or Windows Server 2008 - these systems tend to have a lot of hacks
        /// and work-arounds that are needed, especially for windows filtering platform
        /// </summary>
        public static bool IsWindows7OrServer2008
        {
            get
            {
                // Windows 7 and Server 2008 have major version of 6 and minor version of 0 or 1
                var version = System.Environment.OSVersion;
                return (version.Platform == PlatformID.Win32NT &&
                    version.Version.Major == 6 &&
                    version.Version.Minor < 2);
            }
        }

        /// <summary>
        /// Determine if system is Windows 8 or Windows Server 2012 - these systems are unable to log
        /// ip addresses is NLA is enabled
        /// </summary>
        public static bool IsWindows8OrServer2012
        {
            get
            {
                var version = System.Environment.OSVersion;
                return (version.Platform == PlatformID.Win32NT &&
                    version.Version.Major == 6 &&
                    version.Version.Minor > 1 && version.Version.Minor < 4);
            }
        }
    }

    /// <summary>
    /// Mark a class as requiring a specific operating system
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class RequiredOperatingSystemAttribute : Attribute
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="os">OS (IPBanOS.*) or null/empty if none</param>
        public RequiredOperatingSystemAttribute(string os)
        {
            RequiredOS = os?.Trim();
        }

        /// <summary>
        /// The required operating system (IPBanOS.*)
        /// </summary>
        public string RequiredOS { get; }

        /// <summary>
        /// Priority - higher priority are preferred when registering firewalls.
        /// Set to less than 0 to not include in regular firewall injection.
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// Major version minimum. Set to 0 or less to ignore.
        /// </summary>
        public int MajorVersionMinimum { get; set; }

        /// <summary>
        /// Minor version minimum. Set to 0 or less to ignore.
        /// </summary>
        public int MinorVersionMinimum { get; set; }

        /// <summary>
        /// Optional fallback firewall type if the constructor fails
        /// </summary>
        public Type FallbackFirewallType { get; set; }

        /// <summary>
        /// Require an environment variable to exist (key=value syntax)
        /// </summary>
        public string RequireEnvironmentVariable { get; set; }

        /// <summary>
        /// Whether the current OS is a match for this attribute
        /// </summary>
        public bool IsMatch
        {
            get
            {
                OperatingSystem os = Environment.OSVersion;

                // if priority less than 0, do not match
                bool matchPriority = Priority >= 0;

                // if no os specified, do not match
                bool matchRequiredOS = !string.IsNullOrWhiteSpace(RequiredOS) &&
                    RequiredOS.Equals(OSUtility.Name, StringComparison.OrdinalIgnoreCase);

                // major version matches if param is 0 or we are less than or equal to os major version with the param
                bool matchMajorVersion = (MajorVersionMinimum <= 0 || MajorVersionMinimum <= os.Version.Major);

                // minor version matches if major version param is 0 or minor version param is 0 or major version param
                //  is less than os major version or the minor version param is less than or equal to the os minor version
                bool matchMinorVersion = (MajorVersionMinimum <= 0 || MinorVersionMinimum <= 0 || MajorVersionMinimum < os.Version.Major ||
                        MinorVersionMinimum <= os.Version.Minor);

                bool matchEnvVar = true;
                if (!string.IsNullOrWhiteSpace(RequireEnvironmentVariable))
                {
                    string[] pieces = RequireEnvironmentVariable.Split('=');
                    if (pieces.Length == 2 && !string.IsNullOrWhiteSpace(pieces[0]) && !string.IsNullOrWhiteSpace(pieces[1]))
                    {
                        string value = Environment.GetEnvironmentVariable(pieces[0]);
                        matchEnvVar = pieces[1].Equals(value, StringComparison.OrdinalIgnoreCase);
                    }
                }

                // valid is AND of all of the above
                bool valid = matchPriority && matchRequiredOS && matchMajorVersion && matchMinorVersion && matchEnvVar;

                return valid;
            }
        }
    }

    /// <summary>
    /// Apply a custom name to a class
    /// </summary>
    [AttributeUsage(AttributeTargets.Class)]
    public class CustomNameAttribute : Attribute
    {
        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="name">Custom name</param>
        public CustomNameAttribute(string name)
        {
            Name = name;
        }

        /// <summary>
        /// Short name
        /// </summary>
        public string Name { get; set; }
    }
}

#pragma warning restore CA1416 // Validate platform compatibility
