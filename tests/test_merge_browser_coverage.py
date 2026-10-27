"""Verify coverage merging and include its executed lines in the generic report."""

import dis
from pathlib import Path
import runpy
import sys
from tempfile import TemporaryDirectory
import trace
import unittest
from unittest.mock import patch
from xml.etree import ElementTree


SCRIPT = Path(__file__).with_name("merge_browser_coverage.py").resolve()
COUNTS = {}


class MergeCoverageTests(unittest.TestCase):
    def test_preserves_dotnet_coverage_and_merges_browser_lines_and_branches(self):
        with TemporaryDirectory() as directory:
            root = Path(directory)
            existing = root / "existing.xml"
            existing.write_text('<coverage version="1"><file path="existing.cs" /></coverage>', encoding="utf-8")
            lcov = root / "browser.lcov"
            lcov.write_text("SF:src\\browser.js\nFN:1,start\nDA:1,1\nDA:2,0\n"
                            "BRDA:1,0,0,1\nBRDA:1,0,1,-\nBRDA:1,0,2,0\nend_of_record\n"
                            "SF:src/second.js\nDA:3,2\nend_of_record\n", encoding="utf-8")
            output = root / "result" / "SonarQube.xml"
            tracer = trace.Trace(count=True, trace=False)
            with patch.object(sys, "argv", [str(SCRIPT), str(existing), str(lcov), str(output)]):
                tracer.runfunc(runpy.run_path, str(SCRIPT), run_name="__main__")
            COUNTS.update(tracer.results().counts)

            files = ElementTree.parse(output).getroot().findall("file")
            self.assertEqual([file.attrib["path"] for file in files], ["existing.cs", "src/browser.js", "src/second.js"])
            self.assertEqual(files[1][0].attrib, {"lineNumber": "1", "covered": "true", "branchesToCover": "3", "coveredBranches": "1"})
            self.assertEqual(files[1][1].attrib, {"lineNumber": "2", "covered": "false"})
            self.assertEqual(files[2][0].attrib, {"lineNumber": "3", "covered": "true"})


if __name__ == "__main__":
    result = unittest.main(argv=[sys.argv[0]], exit=False).result
    if not result.wasSuccessful():
        sys.exit(1)
    if len(sys.argv) == 3:
        report = ElementTree.parse(sys.argv[1])
        file = ElementTree.SubElement(report.getroot(), "file", path="tests/merge_browser_coverage.py")
        for number in sorted({line for _, line in dis.findlinestarts(compile(SCRIPT.read_text(encoding="utf-8"), str(SCRIPT), "exec")) if line > 0}):
            covered = COUNTS.get((str(SCRIPT), number), 0) > 0
            if not covered:
                raise AssertionError(f"Coverage merge line {number} was not exercised")
            ElementTree.SubElement(file, "lineToCover", lineNumber=str(number), covered="true")
        destination = Path(sys.argv[2])
        destination.parent.mkdir(parents=True, exist_ok=True)
        report.write(destination, encoding="utf-8", xml_declaration=True)
