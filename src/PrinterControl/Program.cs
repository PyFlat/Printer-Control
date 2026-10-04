using MacroDeck.Plugin.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MacroDeck.Plugin.Serilog;
using PrinterControl;
using PrinterControl.Backends;
using PrinterControl.Core;

var builder = MacroDeckPlugin.CreatePlugin(args)
	.UseMacroDeckLogging()
	.UseLocalization(Strings.LocalizationCatalog)
	.RegisterIntegration<PrinterControlIntegration>();

builder.Services.AddHttpClient(PrinterRegistry.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10))
	.ConfigurePrimaryHttpMessageHandler(SameHostRedirectHandler.CreatePipeline);
builder.Services.AddPrinterBackends();

var plugin = builder.Build();

await plugin.RunAsync();
