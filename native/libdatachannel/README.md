# libdatachannel runtime assets

These data-only libdatachannel v0.24.6 builds use upstream commit
6b1e2e620f1e37f0eafeee702eaea0043cb305fd. The source and recursive submodule
revisions are available from the [upstream repository](https://github.com/paullouisageneau/libdatachannel/tree/6b1e2e620f1e37f0eafeee702eaea0043cb305fd).
The corresponding license texts are included in this directory.

Run `pwsh scripts/build-native-rtc.ps1 -Target win-x64` (or `linux-x64`,
`linux-arm64`) with a Linux Docker engine. The recipe clones the pinned source
and submodules, uses a pinned Ubuntu build image, disables media/WebSockets,
and puts output under `artifacts/native-rtc/native`. Windows cross-builds
statically link a checksum-verified OpenSSL 3.5.7 archive. Linux builds target
Ubuntu 24.04 and dynamically link its system OpenSSL. Linux ARM64 may require
native ARM64 Docker or emulation. Compiler and distribution packages can vary
with the package mirror, so an independent rebuild can have a different hash.

The checked-in binaries match the SHA-256 values in `manifest.json`. Run
`node scripts/check-native-rtc.mjs` to verify them. Windows x64 and Ubuntu
Linux x64 were exercised; Linux ARM64 is built but has not been runtime tested.
For macOS, install Xcode command-line tools, CMake, and Homebrew `openssl@3`.
Use a matching native terminal and run `pwsh scripts/build-native-rtc.ps1
-Target osx-arm64` on Apple Silicon or use `-Target osx-x64` on Intel. Build or
publish the game afterward. Both server and terminal copy the generated dylib
into their app-local runtime directory automatically. The build uses static
OpenSSL, and CI records the installed OpenSSL version and checks linked
dependencies with `otool -L` before loading the published native library.
There is no shipped macOS binary, and macOS gameplay has not been verified.
