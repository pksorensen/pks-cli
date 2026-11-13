FROM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
WORKDIR /src

COPY src/pks-cli.csproj src/
COPY src/pks-cli-runtime/pks-cli-runtime.csproj src/pks-cli-runtime/
RUN dotnet restore src/pks-cli-runtime/pks-cli-runtime.csproj

COPY src/ src/
COPY templates/ templates/
RUN dotnet publish src/pks-cli-runtime/pks-cli-runtime.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble
WORKDIR /app
COPY --from=build /app/ ./
ENV ASPNETCORE_URLS=http://+:8080
ENV PKS_RUNTIME_CAPABILITIES=speech.realtime.transcribe
EXPOSE 8080
ENTRYPOINT ["dotnet", "pks-cli-runtime.dll"]
