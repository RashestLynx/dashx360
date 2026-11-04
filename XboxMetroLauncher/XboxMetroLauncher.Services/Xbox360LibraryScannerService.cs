using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using XboxMetroLauncher.Models;
using XboxMetroLauncher.Utilities;

namespace XboxMetroLauncher.Services;

// Finds Xbox 360 disc images and adds them as tiles that boot in Xenia, like a disc on the console.
public sealed class Xbox360LibraryScannerService
{
	public const string PlatformName = "Xbox 360";

	private const int SectorSize = 0x800;

	// XGD partition offsets: plain XDVDFS/XSF, XGD3, XGD2, XGD1.
	private static readonly long[] PartitionOffsets = new long[4] { 0L, 0x2080000L, 0xFD90000L, 0x18300000L };

	private static readonly byte[] MediaMagic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

	// Discs that only install data; the console never shows these as games.
	private static readonly Regex DataOnlyDisc = new Regex("\\((Content Install Disc|Install Disc|Bonus Disc|Bonus Content Disc)\\)|Bonus Content Disc", RegexOptions.IgnoreCase);

	private static readonly HashSet<string> LauncherDiscRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default.xex", "media", "Content", "$SystemUpdate", "$TitleUpdate", "AvatarAssetPack", "nxeart" };

	private static readonly Regex MultiplayerDisc = new Regex("\\((Multiplayer|MP)\\)", RegexOptions.IgnoreCase);

	private static readonly Regex DiscTag =new Regex("\\(Disc (\\d+)\\)(?:\\s*\\(([^)]*)\\))?", RegexOptions.IgnoreCase);

	private static readonly HttpClient HttpClient = new HttpClient
	{
		Timeout = TimeSpan.FromSeconds(20.0)
	};

	public static string ArtFolder => Path.Combine(AppPaths.UserDataFolder, "GameArt", "Xbox360");

	public static string PackageFolder => Path.Combine(AppPaths.UserDataFolder, "Xbox360Packages");

