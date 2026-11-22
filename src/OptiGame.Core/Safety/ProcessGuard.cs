using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace OptiGame.Core.Safety;

/// <summary>Tells whether any process is running from inside a game's install folder.</summary>
public static class ProcessGuard
{
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(IntPtr h);

    /// <summary>Full image paths of running processes this user can query.</summary>
    public static List<string> RunningImages()
    {
        var list = new List<string>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (h == IntPtr.Zero) continue;
                try
                {
                    var sb = new StringBuilder(1024);
                    int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) list.Add(sb.ToString());
                }
                finally { CloseHandle(h); }
            }
        }
        return list;
    }

    /// <summary>The first running exe found inside installDir, or null.</summary>
    public static string? RunningFrom(string installDir, IReadOnlyList<string>? images = null)
    {
        var root = Path.GetFullPath(installDir).TrimEnd('\\') + "\\";
        return (images ?? RunningImages()).FirstOrDefault(i => i.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }
}
