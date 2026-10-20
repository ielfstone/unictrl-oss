using System.Diagnostics;
using System.IO.Compression;
using System.Text;
namespace Unictrl;

internal sealed class AppUpdater : IDisposable
{
    private readonly Settings settings;
    private readonly Action<string, bool> status;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly System.Threading.Timer timer;
    private readonly CancellationTokenSource lifetime = new();
    private int busy;
    private string? staged, work;
    public AppUpdater(Settings settings, Action<string, bool> status)
    {
        this.settings = settings; this.status = status;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Unictrl-Personal-Fork/0.4.0");
        timer = new(_ => { if (settings.AutomaticUpdates) _ = Check(); }, null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(6));
    }
    public async Task Check()
    {
        if (staged is not null || Interlocked.Exchange(ref busy, 1) != 0) return;
        try
        {
            status("Checking for updates…", false);
            using var response = await http.GetAsync(UpdateProtocol.Feed, HttpCompletionOption.ResponseHeadersRead, lifetime.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { status("No published update yet", false); return; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("Update manifest too large");
            using var feed = await response.Content.ReadAsStreamAsync(lifetime.Token);
            using var memory = new MemoryStream(); var buffer = new byte[65536]; int count;
            while ((count = await feed.ReadAsync(buffer, lifetime.Token)) > 0) { if (memory.Length + count > 65536) throw new InvalidDataException("Update manifest too large"); memory.Write(buffer, 0, count); }
            var release = UpdateProtocol.Parse(memory.ToArray());
            var current = typeof(App).Assembly.GetName().Version!;
            if (UpdateProtocol.VersionOf(release.Version)! <= new Version(current.Major, current.Minor, current.Build)) { status($"Up to date · {current.ToString(3)}", false); return; }
            if (!Settings.IsPortable) throw new InvalidOperationException("Automatic installation is available in the portable build");
            var installed = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            work = Path.Combine(Directory.GetParent(installed)!.FullName, ".Unictrl-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, "update.zip"); status($"Downloading {release.Version}…", false);
            using (var download = await http.GetAsync(release.Windows.Url, HttpCompletionOption.ResponseHeadersRead, lifetime.Token))
            {
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is { } length && length != release.Windows.Size) throw new InvalidDataException("Update download length");
                using var input = await download.Content.ReadAsStreamAsync(lifetime.Token);
                using var output = File.Create(archive); long total = 0; var percent = -1;
                while ((count = await input.ReadAsync(buffer, lifetime.Token)) > 0)
                {
                    total += count; if (total > release.Windows.Size) throw new InvalidDataException("Update download exceeds limit");
                    await output.WriteAsync(buffer.AsMemory(0, count), lifetime.Token);
                    var next = (int)(total * 100 / release.Windows.Size);
                    if (next != percent) { percent = next; status($"Downloading {release.Version} · {percent}%", false); }
                }
            }
            UpdateProtocol.Verify(archive, release.Windows);
            var extracted = Path.Combine(work, "extracted"); Extract(archive, extracted);
            var roots = Directory.GetDirectories(extracted);
            if (roots.Length != 1 || !File.Exists(Path.Combine(roots[0], "Unictrl.exe")) || !File.Exists(Path.Combine(roots[0], "Unictrl.portable")) || Directory.Exists(Path.Combine(roots[0], "Data")))
                throw new InvalidDataException("Invalid portable update layout");
            var version = FileVersionInfo.GetVersionInfo(Path.Combine(roots[0], "Unictrl.exe"));
            if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) != UpdateProtocol.VersionOf(release.Version)) throw new InvalidDataException("Update executable version");
            staged = roots[0]; status($"{release.Version} ready · restart to install", true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (work is not null) { try { Directory.Delete(work, true); } catch { } work = null; }
            status("Update failed: " + error.Message, false);
        }
        finally { Interlocked.Exchange(ref busy, 0); }
    }
    internal static void Extract(string archive, string target)
    {
        Directory.CreateDirectory(target); var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive); long total = 0;
        if (zip.Entries.Count > 5000) throw new InvalidDataException("Update has too many entries");
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            var destination = Path.GetFullPath(Path.Combine(target, entry.FullName));
            if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains(':') ||
                ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || total > 2_147_483_648)
                throw new InvalidDataException("Invalid update archive path or size");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(destination);
            else { Directory.CreateDirectory(Path.GetDirectoryName(destination)!); entry.ExtractToFile(destination, false); }
        }
    }
    public bool Install(Action quit)
    {
        if (staged is null || work is null) return false;
        try
        {
            var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new {
                OldPid = Environment.ProcessId, Target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), Source = staged, Work = work
            })));
            var command = "$cfg=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + config + "'))|ConvertFrom-Json; & {" + Helper + "} -OldPid $cfg.OldPid -Target $cfg.Target -Source $cfg.Source -Work $cfg.Work";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] {"-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))}) start.ArgumentList.Add(argument);
            _ = Process.Start(start) ?? throw new IOException("Updater could not start");
            staged = null; work = null; quit();
        }
        catch (Exception error) { status("Cannot start installer: " + error.Message, true); }
        return true;
    }
    public void Dispose() { timer.Dispose(); lifetime.Cancel(); http.Dispose(); }
    internal const string Helper = """
        param([int]$OldPid,[string]$Target,[string]$Source,[string]$Work)
        $ErrorActionPreference='Stop'
        $process=Get-Process -Id $OldPid -ErrorAction SilentlyContinue
        if($process -and -not $process.WaitForExit(60000)){exit 1}
        $backup=$Target+'.previous'
        if(Test-Path -LiteralPath $backup){Move-Item -LiteralPath $backup -Destination ($backup+'.'+[DateTime]::UtcNow.Ticks)}
        Move-Item -LiteralPath $Target -Destination $backup
        try {
          $data=Join-Path $backup 'Data'
          if(Test-Path -LiteralPath $data){Copy-Item -LiteralPath $data -Destination (Join-Path $Source 'Data') -Recurse}
          Move-Item -LiteralPath $Source -Destination $Target
          Start-Process -FilePath (Join-Path $Target 'Unictrl.exe') -WorkingDirectory $Target
        } catch {
          if(Test-Path -LiteralPath $Target){Move-Item -LiteralPath $Target -Destination ($Target+'.failed.'+[DateTime]::UtcNow.Ticks)}
          Move-Item -LiteralPath $backup -Destination $Target
          Start-Process -FilePath (Join-Path $Target 'Unictrl.exe') -WorkingDirectory $Target
          exit 1
        }
        Set-Location -LiteralPath ([IO.Path]::GetTempPath())
        Remove-Item -LiteralPath $Work -Recurse -Force
        """;
}
