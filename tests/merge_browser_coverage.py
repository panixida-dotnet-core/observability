"""Merge the browser's LCOV report into the existing SonarQube generic report."""

from pathlib import Path
import sys
from xml.etree import ElementTree


report = ElementTree.parse(sys.argv[1])
current = None
lines = {}
branches = {}
for line in Path(sys.argv[2]).read_text(encoding="utf-8").splitlines():
    prefix, _, value = line.partition(":")
    if prefix == "SF":
        current = ElementTree.SubElement(report.getroot(), "file", path=value.replace("\\", "/"))
        lines = {}
        branches = {}
    elif prefix == "DA":
        number, hits, *_ = value.split(",")
        lines[number] = int(hits)
    elif prefix == "BRDA":
        number, _, _, hits = value.split(",")
        total, covered = branches.get(number, (0, 0))
        branches[number] = (total + 1, covered + (hits not in ("0", "-")))
    elif prefix == "end_of_record":
        for number, hits in lines.items():
            attributes = {"lineNumber": number, "covered": str(hits > 0).lower()}
            if number in branches:
                total, covered = branches[number]
                attributes.update(branchesToCover=str(total), coveredBranches=str(covered))
            ElementTree.SubElement(current, "lineToCover", attributes)

destination = Path(sys.argv[3])
destination.parent.mkdir(parents=True, exist_ok=True)
report.write(destination, encoding="utf-8", xml_declaration=True)
