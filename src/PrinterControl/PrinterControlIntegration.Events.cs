using System.Text.Json;
using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;
using PrinterControl.Core;

namespace PrinterControl;

// Event ids and payload names are persisted in users' automations; never rename one.
internal sealed partial class PrinterControlIntegration : IEventProvider
{
	internal const string PrintStartedEvent = "print-started";
	internal const string PrintDoneEvent = "print-done";
	internal const string PrintFailedEvent = "print-failed";
	internal const string PrintCancelledEvent = "print-cancelled";
	internal const string PrintPausedEvent = "print-paused";
	internal const string PrintResumedEvent = "print-resumed";
	internal const string ProgressChangedEvent = "progress-changed";
	internal const string StatusChangedEvent = "status-changed";
	internal const string PrinterConnectedEvent = "printer-connected";
	internal const string PrinterDisconnectedEvent = "printer-disconnected";
	internal const string PrinterErrorEvent = "printer-error";
	internal const string PrinterEventEvent = "printer-event";

	public IReadOnlyList<EventDefinition> EventDefinitions =>
	[
		Define(PrintStartedEvent, Strings.Events.PrintStarted.Name(), Strings.Events.PrintStarted.Description(), File),
		Define(PrintDoneEvent, Strings.Events.PrintDone.Name(), Strings.Events.PrintDone.Description(), File, Duration),
		Define(PrintFailedEvent, Strings.Events.PrintFailed.Name(), Strings.Events.PrintFailed.Description(), File, Reason),
		Define(PrintCancelledEvent, Strings.Events.PrintCancelled.Name(), Strings.Events.PrintCancelled.Description(), File),
		Define(PrintPausedEvent, Strings.Events.PrintPaused.Name(), Strings.Events.PrintPaused.Description(), File),
		Define(PrintResumedEvent, Strings.Events.PrintResumed.Name(), Strings.Events.PrintResumed.Description(), File),
		Define(ProgressChangedEvent, Strings.Events.ProgressChanged.Name(), Strings.Events.ProgressChanged.Description(), File, Percent),
		Define(StatusChangedEvent, Strings.Events.StatusChanged.Name(), Strings.Events.StatusChanged.Description(), Status, PreviousStatus),
		Define(PrinterConnectedEvent, Strings.Events.PrinterConnected.Name(), Strings.Events.PrinterConnected.Description()),
		Define(PrinterDisconnectedEvent, Strings.Events.PrinterDisconnected.Name(), Strings.Events.PrinterDisconnected.Description()),
		Define(PrinterErrorEvent, Strings.Events.PrinterError.Name(), Strings.Events.PrinterError.Description(), Reason),
		Define(PrinterEventEvent, Strings.Events.PrinterEvent.Name(), Strings.Events.PrinterEvent.Description(), Type, Payload),
	];

	private static ActionParameter File => ActionParameter.Text("file", Strings.Events.Fields.File());

	private static ActionParameter Duration => ActionParameter.Number("duration", Strings.Events.Fields.Duration());

	private static ActionParameter Reason => ActionParameter.Text("reason", Strings.Events.Fields.Reason());

	private static ActionParameter Percent => ActionParameter.Number("percent", Strings.Events.Fields.Percent());

	private static ActionParameter Status => ActionParameter.Text("status", Strings.Events.Fields.Status());

	private static ActionParameter PreviousStatus => ActionParameter.Text("previous", Strings.Events.Fields.PreviousStatus());

	private static ActionParameter Type => ActionParameter.Text("type", Strings.Events.Fields.Type());

	private static ActionParameter Payload => ActionParameter.Text("payload", Strings.Events.Fields.Payload());

	private static EventDefinition Define(string id, LocalizedText name, LocalizedText description, params ActionParameter[] fields) => new()
	{
		Id = id,
		Name = name,
		Description = description,
		PayloadParameters =
		[
			ActionParameter.Text("printer", Strings.Events.Fields.Printer()),
			ActionParameter.Text("printer_id", Strings.Events.Fields.PrinterId()),
			.. fields,
		],
	};

	private void OnPrinterEvent(object? sender, PrinterEventArgs<PrinterEvent> e)
	{
		var received = e.Data;
		Publish(e.Printer, PrinterEventEvent, new()
		{
			["type"] = received.Type,
			["payload"] = received.Payload.ValueKind == JsonValueKind.Undefined ? string.Empty : received.Payload.GetRawText(),
		});

		if (received.Kind is not { } kind)
		{
			return;
		}

		var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
		if (received.File is { } file)
		{
			fields["file"] = file;
		}

		if (received.Duration is { } duration)
		{
			fields["duration"] = duration;
		}

		if (received.Reason is { } reason)
		{
			fields["reason"] = reason;
		}

		Publish(e.Printer, EventId(kind), fields);
	}

	private static string EventId(PrinterEventKind kind) => kind switch
	{
		PrinterEventKind.PrintStarted => PrintStartedEvent,
		PrinterEventKind.PrintDone => PrintDoneEvent,
		PrinterEventKind.PrintFailed => PrintFailedEvent,
		PrinterEventKind.PrintCancelled => PrintCancelledEvent,
		PrinterEventKind.PrintPaused => PrintPausedEvent,
		PrinterEventKind.PrintResumed => PrintResumedEvent,
		PrinterEventKind.Connected => PrinterConnectedEvent,
		PrinterEventKind.Disconnected => PrinterDisconnectedEvent,
		_ => PrinterErrorEvent,
	};

	private void PublishStateEvents(PrinterConnection printer, PrinterSnapshot previous, PrinterSnapshot current)
	{
		// The first state after the plugin started is a baseline, not a change.
		if (ReferenceEquals(previous, PrinterSnapshot.Offline))
		{
			return;
		}

		if (previous.Status != current.Status)
		{
			Publish(printer, StatusChangedEvent, new()
			{
				["status"] = PrinterStatusText.Id(current.Status),
				["previous"] = PrinterStatusText.Id(previous.Status),
			});
		}

		if (current.IsJobActive
			&& current.Completion is { } completion
			&& (int)completion != (int)(previous.Completion ?? -1))
		{
			Publish(printer, ProgressChangedEvent, new()
			{
				["file"] = current.FileName ?? string.Empty,
				["percent"] = (double)(int)completion,
			});
		}
	}

	private void Publish(PrinterConnection printer, string eventId, Dictionary<string, object?> fields)
	{
		fields["printer"] = printer.Config.DisplayName;
		fields["printer_id"] = printer.Config.Key;
		_context?.Events.Publish(eventId, fields);
	}
}
