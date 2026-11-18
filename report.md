## SonarQube Quality Report

- **Project**: `jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f`
- **Quality Gate**: **OK**
- **Generated (UTC)**: `2026-11-18T19:38:31Z`
- **Repository**: https://github.com/jav76/hyperdu
- **Public Dashboard**: https://jav76.github.io/hyperdu/

| Metric | Value |
| :--- | :--- |
| Quality Gate | OK |
| Reliability Rating / Issues | A (0) |
| Security Rating / Issues | B (1) |
| Maintainability Rating / Issues | A (80) |
| Coverage | 59.9% |
| Duplicated Lines | 0.6% |
| Lines of Code (NCLOC) | 14.4k |
| Technical Debt | 5h40min |
| Open Issues (total) | 81 |
| Open Security Hotspots | 0 |
| SARIF Exported Findings | 37 |

### Quality Gate Conditions

| Metric | Status | Actual | Threshold |
| :--- | :--- | :--- | :--- |
| `new_coverage` | OK | 61.5 | LT 50 |
| `new_duplicated_lines_density` | OK | 0.44142 | GT 5 |
| `new_software_quality_reliability_rating` | OK | 1 | GT 2 |
| `new_software_quality_security_rating` | OK | 2 | GT 2 |

### Top Findings (Major / MEDIUM+ and Open Hotspots)

| Kind | Severity | Rule | Location | Message |
| :--- | :--- | :--- | :--- | :--- |
| Issue | CRITICAL/HIGH | `csharpsquid:S4487` | `hyperdu.Core/Du/ParallelDuWalker.cs:149` | Remove this unread private class field 'RawCount' or refactor the code to use its value. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:298` | Refactor this function to reduce its Cognitive Complexity from 26 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:346` | Refactor this function to reduce its Cognitive Complexity from 17 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:439` | Refactor this function to reduce its Cognitive Complexity from 28 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:693` | Refactor this function to reduce its Cognitive Complexity from 42 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:845` | Refactor this function to reduce its Cognitive Complexity from 18 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:931` | Refactor this function to reduce its Cognitive Complexity from 48 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/sonar_export.py:1077` | Refactor this function to reduce its Cognitive Complexity from 49 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `python:S3776` | `.github/scripts/check-aot-startup.py:93` | Refactor this function to reduce its Cognitive Complexity from 22 to the 15 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1752` | Refactor this method to reduce its Cognitive Complexity from 51 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1975` | Refactor this method to reduce its Cognitive Complexity from 30 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Models/DirectoryNode.cs:286` | Refactor this method to reduce its Cognitive Complexity from 56 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Scanning/ParallelScanner.cs:273` | Refactor this method to reduce its Cognitive Complexity from 76 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Scanning/ParallelScanner.cs:938` | Refactor this method to reduce its Cognitive Complexity from 54 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Native/NativeDirectory.cs:260` | Refactor this method to reduce its Cognitive Complexity from 43 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Native/WindowsIo.cs:414` | Refactor this method to reduce its Cognitive Complexity from 33 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/BlockSize.cs:188` | Refactor this method to reduce its Cognitive Complexity from 38 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/DuOptions.cs:394` | Refactor this method to reduce its Cognitive Complexity from 41 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Cli/Program.cs:86` | Refactor this method to reduce its Cognitive Complexity from 31 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Scanning/DirectoryListing.cs:87` | Refactor this method to reduce its Cognitive Complexity from 30 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Native/NativeIo.cs:667` | Refactor this method to reduce its Cognitive Complexity from 38 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:564` | Refactor this method to reduce its Cognitive Complexity from 72 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:951` | Refactor this method to reduce its Cognitive Complexity from 27 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1056` | Refactor this method to reduce its Cognitive Complexity from 73 to the 25 allowed. |
| Issue | MAJOR/MEDIUM | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1400` | Refactor this method to reduce its Cognitive Complexity from 44 to the 25 allowed. |
