param([ValidateSet("linux-x64","linux-arm64","win-x64","osx-x64","osx-arm64")][string]$Target = "linux-x64")
$ErrorActionPreference = "Stop"
$buildRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "artifacts/native-rtc"
New-Item -ItemType Directory -Force $buildRoot | Out-Null
$source = Join-Path $buildRoot "upstream"
$tag = "v0.24.6"
$commit = "6b1e2e620f1e37f0eafeee702eaea0043cb305fd"
$image = "ubuntu@sha256:2198e4c65076e9cb0b3d695219df667c0e3b0072da64ca3c2648aca17dcecaf5"
if (!(Test-Path $source)) {
    git clone --branch $tag --depth 1 --recurse-submodules --shallow-submodules https://github.com/paullouisageneau/libdatachannel.git $source
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
if ((git -C $source rev-parse HEAD) -ne $commit) { throw "Unexpected source commit." }
$submodules = git -C $source submodule status --recursive
$submodules
if ($LASTEXITCODE -ne 0 -or !$submodules -or ($submodules | Where-Object { !($_.StartsWith(" ")) })) {
    throw "Missing or mismatched upstream submodule."
}
if ($Target.StartsWith("osx-")) {
    if (!$IsMacOS) { throw "A matching macOS host with Xcode tools, CMake, and Homebrew OpenSSL 3 is required." }
    $arch = if ($Target -eq "osx-arm64") { "arm64" } else { "x86_64" }
    if ((uname -m) -ne $arch) { throw "Build the native library on a matching macOS architecture." }
    $openssl = brew --prefix openssl@3
    if ($LASTEXITCODE -ne 0 -or !(Test-Path (Join-Path $openssl "lib/libssl.a"))) {
        throw "Install openssl@3 for the native build."
    }
    $build = Join-Path $buildRoot $Target
    $flags = @(
        "-DCMAKE_BUILD_TYPE=Release", "-DCMAKE_OSX_ARCHITECTURES=$arch", "-DBUILD_SHARED_LIBS=ON",
        "-DBUILD_SHARED_DEPS_LIBS=OFF", "-DNO_MEDIA=ON", "-DNO_WEBSOCKET=ON", "-DNO_EXAMPLES=ON",
        "-DNO_TESTS=ON", "-DUSE_NICE=OFF", "-DCAPI_STDCALL=OFF", "-DOPENSSL_ROOT_DIR=$openssl",
        "-DOPENSSL_USE_STATIC_LIBS=TRUE"
    )
    cmake -S $source -B $build @flags
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    cmake --build $build --parallel
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $output = Join-Path $buildRoot "native/$Target"
    New-Item -ItemType Directory -Force $output | Out-Null
    Copy-Item -LiteralPath (Join-Path $build "libdatachannel.dylib") -Destination $output
    Get-FileHash (Join-Path $output "libdatachannel.dylib")
    $dependencies = otool -L (Join-Path $output "libdatachannel.dylib")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $dependencies
    if ($dependencies | Select-Object -Skip 1 | Where-Object { $_ -match '\blib(ssl|crypto)\.' }) {
        throw "The macOS build unexpectedly links dynamic OpenSSL."
    }
    exit 0
}

$script = @'
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq --no-install-recommends cmake ninja-build make ca-certificates
flags=(-DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DBUILD_SHARED_DEPS_LIBS=OFF -DNO_MEDIA=ON -DNO_WEBSOCKET=ON -DNO_EXAMPLES=ON -DNO_TESTS=ON -DUSE_NICE=OFF -DCAPI_STDCALL=OFF)
if [ "$1" = win-x64 ]; then
  apt-get install -y -qq --no-install-recommends g++-mingw-w64-x86-64-posix curl perl
  x86_64-w64-mingw32-g++-posix --version
  curl -fsSL https://github.com/openssl/openssl/releases/download/openssl-3.5.7/openssl-3.5.7.tar.gz -o /tmp/openssl.tar.gz
  echo 'a8c0d28a529ca480f9f36cf5792e2cd21984552a3c8e4aa11a24aa31aeac98e8  /tmp/openssl.tar.gz' | sha256sum -c
  mkdir /tmp/openssl
  tar -xzf /tmp/openssl.tar.gz --strip-components=1 -C /tmp/openssl
  cd /tmp/openssl
  ./Configure mingw64 --cross-compile-prefix=x86_64-w64-mingw32- no-shared no-tests --prefix=/tmp/openssl-win
  make -j8 build_libs
  make install_dev
  flags+=(-DCMAKE_SYSTEM_NAME=Windows -DCMAKE_C_COMPILER=x86_64-w64-mingw32-gcc-posix -DCMAKE_CXX_COMPILER=x86_64-w64-mingw32-g++-posix -DOPENSSL_ROOT_DIR=/tmp/openssl-win -DOPENSSL_USE_STATIC_LIBS=TRUE "-DCMAKE_SHARED_LINKER_FLAGS=-static-libgcc -static-libstdc++ -static")
else
  apt-get install -y -qq --no-install-recommends build-essential libssl-dev pkg-config
  c++ --version
fi
cmake --version
cmake -S /spike/upstream -B /tmp/rtc -G Ninja "${flags[@]}"
cmake --build /tmp/rtc -j8
mkdir -p "/spike/native/$1"
if [ "$1" = win-x64 ]; then
  cp /tmp/rtc/libdatachannel.dll /spike/native/win-x64/datachannel.dll
  sha256sum /spike/native/win-x64/datachannel.dll
  x86_64-w64-mingw32-objdump -p /spike/native/win-x64/datachannel.dll | grep "DLL Name"
else
  cp -L /tmp/rtc/libdatachannel.so "/spike/native/$1/libdatachannel.so"
  sha256sum "/spike/native/$1/libdatachannel.so"
  ldd "/spike/native/$1/libdatachannel.so"
fi
'@
$platform = if ($Target -eq "linux-arm64") { "linux/arm64" } else { "linux/amd64" }
docker run --rm --platform $platform --mount "type=bind,source=$buildRoot,target=/spike" $image bash -lc $script spike $Target
exit $LASTEXITCODE
