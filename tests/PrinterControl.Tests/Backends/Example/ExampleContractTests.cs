using NUnit.Framework;
using PrinterControl.Backends;
using PrinterControl.ConfigFlow;
using PrinterControl.Tests.Support;

namespace PrinterControl.Tests.Backends.Example;

// A new backend needs exactly this: the contract against its fake, plus tests for what is special about it.
[TestFixture]
public sealed class ExampleContractTests : BackendContract
{
	private protected override IPrinterBackend CreateBackend(IHttpClientFactory http) => new ExampleBackend(http);

	private protected override async Task<IFakePrinterServer> StartServerAsync() => await ExampleServer.StartAsync();

	[Test]
	public async Task Setup_needs_no_login_and_uses_the_servers_name()
	{
		await using var server = await ExampleServer.StartAsync();
		await using var flow = new PrinterConfigFlow(
			new PrinterBackends([new ExampleBackend(new TestHttpClientFactory())]),
			Serilog.Core.Logger.None);

		var result = await flow.SubmitAsync(
			PrinterConfigFlow.ConnectionStepId,
			new Dictionary<string, object?> { [ConfigKeys.Url] = server.BaseUri.ToString() },
			new FakeFlowContext(),
			CancellationToken.None);

		using (Assert.EnterMultipleScope())
		{
			Assert.That(result.EntryTitle, Is.EqualTo("Example MK1"));
			Assert.That(result.Values?[ConfigKeys.Backend].Value, Is.EqualTo(ExampleBackend.BackendId));
		}
	}
}
