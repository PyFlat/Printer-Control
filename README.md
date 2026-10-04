# Printer Control for Macro Deck

Watch and control your 3D printers from Macro Deck: print progress, time left and temperatures on your
deck, buttons to pause, resume, cancel, preheat, move the print head, switch the light or send G-code, and
automations that react when a print starts, finishes or fails. The printer's **webcam** comes to your deck
too, with the print progress on top of the picture.

Printer Control talks to the software that runs your printer. Supported today:

| Printer software | Notes |
| --- | --- |
| [OctoPrint](https://octoprint.org) | Everything below. Lights need the GPIO Control plugin. |

The plugin is built so more printer software can be added; see
[Adding printer software](#adding-printer-software). This is an unofficial community plugin, not made or
endorsed by any of these projects.

Any number of printers can be set up, each with its own server.

## Setting up a printer

1. Open the Printer Control integration in Macro Deck and add a printer.
2. Enter the address you open your printer's web interface at, for example `http://octopi.local`.
   Printer Control recognizes the printer software there by itself.
3. Choose **Approve in OctoPrint**. OctoPrint shows an "Access Request" dialog for Macro Deck in its web
   interface; choose **Allow** there, then continue in Macro Deck. If the application keys plugin is
   turned off in your OctoPrint, choose **Paste an API key** instead and create a key in OctoPrint under
   *Settings > Application Keys*.

Leave the name empty to use the printer's own name, the name set in OctoPrint (*Settings > Appearance*).
The advanced settings of the address step take a different webcam stream; those of OctoPrint's sign-in
step limit the approval to one OctoPrint user.

## Widgets

| Widget | Shows |
| --- | --- |
| **Printer status** | While printing: a progress ring with the percentage and time left, the file, and when it finishes on wide tiles. Otherwise the printer's state, with hotend and bed gauges that fill as they heat. Hotend and bed temperatures below. Adapts to square, wide and tall tiles. |
| **Printer webcam** | The webcam, with the progress as an overlay while printing. |

Every widget can run your own actions when pressed, for example the *Control print job* action to pause.

## Actions

Every action starts with the printer to use; with only one printer set up, leave it empty.

| Action | What it does |
| --- | --- |
| Control print job | Pause or resume, pause, resume, cancel, start the selected file, restart. Waits until the printer confirms. |
| Print a file | Picks a file stored on the printer's server (or on the SD card) and starts it. |
| Set temperature | Sets the hotend, bed or chamber target. 0 turns it off. |
| Preheat | Uses one of the printer's temperature presets (PLA, PETG, ...) or cools everything down. |
| Home axes | Homes X, Y and/or Z. |
| Move print head | Moves one axis by a distance. |
| Extrude or retract | Moves filament. |
| Set part cooling fan | 0 to 100 %. |
| Set feed or flow rate | 50 to 200 %. |
| Send G-code | Any commands, one per line. |
| Connect printer | Connects the server to the printer or disconnects it. |
| Run system command | Restart the server, reboot or shut down the Raspberry Pi, or your own custom commands. |
| Switch output | Turns a light or relay on, off, or toggles it. On OctoPrint this needs the [GPIO Control](https://plugins.octoprint.org/plugins/gpiocontrol/) plugin. |

Moves, extrusion, the fan, rates and G-code are queued by the server, which does not report when the
printer ran them, so Macro Deck shows them as sent rather than done.

Switch output, Control print job and Connect printer also provide a button state. In the button editor,
use the action as the button's state provider. The button then follows the light (on, off), the job
(printing, paused, ready) or the connection (connected, connecting, not connected), and each state can
get its own label, colours and icon. While the printer is offline the button shows "Unavailable".

An output's state is the level of its pin. A light driven by a short pulse (an impulse relay or an
emulated push button) only shows "on" for that moment, so the button cannot follow it. For such a
light, use the button's own states with "cycle states on press" instead.

## Variables

For each printer, `<printer>` being its name in lowercase with `_` (for example `prusa_mk3s`):

| Variable | Value |
| --- | --- |
| `printer_<printer>_status` | `offline`, `disconnected`, `connecting`, `operational`, `printing`, `pausing`, `paused`, `resuming`, `cancelling`, `finishing` or `error` |
| `printer_<printer>_state` | The server's own state text |
| `printer_<printer>_online`, `_connected`, `_printing`, `_paused` | true or false |
| `printer_<printer>_progress` | Print progress in % |
| `printer_<printer>_file` | The selected file |
| `printer_<printer>_print_time`, `_time_left`, `_estimated_time` | Seconds, shown as a duration |
| `printer_<printer>_eta` | When the print finishes, `HH:mm` |
| `printer_<printer>_z` | Current Z height in mm |
| `printer_<printer>_tool_temp`, `_tool_target`, `_bed_temp`, `_bed_target`, `_chamber_temp`, `_chamber_target` | °C in whole degrees, so the built-in history graph widget can chart them |
| `printer_<printer>_error` | The last error the server reported |
| `printer_<printer>_output_<output>` | true while an output (a light, a relay) is on, one per output. On OctoPrint these are the [GPIO Control](https://plugins.octoprint.org/plugins/gpiocontrol/) outputs, read every 2 seconds since GPIO Control does not push changes. |

Renaming a printer changes its variable names, not the variables already bound to buttons.

## Events

`print-started`, `print-done`, `print-failed`, `print-cancelled`, `print-paused`, `print-resumed`,
`progress-changed` (every whole percent), `status-changed`, `printer-connected`, `printer-disconnected`,
`printer-error`, and `printer-event`, which carries every event the server or its plugins report
(`type` and the `payload` as JSON). Each one names the `printer`.

## Webcam

Each printer's webcam is offered as a video stream called *Printer webcams*, usable in the printer webcam
widget and anywhere else Macro Deck shows video.

- The stream is the one set up in the server (in OctoPrint: *Settings > Webcam & Timelapse*), or the
  address entered when setting up the printer. It has to be an MJPEG stream, which is what OctoPi serves
  by default.
- Macro Deck loads the picture from the webcam and relays it to your deck devices, so only the computer
  running Macro Deck has to reach the webcam. A webcam the server lists on `localhost` is reached through
  the server's address.
- *Rotate 90°* and *flip both ways* settings are applied. Flipping only one way is not.

## Privacy

The plugin only talks to the printer servers you set up. While you add a printer, it asks the address you
entered which printer software runs there (`/api/version`), without credentials. After that:

- the OctoPrint REST API (`/api/...`) to read settings and files and to send your commands,
- OctoPrint's push socket (`/sockjs/websocket`) for live state,
- the application keys plugin (`/plugin/appkeys/...`) while you approve Macro Deck,
- the GPIO Control plugin (`/api/plugin/gpiocontrol`), every 2 seconds while OctoPrint lists GPIO outputs, and
  when you use the switch output action.

The webcam is loaded by Macro Deck itself from the webcam address and relayed to your deck devices; it
does not pass through the plugin. The API key is stored in Macro Deck's encrypted secret store. The plugin
writes no files of its own and sends nothing anywhere else.

## Building

Requires the .NET 10 SDK and, for packing and running, the `macrodeck-plugin` CLI (`make cli`).

```bash
make build        # build
make test         # unit tests and end-to-end tests against fake printer servers
make run          # run against the Macro Deck running on this machine
make pack         # build this platform's .macroDeckPlugin into artifacts/
make preview      # render the widget previews to artifacts/previews/ (STORE=1 pads them to 16:9)
make conformance  # the Store's conformance suite, report in conformance.md
```

## Adding printer software

Each kind of printer software is a backend in `src/PrinterControl/Backends/`: its own setup steps, and a
connection that keeps the printer's state current and carries out the actions. Everything else (actions,
variables, events, widgets, the webcam) is shared. [AGENTS.md](AGENTS.md#adding-a-backend) lists what a new
backend needs; pull requests are welcome, ideally from someone who owns such a printer and can test it.

## License

MIT
