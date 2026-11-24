using System.Net.NetworkInformation;

namespace v2rayN.Manager;

/// <summary>
/// Controls chain.exe (DoH + TLS fragment + fake packet local proxy).
/// dns_chain.ini always lives next to chain.exe, inside the "Chain" folder.
/// The app contains NO built-in defaults: chain.exe itself creates the file the first
/// time it runs, and the app only reads / saves what is in that file.
/// "Reset to default" deletes the file, runs chain.exe once (hidden) so that it
/// generates a fresh one, stops it again and reloads the file.
/// </summary>
internal static class ChainManager
{
    private static Process? _proc;

    // chain.exe + dns_chain.ini: always together in <app>\Chain
    public static string ChainDir => Utils.GetBaseDirectory("Chain");
    public static string ExePath => Path.Combine(ChainDir, "chain.exe");
    public static string IniPath => Path.Combine(ChainDir, "dns_chain.ini");

    // runtime/state files: <app>\guiChain
    private static string DataDir => Utils.GetPath("guiChain");
    private static string LogPath => Path.Combine(DataDir, "chain.log");
    private static string StopPath => Path.Combine(DataDir, "chain.stop");
    private static string EnabledFlag => Path.Combine(DataDir, "chain.enabled");
    private static string ConsoleFlag => Path.Combine(DataDir, "chain.console");

    public static bool EnabledPreference => File.Exists(EnabledFlag);

    public static bool ShowConsole
    {
        get => File.Exists(ConsoleFlag);
        set => SetFlag(ConsoleFlag, value);
    }

    public static bool IsRunning => _proc != null && !SafeExited(_proc);

    #region ini

    public static bool IniExists => File.Exists(IniPath);

