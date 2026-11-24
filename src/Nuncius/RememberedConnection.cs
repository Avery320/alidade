using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Torquetum.Nuncius;

internal sealed record RememberedConnection(string ProjectId, string Locator, string Secret, string PairingCodeHash)
{
    private sealed record PublishedProject(int Port, Guid InstanceId, int ProcessId);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string LocalDirectory
    {
        get
        {
            var home = Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME")
                ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!Path.IsPathFullyQualified(home)) throw new NuncConnectionException("The local user directory is unavailable.");
            return Path.Combine(home, ".torquetum", "connections");
        }
    }

    private static string FilePath(Guid connector) => Path.Combine(LocalDirectory, "nuncius", $"{connector:N}.json");

    internal bool Matches(string? code) => string.IsNullOrWhiteSpace(code) || Hash(Encoding.UTF8.GetBytes(code)) == PairingCodeHash;

    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal static RememberedConnection? Load(Guid connector)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<RememberedConnection>(File.ReadAllBytes(FilePath(connector)), JsonOptions);
            return saved != null && !string.IsNullOrWhiteSpace(saved.ProjectId) && IsLocator(saved.Locator)
                && saved.Secret is { Length: 43 } && saved.Secret.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                && IsLocator(saved.PairingCodeHash) ? saved : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    internal void Save(Guid connector)
    {
        var path = FilePath(connector);
        var staged = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            var options = new FileStreamOptions { Access = FileAccess.Write, Mode = FileMode.CreateNew };
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(directory);
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var file = new FileStream(staged, options))
                JsonSerializer.Serialize(file, this, JsonOptions);
            File.Move(staged, path, overwrite: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(staged); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            throw new NuncConnectionException("Nuncius could not save the connection for this user.");
        }
    }

    internal (Uri Endpoint, Guid InstanceId) Locate(Guid? preferredInstance = null)
    {
        var directory = Path.Combine(LocalDirectory, "projects", Locator);
        (Uri Endpoint, Guid InstanceId)? found = null;
        var ambiguous = false;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
            {
                var location = ReadLocation(file);
                if (location == null) continue;
                var target = (new Uri($"http://127.0.0.1:{location.Port}"), location.InstanceId);
                if (location.InstanceId == preferredInstance) return target;
                if (found != null) ambiguous = true;
                found = target;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }

        if (ambiguous) throw new NuncConnectionException("Close the duplicate Torquetum Project before reconnecting.");
        return found ?? throw new NuncConnectionException("Open the remembered Project in Torquetum to reconnect.");
    }

    private static PublishedProject? ReadLocation(string file)
    {
        try
        {
            var location = JsonSerializer.Deserialize<PublishedProject>(File.ReadAllBytes(file), JsonOptions);
            if (location is not { Port: > 0 and <= 65535, ProcessId: > 0 }
                || location.InstanceId == Guid.Empty || Path.GetFileNameWithoutExtension(file) != location.InstanceId.ToString("D")) return null;
            using var process = Process.GetProcessById(location.ProcessId);
            return !process.HasExited && process.StartTime.ToUniversalTime() <= File.GetLastWriteTimeUtc(file) ? location : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static bool IsLocator(string? value) => value is { Length: 64 }
        && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
