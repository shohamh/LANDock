using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LANDock.Core;

namespace LANDock.Host;

/// <summary>Writes what the Apollo instances need before they can start: one config per instance, the web API credentials,
/// and a private place for each instance's certificate. Safe to run again.</summary>
public static class HostSetup
{
    public static void Ensure()
    {
        if (!Paths.HostInstalled) throw new InvalidOperationException($"the host component is not installed ({Paths.Sunshine} is missing)");
        Directory.CreateDirectory(Paths.HostState);

        if (!File.Exists(Paths.Credentials))
        {
            var pw = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            File.WriteAllText(Paths.Credentials, $"user: landock  password: {pw}{Environment.NewLine}");
            Log.Info("created web API credentials (only used on this computer)");
        }
        var password = ApolloApi.Credentials()?.password ?? throw new InvalidOperationException("credentials file is unreadable");

        foreach (var inst in HostPlanning.Instances)
        {
            var dir = Paths.InstanceDir(inst.Key);
            Directory.CreateDirectory(dir);
            var conf = new StringBuilder()
                .AppendLine($"sunshine_name = LANDock-{inst.Key.ToUpperInvariant()}")
                .AppendLine($"port = {inst.Port}")
                .AppendLine($"file_apps = {Paths.Fwd(Paths.AppsJson)}")
                .AppendLine($"file_state = {Paths.Fwd(Path.Combine(dir, "state.json"))}")
                .AppendLine($"credentials_file = {Paths.Fwd(Path.Combine(dir, "web-credentials.json"))}")
                .AppendLine($"log_path = {Paths.Fwd(Paths.InstanceLog(inst.Key))}")
                .AppendLine($"pkey = {Paths.Fwd(Path.Combine(dir, "cakey.pem"))}")
                .AppendLine($"cert = {Paths.Fwd(Path.Combine(dir, "cacert.pem"))}")
                .AppendLine("origin_web_ui_allowed = pc")   // the web UI answers on this computer only
                .ToString();
            File.WriteAllText(Paths.InstanceConf(inst.Key), conf);

            if (!File.Exists(Path.Combine(dir, "web-credentials.json")))
            {
                var psi = new ProcessStartInfo(Paths.Sunshine)
                {
                    WorkingDirectory = Paths.HostBin, UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                psi.ArgumentList.Add(Paths.Fwd(Paths.InstanceConf(inst.Key)));
                psi.ArgumentList.Add("--creds"); psi.ArgumentList.Add("landock"); psi.ArgumentList.Add(password);
                using var p = Process.Start(psi)!;
                p.StandardOutput.ReadToEndAsync(); p.StandardError.ReadToEndAsync();
                if (!p.WaitForExit(30_000)) { try { p.Kill(true); } catch { } }
            }
        }
    }

    /// <summary>The laptop's IPv4 addresses on real networks: what the desktop PC types as host address.</summary>
    public static List<(string name, string address)> LocalAddresses()
    {
        var res = new List<(string, string)>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            var desc = ni.Description;
            if (desc.Contains("Virtual", StringComparison.OrdinalIgnoreCase) || desc.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                || desc.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) || desc.Contains("WSL", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var s = ua.Address.ToString();
                if (s.StartsWith("169.254.")) continue;
                res.Add((ni.Name, s));
            }
        }
        return res;
    }
}

/// <summary>The Windows Firewall rules the host needs, so desktop PCs can reach the Apollo streams (sunshine.exe) and the
/// control port (LANDock.exe). Both are for private networks only.</summary>
public static class Firewall
{
    public const string RuleName = "LANDock host";
    public const string ControlRuleName = "LANDock control";

    private static string AppExe => Environment.ProcessPath ?? Path.Combine(Paths.InstallDir, "LANDock.exe");

    private static bool Exists(string rule)
    {
        try
        {
            var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{rule}\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi)!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public static bool RuleExists() => Exists(RuleName) && Exists(ControlRuleName);

    /// <summary>Adds the rules (Private networks only). Needs administrator rights, so Windows shows its own permission prompt.</summary>
    public static bool AddRuleElevated()
    {
        try
        {
            var args = string.Join(" & ",
                $"netsh advfirewall firewall delete rule name=\"{RuleName}\"",
                $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{Paths.Sunshine}\" profile=private enable=yes",
                $"netsh advfirewall firewall delete rule name=\"{ControlRuleName}\"",
                $"netsh advfirewall firewall add rule name=\"{ControlRuleName}\" dir=in action=allow program=\"{AppExe}\" profile=private enable=yes");
            var psi = new ProcessStartInfo("cmd.exe", $"/c {args}") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            using var p = Process.Start(psi)!;
            p.WaitForExit(60_000);
            return RuleExists();
        }
        catch (Exception ex) { Log.Error("could not add the firewall rules", ex); return false; }
    }
}
