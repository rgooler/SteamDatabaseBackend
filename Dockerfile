FROM ubuntu:20.04 as builder
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1

RUN mkdir /app && \
    apt-get update && \
    apt-get install -y wget --no-install-recommends apt-transport-https ca-certificates && \
    wget https://packages.microsoft.com/config/ubuntu/20.04/packages-microsoft-prod.deb -O packages-microsoft-prod.deb && \
    dpkg -i packages-microsoft-prod.deb && \
    apt-get update && \
    apt-get install -y dotnet-sdk-3.1 aspnetcore-runtime-3.1 --no-install-recommends && \
    rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY . .

RUN dotnet publish --configuration Release -p:PublishSingleFile=true --runtime linux-x64 --self-contained true

##################################################################
FROM ubuntu:20.04
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
COPY --from=builder /app/bin/linux-x64/publish /app/
WORKDIR /app
ADD settings.json settings.json
RUN apt-get update && \
    apt-get install ca-certificates libssl1.1 -y --no-install-recommends && \
    rm -rf /var/lib/apt/lists/*
CMD ["./SteamDatabaseBackend"]
