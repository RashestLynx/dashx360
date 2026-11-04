// Combines multi-disc Xbox 360 games into one playable folder for Xenia, the way a console
// ends up with everything on its hard drive:
//   * every disc's files are copied into <games>\Combined\<game>\ (conflicts go to the play disc,
//     i.e. the disc that carries no install packages);
//   * install packages (\Content\0000000000000000\<title>\<type>\<package>) are unpacked into
//     Xenia's content storage, as the console installs them, with every block checked against
//     the package's SHA-1 hash tables.
//
//   dotnet run -- <games folder> <xenia folder> [--dry-run] [--only <title id>]
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

if (args.Length < 2)
{
	Console.WriteLine("usage: DiscMerger <games folder> <xenia folder> [--dry-run]");
	return 1;
}
string gamesFolder = args[0];
string xeniaFolder = args[1];
bool dryRun = args.Contains("--dry-run");
int onlyIndex = Array.IndexOf(args, "--only");
string? only = onlyIndex >= 0 && onlyIndex + 1 < args.Length ? args[onlyIndex + 1] : null;
string combinedRoot = Path.Combine(gamesFolder, "Combined");

var discs = Directory.GetFiles(gamesFolder, "*.iso")
	.Select(path => new { Path = path, Disc = Disc.TryOpen(path) })
	.Where(d => d.Disc != null)
	.Select(d => d.Disc!)
	.ToList();

int failures = 0;
foreach (var group in discs.Where(d => d.TitleId != null).GroupBy(d => d.TitleId!).Where(g => g.Count() > 1).OrderBy(g => g.First().Name))
{
	var set = group.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
	Console.WriteLine($"=== {group.Key}");
	foreach (var disc in set)
	{
		Console.WriteLine($"    {(disc.Packages.Count > 0 ? $"install x{disc.Packages.Count}" : "play     ")}  {disc.Name}");
	}
	// Play discs (no install packages) win file conflicts; install discs only fill gaps.
	var ordered = set.OrderBy(d => d.Packages.Count > 0 ? 1 : 0).ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
	var winners = new Dictionary<string, (Disc disc, Disc.Entry entry)>(StringComparer.OrdinalIgnoreCase);
	var conflicts = new List<string>();
	foreach (var disc in ordered)
	{
		foreach (var (path, entry) in disc.Files)
		{
			if (IsSkipped(path))
			{
				continue;
			}
			if (winners.TryGetValue(path, out var existing))
			{
				if (existing.entry.Size != entry.Size)
				{
					conflicts.Add($"{path} ({existing.disc.ShortTag} vs {disc.ShortTag})");
				}
				continue;
			}
			winners[path] = (disc, entry);
		}
	}
	if (only != null && !string.Equals(group.Key, only, StringComparison.OrdinalIgnoreCase))
	{
		continue;
	}
	// Discs that only carry packages (DLC/bonus discs share a generic installer title) are not a game.
	if (set.All(d => d.Packages.Count > 0))
	{
		Console.WriteLine("    SKIP: install/bonus discs only, no game disc");
		continue;
	}
	bool anyInstallDisc = set.Any(d => d.Packages.Count > 0);
	if (conflicts.Count > 0 && !anyInstallDisc)
	{
		Console.WriteLine($"    SKIP: discs swap mid-game ({conflicts.Count} conflicting files, e.g. {conflicts[0]})");
		continue;
	}
	var playDisc = ordered[0];
	if (!winners.ContainsKey("default.xex"))
	{
		Console.WriteLine("    SKIP: no default.xex on any disc");
		continue;
	}
	string folder = Path.Combine(combinedRoot, playDisc.BaseName);
	long bytes = winners.Values.Sum(w => (long)w.entry.Size);
	var packages = set.SelectMany(d => d.Packages.Select(p => (disc: d, package: p))).ToList();
	Console.WriteLine($"    -> {folder}  ({winners.Count} files, {bytes / 1e9:F1} GB, {conflicts.Count} conflicts resolved to the play disc)");
	foreach (var (disc, package) in packages)
	{
		Console.WriteLine($"    -> install {package.ContentType}\\{package.Name} ({package.Size / 1e9:F2} GB) from {disc.ShortTag}");
	}
	if (dryRun)
	{
		continue;
	}
	try
	{
		foreach (var (path, (disc, entry)) in winners)
		{
			disc.CopyFile(entry, Path.Combine(folder, path));
		}
		foreach (var (disc, package) in packages)
		{
			string packageFile = Path.Combine(Path.GetTempPath(), "discmerger-" + package.Name);
			disc.CopyFile(package.Entry, packageFile);
			try
			{
				Stfs.Install(packageFile, xeniaFolder, package.TitleId, package.ContentType, package.Name);
			}
			finally
			{
				File.Delete(packageFile);
			}
		}
		File.WriteAllText(Path.Combine(folder, "combined-from.txt"), string.Join(Environment.NewLine, set.Select(d => d.Name)) + Environment.NewLine);
		Console.WriteLine("    done");
	}
	catch (Exception ex)
	{
		failures++;
		Console.WriteLine("    FAILED: " + ex.Message);
	}
}
return failures == 0 ? 0 : 2;

