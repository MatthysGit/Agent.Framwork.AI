$source = "D:\Repository\AI\Agent.Framwork.AI\Ai.AgentFramwork.Massar.Web"
$zip    = "D:\Repository\AI\Agent.Framwork.AI\Ai.AgentFramwork.Massar.Web.zip"

$files = Get-ChildItem -Path $source -Recurse -File | Where-Object {
    $_.FullName -notmatch '[\\/]bin[\\/]'
}

Compress-Archive -Path $files.FullName -DestinationPath $zip -Force