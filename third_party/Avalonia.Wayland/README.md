# Patched Avalonia Wayland backend

This directory contains only the source needed to build `Avalonia.Wayland` for
WattLedger's .NET 10 Linux desktop. It contains 82 Wayland C# files (including
the pointer-pinch helper), five linked upstream shared/X11 files, and three
upstream generator files. The remaining framework assemblies come from official NuGet packages.

Upstream: [Avalonia 12.1.4](https://github.com/AvaloniaUI/Avalonia/tree/2d79ba55fae1b1e71c69f93a668080c9029e6a31)
(commit `2d79ba55fae1b1e71c69f93a668080c9029e6a31`).
MIT license and copyright notice: [LICENSE-Avalonia.md](LICENSE-Avalonia.md).
`source-manifest.json` records the pinned revision, patch SHA256, and SHA256 of
every vendored source file after applying the patch.

## Build

The Linux desktop references `Avalonia.Wayland.csproj` instead of the official
`Avalonia.Wayland` package. A normal `dotnet restore` / `dotnet build` needs no
Git, Python, upstream checkout, or source-generation download. It works on any
host with .NET 10 SDK, including Windows; runtime Wayland functionality still
requires Linux and a compositor supporting `zwp_pointer_gestures_v1`.

The backend retains `Avalonia.Wayland, Version=12.1.4.0` and upstream public key
token `c8d484a7012f9a8b`. `avalonia-public.snk` contains only the upstream public
key. Public signing permits the existing upstream friend-assembly APIs; it does
not include or require an upstream private signing key. There is no reflection
against private APIs.

Official Avalonia and FreeDesktop packages are pinned to 12.1.4, and NWayland to
0.11.0. The project deliberately references official `lib/net10.0` assemblies:
Avalonia's public reference assemblies remove APIs needed by its platform
implementations. Package locations come from NuGet's generated path properties,
without machine-specific paths. The source generators use Roslyn from the
installed SDK and compile only the two required generators and their helper.
The existing upstream `pollfd.revents` native-output-field warning is suppressed
by the project. Upstream source is marked generated to exclude automatic
formatting and keep the upstream/patch comparison stable. The resulting CS8669
nullable-context warnings for these verbatim files are also suppressed.

## Verify source provenance

Run this from the repository root, substituting the checked-in patch path:

```sh
python3 third_party/Avalonia.Wayland/verify-source.py \
  --patch docs/patches/avalonia-12.1.4-wayland-pinch.patch
```

The verifier downloads the exact Git commit into a temporary directory, confirms
the commit, extracts only required files, checks that the patch applies cleanly,
and verifies all resulting source hashes, the original MIT notice, and the public
signing key. It does not modify the vendored tree. An existing official clone can
be reused offline by adding `--source /path/to/Avalonia` (working-tree changes are
ignored; Git objects at the pinned revision are read). On Windows, use `python`
instead of `python3` where necessary.

When upgrading Avalonia, update the dependency versions, upstream revision,
reviewable protocol patch, vendored source and manifest together, then run the
verifier and build. Do not mix this backend with another Avalonia version.
