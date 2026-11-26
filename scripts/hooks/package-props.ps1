# azd hook for the code deploy of src/Aps.Agent: the remote build only receives the project folder, so copy the
# repository-wide MSBuild props (target framework, exact package pins) into it before packaging and remove them after.
param([ValidateSet('copy', 'clean')] [string] $Mode = 'copy')
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project = Join-Path $root 'src/Aps.Agent'
foreach ($file in 'Directory.Build.props', 'Directory.Packages.props') {
    $target = Join-Path $project $file
    if ($Mode -eq 'copy') { Copy-Item (Join-Path $root $file) $target -Force }
    elseif (Test-Path $target) { Remove-Item $target -Force }
}
