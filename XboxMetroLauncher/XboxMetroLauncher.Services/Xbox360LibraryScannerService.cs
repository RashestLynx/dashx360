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

	public const string CombinedFolderName = "Combined";

	public const string CombinedMarkerName = "combined-from.txt";

	private const int SectorSize = 0x800;

	// XGD partition offsets: plain XDVDFS/XSF, XGD3, XGD2, XGD1.
	private static readonly long[] PartitionOffsets = new long[4] { 0L, 0x2080000L, 0xFD90000L, 0x18300000L };

	private static readonly byte[] MediaMagic = Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA");

	// Discs that only install data; the console never shows these as games.
	private static readonly Regex DataOnlyDisc = new Regex("\\((Content Install Disc|Install Disc|Bonus Disc|Bonus Content Disc)\\)|Bonus Content Disc", RegexOptions.IgnoreCase);

	private static readonly HashSet<string> LauncherDiscRootNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "default.xex", "media", "Content", "$SystemUpdate", "$TitleUpdate", "AvatarAssetPack", "nxeart" };

	private static readonly Regex DiscTag = new Regex("\\(Disc (\\d+)\\)(?:\\s*\\(([^)]*)\\))?", RegexOptions.IgnoreCase);

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
		// Combined multi-disc games (built by tools\DiscMerger) replace the discs they came from.
		// combined-from.txt is written last, so a half-built folder is ignored.
		HashSet<string> supersededDiscs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		HashSet<string> combinedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		string combinedRoot = Path.Combine(gamesFolder, CombinedFolderName);
		if (Directory.Exists(combinedRoot))
		{
			foreach (string folder in Directory.EnumerateDirectories(combinedRoot))
			{
				string marker = Path.Combine(folder, CombinedMarkerName);
				if (File.Exists(marker))
				{
					combinedFolders.Add(folder);
					supersededDiscs.UnionWith(File.ReadAllLines(marker).Where((string line) => !string.IsNullOrWhiteSpace(line)).Select((string line) => line.Trim()));
				}
			}
		}
		List<string> isoPaths = Directory.EnumerateFiles(gamesFolder, "*.*", SearchOption.AllDirectories).Where((string path) => IsGameImage(path, combinedRoot, combinedFolders)).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase)
			.ToList();
		Directory.CreateDirectory(ArtFolder);
		int added = 0;
		int updated = 0;
		int skipped = 0;
		List<GameMetadata> retired = library.Games.Where((GameMetadata existing) => string.Equals(existing.Platform, PlatformName, StringComparison.OrdinalIgnoreCase) && supersededDiscs.Contains(Path.GetFileNameWithoutExtension(existing.InstallPath))).ToList();
		foreach (GameMetadata disc in retired)
		{
			library.Games.Remove(disc);
			updated++;
		}
		foreach (string isoPath in isoPaths)
		{
			cancellationToken.ThrowIfCancellationRequested();
			bool isFolderGame = isoPath.EndsWith(".xex", StringComparison.OrdinalIgnoreCase);
			string fileName = isFolderGame ? Path.GetFileName(Path.GetDirectoryName(isoPath)) ?? string.Empty : Path.GetFileNameWithoutExtension(isoPath);
			if (supersededDiscs.Contains(fileName))
			{
				skipped++;
				continue;
			}
			if (DataOnlyDisc.IsMatch(fileName))
			{
				skipped++;
				continue;
			}
			string titleId = await Task.Run(() => TryReadTitleId(isoPath), cancellationToken).ConfigureAwait(continueOnCapturedContext: false) ?? string.Empty;
			string launchPath = isFolderGame ? ResolveDiscLauncher(isoPath) : isoPath;
			string? packagePath = await Task.Run(() => TryExtractDiscPackage(isoPath), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			if (packagePath != null)
			{
				launchPath = packagePath;
				titleId = TryReadPackageTitleId(packagePath) ?? titleId;
			}
			string coverPath = await EnsureBoxArtAsync(titleId, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
			GameMetadata? game = library.Games.FirstOrDefault((GameMetadata existing) => string.Equals(existing.Platform, PlatformName, StringComparison.OrdinalIgnoreCase) && string.Equals(existing.InstallPath, isoPath, StringComparison.OrdinalIgnoreCase));
			if (game == null)
			{
				game = new GameMetadata
				{
					Title = CleanTitle(fileName),
					Platform = PlatformName,
					Genre = "Xbox 360",
					InstallPath = isoPath
				};
				// Keep the play history of the separate discs this combined game replaced.
				foreach (GameMetadata disc in retired.Where((GameMetadata disc) => isFolderGame && File.ReadAllLines(Path.Combine(Path.GetDirectoryName(isoPath)!, CombinedMarkerName)).Any((string line) => string.Equals(line.Trim(), Path.GetFileNameWithoutExtension(disc.InstallPath), StringComparison.OrdinalIgnoreCase))))
				{
					game.Playtime += disc.Playtime;
					if (disc.LastPlayed > game.LastPlayed || !game.LastPlayed.HasValue)
					{
						game.LastPlayed = disc.LastPlayed;
					}
					game.IsFavorite |= disc.IsFavorite;
				}
				library.Games.Add(game);
				added++;
			}
			else
			{
				updated++;
			}
			game.LaunchType = "Exe";
			game.ExecutablePath = xeniaPath;
			game.Arguments = "\"" + launchPath + "\" --fullscreen=true";
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
			Message = ((isoPaths.Count == 0) ? "No Xbox 360 games were found." : $"Xbox 360 scan complete. Added: {added}, Updated: {updated}, Skipped: {skipped}.")
		};
	}

	// Some publishers (e.g. Ubisoft's x360disclauncher) ship a tiny default.xex that only starts the
	// real game xex. Xenia stops at that hand-off, so boot the biggest game xex beside it instead.
	private static string ResolveDiscLauncher(string defaultXex)
	{
		try
		{
			byte[] header = File.ReadAllBytes(defaultXex).AsSpan(0, (int)Math.Min(new FileInfo(defaultXex).Length, 0x10000L)).ToArray();
			string? originalName = ReadXexOriginalName(header);
			if (originalName == null || originalName.IndexOf("disclauncher", StringComparison.OrdinalIgnoreCase) < 0)
			{
				return defaultXex;
			}
			string? game = Directory.EnumerateFiles(Path.GetDirectoryName(defaultXex)!, "*.xex")
				.Where((string path) => !string.Equals(path, defaultXex, StringComparison.OrdinalIgnoreCase) && !Path.GetFileName(path).StartsWith("Uplay", StringComparison.OrdinalIgnoreCase))
				.OrderByDescending((string path) => new FileInfo(path).Length)
				.FirstOrDefault();
			return game ?? defaultXex;
		}
		catch (Exception)
		{
			return defaultXex;
		}
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

	// Disc images anywhere, plus extracted games (a folder holding default.xex). Under Combined\,
	// only finished folders count.
	private static bool IsGameImage(string path, string combinedRoot, HashSet<string> combinedFolders)
	{
		if (path.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}
		if (!string.Equals(Path.GetFileName(path), "default.xex", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}
		string folder = Path.GetDirectoryName(path) ?? string.Empty;
		if (folder.StartsWith(combinedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			return combinedFolders.Contains(folder);
		}
		return true;
	}

	public static string CleanTitle(string fileName)
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
		if (disc.Success)
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
