// Pads every PNG in a directory, in place, onto a 16:9 canvas for the store listing. The store crops
// card artwork to 16:9 (object-fit: cover), so a square or 2.1:1 widget render loses its edges there;
// centred on a 16:9 canvas with a margin it shows whole. The padding takes the colour of the image's
// top-left pixel - what the renderer put behind the tile's rounded corner (transparent by default).
//
// Usage: dotnet run tools/StoreCanvas.cs -- <directory>
// No packages: reads and writes the 8-bit, non-interlaced PNGs `macrodeck-plugin preview render` emits.

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// The share of the canvas's limiting side the widget may take.
const double Fill = 0.8;

if (args.Length != 1 || !Directory.Exists(args[0]))
{
	Console.Error.WriteLine("usage: dotnet run tools/StoreCanvas.cs -- <directory>");
	return 1;
}

foreach (var path in Directory.GetFiles(args[0], "*.png"))
{
	var (width, height, rgba) = Png.Read(path);

	var canvasWidth = (int)Math.Ceiling(Math.Max(width / Fill, height / Fill * 16 / 9));
	var canvasHeight = (int)Math.Round(canvasWidth * 9 / 16.0);
	var canvas = new byte[canvasWidth * canvasHeight * 4];
	for (var i = 0; i < canvas.Length; i += 4)
	{
		rgba.AsSpan(0, 4).CopyTo(canvas.AsSpan(i));
	}

	var left = (canvasWidth - width) / 2;
	var top = (canvasHeight - height) / 2;
	for (var y = 0; y < height; y++)
	{
		rgba.AsSpan(y * width * 4, width * 4).CopyTo(canvas.AsSpan(((top + y) * canvasWidth + left) * 4));
	}

	Png.Write(path, canvasWidth, canvasHeight, canvas);
	Console.WriteLine($"{Path.GetFileName(path)}: {width}x{height} -> {canvasWidth}x{canvasHeight}");
}

return 0;

static class Png
{
	static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];
	static readonly uint[] CrcTable = BuildCrcTable();

	public static (int Width, int Height, byte[] Rgba) Read(string path)
	{
		var file = File.ReadAllBytes(path);
		if (!file.AsSpan(0, 8).SequenceEqual(Signature))
		{
			throw new InvalidDataException($"{path} is not a PNG");
		}

		int width = 0, height = 0, channels = 0;
		using var idat = new MemoryStream();
		for (var offset = 8; offset < file.Length;)
		{
			var length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(offset));
			var type = Encoding.ASCII.GetString(file, offset + 4, 4);
			var data = file.AsSpan(offset + 8, length);
			offset += 12 + length;

			if (type == "IHDR")
			{
				width = BinaryPrimitives.ReadInt32BigEndian(data);
				height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
				channels = data[9] switch
				{
					6 => 4,
					2 => 3,
					_ => 0,
				};
				if (data[8] != 8 || channels == 0 || data[12] != 0)
				{
					throw new InvalidDataException($"{path}: only 8-bit, non-interlaced RGB(A) PNGs are supported");
				}
			}
			else if (type == "IDAT")
			{
				idat.Write(data);
			}
			else if (type == "IEND")
			{
				break;
			}
		}

		idat.Position = 0;
		using var inflated = new MemoryStream();
		using (var zlib = new ZLibStream(idat, CompressionMode.Decompress))
		{
			zlib.CopyTo(inflated);
		}

		var raw = inflated.GetBuffer();
		var stride = width * channels;
		var pixels = new byte[stride * height];
		for (var y = 0; y < height; y++)
		{
			var filter = raw[y * (stride + 1)];
			var source = raw.AsSpan(y * (stride + 1) + 1, stride);
			var row = pixels.AsSpan(y * stride, stride);
			var previous = y > 0 ? pixels.AsSpan((y - 1) * stride, stride) : new byte[stride];
			for (var x = 0; x < stride; x++)
			{
				int a = x >= channels ? row[x - channels] : 0;
				int b = previous[x];
				int c = x >= channels ? previous[x - channels] : 0;
				row[x] = (byte)(source[x] + filter switch
				{
					0 => 0,
					1 => a,
					2 => b,
					3 => (a + b) / 2,
					4 => Paeth(a, b, c),
					_ => throw new InvalidDataException($"{path}: unknown filter {filter}"),
				});
			}
		}

		if (channels == 4)
		{
			return (width, height, pixels);
		}

		var rgba = new byte[width * height * 4];
		for (int i = 0, j = 0; i < pixels.Length; i += 3, j += 4)
		{
			pixels.AsSpan(i, 3).CopyTo(rgba.AsSpan(j));
			rgba[j + 3] = 255;
		}

		return (width, height, rgba);
	}

	public static void Write(string path, int width, int height, byte[] rgba)
	{
		var header = new byte[13];
		BinaryPrimitives.WriteInt32BigEndian(header, width);
		BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
		header[8] = 8;
		header[9] = 6;

		using var deflated = new MemoryStream();
		using (var zlib = new ZLibStream(deflated, CompressionLevel.SmallestSize))
		{
			for (var y = 0; y < height; y++)
			{
				zlib.WriteByte(0);
				zlib.Write(rgba, y * width * 4, width * 4);
			}
		}

		using var file = File.Create(path);
		file.Write(Signature);
		WriteChunk(file, "IHDR", header);
		WriteChunk(file, "IDAT", deflated.ToArray());
		WriteChunk(file, "IEND", []);
	}

	static void WriteChunk(Stream stream, string type, byte[] data)
	{
		Span<byte> word = stackalloc byte[4];
		BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
		stream.Write(word);

		var typeAndData = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
		stream.Write(typeAndData);

		var crc = 0xFFFFFFFFu;
		foreach (var value in typeAndData)
		{
			crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
		}

		BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xFFFFFFFFu);
		stream.Write(word);
	}

	static int Paeth(int a, int b, int c)
	{
		var p = a + b - c;
		int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
		return pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
	}

	static uint[] BuildCrcTable()
	{
		var table = new uint[256];
		for (var n = 0u; n < 256; n++)
		{
			var c = n;
			for (var k = 0; k < 8; k++)
			{
				c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
			}

			table[n] = c;
		}

		return table;
	}
}
