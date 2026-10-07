using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Security.Cryptography;
using System.Text;

namespace Bowl.Host;

internal static class Disk
{
    public const int MaximumRecordBytes = 1024 * 1024;

    public static T? Read<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        Paths.NoLinks(path);
        try
        {
            // FileShare.Delete is essential for the producer's atomic replace on Windows.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete);
            if (stream.Length > MaximumRecordBytes)
                throw new InvalidDataException("Protocol record exceeds 1 MiB.");
            return JsonSerializer.Deserialize(stream, type)
                ?? throw new InvalidDataException("Null protocol record.");
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    public static void Write<T>(string path, T value, JsonTypeInfo<T> type)
    {
        Paths.NoLinks(path);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (bytes.Length > MaximumRecordBytes)
            throw new InvalidDataException("Protocol record exceeds 1 MiB.");
        var temporary = path + ".tmp";
        // The attempt lock guarantees one writer; fixed temp names also bound
        // debris after repeated crashes. Never follow a stale symlink.
        Paths.NoLinks(temporary);
        try
        {
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path))
                File.Replace(temporary, path, null);
            else
                File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void Validate(Envelope record, string attempt)
    {
        if (record.ProtocolVersion != 1 || record.AttemptId != attempt)
            throw new InvalidDataException("Protocol version or attempt identity mismatch.");
    }

    public static FileStream Lock(string directory)
    {
        var path = Path.Combine(directory, "host.lock");
        Paths.NoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static FileStream LockInstallation(string root, string install)
    {
        var canonical = AbsoluteInstallation(install);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        var directory = Path.Combine(root, "locks");
        Paths.NoLinks(directory);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, key + ".lock");
        Paths.NoLinks(path);
        if (!File.Exists(path) && Directory.EnumerateFiles(directory, "*.lock").Take(10_000).Count() >= 10_000)
            throw new InvalidDataException("Installation lock capacity exhausted.");
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    private static string AbsoluteInstallation(string path)
    {
        var canonical = Paths.Absolute(path);
        return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
    }
}

internal static class Paths
{
    private static StringComparison Comparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("An absolute path is required.");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!))
            throw new InvalidDataException("A filesystem root is not a valid working directory.");
        NoLinks(full);
        return full;
    }

    public static bool Contains(string parent, string child) =>
        string.Equals(parent, child, Comparison) ||
        child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, Comparison);

    public static bool Overlaps(string a, string b) => Contains(a, b) || Contains(b, a);

    public static void NoLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null;
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Symbolic links and reparse points are not allowed.");
            }
            catch (FileNotFoundException) { /* The atomic destination may not exist yet. */ }
            catch (DirectoryNotFoundException) { /* Ancestors are still checked. */ }
        }
    }

    public static void Validate(Request request, string root)
    {
        var install = Absolute(request.InstallPath);
        var host = Absolute(AppContext.BaseDirectory);
        if (Overlaps(root, install) || Overlaps(host, install) || Overlaps(host, root))
            throw new InvalidDataException("Host, state and installation must be disjoint.");
        if (request.BackupDirectory is { Length: > 0 } backup)
        {
            backup = Absolute(backup);
            if (Overlaps(root, backup) || Overlaps(host, backup) || Contains(backup, install))
                throw new InvalidDataException("Unsafe backup location.");
            if (Contains(install, backup) &&
                (!Contains(Path.Combine(install, ".backups"), backup) ||
                 string.Equals(backup, Path.Combine(install, ".backups"), Comparison)))
                throw new InvalidDataException("Nested snapshots must be children of .backups.");
        }
        else if (request.AutoRollback)
            throw new InvalidDataException("Automatic rollback requires a snapshot directory.");

        if (request.Updater is null || request.Updater.Pid <= 0 ||
            request.Updater.StartTimeUtc == default || request.CreatedAtUtc == default ||
            request.CreatedAtUtc > DateTimeOffset.UtcNow.AddMinutes(5) ||
            request.LaunchMode is not ("filesOnly" or "processAlive") ||
            request.HealthTimeoutSeconds is < 1 or > 3600 ||
            request.UpdateTimeoutSeconds is < 1 or > 86400 || request.Report is null ||
            request.Report.Type is not (1 or 2))
            throw new InvalidDataException("Invalid request configuration.");
        if (request.Report.Url is { Length: > 0 } url &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
             uri.Scheme is not ("http" or "https") ||
             uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0))
            throw new InvalidDataException("Report URL must be HTTP(S) without embedded credentials.");
    }
}
