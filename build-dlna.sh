#!/bin/bash
set -e

echo "Building Cove DLNA Extension..."

# Check if dotnet is installed
if ! command -v dotnet &> /dev/null; then
    echo "Error: dotnet SDK is not installed."
    echo "Please install the .NET 10 SDK first: https://dotnet.microsoft.com/download/dotnet/10.0"
    exit 1
fi

# Restore and Publish
dotnet restore src/Cove.DlnaServer/Cove.DlnaServer.csproj
dotnet publish src/Cove.DlnaServer/Cove.DlnaServer.csproj --configuration Release --output artifacts/dlna-extension

# Zip it up for installation
cd artifacts/dlna-extension
python3 -m zipfile --create ../com.example.dlna-server.zip .
cd ../..

echo "Done! You can now install artifacts/com.example.dlna-server.zip in Cove."
