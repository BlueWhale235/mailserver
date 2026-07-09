using System.Security.AccessControl;
using System.Security.Principal;

namespace MailServer.Infrastructure;

internal static class SecurityAssetFiles
{
    public static string GetDomainDirectory(string securityRoot, Guid domainId)
    {
        var root = Path.GetFullPath(securityRoot);
        var directory = Path.GetFullPath(Path.Combine(root, "domains", domainId.ToString("N")));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Security asset path escaped its configured root.");
        return directory;
    }

    public static void RestrictDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return;
        }

        var identity = WindowsIdentity.GetCurrent().User;
        if (identity is null) return;
        var security = new DirectorySecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    public static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return;
        }

        var identity = WindowsIdentity.GetCurrent().User;
        if (identity is null) return;
        var security = new FileSecurity();
        security.SetOwner(identity);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    public static void DeleteManagedFile(string securityRoot, string? path, string? except = null)
    {
        if (string.IsNullOrWhiteSpace(path) || string.Equals(path, except, StringComparison.OrdinalIgnoreCase)) return;
        var root = Path.GetFullPath(securityRoot);
        var candidate = Path.GetFullPath(path);
        if (candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && File.Exists(candidate))
        {
            File.Delete(candidate);
        }
    }
}
