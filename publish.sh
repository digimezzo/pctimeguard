#!/bin/bash
set -e

cd "$(dirname "$0")"

DEPLOY_DIR="deploy"

dotnet publish PcTimeGuard.csproj \
  -c Release \
  -r win-x64 \
  --self-contained true \
  /p:PublishSingleFile=true \
  /p:IncludeNativeLibrariesForSelfExtract=true

rm -rf "$DEPLOY_DIR"
mkdir -p "$DEPLOY_DIR"

cp bin/Release/net10.0/win-x64/publish/PcTimeGuard.exe "$DEPLOY_DIR/"
cp setup-task.ps1 check-permissions.bat PcTimeGuard.vbs "$DEPLOY_DIR/"

echo
echo "Deploy files ready in $(pwd)/$DEPLOY_DIR:"
ls -1 "$DEPLOY_DIR"