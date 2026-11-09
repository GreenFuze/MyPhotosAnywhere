// MPA regression checks for the pinned FlatBuffers append compatibility fix.
#include <algorithm>
#include <cstdint>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <vector>
#include "flatbuffers/flatbuffers.h"
#include "flatbuffers/reflection.h"

static void Require(bool condition) {
  if (!condition) throw std::runtime_error("FlatBuffer append invariant failed");
}

template <typename Exception, typename Operation>
static void MustReject(Operation operation) {
  bool rejected = false;
  try { operation(); } catch (const Exception&) { rejected = true; }
  Require(rejected);
}

int main() {
  flatbuffers::FlatBufferBuilder builder;
  builder.Finish(builder.CreateString("mpa-native-fixture"));
  const auto* source = builder.GetBufferPointer();
  const auto length = builder.GetSize();
  for (size_t prefix = 0; prefix < 65; ++prefix) {
    std::vector<uint8_t> destination(prefix, 0x5a);
    const auto* appended = flatbuffers::AddFlatBuffer(destination, source, length);
    Require(std::all_of(destination.begin(), destination.begin() + prefix,
                        [](uint8_t value) { return value == 0x5a; }));
    Require(reinterpret_cast<const flatbuffers::String*>(appended)->str() == "mpa-native-fixture");
    Require(std::equal(source + sizeof(flatbuffers::uoffset_t), source + length,
                       destination.end() - (length - sizeof(flatbuffers::uoffset_t))));
  }
  std::vector<uint8_t> destination;
  MustReject<std::invalid_argument>([&] { flatbuffers::AddFlatBuffer(destination, nullptr, 0); });
  MustReject<std::invalid_argument>([&] { flatbuffers::AddFlatBuffer(destination, source, 3); });
  const uint8_t invalidRoot[8] = {};
  MustReject<std::invalid_argument>([&] { flatbuffers::AddFlatBuffer(destination, invalidRoot, sizeof(invalidRoot)); });
  const uint8_t outsideRoot[8] = { 8, 0, 0, 0, 0, 0, 0, 0 };
  MustReject<std::invalid_argument>([&] { flatbuffers::AddFlatBuffer(destination, outsideRoot, sizeof(outsideRoot)); });
  MustReject<std::length_error>([&] { flatbuffers::AddFlatBuffer(destination, source, std::numeric_limits<size_t>::max()); });
  std::cout << "FlatBuffer append checks passed: 65 prefixes, exact bytes, truncated/root/capacity rejection.\n";
}
