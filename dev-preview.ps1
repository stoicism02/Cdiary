param([switch]$Background)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Cdiary.csproj'
$styles = Join-Path $PSScriptRoot 'Styles\SharedStyles.xaml'
$previewArguments = @('--dev', '--styles', $styles)
if ($Background) { $previewArguments += '--background' }
# Run from the development output; dist/current and the normal database stay independent.
dotnet run --project $project -c Debug -- @previewArguments
exit $LASTEXITCODE
