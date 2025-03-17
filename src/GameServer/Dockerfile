FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy csproj files and restore dependencies
COPY GameServer/*.csproj ./GameServer/
COPY Common/*.csproj ./Common/
COPY *.sln ./
RUN dotnet restore GameServer/GameServer.csproj

# Copy source files and build
COPY GameServer/. ./GameServer/
COPY Common/. ./Common/
RUN dotnet publish GameServer/GameServer.csproj -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "GameServer.dll"] 