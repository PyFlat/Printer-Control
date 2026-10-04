# Agent guidance

**Printer Control** is a Macro Deck 3 out-of-process plugin (Windows, macOS and Linux) that connects Macro
Deck to 3D printers through the software that runs them: live printer state as variables and events,
printer control as actions, a print status widget, and the printer webcam as a video stream (SDK
3.0.0-beta.15). Each kind of printer software is a backend; OctoPrint is the only one shipped. A Moonraker
backend waits on the `feature/moonraker` branch until it has been tested on a real Klipper printer.
[README.md](README.md) is the user-facing guide; this file is the rule set for changing the code. Keep it
current when a rule stops matching reality.

## Orientation

```
src/PrinterControl/
  Program.cs                      builder chain + the named HttpClient and AddPrinterBackends
  PrinterControlIntegration.cs    lifecycle, config flow, the action list
  PrinterControlIntegration.*.cs  the same partial class per capability: Variables (eager, per printer),
                                  Events (printer events and state changes), Widgets
                                  (IWidgetTypeProvider + IUiProvider)
  Core/                           backend-neutral: PrinterConfig, PrinterSnapshot, PrinterModels (settings,
                                  files, outputs, events), PrinterConnection (abstract: state, reconnect
                                  loop), PrinterFeatures (the optional feature interfaces), PrinterRegistry,
                                  PrinterException, PrinterUrls, JsonRead
  Backends/
    IPrinterBackend.cs            the backend contract, PrinterBackends, AddPrinterBackends (registration)
    IPrinterSetup.cs              a backend's own setup steps after the address (signing in)
    OctoPrint/                    REST client, push frame parsing, sign-in (OctoPrintSetup: application
                                  keys or a pasted key), OctoPrintConnection, GPIO Control outputs
  ConfigFlow/                     PrinterConfigFlow (name, address and webcam, recognizes the printer
                                  software, then runs its IPrinterSetup), ConfigKeys, PrinterConfigReader
  Actions/                        PrinterAction (printer picker, error mapping, ConfirmAsync) + actions
  Variables/                      PrinterVariables: field list, ids, names, readings
  Ui/                             widget views and their shared parts (rings, gauges, path glyphs), options,
                                  samples + [UiPreview]s, config view
  VideoStreams/                   webcam provider, its integration partial, the webcam widget view
tests/PrinterControl.Tests/
  Support/FakeOctoPrint.cs        a Kestrel fake of OctoPrint's REST API, application keys, GPIO Control
                                  and push socket
  Support/IFakePrinterServer.cs   what the backend contract needs from a fake, FakeServerHost, NotAPrinterServer
  Backends/BackendContract.cs     the tests every backend passes against its fake; AllFakeServers
  Backends/Example/               a minimal polling backend, its fake and contract tests: the template
  EndToEndTests.cs                the plugin in PluginTestHarness against the fake
  VideoStreams/                   the webcam provider and widget against the harness
```

