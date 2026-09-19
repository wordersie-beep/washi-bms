#!/usr/bin/env bash
#
# Собирает САМОДОСТАТОЧНЫЙ пакет для cTrader.
#
# В репозитории проект бота подключает ядро по пути ..\..\Quant.Core, потому что рядом
# лежат ещё и тесты. При копировании одной папки в Documents\cAlgo\Sources\Robots этот
# путь сломается — ядро окажется вне дерева.
#
# Пакет устроен так, чтобы работать сразу после копирования:
#
#   QuantCryptoV3/            ← из ЭТОЙ папки SDK берёт имя .algo
#   ├── Quant.Core/           ← ядро лежит внутри
#   └── QuantCryptoV3/
#       └── QuantCryptoV3.csproj   ← путь ..\Quant.Core, один уровень вверх

set -euo pipefail

cd "$(dirname "$0")/.."

OUT="dist/QuantCryptoV3"
rm -rf dist && mkdir -p "$OUT/QuantCryptoV3"

# Ядро: только исходники, без артефактов сборки.
mkdir -p "$OUT/Quant.Core"
(cd src/Quant.Core && find . -name "*.cs" -not -path "./obj/*" -not -path "./bin/*" \
    -exec cp --parents {} "../../$OUT/Quant.Core/" \;)

# Бот.
cp src/QuantCryptoV3/QuantCryptoV3/*.cs "$OUT/QuantCryptoV3/"

# csproj с исправленным путём к ядру и без Directory.Build.props,
# которого в папке cTrader не будет.
cat > "$OUT/QuantCryptoV3/QuantCryptoV3.csproj" <<'CSPROJ'
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    cBot QuantCryptoV3 для cTrader Algo.

    РАСПОЛОЖЕНИЕ: эту папку (QuantCryptoV3, ту что уровнем выше) нужно целиком
    скопировать в Documents\cAlgo\Sources\Robots\. Вложенность обязательна: SDK
    cTrader.Automate берёт имя .algo из имени РОДИТЕЛЬСКОГО каталога проекта.
  -->

  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <AssemblyName>QuantCryptoV3</AssemblyName>
    <RootNamespace>Quant.Bot</RootNamespace>
    <Product>QuantCryptoV3</Product>
    <Version>3.0.0</Version>
    <LangVersion>10.0</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <InvariantGlobalization>true</InvariantGlobalization>
    <!-- Совместимость с cTrader Cloud: Linux, без GUI, без Windows-зависимостей. -->
    <UseWindowsForms>false</UseWindowsForms>
    <UseWPF>false</UseWPF>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="cTrader.Automate" Version="1.0.21" />
  </ItemGroup>

  <!--
    Ядро подключается ИСХОДНИКАМИ, а не ProjectReference: итоговый .algo остаётся
    одной сборкой и не может сломаться в облаке из-за неупакованной вспомогательной
    DLL. cTrader Cloud не умеет подгружать сторонние .dll во время работы.
  -->
  <ItemGroup>
    <Compile Include="..\Quant.Core\**\*.cs" LinkBase="Core" />
  </ItemGroup>

</Project>
CSPROJ

cp README.md "$OUT/README.md" 2>/dev/null || true
cp INSTALL.md "$OUT/INSTALL.md" 2>/dev/null || true
cp -r docs "$OUT/docs"

# Проверка: пакет обязан собираться сам по себе.
echo "Проверяю сборку пакета..."
BUILD=$(cd "$OUT/QuantCryptoV3" && dotnet build -c Release --nologo 2>&1)
if echo "$BUILD" | grep -qE "error|warning"; then
    echo "ПРОВАЛ: пакет не собирается чисто"
    echo "$BUILD" | grep -E "error|warning" | head
    exit 1
fi

ALGO=$(find "$OUT/QuantCryptoV3/bin/Release" -name "*.algo" | head -1)
if [ -z "$ALGO" ]; then echo "ПРОВАЛ: .algo не создан"; exit 1; fi
if [ "$(basename "$ALGO")" != "QuantCryptoV3.algo" ]; then
    echo "ПРОВАЛ: артефакт назван $(basename "$ALGO") вместо QuantCryptoV3.algo"
    exit 1
fi

cp "$ALGO" "dist/QuantCryptoV3.algo"

# Из архива убираем артефакты сборки: пользователь соберёт сам.
rm -rf "$OUT/QuantCryptoV3/bin" "$OUT/QuantCryptoV3/obj"

(cd dist && zip -qr QuantCryptoV3-source.zip QuantCryptoV3)

echo
echo "Готово:"
echo "  dist/QuantCryptoV3.algo           $(du -h dist/QuantCryptoV3.algo | cut -f1)  — загрузить в cTrader напрямую"
echo "  dist/QuantCryptoV3-source.zip     $(du -h dist/QuantCryptoV3-source.zip | cut -f1)  — исходники для cTrader Editor"
