namespace PrinterControl.Ui;

// Text is laid out on the viewing device, so a run beside a filling sibling gets an estimated width: em
// widths of a semibold UI face, erring wide. Only for the plugin's own numbers, never for localized text.
internal static class TextWidth
{
	private const string NarrowChars = " .,:;'!|iljtfrI-/()[]°";
	private const string WideChars = "mwMW%@";

	private const double NarrowEm = 0.36;
	private const double RegularEm = 0.6;
	private const double CapitalEm = 0.7;
	private const double WideEm = 0.95;

	public static double Of(string text, double size) =>
		text.Sum(c =>
			NarrowChars.Contains(c) ? NarrowEm
			: WideChars.Contains(c) ? WideEm
			: char.IsUpper(c) || char.IsDigit(c) ? CapitalEm
			: RegularEm) * size;
}
