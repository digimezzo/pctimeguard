#!/bin/bash

cd "$(dirname "$0")" || exit 1

dotnet run --project PcTimeGuard.csproj