	public async Task<SteamGameScanResult> ScanAsync(GameLibrary library, string xeniaPath, string gamesFolder, CancellationToken cancellationToken = default(CancellationToken))
	{
		if (string.IsNullOrWhiteSpace(xeniaPath) || !File.Exists(xeniaPath))
		{
			return new SteamGameScanResult
			{
				Message = "Xenia was not found. Choose xenia_canary.exe in Settings > Games."
			};
		}
		if (string.IsNullOrWhiteSpace(gamesFolder) || !Directory.Exists(gamesFolder))
		{
			return new SteamGameScanResult
			{
				Message = "The Xbox 360 games folder was not found."
			};
		}
		List<string> imagePaths = Directory.EnumerateFiles(gamesFolder, "*.*", SearchOption.AllDirectories).Where(IsGameImage).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase)
			.ToList();
		Directory.CreateDirectory(ArtFolder);
		string installedContent = Path.Combine(Path.GetDirectoryName(xeniaPath) ?? string.Empty, "content", "0000000000000000");
		List<DiscInfo> discs = new List<DiscInfo>();
		foreach (string path in imagePaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			DiscInfo? disc = await Task.Run(() => InspectDisc(path, installedContent), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (disc != null)
			{
				discs.Add(disc);
			}
		}
		MarkHiddenDiscs(discs);

		int added = 0;
		int updated = 0;
		int skipped = 0;
		// Tiles for discs that are now hidden, or whose file is gone, hand their play history to the game's main tile.
		HashSet<string> visiblePaths = new HashSet<string>(discs.Where((DiscInfo disc) => disc.HiddenReason == null).Select((DiscInfo disc) => disc.Path), StringComparer.OrdinalIgnoreCase);
		List<GameMetadata> retired = library.Games.Where((GameMetadata existing) => string.Equals(existing.Platform, PlatformName, StringComparison.OrdinalIgnoreCase) && !visiblePaths.Contains(existing.InstallPath)).ToList();
		foreach (GameMetadata tile in retired)
		{
			library.Games.Remove(tile);
			updated++;
		}
		foreach (DiscInfo disc in discs)
		{
			cancellationToken.ThrowIfCancellationRequested();
			if (disc.HiddenReason != null)
			{
				skipped++;
				continue;
			}
			string titleId = disc.TitleId ?? string.Empty;
			string launchPath = disc.Path;
			string? packagePath = await Task.Run(() => TryExtractDiscPackage(disc.Path), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (packagePath != null)
			{
				launchPath = packagePath;
				titleId = TryReadPackageTitleId(packagePath) ?? titleId;
			}
			string coverPath = await EnsureBoxArtAsync(titleId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			// A game whose other discs are hidden is just "the game", not "Disc 2 (Play)".
			bool hasHiddenSiblings = discs.Any((DiscInfo other) => other.HiddenReason != null && other.GameTitleIds.Overlaps(disc.GameTitleIds));
			string title = CleanTitle(disc.Name, !hasHiddenSiblings);
			GameMetadata? game = library.Games.FirstOrDefault((GameMetadata existing) => string.Equals(existing.Platform, PlatformName, StringComparison.OrdinalIgnoreCase) && string.Equals(existing.InstallPath, disc.Path, StringComparison.OrdinalIgnoreCase));
			if (game == null)
			{
				game = new GameMetadata
				{
					Title = title,
					Platform = PlatformName,
					Genre = "Xbox 360",
					InstallPath = disc.Path
				};
				library.Games.Add(game);
				added++;
			}
			else
			{
				// Only replace names the scanner made itself, never one the user typed.
				if (string.Equals(game.Title, CleanTitle(disc.Name, includeDisc: true), StringComparison.Ordinal))
				{
					game.Title = title;
				}
				updated++;
			}
			foreach (GameMetadata tile in retired.Where((GameMetadata tile) => string.Equals(BaseTitle(tile.Title), BaseTitle(title), StringComparison.OrdinalIgnoreCase)).ToList())
			{
				game.Playtime += tile.Playtime;
				if (!game.LastPlayed.HasValue || tile.LastPlayed > game.LastPlayed)
				{
					game.LastPlayed = tile.LastPlayed;
				}
				game.IsFavorite |= tile.IsFavorite;
				retired.Remove(tile);
			}
			game.LaunchType = "Exe";
			game.ExecutablePath = xeniaPath;
			game.Arguments = "\"" + launchPath + "\"" + (packagePath == null && disc.BootModule != null ? " --launch_module=\"" + disc.BootModule + "\"" : string.Empty) + " --fullscreen=true";
			game.WorkingDirectory = Path.GetDirectoryName(xeniaPath) ?? string.Empty;
			if (!string.IsNullOrWhiteSpace(coverPath) && (string.IsNullOrWhiteSpace(game.CoverArtPath) || (packagePath != null && game.CoverArtPath.StartsWith(ArtFolder, StringComparison.OrdinalIgnoreCase))))
			{
				game.CoverArtPath = coverPath;
			}
		}
		return new SteamGameScanResult
		{
			Added = added,
			Updated = updated,
			Skipped = skipped,
			Message = ((imagePaths.Count == 0) ? "No Xbox 360 games were found." : $"Xbox 360 scan complete. Added: {added}, Updated: {updated}, Skipped: {skipped}.")
		};
	}

	private sealed class DiscInfo
	{
		public required string Path { get; init; }

		public required string Name { get; init; }

		public string? TitleId { get; init; }

		// Install/add-on packages (content type 00000002) on the disc: their game's title, and whether Xenia has them.
		public List<(string titleId, bool installed)> ContentPackages { get; } = new List<(string, bool)>();

		// The disc boots an arcade package (Minecraft-style launcher disc) rather than a xex.
		public bool BootsPackage { get; set; }

		// For Ubisoft-style disc launchers: the real game xex on the disc.
		public string? BootModule { get; set; }

		// A "multi" folder at the root holds multiplayer data (Ubisoft's AC III/IV multiplayer discs).
		public bool HasMultiplayerData { get; set; }

		public bool IsMultiplayerDisc => (MultiplayerDisc.IsMatch(Name) && !Regex.IsMatch(Name, "single", RegexOptions.IgnoreCase)) || HasMultiplayerData;

		public string? HiddenReason { get; set; }

		// The games this disc belongs to: its own title plus the titles its packages install into.
		public HashSet<string> GameTitleIds { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
	}

	// Like a console's hard drive: once an install/content/add-on disc's packages are installed in
	// Xenia, the game only needs its main disc, so the extra disc stops showing as a separate game.
	private static void MarkHiddenDiscs(List<DiscInfo> discs)
	{
		foreach (DiscInfo disc in discs)
		{
			if (DataOnlyDisc.IsMatch(disc.Name))
			{
				disc.HiddenReason = "data disc";
			}
		}
		foreach (DiscInfo disc in discs.Where((DiscInfo disc) => disc.HiddenReason == null && !disc.BootsPackage && disc.ContentPackages.Count > 0))
		{
			bool allInstalled = disc.ContentPackages.All(((string titleId, bool installed) package) => package.installed);
			HashSet<string> packageGames = disc.ContentPackages.Select(((string titleId, bool installed) package) => package.titleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
			// Only when the game has a separate play disc of its own, so a single disc that also carries DLC never disappears.
			bool gameHasPlayDisc = discs.Any((DiscInfo other) => other != disc && other.TitleId != null && packageGames.Contains(other.TitleId) && other.ContentPackages.Count == 0 && !DataOnlyDisc.IsMatch(other.Name));
			if (allInstalled && gameHasPlayDisc)
			{
				disc.HiddenReason = "content installed";
			}
		}
		// A separate multiplayer disc (AC III, AC IV) is hidden when the same game has a single-player disc.
		foreach (DiscInfo disc in discs.Where((DiscInfo disc) => disc.HiddenReason == null && disc.IsMultiplayerDisc))
		{
			if (discs.Any((DiscInfo other) => other != disc && other.HiddenReason == null && !other.IsMultiplayerDisc && other.TitleId != null && string.Equals(other.TitleId, disc.TitleId, StringComparison.OrdinalIgnoreCase)))
			{
				disc.HiddenReason = "multiplayer disc";
			}
		}
	}

	private static DiscInfo? InspectDisc(string path, string installedContent)
	{
		string name = path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) ? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path)) ?? string.Empty : System.IO.Path.GetFileNameWithoutExtension(path);
		DiscInfo info = new DiscInfo
		{
			Path = path,
			Name = name,
			TitleId = TryReadTitleId(path)
		};
		if (info.TitleId != null)
		{
			info.GameTitleIds.Add(info.TitleId);
		}
		if (!path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
		{
			return info;
		}
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			(long partition, byte[] root)? disc = OpenDisc(stream);
			if (!disc.HasValue)
			{
				return info;
			}
			info.BootModule = FindDiscLauncherTarget(stream, disc.Value.partition, disc.Value.root);
			info.HasMultiplayerData = FindEntry(disc.Value.root, "multi")?.IsDirectory == true;
			DirectoryEntry? content = FindEntry(disc.Value.root, "Content");
			if (content == null || !content.IsDirectory)
			{
				return info;
			}
			List<(string path, DirectoryEntry entry)> files = new List<(string, DirectoryEntry)>();
			CollectFiles(stream, disc.Value.partition, content, "Content", files, 0);
			foreach ((string filePath, DirectoryEntry entry) in files)
			{
				byte[] header = ReadAt(stream, disc.Value.partition + entry.Sector * (long)SectorSize, 0x364);
				if (header.Length == 0 || !IsPackageMagic(header))
				{
					continue;
				}
				string contentType = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x344)).ToString("X8");
				string titleId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x360)).ToString("X8");
				string packageName = filePath.Split('\\')[^1];
				if (contentType == "000D0000")
				{
					info.BootsPackage |= !EnumerateEntries(disc.Value.root).Any((DirectoryEntry rootEntry) => !LauncherDiscRootNames.Contains(rootEntry.Name));
				}
				else if (contentType == "00000002")
				{
					bool installed = File.Exists(System.IO.Path.Combine(installedContent, titleId, "Headers", contentType, packageName + ".header"));
					info.ContentPackages.Add((titleId, installed));
					info.GameTitleIds.Add(titleId);
				}
			}
		}
		catch (Exception)
		{
		}
		return info;
	}

	// Some publishers (e.g. Ubisoft's x360disclauncher) ship a tiny default.xex that only starts the
	// real game xex. Xenia stops at that hand-off, so name the biggest game xex on the disc instead
	// (passed to Xenia as --launch_module).
	private static string? FindDiscLauncherTarget(FileStream stream, long partition, byte[] root)
	{
		DirectoryEntry? defaultXex = FindEntry(root, "default.xex");
		if (defaultXex == null)
		{
			return null;
		}
		string? originalName = ReadXexOriginalName(ReadAt(stream, partition + defaultXex.Sector * (long)SectorSize, (int)Math.Min(defaultXex.Size, 0x10000u)));
		if (originalName == null || originalName.IndexOf("disclauncher", StringComparison.OrdinalIgnoreCase) < 0)
		{
			return null;
		}
		return EnumerateEntries(root)
			.Where((DirectoryEntry entry) => !entry.IsDirectory && entry.Name.EndsWith(".xex", StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.Name, "default.xex", StringComparison.OrdinalIgnoreCase) && !entry.Name.StartsWith("Uplay", StringComparison.OrdinalIgnoreCase))
			.OrderByDescending((DirectoryEntry entry) => entry.Size)
			.FirstOrDefault()?.Name;
	}

	private static bool IsPackageMagic(byte[] header)
	{
		string magic = Encoding.ASCII.GetString(header, 0, 4);
		return magic == "PIRS" || magic == "LIVE" || magic == "CON ";
	}

	// "Forza Motorsport 4 (Play Disc)" -> "Forza Motorsport 4", for matching tiles of the same game.
	private static string BaseTitle(string title)
	{
		return Regex.Replace(title, "(\\s*\\([^)]*\\))+\\s*$", string.Empty).Trim();
	}

	private static string? ReadXexOriginalName(byte[] xex)
	{
		if (xex.Length < 24 || Encoding.ASCII.GetString(xex, 0, 4) != "XEX2")
		{
			return null;
		}
		uint headerCount = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(20));
		for (int i = 0; i < headerCount && 24 + i * 8 + 8 <= xex.Length; i++)
		{
			int entry = 24 + i * 8;
			if (BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry)) != 0x183FF)
			{
				continue;
			}
			// Original PE name: size-prefixed, null-terminated string.
			int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry + 4));
			if (offset + 4 >= xex.Length)
			{
				return null;
			}
			int end = Array.IndexOf(xex, (byte)0, offset + 4);
			return Encoding.ASCII.GetString(xex, offset + 4, (end < 0 ? xex.Length : end) - offset - 4);
		}
		return null;
	}

	// Disc images, plus extracted games (a folder holding default.xex).
	private static bool IsGameImage(string path)
	{
		return path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase) || string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase);
	}

	public static string CleanTitle(string fileName, bool includeDisc = true)
	{
		Match disc = DiscTag.Match(fileName);
		string title = fileName;
		int paren = title.IndexOf(" (", StringComparison.Ordinal);
		if (paren > 0)
		{
			title = title.Substring(0, paren);
		}
		// "Amazing Spider-Man, The" -> "The Amazing Spider-Man"
		Match article = Regex.Match(title, "^(.*?), (The|A|An)(\\s*-.*)?$");
		if (article.Success)
		{
			title = article.Groups[2].Value + " " + article.Groups[1].Value + article.Groups[3].Value;
		}
		title = title.Replace(" - ", ": ");
		if (disc.Success && includeDisc)
		{
			title += (disc.Groups[2].Success ? (" (" + disc.Groups[2].Value + ")") : (" (Disc " + disc.Groups[1].Value + ")"));
		}
		return title.Trim();
	}

	private static async Task<string> EnsureBoxArtAsync(string titleId, CancellationToken cancellationToken)
	{
		if (string.IsNullOrWhiteSpace(titleId))
		{
			return string.Empty;
		}
		string path = Path.Combine(ArtFolder, titleId + ".jpg");
		if (File.Exists(path))
		{
			return path;
		}
		try
		{
			string url = "http://download.xbox.com/content/images/66acd000-77fe-1000-9115-d802" + titleId.ToLowerInvariant() + "/1033/boxartlg.jpg";
			byte[] bytes = await HttpClient.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (bytes.Length < 1024)
			{
				return string.Empty;
			}
			await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			return path;
		}
		catch (Exception) when (!cancellationToken.IsCancellationRequested)
		{
			return string.Empty;
		}
	}

	// Some discs (Minecraft, arcade bundles) boot a small launcher whose only job is to start a
	// game package stored under \Content. Xenia cannot follow that hand-off, so copy the package
	// out once and boot it directly. Discs with several packages stay as they are.
	public static string? TryExtractDiscPackage(string isoPath)
	{
		if (!isoPath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}
		try
		{
			using FileStream stream = new FileStream(isoPath, FileMode.Open, FileAccess.Read, FileShare.Read);
			(long partition, byte[] root)? disc = OpenDisc(stream);
			if (!disc.HasValue)
			{
				return null;
			}
			long partition = disc.Value.partition;
			// Only launcher discs: nothing at the root but the stub xex and system files.
			if (EnumerateEntries(disc.Value.root).Any((DirectoryEntry entry) => !LauncherDiscRootNames.Contains(entry.Name)))
			{
				return null;
			}
			DirectoryEntry? content = FindEntry(disc.Value.root, "Content");
			if (content == null || !content.IsDirectory)
			{
				return null;
			}
			List<(string path, DirectoryEntry entry)> packages = new List<(string, DirectoryEntry)>();
			CollectFiles(stream, partition, content, "Content", packages, 0);
			List<(string path, DirectoryEntry entry)> games = packages.Where(((string path, DirectoryEntry entry) file) => file.path.Split('\\').Length == 5 && string.Equals(file.path.Split('\\')[3], "000D0000", StringComparison.OrdinalIgnoreCase)).ToList();
			if (games.Count != 1)
			{
				return null;
			}
			(string path, DirectoryEntry entry) game = games[0];
			string outputPath = Path.Combine(PackageFolder, game.path.Split('\\')[2], game.path.Split('\\')[4]);
			if (File.Exists(outputPath) && new FileInfo(outputPath).Length == game.entry.Size)
			{
				return outputPath;
			}
			Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
			string tempPath = outputPath + ".partial";
			using (FileStream output = File.Create(tempPath))
			{
				stream.Seek(partition + game.entry.Sector * (long)SectorSize, SeekOrigin.Begin);
				byte[] buffer = new byte[1 << 20];
				long remaining = game.entry.Size;
				while (remaining > 0)
				{
					int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
					if (read <= 0)
					{
						throw new EndOfStreamException();
					}
					output.Write(buffer, 0, read);
					remaining -= read;
				}
			}
			File.Move(tempPath, outputPath, overwrite: true);
			return outputPath;
		}
		catch (Exception)
		{
			return null;
		}
	}

	// STFS/PIRS/LIVE/CON packages keep the title id at 0x360.
	public static string? TryReadPackageTitleId(string path)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			byte[] header = ReadAt(stream, 0L, 0x364);
			if (header.Length == 0)
			{
				return null;
			}
			string magic = Encoding.ASCII.GetString(header, 0, 4);
			if (magic != "PIRS" && magic != "LIVE" && magic != "CON ")
			{
				return null;
			}
			return BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x360)).ToString("X8");
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static void CollectFiles(FileStream stream, long partition, DirectoryEntry directory, string prefix, List<(string path, DirectoryEntry entry)> files, int depth)
	{
		if (depth > 4 || directory.Size == 0)
		{
			return;
		}
		byte[] data = ReadAt(stream, partition + directory.Sector * (long)SectorSize, (int)Math.Min(directory.Size, 0x100000u));
		foreach (DirectoryEntry entry in EnumerateEntries(data))
		{
			string path = prefix + "\\" + entry.Name;
			if (entry.IsDirectory)
			{
				CollectFiles(stream, partition, entry, path, files, depth + 1);
			}
			else
			{
				files.Add((path, entry));
			}
		}
	}

	private static (long partition, byte[] root)? OpenDisc(FileStream stream)
	{
		foreach (long partition in PartitionOffsets)
		{
			byte[] descriptor = ReadAt(stream, partition + 32 * SectorSize, 28);
			if (descriptor.Length < 28 || !descriptor.AsSpan(0, 20).SequenceEqual(MediaMagic))
			{
				continue;
			}
			uint rootSector = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(20));
			uint rootSize = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(24));
			return (partition, ReadAt(stream, partition + rootSector * (long)SectorSize, (int)Math.Min(rootSize, 0x100000u)));
		}
		return null;
	}

	public static string? TryReadTitleId(string path)
	{
		try
		{
			using FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
			if (path.EndsWith(".xex", StringComparison.OrdinalIgnoreCase))
			{
				byte[] xex = new byte[Math.Min(stream.Length, 0x10000L)];
				stream.ReadExactly(xex);
				return ReadXexTitleId(xex);
			}
			(long partition, byte[] root)? disc = OpenDisc(stream);
			DirectoryEntry? xexEntry = disc.HasValue ? FindEntry(disc.Value.root, "default.xex") : null;
			if (xexEntry == null)
			{
				return null;
			}
			byte[] header = ReadAt(stream, disc.Value.partition + xexEntry.Sector * (long)SectorSize, (int)Math.Min(xexEntry.Size, 0x10000u));
			return ReadXexTitleId(header);
		}
		catch (Exception)
		{
		}
		return null;
	}

	private sealed record DirectoryEntry(string Name, uint Sector, uint Size, bool IsDirectory);

	private static DirectoryEntry? FindEntry(byte[] directory, string name)
	{
		return EnumerateEntries(directory).FirstOrDefault((DirectoryEntry entry) => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
	}

	// XDVDFS directory entries are 4-byte aligned; 0xFF padding fills the rest of a sector.
	private static IEnumerable<DirectoryEntry> EnumerateEntries(byte[] directory)
	{
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
			yield return new DirectoryEntry(Encoding.ASCII.GetString(directory, offset + 14, nameLength), BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 4)), BinaryPrimitives.ReadUInt32LittleEndian(directory.AsSpan(offset + 8)), (directory[offset + 12] & 0x10) != 0);
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
			uint key = BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry));
			if (key != 0x40006)
			{
				continue;
			}
			// Execution info: media id, version, base version, title id.
			int info = (int)BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(entry + 4));
			if (info + 16 > xex.Length)
			{
				return null;
			}
			return BinaryPrimitives.ReadUInt32BigEndian(xex.AsSpan(info + 12)).ToString("X8");
		}
		return null;
	}

	private static byte[] ReadAt(FileStream stream, long offset, int count)
	{
		if (offset + count > stream.Length)
		{
			return Array.Empty<byte>();
		}
		byte[] buffer = new byte[count];
		stream.Seek(offset, SeekOrigin.Begin);
		stream.ReadExactly(buffer);
		return buffer;
	}
}
