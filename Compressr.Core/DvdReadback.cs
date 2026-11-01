using System.Security.Cryptography;

namespace Compressr.Core;

public sealed class DvdReadbackException : IOException
{
    public string FilePath { get; }
    public long BytesRead { get; }
    public bool ReadFailed { get; }
    public int? WindowsError { get; }

    public DvdReadbackException(string path, long bytesRead, string reason, bool readFailed, Exception? inner = null)
        : base($"DVD verification failed for {path} after {bytesRead:N0} bytes: {reason}\n" +
            "The disc is NOT verified. Keep the prepared ISO. Do not rely on this disc or automatically erase/reburn it.", inner)
    {
        FilePath = path;
        BytesRead = bytesRead;
        ReadFailed = readFailed;
        WindowsError = inner is IOException && (inner.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
            ? inner.HResult & 0xFFFF : null;
    }
}

public static class DvdReadback
{
    public static Task VerifyAsync(IReadOnlyList<DvdImageFile> files, string videoTs,
        IProgress<StageProgress> progress, CancellationToken token, Func<string, Stream>? open = null)
        => Task.Run(async () =>
    {
        long total = files.Sum(f => f.Length), complete = 0;
        byte[] buffer = new byte[1024 * 1024];
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(videoTs, file.Name);
            long read = 0;
            try
            {
                progress.Report(new("Verify", total > 0 ? (double)complete / total : null, $"Reading back {path}…"));
                await using var copy = open != null ? open(path) : new FileStream(path,
                    FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                if (copy.Length != file.Length)
                    throw new DvdReadbackException(path, 0, $"file length is {copy.Length:N0}, expected {file.Length:N0}. The mounted files differ from the prepared image.", false);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (read < file.Length)
                {
                    int count = await copy.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, file.Length - read)), token).ConfigureAwait(false);
                    if (count == 0) throw new DvdReadbackException(path, read, "the file ended before all expected bytes were read.", true);
                    hash.AppendData(buffer, 0, count);
                    read += count;
                    progress.Report(new("Verify", (double)(complete + read) / total,
                        $"Reading back {file.Name} · {read / 1_000_000.0:F1} / {file.Length / 1_000_000.0:F1} MB"));
                }
                if (!CryptographicOperations.FixedTimeEquals(file.Hash, hash.GetHashAndReset()))
                    throw new DvdReadbackException(path, read, "SHA-256 does not match the prepared ISO. The bytes read differ from the image.", false);
                complete += read;
            }
            catch (DvdReadbackException) { throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                bool crc = (ex.HResult & 0xFFFF) == 23;
                string reason = crc
                    ? "Windows reported a cyclic redundancy check (CRC) read error (error 23). The device could not return readable data; this is not a SHA-256 mismatch. Check the disc, drive and connection."
                    : $"Windows could not read the file: {ex.Message} (HRESULT 0x{ex.HResult:X8}). Check the mounted disc, drive and connection.";
                throw new DvdReadbackException(path, read, reason, true, ex);
            }
        }
    }, token);
}
