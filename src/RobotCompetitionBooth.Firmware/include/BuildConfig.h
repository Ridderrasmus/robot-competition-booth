#pragma once

#include <cstdint>

#ifndef ROBOBOOTH_DEVICE_NAME
#define ROBOBOOTH_DEVICE_NAME "RobotBooth-ESP32S3"
#endif

#ifndef ROBOBOOTH_PAIRING_PASSKEY
#define ROBOBOOTH_PAIRING_PASSKEY 123
#endif

namespace RobotBuildConfig {

constexpr char DeviceName[] = ROBOBOOTH_DEVICE_NAME;
constexpr std::uint32_t PairingPasskey = ROBOBOOTH_PAIRING_PASSKEY;

static_assert(sizeof(DeviceName) > sizeof("RobotBooth-"), "The robot name must include a suffix.");
static_assert(sizeof(DeviceName) <= 25, "The robot name must be at most 24 characters.");
static_assert(PairingPasskey <= 999999, "The BLE pairing passkey must contain at most six digits.");

} // namespace RobotBuildConfig
