# 0001. Layered architecture: ports and adapters with inward-only dependencies

- Status: accepted
- Date: 2026-10-01
- Deciders: Sufyan Khan (owner)
- Consulted: Stage S0 build agents
- Informed: all later-stage builders and reviewers

## Context and problem statement

Cubeglass spans two execution processes and four language lanes: a C++ hand
service, a C#/Unity game, offline Python tooling, and shared contracts. If
dependencies were allowed to point in any direction, pure logic would acquire
engine, SDK, file-system and threading dependencies, hardware would leak into
tests, and neither lane could be tested without a device. The dossier fixes the
rule in section 3.3 and the component inventory in section 3.2; this ADR records
that architecture as a binding decision for every stage.

This decision does not change a contract. It constrains where code may live and
what it may reference.

## Decision drivers

- Pure logic must be testable without Unity, the VITURE SDK, ONNX Runtime,
  hardware, the file system or threads (dossier section 0 rule 4, section 3.3).
- The same contract needs parallel C++ and C# implementations checked against
  shared goldens (dossier section 3.2, section 5).
- A machine-checked gate must fail the build when the dependency rule is broken,
  rather than relying on review alone (dossier section 3.3, section 9 item 3).

## Considered options

- **Layered ports and adapters.** Inner pure logic owns interfaces; edge
  adapters implement them. Dependencies point inward only.
- **A single unified engine assembly.** Rejected: it couples gameplay logic to
  Unity and makes hardware-free testing impossible.
- **Free-form module dependencies governed by review.** Rejected: the dossier
  requires an automated dependency-rule check in CI (section 3.3, section 9).

## Decision outcome

Chosen option: **layered ports and adapters with inward-only dependencies**, per
dossier sections 3.2 and 3.3.

### Components and their dependencies

The component inventory and the languages are fixed by dossier section 3.2:

| Component | Language | Depends on |
| --- | --- | --- |
| `cg-core-math` | C++ and C# (parallel, contract-tested) | none |
| `cg-glasses` | C++ | core-math, VITURE SDK |
| `cg-recorder` | C++ | core-math, glasses ports |
| `cg-calib` | Python (offline tool) | dataset format |
| `cg-handcore` | C++ | core-math |
| `cg-infer` | C++ | none (ONNX Runtime) |
| `cg-handservice` | C++ | handcore, infer, glasses ports |
| `cg-voxel` | C# (netstandard2.1) | core-math (C#) |
| `cg-mesh` | C# (netstandard2.1) | voxel |
| `cg-gameplay` | C# (netstandard2.1) | voxel, core-math |
| `cg-unity` | C# (Unity 6 asmdefs) | everything above |

### The dependency rule

Dependencies point inward only. From dossier section 3.3: pure logic
(`core-math`, `handcore`, `voxel`, `mesh`, `gameplay`) never references the
engine, the VITURE SDK, ONNX Runtime, the file system or threads. Adapters at the
edge implement ports defined by the inner layers.

```
 adapters (cg-unity, cg-glasses, cg-infer, cg-handservice shell)
        |
        v
 ports (interfaces in contracts)
        |
        v
 pure logic (core-math, handcore, voxel, mesh, gameplay)
```

The pure-module set is therefore `core-math`, `handcore`, `voxel`, `mesh` and
`gameplay`. Any module not in that set is an adapter or edge module and may
depend on external SDKs, but still may not be depended on by a pure module.

### Repository layout

Dossier section 4.1 defines the monorepo layout. This ADR records it with the
`python/depcheck` tool addition that the S0 dependency-rule checker introduced
(dossier section 6, S0 deliverable 3):

```
cubeglass/
  docs/                  # adr/, questions/, dossier, diagrams
  contracts/             # C headers, JSON schemas, shared-memory spec, golden files
  cpp/
    CMakeLists.txt  CMakePresets.json  vcpkg.json
    core-math/  glasses/  recorder/  handcore/  infer/  handservice/
    tests/ (per module)  tools/
  dotnet/
    Cubeglass.sln  global.json
    src/ Voxel/  Mesh/  Gameplay/  CoreMath/
    tests/ Voxel.Tests/ ...
  unity/
    Packages/ com.cubeglass.* (asmdefs)  Assets/  Tests/
  python/
    calib/  depcheck/   pyproject.toml  requirements-dev.txt  tests/
  data/                  # small fixtures only; large datasets via Git LFS or external storage
  .github/workflows/  .editorconfig  .clang-format  .clang-tidy
```

`python/depcheck` is the S0 tool that reads `contracts/layers.json` and enforces
the dependency rule for both the C++ and C# lanes; it is the addition to the
section 4.1 layout.

### Toolchain pins

The build is pinned by `docs/toolchains.md`, which is the single source of truth.
At the S0 baseline the pins are:

| Tool | Pin |
| --- | --- |
| CMake | 4.4.3 |
| Ninja | 1.13.2 |
| clang-format / clang-tidy | 23.1.2 |
| VS Build Tools | 18.9.12105.275 |
| .NET SDK | 10.0.401 (also pinned in `dotnet/global.json`) |
| vcpkg baseline commit | `eb2d3a3279fd019cb7733072d86900d0ad2a1aef` |
| Python | 3.14.7 |
| Unity editor | 6000.6.3f1 |
| GitHub CLI | 2.102.0 |
| Node | 24.19.0 |

Tooling rules per language are fixed by dossier section 4.2: C++20, CMake with
presets, vcpkg manifest, warnings as errors, `clang-format` and `clang-tidy`;
C# with nullable enabled, `TreatWarningsAsErrors`, analyzers, no `UnityEngine`
in non-Unity assemblies, NUnit via `dotnet test`; Unity assembly definitions
with EditMode/PlayMode tests and no business logic in MonoBehaviours; Python
3.11+ with `ruff`, strict `mypy` and `pytest`, used only for offline tools.

### Consequences

- Good: pure modules stay hardware- and engine-free, so the default test run
  needs no glasses and no GPU (dossier section 0 rule 4).
- Good: the same contracts are implemented in C++ and C# and checked against
  shared goldens (dossier section 3.2, section 5).
- Bad: adapters carry translation cost (conventions, units, time), which is why
  those conversions are confined to single boundaries (dossier section 3.6).
- Follow-up: the inward-only rule is enforced from S0 onward and must not be
  relaxed without a superseding ADR.

## Confirmation

The rule is machine-checked. `contracts/layers.json` declares the forbidden
includes for C++ pure modules and the forbidden namespaces plus allowed project
references for C# pure modules. `python -m depcheck --root .` fails the build on
a violation and runs as the required `depcheck` CI job in
`.github/workflows/ci.yml` (dossier section 9 item 3). The dispatch-only negative
self-test `scripts/negative/depcheck-root/` proves the checker still rejects a
forbidden include.

## Links

- Dossier sections: 3.2, 3.3, 4.1, 4.2, and S0 in section 6.
- Related ADRs: [ADR-0002](0002-shared-memory-ipc.md),
  [ADR-0003](0003-testing-strategy.md).
