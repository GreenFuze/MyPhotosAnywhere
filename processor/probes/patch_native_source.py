"""Reviewed compatibility fixes for the pinned CPU-only ORT build, not warning filters.

Each edit requires its exact source anchor; drift fails before compilation. Upstream
licenses stay in place. Before/after hashes and reasons accompany every modified file.
"""
from pathlib import Path
import hashlib
import json
import subprocess
import sys


REVISION = "402979ac1bef89dcd55add41eaa7277c407599eb"


class PatchBook:
    def __init__(self, root):
        self.root = Path(root).resolve(strict=True)
        self.records = []

    def edit(self, relative, replacements, reason):
        path = self.root / relative
        if not path.resolve(strict=True).is_relative_to(self.root):
            raise RuntimeError("Native source patch escaped its checked root")
        original = path.read_bytes()
        text = original.decode("utf-8")
        for old, new, count in replacements:
            if text.count(old) != count:
                raise RuntimeError(f"Pinned source anchor drift: {relative}: {old[:70]!r}")
            text = text.replace(old, new)
        changed = text.encode("utf-8")
        path.write_bytes(changed)
        previous = next((record for record in self.records if record["file"] == relative), None)
        if previous is None:
            self.records.append({"file": relative, "before": hashlib.sha256(original).hexdigest(),
                                 "after": hashlib.sha256(changed).hexdigest(), "reasons": [reason]})
        else:
            if previous["after"] != hashlib.sha256(original).hexdigest():
                raise RuntimeError("Native patch source changed between owned edits")
            previous["after"] = hashlib.sha256(changed).hexdigest()
            previous["reasons"].append(reason)

    def finish(self):
        report = self.root / ".mpa-source-changes.json"
        report.write_text(json.dumps({"ortRevision": REVISION, "changes": self.records}, indent=2) + "\n", encoding="utf-8")

    def runtime(self):
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=self.root, text=True, timeout=15).strip()
        if revision != REVISION:
            raise RuntimeError("Native compatibility fixes require the exact reviewed runtime revision")
        self.edit("cmake/CMakeLists.txt", [
            ("cmake_policy(SET CMP0104 OLD)", "cmake_policy(SET CMP0104 NEW)\n"
             "# MPA CPU-only build: honor linker capabilities when deduplicating libraries.\n"
             "if(POLICY CMP0156)\n  cmake_policy(SET CMP0156 NEW)\nendif()\n"
             "if(POLICY CMP0179)\n  cmake_policy(SET CMP0179 NEW)\nendif()", 1)
        ], "Use current policies for the CPU-only build; no CUDA language is enabled")
        self.edit("tools/ci_build/build.py", [
            ('        "-DDAWN_USE_AGILITY_SDK=" + ("ON" if args.use_dawn_agility_sdk else "OFF"),',
             '        *(["-DDAWN_USE_AGILITY_SDK=" + ("ON" if args.use_dawn_agility_sdk else "OFF")] if args.use_webgpu else []),', 1)
        ], "Supply Dawn's option only when its WebGPU project is enabled")

        def hook(dependency):
            return '\n    COMMAND "${Python_EXECUTABLE}" "${MPA_NATIVE_PATCHER}" dependency ' + dependency + ' "<SOURCE_DIR>"'

        self.edit("cmake/external/onnxruntime_external_deps.cmake", [
            ("${PROJECT_SOURCE_DIR}/patches/date/date.patch\n", "${PROJECT_SOURCE_DIR}/patches/date/date.patch" + hook("date") + "\n", 1)
        ], "Apply the date policy fix before configuring the fetched dependency")
        # Preserve each existing upstream patch command; append our dependency fix.
        self.edit("cmake/external/onnxruntime_external_deps.cmake", [
            ("  PATCH_COMMAND ${ONNXRUNTIME_PROTOBUF_PATCH_COMMAND}\n", "  PATCH_COMMAND ${ONNXRUNTIME_PROTOBUF_PATCH_COMMAND}" + hook("protobuf") + "\n", 1),
            ("    PATCH_COMMAND ${ONNXRUNTIME_FLATBUFFERS_PATCH_COMMAND}\n", "    PATCH_COMMAND ${ONNXRUNTIME_FLATBUFFERS_PATCH_COMMAND}" + hook("flatbuffers") + "\n", 1),
            ("     URL ${DEP_URL_mp11}\n", "     URL ${DEP_URL_mp11}\n     SOURCE_SUBDIR mpa-download-only\n", 1),
            ("    FetchContent_Populate(mp11)", "    FetchContent_MakeAvailable(mp11)", 1),
            ("      URL ${DEP_URL_safeint}\n", "      URL ${DEP_URL_safeint}\n      SOURCE_SUBDIR mpa-download-only\n", 1),
            ("  FetchContent_Populate(safeint)", "  FetchContent_MakeAvailable(safeint)", 1),
            ("URL_HASH SHA1=${DEP_SHA1_protoc_mac_universal} EXCLUDE_FROM_ALL)\n    FetchContent_Populate(protoc_binary)",
             "URL_HASH SHA1=${DEP_SHA1_protoc_mac_universal} SOURCE_SUBDIR mpa-download-only EXCLUDE_FROM_ALL)\n    FetchContent_MakeAvailable(protoc_binary)", 1),
            ('  if (NOT onnxruntime_USE_FULL_PROTOBUF)\n    target_compile_definitions(onnx PUBLIC "__ONNX_NO_DOC_STRINGS")\n  endif()',
             '  # MPA retains ONNX operator documentation: its generators use their named\n'
             '  # arguments/constants, without changing protobuf flavor or graph operators.', 1)
        ], "Modern download-only FetchContent; retain ONNX documentation; preserve upstream dependency fixes")
        self.edit("cmake/external/eigen.cmake", [
            ("${Patch_EXECUTABLE} --binary --ignore-whitespace -p1 < ${PATCH_EIGEN_S390X_WERROR}\n",
             "${Patch_EXECUTABLE} --binary --ignore-whitespace -p1 < ${PATCH_EIGEN_S390X_WERROR}" + hook("eigen") + "\n", 1)
        ], "Apply Eigen's CPU-only policy update before dependency configuration")
        self.edit("onnxruntime/core/mlas/lib/x86_64/QgemmU8S8KernelAmx.S", [
            ("        .end\n", '#if defined(__ELF__)\n        .section .note.GNU-stack,"",@progbits\n#endif\n\n        .end\n', 1)
        ], "Declare a non-executable stack for code that contains no stack-executed instructions")

    def dependency(self, name):
        if name == "date":
            self.edit("CMakeLists.txt", [("cmake_minimum_required( VERSION 3.7 )", "cmake_minimum_required( VERSION 3.7...3.28 )", 1)],
                      "Adopt current policies for the header-only date dependency")
        elif name == "protobuf":
            self.edit("CMakeLists.txt", [("cmake_policy(SET CMP0141 OLD)", "cmake_policy(SET CMP0141 NEW)", 1)],
                      "Current MSVC debug-information policy; this native path builds on Linux/macOS")
        elif name == "eigen":
            self.edit("CMakeLists.txt", [("cmake_policy(SET CMP0146 OLD)", "cmake_policy(SET CMP0146 NEW)", 1)],
                      "Current CUDA discovery policy; CPU-only Eigen never enables CUDA")
        elif name == "flatbuffers":
            self.edit("src/bfbs_gen_nim.cpp", [('const std::string Indent = "  ";\n', "", 1)], "Remove an unused generator constant")
            self.edit("src/reflection.cpp", [
                ('#include "flatbuffers/reflection.h"', '#include <stdexcept>\n\n#include "flatbuffers/reflection.h"', 1),
                ('  // Align to sizeof(uoffset_t) past sizeof(largest_scalar_t) since we\'re\n',
                 '  // MPA: reject truncated/invalid input before pointer arithmetic.\n'
                 '  if (newbuf == nullptr || newlen < sizeof(uoffset_t))\n'
                 '    throw std::invalid_argument("Truncated appended FlatBuffer");\n'
                 '  const auto root = ReadScalar<uoffset_t>(newbuf);\n'
                 '  if (root < sizeof(uoffset_t) || root >= newlen)\n'
                 '    throw std::invalid_argument("Invalid appended FlatBuffer root");\n'
                 '  if (flatbuf.size() > flatbuf.max_size() - sizeof(largest_scalar_t) ||\n'
                 '      newlen > flatbuf.max_size() - flatbuf.size() - sizeof(largest_scalar_t))\n'
                 '    throw std::length_error("Appended FlatBuffer exceeds vector capacity");\n'
                 '  // Align to sizeof(uoffset_t) past sizeof(largest_scalar_t) since we\'re\n', 1),
                ('  flatbuf.insert(flatbuf.end(), newbuf + sizeof(uoffset_t), newbuf + newlen);\n'
                 '  auto root_offset = ReadScalar<uoffset_t>(newbuf) - sizeof(uoffset_t);',
                 '  flatbuf.reserve(flatbuf.size() + newlen - sizeof(uoffset_t));\n'
                 '  for (size_t i = sizeof(uoffset_t); i < newlen; ++i) flatbuf.push_back(newbuf[i]);\n'
                 '  auto root_offset = root - sizeof(uoffset_t);', 1)
            ], "Bound append arithmetic and copy valid bytes without GCC's vector range-insert overflow diagnostic")
        else:
            raise RuntimeError("Unreviewed native dependency")


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "runtime":
        book = PatchBook(sys.argv[2])
        book.runtime()
    elif len(sys.argv) == 4 and sys.argv[1] == "dependency":
        book = PatchBook(sys.argv[3])
        book.dependency(sys.argv[2])
    else:
        raise RuntimeError("Usage: patch_native_source.py runtime <checkout> | dependency <name> <source>")
    book.finish()
