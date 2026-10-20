using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
namespace Unictrl;
internal static class UpdateSelfTest
{
    public static void Run()
    {
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
                if (!process.WaitForExit(15000)) { process.Kill(); throw new Exception("Updater helper timed out"); }
                if (process.ExitCode != (fail ? 1 : 0)) throw new Exception("Updater helper exit: " + stderr.GetAwaiter().GetResult());
                if (File.ReadAllText(Path.Combine(target, "Data", "settings.json")) != "preserved-pairing-test-only" ||
                    File.ReadAllText(Path.Combine(target, "version")) != (fail ? "old" : "new")) throw new Exception("Update transaction lost state");
            }
            Log.Write("update_test", "passed portable_data_preserved rollback zip_traversal_rejected");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
