#!/usr/bin/env bash
#
# Предполётная проверка совместимости с cTrader Cloud.
#
# Cloud исполняет .algo на Linux без GUI и не умеет подгружать сторонние .dll во время
# работы. Нарушение любого из этих условий даёт робота, который прекрасно работает
# локально и молча ломается в облаке — поэтому проверки автоматизированы, а не
# оставлены на внимательность.
#
# Поиск идёт ТОЛЬКО по файлам *.cs и исключает obj/ и bin/. Без этого проверки
# срабатывают на собственные комментарии, на отладочные .pdb и на project.assets.json,
# где перечислены вспомогательные библиотеки SDK, не попадающие в .algo.

set -uo pipefail

cd "$(dirname "$0")/.." || exit 1

CORE="src/Quant.Core"
BOT="src/QuantCryptoV3/QuantCryptoV3"
FAILURES=0

scan() {
    local label="$1" pattern="$2"
    shift 2

    local hits
    hits=$(grep -rnE "$pattern" "$@" --include="*.cs" --exclude-dir=obj --exclude-dir=bin 2>/dev/null \
           | grep -vE '^\s*[^:]+:[0-9]+:\s*(//|/\*|\*)')

    if [ -n "$hits" ]; then
        echo "  ПРОВАЛ: $label"
        echo "$hits" | sed 's/^/      /'
        FAILURES=$((FAILURES + 1))
    else
        echo "  OK: $label"
    fi
}

echo "== 1. Релизная сборка без предупреждений =="
BUILD_OUTPUT=$(cd "$BOT" && dotnet build -c Release --nologo 2>&1)
PROBLEMS=$(echo "$BUILD_OUTPUT" | grep -cE "warning|error")
if [ "$PROBLEMS" -eq 0 ]; then
    echo "  OK: 0 предупреждений, 0 ошибок"
else
    echo "  ПРОВАЛ: найдено $PROBLEMS предупреждений/ошибок"
    echo "$BUILD_OUTPUT" | grep -E "warning|error" | sed 's/^/      /'
    FAILURES=$((FAILURES + 1))
fi

echo "== 2. Тесты =="
TEST_OUTPUT=$(cd tests/Quant.Core.Tests && dotnet test --nologo -v q 2>&1)
if echo "$TEST_OUTPUT" | grep -q "Passed!"; then
    echo "  OK: $(echo "$TEST_OUTPUT" | grep -oE 'Passed:[[:space:]]+[0-9]+' | head -1)"
else
    echo "  ПРОВАЛ: тесты не прошли"
    echo "$TEST_OUTPUT" | grep -E "Failed|error" | head -10 | sed 's/^/      /'
    FAILURES=$((FAILURES + 1))
fi

echo "== 3. Ядро не зависит от cAlgo =="
scan "using cAlgo в Quant.Core" "using cAlgo" "$CORE"

echo "== 4. Нет обращений к GUI =="
scan "Chart / ChartObjects / System.Windows / System.Drawing" \
     "(^|[^a-zA-Z])Chart(Objects|Indicators|Robots|Templates)?\.|System\.Windows|System\.Drawing" \
     "$CORE" "$BOT"

echo "== 5. Нет файловых, сетевых операций и динамической загрузки =="
scan "File / Directory / HttpClient / WebRequest / Assembly.Load" \
     "(^|[^a-zA-Z])(File|Directory)\.|HttpClient|WebRequest|Assembly\.Load" \
     "$CORE" "$BOT"

echo
if [ "$FAILURES" -eq 0 ]; then
    ALGO=$(find "$BOT/bin/Release" -name "*.algo" 2>/dev/null | head -1)
    echo "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ"
    [ -n "$ALGO" ] && echo "Артефакт: $ALGO ($(du -h "$ALGO" | cut -f1))"
    exit 0
else
    echo "ПРОВЕРОК ПРОВАЛЕНО: $FAILURES — загружать в облако нельзя"
    exit 1
fi
