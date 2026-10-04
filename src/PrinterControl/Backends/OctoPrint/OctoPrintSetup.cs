using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.ConfigFlow;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;
using Serilog;

namespace PrinterControl.Backends.OctoPrint;

internal sealed class OctoPrintSetup(Uri baseUri, bool editing, Func<string?, OctoPrintApi> api, ILogger logger) : IPrinterSetup
{
	public const string SignInStepId = "sign-in";
	public const string AuthorizeStepId = "authorize";

	// OctoPrint drops a pending request that has not been polled for 5 s, and any request after 10 min,
	// so the setup keeps polling while the user switches to OctoPrint instead of only on Continue.
	private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1.5);
	private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);

	private readonly ILogger _logger = logger.ForContext<OctoPrintSetup>();
	private CancellationTokenSource? _polling;
	private Task<AppKeyPoll>? _decision;
	private volatile PrinterException? _pollFailure;

	public Task<PrinterSetupResult> StartAsync(CancellationToken cancellationToken) =>
		Task.FromResult(PrinterSetupResult.Step(SignInStep()));

	public Task<PrinterSetupResult> SubmitAsync(
		string stepId,
		IReadOnlyDictionary<string, object?> input,
		CancellationToken cancellationToken) => stepId switch
	{
		SignInStepId => SubmitSignInAsync(input, cancellationToken),
		AuthorizeStepId => SubmitAuthorizeAsync(cancellationToken),
		_ => Task.FromResult(PrinterSetupResult.Step(SignInStep())),
	};

	private async Task<PrinterSetupResult> SubmitSignInAsync(IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken)
	{
		StopPolling();
		try
		{
			switch (Text(input, ConfigKeys.Auth) ?? ConfigKeys.AuthAppKeys)
			{
				case ConfigKeys.AuthKeep when editing:
					return PrinterSetupResult.Done();

				case ConfigKeys.AuthApiKey:
					if (Text(input, ConfigKeys.ApiKey) is not { } apiKey)
					{
						return PrinterSetupResult.Error(SignInStep(), fieldErrors: new Dictionary<string, LocalizedText>
						{
							[ConfigKeys.ApiKey] = MacroDeckStrings.Validation.Required(Strings.Setup.OctoPrint.ApiKey.Label()),
						});
					}

					return await SignedInAsync(api(apiKey), granted: null, cancellationToken);

				default:
					var anonymous = api(null);
					if (!await anonymous.SupportsAppKeysAsync(cancellationToken))
					{
						return PrinterSetupResult.Error(SignInStep(), Strings.Setup.OctoPrint.Errors.AppKeysUnavailable());
					}

					var request = await anonymous.RequestAppKeyAsync(Text(input, ConfigKeys.User), cancellationToken);
					_polling = new CancellationTokenSource();
					_decision = PollUntilDecidedAsync(anonymous, request, _polling.Token);
					return PrinterSetupResult.Step(AuthorizeStep());
			}
		}
		catch (PrinterException exception)
		{
			_logger.Information("Signing in to OctoPrint at {Url} failed: {Failure}.", baseUri, exception.Failure);
			return PrinterSetupResult.Error(SignInStep(), ErrorFor(exception.Failure));
		}
	}

	private async Task<PrinterSetupResult> SubmitAuthorizeAsync(CancellationToken cancellationToken)
	{
		if (_decision is null)
		{
			return PrinterSetupResult.Step(SignInStep());
		}

		// Allow pressed a moment ago may not have been polled yet.
		await Task.WhenAny(_decision, Task.Delay(PollInterval * 2, cancellationToken));
		if (!_decision.IsCompleted)
		{
			return PrinterSetupResult.Error(
				AuthorizeStep(),
				_pollFailure is { } failure ? ErrorFor(failure.Failure) : Strings.Setup.OctoPrint.Authorize.StillPending());
		}

		var poll = await _decision;
		if (poll.Decision != AppKeyDecision.Granted)
		{
			StopPolling();
			return PrinterSetupResult.Error(SignInStep(), Strings.Setup.OctoPrint.Authorize.Denied());
		}

		try
		{
			return await SignedInAsync(api(poll.ApiKey), poll.ApiKey, cancellationToken);
		}
		catch (PrinterException exception)
		{
			_logger.Information("Signing in with the granted application key at {Url} failed: {Failure}.", baseUri, exception.Failure);
			return PrinterSetupResult.Error(AuthorizeStep(), ErrorFor(exception.Failure));
		}
	}

	// A pasted key is a form field and stored as one; a granted key never was, so it is added here.
	private static async Task<PrinterSetupResult> SignedInAsync(OctoPrintApi signedIn, string? granted, CancellationToken cancellationToken)
	{
		await signedIn.LoginPassiveAsync(cancellationToken);
		string? instanceName;
		try
		{
			instanceName = (await signedIn.GetSettingsAsync(cancellationToken)).Common.InstanceName;
		}
		catch (PrinterException)
		{
			instanceName = null;
		}

		return PrinterSetupResult.Done(
			instanceName,
			granted is null ? null : new Dictionary<string, ConfigFlowValue> { [ConfigKeys.ApiKey] = ConfigFlowValue.Secret(granted) });
	}

	private async Task<AppKeyPoll> PollUntilDecidedAsync(OctoPrintApi anonymous, AppKeyRequest request, CancellationToken cancellationToken)
	{
		var deadline = DateTime.UtcNow + RequestLifetime;
		while (DateTime.UtcNow < deadline)
		{
			try
			{
				var poll = await anonymous.PollAppKeyAsync(request, cancellationToken);
				_pollFailure = null;
				if (poll.Decision != AppKeyDecision.Pending)
				{
					return poll;
				}
			}
			catch (PrinterException exception)
			{
				// Retried; once OctoPrint is back it answers 404 if the gap outlived its poll timeout.
				if (_pollFailure is null)
				{
					_logger.Information("Polling the application key at {Url} failed: {Failure}.", baseUri, exception.Failure);
				}

				_pollFailure = exception;
			}

			await Task.Delay(PollInterval, cancellationToken);
		}

		return new AppKeyPoll(AppKeyDecision.Denied, null);
	}

	private void StopPolling()
	{
		_polling?.Cancel();
		_polling?.Dispose();
		_polling = null;
		_decision = null;
		_pollFailure = null;
	}

	public ValueTask DisposeAsync()
	{
		StopPolling();
		return ValueTask.CompletedTask;
	}

	private static LocalizedText ErrorFor(PrinterFailure failure) => failure switch
	{
		PrinterFailure.Unreachable => Strings.Setup.Errors.Unreachable(),
		PrinterFailure.Unauthorized or PrinterFailure.Forbidden => Strings.Setup.OctoPrint.Errors.Unauthorized(),
		_ => Strings.Setup.OctoPrint.Errors.Unexpected(),
	};

	private static string? Text(IReadOnlyDictionary<string, object?> input, string key) =>
		input.GetValueOrDefault(key)?.ToString() is { } value && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

	private ConfigFlowStep SignInStep()
	{
		var authOptions = new List<ActionParameterOption>
		{
			new() { Value = ConfigKeys.AuthAppKeys, Label = Strings.Setup.OctoPrint.Auth.AppKeys() },
			new() { Value = ConfigKeys.AuthApiKey, Label = Strings.Setup.OctoPrint.Auth.ApiKey() },
		};
		if (editing)
		{
			authOptions.Insert(0, new() { Value = ConfigKeys.AuthKeep, Label = Strings.Setup.OctoPrint.Auth.Keep() });
		}

		return new ConfigFlowStep
		{
			StepId = SignInStepId,
			Title = Strings.Setup.OctoPrint.SignIn.Title(),
			Description = Strings.Setup.OctoPrint.SignIn.Description(),
			Fields =
			[
				ActionParameter.Choice(
					ConfigKeys.Auth,
					authOptions,
					label: Strings.Setup.OctoPrint.Auth.Label(),
					defaultValue: editing ? ConfigKeys.AuthKeep : ConfigKeys.AuthAppKeys,
					required: true),
				ActionParameter.Secret(
					ConfigKeys.ApiKey,
					label: Strings.Setup.OctoPrint.ApiKey.Label(),
					description: Strings.Setup.OctoPrint.ApiKey.Description()).OnlyWhen(ConfigKeys.Auth, ConfigKeys.AuthApiKey),
			],
			AdvancedFields =
			[
				ActionParameter.Text(
					ConfigKeys.User,
					label: Strings.Setup.OctoPrint.User.Label(),
					description: Strings.Setup.OctoPrint.User.Description()),
			],
		};
	}

	private ConfigFlowStep AuthorizeStep() => new()
	{
		StepId = AuthorizeStepId,
		Title = Strings.Setup.OctoPrint.Authorize.Title(),
		Description = Strings.Setup.OctoPrint.Authorize.Description(),
		Instructions =
		[
			new ConfigFlowInstruction { Text = Strings.Setup.OctoPrint.Authorize.OpenOctoPrint() },
			new ConfigFlowInstruction { Text = Strings.Setup.OctoPrint.Authorize.Allow() },
			new ConfigFlowInstruction { Text = Strings.Setup.OctoPrint.Authorize.Continue() },
		],
		Links = [new ConfigFlowLink { Label = Strings.Setup.OctoPrint.Authorize.Link(), Url = baseUri.ToString() }],
		Fields = [],
	};
}
