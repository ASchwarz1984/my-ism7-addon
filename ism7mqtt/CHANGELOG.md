# Changelog

## 1.1.6

- Fix BWL-1S `Eingang E1` being reported as a 200 °C temperature when the
  input is closed. Home Assistant now shows the contact state as `Geschlossen`
  or `Geöffnet`.

## 1.1.5

- Mark all ISM7-backed Home Assistant entities as unavailable while the ISM7
  connection is offline.
- Restore MQTT subscriptions, bridge availability, and discovery information
  after an MQTT broker reconnect.
