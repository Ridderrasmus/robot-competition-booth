# Robot Competition Booth

A unified project for controlling a premade Arduino-based robot through a compact Blazor Server application with a Blockly programming interface.

## Project Goal

This repository is designed to host:

- A **Blazor Server** application (operator UI + local control backend)
- A **Python** executable that will be the blockly workspace JSON to robot compatible instructions compiler
- A **C++ Arduino** firmware project (robot runtime)
- Shared protocol/docs for communication between app and robot

The main objective is to allow users to visually program robot behaviors using **Blockly**, then deploy and run those behaviors on a premade Arduino robot.

## Planned System Design

### 1) Visual Programming with Blockly
- The Blazor Server app embeds Blockly for drag-and-drop robot programming.
- Blockly blocks represent robot capabilities (movement, sensors, conditions, loops, actions).
- Generated code/commands are exported to JSON.

### 2) Python compiler
- The python app should be able to take in a blockly workspace json.
- It will find the code that actually runs and is used and compile that to opcode.
- Verify it'll run and not break somehow.
- Potentially even simulate the robot running.

### 3) Initial Robot Configuration via Bluetooth
- During onboarding, the app connects to the robot over **Bluetooth**.
- Bluetooth is used for initial setup tasks such as:
  - device pairing
  - first-time configuration
  - provisioning Wi-Fi credentials

### 4) Runtime Communication via Wi-Fi + MQTT
- After initial Bluetooth setup, the robot joins the configured Wi-Fi network.
- App-to-robot communication then transitions to **MQTT** over Wi-Fi.
- The **MQTT broker runs natively inside the app** to keep deployment simple and lightweight.
- Preferred architecture is **not distributed** (single app host where possible).

## Repository Structure (Planned)

```text
robot-competition-booth/
├─ src/
│  ├─ app/                    # Blazor Server application
│  │  ├─ RobotCompetition.App.sln
│  │  └─ RobotCompetition.App/
│  ├─ firmware/               # Arduino C++ project(s)
│  │  └─ robot-controller/
|  ├─ compiler/               # Python blockly to opcode compiler
|  |  └─ compiler.py
│  └─ shared/                 # Shared protocol contracts/docs
├─ docs/
│  ├─ challenges/             # Each folder contains relevant files, documentation, etc. for each individual challenge
|  |  ├─ challenge1/
|  |  ├─ challenge2/
|  |  ├─ challenge3/
|  |  ├─ challenge4/
|  |  └─ challenge5/
│  ├─ architecture/
│  ├─ communication/
│  └─ blockly/
├─ tools/
└─ README.md
```

## Parts List

To be filled with data from [excel sheet]()

- _No parts added yet._

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).
