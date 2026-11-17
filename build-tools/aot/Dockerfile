# Builds the Checker Lambda as a Native AOT binary on Amazon Linux 2023 (same OS as the provided.al2023 runtime).
#   docker build --target artifact --output type=local,dest=out -f build-tools/aot/Dockerfile .
FROM public.ecr.aws/amazonlinux/amazonlinux:2023 AS build

# clang + zlib + binutils (objcopy) are required by the NativeAOT toolchain on Linux; libicu is required by the .NET SDK itself.
RUN dnf install -y clang zlib-devel binutils tar gzip which findutils libicu && dnf clean all

# Official Microsoft install script; channel 10.0 = latest .NET 10 patch.
RUN curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && bash /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
 && rm /tmp/dotnet-install.sh
ENV DOTNET_ROOT=/usr/share/dotnet \
    PATH=/usr/share/dotnet:$PATH \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1

WORKDIR /src
COPY backend/Directory.Build.props backend/
COPY backend/src backend/src
RUN dotnet publish backend/src/StatusHub.Functions.Checker -c Release -r linux-x64 -o /out

# Only the bootstrap executable leaves the build stage.
FROM scratch AS artifact
COPY --from=build /out/bootstrap /bootstrap
