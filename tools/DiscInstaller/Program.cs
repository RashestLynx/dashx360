// Installs the install/add-on content carried on Xbox 360 discs into Xenia, the way a console copies
// an install disc, content disc or add-on pack to its hard drive. Afterwards only the game's main
// disc is needed (DashX360 hides the discs whose content is installed), and nothing is copied twice.
//
// Packages live on discs under \Content\0000000000000000\...\<package>. Each one is unpacked into
//   <xenia>\content\0000000000000000\<title>\<type>\<package>\  (+ Headers\<type>\<package>.header)
// using the title and content type from the package's own header, straight out of the ISO, with
// every block checked against the package's SHA-1 hash tables.
//
//   dotnet run -- <games folder> <xenia folder> [--dry-run]
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

if (args.Length < 2)
{
	Console.WriteLine("usage: DiscInstaller <games folder> <xenia folder> [--dry-run]");
	return 1;
}
string gamesFolder = args[0];
string xeniaFolder = args[1];
bool dryRun = args.Contains("--dry-run");

var discs = Directory.GetFiles(gamesFolder, "*.iso").OrderBy(p => p, StringComparer.OrdinalIgnoreCase).Select(Disc.TryOpen).OfType<Disc>().ToList();
// Content is only useful for games that are actually here (a bonus disc can carry DLC for a game you don't have).
var games = discs.Select(d => d.TitleId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
// Launcher discs (Minecraft, Telltale Batman) carry the game itself as an arcade package.
games.UnionWith(discs.SelectMany(d => d.Packages.Select(p => Stfs.ReadHeader(d, p))).Where(h => h?.ContentType == "000D0000").Select(h => h!.TitleId));
int installed = 0, present = 0, failures = 0;
foreach (Disc disc in discs)
{
	if (disc.Packages.Count == 0)
	{
		continue;
	}
	Console.WriteLine($"=== {Path.GetFileNameWithoutExtension(disc.Path)}");
	foreach (Disc.Package package in disc.Packages)
	{
		Stfs.Header? header = Stfs.ReadHeader(disc, package);
		if (header == null)
		{
			Console.WriteLine($"    skip {package.Name}: not an STFS package");
			continue;
		}
		// 00000002 is install/add-on content; launcher-disc arcade games (000D0000) are booted by DashX360 itself.
		if (header.ContentType != "00000002")
		{
			Console.WriteLine($"    skip {header.ContentType}\\{package.Name} \"{header.DisplayName}\"");
			continue;
		}
		if (!games.Contains(header.TitleId))
		{
			Console.WriteLine($"    skip {header.TitleId}\\{package.Name} \"{header.DisplayName}\": that game isn't in the folder");
			continue;
		}
		bool isInstalled = Stfs.IsInstalled(xeniaFolder, header, package.Name);
		Console.WriteLine($"    {(isInstalled ? "installed " : "install   ")} {header.TitleId}\\{header.ContentType}\\{package.Name}  {package.Entry.Size / 1e9:F2} GB  \"{header.DisplayName}\"");
		if (isInstalled)
		{
			present++;
			continue;
		}
		if (dryRun)
		{
			continue;
		}
		try
		{
			Stfs.Install(disc, package, header, xeniaFolder);
			installed++;
		}
		catch (Exception ex)
		{
			failures++;
			Console.WriteLine($"    FAILED: {ex.Message}");
		}
	}
}
Console.WriteLine($"done: {installed} installed, {present} already installed, {failures} failed");
return failures == 0 ? 0 : 2;

sealed class Disc
{
	public sealed record Entry(uint Sector, uint Size);

	public sealed record Package(string Name, Entry Entry);

	private const int SectorSize = 0x800;

	private static readonly long[] PartitionOffsets = { 0L, 0x2080000L, 0xFD90000L, 0x18300000L };

	public string Path { get; }

	public List<Package> Packages { get; } = new();

	public string? TitleId { get; }

	private readonly long _partition;

	private Disc(string path, long partition, uint rootSector, uint rootSize, FileStream stream)
	{
		Path = path;
		_partition = partition;
		var files = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
		Walk(stream, rootSector, rootSize, string.Empty, files);
		foreach (var (file, entry) in files)
		{
			string[] parts = file.Split('\\');
			if (parts.Length >= 4 && parts[0].Equals("Content", StringComparison.OrdinalIgnoreCase))
			{
				Packages.Add(new Package(parts[^1], entry));
			}
		}
		if (files.TryGetValue("default.xex", out var xex))
		{
			TitleId = ReadXexTitleId(Read(stream, xex.Sector, (int)Math.Min(xex.Size, 0x10000u)));
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
			// Execution info: media id, version, base version, title id.
			int info = (int)BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry + 4));
			return info + 16 > xex.Length ? null : BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(info + 12)).ToString("X8");
		}
		return null;
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

	public long Offset(Entry entry) => _partition + entry.Sector * (long)SectorSize;

	private byte[] Read(FileStream stream, uint sector, int count)
	{
		byte[] buffer = new byte[count];
		stream.Seek(_partition + sector * (long)SectorSize, SeekOrigin.Begin);
		stream.ReadExactly(buffer);
		return buffer;
	}

	// XDVDFS directory entries are 4-byte aligned; 0xFF padding fills the rest of a sector.
	private void Walk(FileStream stream, uint sector, uint size, string prefix, Dictionary<string, Entry> files)
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
				Walk(stream, entrySector, entrySize, name, files);
			}
			else
			{
				files[name] = new Entry(entrySector, entrySize);
			}
			offset += (14 + nameLength + 3) & ~3;
		}
	}
}

