using MacroDeck.Localization;
using MacroDeck.Sdk.ConfigFlow;
using NUnit.Framework;
using PrinterControl.Backends;
using PrinterControl.Backends.OctoPrint;
using PrinterControl.ConfigFlow;
using PrinterControl.Core;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests;

internal sealed class FakeFlowContext(string? entryTitle = null) : IConfigFlowEntryContext
{
	public IOAuthSession OAuth => throw new NotSupportedException();

	public string? EntryTitle => entryTitle;
}

[TestFixture]
public sealed class ConfigFlowTests
{
	private static readonly IReadOnlyDictionary<string, object?> NoInput = new Dictionary<string, object?>();

	private FakeOctoPrint _server = null!;

	[SetUp]
	public async Task StartServer() => _server = await FakeOctoPrint.StartAsync();

	[TearDown]
	public async Task StopServer() => await _server.DisposeAsync();

	private static PrinterConfigFlow Flow() => new(
		new PrinterBackends([new OctoPrintBackend(new TestHttpClientFactory(), Serilog.Core.Logger.None)]),
		Serilog.Core.Logger.None);

	private Dictionary<string, object?> Address(string? name = null, string? url = null) => new()
	{
		[ConfigKeys.Url] = url ?? _server.BaseUri.ToString(),
		[ConfigKeys.Name] = name,
	};

	private static Dictionary<string, object?> SignIn(string auth, string? apiKey = null) => new()
	{
		[ConfigKeys.Auth] = auth,
		[ConfigKeys.ApiKey] = apiKey,
	};

	private async Task<ConfigFlowResult> ToSignInAsync(PrinterConfigFlow flow, string? entryTitle = null, string? name = null)
	{
		await flow.StartAsync(new FakeFlowContext(entryTitle), CancellationToken.None);
		return await flow.SubmitAsync(PrinterConfigFlow.ConnectionStepId, Address(name), new FakeFlowContext(entryTitle), CancellationToken.None);
	}

	[Test]
	public async Task OctoPrint_is_recognized_and_asks_to_sign_in()
	{
		await using var flow = Flow();

		var signIn = await ToSignInAsync(flow);

		Assert.That(signIn.NextStep?.StepId, Is.EqualTo(OctoPrintSetup.SignInStepId));
	}

