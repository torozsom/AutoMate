FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Domain/Domain.csproj Domain/
COPY Application/Application.csproj Application/
COPY Infrastructure/Infrastructure.csproj Infrastructure/
COPY Telemetry/Telemetry.csproj Telemetry/
RUN dotnet restore Telemetry/Telemetry.csproj
COPY Domain/ Domain/
COPY Application/ Application/
COPY Infrastructure/ Infrastructure/
COPY Telemetry/ Telemetry/
RUN dotnet publish Telemetry/Telemetry.csproj -c Release -o /publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
COPY --from=build /publish .
USER 10001:10001
ENTRYPOINT ["dotnet", "Telemetry.dll"]
