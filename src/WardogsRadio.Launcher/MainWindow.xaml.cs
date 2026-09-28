using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WardogsRadio.Update;

namespace WardogsRadio.Launcher;

public partial class MainWindow : Window
{
    readonly string _installRoot = AppContext.BaseDirectory;
    readonly string _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "Logs");
    public MainWindow() => InitializeComponent();

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_logDirectory);
            var localText = File.ReadAllText(Path.Combine(_installRoot, "VERSION")).Trim();
            if (!SemanticVersion.TryParse(localText, out var localVersion)) { Log("Package preflight failed: VERSION is invalid."); Status.Text = "Installation needs repair."; return; }
            if (!PackageVerifier.Verify(_installRoot, localText, out var verificationError)) { Log("Package preflight failed: " + verificationError); Status.Text = "Installation needs repair."; return; }
            Version.Text = "v" + localVersion;
            Log("Launcher started for v" + localVersion);
            // The manifest is small, but the self-contained installer is hundreds
            // of megabytes. Its transfer must not inherit the manifest deadline.
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            string? json = null;
            using (var manifestCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            {
                try { json = await client.GetStringAsync(ReleaseManifestValidator.LatestManifestUrl, manifestCancellation.Token); }
                catch (Exception exception) { Log("Manifest fetch unavailable: " + exception.GetType().Name); }
            }
            var decision = ReleaseManifestValidator.Decide(json is not null, json, localVersion, localVersion, out var manifest, out var reason);
            Log("Update decision: " + reason);
            if (decision == UpdateDecision.LaunchCurrent || manifest is null) { Status.Text = "Up to date"; await LaunchCurrentSoonAsync(); return; }
            Status.Text = "Downloading v" + manifest.Version + "…";
            using var installerCancellation = new CancellationTokenSource(TimeSpan.FromMinutes(30));
            var installer = await DownloadVerifiedInstallerAsync(client, manifest, installerCancellation.Token);
            if (installer is null) { Status.Text = "Update couldn't be completed. Starting WARDOGS Radio."; await LaunchCurrentSoonAsync(); return; }
            Status.Text = "Installing v" + manifest.Version + "…";
            var agentSource = Path.Combine(_installRoot, "WARDOGS Radio Update Agent.exe");
            var handoff = Path.Combine(Path.GetTempPath(), "WARDOGS Radio", "update-" + Guid.NewGuid().ToString("N"));
            CopyPackageForHandoff(_installRoot, handoff);
            var agent = Path.Combine(handoff, Path.GetFileName(agentSource));
            var start = new ProcessStartInfo(agent) { UseShellExecute = false, WorkingDirectory = handoff };
            start.ArgumentList.Add("--install-root"); start.ArgumentList.Add(_installRoot);
            start.ArgumentList.Add("--installer"); start.ArgumentList.Add(installer);
            start.ArgumentList.Add("--version"); start.ArgumentList.Add(manifest.Version.ToString());
            Process.Start(start);
            Log("Verified installer handed to temporary update agent.");
            Close();
        }
        catch (Exception exception)
        {
            Log("Launcher exception: " + exception.GetType().Name);
            Status.Text = "Starting installed WARDOGS Radio.";
            await LaunchCurrentSoonAsync();
        }
    }
    async Task<string?> DownloadVerifiedInstallerAsync(HttpClient client, ReleaseManifest manifest, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "Updates");
        Directory.CreateDirectory(directory);
        var candidate = Path.Combine(directory, manifest.Installer + ".download");
        try
        {
            using var response = await client.GetAsync(manifest.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            var buffer = new byte[81920]; long written = 0; int read;
            // Release the destination handle before hashing and atomically moving
            // the verified installer. On Windows, moving an open FileStream throws
            // IOException and otherwise makes every update fall back to the old app.
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(candidate))
            {
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    written += read;
                    if (total is > 0) Progress.Value = Math.Min(100, written * 100d / total.Value);
                }
                await output.FlushAsync(cancellationToken);
            }
            if (!PackageVerifier.VerifySha256(candidate, manifest.Sha256)) { File.Delete(candidate); Log("Downloaded installer hash verification failed."); return null; }
            var verified = Path.Combine(directory, manifest.Installer);
            File.Move(candidate, verified, true); Log("Installer download and SHA-256 verification succeeded."); return verified;
        }
        catch (Exception exception) { if (File.Exists(candidate)) File.Delete(candidate); Log("Installer download failed: " + exception.GetType().Name + ": " + exception.Message); return null; }
    }
    async Task LaunchCurrentSoonAsync() { await Task.Delay(700); LaunchCurrent(); Close(); }
    void LaunchCurrent() { var app = Path.Combine(_installRoot, "WARDOGS Radio.exe"); if (File.Exists(app)) { Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = _installRoot }); Log("Launch requested for installed application."); } }
    static void CopyPackageForHandoff(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
    }
    void Log(string text) { try { File.AppendAllText(Path.Combine(_logDirectory, "launcher.log"), $"{DateTimeOffset.UtcNow:o} {text}{Environment.NewLine}"); } catch { } }
}
