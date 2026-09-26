using System.Diagnostics;
using WardogsRadio.Update;

if (!TryReadArguments(args, out var installRoot, out var installer, out var candidateVersion)) return;
var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "Logs");
Directory.CreateDirectory(logDirectory);
void Log(string text) => File.AppendAllText(Path.Combine(logDirectory, "updater.log"), $"{DateTimeOffset.UtcNow:o} {text}{Environment.NewLine}");
try
{
    if (!PackageVerifier.Verify(installRoot, File.ReadAllText(Path.Combine(installRoot, "VERSION")).Trim(), out var preflight)) throw new InvalidOperationException("Current package preflight failed: " + preflight);
    var rollback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "Updates", "rollback-" + Guid.NewGuid().ToString("N"));
    CopyTree(installRoot, rollback); Log("Created rollback package outside the installation directory.");
    var install = Process.Start(new ProcessStartInfo(installer, "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS") { UseShellExecute = false })!;
    install.WaitForExit();
    if (install.ExitCode != 0) throw new InvalidOperationException("Installer exited with " + install.ExitCode + ".");
    if (!PackageVerifier.Verify(installRoot, candidateVersion, out var candidateError)) throw new InvalidOperationException("Candidate package verification failed: " + candidateError);
    if (!RunHealthCheck(installRoot)) throw new InvalidOperationException("Candidate startup health check failed.");
    Directory.Delete(rollback, true); Log("Candidate committed after package and startup verification."); Launch(installRoot);
}
catch (Exception exception)
{
    Log("Update failed: " + exception.Message);
    try
    {
        var parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "Updates");
        var backup = Directory.GetDirectories(parent, "rollback-*").OrderByDescending(Directory.GetCreationTimeUtc).FirstOrDefault();
        if (backup is null) throw new InvalidOperationException("Rollback package was unavailable.");
        CopyTree(backup, installRoot); var previous = File.ReadAllText(Path.Combine(backup, "VERSION")).Trim();
        if (!PackageVerifier.Verify(installRoot, previous, out var rollbackError) || !RunHealthCheck(installRoot)) throw new InvalidOperationException("Rollback verification failed: " + rollbackError);
        Log("Rollback restored v" + previous + "."); Launch(installRoot);
    }
    catch (Exception rollbackFailure) { Log("Rollback failure: " + rollbackFailure.Message); }
}

static bool TryReadArguments(string[] arguments, out string root, out string installer, out string version)
{
    root = installer = version = "";
    for (var index = 0; index + 1 < arguments.Length; index += 2) { if (arguments[index] == "--install-root") root = arguments[index + 1]; if (arguments[index] == "--installer") installer = arguments[index + 1]; if (arguments[index] == "--version") version = arguments[index + 1]; }
    return Directory.Exists(root) && File.Exists(installer) && SemanticVersion.TryParse(version, out _);
}
static bool RunHealthCheck(string root)
{
    var process = Process.Start(new ProcessStartInfo(Path.Combine(root, "WARDOGS Radio.exe"), "--startup-health-check") { UseShellExecute = false, WorkingDirectory = root })!;
    return process.WaitForExit(15000) && process.ExitCode == 0;
}
static void Launch(string root) => Process.Start(new ProcessStartInfo(Path.Combine(root, "WARDOGS Radio.exe")) { UseShellExecute = true, WorkingDirectory = root });
static void CopyTree(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), true);
}
