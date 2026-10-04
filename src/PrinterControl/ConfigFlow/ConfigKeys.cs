namespace PrinterControl.ConfigFlow;

// Persisted in users' config entries; never rename one.
internal static class ConfigKeys
{
	public const string Backend = "backend";
	public const string Name = "name";
	public const string Url = "url";
	public const string Auth = "auth";
	public const string User = "user";
	public const string ApiKey = "api_key";
	public const string WebcamUrl = "webcam_url";

	// Entries written before the printer type was stored are OctoPrint ones.
	public const string DefaultBackend = "octoprint";

	public const string AuthAppKeys = "appkeys";
	public const string AuthApiKey = "apikey";
	public const string AuthKeep = "keep";
}