SDK behaviour is documented upstream in the
[Macro Deck repository](https://github.com/Macro-Deck-App/Macro-Deck/tree/main/docs/src/content/docs).
OctoPrint's API is documented at <https://docs.octoprint.org/en/master/api/>. Look there instead of guessing.

## Rules specific to this plugin

**Public API.** Persisted in users' profiles, widgets and automations. Renaming one breaks them:

- the plugin id `com.pyflat.printer-control` and the backend ids (`IPrinterBackend.Id`, stored per entry)
- the printer key (`PrinterConfig.Key`, the config entry id as 32 hex digits). Variable ids, widget data
  and stream ids store it, which is why a printer rename breaks nothing
- variable ids `p<key>-<field>` and `p<key>-output-<output id>`, names `printer_<printer>_<field>` and
  `printer_<printer>_output_<output>` (`PrinterVariables`, a test pins them)
- action ids and parameter names, event ids and payload names, the status ids in `PrinterStatusText.Id`
- button state ids of the state provider actions (`on`/`off`, `printing`/`paused`/`idle`,
  `connected`/`connecting`/`disconnected`, `unavailable`); a button that adopted one stores it
- widget type ids and the widget data keys (`WidgetTypes`, `WidgetOptions`)
- config keys (`ConfigKeys`), the provider id `webcams`
- ids a backend hands out and actions store: file ids, system command ids, output ids. OctoPrint's are
  `local:<path>`, `<source>/<action>` and `gpio<pin>` (GPIO Control's API addresses outputs by list
  position, which reordering changes, so the id follows the pin)

**Backends.** Everything outside `Backends/` talks to `PrinterConnection` and the `Core` types only; no
backend type leaks into actions, variables, events or the UI.

- A connection pushes its state (`PublishSnapshot`, `PublishSettings`, `PublishEvent`,
  `PublishOutputStates`); readers never wait on the network. Prefer the server's push channel to polling.
- `RunSessionAsync` connects and returns only when the connection dropped; the base class retries with
  backoff (2 s doubling to 30 s), logs once per kind of failure and resets to offline in between.
- `PrinterConnection` only runs the session and holds the state. Commands come from the feature
  interfaces in `Core/PrinterFeatures.cs` (`IJobControl`, `IFileControl`, `ITemperatureControl`,
  `IGcodeControl`, `IMotionControl`, `IPrinterLink`, `ISystemCommands`, `IOutputControl`); a connection
  implements the ones its server supports. Actions get one through `PrinterAction.Require<T>`, which
  reports the action as unavailable on a printer without it.
- `IMotionControl` defaults to plain G-code (`G28`, relative `G1`, `M220`, `M221`) on top of
  `IGcodeControl`. Override a method only when the server has a better call for it.
- Commands throw `PrinterException`; `PrinterFailure.NotSupported` for a single command the server lacks
  within a feature it otherwise has.
- The backend maps its own events onto `PrinterEventKind` and drops duplicates; the integration only
  publishes.
- Read server JSON through `JsonRead`: fields are missing, null or retyped between versions and plugins.

**OctoPrint.**

- `OctoPrintConnection` logs in passively with the API key, reads `/api/settings`, then authenticates the
  push socket (`/sockjs/websocket`) with `user:session`. State comes from push frames. The one exception
  is GPIO Control, which pushes nothing: its states are read every 2 s while the socket is up and the
  settings list outputs (`PollGpioAsync`).
- A `current` frame only carries the temperature samples since the last one, often none; keep the previous
  reading (`PushMessages.MergeTemperatures`).
- OctoPrint 1.9 moved the webcam settings to `plugins.classicwebcam`; older servers use `webcam`. Both are read.
- OctoPrint reports a cancel as `PrintCancelled` and again as `PrintFailed` with reason `cancelled`; only
  the first becomes `print-cancelled` (`PushMessages.ReadEvent`).

**Actions.**

- Every action takes the `printer` dynamic choice first; empty means the only printer.
- `ActionResult` stays truthful. Job, connection and temperature actions wait (bounded, 5 s) for the
  pushed state to show the result (`PrinterAction.ConfirmAsync`) and answer `Accepted` when it has not
  arrived. Switching an output reads the state back. Moves, extrusion, fan, rates and G-code are only
  queued by the server and cannot be confirmed, so they answer `Accepted`. Never turn either into `Success`.
- A 409 (`PrinterFailure.Conflict`) means the printer is in the wrong state; it maps to `ProviderRejected`.

**Secrets.** The API key lives only in the host's secret store (`ConfigFlowValue.Secret`). It never goes
into a log line, a variable, an event, a widget or a video stream description.

**Video streams.** `IVideoStreamIntegration` (SDK 3.0.0-beta.15) lives in `VideoStreams/`. It is still
behind `EnableVideoStreams` (on by default) and `#if VIDEO_STREAMS` in `WidgetTypes` and
`PrinterControlIntegration.Widgets`; remove both in a follow-up.

- The webcam is described as `VideoStreamSessionDescription.Mjpeg`, pointing at the server's stream.
  Macro Deck fetches that URL itself and relays the media to its clients, so it only has to be reachable
  from this machine and must carry no credential. `PrinterConnection.WebcamStream` resolves a relative
  stream against the server and swaps a loopback host for the server's host (`PrinterUrls.ResolveStream`).
- The webcam widget builds its `UiVideoStream` once per session. Only the overlay is re-templated, so a
  progress frame never makes the client reopen the stream (a test checks the patches).

## Adding a backend

Start from `tests/PrinterControl.Tests/Backends/Example/`, the smallest complete backend. The backend
contract keeps it working, so it is always a correct starting point.

1. Copy `ExampleBackend.cs` to `src/PrinterControl/Backends/<Name>/` and give the backend a new,
   permanent `Id`. Replace the made-up protocol with the server's API: the session loop (prefer the
   server's push channel to polling), the state mapping and the feature interfaces its server supports.
   Start with none and add them one by one; `IMotionControl` comes free with `IGcodeControl`.
2. `DetectAsync`: recognize the server from an endpoint it answers without credentials, and rule out
   servers that imitate it (Moonraker answers OctoPrint's `/api/version`). Setup tries every backend in
   registration order, so a detection must not claim another backend's server.
3. `CreateSetup`: the steps after the shared address step. A server without a login returns
   `PrinterSetupResult.Done` from `StartAsync`; one with a login asks for it and returns `Done` with the
   server's own name and any granted key (`ConfigKeys.ApiKey`, as a secret). Name, address, webcam and
   the backend id are handled by `PrinterConfigFlow`.
4. Map the server's states onto `PrinterStatus` and its events onto `PrinterEventKind`; heaters are named
   the ids in `HeaterIds` (`extruder`, `bed`, `chamber`), mapped from the server's own names at the edge.
5. Register it in `AddPrinterBackends`. Setup strings go under `Setup.<Name>.*` in `Strings.resx`.
6. Tests: copy `ExampleServer.cs` to `Support/` as the fake of the real API (it implements
   `IFakePrinterServer`), add it to `AllFakeServers`, and derive a `<Name>ContractTests` from
   `BackendContract` like `ExampleContractTests`. The backend is done when the contract passes; add
   tests for what is special about it next to it.
7. README: the supported table, setup steps, and every network call it makes in Privacy.
8. Ship it only once it has run against a real server, tested by someone who keeps access to one and can
   reproduce its bug reports. The Creator Guidelines (Quality and Functionality) refuse features the
   publisher cannot test; until then the backend stays on its own branch.

## SDK rules

- Identity lives in `manifest.json` only. Ids in source are local ids (`^[a-z][a-z0-9]*(-[a-z0-9]+)*$`).
- Keep `Program.cs` as the plain builder chain. Constructors must be side-effect free: `Build()` constructs
  everything to validate it. Never set a listener URL. Only write to `MACRO_DECK_PLUGIN_DATA_DIRECTORY`.
- `InitializeAsync` runs again after every reconnect and config change, so it must be idempotent
  (`PrinterRegistry.ApplyAsync` keeps unchanged connections). Never call `CatalogChanged` from it.
- Variables are eager, one set per printer grouped by `VariableConfiguration`, like the built-in OBS and
  Twitch integrations. An on-demand catalog would hide them from the normal pickers (the history graph's
  included). `DeclaredVariables` shows templates until a printer exists, and
  `AnnounceVariablesAfterInitialization` sends `CatalogChanged` once per printer set, after
  `InitializeAsync` returns, because the host's first describe can run before the printers are read.
  Never send it from inside `InitializeAsync`. Temperatures are whole degrees for the history graph.
- Forward `context.CancellationToken`. No blocking waits and no `async void` in SDK contract types.
- Every user-facing string is a dotted key in `Localization/Strings.resx`; check `MacroDeckStrings` first.
  A text that depends on a value is one key with a placeholder, never a concatenation. Log and exception
  messages stay English literals. The server's own texts (state text, file and profile names) pass through.
- Every `UiElement.Key` must match `^[A-Za-z0-9][A-Za-z0-9._-]*$`; build every view through a real
  `UiView` in a test. A widget data schema key the Set Border action writes (`border`) must stay allowed.
- Log through Serilog. Structured properties are not persisted to the log file, so put what matters into
  the message template.

## Code style

- Build warning-free in both configurations. Do not relax `Directory.Build.props`.
- C# is tab-indented.
- Prefer no comment. Write one only for what the code cannot say: a race, a workaround, a protocol quirk,
  why a shape was chosen. Never restate what a well-named type, method or property already says; if a
  name needs a comment to be understood, rename it.
- A comment is one or two plain English `//` lines directly above the code it explains. No XML doc
  summaries, no banners or dividers. A link to the server's API docs is a fine comment on a backend.
- No em dashes in code, comments, commits or docs.

## Verifying a change

```bash
make build && make test
make conformance                           # after changing capability shape, cancellation or the manifest
make preview STORE=1                       # store images of every [UiPreview] in artifacts/previews/
```

`make preview` renders each `[UiPreview]` scenario (`Ui/WidgetSamples.cs`) at 1x1, 2x1 and 2x2 deck
cells; `STORE=1` pads them onto the 16:9 canvas the store crops card artwork to (`tools/StoreCanvas.cs`).
Text comes from the plugin's catalog only with a `macrodeck-plugin` newer than 3.0.0-beta.15; older ones
draw it as `[[plugin:...]]` placeholders.

The tests run the plugin against fake servers, so they need no printer. To see a real one, `make run`
with Macro Deck running and a printer server on the network.

## Store gate

Before a release, fetch the Creator Guidelines, the blocked packages and the SDK policy again (URLs in
the [Device Battery Info AGENTS.md](https://github.com/PyFlat/Device-Battery-Info/blob/main/AGENTS.md#store-gate))
and check the change against them. The README's Privacy section must list every network call and every
file the plugin stores.

- The display name is "Printer Control" and the README says the plugin is unofficial and not endorsed by
  the projects it talks to: a name that is only a third-party product name would read as that product's
  own plugin (guideline 5).
- `ai.generatedAssets` is `true` because `Assets/icon.svg` was drawn with AI (guideline 9). Set it back to
  `false` only together with replacing the icon by one that was not.

## Workflow

- Work on a branch (`feature/`, `fix/`, `refactor/`, `chore/`, `docs/`, `ci/`), keep changes focused.
- Do not push or open a pull request unless asked. No AI attribution or co-author trailers.
- A release is a pushed `vX.Y.Z` tag matching `manifest.json`'s `version` (`make release`).
- Update README.md when the build, run, packaging or user-facing behaviour changes.
