using MacroDeck.Localization;
using MacroDeck.Sdk.Actions;
using PrinterControl.Core;

namespace PrinterControl.Actions;

internal sealed class PrintFileAction(PrinterRegistry registry) : PrinterAction(registry)
{
	public const string FileParameter = "file";
	public const string StartParameter = "start";

	public override string Id => "print-file";

	public override LocalizedText Name => Strings.Actions.PrintFile.Name();

	public override LocalizedText Description => Strings.Actions.PrintFile.Description();

	protected override IReadOnlyList<ActionParameter> ActionParameters { get; } =
	[
		ActionParameter.DynamicChoice(FileParameter, label: Strings.Actions.PrintFile.File(), required: true),
		ActionParameter.Toggle(StartParameter, label: Strings.Actions.PrintFile.Start(), defaultValue: true),
	];

	protected override async Task<DynamicOptionsResult> GetOptionsAsync(
		PrinterConnection printer,
		DynamicOptionsContext context,
		CancellationToken cancellationToken)
	{
		var files = await Require<IFileControl>(printer).GetFilesAsync(cancellationToken);
		IEnumerable<PrintFile> matching = files.OrderByDescending(f => f.Date);
		if (!string.IsNullOrWhiteSpace(context.Filter))
		{
			matching = matching.Where(f => f.Display.Contains(context.Filter, StringComparison.OrdinalIgnoreCase));
		}

		return new DynamicOptionsResult
		{
			Options = [.. matching.Select(f => new ActionParameterOption
			{
				Value = f.Id,
				Label = f.Display,
			})],
			AllowsCustomValue = true,
			CacheSeconds = 10,
		};
	}

	protected override async Task<ActionResult> RunAsync(PrinterConnection printer, ActionExecutionContext context)
	{
		if (Text(context, FileParameter) is not { } file)
		{
			return Invalid(Strings.Actions.PrintFile.File());
		}

		var start = Flag(context, StartParameter) ?? true;
		if (start && printer.Snapshot.IsJobActive)
		{
			return ActionResult.Failed(ActionErrorCodes.ProviderRejected, Strings.Errors.AlreadyPrinting());
		}

		await Require<IFileControl>(printer).SelectFileAsync(file, start, context.CancellationToken);
		return start
			? await ConfirmAsync(printer, s => s.IsJobActive, Strings.Actions.Job.Starting(), context.CancellationToken)
			: ActionResult.Success();
	}
}
