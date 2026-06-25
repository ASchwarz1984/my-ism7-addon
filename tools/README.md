# tools

Helper scripts for working with the vendored Wolf ISM7 parameter database.

## generate-parameter-map.ps1

Your runtime parameter file (`/config/ism7-parameters-<device_name>.json`) lists
parameters only by numeric **PTID**. This script turns those numbers into readable
German names by joining the two embedded resource files:

- `ism7mqtt/src/ism7mqtt/Resources/device.xml` — device template (DTID) → list of PTIDs
- `ism7mqtt/src/ism7mqtt/Resources/parameter.xml` — PTID → Name, unit, writable, app-visibility

### Usage

```powershell
# All device templates
./generate-parameter-map.ps1

# Only the device templates you actually have (DeviceTemplateId in your json)
./generate-parameter-map.ps1 -Dtid 190000,220000,240000,340000,350000
```

Outputs `parameter-map.csv` (open in Excel and filter) and `parameter-map.md`
(grouped per device). Columns:

- **Writable** — `True` = setpoint you can change, `False` = read-only sensor.
- **InApp** — `False` = parameter is normally hidden in the Wolf Smartset app.

### Deciding what to remove

Open `parameter-map.md`, find your device sections, and in your
`ism7-parameters-<device>.json` delete the PTID numbers you don't care about
from each device's `Parameter` array. Fewer parameters = less load on the ISM7
and faster polling. (Restart the add-on afterwards.)

## Adding hidden parameters (PV surplus / Smart Grid)

Some parameters exist in the device template but are not surfaced by the Wolf
app. They use Wolf's "SG/PV" (Smart Grid / Photovoltaik) naming. On the heat-pump
device template **DTID 240000 (BWL-1S)** these are:

| PTID | Name | Writable |
| --- | --- | --- |
| 240108 | Status PV | read-only |
| 240109 | **Heizen bei PV/SG** | read-only |
| 240110 | **Kühlen bei PV/SG** | read-only |
| 240051 | Smart Grid | writable |

To expose them, add the PTID numbers to the `Parameter` list of the device whose
`DeviceTemplateId` is `240000` in your `ism7-parameters-<device>.json`, e.g.:

```jsonc
{
  "ReadBusAddress": "0x08",
  "WriteBusAddress": "0x08",
  "DeviceTemplateId": 240000,
  "Parameter": [
    240076,
    240004,
    240108,   // Status PV
    240109,   // Heizen bei PV/SG  (heating on PV surplus)
    240110    // Kühlen bei PV/SG  (cooling on PV surplus)
  ]
}
```

Then restart the add-on. New MQTT/HA entities appear for the added PTIDs.

> Note: these three are read-only status values reported by the controller, so
> they tell you *whether* the heat pump is currently running on PV surplus, not a
> switch to force it. `Smart Grid` (240051) is writable if you want to experiment.
