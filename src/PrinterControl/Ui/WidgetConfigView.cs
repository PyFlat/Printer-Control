using System.Text.Json;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Config.Options;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Runtime;
using PrinterControl.Core;

namespace PrinterControl.Ui;

internal static class WidgetConfigView
{
	public static UiElement Build(string widgetLocalId, WidgetOptions current, JsonElement? data, IReadOnlyList<PrinterConfig> printers)
	{
		var printer = new UiState<string>(current.UsesFirstPrinter ? WidgetOptions.FirstPrinter : current.Printer);
		var showFile = new UiState<bool>(current.ShowFile);
		var showTemperatures = new UiState<bool>(current.ShowTemperatures);
		var showEta = new UiState<bool>(current.ShowEta);
		var overlay = new UiState<bool>(current.Overlay);
		var fit = new UiState<string>(current.Fit);
		var flows = new UiState<JsonElement>(
			data is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty("flows", out var stored)
				? stored.Clone()
				: JsonDocument.Parse("[]").RootElement.Clone());

		var printerOptions = new List<UiOption> { UiOption.Of(WidgetOptions.FirstPrinter) with { Label = Strings.Widgets.Config.FirstPrinter() } };
		printerOptions.AddRange(printers.Select(p => UiOption.Of(p.Key) with { Label = p.DisplayName }));

		var children = new List<UiElement>
		{
			new UiChoiceInput
			{
				Key = "printer",
				Label = Strings.Widgets.Config.Printer(),
				Options = UiValue.Of<IReadOnlyList<UiOption>>(printerOptions),
				Binding = Bind.To(printer),
			},
		};

		switch (widgetLocalId)
		{
			case WidgetTypes.StatusId:
				children.Add(Toggle("showFile", Strings.Widgets.Config.ShowFile(), showFile));
				children.Add(Toggle("showEta", Strings.Widgets.Config.ShowEta(), showEta));
				children.Add(Toggle("showTemperatures", Strings.Widgets.Config.ShowTemperatures(), showTemperatures));
				break;
			case WidgetTypes.WebcamId:
				children.Add(Toggle("overlay", Strings.Widgets.Config.Overlay(), overlay));
				children.Add(new UiChoiceInput
				{
					Key = "fit",
					Label = Strings.Widgets.Config.Fit(),
					Options = UiValue.Of<IReadOnlyList<UiOption>>(
					[
						UiOption.Of(WidgetOptions.FitContain) with { Label = Strings.Widgets.Config.FitContain() },
						UiOption.Of(WidgetOptions.FitCover) with { Label = Strings.Widgets.Config.FitCover() },
					]),
					Binding = Bind.To(fit),
				});
				break;
		}

		children.Add(new UiActionsListEditor
		{
			Key = "flows",
			Label = Strings.Widgets.Config.Actions(),
			Binding = Bind.To(flows),
			CanRun = true,
		});

		return new UiWidgetConfiguration
		{
			Key = "printer-widget-config",
			Properties = new UiWidgetProperties
			{
				Key = "printer-widget-config-properties",
				Children = children,
			},
		};
	}

	private static UiBooleanInput Toggle(string key, MacroDeck.Localization.LocalizedText label, UiState<bool> state) => new()
	{
		Key = key,
		Label = label,
		Binding = Bind.To(state),
	};
}
