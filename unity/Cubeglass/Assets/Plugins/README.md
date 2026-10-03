# Native and managed plugins

`win-x64/cg_unity_bridge.dll` is not tracked in git. The C++ lane builds it to
`cpp/build/windows-msvc/bridge/cg_unity_bridge.dll` (`cmake --build --preset
windows-msvc` from the dev shell), and `scripts/ci-local.ps1` copies it into this
folder before running the Unity lane.

For a manual Unity run, copy it yourself first:

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