// $SystemUpdate is console firmware; install packages are unpacked into Xenia instead of copied.
static bool IsSkipped(string path) => path.StartsWith("$SystemUpdate\\", StringComparison.OrdinalIgnoreCase) || path.StartsWith("Content\\", StringComparison.OrdinalIgnoreCase);

sealed class Disc
{
	public sealed record Entry(uint Sector, uint Size);

	public sealed record Package(string TitleId, string ContentType, string Name, Entry Entry)
	{
		public long Size => Entry.Size;
	}

	private const int SectorSize = 0x800;

	private static readonly long[] PartitionOffsets = { 0L, 0x2080000L, 0xFD90000L, 0x18300000L };

	private static readonly Regex DiscTag = new(@"\s\((Disc \d+|Install Disc|Game Disc|Play Disc)\).*$", RegexOptions.IgnoreCase);

	private readonly long _partition;

	public string Path { get; }

	public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

	public string BaseName => DiscTag.Replace(Name, string.Empty).Trim();

	public string ShortTag => DiscTag.Match(Name) is { Success: true } m ? m.Groups[1].Value : Name;

	public string? TitleId { get; }

	public Dictionary<string, Entry> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

	public List<Package> Packages { get; } = new();

	private Disc(string path, long partition, uint rootSector, uint rootSize, FileStream stream)
	{
		Path = path;
		_partition = partition;
		Walk(stream, rootSector, rootSize, string.Empty);
		if (Files.TryGetValue("default.xex", out var xex))
		{
			TitleId = ReadXexTitleId(Read(stream, xex.Sector, (int)Math.Min(xex.Size, 0x10000u)));
		}
		foreach (var (file, entry) in Files)
		{
			string[] parts = file.Split('\\');
			if (parts.Length == 5 && parts[0].Equals("Content", StringComparison.OrdinalIgnoreCase) && parts[1] == "0000000000000000")
			{
				Packages.Add(new Package(parts[2].ToUpperInvariant(), parts[3].ToUpperInvariant(), parts[4], entry));
			}
		}
		// Some install discs carry only a launcher xex of their own; group them by their packages' title.
		TitleId ??= Packages.FirstOrDefault()?.TitleId;
	}

	public static Disc? TryOpen(string path)
	{
		using var stream = File.OpenRead(path);
		foreach (long partition in PartitionOffsets)
		{
			if (partition + 33 * SectorSize > stream.Length)
			{
				continue;
			}
			byte[] descriptor = new byte[28];
			stream.Seek(partition + 32 * SectorSize, SeekOrigin.Begin);
			stream.ReadExactly(descriptor);
			if (Encoding.ASCII.GetString(descriptor, 0, 20) != "MICROSOFT*XBOX*MEDIA")
			{
				continue;
			}
			return new Disc(path, partition, BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(20)), BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(24)), stream);
		}
		return null;
	}

	public void CopyFile(Entry entry, string destination)
	{
		if (File.Exists(destination) && new FileInfo(destination).Length == entry.Size)
		{
			return;
		}
		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(destination)!);
		string temp = destination + ".partial";
		using (var input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
		using (var output = File.Create(temp))
		{
			input.Seek(_partition + entry.Sector * (long)SectorSize, SeekOrigin.Begin);
			byte[] buffer = new byte[4 << 20];
			long remaining = entry.Size;
			while (remaining > 0)
			{
				int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
				if (read <= 0)
				{
					throw new EndOfStreamException(destination);
				}
				output.Write(buffer, 0, read);
				remaining -= read;
			}
		}
		File.Move(temp, destination, overwrite: true);
	}

	private byte[] Read(FileStream stream, uint sector, int count)
	{
		byte[] buffer = new byte[count];
		stream.Seek(_partition + sector * (long)SectorSize, SeekOrigin.Begin);
		stream.ReadExactly(buffer);
		return buffer;
	}

	// XDVDFS directory entries are 4-byte aligned; 0xFF padding fills the rest of a sector.
	private void Walk(FileStream stream, uint sector, uint size, string prefix)
	{
		if (size == 0)
		{
			return;
		}
		byte[] directory = Read(stream, sector, (int)size);
		int offset = 0;
		while (offset + 14 <= directory.Length)
		{
			if (directory[offset] == 0xFF && directory[offset + 1] == 0xFF)
			{
				offset = (offset / SectorSize + 1) * SectorSize;
				continue;
			}
			int nameLength = directory[offset + 13];
			if (nameLength == 0 || offset + 14 + nameLength > directory.Length)
			{
				break;
			}
			string name = (prefix.Length > 0 ? prefix + "\\" : string.Empty) + Encoding.ASCII.GetString(directory, offset + 14, nameLength);
			uint entrySector = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 4));
			uint entrySize = BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 8));
			if ((directory[offset + 12] & 0x10) != 0)
			{
				Walk(stream, entrySector, entrySize, name);
			}
			else
			{
				Files[name] = new Entry(entrySector, entrySize);
			}
			offset += (14 + nameLength + 3) & ~3;
		}
	}

	private static string? ReadXexTitleId(byte[] xex)
	{
		if (xex.Length < 24 || Encoding.ASCII.GetString(xex, 0, 4) != "XEX2")
		{
			return null;
		}
		uint headerCount = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(20));
		for (int i = 0; i < headerCount && 24 + i * 8 + 8 <= xex.Length; i++)
		{
			int entry = 24 + i * 8;
			if (BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry)) != 0x40006)
			{
				continue;
			}
			int info = (int)BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry + 4));
			return info + 16 > xex.Length ? null : BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(info + 12)).ToString("X8");
		}
		return null;
	}
}

