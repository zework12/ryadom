#!/bin/sh
# Сборка основного проекта и короткий список ошибок.
cd "$(dirname "$0")/src/Ryadom" || exit 1
"${DOTNET:-$( [ -x ../../../tools/dotnet/dotnet.exe ] && echo ../../../tools/dotnet/dotnet.exe || echo dotnet )}" build -nologo -v q "$@" 2>&1 > /tmp/ryadom-build.log
grep -E " error | warning CS8" /tmp/ryadom-build.log | sed -E 's#\[C:[^]]*\]##' | awk -F'Ryadom.src.Ryadom.' '{print $NF}' | sort -u | head -60
grep -qE "Build succeeded|Сборка успешно" /tmp/ryadom-build.log && echo OK
