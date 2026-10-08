using System.Net;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;

namespace PrinterControl.Backends.Moonraker;

internal sealed class MoonrakerSetup(MoonrakerHttp http, bool editing) : IPrinterSetup
{
	public const string ApiKeyStepId = "api-key";

	public async Task<PrinterSetupResult> StartAsync(CancellationToken cancellationToken)
	{
		try
		{
			var info = await http.GetAsync("server/info", null, cancellationToken);
			return info.Status == HttpStatusCode.OK
				? PrinterSetupResult.Done(await HostnameAsync(null, cancellationToken))
				: PrinterSetupResult.Step(ApiKeyStep());
		}
		catch (PrinterException)
		{
			return PrinterSetupResult.Error(ApiKeyStep(), Strings.Setup.Errors.Unreachable());
		}
	}

	public async Task<PrinterSetupResult> SubmitAsync(string stepId, IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
	{
		var apiKey = input.GetValueOrDefault(ConfigKeys.ApiKey)?.ToString()?.Trim();
		if (string.IsNullOrEmpty(apiKey))
		{
			// Editing: an empty secret keeps the stored key.
			return editing
				? PrinterSetupResult.Done()
				: PrinterSetupResult.Error(ApiKeyStep(), fieldErrors: new Dictionary<string, LocalizedText>
				{
					[ConfigKeys.ApiKey] = MacroDeckStrings.Validation.Required(Strings.Setup.Moonraker.ApiKey.Label()),
				});
		}

		try
		{
			var info = await http.GetAsync("server/info", apiKey, cancellationToken);
			return info.Status == HttpStatusCode.OK
				? PrinterSetupResult.Done(await HostnameAsync(apiKey, cancellationToken))
				: PrinterSetupResult.Error(ApiKeyStep(), fieldErrors: new Dictionary<string, LocalizedText>
				{
					[ConfigKeys.ApiKey] = Strings.Setup.Moonraker.Errors.Unauthorized(),
				});
		}
		catch (PrinterException)
		{
			return PrinterSetupResult.Error(ApiKeyStep(), Strings.Setup.Errors.Unreachable());
		}
	}

	private async Task<string?> HostnameAsync(string? apiKey, CancellationToken cancellationToken) =>
		(await http.GetAsync("printer/info", apiKey, cancellationToken)).Result?.Str("hostname");

	private ConfigFlowStep ApiKeyStep() => new()
	{
		StepId = ApiKeyStepId,
		Title = Strings.Setup.Moonraker.ApiKey.Title(),
		Description = Strings.Setup.Moonraker.ApiKey.Description(),
		Fields =
		[
			ActionParameter.Secret(
				ConfigKeys.ApiKey,
				label: Strings.Setup.Moonraker.ApiKey.Label(),
				description: Strings.Setup.Moonraker.ApiKey.Help(),
				required: !editing),
		],
	};
}