// Unpacks an STFS (CON/LIVE/PIRS) package into Xenia's content layout:
//   content\0000000000000000\<title>\<type>\<package>\...  and  ...\<title>\Headers\<type>\<package>.header
static class Stfs
{
	private const int BlockSize = 0x1000;

	public static void Install(string packagePath, string xeniaFolder, string titleId, string contentType, string packageName)
	{
		using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
		byte[] header = new byte[0x971A];
		stream.ReadExactly(header);
		string magic = Encoding.ASCII.GetString(header, 0, 4);
		if (magic != "LIVE" && magic != "PIRS" && magic != "CON ")
		{
			throw new InvalidDataException($"{packageName} is not an STFS package ({magic})");
		}
		uint headerSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x340));
		byte separation = header[0x37B];
		ushort tableBlocks = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x37C));
		int tableStart = header[0x37E] | header[0x37F] << 8 | header[0x380] << 16;
		uint allocated = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x395));
		var reader = new BlockReader(stream, headerSize, separation, allocated);

		// The file table may itself be fragmented; follow its chain through the hash tables.
		var tableBytes = new MemoryStream();
		int block = tableStart;
		for (int i = 0; i < tableBlocks; i++)
		{
			tableBytes.Write(reader.ReadVerified(block));
			block = reader.NextBlock(block);
		}
		byte[] table = tableBytes.ToArray();
		var entries = new List<(string name, bool dir, bool contiguous, int blocks, int start, short parent, uint size)>();
		for (int i = 0; i + 0x40 <= table.Length; i += 0x40)
		{
			int nameLength = table[i + 0x28] & 0x3F;
			if (nameLength == 0)
			{
				break;
			}
			entries.Add((Encoding.Latin1.GetString(table, i, nameLength), (table[i + 0x28] & 0x80) != 0, (table[i + 0x28] & 0x40) != 0,
				table[i + 0x29] | table[i + 0x2A] << 8 | table[i + 0x2B] << 16, table[i + 0x2F] | table[i + 0x30] << 8 | table[i + 0x31] << 16,
				BinaryPrimitives.ReadInt16BigEndian(table.AsSpan(i + 0x32)), BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i + 0x34))));
		}

		string titleFolder = Path.Combine(xeniaFolder, "content", "0000000000000000", titleId);
		string root = Path.Combine(titleFolder, contentType, packageName);
		string Resolve(int index) => entries[index].parent < 0 ? Path.Combine(root, entries[index].name) : Path.Combine(Resolve(entries[index].parent), entries[index].name);
		for (int i = 0; i < entries.Count; i++)
		{
			var e = entries[i];
			string target = Resolve(i);
			if (e.dir)
			{
				Directory.CreateDirectory(target);
				continue;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(target)!);
			using var output = File.Create(target);
			long remaining = e.size;
			int current = e.start;
			for (int n = 0; n < e.blocks && remaining > 0; n++)
			{
				byte[] data = reader.ReadVerified(current);
				int take = (int)Math.Min(BlockSize, remaining);
				output.Write(data, 0, take);
				remaining -= take;
				current = e.contiguous ? current + 1 : reader.NextBlock(current);
			}
			if (remaining != 0)
			{
				throw new InvalidDataException($"{packageName}: {e.name} ended early");
			}
		}

		// Xenia's per-package record: device, type, display name (UTF-16BE), file name, title id.
		byte[] record = new byte[0x148];
		BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0), 1);
		BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), Convert.ToUInt32(contentType, 16));
		Array.Copy(header, 0x411, record, 8, 0x80 * 2 - 2);
		byte[] fileName = Encoding.ASCII.GetBytes(packageName);
		Array.Copy(fileName, 0, record, 0x108, Math.Min(fileName.Length, 42));
		BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x140), Convert.ToUInt32(titleId, 16));
		string headerFolder = Path.Combine(titleFolder, "Headers", contentType);
		Directory.CreateDirectory(headerFolder);
		File.WriteAllBytes(Path.Combine(headerFolder, packageName + ".header"), record);
		Console.WriteLine($"       installed {entries.Count(e => !e.dir)} files, {reader.Verified} blocks verified");
	}

	private sealed class BlockReader
	{
		private const int PerTable = 0xAA;

		private readonly FileStream _stream;

		private readonly long _base;

		private readonly int _shift;

		private readonly byte _separation;

		private readonly uint _allocated;

		private readonly Dictionary<long, byte[]> _tables = new();

		public long Verified { get; private set; }

		public BlockReader(FileStream stream, uint headerSize, byte separation, uint allocated)
		{
			_stream = stream;
			_base = (headerSize + 0xFFF) & 0xF000;
			_separation = separation;
			_shift = (separation & 1) != 0 ? 0 : 1;
			_allocated = allocated;
		}

		// Hash tables sit in front of the blocks they cover; one table per level per 170^level blocks
		// (two copies each in read-write packages).
		private long DataBlock(long b)
		{
			long adjust = 0;
			if (b >= PerTable)
			{
				adjust += (b / PerTable + 1) << _shift;
			}
			if (b >= PerTable * PerTable)
			{
				adjust += (b / (PerTable * PerTable) + 1) << _shift;
			}
			return b + adjust + (1L << _shift);
		}

		private long LevelTable(long b, int level)
		{
			long step0 = _shift == 1 ? 0xAC : 0xAB;
			long step1 = _shift == 1 ? 0x723A : 0x718F;
			if (level == 0)
			{
				if (b < PerTable)
				{
					return 0;
				}
				long n = b / PerTable * step0 + ((b / (PerTable * PerTable) + 1) << _shift);
				return b / (PerTable * PerTable) == 0 ? n : n + (1L << _shift);
			}
			if (level == 1)
			{
				return b < PerTable * PerTable ? step0 : (1L << _shift) + b / (PerTable * PerTable) * step1;
			}
			return step1;
		}

		private byte[] ReadPhysical(long physical)
		{
			byte[] buffer = new byte[BlockSize];
			_stream.Seek(_base + physical * BlockSize, SeekOrigin.Begin);
			_stream.ReadExactly(buffer);
			return buffer;
		}

		private byte[] Table(long physical)
		{
			if (!_tables.TryGetValue(physical, out var table))
			{
				table = ReadPhysical(physical);
				if (_tables.Count > 4096)
				{
					_tables.Clear();
				}
				_tables[physical] = table;
			}
			return table;
		}

		// Which of the two table copies is live is recorded one level up (or in the header at the top).
		private long ActiveTable(long b, int level)
		{
			long table = LevelTable(b, level);
			if (_shift == 0)
			{
				return table;
			}
			int top = _allocated <= PerTable ? 0 : _allocated <= PerTable * PerTable ? 1 : 2;
			if (level == top)
			{
				return table + ((_separation & 2) != 0 ? 1 : 0);
			}
			long perEntry = level == 0 ? PerTable : PerTable * PerTable;
			byte[] parent = Table(ActiveTable(b, level + 1));
			int index = (int)(b / perEntry % PerTable);
			return table + ((parent[index * 0x18 + 0x14] & 0x40) != 0 ? 1 : 0);
		}

		private ReadOnlySpan<byte> HashEntry(long b)
		{
			byte[] table = Table(ActiveTable(b, 0));
			return table.AsSpan((int)(b % PerTable) * 0x18, 0x18);
		}

		public byte[] ReadVerified(long b)
		{
			byte[] data = ReadPhysical(DataBlock(b));
			if (!SHA1.HashData(data).AsSpan().SequenceEqual(HashEntry(b)[..20]))
			{
				throw new InvalidDataException($"block {b} failed its SHA-1 check");
			}
			Verified++;
			return data;
		}

		public int NextBlock(long b)
		{
			var entry = HashEntry(b);
			return entry[0x15] << 16 | entry[0x16] << 8 | entry[0x17];
		}
	}
}
