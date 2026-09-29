#!/usr/bin/env bash
# Builds release/QuantAI_Scalper_24x7_V3_<x_y_z>.algo from QuantAI_Scalper_24x7_V3/QuantAI_Scalper_24x7_V3.cs.
# The source is laid out the way cTrader keeps a cBot (Name/Name/Name.csproj) under the versioned name, so the .algo
# file, its assembly and the cBot in cTrader all carry the version: every version installs as its own cBot and never
# replaces the running one. A renamed .algo is not an option - cTrader reports errors for renamed files.
# Usage: ./build_release.sh   (needs the .NET SDK; the version comes from BotVersion in the source)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
src="$here/QuantAI_Scalper_24x7_V3/QuantAI_Scalper_24x7_V3.cs"
version="$(sed -n 's/.*const string BotVersion = "\([0-9.]*\)".*/\1/p' "$src" | head -1)"
name="QuantAI_Scalper_24x7_V${version//./_}"
if ! grep -q "const string BotName = \"$name\"" "$src"; then
  echo "BotName in the source must be $name (BotVersion $version)" >&2
  exit 1
fi
if ! grep -q "^## $version " "$here/CHANGELOG.md"; then
  echo "CHANGELOG.md has no entry for $version" >&2
  exit 1
fi
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$work/$name/$name"
# The class takes the versioned name too, so every name inside the .algo (type, assembly, file) carries the version.
sed -E "s/\bQuantAI_Scalper_24x7_V3\b/$name/g" "$src" > "$work/$name/$name/$name.cs"
cat > "$work/$name/$name/$name.csproj" <<PROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <LangVersion>7.3</LangVersion>
    <AlgoPublish>False</AlgoPublish>
    <CheckEolTargetFramework>false</CheckEolTargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="cTrader.Automate" Version="1.0.9" />
  </ItemGroup>
</Project>
PROJ
dotnet build "$work/$name/$name/$name.csproj" -c Release -o "$work/out" -nologo -v q
mkdir -p "$here/release"
cp "$work/out/$name.algo" "$here/release/$name.algo"
echo "release/$name.algo"
