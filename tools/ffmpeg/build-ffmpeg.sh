#!/usr/bin/env bash
set -euo pipefail

# Reproducible minimal static GPLv3 FFmpeg build for Screen (win-x64).
# Includes ONLY the codecs, containers, filters, and hardware accelerators
# required by Screen's recording, audio muxing, preview playback, and trim export.

FFMPEG_SRC="${1:?Usage: build-ffmpeg.sh <ffmpeg-src-dir> <build-dir> <output-dir>}"
BUILD_DIR="${2:?Usage: build-ffmpeg.sh <ffmpeg-src-dir> <build-dir> <output-dir>}"
OUT_DIR="${3:?Usage: build-ffmpeg.sh <ffmpeg-src-dir> <build-dir> <output-dir>}"

export PATH="/mingw64/bin:/usr/bin"
export PKG_CONFIG_PATH="/mingw64/lib/pkgconfig"

rm -rf "${BUILD_DIR}"
mkdir -p "${BUILD_DIR}" "${OUT_DIR}"
cd "${BUILD_DIR}"

"${FFMPEG_SRC}/configure" \
  --prefix="${OUT_DIR}/prefix" \
  --arch=x86_64 \
  --target-os=mingw32 \
  --pkg-config=pkgconf \
  --pkg-config-flags="--static" \
  --extra-cflags="-O2 -UX264_API_IMPORTS" \
  --extra-ldflags="-static -static-libgcc -static-libstdc++" \
  --extra-libs="-lstdc++" \
  --extra-version="screen-minimal" \
  --enable-gpl \
  --enable-version3 \
  --enable-static \
  --disable-shared \
  --disable-debug \
  --disable-doc \
  --disable-htmlpages \
  --disable-manpages \
  --disable-podpages \
  --disable-txtpages \
  --disable-network \
  --disable-autodetect \
  --disable-avdevice \
  --disable-ffplay \
  --disable-ffprobe \
  --enable-ffmpeg \
  --enable-swscale \
  --enable-swresample \
  --enable-avcodec \
  --enable-avformat \
  --enable-avfilter \
  --enable-w32threads \
  --enable-d3d11va \
  --enable-dxva2 \
  --enable-ffnvcodec \
  --enable-nvenc \
  --enable-amf \
  --enable-libvpl \
  --enable-libx264 \
  --disable-everything \
  --enable-protocol=file,pipe,fd \
  --enable-hwaccel=h264_d3d11va,h264_d3d11va2,h264_dxva2 \
  --enable-encoder=libx264,h264_nvenc,h264_qsv,h264_amf,aac,rawvideo,pcm_s16le,pcm_f32le,wrapped_avframe \
  --enable-decoder=h264,aac,rawvideo,pcm_s16le,pcm_f32le,wrapped_avframe \
  --enable-parser=h264,aac \
  --enable-demuxer=mov,rawvideo,pcm_s16le,pcm_f32le,h264,aac \
  --enable-muxer=mp4,mov,ipod,rawvideo,pcm_s16le,pcm_f32le,image2pipe,null \
  --enable-bsf=aac_adtstoasc,h264_mp4toannexb,extract_extradata,null \
  --enable-filter=buffer,buffersink,abuffer,abuffersink,scale,format,aformat,null,anull,aresample,apad,amix,atempo,hwdownload,hwupload

JOBS="$(nproc 2>/dev/null || echo 8)"
make -j"${JOBS}"
cp -f ffmpeg.exe "${OUT_DIR}/ffmpeg.exe"
strip -s "${OUT_DIR}/ffmpeg.exe"

{
  echo "=== DLL Imports ==="
  objdump -p "${OUT_DIR}/ffmpeg.exe" | grep "DLL Name:" || true
  echo ""
  echo "=== ffmpeg -version ==="
  "${OUT_DIR}/ffmpeg.exe" -version
  echo ""
  echo "=== ffmpeg -buildconf ==="
  "${OUT_DIR}/ffmpeg.exe" -buildconf
} > "${OUT_DIR}/build-manifest.txt"

ls -lh "${OUT_DIR}/ffmpeg.exe"
