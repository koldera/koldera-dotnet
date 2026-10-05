#!/usr/bin/env bash
# Checks the packed Koldera.Sdk: required files, the symbol package, and a clean consumer project that restores the
# package from the local folder and builds against it on every target framework.
set -euo pipefail

dir="${1:?usage: check-package.sh <folder with the .nupkg and .snupkg>}"
version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/Koldera.Sdk/Koldera.Sdk.csproj | head -n 1)"
[ -n "$version" ] || { echo "::error::no <Version> in src/Koldera.Sdk/Koldera.Sdk.csproj"; exit 1; }
nupkg="$dir/Koldera.Sdk.$version.nupkg"
snupkg="$dir/Koldera.Sdk.$version.snupkg"
[ -f "$nupkg" ] || { echo "::error::$nupkg is missing"; exit 1; }
[ -f "$snupkg" ] || { echo "::error::$snupkg is missing"; exit 1; }

listing="$(unzip -Z1 "$nupkg")"
for entry in README.md lib/net8.0/Koldera.Sdk.dll lib/net8.0/Koldera.Sdk.xml lib/net10.0/Koldera.Sdk.dll lib/net10.0/Koldera.Sdk.xml; do
  grep -qx "$entry" <<<"$listing" || { echo "::error::$nupkg has no $entry"; exit 1; }
done
unzip -Z1 "$snupkg" | grep -q '\.pdb$' || { echo "::error::$snupkg has no pdb"; exit 1; }
unzip -p "$nupkg" Koldera.Sdk.nuspec | grep -q '<license type="expression">Apache-2.0</license>' || { echo "::error::nuspec licence expression is not Apache-2.0"; exit 1; }
echo "package contents ok:"; echo "$listing" | sed 's/^/  /'

work="$(mktemp -d)"
source_dir="$(cd "$dir" && (cygpath -w "$PWD" 2>/dev/null || pwd))"
# A private package cache, so a stale Koldera.Sdk of the same version can never stand in for the packed one.
export NUGET_PACKAGES="$work/packages"
trap 'rm -rf "$work"' EXIT
cat > "$work/nuget.config" <<EOF
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$source_dir" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF
cat > "$work/Consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <OutputType>Exe</OutputType>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Koldera.Sdk" Version="$version" />
  </ItemGroup>
</Project>
EOF
cat > "$work/Program.cs" <<EOF
using Koldera;

using var client = new KolderaClient(new KolderaClientOptions { BaseUrl = new Uri("https://koldera.example.com"), Token = "t" });
Console.WriteLine(client.Pdf.GetType().Name);
EOF
(cd "$work" && dotnet build -c Release)
