using System.Globalization;
using System.Text;

namespace PrinterControl.Ui;

// Plugins ship no images. The renderer takes absolute M/L/H/V/C/Q/A/Z in a unit box and fills nonzero,
// so a cut-out winds counter-clockwise.
internal static class Glyphs
{
	public static readonly string Printer = new GlyphPath()
		.RoundedRect(0.1, 0.06, 0.8, 0.88, 0.1)
		.RoundedRect(0.19, 0.15, 0.62, 0.7, 0.05, hole: true)
		.RoundedRect(0.19, 0.3, 0.62, 0.07, 0.02)
		.RoundedRect(0.4, 0.25, 0.2, 0.17, 0.03)
		.Polygon((0.44, 0.42), (0.56, 0.42), (0.5, 0.5))
		.RoundedRect(0.41, 0.58, 0.18, 0.14, 0.02)
		.RoundedRect(0.24, 0.72, 0.52, 0.06, 0.02)
		.ToString();

	public static readonly string Nozzle = new GlyphPath()
		.RoundedRect(0.22, 0.08, 0.56, 0.42, 0.08)
		.Circle(0.5, 0.29, 0.08, hole: true)
		.RoundedRect(0.38, 0.48, 0.24, 0.17, 0.02)
		.Polygon((0.3, 0.64), (0.7, 0.64), (0.5, 0.92))
		.ToString();

	public static readonly string Bed = new GlyphPath()
		.RoundedRect(0.2, 0.18, 0.11, 0.38, 0.055)
		.RoundedRect(0.445, 0.08, 0.11, 0.48, 0.055)
		.RoundedRect(0.69, 0.18, 0.11, 0.38, 0.055)
		.RoundedRect(0.06, 0.66, 0.88, 0.16, 0.06)
		.ToString();

	public static readonly string Pause = new GlyphPath()
		.RoundedRect(0.24, 0.16, 0.18, 0.68, 0.06)
		.RoundedRect(0.58, 0.16, 0.18, 0.68, 0.06)
		.ToString();

	public static readonly string Stop = new GlyphPath()
		.RoundedRect(0.2, 0.2, 0.6, 0.6, 0.1)
		.ToString();

	public static readonly string Warning = new GlyphPath()
		.Polygon((0.5, 0.07), (0.96, 0.89), (0.04, 0.89))
		.RoundedRect(0.455, 0.33, 0.09, 0.32, 0.045, hole: true)
		.Circle(0.5, 0.76, 0.055, hole: true)
		.ToString();

	public static readonly string Clock = new GlyphPath()
		.Circle(0.5, 0.5, 0.44)
		.Circle(0.5, 0.5, 0.33, hole: true)
		.RoundedRect(0.455, 0.25, 0.09, 0.3, 0.045)
		.RoundedRect(0.455, 0.455, 0.25, 0.09, 0.045)
		.ToString();

	public static readonly string Dot = new GlyphPath()
		.Circle(0.5, 0.5, 0.5)
		.ToString();

	public static readonly string Camera = new GlyphPath()
		.RoundedRect(0.06, 0.26, 0.88, 0.58, 0.1)
		.RoundedRect(0.32, 0.16, 0.36, 0.14, 0.04)
		.Circle(0.5, 0.55, 0.2, hole: true)
		.Circle(0.5, 0.55, 0.12)
		.ToString();

	private sealed class GlyphPath
	{
		private readonly StringBuilder _data = new();

		public GlyphPath RoundedRect(double x, double y, double width, double height, double radius, bool hole = false)
		{
			var r = Math.Min(radius, Math.Min(width, height) / 2);
			var right = x + width;
			var bottom = y + height;
			Append("M", x + r, y);
			if (hole)
			{
				Arc(r, x, y + r, clockwise: false);
				Append("L", x, bottom - r);
				Arc(r, x + r, bottom, clockwise: false);
				Append("L", right - r, bottom);
				Arc(r, right, bottom - r, clockwise: false);
				Append("L", right, y + r);
				Arc(r, right - r, y, clockwise: false);
			}
			else
			{
				Append("L", right - r, y);
				Arc(r, right, y + r, clockwise: true);
				Append("L", right, bottom - r);
				Arc(r, right - r, bottom, clockwise: true);
				Append("L", x + r, bottom);
				Arc(r, x, bottom - r, clockwise: true);
				Append("L", x, y + r);
				Arc(r, x + r, y, clockwise: true);
			}

			return Close();
		}

		public GlyphPath Circle(double cx, double cy, double radius, bool hole = false)
		{
			Append("M", cx - radius, cy);
			Arc(radius, cx + radius, cy, clockwise: !hole, large: true);
			Arc(radius, cx - radius, cy, clockwise: !hole, large: true);
			return Close();
		}

		public GlyphPath Polygon(params (double X, double Y)[] points)
		{
			Append("M", points[0].X, points[0].Y);
			foreach (var (x, y) in points.Skip(1))
			{
				Append("L", x, y);
			}

			return Close();
		}

		public override string ToString() => _data.ToString().Trim();

		private GlyphPath Close()
		{
			_data.Append("Z ");
			return this;
		}

		private void Arc(double radius, double x, double y, bool clockwise, bool large = false) =>
			_data.Append(CultureInfo.InvariantCulture, $"A {Number(radius)} {Number(radius)} 0 {(large ? 1 : 0)} {(clockwise ? 1 : 0)} {Number(x)} {Number(y)} ");

		private void Append(string command, double x, double y) =>
			_data.Append(CultureInfo.InvariantCulture, $"{command} {Number(x)} {Number(y)} ");

		private static string Number(double value) =>
			Math.Round(value, 4).ToString("0.####", CultureInfo.InvariantCulture);
	}
}
