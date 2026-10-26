# Changelog

## 1.1.9

- Fix startup failing when valid parameter JSON has multiple IDs per line or is
  minified. Parameter counting now parses the JSON instead of counting lines.
- Log the actual parameter count, preserve the warning above 150 parameters, and
  report unreadable or malformed parameter files explicitly.

## 1.1.8

- Give an unresponsive ISM7 a quiet recovery window: after the initial bridge
  attempt and three retries spaced 10 seconds apart, wait five minutes before
  starting another cycle.
- Log retry numbers and cooldowns separately for each configured module.

## 1.1.7

- Stop startup if an ISM7 parameter bundle receives no response, allowing the
  add-on to retry instead of waiting indefinitely. The new `startup_timeout`
  setting defaults to 90 seconds per bundle.
- Add optional `serialize_push_subscribes` for some ISM7 firmware 5.20 devices
  (including 5.20.6) that stop updating when subscriptions are sent together.
  It defaults to `false`; enable it to wait for each subscription response.
- Preserve offline entity availability and the BWL-1S E1 contact-state fix.

## 1.1.6

- Fix BWL-1S `Eingang E1` being reported as a 200 °C temperature when the
  input is closed. Home Assistant now shows the contact state as `Geschlossen`
  or `Geöffnet`.

## 1.1.5

- Mark all ISM7-backed Home Assistant entities as unavailable while the ISM7
  connection is offline.
- Restore MQTT subscriptions, bridge availability, and discovery information
  after an MQTT broker reconnect.
