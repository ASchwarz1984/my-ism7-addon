# Updating the vendored ism7mqtt source

The C# program is vendored under `ism7mqtt/src/` so this add-on has no upstream
build/runtime dependency. To refresh it with newer upstream fixes:

1. Clone or download the upstream source snapshot you want.
2. Replace these folders with the new versions (keep the same layout):
   - `ism7mqtt/src/ism7ssl/`
   - `ism7mqtt/src/ism7mqtt/`
   - `ism7mqtt/src/ism7config/`
   (The `ism7proxy` project is intentionally not vendored — the add-on does not use it.)
3. If upstream changed the target framework, update the SDK tag in
   `ism7mqtt/Dockerfile` (`mcr.microsoft.com/dotnet/sdk:<version>`).
4. Bump `version:` in `ism7mqtt/config.yaml` and `VERSION` in `ism7mqtt/build.yaml`.
5. Rebuild locally to verify (see below), then commit.

## Local build test (requires Docker)

```powershell
docker build --build-arg BUILD_ARCH=amd64 `
  --build-arg BUILD_FROM=ghcr.io/home-assistant/amd64-base:latest `
  -t my-ism7-addon:test ism7mqtt
```

The build is self-contained: it restores NuGet packages, compiles
`ism7mqtt` + `ism7config` as musl self-contained binaries, and copies them into
the Home Assistant base image.

## Notes

- Versioning uses MinVer. Without git tags the build would default to `0.0.0`,
  so the Dockerfile sets `MINVERVERSIONOVERRIDE` from the `VERSION` build-arg.
- The program is GPL-3.0; keep `ism7mqtt/src/` (the corresponding source)
  distributed alongside any image you publish.
