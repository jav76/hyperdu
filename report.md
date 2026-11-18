## SonarQube Quality Report

- **Project**: `jav76_hyperdu_205c3499-2670-4991-8a9a-29c5f76e3d9f`
- **Quality Gate**: **OK**
- **Generated (UTC)**: `2026-11-18T19:26:21Z`
- **Repository**: https://github.com/jav76/hyperdu
- **Public Dashboard**: https://jav76.github.io/hyperdu/

| Metric | Value |
| :--- | :--- |
| Quality Gate | OK |
| Reliability Rating / Issues | A (0) |
| Security Rating / Issues | B (1) |
| Maintainability Rating / Issues | A (98) |
| Coverage | 59.8% |
| Duplicated Lines | 0.6% |
| Lines of Code (NCLOC) | 14.4k |
| Technical Debt | 7h28min |
| Open Issues (total) | 99 |
| Open Security Hotspots | 0 |
| SARIF Exported Findings | 55 |

### Quality Gate Conditions

| Metric | Status | Actual | Threshold |
| :--- | :--- | :--- | :--- |
| `new_coverage` | OK | 61.4 | LT 50 |
| `new_duplicated_lines_density` | OK | 0.44142 | GT 5 |
| `new_software_quality_reliability_rating` | OK | 1 | GT 2 |
| `new_software_quality_security_rating` | OK | 2 | GT 2 |

### Top Findings (Major / MEDIUM+ and Open Hotspots)

| Kind | Severity | Rule | Location | Message |
| :--- | :--- | :--- | :--- | :--- |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1680` | Refactor this method to reduce its Cognitive Complexity from 22 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1752` | Refactor this method to reduce its Cognitive Complexity from 51 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1897` | Refactor this method to reduce its Cognitive Complexity from 16 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:1975` | Refactor this method to reduce its Cognitive Complexity from 30 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:2296` | Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:2328` | Refactor this method to reduce its Cognitive Complexity from 16 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Models/DirectoryNode.cs:204` | Refactor this method to reduce its Cognitive Complexity from 22 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Models/DirectoryNode.cs:286` | Refactor this method to reduce its Cognitive Complexity from 56 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Scanning/ParallelScanner.cs:273` | Refactor this method to reduce its Cognitive Complexity from 76 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Scanning/ParallelScanner.cs:621` | Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Scanning/ParallelScanner.cs:772` | Refactor this method to reduce its Cognitive Complexity from 22 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Scanning/ParallelScanner.cs:938` | Refactor this method to reduce its Cognitive Complexity from 54 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Native/NativeDirectory.cs:260` | Refactor this method to reduce its Cognitive Complexity from 43 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Native/WindowsIo.cs:176` | Refactor this method to reduce its Cognitive Complexity from 17 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Native/WindowsIo.cs:339` | Refactor this method to reduce its Cognitive Complexity from 20 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Native/WindowsIo.cs:414` | Refactor this method to reduce its Cognitive Complexity from 33 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/BlockSize.cs:188` | Refactor this method to reduce its Cognitive Complexity from 38 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/DuAccounting.cs:516` | Refactor this method to reduce its Cognitive Complexity from 25 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/DuOptions.cs:394` | Refactor this method to reduce its Cognitive Complexity from 41 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/GetoptLong.cs:203` | Refactor this method to reduce its Cognitive Complexity from 25 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/HumanFormat.cs:51` | Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Du/ParallelDuWalker.cs:486` | Refactor this method to reduce its Cognitive Complexity from 18 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Cli/Program.cs:86` | Refactor this method to reduce its Cognitive Complexity from 31 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Scanning/DirectoryListing.cs:87` | Refactor this method to reduce its Cognitive Complexity from 30 to the 15 allowed. |
| Issue | CRITICAL/HIGH | `csharpsquid:S3776` | `hyperdu.Core/Native/NativeIo.cs:667` | Refactor this method to reduce its Cognitive Complexity from 38 to the 15 allowed. |
