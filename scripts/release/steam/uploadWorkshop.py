import subprocess
from rich.table import Table
from utils.console import console


def showReleaseSummary(modVersion, publishConfig, stageDir, zipPath, changeNotes):
    console.print("")
    console.print(f"[bold green]Release Summary (v{modVersion})[/bold green]")
    console.print(f"App ID: {publishConfig['appId']}")
    console.print(f"Workshop ID: {publishConfig['publishedFileId']}")
    console.print(f"Release Zip: {zipPath}")

    fileTable = Table(title="Files To Upload")
    fileTable.add_column("File")
    fileTable.add_column("Size (bytes)", justify="right")

    for filePath in sorted(stageDir.rglob("*")):
        if filePath.is_file():
            relativeName = str(filePath.relative_to(stageDir))
            fileTable.add_row(relativeName, f"{filePath.stat().st_size:,}")

    console.print(fileTable)
    console.print("[bold]Change Notes:[/bold]")
    console.print(changeNotes)
    console.print("")


def confirmUpload():
    console.print("Type [bold yellow]yes[/bold yellow] to upload this build to Steam Workshop:")
    answer = input("> ").strip().lower()
    return answer == "yes"


def runSteamUpload(steamCmdExe, steamUsername, vdfPath):
    uploadCommand = [
        str(steamCmdExe),
        "+login",
        steamUsername,
        "+workshop_build_item",
        str(vdfPath.resolve()),
        "+quit",
    ]

    console.print("[bold cyan]Launching SteamCMD to upload to Steam Workshop...[/bold cyan]")
    uploadResult = subprocess.run(uploadCommand)

    if uploadResult.returncode != 0:
        raise RuntimeError(f"SteamCMD exited with code {uploadResult.returncode}.")

    console.print("[bold green]Steam Workshop upload complete.[/bold green]")
