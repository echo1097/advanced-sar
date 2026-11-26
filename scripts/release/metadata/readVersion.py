import re
import xml.etree.ElementTree as elementTree


def readModVersion(csprojPath):
    if not csprojPath.exists():
        raise FileNotFoundError(f"Project file not found: {csprojPath}")

    tree = elementTree.parse(csprojPath)
    root = tree.getroot()
    versionNode = root.find(".//Version")

    if versionNode is None or not (versionNode.text or "").strip():
        raise ValueError(f"Could not find <Version> in {csprojPath}")

    return versionNode.text.strip()


def readChangeNotes(changelogPath, modVersion):
    if not changelogPath.exists():
        return f"Release {modVersion}"

    changelogText = changelogPath.read_text(encoding="utf-8")
    headingPattern = re.compile(
        rf"^##\s+{re.escape(modVersion)}\s*$",
        re.MULTILINE,
    )
    headingMatch = headingPattern.search(changelogText)

    if not headingMatch:
        return f"Release {modVersion}"

    sectionStart = headingMatch.end()
    nextHeading = re.search(r"^##\s+", changelogText[sectionStart:], re.MULTILINE)

    if nextHeading:
        sectionText = changelogText[sectionStart : sectionStart + nextHeading.start()]
    else:
        sectionText = changelogText[sectionStart:]

    cleanedNotes = sectionText.strip()

    if not cleanedNotes:
        return f"Release {modVersion}"

    return cleanedNotes