	[Test]
	public async Task Approving_in_octoprint_stores_the_granted_key()
	{
		await using var flow = Flow();
		await ToSignInAsync(flow);

		var authorize = await flow.SubmitAsync(OctoPrintSetup.SignInStepId, SignIn(ConfigKeys.AuthAppKeys), new FakeFlowContext(), CancellationToken.None);
		Assert.That(authorize.NextStep?.StepId, Is.EqualTo(OctoPrintSetup.AuthorizeStepId));

		var pending = await flow.SubmitAsync(OctoPrintSetup.AuthorizeStepId, NoInput, new FakeFlowContext(), CancellationToken.None);
		Assert.That(pending.Kind, Is.EqualTo(ConfigFlowResultKind.Error));

		_server.AppKeyAllowed = true;
		var done = await flow.SubmitAsync(OctoPrintSetup.AuthorizeStepId, NoInput, new FakeFlowContext(), CancellationToken.None);
		using (Assert.EnterMultipleScope())
		{
			Assert.That(done.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
			Assert.That(done.EntryTitle, Is.EqualTo("Fake MK3S"), "defaults to OctoPrint's own instance name");
			Assert.That(done.Values?[ConfigKeys.ApiKey].IsSecret, Is.True);
			Assert.That(done.Values?[ConfigKeys.ApiKey].Value, Is.EqualTo(FakeOctoPrint.ApiKey));
			Assert.That(done.Values?[ConfigKeys.Backend].Value, Is.EqualTo(OctoPrintBackend.BackendId));
			Assert.That(done.Values?[ConfigKeys.Url].Value, Is.EqualTo(_server.BaseUri.ToString()));
		}
	}

	[Test]
	public async Task The_request_stays_alive_while_the_user_takes_longer_than_octoprints_poll_timeout()
	{
		_server.AppKeyPollTimeout = TimeSpan.FromSeconds(3);
		await using var flow = Flow();
		await ToSignInAsync(flow);
		await flow.SubmitAsync(OctoPrintSetup.SignInStepId, SignIn(ConfigKeys.AuthAppKeys), new FakeFlowContext(), CancellationToken.None);

		await Task.Delay(TimeSpan.FromSeconds(5));
		_server.AppKeyAllowed = true;
		var done = await flow.SubmitAsync(OctoPrintSetup.AuthorizeStepId, NoInput, new FakeFlowContext(), CancellationToken.None);

		Assert.That(done.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
	}

	[Test]
	public async Task A_denied_request_goes_back_to_signing_in()
	{
		_server.DenyAppKey = true;
		await using var flow = Flow();
		await ToSignInAsync(flow);
		await flow.SubmitAsync(OctoPrintSetup.SignInStepId, SignIn(ConfigKeys.AuthAppKeys), new FakeFlowContext(), CancellationToken.None);

		var result = await flow.SubmitAsync(OctoPrintSetup.AuthorizeStepId, NoInput, new FakeFlowContext(), CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
			Assert.That(result.NextStep?.StepId, Is.EqualTo(OctoPrintSetup.SignInStepId));
		}
	}

	[Test]
	public async Task A_valid_api_key_completes_with_the_chosen_name()
	{
		await using var flow = Flow();
		await ToSignInAsync(flow, name: "Workshop");

		var result = await flow.SubmitAsync(
			OctoPrintSetup.SignInStepId,
			SignIn(ConfigKeys.AuthApiKey, FakeOctoPrint.ApiKey),
			new FakeFlowContext(),
			CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
			Assert.That(result.EntryTitle, Is.EqualTo("Workshop"));
			Assert.That(result.Values!.ContainsKey(ConfigKeys.ApiKey), Is.False, "a pasted key is stored as its form field");
		}
	}

	[Test]
	public async Task A_wrong_api_key_is_refused()
	{
		await using var flow = Flow();
		await ToSignInAsync(flow);

		var result = await flow.SubmitAsync(OctoPrintSetup.SignInStepId, SignIn(ConfigKeys.AuthApiKey, "wrong"), new FakeFlowContext(), CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
			Assert.That(result.NextStep?.StepId, Is.EqualTo(OctoPrintSetup.SignInStepId));
		}
	}

	[Test]
	public async Task An_invalid_address_is_a_field_error()
	{
		await using var flow = Flow();
		await flow.StartAsync(new FakeFlowContext(), CancellationToken.None);

		var result = await flow.SubmitAsync(PrinterConfigFlow.ConnectionStepId, Address(url: "ftp://nope"), new FakeFlowContext(), CancellationToken.None);

		Assert.That(result.FieldErrors?.Keys, Does.Contain(ConfigKeys.Url));
	}

	[Test]
	public async Task An_unreachable_address_is_reported()
	{
		await using var flow = Flow();
		await flow.StartAsync(new FakeFlowContext(), CancellationToken.None);

		var result = await flow.SubmitAsync(PrinterConfigFlow.ConnectionStepId, Address(url: "http://127.0.0.1:1"), new FakeFlowContext(), CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
			Assert.That(result.NextStep?.StepId, Is.EqualTo(PrinterConfigFlow.ConnectionStepId));
			Assert.That(result.ErrorMessage, Is.EqualTo((LocalizedText)Strings.Setup.Errors.Unreachable()));
		}
	}

	[Test]
	public async Task A_server_that_is_no_printer_software_is_reported()
	{
		await using var flow = Flow();
		await flow.StartAsync(new FakeFlowContext(), CancellationToken.None);

		var result = await flow.SubmitAsync(
			PrinterConfigFlow.ConnectionStepId,
			Address(url: new Uri(_server.BaseUri, "not-octoprint/").ToString()),
			new FakeFlowContext(),
			CancellationToken.None);

		Assert.That(result.ErrorMessage, Is.EqualTo((LocalizedText)Strings.Setup.Errors.Unsupported()));
	}

	[Test]
	public async Task Editing_offers_to_keep_the_current_key()
	{
		await using var flow = Flow();
		var start = await flow.StartAsync(new FakeFlowContext("Existing"), CancellationToken.None);
		Assert.That(start.NextStep!.Fields.Select(f => f.Name), Does.Not.Contain(ConfigKeys.Name), "an edited entry keeps its name");

		var signIn = await flow.SubmitAsync(PrinterConfigFlow.ConnectionStepId, Address(), new FakeFlowContext("Existing"), CancellationToken.None);
		Assert.That(signIn.NextStep!.Fields.Single(f => f.Name == ConfigKeys.Auth).DefaultValue, Is.EqualTo(ConfigKeys.AuthKeep));

		var result = await flow.SubmitAsync(OctoPrintSetup.SignInStepId, SignIn(ConfigKeys.AuthKeep), new FakeFlowContext("Existing"), CancellationToken.None);
		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete));
			Assert.That(result.Values!.ContainsKey(ConfigKeys.ApiKey), Is.False, "an empty secret keeps the stored one");
		}
	}
}

