import json
from utils.console import console


def loadPublishConfig(publishConfigPath):
    if not publishConfigPath.exists():
        raise FileNotFoundError(f"Missing publish config: {publishConfigPath}")

    with publishConfigPath.open("r", encoding="utf-8") as fileHandle:
        publishConfig = json.load(fileHandle)

    requiredKeys = ["appId", "publishedFileId", "expectedModName"]

    for key in requiredKeys:
        if not str(publishConfig.get(key, "")).strip():
            raise ValueError(f"Missing required field '{key}' in {publishConfigPath}")

    return publishConfig


def getSteamUsername(localConfigPath):
    savedUsername = ""

    if localConfigPath.exists():
        with localConfigPath.open("r", encoding="utf-8") as fileHandle:
            localConfig = json.load(fileHandle)
            savedUsername = str(localConfig.get("steamUsername", "")).strip()

    if savedUsername:
        console.print(f"Using saved Steam username: [bold]{savedUsername}[/bold]")
        return savedUsername

    console.print("Enter your Steam username (never saved to git):")
    enteredUsername = input("> ").strip()

    if not enteredUsername:
        raise ValueError("Steam username cannot be empty.")

    console.print("Save this Steam username locally in workshop/local.json? (y/n)")
    saveAnswer = input("> ").strip().lower()

    if saveAnswer in ("y", "yes"):
        with localConfigPath.open("w", encoding="utf-8") as fileHandle:
            json.dump({"steamUsername": enteredUsername}, fileHandle, indent=2)
            fileHandle.write("\n")

    return enteredUsername