    /// <summary>Content of dns_chain.ini, or an empty string when the file does not exist.</summary>
    public static string ReadIni()
    {
        try
        {
            if (!File.Exists(IniPath))
            {
                return string.Empty;
            }
            using var fs = new FileStream(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return sr.ReadToEnd();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static void SaveIni(string text) => WriteText(IniPath, text);

    // UTF-8 without BOM (a BOM breaks Python configparser on [proxy])
    private static void WriteText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    private static int ReadListenPort()
    {
        try
        {
            foreach (var raw in File.ReadLines(IniPath))
            {
                var line = raw.Trim();
                if (line.StartsWith(';') || line.StartsWith('#'))
                {
                    continue;
                }
                var i = line.IndexOf('=');
                if (i > 0
                    && line[..i].Trim().Equals("listen_port", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[(i + 1)..].Trim(), out var p))
                {
                    return p;
                }
            }
        }
        catch
        {
        }
        return 18080;
    }

    #endregion ini

    #region start / stop

    /// <returns>null on success, otherwise an error message.</returns>
    public static async Task<string?> StartAsync()
    {
        if (IsRunning)
        {
            SetFlag(EnabledFlag, true);
            return null;
        }

        if (!File.Exists(ExePath))
        {
            return $"{ExePath} is missing.";
        }

        try
        {
            Directory.CreateDirectory(DataDir);
            TryDelete(StopPath);

            await WaitPortFreeAsync(ReadListenPort(), 3000);

            var showConsole = ShowConsole;
            var args = $"--stop-file \"{StopPath}\" --parent-pid {Environment.ProcessId}";
            if (!showConsole)
            {
                args += $" --log-file \"{LogPath}\"";
            }

            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = args,
                WorkingDirectory = ChainDir, // dns_chain.ini sits next to chain.exe
                UseShellExecute = true,
                WindowStyle = showConsole ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden
            };
            if (!Utils.IsAdministrator())
            {
                psi.Verb = "runas";
            }

            _proc = Process.Start(psi);
            SetFlag(EnabledFlag, true);

            await Task.Delay(2000);
            if (_proc == null || SafeExited(_proc))
            {
                SetFlag(EnabledFlag, false);
                var tail = showConsole ? "" : Environment.NewLine + TailLog(8);
                return "chain.exe exited right after start." + tail;
            }
            return null;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            SetFlag(EnabledFlag, false);
            return "Administrator permission was cancelled.";
        }
        catch (Exception ex)
        {
            SetFlag(EnabledFlag, false);
            return ex.Message;
        }
    }

    public static async Task StopAsync()
    {
        SetFlag(EnabledFlag, false);

        var p = _proc;
        if (p == null || SafeExited(p))
        {
            _proc = null;
            return;
        }
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(StopPath, "stop");
            for (var i = 0; i < 30 && !SafeExited(p); i++)
            {
                await Task.Delay(100);
            }
            if (!SafeExited(p))
            {
                p.Kill(true);
            }
        }
        catch
        {
        }
        _proc = null;
    }

    /// <summary>Called when v2rayN exits. Keeps the enabled flag so Chain restarts next time.</summary>
    public static void StopProcess()
    {
        var p = _proc;
        if (p == null || SafeExited(p))
        {
            return;
        }
        try
        {
            File.WriteAllText(StopPath, "stop");
            if (!p.WaitForExit(1500))
            {
                p.Kill(true);
            }
        }
        catch
        {
        }
    }

    #endregion start / stop

    #region reset to default

    /// <summary>
    /// Deletes dns_chain.ini, runs chain.exe once (hidden) so that it writes its own default file,
    /// stops it again, and leaves the new file in place. The old file is restored on failure.
    /// If Chain was running it is started again afterwards.
    /// </summary>
    /// <returns>null on success, otherwise an error message.</returns>
    public static async Task<string?> ResetIniAsync()
    {
        if (!File.Exists(ExePath))
        {
            return $"{ExePath} is missing.";
        }

        var wasRunning = IsRunning;
        var hadIni = File.Exists(IniPath);
        var backup = IniPath + ".bak";
        string? error;

        try
        {
            if (wasRunning)
            {
                await StopAsync();
            }

            Directory.CreateDirectory(DataDir);
            if (hadIni)
            {
                File.Copy(IniPath, backup, true);
                File.Delete(IniPath);
            }

            error = await GenerateIniAsync();
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        if (error != null)
        {
            // put the user's old file back
            try
            {
                if (hadIni && !File.Exists(IniPath) && File.Exists(backup))
                {
                    File.Copy(backup, IniPath, true);
                }
            }
            catch
            {
            }
        }
        TryDelete(backup);

        if (wasRunning)
        {
            var startError = await StartAsync();
            if (startError != null && error == null)
            {
                error = "dns_chain.ini was reset, but chain.exe could not be started again: " + startError;
            }
        }

        return error;
    }

    /// <returns>null when chain.exe created dns_chain.ini, otherwise an error message.</returns>
    private static async Task<string?> GenerateIniAsync()
    {
        TryDelete(StopPath);

        var args = $"--stop-file \"{StopPath}\" --parent-pid {Environment.ProcessId} --log-file \"{LogPath}\"";
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            Arguments = args,
            WorkingDirectory = ChainDir, // chain.exe writes dns_chain.ini next to itself
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (!Utils.IsAdministrator())
        {
            psi.Verb = "runas";
        }

        Process? p = null;
        var created = false;
        try
        {
            p = Process.Start(psi);

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(IniPath) && await IsIniReadyAsync())
                {
                    created = true;
                    break;
                }
                if (p == null || SafeExited(p))
                {
                    created = File.Exists(IniPath) && await IsIniReadyAsync();
                    break;
                }
                await Task.Delay(100);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "Administrator permission was cancelled.";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            await StopTempProcessAsync(p);
        }

        return created
            ? null
            : "chain.exe did not create dns_chain.ini." + Environment.NewLine + TailLog(8);
    }

    // the file exists, is not empty and has stopped growing (chain.exe finished writing it)
    private static async Task<bool> IsIniReadyAsync()
    {
        try
        {
            var length = new FileInfo(IniPath).Length;
            if (length == 0)
            {
                return false;
            }
            await Task.Delay(500);
            if (new FileInfo(IniPath).Length != length)
            {
                return false;
            }
            using var fs = new FileStream(IniPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static async Task StopTempProcessAsync(Process? p)
    {
        if (p == null)
        {
            return;
        }
        try
        {
            if (!SafeExited(p))
            {
                File.WriteAllText(StopPath, "stop");
                for (var i = 0; i < 30 && !SafeExited(p); i++)
                {
                    await Task.Delay(100);
                }
                if (!SafeExited(p))
                {
                    p.Kill(true);
                }
            }
        }
        catch
        {
        }
        TryDelete(StopPath);
    }

    #endregion reset to default

    #region helpers

    private static bool SafeExited(Process p)
    {
        try
        {
            return p.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static void SetFlag(string path, bool on)
    {
        try
        {
            if (on)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "1");
            }
            else
            {
                TryDelete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string TailLog(int lines)
    {
        try
        {
            using var fs = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var all = sr.ReadToEnd().Split('\n');
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch
        {
            return "";
        }
    }

    private static async Task WaitPortFreeAsync(int port, int timeoutMs)
    {
        var waited = 0;
        while (waited < timeoutMs)
        {
            var busy = IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners().Any(e => e.Port == port);
            if (!busy)
            {
                return;
            }
            await Task.Delay(300);
            waited += 300;
        }
    }

    #endregion helpers
}
