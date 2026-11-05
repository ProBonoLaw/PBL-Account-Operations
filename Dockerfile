# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/AccountOperations/AccountOperations.csproj src/AccountOperations/
RUN dotnet restore src/AccountOperations/AccountOperations.csproj
COPY src/ src/
RUN dotnet publish src/AccountOperations/AccountOperations.csproj \
    --configuration Release --no-restore --output /app

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENTRYPOINT ["dotnet", "AccountOperations.dll"]
