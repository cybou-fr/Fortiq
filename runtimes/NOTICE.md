# The inference runtime Fortiq ships

The assistant's model is run by llama.cpp, as a separate process on the local machine. It listens on
the loopback interface only, is started by Fortiq and dies with it, and is never reachable from
anywhere else.

## llama-server b10830 (win-x64)

| | |
|---|---|
| Project | [llama.cpp](https://github.com/ggml-org/llama.cpp) by the ggml authors |
| Build | `llama-b10830-bin-win-cpu-x64.zip` |
| Archive size | 18,416,266 bytes |
| Archive SHA-256 | `2bdf856e95d4070ccb9052322f8598b9deb08fe2b4a6870740231fb375388039` |
| Licence | MIT — the full text is in [LICENSE-llama.cpp.txt](LICENSE-llama.cpp.txt) |

The CPU build, deliberately. Spec 28 requires that a CPU-capable runtime stay the supported
baseline, and a GPU build would make the assistant depend on the machine — which is the mistake the
earlier Phi Silica design made, in a different form.

The archive also contains LLVM's OpenMP runtime under the Apache 2.0 licence with the LLVM
exception; its licence file travels inside the archive and is extracted with it.

## Why the archive is what is pinned

The engine, restic, is one executable, and pinning that file's hash pins everything that runs.
llama-server is not: `llama-server.exe` is a nine-kilobyte launcher, and every line of code that
matters is in `llama-server-impl.dll` and the fifteen `ggml-cpu-*.dll` files beside it. A manifest
that pinned the executable's hash would look rigorous and would verify almost nothing.

So the archive hash is checked before anything is extracted, which is the point where the whole tree
is still one object. After that, per-file integrity is the deployment bundle's job:
`bundle-manifest.json` lists every file that gets installed with its own hash, and the installer
verifies the payload against it.
