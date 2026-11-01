using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Compressr.Core;

public sealed record DvdImageFile(string Name, long Offset, long Length, byte[] Hash);

// Keep the image locked against replacement from inspection through final read-back.
public sealed class PreparedDvd : IDisposable
{
    private readonly FileStream image;
    public string Path { get; }
    public long Length => image.Length;
    public IReadOnlyList<DvdImageFile> Files { get; private set; } = [];

    private PreparedDvd(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        image = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    public static async Task<PreparedDvd> OpenAsync(string path, IProgress<StageProgress> progress, CancellationToken token)
    {
        var dvd = new PreparedDvd(path);
        try { await dvd.InspectAsync(progress, token); return dvd; }
        catch { dvd.Dispose(); throw; }
    }

    public static void CheckCapacity(long imageBytes, long availableBytes)
    {
        if (imageBytes <= 0 || imageBytes % 2048 != 0 || imageBytes > DvdPlan.DiscCapacity(DiscSize.DualLayer))
            throw new InvalidDataException("The image is empty, truncated or larger than a supported DVD.");
        if (imageBytes > availableBytes)
            throw new InvalidOperationException("The prepared DVD image is larger than the inserted DVD's available capacity. Insert a larger compatible blank DVD.");
    }

    private byte[] Read(long offset, int length)
    {
        if (offset < 0 || length < 0 || offset > image.Length - length)
            throw new InvalidDataException("The DVD image contains a truncated or invalid file extent.");
        image.Position = offset;
        byte[] bytes = new byte[length];
        image.ReadExactly(bytes);
        return bytes;
    }

    private static uint Both32(byte[] bytes, int offset)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
        if (value != BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4, 4)))
            throw new InvalidDataException("The ISO directory contains inconsistent sizes.");
        return value;
    }

    private static void CheckUdfTag(byte[] block, long sector)
    {
        int checksum = 0;
        for (int i = 0; i < 16; i++) if (i != 4) checksum += block[i];
        int length = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(10, 2));
        if ((byte)checksum != block[4] || length > block.Length - 16 ||
            BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(12, 4)) != sector)
            throw new InvalidDataException("Damaged UDF descriptor.");
        ushort crc = 0;
        foreach (byte value in block.AsSpan(16, length))
        {
            crc ^= (ushort)(value << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
        }
        if (crc != BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(8, 2)))
            throw new InvalidDataException("UDF descriptor checksum failed.");
    }

    private sealed record Entry(string Name, long Offset, long Length, bool Directory);
    private Entry EntryAt(byte[] bytes, int offset)
    {
        int size = bytes[offset];
        if (size < 34 || offset + size > bytes.Length || bytes[offset + 32] > size - 33 || bytes[offset + 1] != 0 ||
            bytes[offset + 26] != 0 || bytes[offset + 27] != 0 || (bytes[offset + 25] & ~2) != 0)
            throw new InvalidDataException("Unsupported or damaged ISO directory record.");
        long start = Both32(bytes, offset + 2) * 2048L, length = Both32(bytes, offset + 10);
        if (length == 0 || start < 2048 * 16 || start > image.Length - length)
            throw new InvalidDataException("The ISO file extent is empty or outside the image.");
        return new(Encoding.ASCII.GetString(bytes, offset + 33, bytes[offset + 32]).Replace(";1", ""),
            start, length, (bytes[offset + 25] & 2) != 0);
    }

    private List<Entry> Directory(Entry directory)
    {
        if (!directory.Directory || directory.Length > 1024 * 1024)
            throw new InvalidDataException("Invalid DVD directory.");
        byte[] data = Read(directory.Offset, (int)directory.Length);
        var entries = new List<Entry>();
        for (int i = 0; i < data.Length;)
        {
            if (data[i] == 0) { i = (i / 2048 + 1) * 2048; continue; }
            var entry = EntryAt(data, i);
            if (entry.Name is not ("\0" or "\u0001")) entries.Add(entry);
            i += data[i];
        }
        if (entries.Select(e => e.Name).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new InvalidDataException("Duplicate DVD filenames.");
        return entries;
    }

    private async Task InspectAsync(IProgress<StageProgress> progress, CancellationToken token)
    {
        CheckCapacity(Length, DvdPlan.DiscCapacity(DiscSize.DualLayer));
        progress.Report(new("Inspect ISO", null, "Validating DVD navigation, UDF/ISO structure and reading all DVD files…"));
        byte[] pvd = Read(16 * 2048, 2048);
        if (pvd[0] != 1 || Encoding.ASCII.GetString(pvd, 1, 5) != "CD001" || pvd[6] != 1 ||
            Both32(pvd, 80) * 2048L != Length || BinaryPrimitives.ReadUInt16LittleEndian(pvd.AsSpan(128, 2)) != 2048 ||
            BinaryPrimitives.ReadUInt16BigEndian(pvd.AsSpan(130, 2)) != 2048)
            throw new InvalidDataException("This is not a complete supported DVD ISO9660 image.");
        bool nsr02 = false, udf102 = false;
        for (int sector = 17; sector < 256; sector++)
        {
            token.ThrowIfCancellationRequested();
            byte[] block = Read(sector * 2048L, 2048);
            nsr02 |= Encoding.ASCII.GetString(block, 1, 5) == "NSR02";
        }
        byte[] anchor = Read(256 * 2048, 2048);
        CheckUdfTag(anchor, 256);
        long descriptors = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(20, 4));
        uint descriptorBytes = BinaryPrimitives.ReadUInt32LittleEndian(anchor.AsSpan(16, 4));
        if (descriptorBytes == 0 || descriptorBytes > 1024 * 1024 || descriptorBytes % 2048 != 0)
            throw new InvalidDataException("Invalid UDF volume descriptor extent.");
        for (long sector = descriptors; sector < descriptors + descriptorBytes / 2048; sector++)
        {
            byte[] block = Read(sector * 2048, 2048);
            CheckUdfTag(block, sector);
            ushort tag = BinaryPrimitives.ReadUInt16LittleEndian(block);
            if (tag == 6 &&
                Encoding.ASCII.GetString(block, 217, 19) == "*OSTA UDF Compliant" &&
                BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(240, 2)) == 0x102 &&
                BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(212, 4)) == 2048)
                udf102 = true;
            if (tag == 8) break;
        }
        if (!nsr02 || !udf102 || BinaryPrimitives.ReadUInt16LittleEndian(anchor) != 2)
            throw new InvalidDataException("The image does not have the DVD-compatible UDF 1.02 layout produced by Compressr.");
        var root = Directory(EntryAt(pvd, 156));
        var video = root.SingleOrDefault(e => e.Name == "VIDEO_TS" && e.Directory)
            ?? throw new InvalidDataException("The image has no uppercase VIDEO_TS directory.");
        var entries = Directory(video);
        string[] navigation = ["VIDEO_TS.IFO", "VIDEO_TS.BUP", "VTS_01_0.IFO", "VTS_01_0.BUP"];
        int vobs = entries.Count(e => System.Text.RegularExpressions.Regex.IsMatch(e.Name, @"^VTS_01_[1-9]\.VOB$"));
        var expected = navigation.Concat(Enumerable.Range(1, vobs).Select(i => $"VTS_01_{i}.VOB")).ToHashSet();
        if (vobs == 0 || entries.Count != expected.Count || entries.Any(e => e.Directory || !expected.Contains(e.Name)))
            throw new InvalidDataException("Use a prepared Compressr DVD ISO: one continuous title, without menus or extra title sets.");
        var files = new List<DvdImageFile>();
        byte[] buffer = new byte[1024 * 1024];
        foreach (var entry in entries)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Length % 2048 != 0)
                throw new InvalidDataException($"Truncated DVD file: {entry.Name}.");
            byte[] header = Read(entry.Offset, 16);
            if (entry.Name.EndsWith(".VOB"))
            {
                if (entry.Length > 1024L * 1024 * 1024 || !header.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 1, 0xba }))
                    throw new InvalidDataException($"Invalid DVD program stream: {entry.Name}.");
            }
            else if (entry.Length < 2048 || Encoding.ASCII.GetString(header, 0, 12) !=
                (entry.Name.StartsWith("VIDEO_TS") ? "DVDVIDEO-VMG" : "DVDVIDEO-VTS"))
                throw new InvalidDataException($"Invalid DVD navigation: {entry.Name}.");
            if (entry.Name == "VTS_01_0.IFO")
            {
                byte[] attributes = Read(entry.Offset + 512, 2);
                if ((attributes[0] >> 6) != 1 || ((attributes[0] >> 4) & 3) > 1 ||
                    ((attributes[0] >> 2) & 3) is not (0 or 3) || ((attributes[1] >> 3) & 7) > 2)
                    throw new InvalidDataException("The title does not declare supported DVD MPEG-2 PAL/NTSC video.");
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            image.Position = entry.Offset;
            long remaining = entry.Length;
            while (remaining > 0)
            {
                int count = (int)Math.Min(buffer.Length, remaining);
                await image.ReadExactlyAsync(buffer.AsMemory(0, count), token);
                if (entry.Name.EndsWith(".VOB"))
                    for (int offset = 0; offset < count; offset += 2048)
                        if (buffer[offset] != 0 || buffer[offset + 1] != 0 || buffer[offset + 2] != 1 || buffer[offset + 3] != 0xba)
                            throw new InvalidDataException($"Damaged DVD program-stream sector: {entry.Name}.");
                hash.AppendData(buffer, 0, count); remaining -= count;
                progress.Report(new("Inspect ISO", (double)image.Position / Length, $"Reading {entry.Name}…"));
            }
            files.Add(new(entry.Name, entry.Offset, entry.Length, hash.GetHashAndReset()));
        }
        foreach (string name in new[] { "VIDEO_TS", "VTS_01_0" })
            if (!files.Single(f => f.Name == name + ".IFO").Hash.SequenceEqual(files.Single(f => f.Name == name + ".BUP").Hash))
                throw new InvalidDataException($"DVD navigation backup does not match: {name}.");
        var extents = files.OrderBy(f => f.Offset).ToArray();
        for (int i = 1; i < extents.Length; i++)
            if (extents[i - 1].Offset + extents[i - 1].Length > extents[i].Offset)
                throw new InvalidDataException("DVD files overlap inside the image.");
        Files = files;
    }

    public async Task VerifyDirectoryAsync(string videoTs, IProgress<StageProgress> progress, CancellationToken token)
    {
        for (int i = 0; i < Files.Count; i++)
        {
            var file = Files[i];
            progress.Report(new("Verify", (double)i / Files.Count, $"Reading back {file.Name}…"));
            await using var copy = File.OpenRead(System.IO.Path.Combine(videoTs, file.Name));
            byte[] hash = await SHA256.HashDataAsync(copy, token);
            if (copy.Length != file.Length || !CryptographicOperations.FixedTimeEquals(file.Hash, hash))
                throw new IOException($"Read-back verification failed for {file.Name}. Do not rely on this disc.");
        }
    }

    public void Dispose() => image.Dispose();
}
