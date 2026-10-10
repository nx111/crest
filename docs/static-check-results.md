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
| Unity assembly/API binding | **Not run** |
| Unity shader compiler / variant builds | **Not run** |
| Editor rendering / Windows / Android device | **Not run** |

The C# checker was compiled directly with the installed SDK's `csc.dll` and run
against the source tree. The local `dotnet run` restore bootstrap encountered a
NuGet environment-path error, so the check used local reference assemblies without
package restoration. Two framework-unification warnings occurred while building
the checker against .NET 10 with the SDK's .NET 9 Roslyn binaries; the checker
itself ran successfully. These were not Unity project compilation results.

Manual source review covered the new URP API calls against Unity's official URP
17.6 source, render pass ordering, camera filters, color-copy lifetime, texture
imports, MSAA descriptor handling, reflection state restoration, shader lighting
and depth interfaces, and preservation of the existing simulation/query code.

Static success does not establish visual correctness or Unity API compatibility.
Complete the [local validation checklist](URP-MIGRATION.md) before treating the
port as tested. The upstream example materials still require local Unity material
conversion; neither serialized pipeline assets nor runtime images were fabricated
without the editor.
