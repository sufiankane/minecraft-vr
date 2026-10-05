# Native and managed plugins

`win-x64/cg_unity_bridge.dll` (production 5.12 reader) and
`win-x64/cg_bridge_test_support.dll` (test-only native writer, TD-004/TD-067)
are not tracked in git. The C++ lane builds them to
`cpp/build/windows-msvc/bridge/` (`cmake --build --preset windows-msvc --target
cg_bridge cg_bridge_test_support` from the dev shell). The test-only library is
staged only for Unity test runs and must not be shipped: the release workflow
removes it before the player build.

`scripts/sync-unity-plugins.ps1 -IncludeTestSupport` (what `scripts/ci-local.ps1`
runs in the Unity lane) stages both native DLLs plus the managed plugins. For a
manual Unity test run:

    powershell -File scripts/sync-unity-plugins.ps1 -IncludeTestSupport

For a manual player build, stage only the production DLL:

    Copy-Item cpp/build/windows-msvc/bridge/cg_unity_bridge.dll unity/Cubeglass/Assets/Plugins/win-x64/

`managed/Cubeglass.CoreMath.dll`, `managed/Cubeglass.Voxel.dll`,
`managed/Cubeglass.Mesh.dll`, `managed/Cubeglass.Streaming.dll` and
`managed/Cubeglass.Gameplay.dll` are not tracked in git either. The packages
cannot reference the Cubeglass .NET libraries through an asmdef (they are plain
.NET libraries outside the Unity project), so Unity auto-references them from
this folder as managed plugins (the runtime asmdefs leave `overrideReferences`
false; the test asmdefs set it true and list the DLLs in
`precompiledReferences`). `scripts/sync-unity-plugins.ps1` builds
`dotnet/src/Streaming` and `dotnet/src/Gameplay` in Release and copies the five
`netstandard2.1` DLLs here (it fails loudly when the build output is missing);
`scripts/ci-local.ps1` runs it in the Unity lane. For a manual Unity run:

    powershell -File scripts/sync-unity-plugins.ps1

`System.Text.Json.dll` is deliberately not copied: `Cubeglass.Voxel` only needs
it for the optional `BlockRegistry` JSON path, which the Unity runtime never
uses (Unity does not ship that assembly). Chunk meshing uses
`SliceBlockRegistry`, a code-built copy of the six S7 block definitions pinned
against `dotnet/src/Voxel/Content/blocks.json` by an EditMode test.
