ARG BUILD_FROM=ghcr.io/home-assistant/amd64-base:latest

# ---------------------------------------------------------------------------
# Stage 1: build the ism7mqtt / ism7config binaries from the vendored source.
# Self-contained, musl-targeted binaries so they run on the Alpine-based
# Home Assistant base image without a separate .NET runtime.
# This mirrors the upstream ism7mqtt Dockerfile build steps.
#
# Pin the build stage to the native build platform ($BUILDPLATFORM) so the
# cross-compile runs natively (e.g. on amd64 CI) instead of under slow QEMU
# emulation when targeting arm64. The binaries are cross-published per RID.
# ---------------------------------------------------------------------------
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /app

COPY src/ism7ssl/    ./ism7ssl/
COPY src/ism7mqtt/   ./ism7mqtt/
COPY src/ism7config/ ./ism7config/

ARG BUILD_ARCH=amd64
ARG VERSION=0.0.0-local
ENV MINVERVERSIONOVERRIDE=${VERSION}
RUN set -eu; \
    case "$BUILD_ARCH" in \
        amd64)   RID=linux-musl-x64 ;; \
        aarch64) RID=linux-musl-arm64 ;; \
        armv7)   RID=linux-musl-arm ;; \
        *) echo "Unsupported BUILD_ARCH: $BUILD_ARCH" >&2; exit 1 ;; \
    esac; \
    dotnet publish -c Release -o out -r "$RID" --self-contained true ism7mqtt/ism7mqtt.csproj; \
    dotnet publish -c Release -o out -r "$RID" --self-contained true ism7config/ism7config.csproj

# ---------------------------------------------------------------------------
# Stage 2: assemble the Home Assistant add-on image.
# ---------------------------------------------------------------------------
FROM ${BUILD_FROM}

# ism7mqtt creates a German culture during startup; keep this enabled so the
# binaries work even if ICU is missing for that specific culture.
ENV DOTNET_SYSTEM_GLOBALIZATION_PREDEFINED_CULTURES_ONLY=false

RUN apk add --no-cache \
        icu-libs \
        moreutils \
        mosquitto-clients

COPY run.sh /
RUN chmod a+x /run.sh

COPY --from=build-env /app/out /app/

CMD [ "/run.sh" ]
