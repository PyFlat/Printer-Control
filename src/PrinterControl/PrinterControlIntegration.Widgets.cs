using System.Text.Json;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using PrinterControl.Core;
using PrinterControl.Ui;

namespace PrinterControl;

internal sealed partial class PrinterControlIntegration : IWidgetTypeProvider, IUiProvider
{
	// Set once the webcam provider is registered; the qualified id every webcam widget names.
	private string WebcamProviderId { get; set; } = string.Empty;

	string IWidgetTypeProvider.ProviderName => "Printer Control";

	public IReadOnlyList<WidgetTypeDescriptor> GetWidgetTypes() => WidgetTypes.All;

	async Task IWidgetTypeProvider.InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken)
	{
		foreach (var descriptor in WidgetTypes.All)
		{
			var registration = await context.RegisterWidgetTypeAsync(descriptor, cancellationToken);
			_logger.Information("Registered widget type {LocalId} as {QualifiedId}.", descriptor.Id, registration.WidgetTypeId);
		}
	}

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive },
	];

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		var surface = request.Surface;
		IUiSession? session = surface.Kind switch
		{
			UiSurfaceKinds.Widget or UiSurfaceKinds.Preview => CreateWidgetSession(surface),
			UiSurfaceKinds.Config => CreateConfigSession(surface),
			_ => null,
		};

		return Task.FromResult(session);
	}

	private UiViewSession? CreateWidgetSession(UiSurface surface)
	{
		if (WidgetTypes.LocalId(Attribute(surface, UiWidgetSurfaceAttributes.WidgetType) ?? string.Empty) is not { } localId)
		{
			return null;
		}

		var cornerRadius = CornerRadius(surface);
		if (AttributeJson(surface, UiWidgetSurfaceAttributes.Sample) is { ValueKind: JsonValueKind.True })
		{
			var sample = new UiState<WidgetModel>(WidgetSamples.Printing);
			return BuildView(localId, surface, sample, cornerRadius, () => WidgetSamples.Now, WebcamProviderId, isSample: true) is { } sampleView
				? new UiViewSession(sampleView)
				: null;
		}

		var options = WidgetOptions.Parse(AttributeJson(surface, UiWidgetSurfaceAttributes.Data));
		WidgetModel Compute() => options.Resolve(_registry) is { } printer
			? new WidgetModel(
				printer.Config.DisplayName,
				printer.Snapshot,
				options,
				true,
				printer.Config.Key,
				WidgetModel.RotationFor(printer.Settings.Webcam))
			: WidgetModel.NoPrinter(options);

		var state = new UiState<WidgetModel>(Compute());
		if (BuildView(localId, surface, state, cornerRadius, () => DateTimeOffset.Now, WebcamProviderId) is not { } view)
		{
			return null;
		}

		void Refresh() => state.Set(Compute());
		void OnSnapshot(object? sender, PrinterEventArgs<PrinterSnapshotChangedEventArgs> e) => Refresh();
		void OnPrinters(object? sender, EventArgs e) => Refresh();
		_registry.SnapshotChanged += OnSnapshot;
		_registry.PrintersChanged += OnPrinters;

		return new UiViewSession(view, onDispose: () =>
		{
			_registry.SnapshotChanged -= OnSnapshot;
			_registry.PrintersChanged -= OnPrinters;
		});
	}

	private static UiView? BuildView(
		string localId,
		UiSurface surface,
		UiState<WidgetModel> state,
		int cornerRadius,
		Func<DateTimeOffset> clock,
		string webcamProviderId,
		bool isSample = false)
	{
		var root = localId switch
		{
			WidgetTypes.StatusId => StatusWidgetView.Build(state, cornerRadius, clock),
#if VIDEO_STREAMS
			WidgetTypes.WebcamId => isSample ? WebcamWidgetView.Sample(state, cornerRadius) : WebcamWidgetView.Build(state, cornerRadius, webcamProviderId),
#endif
			_ => null,
		};

		return root is null ? null : new UiView(surface, root);
	}

	private UiViewSession? CreateConfigSession(UiSurface surface)
	{
		if (Attribute(surface, UiConfigSurfaceAttributes.EntryPoint) != UiConfigEntryPoints.WidgetConfig
			|| WidgetTypes.LocalId(Attribute(surface, UiConfigSurfaceAttributes.WidgetType) ?? string.Empty) is not { } localId)
		{
			return null;
		}

		var data = AttributeJson(surface, UiConfigSurfaceAttributes.WidgetData);
		var view = WidgetConfigView.Build(localId, WidgetOptions.Parse(data), data, [.. _registry.Printers.Select(p => p.Config)]);
		return new UiViewSession(new UiView(surface, view));
	}

	private static string? Attribute(UiSurface surface, string key) =>
		surface.Attributes.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

	private static JsonElement? AttributeJson(UiSurface surface, string key) =>
		surface.Attributes.TryGetValue(key, out var value) ? value : null;

	private static int CornerRadius(UiSurface surface) =>
		surface.Attributes.TryGetValue(UiWidgetSurfaceAttributes.CornerRadius, out var value)
		&& value.ValueKind == JsonValueKind.Number
		&& value.TryGetInt32(out var radius)
			? radius
			: 16;
}
