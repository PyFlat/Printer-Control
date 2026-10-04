using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using PrinterControl.Backends;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.ConfigFlow;

// Asks for the name, the address and the webcam, recognizes the printer software at that address, then
// runs that backend's own steps (IPrinterSetup) and stores the backend id with the entry.
internal sealed class PrinterConfigFlow(PrinterBackends backends, ILogger logger) : IConfigFlow, IAsyncDisposable
{
	public const string ConnectionStepId = "connection";

	private readonly ILogger _logger = logger.ForContext<PrinterConfigFlow>();
	private bool _editing;
	private string? _requestedName;
	private Uri? _baseUri;
	private IPrinterBackend? _backend;
	private IPrinterSetup? _setup;

	public Task<ConfigFlowResult> StartAsync(IConfigFlowContext context, CancellationToken cancellationToken)
	{
		_editing = (context as IConfigFlowEntryContext)?.EntryTitle is not null;
		return Task.FromResult(ConfigFlowResult.Step(ConnectionStep()));
	}

	public async Task<ConfigFlowResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		IConfigFlowContext context,
		CancellationToken cancellationToken)
	{
		if (stepId == ConnectionStepId)
		{
			return await SubmitConnectionAsync(input, cancellationToken);
		}

		return _setup is null
			? ConfigFlowResult.Step(ConnectionStep())
			: Finish(await _setup.SubmitAsync(stepId, input, cancellationToken));
	}

	private async Task<ConfigFlowResult> SubmitConnectionAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
	{
		if (!PrinterUrls.TryParse(input.GetValueOrDefault(ConfigKeys.Url)?.ToString(), out var baseUri))
		{
			return ConfigFlowResult.Error(ConnectionStep(), fieldErrors: new Dictionary<string, LocalizedText>
			{
				[ConfigKeys.Url] = Strings.Setup.Connection.Url.Invalid(),
			});
		}

		await DisposeAsync();
		_requestedName = input.GetValueOrDefault(ConfigKeys.Name)?.ToString() is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
		_baseUri = baseUri;

		var answered = false;
		foreach (var backend in backends.All)
		{
			try
			{
				if (await backend.DetectAsync(baseUri, cancellationToken))
				{
					_backend = backend;
					break;
				}

				answered = true;
			}
			catch (PrinterException exception) when (exception.Failure == PrinterFailure.Unreachable)
			{
				_logger.Information("Nothing answered {Backend} at {Url}: {Message}", backend.Id, baseUri, exception.Message);
			}
		}

		if (_backend is null)
		{
			return ConfigFlowResult.Error(
				ConnectionStep(),
				answered ? Strings.Setup.Errors.Unsupported() : Strings.Setup.Errors.Unreachable());
		}

		_setup = _backend.CreateSetup(baseUri, _editing);
		return Finish(await _setup.StartAsync(cancellationToken));
	}

	// The title only applies to a new entry; an edited one keeps its name.
	private ConfigFlowResult Finish(PrinterSetupResult result)
	{
		if (result.Show is { } show)
		{
			return show;
		}

		var values = new Dictionary<string, ConfigFlowValue>(result.Values)
		{
			[ConfigKeys.Url] = ConfigFlowValue.Plain(_baseUri!.ToString()),
			[ConfigKeys.Backend] = ConfigFlowValue.Plain(_backend!.Id),
		};
		return ConfigFlowResult.Complete(_requestedName ?? result.InstanceName ?? _baseUri.Host, values);
	}

	public async ValueTask DisposeAsync()
	{
		if (_setup is { } setup)
		{
			_setup = null;
			await setup.DisposeAsync();
		}

		_backend = null;
	}

	private ConfigFlowStep ConnectionStep()
	{
		var fields = new List<ActionParameter>();
		if (!_editing)
		{
			fields.Add(ActionParameter.Text(
				ConfigKeys.Name,
				label: Strings.Setup.Connection.Name.Label(),
				description: Strings.Setup.Connection.Name.Description(),
				placeholder: "Prusa MK3S"));
		}

		fields.Add(ActionParameter.Url(
			ConfigKeys.Url,
			label: Strings.Setup.Connection.Url.Label(),
			description: Strings.Setup.Connection.Url.Description(),
			placeholder: "http://octopi.local",
			required: true));

		return new ConfigFlowStep
		{
			StepId = ConnectionStepId,
			Title = Strings.Setup.Connection.Title(),
			Description = Strings.Setup.Connection.Description(),
			Fields = fields,
			AdvancedFields =
			[
				ActionParameter.Text(
					ConfigKeys.WebcamUrl,
					label: Strings.Setup.Connection.WebcamUrl.Label(),
					description: Strings.Setup.Connection.WebcamUrl.Description(),
					placeholder: "http://octopi.local/webcam/?action=stream"),
			],
		};
	}
}
