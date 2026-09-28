param([Parameter(Mandatory=$true)][string]$DllPath)
$ErrorActionPreference='Stop'
$assembly=[Reflection.Assembly]::LoadFrom((Resolve-Path $DllPath).Path)
$resourceName=$assembly.GetManifestResourceNames()|Where-Object{$_-like '*.g.resources'}|Select-Object -First 1
if([string]::IsNullOrWhiteSpace($resourceName)){throw 'No WPF resource stream was found.'}
$stream=$assembly.GetManifestResourceStream($resourceName)
$reader=New-Object Resources.ResourceReader($stream)
$keys=@();$enumerator=$reader.GetEnumerator();while($enumerator.MoveNext()){$keys+=[string]$enumerator.Key};$reader.Close()
foreach($expected in @('importwindow.baml','previewwindow.baml')){
  if(-not($keys|Where-Object{$_-eq $expected})){throw "Compiled $expected was not found."}
}
foreach($expected in @('BuildAI.png','syncback16.png','syncback32.png','settings16.png','settings32.png')){
  if(-not($assembly.GetManifestResourceNames()|Where-Object{$_-like "*.Resources.$expected"})){throw "Embedded $expected was not found."}
}
