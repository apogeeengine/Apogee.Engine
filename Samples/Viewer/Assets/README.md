# Viewer sample assets

Small glTF models from [KhronosGroup/glTF-Sample-Assets](https://github.com/KhronosGroup/glTF-Sample-Assets) (commit
`8f492390ddc7a2ae221a1369ef560589cf8c70fb`), `Models/<name>/glTF-Binary/<name>.glb`, unchanged. Every one is **CC0-1.0** (public domain); each folder
keeps the upstream `LICENSE.md`. They are Git LFS objects.

| Model | What it exercises | sha256 |
| --- | --- | --- |
| WaterBottle | metal/roughness PBR with normal, occlusion and emissive textures | `b337e526fd6a162013c2984aeec163f5fbb4f717252724dfc3f3458bd51df94b` |
| Lantern | textured PBR, a tall asset | `a79458c4b02d695187a952f23a63b8bf278e7bc3d316a3c2a314f2d6974181f1` |
| BoomBox | emissive texture, tiny units (it is 2 cm tall) | `f8b918445ebdd006768232205a62f5182d2208ca57f84c6ccc084943c0bc8f15` |
| NormalTangentTest | normal map with no tangents in the file (ours are generated) | `5ac0932355ae1ea05a7485eeddcc3f1fbe56c678e00b519075feced80e9e9d6a` |
| ToyCar | PBR with KHR extensions | `01a60862de55cd4b9f3acfab0b0def86451800f9c42467fcd61052c16cb9838c` |
| SheenChair | a textured fabric chair | `f0af2a2b102d28d540236306ae19f8fb36842df76bd38cf76f063f9bd2853399` |

The courtyard in `Samples/Viewer` is generated in code and needs no files.
