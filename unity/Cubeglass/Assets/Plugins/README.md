# Native plugins

`win-x64/cg_unity_bridge.dll` is not tracked in git. The C++ lane builds it to
`cpp/build/windows-msvc/bridge/cg_unity_bridge.dll` (`cmake --build --preset
windows-msvc` from the dev shell), and `scripts/ci-local.ps1` copies it into this
folder before running the Unity lane.

For a manual Unity run, copy it yourself first:

    Copy-Item cpp/build/windows-msvc/bridge/cg_unity_bridge.dll unity/Cubeglass/Assets/Plugins/win-x64/
