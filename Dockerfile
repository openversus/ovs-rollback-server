# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# The Native AOT binary is self-contained, so the final image only needs runtime-deps
# (native libraries), not the .NET runtime
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS base
USER $APP_UID
WORKDIR /app


# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
# clang + zlib headers are required by the Native AOT compiler (ILC) to link the native binary
RUN apt update -y \
 && apt install clang zlib1g-dev -y \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY ["OVSRollbackServer/OVSRollbackServer.csproj", "OVSRollbackServer/"]
RUN dotnet restore "./OVSRollbackServer/OVSRollbackServer.csproj"
COPY . .
WORKDIR "/src/OVSRollbackServer"
RUN dotnet build "./OVSRollbackServer.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish

ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./OVSRollbackServer.csproj" -c $BUILD_CONFIGURATION -r linux-x64 -o /app/publish

USER root
RUN apt update -y \
 && apt install wget -y \
 && wget 'https://aka.ms/dotnet-counters/linux-x64' -O /usr/sbin/dotnet-counters \
 && chmod ug+x /usr/sbin/dotnet-counters \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir /tmp/rollback_logs \
 && chown 1654:1654 /tmp/rollback_logs

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final

USER root
RUN apt update -y \
 && apt upgrade -y \
 && apt install vim wget net-tools -y \
 && rm -rf /var/lib/apt/lists/*
USER $APP_UID

WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /usr/sbin/dotnet-counters /usr/sbin/
COPY ./OVSRollbackServer/appsettings.json /app/appsettings.json

ARG OVS_SERVER=https://prod.openversus.org/
ENV OVS_SERVER=${OVS_SERVER}

# If desired, set the value of MATCH_UPDATE_KEY below as a default to be overridden by the value in appsettings.json
#ARG MATCH_UPDATE_KEY=DockerMisconfiguredMatchUpdateKey
#ENV Server__MatchUpdateKey=${MATCH_UPDATE_KEY}

# If desired, set the value of LOG_ARCHIVE_PATH below if the logfile should be renamed with the MatchID, port, and time of the match
# and moved to another location. The expected value is a directory.
#
# If not set, the rollback server will default to:
#  Windows: $env:APPDATA\openversus\rollback-server
#  Linux/Unices: $HOME/openversus/rollback-server
#
#ARG LOG_ARCHIVE_PATH=/tmp/rollback_logs
#ENV Logging__LogArchivePath=${LOG_ARCHIVE_PATH}

ENTRYPOINT ["/app/OVS.Rollback.Server"]
