using System.ComponentModel;
using System.Diagnostics;

namespace NdsStation.Services;

/// <summary>
/// Windows Firewall rule that lets consoles on other PCs of the home network reach this
/// one (battles and trades between players behind the same router). It is as narrow as
/// possible: UDP, the relay port only, from the local subnet only, for this program only.
/// </summary>
public static class Firewall
{
    public const string RuleName = "NDS Station (rete locale)";
    public const int RelayPort = 28950;

    private static string ProgramPath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "NdsStation.exe");

    /// <summary>True if a rule with our name exists (reading rules needs no elevation).</summary>
    public static async Task<bool> RuleExistsAsync()
    {
        try
        {
            using var netsh = Process.Start(new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{RuleName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (netsh is null) return false;
            var output = await netsh.StandardOutput.ReadToEndAsync();
            await netsh.WaitForExitAsync();
            // The rule is tied to one executable: a copy of the app elsewhere needs its own.
            return netsh.ExitCode == 0 && output.Contains(ProgramPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Replaces our rule with one for this executable. Windows asks the user to confirm
    /// (UAC); returns false if they decline or it fails.
    /// </summary>
    public static async Task<bool> AllowAsync()
    {
        var program = ProgramPath;
        var command =
            $"netsh advfirewall firewall delete rule name=\"{RuleName}\" >nul 2>&1 & " +
            $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow program=\"{program}\" " +
            $"protocol=UDP localport={RelayPort} remoteip=localsubnet profile=any enable=yes";
        try
        {
            using var elevated = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{command}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (elevated is null) return false;
            await elevated.WaitForExitAsync();
        }
        catch (Win32Exception)
        {
            return false;   // UAC prompt declined
        }
        return await RuleExistsAsync();
    }
}
