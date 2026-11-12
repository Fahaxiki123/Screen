Screen 1.0.12 — Third-party notices
Inventory updated on 2026-11-12 from the actual win-x64 publication.

This folder contains third-party license texts, attribution, package versions,
and source records. Each license applies to its respective component.
The Microsoft runtime terms do not replace the licenses of other components.

Runtime dependency inventory
----------------------------
COMPONENTS.txt lists the NuGet package entries in Screen.deps.json,
their exact versions, declared licenses, source commits where available,
and the included license/notice files. This includes:

  Clipper2 2.0.0                              Boost Software License 1.0
  Lucide.NET.Core 0.1.2                       MIT (package declaration)
  Lucide / Feather icon data                  ISC / MIT notices included
  Microsoft.ML.OnnxRuntime[.Managed] 1.27.0    MIT + third-party notices
  NAudio.Core / NAudio.Wasapi / NAudio.WinMM 3.0.0   MIT
  RapidOcrNet 3.0.0                           Apache 2.0
  SharpGen.Runtime[.COM] 2.4.2-beta           MIT
  SkiaSharp[.NativeAssets.Win32] 3.119.1      MIT + third-party notices
  System.Numerics.Tensors 9.0.0               MIT + third-party notices
  Vortice.Direct3D11/DirectX/DXGI 3.8.3       MIT
  Vortice.Mathematics 2.1.0                   MIT

The self-contained publication also includes:

  Microsoft.NETCore.App.Runtime.win-x64 10.0.12
    licenses/microsoft.netcore.app.runtime.win-x64-10.0.12-LICENSE.TXT
    licenses/microsoft.netcore.app.runtime.win-x64-10.0.12-THIRD-PARTY-NOTICES.TXT

  Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.12 (WPF / Windows Forms)
    licenses/microsoft.windowsdesktop.app.runtime.win-x64-10.0.12-LICENSE.txt
    licenses/WPF-10.0.12-THIRD-PARTY-NOTICES.txt
    licenses/WindowsForms-10.0.12-THIRD-PARTY-NOTICES.txt

  Microsoft Visual C++ v14 x64 runtime, file version 14.51.36247.0
    MSVC-RUNTIME.txt and MSVC-FILE-HASHES.txt
    licenses/Microsoft-Visual-C-V14-2026-License.txt
    licenses/Microsoft-Visual-Studio-2026-Community-License.txt

  PP-OCRv6 model files (RapidOCR / PaddleOCR attribution)
    OCR-MODELS.txt
    licenses/RapidOCR-LICENSE.txt and licenses/PaddleOCR-LICENSE.txt

  FFmpeg 9.0.2-screen-minimal (commit 946fcce07b6dcd0331c8cc609192aeff5e1924f8), GPL version 3 or later
    Reproducible build scripts: tools/ffmpeg/build-ffmpeg.sh, tools/ffmpeg/Build-Ffmpeg.ps1
    FFMPEG-BUILD-AND-SOURCE.txt
    licenses/FFmpeg-9.0.2-COPYING.GPLv3.txt
    licenses/FFmpeg-9.0.2-LICENSE.txt
    licenses/libvpl-2.17.0-LICENSE.txt
    licenses/ffnvcodec-headers-13.0.19.0-LICENSE.txt
    licenses/AMF-headers-1.5.3-LICENSE.txt

  NSIS 3.12 installer runtime and compression components
    licenses/NSIS-3.12-COPYING.txt (unmodified from the compiler distribution)
    Project and source: https://nsis.sourceforge.io/
    https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/

Source records and remaining provenance notes
---------------------------------------------
UPSTREAM-SOURCES.txt records upstream license URLs and SHA256 hashes.
NOTICE-FILES-SHA256.txt verifies the included texts. NuGet license files were
copied unchanged from the exact cached package versions. Microsoft Word
license documents were converted to paragraph text; formatting was removed.

The original Lucide.NET.Core package has a placeholder repository URL and
no standalone LICENSE file. Its MIT declaration and author metadata are
preserved in COMPONENTS.txt; the NuGet MIT reference and official Lucide /
Feather icon notices are included.

OCR-MODELS.txt records the existing project attribution and actual file hashes
for the bundled PP-OCRv6 / PP-LCNet ONNX models.

FFmpeg is built reproducibly from pinned upstream release tag n9.0.2 with
explicitly enumerated minimal components (x264, libvpl, ffnvcodec-headers,
AMF headers); exact revisions, source URLs, and ./configure flags are recorded
in FFMPEG-BUILD-AND-SOURCE.txt.
