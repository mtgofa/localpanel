# Builds the release exe (self-contained single file — no .NET needed on the target machine)
# Output: dist\LocalPanel.exe  → copy it anywhere; it creates its portable folders next to itself.
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" } else { $dotnet = $dotnet.Source }

& $dotnet publish "$PSScriptRoot\src\LocalPanel.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o "$PSScriptRoot\dist"
