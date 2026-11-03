using System.Diagnostics;
using System.Text;

namespace DiskCleaner
{
    public class ProcessResult
    {
        public int ExitCode { get; set; }
        public string Output { get; set; }
        public bool NotFound { get; set; }
        public bool TimedOut { get; set; }
    }

    public class VirtualDisk
    {
        public string Path { get; set; }
        public long Size { get; set; }
        public string SizeText => Fmt.Bytes(Size);
        public string Name => System.IO.Path.GetFileName(Path);
    }

    public static class DockerTools
    {
        private static readonly string DockerData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Docker");

        /// <summary>Docker Desktop's WSL virtual disks (docker_data.vhdx and friends).</summary>
        public static List<VirtualDisk> FindVirtualDisks()
        {
            var list = new List<VirtualDisk>();
            string wsl = Path.Combine(DockerData, "wsl");
            if (!FileWalker.DirectoryExists(wsl)) return list;
            FileWalker.Walk(wsl, CancellationToken.None, new WalkStats(),
                (string path, ref Native.WIN32_FIND_DATA d) =>
                {
                    if (path.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase))
                        list.Add(new VirtualDisk { Path = path, Size = d.Size });
                },
                null, null);
            return list.OrderByDescending(v => v.Size).ToList();
        }

        public static Task<ProcessResult> SystemDf() => RunAsync("system df", TimeSpan.FromSeconds(60));

        /// <summary>
        /// Removes stopped containers, unused networks, all unused images and build cache.
        /// Deliberately without --volumes: volumes hold databases and other persistent data.
        /// </summary>
        public static Task<ProcessResult> PruneAll() => RunAsync("system prune -a -f", TimeSpan.FromMinutes(30));

        private static Task<ProcessResult> RunAsync(string args, TimeSpan timeout) => Task.Run(() => Run(args, timeout));

        private static ProcessResult Run(string args, TimeSpan timeout)
        {
            var psi = new ProcessStartInfo("docker", args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            var output = new StringBuilder();
            Process p;
            try
            {
                p = Process.Start(psi);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new ProcessResult { NotFound = true, ExitCode = -1, Output = "Docker CLI not found. Install Docker Desktop or add docker to PATH." };
            }
            using (p)
            {
                p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (output) output.AppendLine(e.Data); };
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                if (!p.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { p.Kill(true); } catch (InvalidOperationException) { }
                    return new ProcessResult { TimedOut = true, ExitCode = -1, Output = output.ToString() + "\nTimed out." };
                }
                p.WaitForExit();
                return new ProcessResult { ExitCode = p.ExitCode, Output = output.ToString().TrimEnd() };
            }
        }

        public const string CompactSteps =
            "1. Quit Docker Desktop (tray icon > Quit Docker Desktop).\n" +
            "2. Open Terminal as administrator and run:  wsl --shutdown\n" +
            "3. Run:  diskpart\n" +
            "4. In diskpart run, one line at a time:\n" +
            "     select vdisk file=\"{0}\"\n" +
            "     attach vdisk readonly\n" +
            "     compact vdisk\n" +
            "     detach vdisk\n" +
            "     exit\n" +
            "5. Start Docker Desktop again.";
    }
}