// Unpacks an STFS (CON/LIVE/PIRS) package into Xenia's content layout.
static class Stfs
{
	private const int BlockSize = 0x1000;

	public sealed record Header(string TitleId, string ContentType, string DisplayName, byte[] Bytes);

	public static Header? ReadHeader(Disc disc, Disc.Package package)
	{
		if (package.Entry.Size < 0x971A)
		{
			return null;
		}
		using var stream = File.OpenRead(disc.Path);
		byte[] header = new byte[0x971A];
		stream.Seek(disc.Offset(package.Entry), SeekOrigin.Begin);
		stream.ReadExactly(header);
		string magic = Encoding.ASCII.GetString(header, 0, 4);
		if (magic != "LIVE" && magic != "PIRS" && magic != "CON ")
		{
			return null;
		}
		string displayName = Encoding.BigEndianUnicode.GetString(header, 0x411, 0x80).TrimEnd('\0');
		return new Header(BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x360)).ToString("X8"), BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x344)).ToString("X8"), displayName, header);
	}

	private static string TitleFolder(string xeniaFolder, Header header) => Path.Combine(xeniaFolder, "content", "0000000000000000", header.TitleId);

	// The .header record is written last, so its presence means the package finished installing.
	public static bool IsInstalled(string xeniaFolder, Header header, string packageName) =>
		File.Exists(Path.Combine(TitleFolder(xeniaFolder, header), "Headers", header.ContentType, packageName + ".header"));

	public static void Install(Disc disc, Disc.Package package, Header header, string xeniaFolder)
	{
		using var stream = new FileStream(disc.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
		byte[] h = header.Bytes;
		uint headerSize = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x340));
		byte separation = h[0x37B];
		ushort tableBlocks = BinaryPrimitives.ReadUInt16LittleEndian(h.AsSpan(0x37C));
		int tableStart = h[0x37E] | h[0x37F] << 8 | h[0x380] << 16;
		uint allocated = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x395));
		var reader = new BlockReader(stream, disc.Offset(package.Entry), headerSize, separation, allocated);

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

		string titleFolder = TitleFolder(xeniaFolder, header);
		string root = Path.Combine(titleFolder, header.ContentType, package.Name);
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
				throw new InvalidDataException($"{package.Name}: {e.name} ended early");
			}
		}

		// Xenia's per-package record: device, type, display name (UTF-16BE), file name, title id.
		byte[] record = new byte[0x148];
		BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0), 1);
		BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(4), Convert.ToUInt32(header.ContentType, 16));
		Array.Copy(h, 0x411, record, 8, 0x80 * 2 - 2);
		byte[] fileName = Encoding.ASCII.GetBytes(package.Name);
		Array.Copy(fileName, 0, record, 0x108, Math.Min(fileName.Length, 42));
		BinaryPrimitives.WriteUInt32BigEndian(record.AsSpan(0x140), Convert.ToUInt32(header.TitleId, 16));
		string headerFolder = Path.Combine(titleFolder, "Headers", header.ContentType);
		Directory.CreateDirectory(headerFolder);
		File.WriteAllBytes(Path.Combine(headerFolder, package.Name + ".header"), record);
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

		public BlockReader(FileStream stream, long packageOffset, uint headerSize, byte separation, uint allocated)
		{
			_stream = stream;
			_base = packageOffset + ((headerSize + 0xFFF) & 0xF000);
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
