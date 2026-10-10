# URP migration static check results

Date: 2026-10-10. Base: `db0658ff0b2e93e4a9e28cc2867509658b0ecc00`.

| Check | Result |
| --- | --- |
| C# 9 syntax / conditional directives | 145 C# files × 4 configurations (Unity 6.0/6.6, Editor/Player); **0 errors** |
| Shader source structure | 99 shader/HLSL/compute files; balanced preprocessor and shader-program blocks; **0 errors** |
| Local shader includes | 185 references resolve; **0 missing files** |
| Unsupported shader constructs | No active `GrabPass` or Surface Shader pragmas |
| Assembly and package JSON | 6 assembly definitions plus manifest parse successfully; no duplicate assembly references |
| New Unity assets | 7 new asset/folder entries have valid distinct stable metadata GUIDs |
| Whitespace | `git diff --check` / staged equivalent: passed |
| Unity headless import, pipeline creation and validation | **Passed locally with Unity 6000.6.2f1 / URP 17.6.0** |
| Unity assembly/API binding | **Passed locally with Unity 6000.6.2f1** |
| Unity shader compiler / variant builds | **Not run** |
| Editor rendering / Windows / Android device | **Not run** |

During handoff, the C# checker was compiled directly with the installed SDK's `csc.dll`
and run against the source tree. The `dotnet run` restore bootstrap encountered a
NuGet environment-path error, so the check used local reference assemblies without
package restoration. Two framework-unification warnings occurred while building
the checker against .NET 10 with the SDK's .NET 9 Roslyn binaries; the checker
itself ran successfully. These were not Unity project compilation results.

During local integration, `python tools/check_urp.py`, the documented `dotnet run`
command and `git diff --check db0658ff HEAD` passed again. The bundle SHA-256 and
imported Git tree matched `commit-verification.json`.

A temporary project copy was imported with Unity 6000.6.2f1 in batch mode with
`-nographics`. Project C# compilation, `Crest.CrestURPSetup.CreatePipeline` and
`Crest.CrestURPSetup.ValidatePipeline` completed successfully; both editor runs
exited with code 0. URP/Core remained at 17.6.0,
while Unity automatically upgraded older dependencies, including Burst to 2.0.0,
Mathematics to 1.4.0, Input System to 1.20.0 and Splines to 2.9.0. The committed
manifest and editor target retain the handoff configuration. The exact 6000.6.0f1
editor target, GPU shader variants, player builds and device rendering remain untested.

Manual source review covered the new URP API calls against Unity's official URP
17.6 source, render pass ordering, camera filters, color-copy lifetime, texture
imports, MSAA descriptor handling, reflection state restoration, shader lighting
and depth interfaces, and preservation of the existing simulation/query code.

Source checks and headless editor checks do not establish visual correctness or
shader variant coverage. Complete the [local validation checklist](URP-MIGRATION.md)
before treating the port as visually tested. The upstream example materials still
require local Unity material conversion. Generated pipeline assets were created
only in the validation copy.
