# my-ism7-addon

A **fully self-contained** Home Assistant add-on for reading data from
[Wolf](https://www.wolf.eu/) heaters equipped with an **ISM7 / WolfLink** module
and publishing it to MQTT.

This repository has **no runtime dependency on any upstream image or repository**:
the `ism7mqtt` / `ism7config` programs are built from vendored source code inside
the add-on's own multi-stage `Dockerfile`. Nothing is pulled from Docker Hub or a
third-party add-on repository at build or run time (only the official .NET SDK and
Home Assistant base images, plus the NuGet packages the program depends on).

## How it works

```
Wolf ISM7  <--TCP/TLS-->  ism7mqtt  --MQTT-->  Mosquitto  -->  Home Assistant
                          (built from ./ism7mqtt/src)
```

- `ism7config` generates a `parameter.json` describing your installation (run once).
- `ism7mqtt` connects to the ISM7, reads/writes values and bridges them to MQTT
  with Home Assistant MQTT discovery.
- The shell wrapper (`run.sh`) wires the add-on options to the program and runs one
  instance per configured device.

## Install

1. In Home Assistant: **Settings → Add-ons → Add-on Store → ⋮ → Repositories**.
2. Add `https://github.com/aschwarz1984/my-ism7-addon`.
3. Install **Ism7MQTT (self-built)**, configure it (device name, ISM7 IP, password
   from the sticker), and start it.

The first start builds the image locally on your device — this takes a few minutes
because the .NET binaries are compiled from source. Subsequent starts are fast.

> Requires the **Mosquitto broker** add-on and the **MQTT** integration.

## Configuration

| Option              | Description                                                  |
| ------------------- | ------------------------------------------------------------ |
| `device_name`       | Device id used in MQTT topics/entities (no spaces).          |
| `ism7_ip`           | IP/host of your ISM7 module.                                 |
| `ism7_password`     | Password printed on the ISM7 sticker.                        |
| `language`          | Language for entity names / list values.                     |
| `interval`          | Poll interval in seconds (default 60).                       |
| `additional_devices`| List of extra ISM7 modules (`device_name`, `ism7_ip`, ...).  |
| `mqtt_*`            | Optional manual MQTT broker settings (auto-discovered if omitted). |
| `debug_logging`     | Enable verbose protocol logging.                             |

## Important, if some entities are unavailable

The ISM7 module is easily overwhelmed when monitoring hundreds of parameters,
which can cause connection drops or missing updates. Mitigations:

- Disable the connection to the Wolf portal to reduce microcontroller load.
- Use **Ethernet** instead of Wi‑Fi (Wi‑Fi is reported to be unreliable).
- Trim parameters you don't need: edit `config/ism7-parameters-<device>.json`
  (via the File editor add-on) and remove whole device sections or individual
  numeric parameter ids, then restart.

## Updating the vendored source

The C# program lives under [`ism7mqtt/src/`](ism7mqtt/src/). To pull in upstream
fixes later, replace those folders with a newer snapshot from the original
project and bump `version` in `ism7mqtt/config.yaml`. See
[CONTRIBUTING-vendoring.md](CONTRIBUTING-vendoring.md).

## Licensing

This repository combines two differently-licensed parts — see
[LICENSE.md](LICENSE.md):

- The add-on wrapper (`Dockerfile`, `run.sh`, `config.yaml`, `repository.yaml`,
  CI) is licensed **Apache-2.0**.
- The vendored program under `ism7mqtt/src/` is **GPL-3.0-only** (originally by
  the ism7mqtt project). Redistribution of this repository or images built from it
  must comply with the GPL-3.0, including offering the corresponding source — which
  is included here.

## Credits

Built on the excellent work of the original `ism7mqtt` project (zivillian and
contributors) and the `hassio-addon-ism7mqtt` wrapper (b3nn0). This repo vendors
and self-builds that source for full independence.
