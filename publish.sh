#!/bin/sh
# Сборка одного Ryadom.exe и выкладка на сервер (https://94.159.117.75.sslip.io/ryadom/).
# ./publish.sh          — собрать в dist/
# ./publish.sh upload   — собрать и выложить (Ryadom.exe, version.json, страница)
set -e
cd "$(dirname "$0")"
DOTNET=${DOTNET:-$( [ -x ../tools/dotnet/dotnet.exe ] && echo ../tools/dotnet/dotnet.exe || echo dotnet )}
rm -rf dist && mkdir -p dist
"$DOTNET" publish src/Ryadom/Ryadom.csproj -nologo -v q -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o dist/publish
cp dist/publish/Ryadom.exe dist/Ryadom.exe
VERSION=$(sed -n 's#.*<Version>\(.*\)</Version>.*#\1#p' src/Ryadom/Ryadom.csproj)
SHA=$(sha256sum dist/Ryadom.exe | cut -d' ' -f1)
SIZE=$(stat -c %s dist/Ryadom.exe)
printf '{"version":"%s","sha256":"%s","size":%s}\n' "$VERSION" "$SHA" "$SIZE" > dist/version.json
cat dist/version.json
if [ "$1" = "upload" ]; then
  scp -q dist/Ryadom.exe root@94.159.117.75:/opt/ryadom/public/Ryadom.exe.tmp
  [ -f site/index.html ] && scp -q site/* root@94.159.117.75:/opt/ryadom/public/
  # version.json — последним: обновление начнётся, только когда exe уже на месте.
  scp -q dist/version.json root@94.159.117.75:/opt/ryadom/public/version.json.tmp
  ssh root@94.159.117.75 'cd /opt/ryadom/public && mv Ryadom.exe.tmp Ryadom.exe && mv version.json.tmp version.json && sha256sum Ryadom.exe'
fi
