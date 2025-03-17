FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy csproj files and restore dependencies
COPY ClientSimulator/*.csproj ./ClientSimulator/
COPY Common/*.csproj ./Common/
COPY *.sln ./
RUN dotnet restore ClientSimulator/ClientSimulator.csproj

# Copy source files and build
COPY ClientSimulator/. ./ClientSimulator/
COPY Common/. ./Common/
RUN dotnet publish ClientSimulator/ClientSimulator.csproj -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY --from=build /app/out .
ENTRYPOINT ["dotnet", "ClientSimulator.dll"] 