[TestFixture]
public sealed class PrinterDetectionTests
{
	private sealed class StubBackend(string id, bool matches, PrinterSetupResult? start = null) : IPrinterBackend
	{
		public int SetupsDisposed { get; private set; }

		public string Id => id;

		public LocalizedText Name => LocalizedText.FromLiteral(id);

		public bool RequiresApiKey => false;

		public Task<bool> DetectAsync(Uri baseUri, CancellationToken cancellationToken) => Task.FromResult(matches);

		public IPrinterSetup CreateSetup(Uri baseUri, bool editing) => new Setup(this, start ?? PrinterSetupResult.Done("From the server"));

		public PrinterConnection Connect(PrinterConfig config, HttpClient http, Serilog.ILogger logger) => throw new NotSupportedException();

		private sealed class Setup(StubBackend backend, PrinterSetupResult start) : IPrinterSetup
		{
			public Task<PrinterSetupResult> StartAsync(CancellationToken cancellationToken) => Task.FromResult(start);

			public Task<PrinterSetupResult> SubmitAsync(string stepId, IReadOnlyDictionary<string, object?> input, CancellationToken cancellationToken) =>
				Task.FromResult(PrinterSetupResult.Done());

			public ValueTask DisposeAsync()
			{
				backend.SetupsDisposed++;
				return ValueTask.CompletedTask;
			}
		}
	}

	private static PrinterConfigFlow Flow(params IPrinterBackend[] backends) => new(new PrinterBackends(backends), Serilog.Core.Logger.None);

	private static Task<ConfigFlowResult> SubmitAddress(PrinterConfigFlow flow, string? name = null) =>
		flow.SubmitAsync(
			PrinterConfigFlow.ConnectionStepId,
			new Dictionary<string, object?> { [ConfigKeys.Url] = "http://printer.local", [ConfigKeys.Name] = name },
			new FakeFlowContext(),
			CancellationToken.None);

	[Test]
	public async Task The_recognized_backend_runs_and_is_stored()
	{
		await using var flow = Flow(new StubBackend("first", matches: false), new StubBackend("second", matches: true));

		var result = await SubmitAddress(flow);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Complete), "a server without a login completes right after the address");
			Assert.That(result.Values![ConfigKeys.Backend].Value, Is.EqualTo("second"));
			Assert.That(result.Values[ConfigKeys.Url].Value, Is.EqualTo("http://printer.local/"));
			Assert.That(result.EntryTitle, Is.EqualTo("From the server"));
		}
	}

	[Test]
	public async Task The_users_name_wins_over_the_servers()
	{
		await using var flow = Flow(new StubBackend("only", matches: true));

		var result = await SubmitAddress(flow, name: "Workshop");

		Assert.That(result.EntryTitle, Is.EqualTo("Workshop"));
	}

	[Test]
	public async Task Going_back_to_the_address_ends_the_previous_setup()
	{
		var signIn = new ConfigFlowStep { StepId = "sign-in", Title = LocalizedText.FromLiteral("Sign in"), Fields = [] };
		var backend = new StubBackend("only", matches: true, PrinterSetupResult.Step(signIn));
		await using var flow = Flow(backend);

		await SubmitAddress(flow);
		await SubmitAddress(flow);

		Assert.That(backend.SetupsDisposed, Is.EqualTo(1));
	}

	[Test]
	public async Task No_recognized_backend_is_an_error_on_the_address_step()
	{
		await using var flow = Flow(new StubBackend("only", matches: false));

		var result = await SubmitAddress(flow);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.Kind, Is.EqualTo(ConfigFlowResultKind.Error));
			Assert.That(result.NextStep?.StepId, Is.EqualTo(PrinterConfigFlow.ConnectionStepId));
			Assert.That(result.ErrorMessage, Is.EqualTo((LocalizedText)Strings.Setup.Errors.Unsupported()));
		}
	}
}
