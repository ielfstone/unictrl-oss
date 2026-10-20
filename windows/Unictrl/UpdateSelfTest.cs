using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace Unictrl;
internal static class UpdateSelfTest
{
    private sealed class SlowStream : Stream
    {
        private bool first = true;
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => 3; public override long Position { get => first ? 0 : 1; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
        { if (first) { first = false; buffer.Span[0] = 1; return 1; } await Task.Delay(Timeout.Infinite, cancel); return 0; }
        public override int Read(byte[] b, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { } public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] b, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class UpdateFixtureHandler(byte[] fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            HttpContent content = request.RequestUri!.AbsoluteUri == UpdateProtocol.Feed ? new ByteArrayContent(fixture) : new StreamContent(new SlowStream());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content });
        }
    }
    private static void CancelDownloadCheck()
    {
        using var fixture = typeof(App).Assembly.GetManifestResourceStream("Unictrl.UpdateFixture")!;
        using var memory = new MemoryStream(); fixture.CopyTo(memory);
        using var progress = new ManualResetEventSlim(); string? directory = null; UpdateState? last = null; AppUpdater? updater = null;
        using var client = new HttpClient(new UpdateFixtureHandler(memory.ToArray()));
        using var service = updater = new AppUpdater(new Settings(), state => { last = state; if (state.Progress > 0) { directory = updater!.DownloadDirectory; progress.Set(); } }, client, new Version(0,0,0), false);
        var job = Task.Run(service.Check);
        if (!progress.Wait(5000)) throw new Exception("Update progress missing");
        service.CancelDownload();
        if (!job.Wait(5000) || last?.Text != "Download cancelled" || last.CanCancel || directory is null || Directory.Exists(directory)) throw new Exception("Update cancellation left a partial download");
    }
    public static void Run()
    {
        CancelDownloadCheck();
        var root = Path.Combine(Path.GetTempPath(), "Unictrl update test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var badZip = Path.Combine(root, "bad.zip");
            using (var zip = ZipFile.Open(badZip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../escaped.txt").Open())) writer.Write("blocked");
            try { AppUpdater.Extract(badZip, Path.Combine(root, "extracted")); throw new Exception("Zip traversal accepted"); }
            catch (InvalidDataException) { }
            if (File.Exists(Path.Combine(root, "escaped.txt"))) throw new Exception("Zip traversal wrote a file");
            foreach (var fail in new[] { false, true })
            {
                var parent = Path.Combine(root, fail ? "rollback" : "success");
                var target = Path.Combine(parent, "Unictrl"); var work = Path.Combine(parent, "work"); var source = Path.Combine(work, "Unictrl-new");
                Directory.CreateDirectory(Path.Combine(target, "Data")); Directory.CreateDirectory(source);
                File.WriteAllText(Path.Combine(target, "Data", "settings.json"), "preserved-pairing-test-only");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "whoami.exe"), Path.Combine(target, "Unictrl.exe"));
                File.WriteAllText(Path.Combine(target, "version"), "old");
                File.WriteAllText(Path.Combine(source, "version"), "new");
                if (!fail) File.Copy(Path.Combine(target, "Unictrl.exe"), Path.Combine(source, "Unictrl.exe"));
                var config = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { OldPid = int.MaxValue, Target = target, Source = source, Work = work })));
                var command = "$cfg=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + config + "'))|ConvertFrom-Json; & {" + AppUpdater.Helper + "} -OldPid $cfg.OldPid -Target $cfg.Target -Source $cfg.Source -Work $cfg.Work";
                var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(command)) }) start.ArgumentList.Add(argument);
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(45000)) { process.Kill(true); throw new Exception("Updater helper timed out: " + stderr.GetAwaiter().GetResult()); }
                if (process.ExitCode != (fail ? 1 : 0)) throw new Exception("Updater helper exit: " + stderr.GetAwaiter().GetResult());
                if (File.ReadAllText(Path.Combine(target, "Data", "settings.json")) != "preserved-pairing-test-only" ||
                    File.ReadAllText(Path.Combine(target, "version")) != (fail ? "old" : "new")) throw new Exception("Update transaction lost state");
            }
            Log.Write("update_test", "passed portable_data_preserved rollback zip_traversal_rejected");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
