# Robobooth MQTT protocol v1

## Scope

Version 1 defines the application contract between one authenticated robot and the booth web host. MQTT provides
transport and delivery; JSON schemas define payloads. The current web application receives and validates sensor
snapshots. Program deployment/control and device-side instruction execution are intentionally specified here before
they are wired into firmware.

The topic root is:

```text
robobooth/v1/devices/{deviceId}
```

The authenticated MQTT client ID must equal `{deviceId}`. A device may publish only below its own root. The web host
must verify that rule and validate every payload before updating application state.

## Topics

| Suffix | Publisher | QoS | Retained | Maximum payload | Purpose |
|---|---|---:|---:|---:|---|
| `/status` | Robot/LWT | 1 | Yes | 16 B | Literal UTF-8 `online` or `offline`. |
| `/state/color` | Robot | 0 | No | 512 B | Legacy booth-colour message; remains supported during migration. |
| `/telemetry/sensors` | Robot | 0 | No | 8 KiB | Latest non-blocking sensor, encoder, and servo snapshot. |
| `/program/deploy` | Web host | 1 | No | 256 KiB | Atomic program-package deployment request. |
| `/program/control` | Web host | 1 | No | 2 KiB | Run or stop the saved program. |
| `/program/status` | Robot | 1 | Yes | 4 KiB | Latest deployment/runtime state and request acknowledgement. |

QoS 1 messages may be delivered more than once. The robot must deduplicate deploy/control messages by `requestId`.
The web host must order sensor/status updates by `sequence` and ignore older or repeated values.

## Idle sensor stream

When the user program is not running, the robot publishes
[`sensor-snapshot.schema.json`](schemas/sensor-snapshot.schema.json) at 5 Hz. A rate from 2-10 Hz is acceptable when
network conditions or sensor timing budgets require it. Reporter blocks read the same latest in-memory sensor cache;
they must not initiate a blocking measurement.

The required idle loop is:

1. Poll hardware on its own schedule.
2. Update a complete in-memory snapshot atomically.
3. Publish the latest snapshot without waiting for a subscriber.
4. Continue servicing safety, MQTT, and BLE work even if a sensor is invalid.

During `running`, this implementation does not publish live telemetry. Sensor polling itself continues so
program reporter blocks remain current. A missing reading is represented by `valid: false` and a nullable measurement;
it must never freeze the program or MQTT loop. The web UI considers a snapshot stale after two seconds.

## Compile and deployment lifecycle

1. The editor saves Blockly serialization JSON under the selected device and workspace name.
2. The web host's C# `WorkspaceCompiler` validates that graph and emits a
   [`program-package.schema.json`](schemas/program-package.schema.json) instruction package. Blockly coordinates,
   comments, and visual layout do not enter the instruction graph.
   `tools/compile_workspace.py` remains a compatible developer utility and is not used by the running application.
3. The web host validates the package and computes SHA-256 over the exact UTF-8 package bytes.
4. The host publishes [`program-deploy.schema.json`](schemas/program-deploy.schema.json) with QoS 1.
5. The robot validates the envelope, digest, contract version, opcodes, resource limits, and safety policy before
   touching the active program.
6. The robot writes to a temporary slot, verifies the persisted bytes, then atomically marks that slot active. A power
   loss must leave either the previous valid program or the new valid program selected.
7. The robot publishes `program/status` with the same `requestId` and state `ready`, or state `failed` plus a stable
   error code. A duplicate deploy request returns the previous result without rewriting flash.
8. The host sends a [`program-control.schema.json`](schemas/program-control.schema.json) `run` command. The device
   acknowledges with state `running` only after the requested active program starts.
9. `stop`, normal termination, a runtime fault, or loss of required safety services stops both motors and applies the
   configured safe actuator policy before publishing the resulting state.

Only a successfully deployed program may run. A loose top-level Blockly stack is retained in the instruction package
for diagnostics but is not an automatic entrypoint.

## Program status states

`idle -> receiving -> validating -> ready -> running -> stopped` is the normal path. `failed` and `fault` are terminal
for the current operation but do not erase the last known-good package. The robot publishes a monotonically increasing
status `sequence` on every transition.

Stable v1 error codes:

- `unsupported-version`
- `invalid-envelope`
- `digest-mismatch`
- `invalid-program`
- `unsupported-opcode`
- `resource-limit`
- `storage-failed`
- `program-not-found`
- `runtime-fault`
- `safety-stop`

Human-readable `detail` is diagnostic only. Application logic must branch on `state` and `errorCode`.

## Instruction IR

Each instruction node contains its stable Blockly `opcode`, block `id`, fields, connected input nodes, and optional
`next` instruction. The runtime owns opcode semantics and input validation. It must reject unknown opcodes during
deployment, not halfway through a run.

Entrypoint kinds are:

- `onStart`: executes once after hardware/runtime initialization.
- `forever`: scheduled cooperatively and yields between iterations.
- `event`: triggered with debounce/hysteresis rules owned by that sensor/runtime opcode.
- `function`: callable definition, not scheduled automatically.
- `looseStack`: diagnostic-only stack that never starts automatically.

The package safety flags are mandatory and true in v1. User-program termination and runtime faults therefore always
stop motor outputs and apply the device's configured safe actuator policy.

## Security and operational limits

The existing broker uses an authenticated per-installation token provisioned over encrypted BLE. MQTT traffic is not
TLS-encrypted, so the booth LAN must be trusted and isolated. Payload validation, topic ownership, size limits, and
idempotency are required even on that trusted network.

Never log MQTT credentials or raw Wi-Fi credentials. Program packages and telemetry contain no secrets and may be
logged at debug level, but deployment payload logging should still be bounded by size.
