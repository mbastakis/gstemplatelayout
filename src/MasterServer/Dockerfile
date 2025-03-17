FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy csproj files and restore dependencies
COPY MasterServer/*.csproj ./MasterServer/
COPY Common/*.csproj ./Common/
COPY *.sln ./
RUN dotnet restore MasterServer/MasterServer.csproj

# Copy source files and build
COPY MasterServer/. ./MasterServer/
COPY Common/. ./Common/
RUN dotnet publish MasterServer/MasterServer.csproj -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "MasterServer.dll"] 