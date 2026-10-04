using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Runtime;

namespace PrinterControl.Ui;

internal sealed class UiViewSession : IUiSession
{
	private readonly UiView _view;
	private readonly Action? _onDispose;
	private readonly EventHandler _onViewChanged;
	private readonly EventHandler<UiHandlerFaultEventArgs> _onViewFaulted;

	public event EventHandler? Changed;
	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiViewSession(UiView view, Action? onDispose = null)
	{
		_view = view;
		_onDispose = onDispose;
		_onViewChanged = (_, _) => Changed?.Invoke(this, EventArgs.Empty);
		_onViewFaulted = (_, fault) =>
			Faulted?.Invoke(
				this,
				new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception)
			);
		_view.Changed += _onViewChanged;
		_view.HandlerFaulted += _onViewFaulted;
	}

	public UiTree BuildTree() => _view.Tree;

	public IReadOnlyList<UiPatch> DrainPatches() => _view.DrainPatches();

	public void Dispatch(UiEvent uiEvent) => _view.Dispatch(uiEvent);

	public ValueTask DisposeAsync()
	{
		_view.Changed -= _onViewChanged;
		_view.HandlerFaulted -= _onViewFaulted;
		_onDispose?.Invoke();
		return ValueTask.CompletedTask;
	}
}
