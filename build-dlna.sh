#!/bin/bash
set -e

echo "Building Cove DLNA Extension..."

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
cd "$SCRIPT_DIR"

# Add these two lines to configure the .NET environment
export DOTNET_ROOT="/home/alston/.dotnet"
export PATH="$PATH:$DOTNET_ROOT:$DOTNET_ROOT/tools"

# Restore and Publish C#
dotnet restore src/Cove.DlnaServer/Cove.DlnaServer.csproj
dotnet publish src/Cove.DlnaServer/Cove.DlnaServer.csproj --configuration Release --output artifacts/dlna-extension

# Zip it up
cd artifacts/dlna-extension
python3 -m zipfile --create ../cove.dlna-server.zip .
cd ../..

echo "Done! You can now install artifacts/cove.dlna-server.zip in Cove."