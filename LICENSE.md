# Licensing overview

This repository contains two parts under different licenses.

## 1. Add-on wrapper — Apache-2.0

The files that package the program into a Home Assistant add-on:

- `ism7mqtt/Dockerfile`
- `ism7mqtt/run.sh`
- `ism7mqtt/config.yaml`
- `ism7mqtt/build.yaml`
- `repository.yaml`
- `.github/`

are licensed under the Apache License 2.0. See `LICENSE-APACHE`.

## 2. Vendored program — GPL-3.0-only

Everything under `ism7mqtt/src/` is derived from the `ism7mqtt` project and is
licensed under the GNU General Public License v3.0 only. The full text is in
`ism7mqtt/LICENSE`.

Because the program is GPL-3.0, any binary distribution (including container
images) built from this repository must make the corresponding source available.
That source is included in this repository under `ism7mqtt/src/`.
