using System.Diagnostics;
using System.IO.Compression;
using System.Text;
namespace Unictrl;

internal sealed record UpdateState(string Text, bool Ready = false, bool CanCancel = false, double? Progress = null);
internal sealed class AppUpdater : IDisposable
{
    private readonly Settings settings;
    private readonly Action<UpdateState> status;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly System.Threading.Timer timer;
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? attempt;
    private int busy;
    private string? staged, work;
    public AppUpdater(Settings settings, Action<UpdateState> status)
    {
        this.settings = settings; this.status = status;
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Unictrl-Personal-Fork");
        timer = new(_ => { if (settings.AutomaticUpdates) _ = Check(); }, null, TimeSpan.FromSeconds(5), TimeSpan.FromHours(6));
    }
    public void CancelDownload() { try { attempt?.Cancel(); } catch (ObjectDisposedException) { } }
    public async Task Check()
    {
        if (staged is not null || Interlocked.CompareExchange(ref busy, 1, 0) != 0) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        attempt = operation; var cancel = operation.Token;
        string result = "Download cancelled";
        try
        {
            status(new("Checking for updates…", CanCancel: true));
            using var response = await http.GetAsync(UpdateProtocol.Feed, HttpCompletionOption.ResponseHeadersRead, cancel);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) { result = "No published update yet"; return; }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 65536) throw new InvalidDataException("Update manifest too large");
            using var feed = await response.Content.ReadAsStreamAsync(cancel);
            using var memory = new MemoryStream(); var buffer = new byte[65536]; int count;
            while ((count = await feed.ReadAsync(buffer, cancel)) > 0) { if (memory.Length + count > 65536) throw new InvalidDataException("Update manifest too large"); memory.Write(buffer, 0, count); }
            var release = UpdateProtocol.Parse(memory.ToArray());
            var current = typeof(App).Assembly.GetName().Version!;
            if (UpdateProtocol.VersionOf(release.Version)! <= new Version(current.Major, current.Minor, current.Build)) { result = $"Up to date · {current.ToString(3)}"; return; }
            var asset = Settings.IsPortable ? release.Windows : release.WindowsInstaller ?? throw new InvalidDataException("This release has no Windows installer");
            var installed = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            work = Settings.IsPortable
                ? Path.Combine(Directory.GetParent(installed)!.FullName, ".Unictrl-update-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Path.GetTempPath(), "Unictrl-update-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var archive = Path.Combine(work, Settings.IsPortable ? "update.zip" : "Unictrl-Setup.exe");
            status(new($"Downloading {release.Version}…", CanCancel: true, Progress: 0));
            using (var download = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                download.EnsureSuccessStatusCode();
                if (download.Content.Headers.ContentLength is { } length && length != asset.Size) throw new InvalidDataException("Update download length");
                using var input = await download.Content.ReadAsStreamAsync(cancel);
                using var output = File.Create(archive); long total = 0; var percent = -1;
                while ((count = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    total += count; if (total > asset.Size) throw new InvalidDataException("Update download exceeds limit");
                    await output.WriteAsync(buffer.AsMemory(0, count), cancel);
                    var next = (int)(total * 100 / asset.Size);
                    if (next != percent) { percent = next; status(new($"Downloading {release.Version} · {percent}%", CanCancel: true, Progress: (double)total / asset.Size)); }
                }
            }
            cancel.ThrowIfCancellationRequested();
            status(new("Verifying update…", CanCancel: true));
            UpdateProtocol.Verify(archive, asset);
            if (Settings.IsPortable)
            {
                var extracted = Path.Combine(work, "extracted"); Extract(archive, extracted, cancel);
                var roots = Directory.GetDirectories(extracted);
                if (roots.Length != 1 || !File.Exists(Path.Combine(roots[0], "Unictrl.exe")) || !File.Exists(Path.Combine(roots[0], "Unictrl.portable")) || Directory.Exists(Path.Combine(roots[0], "Data")))
                    throw new InvalidDataException("Invalid portable update layout");
                var version = FileVersionInfo.GetVersionInfo(Path.Combine(roots[0], "Unictrl.exe"));
                if (new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart) != UpdateProtocol.VersionOf(release.Version)) throw new InvalidDataException("Update executable version");
                staged = roots[0];
            }
            else staged = archive;
            cancel.ThrowIfCancellationRequested();
            result = $"{release.Version} ready · restart to install";
        }
        catch (OperationCanceledException) { staged = null; result = "Download cancelled"; }
        catch (Exception error) { staged = null; result = "Update failed: " + error.Message; }
        finally
        {
            if (staged is null && work is not null) { try { Directory.Delete(work, true); } catch { } work = null; }
            attempt = null; Interlocked.Exchange(ref busy, 0);
            if (!lifetime.IsCancellationRequested) status(new(result, Ready: staged is not null));
        }
    }
    internal static void Extract(string archive, string target, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(target); var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive); long total = 0;
        if (zip.Entries.Count > 5000) throw new InvalidDataException("Update has too many entries");
        foreach (var entry in zip.Entries)
        {
            cancel.ThrowIfCancellationRequested(); total += entry.Length;
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
            var helper = Settings.IsPortable ? Helper : InstalledHelper;
            var command = "$cfg=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + config + "'))|ConvertFrom-Json; & {" + helper + "} -OldPid $cfg.OldPid -Target $cfg.Target -Source $cfg.Source -Work $cfg.Work";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in new[] {"-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command))}) start.ArgumentList.Add(argument);
            _ = Process.Start(start) ?? throw new IOException("Updater could not start");
            staged = null; work = null; quit();
        }
        catch (Exception error) { status(new("Cannot start installer: " + error.Message, Ready: true)); }
        return true;
    }
    public void Dispose() { timer.Dispose(); lifetime.Cancel(); CancelDownload(); http.Dispose(); }
    internal const string InstalledHelper = """
        param([int]$OldPid,[string]$Target,[string]$Source,[string]$Work)
        $ErrorActionPreference='Stop'
        $process=Get-Process -Id $OldPid -ErrorAction SilentlyContinue
        if($process -and -not $process.WaitForExit(60000)){exit 1}
        $arguments='/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1 /DIR="'+$Target+'"'
        $installer=Start-Process -FilePath $Source -ArgumentList $arguments -Wait -PassThru
        if($installer.ExitCode -eq 0){Start-Process -FilePath (Join-Path $Target 'Unictrl.exe') -WorkingDirectory $Target}
        else{Start-Process -FilePath (Join-Path $Target 'Unictrl.exe') -WorkingDirectory $Target; exit 1}
        Remove-Item -LiteralPath $Work -Recurse -Force
        """;
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
