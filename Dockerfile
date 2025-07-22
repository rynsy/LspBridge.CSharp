FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

RUN apt-get update && \
  apt-get install -y --no-install-recommends curl ca-certificates && \
  rm -rf /var/lib/apt/lists/*

RUN curl -L \
  https://github.com/OmniSharp/omnisharp-roslyn/releases/latest/download/omnisharp-linux-x64-net6.0.tar.gz \
  -o /tmp/omnisharp.tar.gz \
  && mkdir -p /opt/omnisharp \
  && tar -xzf /tmp/omnisharp.tar.gz -C /opt/omnisharp \
  && chmod +x /opt/omnisharp/OmniSharp

COPY ["LspBridge.CSharp.csproj", "./"]
RUN dotnet restore "LspBridge.CSharp.csproj"
COPY . .
RUN dotnet build "LspBridge.CSharp.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "LspBridge.CSharp.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS final   
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /opt/omnisharp /opt/omnisharp
ENV PATH="/opt/omnisharp:${PATH}"
ENTRYPOINT ["dotnet", "LspBridge.CSharp.dll"]
