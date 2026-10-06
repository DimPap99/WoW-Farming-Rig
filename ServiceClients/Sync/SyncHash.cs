using System;
using System.IO;
using System.Security.Cryptography;

namespace ServiceClients.Sync;

/// <summary>
/// The hash a sync manifest carries (<see cref="Contracts.Pathing.SyncFile.Sha256"/>), computed alike
/// by the Pathing Service and the agent: SHA-256 of a file's bytes with every CRLF read as LF, in
/// lower-case hex. A Windows checkout holds the data files with CRLF (<c>* text=auto</c>) while the
/// service may hold them with LF; they are the same file, so a copy is never replaced, or backed up,
/// for its line endings alone.
/// </summary>
public static class SyncHash
{
    public static string Of(ReadOnlySpan<byte> bytes)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        int start = 0;
        while (bytes[start..].IndexOf("\r\n"u8) is int crlf and >= 0)
        {
            // Up to the CR, which is dropped; the LF starts the next piece.
            hash.AppendData(bytes.Slice(start, crlf));
            start += crlf + 1;
        }

        hash.AppendData(bytes[start..]);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public static string OfFile(string path)
    {
        return Of(File.ReadAllBytes(path));
    }
}
